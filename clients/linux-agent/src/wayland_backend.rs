//! Driving the Wayland helper: one child process, frames in, input out.
//!
//! # Why there is a second process at all
//!
//! Wayland has no equivalent of X11's `GetImage` or XTEST. Capture and input both go through
//! xdg-desktop-portal, and the pixels arrive over PipeWire — and `libpipewire` is a C library. This
//! agent must not link one: it is built for `x86_64-unknown-linux-musl` precisely so it carries no
//! glibc floor and runs on the oldest host in a fleet. So the PipeWire half lives in
//! `clients/linux-agent-wayland`, shipped beside this binary and started only for the duration of a
//! session. See `wire.rs` there for the protocol, which is the only thing the two share.
//!
//! # It is started from *this* process, and that is not incidental
//!
//! The portal is per-user in every respect that matters: `WAYLAND_DISPLAY`, `XDG_RUNTIME_DIR` and
//! `DBUS_SESSION_BUS_ADDRESS` all name the logged-in user's session, and the portal keys its
//! permission store by uid. A helper launched by the root service would be asking *root's*
//! compositor for *root's* grant, and there isn't one. The per-user process already has all three
//! variables because systemd gives them to a `graphical-session.target` unit, so it inherits them by
//! simply not clearing them.
//!
//! That is the same reason capture already lives here rather than in the root service, so nothing
//! about the architecture moves — this is one more thing the display half does with the display's
//! authority. The root service still holds the identity and the socket, and still sees only JPEG
//! tiles.
//!
//! # What crosses which boundary
//!
//! Raw frames are large — a 1920×1080 BGRA frame is 8 MB — and they cross exactly one boundary, the
//! helper's stdout. Encoding happens here, so what goes on to the root service over `remote_ipc` is
//! the same few-tens-of-kilobytes of JPEG tiles the X11 path sends. Moving the encode into the
//! helper was considered and rejected: `FrameEncoder` is the piece shared verbatim with the macOS
//! and Windows agents, and a second copy of it in a fourth binary is exactly the drift this codebase
//! spends its comments avoiding.

use std::io::{BufReader, Read, Write};
use std::path::PathBuf;
use std::process::{Child, ChildStdin, Command, Stdio};
use std::sync::atomic::{AtomicBool, Ordering};
use std::sync::{Arc, Condvar, Mutex};
use std::time::{Duration, Instant};

use anyhow::{anyhow, Context, Result};
use serde::Deserialize;

use crate::config::WAYLAND_BACKEND_BINARY as HELPER_BINARY;
use crate::input_injection::{evdev_keycode_for_hid, MAX_WHEEL_CLICKS, PIXELS_PER_CLICK};
use crate::logging;
use crate::remote_protocol::{MouseButton, PointerAction, ViewerInput};
use crate::screen_capture::{downscale, DisplayGeometry, Frame};

/// The helper's message kinds. Mirrored by hand from `wire.rs` in `clients/linux-agent-wayland`,
/// the same way the Rust request structs mirror the C# DTOs — the two are shipped together and
/// replaced together by `self_update`, so there is no version negotiation to get wrong.
const KIND_FORMAT: u8 = 1;
const KIND_FRAME: u8 = 2;
const KIND_ERROR: u8 = 3;

/// How long to wait for the helper to report a working stream before giving up.
///
/// Generous, because the wait is not this agent's: `Start` on the portal raises the compositor's own
/// permission dialog, and on a host where the restore token has not yet been stored somebody has to
/// read and answer it. Twenty seconds is long enough for that and short enough that a session which
/// is never going to work ends with a stated reason.
///
/// **Something has to time this out.** An unanswered portal dialog blocks the helper's `Start` call
/// indefinitely and it reports nothing at all — no frame, no error. The agent has by then already
/// told the server that consent was granted, so without this the administrator gets a session that
/// connected and then stayed black forever, with nothing anywhere saying why.
const STARTUP_TIMEOUT: Duration = Duration::from_secs(20);

/// A running helper, and the newest frame it has produced.
pub struct WaylandBackend {
    child: Child,
    stdin: ChildStdin,
    stream: Arc<StreamSlot>,
    geometry: DisplayGeometry,
    can_control_input: bool,

    /// Every key currently held down, so they can be released if the session ends mid-chord.
    held_keys: Vec<u8>,
    held_buttons: Vec<MouseButton>,

    /// Set once the helper's death has been logged, so it is said once rather than per poll.
    reported_end: bool,

    /// Where the pointer was last put, so a release at the end of a session happens *there*.
    ///
    /// Needed because the portal has no "release wherever you are" call — a button event is always
    /// preceded by positioning it, since a press at wherever the pointer happened to be is the
    /// classic remote-control misclick. Releasing at a fixed origin instead would be worse than the
    /// bug it fixes: ending a session mid-drag would fling the user's own pointer into the top-left
    /// corner and drop whatever was being dragged there.
    last_pointer: (f64, f64),
}

impl WaylandBackend {
    /// Starts the helper and waits for it to report a stream.
    ///
    /// **This blocks, for up to [`STARTUP_TIMEOUT`], and the X11 path never does** — worth knowing
    /// when reading the two side by side, because `ScreenCapture::start` fails or succeeds at once.
    /// The wait is deliberate: `can_control_input` is not knowable until the portal has answered,
    /// and the compositor may be showing its own permission dialog to somebody. It happens on the
    /// thread serving the IPC socket, so the per-user process handles no further control messages
    /// for that window — bounded, and the session it is holding up is the one being started.
    pub fn start(max_image_width: u32) -> Result<Self> {
        let helper = helper_path()?;

        let mut child = Command::new(&helper)
            .stdin(Stdio::piped())
            .stdout(Stdio::piped())
            // Inherited rather than piped, deliberately. The helper reports its portal negotiation
            // and every PipeWire state change on stderr; a pipe nobody drains fills and blocks the
            // process that is writing to it, which here would be a helper wedged mid-session. This
            // process is a systemd unit, so inheriting sends that straight to the journal beside
            // this agent's own log, which is also where somebody diagnosing a black session looks.
            .stderr(Stdio::inherit())
            .spawn()
            .with_context(|| format!("could not start {}", helper.display()))?;

        let stdin = child.stdin.take().ok_or_else(|| anyhow!("the helper has no stdin"))?;
        let stdout = child.stdout.take().ok_or_else(|| anyhow!("the helper has no stdout"))?;

        let stream = Arc::new(StreamSlot::default());
        let reader_stream = Arc::clone(&stream);
        std::thread::Builder::new()
            .name("wayland-helper".to_string())
            .spawn(move || {
                read_helper(BufReader::new(stdout), &reader_stream);
                // Whatever ended the loop — a closed pipe, a malformed message, the helper exiting —
                // the session is over. Woken so `capture` stops waiting on a frame that will never
                // come.
                reader_stream.finish();
            })
            .context("starting the helper reader thread")?;

        let format = match stream.wait_for_format(STARTUP_TIMEOUT) {
            Ok(format) => format,
            Err(error) => {
                // Killed rather than left running: it holds a portal grant and a PipeWire stream,
                // and a helper nobody is reading from is a capture the user's compositor is still
                // doing work for.
                let _ = child.kill();
                let _ = child.wait();
                return Err(error);
            }
        };

        // The point size *is* the stream size, and that is a decision worth being explicit about
        // because the portal's own wording leaves room for another reading.
        // `NotifyPointerMotionAbsolute` takes the PipeWire node id and, per the spec, coordinates
        // "in the stream's logical coordinate space" — which is taken here to mean the frames that
        // stream actually produces, i.e. `format.width` by `format.height`. Reporting those same
        // numbers as the point size makes the viewer's conversion the identity, so nothing can drift
        // between the two.
        //
        // The other reading is the compositor's logical points, which differ from the stream's
        // pixels on a fractionally-scaled output — a 2560x1440 monitor at 150% is 1706x960 logical.
        // If that reading turns out to be the right one, the symptom is specific and worth
        // recognising: the picture is perfect, the pointer tracks correctly at the top-left, and the
        // error grows linearly towards the bottom-right by exactly the scale factor. It would look
        // like a viewer geometry fault, and it is not one — it would be this line. Unscaled outputs
        // are unaffected either way, which is why it could sit unnoticed.
        let scale = (f64::from(max_image_width) / f64::from(format.width)).min(1.0);
        let geometry = DisplayGeometry {
            point_width: f64::from(format.width),
            point_height: f64::from(format.height),
            image_width: ((f64::from(format.width) * scale).round() as u32).max(1),
            image_height: ((f64::from(format.height) * scale).round() as u32).max(1),
        };

        logging::info(&format!(
            "remote control capture started on Wayland: {}x{} stream, sending {}x{}{}",
            format.width,
            format.height,
            geometry.image_width,
            geometry.image_height,
            if format.can_control_input { "" } else { ", view-only (the portal granted no input)" }
        ));

        Ok(Self {
            child,
            stdin,
            stream,
            geometry,
            can_control_input: format.can_control_input,
            held_keys: Vec::new(),
            held_buttons: Vec::new(),
            reported_end: false,
            last_pointer: (0.0, 0.0),
        })
    }

    pub fn geometry(&self) -> DisplayGeometry {
        self.geometry
    }

    pub fn can_control_input(&self) -> bool {
        self.can_control_input
    }

    /// The newest frame, or `None` if none has arrived since the last call.
    ///
    /// Non-blocking, matching the X11 path: the session loop polls, and a `None` is a frame to skip
    /// rather than an end of stream.
    pub fn capture(&mut self) -> Option<Frame> {
        // A helper that dies mid-session leaves the last frame on the administrator's screen and
        // nothing anywhere saying so — the session loop treats `None` as a frame to skip, which is
        // right for a still desktop and indistinguishable from this. Said once rather than per poll,
        // and only to the log: ending the session from here would mean a fatal-error channel the
        // X11 path has no use for, and a frozen picture the operator can disconnect from is a
        // smaller problem than that is a change.
        if self.stream.is_finished() && !self.reported_end {
            self.reported_end = true;
            logging::warn(
                "the Wayland helper has stopped, so this session's picture will not update again; \
                 the entry above says why",
            );
        }

        let (pixels, format) = self.stream.take()?;

        // De-strided here rather than in the helper. The stride is a property of each PipeWire
        // buffer, and passing it through means the helper never has to copy a frame twice — it hands
        // over what it mapped, and the one place that needs tight rows does the work.
        let frame = tighten(&pixels, &format)?;

        if frame.width == self.geometry.image_width && frame.height == self.geometry.image_height {
            return Some(frame);
        }

        Some(downscale(&frame, self.geometry.image_width, self.geometry.image_height))
    }

    /// Sends one input event to the helper. A no-op on a view-only session.
    pub fn apply(&mut self, input: &ViewerInput) {
        if !self.can_control_input {
            return;
        }

        // Tracked before sending, so a write that fails still leaves this side believing the key is
        // down — which is the safe direction: `release_all` sends a release for it, and a spurious
        // release is harmless where a missed one leaves a key stuck down on somebody's desktop.
        self.remember(input);

        for line in helper_messages(input) {
            if let Err(error) = writeln!(self.stdin, "{line}") {
                logging::warn(&format!("could not send input to the Wayland helper: {error}"));
                return;
            }
        }

        // Flushed per event. The helper is waiting on a line and a buffered keystroke is a keystroke
        // that arrives when the next one does, which feels like a stuck keyboard.
        if let Err(error) = self.stdin.flush() {
            logging::warn(&format!("could not flush input to the Wayland helper: {error}"));
        }
    }

    /// Releases everything still held. Called on every path out of a session.
    ///
    /// The same invariant the X11 and macOS injectors keep, and it matters more here: these events
    /// go through the compositor as if they came from a real keyboard, so a Meta or Alt left down
    /// when the administrator disconnects leaves the user's desktop in a modifier state they cannot
    /// see and did not cause.
    pub fn release_all(&mut self) {
        let keys = std::mem::take(&mut self.held_keys);
        for keycode in keys {
            let _ = writeln!(
                self.stdin,
                r#"{{"type":"key","evdev":{keycode},"down":false}}"#
            );
        }

        let buttons = std::mem::take(&mut self.held_buttons);
        for line in release_messages(&buttons, self.last_pointer) {
            let _ = writeln!(self.stdin, "{line}");
        }

        let _ = self.stdin.flush();
    }

    fn remember(&mut self, input: &ViewerInput) {
        if let ViewerInput::Pointer { x, y, .. } = input {
            self.last_pointer = (*x, *y);
        }

        match input {
            ViewerInput::Key { hid, down } => {
                let Some(keycode) = evdev_keycode_for_hid(*hid) else {
                    return;
                };
                if *down {
                    if !self.held_keys.contains(&keycode) {
                        self.held_keys.push(keycode);
                    }
                } else {
                    self.held_keys.retain(|held| *held != keycode);
                }
            }
            ViewerInput::Pointer { action, button, .. } => match action {
                PointerAction::Down => {
                    if !self.held_buttons.contains(button) {
                        self.held_buttons.push(*button);
                    }
                }
                PointerAction::Up => self.held_buttons.retain(|held| held != button),
                PointerAction::Move => {}
            },
            _ => {}
        }
    }
}

impl Drop for WaylandBackend {
    fn drop(&mut self) {
        // Killed rather than asked to stop. There is deliberately no shutdown protocol: the helper
        // holds no lock and writes no file this fleet depends on, and the same reasoning is why the
        // Windows service terminates its own session helper. Closing stdin would also end it, but
        // only once it noticed, and a helper still capturing after a session has ended is the one
        // outcome that must not happen.
        let _ = self.child.kill();
        let _ = self.child.wait();
    }
}

/// The layout of the frames currently arriving.
#[derive(Debug, Clone, Copy, Deserialize, PartialEq, Eq)]
struct StreamFormat {
    width: u32,
    height: u32,
    stride: u32,
    can_control_input: bool,
}

/// The newest frame the helper has sent, and the format to read it with.
///
/// Newest-wins rather than a queue, for the reason the helper's own slot is: a frame that has been
/// superseded is worthless, and buffering them turns a moment of slowness into a permanent lag.
#[derive(Default)]
struct StreamSlot {
    state: Mutex<SlotState>,
    changed: Condvar,
    finished: AtomicBool,
}

#[derive(Default)]
struct SlotState {
    format: Option<StreamFormat>,
    frame: Option<Vec<u8>>,

    /// What the helper said went wrong, if it said anything. Reported instead of a timeout, because
    /// "the portal refused the screen" is actionable and "the helper produced no frame" is not.
    error: Option<String>,
}

impl StreamSlot {
    fn wait_for_format(&self, timeout: Duration) -> Result<StreamFormat> {
        let deadline = Instant::now() + timeout;
        let mut state = self.state.lock().map_err(|_| anyhow!("the helper reader thread panicked"))?;

        loop {
            if let Some(error) = state.error.take() {
                return Err(anyhow!("{error}"));
            }
            if let Some(format) = state.format {
                return Ok(format);
            }
            if self.finished.load(Ordering::SeqCst) {
                // Deliberately names both causes. The helper's stderr is inherited, so whichever
                // it was is already in the journal above this line — but the two need completely
                // different fixes, and a message naming only the portal would send somebody
                // looking at their compositor when the real problem is that the backend will not
                // start at all on this host's libpipewire.
                return Err(anyhow!(
                    "the Wayland helper stopped without producing a stream — the journal entry \
                     just before this one says whether the portal refused or the backend could not \
                     start on this host's libpipewire"
                ));
            }

            let Some(remaining) = deadline.checked_duration_since(Instant::now()) else {
                return Err(anyhow!(
                    "the Wayland helper did not produce a stream within {} seconds, which usually \
                     means the compositor's own permission dialog is still waiting to be answered",
                    timeout.as_secs()
                ));
            };

            let (next, _) = self
                .changed
                .wait_timeout(state, remaining)
                .map_err(|_| anyhow!("the helper reader thread panicked"))?;
            state = next;
        }
    }

    fn put_format(&self, format: StreamFormat) {
        if let Ok(mut state) = self.state.lock() {
            state.format = Some(format);
            self.changed.notify_all();
        }
    }

    fn put_frame(&self, frame: Vec<u8>) {
        if let Ok(mut state) = self.state.lock() {
            state.frame = Some(frame);
            self.changed.notify_all();
        }
    }

    fn put_error(&self, message: String) {
        if let Ok(mut state) = self.state.lock() {
            state.error = Some(message);
            self.changed.notify_all();
        }
    }

    /// The newest frame and the format to read it with, if one has arrived.
    fn take(&self) -> Option<(Vec<u8>, StreamFormat)> {
        let mut state = self.state.lock().ok()?;
        let format = state.format?;
        // Taken together and under one lock: a frame paired with a format it was not produced under
        // is laid out with the wrong stride, which is a sheared picture rather than an error.
        let frame = state.frame.take()?;
        Some((frame, format))
    }

    fn finish(&self) {
        self.finished.store(true, Ordering::SeqCst);
        self.changed.notify_all();
    }

    fn is_finished(&self) -> bool {
        self.finished.load(Ordering::SeqCst)
    }
}

/// Reads framed messages from the helper until its stdout closes.
fn read_helper(mut stdout: impl Read, stream: &StreamSlot) {
    loop {
        let mut header = [0u8; 5];
        if stdout.read_exact(&mut header).is_err() {
            return;
        }

        let kind = header[0];
        let length = u32::from_be_bytes([header[1], header[2], header[3], header[4]]) as usize;

        // A sanity bound before allocating. The length is big-endian and the helper writes it that
        // way; if the two ever disagreed, a little-endian read of 8 MB is 128 GB, and allocating it
        // takes the agent down rather than failing the session.
        if length > MAX_MESSAGE_BYTES {
            logging::warn(&format!(
                "the Wayland helper sent a {length}-byte message, which is beyond anything it should \
                 produce; ending the session"
            ));
            return;
        }

        let mut payload = vec![0u8; length];
        if stdout.read_exact(&mut payload).is_err() {
            return;
        }

        match kind {
            KIND_FORMAT => match serde_json::from_slice::<StreamFormat>(&payload) {
                Ok(format) => stream.put_format(format),
                Err(error) => {
                    logging::warn(&format!("could not read the Wayland helper's stream format: {error}"));
                    return;
                }
            },
            KIND_FRAME => stream.put_frame(payload),
            KIND_ERROR => {
                let message = serde_json::from_slice::<HelperError>(&payload)
                    .map(|error| error.message)
                    .unwrap_or_else(|_| "the Wayland helper failed for an unstated reason".to_string());
                logging::warn(&format!("the Wayland helper reported: {message}"));
                stream.put_error(message);
                return;
            }
            // Ignored rather than fatal: a newer helper saying something this build does not know
            // about must not end a session that is otherwise working.
            _ => {}
        }
    }
}

/// 64 MB — comfortably past an 8K frame and nowhere near enough to matter if one arrives corrupt.
const MAX_MESSAGE_BYTES: usize = 64 * 1024 * 1024;

#[derive(Deserialize)]
struct HelperError {
    message: String,
}

/// Copies a strided buffer into the tight rows [`Frame`] means by BGRA.
///
/// `None` if the buffer is short, which the caller treats as a frame to skip. PipeWire pads rows for
/// alignment, so a 1366-wide stream commonly arrives with a 5472-byte stride where `width * 4` is
/// 5464 — reading it as tight rows shears the picture by two pixels per row, which looks like a
/// diagonal tear rather than an off-by-eight.
fn tighten(pixels: &[u8], format: &StreamFormat) -> Option<Frame> {
    let width = format.width as usize;
    let height = format.height as usize;
    let stride = format.stride as usize;
    let row = width.checked_mul(4)?;

    if width == 0 || height == 0 || stride < row || pixels.len() < stride * height {
        return None;
    }

    // The common case: no padding at all, so the buffer is already what is wanted.
    if stride == row {
        return Some(Frame::new(pixels[..row * height].to_vec(), format.width, format.height));
    }

    let mut tight = Vec::with_capacity(row * height);
    for y in 0..height {
        let start = y * stride;
        tight.extend_from_slice(&pixels[start..start + row]);
    }

    Some(Frame::new(tight, format.width, format.height))
}

/// Turns one viewer input into the helper's JSON lines. Usually one; a click is two.
fn helper_messages(input: &ViewerInput) -> Vec<String> {
    match input {
        ViewerInput::Pointer { x, y, action, button } => {
            let action = match action {
                PointerAction::Move => "move",
                PointerAction::Down => "down",
                PointerAction::Up => "up",
            };
            vec![format!(
                r#"{{"type":"pointer","x":{x},"y":{y},"action":"{action}","button":"{}"}}"#,
                button_name(*button)
            )]
        }
        ViewerInput::Scroll { delta_x, delta_y, .. } => {
            // Positive is down and right, which is the browser's own convention, X11's (delta_y > 0
            // is wheel-*down*, see `input_injection::scroll`) and wl_pointer's. All three agreeing
            // is why nothing is inverted anywhere on this platform; getting it wrong would scroll
            // backwards on Wayland hosts only.
            let steps_x = discrete_steps(*delta_x);
            let steps_y = discrete_steps(*delta_y);
            if steps_x == 0 && steps_y == 0 {
                return Vec::new();
            }
            vec![format!(r#"{{"type":"scroll","steps_x":{steps_x},"steps_y":{steps_y}}}"#)]
        }
        ViewerInput::Key { hid, down } => match evdev_keycode_for_hid(*hid) {
            // The un-offset evdev code: the portal's `NotifyKeyboardKeycode` wants the kernel's own
            // number, where XTEST wants it plus eight. See `evdev_keycode_for_hid`.
            Some(keycode) => vec![format!(r#"{{"type":"key","evdev":{keycode},"down":{down}}}"#)],
            None => Vec::new(),
        },
        // Quality is a decision for the encoder in this process; the helper has no part in it.
        _ => Vec::new(),
    }
}

/// A wheel delta in pixels as whole wheel steps.
///
/// **The delta is in pixels, not steps, and forgetting that is a scroll a hundred times too fast.**
/// A browser reports one notch of a wheel as a deltaY around 100, so `PIXELS_PER_CLICK` is the
/// divisor — the very same one the X11 path uses, shared rather than restated so the two platforms
/// cannot end up scrolling at different rates from identical input.
///
/// Rounded away from zero so the smallest movement the viewer can express still does something: a
/// trackpad flick arrives as a fraction of a notch, and truncating would make a slow scroll do
/// nothing at all, which is indistinguishable from a session that has stopped responding.
///
/// Capped for the reason [`MAX_WHEEL_CLICKS`] exists, even though the portal takes an amount rather
/// than a burst of button presses: one flick of a high-resolution trackpad would otherwise be a
/// single event scrolling a document hundreds of lines.
fn discrete_steps(delta: f64) -> i32 {
    if delta == 0.0 {
        return 0;
    }

    let steps = (delta.abs() / PIXELS_PER_CLICK).ceil() as u32;
    let steps = steps.min(MAX_WHEEL_CLICKS) as i32;

    if delta < 0.0 {
        -steps
    } else {
        steps
    }
}

/// The lines that release a set of held buttons, at the position the pointer was last put.
fn release_messages(buttons: &[MouseButton], (x, y): (f64, f64)) -> Vec<String> {
    buttons
        .iter()
        .map(|button| {
            format!(
                r#"{{"type":"pointer","x":{x},"y":{y},"action":"up","button":"{}"}}"#,
                button_name(*button)
            )
        })
        .collect()
}

fn button_name(button: MouseButton) -> &'static str {
    match button {
        MouseButton::Left => "left",
        MouseButton::Right => "right",
        MouseButton::Middle => "middle",
    }
}

/// Why Wayland capture cannot work on this host, or `None` if it can be attempted.
///
/// Only the helper's presence, which is all that can be known without negotiating a portal session
/// — and negotiating one raises the compositor's permission dialog, which is not something to do in
/// order to answer "is this host reachable". Everything else fails at [`WaylandBackend::start`] and
/// carries the portal's own words.
///
/// This is the graceful-degradation path that matters: the helper links libpipewire and so is the
/// one binary in this fleet with a glibc floor. A host where it will not run must report *that*,
/// leaving the rest of the agent working, rather than presenting a session that never paints.
pub fn unavailable_reason() -> Option<String> {
    helper_path().err().map(|error| error.to_string())
}

/// Where the helper is, or why this host cannot do Wayland capture.
///
/// Beside this binary first, which is how the archive lays them out and therefore where
/// `self_update` puts the new one. The absolute fallback covers a host installed before the helper
/// existed, whose agent has been updated in place but whose install directory is whatever
/// `install.sh` chose at the time.
fn helper_path() -> Result<PathBuf> {
    let beside = std::env::current_exe()
        .ok()
        .and_then(|exe| exe.parent().map(|dir| dir.join(HELPER_BINARY)));

    if let Some(path) = beside {
        if path.is_file() {
            return Ok(path);
        }
    }

    let installed = PathBuf::from("/usr/local/bin").join(HELPER_BINARY);
    if installed.is_file() {
        return Ok(installed);
    }

    Err(anyhow!(
        "this host is running a Wayland session, which needs {HELPER_BINARY} alongside the agent, \
         and it is not installed — reinstall the agent from a package that includes it"
    ))
}

#[cfg(test)]
mod tests {
    use super::*;

    fn format(width: u32, height: u32, stride: u32) -> StreamFormat {
        StreamFormat { width, height, stride, can_control_input: true }
    }

    #[test]
    fn a_padded_stride_is_copied_row_by_row_rather_than_read_as_tight_rows() {
        // Two 2-pixel rows with four bytes of padding each. Read as tight rows the second row would
        // start inside the first row's padding, which shears the picture diagonally.
        let pixels = vec![
            1, 1, 1, 1, 2, 2, 2, 2, 0xFF, 0xFF, 0xFF, 0xFF, // row 0 + padding
            3, 3, 3, 3, 4, 4, 4, 4, 0xFF, 0xFF, 0xFF, 0xFF, // row 1 + padding
        ];

        let frame = tighten(&pixels, &format(2, 2, 12)).expect("a frame");

        assert_eq!(frame.bgra, vec![1, 1, 1, 1, 2, 2, 2, 2, 3, 3, 3, 3, 4, 4, 4, 4]);
        assert_eq!((frame.width, frame.height), (2, 2));
    }

    #[test]
    fn an_unpadded_frame_is_taken_as_it_is() {
        let pixels = vec![9u8; 2 * 2 * 4];

        let frame = tighten(&pixels, &format(2, 2, 8)).expect("a frame");

        assert_eq!(frame.bgra.len(), 16);
    }

    #[test]
    fn a_short_buffer_is_skipped_rather_than_panicking() {
        // A truncated frame would otherwise be a slice out of bounds on the session thread, which
        // takes down the per-user process rather than dropping one frame.
        assert!(tighten(&[0u8; 8], &format(2, 2, 8)).is_none());
    }

    #[test]
    fn a_stride_narrower_than_a_row_is_refused() {
        // Impossible from PipeWire, but the arithmetic below it assumes otherwise.
        assert!(tighten(&[0u8; 64], &format(4, 2, 8)).is_none());
    }

    #[test]
    fn a_zero_sized_stream_is_refused() {
        assert!(tighten(&[], &format(0, 0, 0)).is_none());
    }

    #[test]
    fn a_key_is_sent_as_the_kernel_code_not_the_x11_one() {
        // KEY_A is 30; XTEST's form is 38. Sending 38 to the portal types the key eight positions
        // along, which reads as a broken keymap on the host rather than a bug here.
        let messages = helper_messages(&ViewerInput::Key { hid: 0x0007_0004, down: true });

        assert_eq!(messages, vec![r#"{"type":"key","evdev":30,"down":true}"#.to_string()]);
    }

    #[test]
    fn an_unmappable_key_sends_nothing_at_all() {
        // Consumer-control usages have kernel codes but no position a keymap binds, so there is
        // nothing to send. Sending 0 would press whatever KEY_RESERVED is wired to.
        assert!(helper_messages(&ViewerInput::Key { hid: 0x000C_00E9, down: true }).is_empty());
    }

    #[test]
    fn a_click_names_its_button_and_its_position() {
        let messages = helper_messages(&ViewerInput::Pointer {
            x: 12.5,
            y: 34.0,
            action: PointerAction::Down,
            button: MouseButton::Right,
        });

        assert_eq!(
            messages,
            vec![r#"{"type":"pointer","x":12.5,"y":34,"action":"down","button":"right"}"#.to_string()]
        );
    }

    #[test]
    fn a_release_names_the_position_the_pointer_was_last_put_at() {
        // The portal has no positionless button event, so an "up" always moves first. Releasing at a
        // fixed origin would end a session mid-drag by flinging the user's own pointer into the
        // top-left corner and dropping there — worse than leaving the button down.
        let released = release_messages(&[MouseButton::Left], (640.0, 480.0));

        assert_eq!(
            released,
            vec![r#"{"type":"pointer","x":640,"y":480,"action":"up","button":"left"}"#.to_string()]
        );
    }

    #[test]
    fn a_wheel_notch_is_one_step_rather_than_a_hundred() {
        // The bug this exists for: a browser reports one notch as a deltaY around 100, so treating
        // the delta as a step count scrolls a hundred times too far on Wayland and correctly on
        // X11 — which reads as the compositor being wrong rather than this being wrong.
        assert_eq!(discrete_steps(PIXELS_PER_CLICK), 1);
        assert_eq!(discrete_steps(-PIXELS_PER_CLICK), -1);
        assert_eq!(discrete_steps(3.0 * PIXELS_PER_CLICK), 3);
    }

    #[test]
    fn the_smallest_scroll_the_viewer_can_send_still_moves_something() {
        // A trackpad flick arrives as a fraction of a notch. Truncating would make a slow scroll do
        // nothing, which is indistinguishable from a session that has stopped responding.
        assert_eq!(discrete_steps(1.0), 1);
        assert_eq!(discrete_steps(-1.0), -1);
        assert_eq!(discrete_steps(0.0), 0);
    }

    #[test]
    fn one_message_cannot_scroll_a_document_to_its_end() {
        // The same cap the X11 path applies, for the same reason — a high-resolution trackpad can
        // report an enormous delta in a single event.
        let capped = MAX_WHEEL_CLICKS as i32;

        assert_eq!(discrete_steps(10_000.0 * PIXELS_PER_CLICK), capped);
        assert_eq!(discrete_steps(-10_000.0 * PIXELS_PER_CLICK), -capped);
    }

    #[test]
    fn a_scroll_that_rounds_to_nothing_sends_nothing() {
        let messages = helper_messages(&ViewerInput::Scroll { x: 0.0, y: 0.0, delta_x: 0.0, delta_y: 0.0 });

        assert!(messages.is_empty());
    }

    #[test]
    fn the_slot_keeps_the_newest_frame_and_pairs_it_with_the_current_format() {
        let slot = StreamSlot::default();
        slot.put_format(format(2, 1, 8));
        slot.put_frame(vec![1]);
        slot.put_frame(vec![2]);

        let (frame, taken) = slot.take().expect("a frame");
        assert_eq!(frame, vec![2]);
        assert_eq!(taken, format(2, 1, 8));

        // And nothing is delivered twice — the session loop polls, and a repeated frame would be
        // re-encoded and re-sent for no change.
        assert!(slot.take().is_none());
    }

    #[test]
    fn a_frame_before_any_format_is_not_delivered() {
        // It cannot be laid out, and guessing a stride is exactly the sheared picture above.
        let slot = StreamSlot::default();
        slot.put_frame(vec![1, 2, 3, 4]);

        assert!(slot.take().is_none());
    }

    #[test]
    fn waiting_for_a_format_reports_what_the_helper_said_rather_than_a_timeout() {
        // "the portal refused the screen" is something an administrator can act on; "no frame
        // arrived" is not, and the two are otherwise indistinguishable from out here.
        let slot = StreamSlot::default();
        slot.put_error("the portal refused the screen".to_string());

        let error = slot.wait_for_format(Duration::from_secs(5)).expect_err("should fail");

        assert!(error.to_string().contains("the portal refused the screen"), "{error}");
    }

    #[test]
    fn waiting_stops_when_the_helper_exits_without_saying_anything() {
        // A helper that dies on startup — a missing libpipewire, say — closes stdout and says
        // nothing. Without this the session would sit out the whole startup timeout first.
        let slot = StreamSlot::default();
        slot.finish();

        let error = slot.wait_for_format(Duration::from_secs(30)).expect_err("should fail");

        assert!(error.to_string().contains("stopped without producing a stream"), "{error}");
    }

    #[test]
    fn the_startup_wait_gives_up_rather_than_blocking_forever() {
        // The case this exists for: an unanswered portal dialog blocks the helper's Start call and
        // it reports nothing at all. The session has already been agreed with the server by then.
        let slot = StreamSlot::default();

        let error = slot.wait_for_format(Duration::from_millis(50)).expect_err("should fail");

        assert!(error.to_string().contains("permission dialog"), "{error}");
    }

    #[test]
    fn the_reader_understands_the_helpers_framing() {
        // Byte for byte what `wire::write_message` produces on the other side. The server relays
        // none of this, and nothing else checks the two ends against each other.
        let mut wire = Vec::new();
        let format_json = br#"{"width":4,"height":2,"stride":16,"can_control_input":false}"#;
        wire.push(KIND_FORMAT);
        wire.extend_from_slice(&(format_json.len() as u32).to_be_bytes());
        wire.extend_from_slice(format_json);
        wire.push(KIND_FRAME);
        wire.extend_from_slice(&32u32.to_be_bytes());
        wire.extend_from_slice(&[7u8; 32]);

        let slot = StreamSlot::default();
        read_helper(&wire[..], &slot);

        let (frame, format) = slot.take().expect("a frame");
        assert_eq!(format, StreamFormat { width: 4, height: 2, stride: 16, can_control_input: false });
        assert_eq!(frame.len(), 32);
    }

    #[test]
    fn the_reader_refuses_an_absurd_length_rather_than_allocating_it() {
        // A little-endian read of an 8 MB length is 128 GB. Allocating that takes the agent down,
        // where refusing it ends one session.
        let mut wire = vec![KIND_FRAME];
        wire.extend_from_slice(&(MAX_MESSAGE_BYTES as u32 + 1).to_be_bytes());

        let slot = StreamSlot::default();
        read_helper(&wire[..], &slot);

        assert!(slot.take().is_none());
    }
}
