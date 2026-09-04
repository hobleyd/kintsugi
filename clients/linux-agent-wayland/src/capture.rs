//! Receiving frames from PipeWire and handing them to the agent.
//!
//! # Why this crate exists, in one paragraph
//!
//! `libpipewire` is a C library. Linking it gives the linking binary a glibc floor and a runtime
//! dependency on `libpipewire-0.3.so.0`, and the Linux agent must have neither: it is built for
//! `x86_64-unknown-linux-musl` precisely so there is no libc floor at all, which is what lets one
//! build run on the oldest host in a fleet (see the CI note in `CLAUDE.md`). Confining PipeWire to a
//! separate binary keeps that property, and costs nothing, because the hosts that need it are by
//! definition running a Wayland compositor and therefore already have PipeWire installed.
//!
//! # Newest-wins, not a queue
//!
//! The `process` callback runs on PipeWire's loop and the agent reads at its own pace, so the two
//! need decoupling. A queue would be wrong: a frame that has been superseded is worthless, and
//! buffering them means an agent that falls behind for a second then works through a second of
//! stale pictures, which looks exactly like lag and never catches up. So one slot, overwritten —
//! the same decision as `FrameSlot` in the macOS agent's `screen_capture.rs`, for the same reason.
//!
//! # Mapped buffers, and the one format request that keeps them mapped
//!
//! `StreamFlags::MAP_BUFFERS` maps the buffers into this process, which is what makes a frame a
//! plain `&[u8]`. It does **not** apply to DMA-BUF buffers — those arrive as a file descriptor and
//! `data()` returns `None`, so the frames would silently all be dropped. What decides which arrives
//! is whether the format pod advertises `SPA_FORMAT_VIDEO_modifier`: advertise it and the
//! compositor may hand back dmabuf. This one deliberately does not, which keeps everything on
//! MemFd or MemPtr and mappable. Do not add a modifier to "support more formats" without also
//! importing the dmabuf, or capture stops working on exactly the GPU-accelerated compositors that
//! most hosts run.

use std::io::Write;
use std::os::fd::OwnedFd;
use std::sync::atomic::{AtomicBool, Ordering};
use std::sync::{Arc, Condvar, Mutex};

use anyhow::{anyhow, Context, Result};
use pipewire as pw;
use pw::spa;
use spa::param::format::{FormatProperties, MediaSubtype, MediaType};
use spa::param::format_utils;
use spa::param::video::{VideoFormat, VideoInfoRaw};
use spa::pod::{ChoiceValue, Object, Pod, Property, PropertyFlags, Value};
use spa::utils::{Choice, ChoiceEnum, ChoiceFlags, Fraction, Id, Rectangle, SpaTypes};

use crate::wire::{self, FormatMessage};

/// The most frames a second to pass on to the agent.
///
/// **There are two rate gates in this pipeline and this is not the authoritative one.** The agent
/// polls `WaylandBackend::capture` on its own schedule, set by `DEFAULT_MAX_FPS` in the Linux
/// agent's `screen_capture.rs`, and that is what decides the rate of the session — the same constant
/// that decides it on the X11 path, so there is one number to tune and it is that one.
///
/// This one is a *bandwidth ceiling*, and it is deliberately set above the agent's rate rather than
/// equal to it. Every frame accepted here is a full-screen copy into the slot and another down a
/// pipe, so an uncapped 60 fps stream would be half a gigabyte a second of copying for pictures the
/// agent will not ask for. But setting it *equal* to the agent's 8 is worse than either: two
/// free-running 8 Hz gates in series beat against each other, so a frame landing just after a poll
/// waits a whole extra interval and the session runs visibly below 8 with jitter. Headroom means
/// the agent's poll always finds something fresh, and the beat disappears.
const MAX_FRAMES_PER_SECOND: u32 = 15;

/// The rate the format request nominates as preferred, and the ceiling it will accept.
///
/// A hint and a very high bound, for the reason above. 1000 fps is not a rate anything publishes;
/// it is a number chosen to be past anything that might.
const PREFERRED_FRAMES_PER_SECOND: u32 = 30;
const MAX_NEGOTIABLE_FRAMES_PER_SECOND: u32 = 1000;

/// The shortest gap between two frames handed to the agent.
const MIN_FRAME_INTERVAL: std::time::Duration =
    std::time::Duration::from_nanos(1_000_000_000 / MAX_FRAMES_PER_SECOND as u64);

/// The pixel size the format request nominates as its preferred one.
///
/// Only a hint inside a range that accepts anything from 1×1 up: the compositor sets the real size
/// from the monitor it is sharing, and a fixed size would fail negotiation outright.
const PREFERRED_SIZE: (u32, u32) = (1920, 1080);
const MAX_SIZE: (u32, u32) = (8192, 8192);

/// One frame, and the layout needed to read it.
struct Frame {
    bytes: Vec<u8>,
    format: FormatMessage,
}

/// The handoff between PipeWire's loop and the thread writing to the agent.
struct FrameSlot {
    frame: Mutex<Option<Frame>>,
    ready: Condvar,
    finished: AtomicBool,
}

impl FrameSlot {
    fn new() -> Self {
        Self { frame: Mutex::new(None), ready: Condvar::new(), finished: AtomicBool::new(false) }
    }

    /// Replaces whatever was waiting. Never blocks the PipeWire loop.
    fn put(&self, frame: Frame) {
        // A poisoned lock would mean the writer thread panicked, which ends the process anyway.
        if let Ok(mut slot) = self.frame.lock() {
            *slot = Some(frame);
            self.ready.notify_one();
        }
    }

    /// Waits for a frame. `None` once the stream is finished.
    fn take(&self) -> Option<Frame> {
        let mut slot = self.frame.lock().ok()?;

        loop {
            if let Some(frame) = slot.take() {
                return Some(frame);
            }
            if self.finished.load(Ordering::SeqCst) {
                return None;
            }
            slot = self.ready.wait(slot).ok()?;
        }
    }

    fn finish(&self) {
        self.finished.store(true, Ordering::SeqCst);
        self.ready.notify_all();
    }
}

/// Runs the capture until the stream ends or stdout closes.
///
/// Blocks for the whole session: PipeWire's main loop must run on the thread that owns it, and this
/// is the only thing this process does once the portal has been negotiated.
pub fn run(fd: OwnedFd, node_id: u32, can_control_input: bool) -> Result<()> {
    pw::init();

    let mainloop = pw::main_loop::MainLoopRc::new(None).context("creating the PipeWire main loop")?;
    let context = pw::context::ContextRc::new(&mainloop, None).context("creating a PipeWire context")?;

    // connect_fd rather than connect: the descriptor came from the portal, which is the only way to
    // reach a compositor's stream. Connecting to the session bus directly would find PipeWire but
    // not the permission the portal just granted.
    let core = context
        .connect_fd_rc(fd, None)
        .context("connecting to the PipeWire remote the portal opened")?;

    stream_node(&mainloop, core, node_id, can_control_input)
}

/// Streams one PipeWire node to stdout until it ends.
///
/// Split from [`run`] so it can be pointed at a node this process reached some other way. That is
/// what `examples/capture-node.rs` does, and it is the only way to test this half honestly: the
/// format pod, the buffer mapping and the stride handling are all negotiated with whatever is
/// producing, and a compositor is not required to prove they work — any PipeWire video source will
/// exercise exactly the same code.
pub fn stream_node(
    mainloop: &pw::main_loop::MainLoopRc,
    core: pw::core::CoreRc,
    node_id: u32,
    can_control_input: bool,
) -> Result<()> {
    let slot = Arc::new(FrameSlot::new());

    let stream = pw::stream::StreamRc::new(
        core,
        "kintsugi-remote-control",
        pw::properties::properties! {
            *pw::keys::MEDIA_TYPE => "Video",
            *pw::keys::MEDIA_CATEGORY => "Capture",
            *pw::keys::MEDIA_ROLE => "Screen",
            // Deliberately **not** `PW_KEY_TARGET_OBJECT`, which is the tempting modern
            // replacement for the `target_id` argument to `connect` below. That property matches an
            // object *name* or an `object.serial`, and what the portal hands out is a global node
            // id — setting it to one produces `Error("no target node available")` and a session
            // with no picture. The deprecated argument is the only thing that takes a node id, and
            // it is what every screen-cast consumer uses for exactly this reason.
        },
    )
    .context("creating the PipeWire stream")?;

    // The format the *producer* settled on, filled in by param_changed and read by process. Held as
    // stream user data rather than a captured variable because the two callbacks are separate
    // closures over the same state.
    let listener_slot = Arc::clone(&slot);
    let quit_loop = mainloop.clone();

    let _listener = stream
        .add_local_listener_with_user_data(StreamState::default())
        .state_changed(move |_, _, old, new| {
            eprintln!("remote control: PipeWire stream {old:?} -> {new:?}");
            if let pw::stream::StreamState::Error(message) = new {
                eprintln!("remote control: PipeWire stream failed: {message}");
                quit_loop.quit();
            }
        })
        .param_changed(|_, state, id, param| {
            // A null param clears the format — the stream is being torn down, and the next frame
            // (if any) will arrive with a fresh one.
            let Some(param) = param else {
                state.format = None;
                return;
            };
            if id != spa::param::ParamType::Format.as_raw() {
                return;
            }

            let Ok((media_type, media_subtype)) = format_utils::parse_format(param) else {
                return;
            };
            if media_type != MediaType::Video || media_subtype != MediaSubtype::Raw {
                return;
            }

            let mut info = VideoInfoRaw::new();
            if info.parse(param).is_err() {
                eprintln!("remote control: could not read the format PipeWire negotiated");
                return;
            }

            let size = info.size();
            eprintln!(
                "remote control: capturing {}x{} at up to {}/{} fps, format {:?}",
                size.width,
                size.height,
                info.framerate().num,
                info.framerate().denom.max(1),
                info.format()
            );

            state.format = Some(NegotiatedFormat {
                width: size.width,
                height: size.height,
                // Whether red and blue need exchanging on the way out. The wire promises BGRA and
                // this is the only place that knows what actually arrived — leaving it to the agent
                // would mean putting the pixel format on the wire and giving every consumer the same
                // decision to get wrong. The symptom of skipping it is not subtle but is easy to
                // misattribute: a perfectly sharp picture with the reds and blues exchanged, which
                // reads as a display-profile problem rather than a byte-order one.
                swap_red_and_blue: matches!(
                    info.format(),
                    VideoFormat::RGBx | VideoFormat::RGBA
                ),
                // The real stride comes off each buffer's own chunk — it is a property of the
                // buffer, not of the format, and a compositor may pad differently per buffer.
                // Recorded here only as the fallback for a chunk reporting none.
                fallback_stride: size.width.saturating_mul(4),
            });
        })
        .process(move |stream, state| {
            let Some(format) = state.format else {
                return;
            };
            let Some(mut buffer) = stream.dequeue_buffer() else {
                // No buffer available. Normal under load and not worth logging per occurrence.
                return;
            };

            // Dequeued before the rate check, and that order matters: returning early without
            // dequeuing would leave the buffer queued and the producer would stall waiting for it
            // back. Recycling it is the whole point of dropping a frame.
            if let Some(previous) = state.last_frame_at {
                if previous.elapsed() < MIN_FRAME_INTERVAL {
                    return;
                }
            }
            state.last_frame_at = Some(std::time::Instant::now());

            let datas = buffer.datas_mut();
            let Some(data) = datas.first_mut() else {
                return;
            };

            let chunk_size = data.chunk().size() as usize;
            let stride = match data.chunk().stride() {
                positive if positive > 0 => positive as u32,
                // A zero or negative stride happens on the first buffer of some backends. The
                // format's own width is the only other thing to go on.
                _ => format.fallback_stride,
            };

            let Some(bytes) = data.data() else {
                // The DMA-BUF case: mapped buffers do not cover it, so there is nothing to read.
                // Reported once rather than per frame, because it would otherwise be every frame.
                if !state.warned_about_unmapped {
                    state.warned_about_unmapped = true;
                    eprintln!(
                        "remote control: PipeWire delivered an unmapped buffer, so no frame can be \
                         read — this means the negotiated format ended up on DMA-BUF"
                    );
                }
                return;
            };

            // Trust the chunk's own size where it is sane, and fall back to the geometry otherwise.
            // A chunk claiming more than the mapping holds would panic on the slice.
            let wanted = (stride as usize).saturating_mul(format.height as usize);
            let available = bytes.len().min(if chunk_size == 0 { wanted } else { chunk_size.min(wanted) });

            if available == 0 {
                return;
            }

            let mut pixels = bytes[..available].to_vec();
            if format.swap_red_and_blue {
                swap_red_and_blue(&mut pixels);
            }

            listener_slot.put(Frame {
                bytes: pixels,
                format: FormatMessage {
                    width: format.width,
                    height: format.height,
                    stride,
                    can_control_input,
                },
            });
        })
        .register()
        .context("registering the PipeWire stream listener")?;

    let format_pod = enum_format_pod()?;
    let mut params = [Pod::from_bytes(&format_pod)
        .ok_or_else(|| anyhow!("the format request did not serialise into a valid pod"))?];

    stream
        .connect(
            spa::utils::Direction::Input,
            Some(node_id),
            // AUTOCONNECT is required, and it does not mean what its name suggests here. Passed
            // *with* the node id above it means "make the link to that node"; without it PipeWire
            // creates no link at all and the stream sits in `Paused` forever, producing no frames
            // and no error. It is the session manager that acts on it, which is worth knowing when
            // reading a log: on a host where wireplumber is not running, capture cannot work.
            //
            // No RT_PROCESS: the callback copies a whole frame, which has no business on a
            // realtime thread.
            pw::stream::StreamFlags::AUTOCONNECT | pw::stream::StreamFlags::MAP_BUFFERS,
            &mut params,
        )
        .context("connecting the PipeWire stream to the portal's node")?;

    // The writer runs on its own thread so a blocked stdout — an agent busy encoding — cannot stall
    // the PipeWire loop, which would make the compositor's own compositing hitch.
    //
    // Ending the loop from that thread needs PipeWire's own channel rather than a cloned handle:
    // `MainLoopRc` is `Rc`-based and deliberately not `Send`, because a loop may only be driven from
    // the thread that owns it. The channel's receiver is attached to the loop and its callback
    // therefore runs *on* the loop thread, which is the only place `quit` is legal.
    let (stopped_tx, stopped_rx) = pw::channel::channel::<()>();
    let quit_on_stop = mainloop.clone();
    let _stop_receiver = stopped_rx.attach(mainloop.loop_(), move |()| quit_on_stop.quit());

    let writer_slot = Arc::clone(&slot);
    let writer = std::thread::Builder::new()
        .name("frame-writer".to_string())
        .spawn(move || {
            if let Err(error) = write_frames(&writer_slot) {
                // The ordinary end of a session: the agent closed the pipe. Not an error worth
                // shouting about, but worth saying which it was.
                eprintln!("remote control: stopped writing frames ({error})");
            }
            // Ignored: a closed channel means the loop has already finished, which is the other way
            // this session ends.
            let _ = stopped_tx.send(());
        })
        .context("starting the frame writer thread")?;

    mainloop.run();

    slot.finish();
    let _ = writer.join();

    Ok(())
}

#[derive(Default)]
struct StreamState {
    format: Option<NegotiatedFormat>,
    warned_about_unmapped: bool,

    /// When the last frame was passed on, for the rate limit. `None` until the first one.
    last_frame_at: Option<std::time::Instant>,
}

#[derive(Clone, Copy)]
struct NegotiatedFormat {
    width: u32,
    height: u32,
    fallback_stride: u32,
    swap_red_and_blue: bool,
}

/// Drains the slot to stdout until the stream finishes or the agent goes away.
fn write_frames(slot: &FrameSlot) -> Result<()> {
    let stdout = std::io::stdout();
    let mut out = stdout.lock();
    // `from_fn` rather than passing the slot: it makes the loop below independent of where frames
    // come from, which is what lets it be driven from a fixed list in a test.
    drain(std::iter::from_fn(|| slot.take()), &mut out)
}

/// The loop itself, over any source of frames and any writer, so the framing and the format-repeat
/// rule can be tested without a compositor.
fn drain(frames: impl Iterator<Item = Frame>, out: &mut impl Write) -> Result<()> {
    let mut last_format: Option<FormatMessage> = None;

    for frame in frames {
        // Re-sent whenever anything about the layout changes, which really happens: a monitor mode
        // change or a hotplug renegotiates the stream mid-session, and a frame read with the old
        // stride is a diagonally sheared picture rather than an error.
        if last_format.as_ref() != Some(&frame.format) {
            wire::write_format(out, &frame.format).context("writing the frame format")?;
            last_format = Some(frame.format.clone());
        }

        wire::write_message(out, wire::KIND_FRAME, &frame.bytes).context("writing a frame")?;
    }

    Ok(())
}

/// Builds the `SPA_PARAM_EnumFormat` pod describing what this process will accept.
///
/// Choices rather than fixed values throughout, because the producer is the compositor and it
/// decides: a fixed size fails negotiation on every monitor that is not that size, and a fixed
/// pixel format fails on every compositor that does not happen to prefer it.
fn enum_format_pod() -> Result<Vec<u8>> {
    let object = Object {
        type_: SpaTypes::ObjectParamFormat.as_raw(),
        id: spa::param::ParamType::EnumFormat.as_raw(),
        properties: vec![
            fixed(FormatProperties::MediaType, Value::Id(Id(MediaType::Video.as_raw()))),
            fixed(FormatProperties::MediaSubtype, Value::Id(Id(MediaSubtype::Raw.as_raw()))),
            // Four 32-bit layouts, all of which the agent can read as BGRA-ish. Deliberately no
            // 24-bit or planar format: they would negotiate happily and then need a conversion pass
            // per frame, and every compositor offers one of these.
            choice(
                FormatProperties::VideoFormat,
                ChoiceValue::Id(Choice(
                    ChoiceFlags::empty(),
                    ChoiceEnum::Enum {
                        default: Id(VideoFormat::BGRx.as_raw()),
                        alternatives: vec![
                            Id(VideoFormat::BGRx.as_raw()),
                            Id(VideoFormat::BGRA.as_raw()),
                            Id(VideoFormat::RGBx.as_raw()),
                            Id(VideoFormat::RGBA.as_raw()),
                        ],
                    },
                )),
            ),
            choice(
                FormatProperties::VideoSize,
                ChoiceValue::Rectangle(Choice(
                    ChoiceFlags::empty(),
                    ChoiceEnum::Range {
                        default: Rectangle { width: PREFERRED_SIZE.0, height: PREFERRED_SIZE.1 },
                        min: Rectangle { width: 1, height: 1 },
                        max: Rectangle { width: MAX_SIZE.0, height: MAX_SIZE.1 },
                    },
                )),
            ),
            choice(
                FormatProperties::VideoFramerate,
                ChoiceValue::Fraction(Choice(
                    ChoiceFlags::empty(),
                    ChoiceEnum::Range {
                        default: Fraction { num: PREFERRED_FRAMES_PER_SECOND, denom: 1 },
                        // A zero minimum means "variable", which is what a screen actually is: a
                        // still desktop should produce no frames at all rather than identical ones
                        // forever. The maximum is deliberately absurd — see MAX_FRAMES_PER_SECOND.
                        min: Fraction { num: 0, denom: 1 },
                        max: Fraction { num: MAX_NEGOTIABLE_FRAMES_PER_SECOND, denom: 1 },
                    },
                )),
            ),
        ],
    };

    let bytes = spa::pod::serialize::PodSerializer::serialize(
        std::io::Cursor::new(Vec::new()),
        &Value::Object(object),
    )
    .map_err(|error| anyhow!("could not serialise the format request: {error:?}"))?
    .0
    .into_inner();

    Ok(bytes)
}

/// Exchanges the first and third byte of every four, turning RGBx/RGBA into BGRx/BGRA in place.
///
/// The fourth byte is left alone: it is either alpha, which does not move, or padding, which nothing
/// reads. Chunked rather than indexed so the bounds check happens once per pixel instead of twice,
/// and so a buffer whose length is not a multiple of four — a truncated final row — is simply left
/// with its tail untouched rather than panicking.
fn swap_red_and_blue(pixels: &mut [u8]) {
    for pixel in pixels.chunks_exact_mut(4) {
        pixel.swap(0, 2);
    }
}

fn fixed(key: FormatProperties, value: Value) -> Property {
    Property { key: key.as_raw(), flags: PropertyFlags::empty(), value }
}

fn choice(key: FormatProperties, value: ChoiceValue) -> Property {
    Property { key: key.as_raw(), flags: PropertyFlags::empty(), value: Value::Choice(value) }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn the_format_request_serialises_into_a_pod_pipewire_will_accept() {
        // Not a deep assertion, but the one that catches the mistake worth catching: a malformed pod
        // makes `Pod::from_bytes` return None and the stream never connects, with PipeWire saying
        // only "invalid argument".
        let bytes = enum_format_pod().expect("the format request should serialise");

        assert!(Pod::from_bytes(&bytes).is_some(), "{} bytes did not parse as a pod", bytes.len());
    }

    #[test]
    fn the_slot_keeps_the_newest_frame_and_discards_the_superseded_one() {
        // The whole point of a slot over a queue: an agent that falls behind must see the current
        // screen when it catches up, not work through a backlog of stale ones.
        let slot = FrameSlot::new();
        let format = FormatMessage { width: 2, height: 1, stride: 8, can_control_input: true };

        slot.put(Frame { bytes: vec![1], format: format.clone() });
        slot.put(Frame { bytes: vec![2], format: format.clone() });
        slot.put(Frame { bytes: vec![3], format });

        assert_eq!(slot.take().expect("a frame").bytes, vec![3]);
    }

    #[test]
    fn taking_from_a_finished_slot_returns_nothing_rather_than_blocking() {
        // The writer thread's exit condition. Without this it would park forever on the condvar and
        // the process would never exit after the stream ended.
        let slot = FrameSlot::new();
        slot.finish();

        assert!(slot.take().is_none());
    }

    #[test]
    fn a_frame_already_waiting_is_still_delivered_after_finishing() {
        // Ordering matters: the last frame of a session must not be dropped just because the stream
        // ended in the same instant it arrived.
        let slot = FrameSlot::new();
        slot.put(Frame {
            bytes: vec![9],
            format: FormatMessage { width: 1, height: 1, stride: 4, can_control_input: false },
        });
        slot.finish();

        assert_eq!(slot.take().expect("the pending frame").bytes, vec![9]);
        assert!(slot.take().is_none());
    }

    #[test]
    fn swapping_red_and_blue_leaves_green_and_the_fourth_byte_alone() {
        // Opaque red as RGBA becomes opaque red as BGRA: the bytes move, the colour does not.
        let mut pixels = vec![0xFF, 0x00, 0x00, 0xFF, 0x11, 0x22, 0x33, 0x44];
        swap_red_and_blue(&mut pixels);

        assert_eq!(pixels, vec![0x00, 0x00, 0xFF, 0xFF, 0x33, 0x22, 0x11, 0x44]);
    }

    #[test]
    fn a_trailing_partial_pixel_is_left_alone_rather_than_panicking() {
        // A stride the buffer does not quite fill would otherwise be an index out of bounds inside
        // the capture callback, which takes the whole session down.
        let mut pixels = vec![1, 2, 3, 4, 9, 9];
        swap_red_and_blue(&mut pixels);

        assert_eq!(pixels, vec![3, 2, 1, 4, 9, 9]);
    }

    #[test]
    fn the_writer_sends_the_format_once_and_again_only_when_it_changes() {
        // A resolution change mid-session renegotiates the stream, and a frame the agent lays out
        // with the previous stride is a diagonally sheared picture — so the repeat is load-bearing,
        // while repeating it per frame would just be noise.
        let first = FormatMessage { width: 2, height: 1, stride: 8, can_control_input: true };
        let resized = FormatMessage { width: 4, height: 1, stride: 16, can_control_input: true };

        // Driven from a fixed list rather than through the slot, which is newest-wins and would
        // collapse these three into one.
        let mut written = Vec::new();
        drain(
            [
                Frame { bytes: vec![1], format: first.clone() },
                Frame { bytes: vec![2], format: first },
                Frame { bytes: vec![3], format: resized },
            ]
            .into_iter(),
            &mut written,
        )
        .expect("draining should succeed");

        let kinds: Vec<u8> = message_kinds(&written);
        assert_eq!(
            kinds,
            vec![
                wire::KIND_FORMAT,
                wire::KIND_FRAME,
                // No second format: nothing about the layout changed.
                wire::KIND_FRAME,
                // The resize does re-announce it.
                wire::KIND_FORMAT,
                wire::KIND_FRAME,
            ]
        );
    }

    /// Walks the framing the way the agent does, which also asserts the lengths line up: a wrong
    /// length would leave this reading a kind byte out of the middle of a payload.
    fn message_kinds(bytes: &[u8]) -> Vec<u8> {
        let mut kinds = Vec::new();
        let mut at = 0;

        while at < bytes.len() {
            let kind = bytes[at];
            let length = u32::from_be_bytes(
                bytes[at + 1..at + 5].try_into().expect("a length should be four bytes"),
            ) as usize;
            kinds.push(kind);
            at += 5 + length;
        }

        assert_eq!(at, bytes.len(), "the framing should end exactly at the end of the buffer");
        kinds
    }
}
