//! Turning this Mac's screen into a stream of JPEG tiles.
//!
//! ScreenCaptureKit, not `CGDisplayCreateImage`. The latter is three lines and would have done, but
//! Apple deprecated it in macOS 15 pointing at exactly this API — and a remote-control feature that
//! stops working on a future macOS is a worse outcome than one that took longer to write.
//! ScreenCaptureKit needs macOS 12.3, which every host this agent supports already is.
//!
//! **This needs the Screen Recording permission** (`kTCCServiceScreenCapture`). Unlike
//! Accessibility, macOS *can* grant this from its own prompt — but the prompt appears on the
//! desktop of whoever is logged in, at whatever moment capture is first attempted, which is no way
//! to start a support session. [`has_screen_recording_permission`] is checked before consent is
//! even offered so the dialog can explain the problem instead.
//!
//! Two halves, deliberately split: [`ScreenCapture`] deals with AppKit and unsafe FFI and cannot be
//! tested without a window server, while [`FrameEncoder`] is pure pixels in, wire messages out, and
//! is where the tiling and change detection live — so the part with the interesting logic is the
//! part that has tests.

use std::sync::{Arc, Condvar, Mutex};
use std::time::Duration;

use anyhow::{anyhow, Context, Result};
use block2::RcBlock;
use dispatch2::DispatchQueue;
use objc2::rc::Retained;
use objc2::runtime::ProtocolObject;
use objc2::{define_class, msg_send, AnyThread, DefinedClass, MainThreadMarker};
use objc2_core_media::{CMSampleBuffer, CMTime};
use objc2_core_video::{
    CVPixelBufferGetBaseAddress, CVPixelBufferGetBytesPerRow, CVPixelBufferGetHeight, CVPixelBufferGetWidth,
    CVPixelBufferLockBaseAddress, CVPixelBufferLockFlags, CVPixelBufferUnlockBaseAddress,
};
use objc2_foundation::{NSArray, NSError, NSObject, NSObjectProtocol};
use objc2_screen_capture_kit::{
    SCContentFilter, SCDisplay, SCShareableContent, SCStream, SCStreamConfiguration, SCStreamOutput, SCStreamOutputType,
};

use crate::logging;
use crate::remote_protocol::{encode_tile, DisplayInfo};

/// `kCVPixelFormatType_32BGRA`, as a FourCC. BGRA rather than one of the YUV formats because the
/// only consumer is a JPEG encoder that wants interleaved 8-bit colour, and converting from
/// biplanar YUV here would be work done twice.
const PIXEL_FORMAT_BGRA: u32 = u32::from_be_bytes(*b"BGRA");

/// How wide a captured image may be, in pixels, before it is scaled down. Chosen so an ordinary
/// Retina laptop display captures at its *point* size — a 1512-point-wide screen becomes a
/// 1512-pixel-wide image, which is entirely readable text at a quarter of the data of native
/// Retina.
pub const DEFAULT_MAX_IMAGE_WIDTH: u32 = 1600;

/// Frames per second requested from ScreenCaptureKit. ScreenCaptureKit does not send a frame when
/// nothing changed, so this is a ceiling rather than a rate: an idle screen costs nothing.
pub const DEFAULT_MAX_FPS: u32 = 15;

/// JPEG quality. 60 is where text stays crisp and a full-screen frame is a couple of hundred
/// kilobytes rather than a megabyte.
pub const DEFAULT_JPEG_QUALITY: u8 = 60;

/// Side of a change-detection tile, in pixels. A compromise: smaller tiles find changes more
/// precisely but each JPEG carries its own headers and quantisation tables, so a full-screen change
/// at 64px would spend more on overhead than on picture.
const TILE_SIZE: u32 = 256;

/// Above this fraction of changed tiles, one whole-image tile is sent instead of many small ones —
/// which is what happens on a scroll, a window opening, or a space switch.
const FULL_FRAME_TILE_FRACTION: f64 = 0.6;

#[link(name = "CoreGraphics", kind = "framework")]
extern "C" {
    fn CGPreflightScreenCaptureAccess() -> bool;
    fn CGRequestScreenCaptureAccess() -> bool;
}

/// Whether this process may capture the screen.
///
/// Checked before a session is offered. A process without this permission does not fail loudly —
/// ScreenCaptureKit hands back a stream that produces either nothing or a desktop picture with
/// every window missing, which looks like a broken agent rather than a missing permission.
pub fn has_screen_recording_permission() -> bool {
    // SAFETY: takes no arguments, returns a Boolean, no preconditions.
    unsafe { CGPreflightScreenCaptureAccess() }
}

/// Asks macOS for the Screen Recording permission, which shows its prompt at most once per process.
///
/// Only worth calling when a session has actually been requested: with an MDM PPPC profile in place
/// this is already granted and returns true without showing anything, and without one it puts a
/// dialog in front of whoever is logged in — so calling it at startup would prompt every Mac in the
/// fleet for a feature most of them will never be asked to use.
pub fn request_screen_recording_permission() -> bool {
    // SAFETY: as above.
    unsafe { CGRequestScreenCaptureAccess() }
}

/// One captured frame, tightly packed BGRA.
///
/// Packed on the way out of the pixel buffer rather than kept at its original stride: IOSurface rows
/// are padded to an alignment that has nothing to do with the image width, and carrying that
/// padding through change detection and JPEG encoding means every consumer has to remember it.
/// Once is cheaper than everywhere.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct Frame {
    pub bgra: Vec<u8>,
    pub width: u32,
    pub height: u32,
}

impl Frame {
    pub fn new(bgra: Vec<u8>, width: u32, height: u32) -> Self {
        Self { bgra, width, height }
    }
}

/// Where the capture callback leaves its most recent frame.
///
/// **Newest wins, and stale frames are dropped on the floor.** A queue would be wrong here in a way
/// that is easy to get wrong: if encoding or the network falls behind, a queue means the remote
/// user watches an ever-growing delay of things that already happened, whereas dropping means they
/// see the screen as it is now at a lower frame rate. For someone driving a mouse, the second is
/// the only usable answer.
#[derive(Default)]
struct FrameSlot {
    latest: Mutex<SlotState>,
    ready: Condvar,
}

#[derive(Default)]
struct SlotState {
    frame: Option<Frame>,
    stopped: bool,
}

impl FrameSlot {
    fn publish(&self, frame: Frame) {
        if let Ok(mut state) = self.latest.lock() {
            state.frame = Some(frame);
            self.ready.notify_all();
        }
    }

    fn take(&self, timeout: Duration) -> Option<Frame> {
        let mut state = self.latest.lock().ok()?;

        if state.frame.is_none() && !state.stopped {
            let (guard, _) = self.ready.wait_timeout(state, timeout).ok()?;
            state = guard;
        }

        state.frame.take()
    }

    fn stop(&self) {
        if let Ok(mut state) = self.latest.lock() {
            state.stopped = true;
            self.ready.notify_all();
        }
    }
}

struct StreamOutputIvars {
    slot: Arc<FrameSlot>,
}

define_class!(
    // SAFETY:
    // - NSObject imposes no subclassing requirements.
    // - This class does not implement Drop; objc2 drops the ivars in dealloc.
    // - No #[thread_kind] restriction, deliberately: ScreenCaptureKit delivers sample buffers on
    //   the dispatch queue handed to addStreamOutput, never on the main thread.
    #[unsafe(super(NSObject))]
    #[name = "KintsugiRemoteControlStreamOutput"]
    #[ivars = StreamOutputIvars]
    struct StreamOutput;

    unsafe impl NSObjectProtocol for StreamOutput {}

    unsafe impl SCStreamOutput for StreamOutput {
        #[unsafe(method(stream:didOutputSampleBuffer:ofType:))]
        // Named after the trait method it implements, which is named after the Objective-C
        // selector. Renaming it to snake case would no longer match `SCStreamOutput`.
        #[allow(non_snake_case)]
        unsafe fn stream_didOutputSampleBuffer_ofType(
            &self,
            _stream: &SCStream,
            sample_buffer: &CMSampleBuffer,
            output_type: SCStreamOutputType,
        ) {
            // Audio and microphone buffers are never asked for, but the protocol is one method for
            // all three types and a future macOS adding a fourth must not be read as pixels.
            if output_type != SCStreamOutputType::Screen {
                return;
            }

            // SAFETY: called by ScreenCaptureKit with a valid sample buffer that outlives this call.
            if let Some(frame) = unsafe { copy_frame(sample_buffer) } {
                self.ivars().slot.publish(frame);
            }
        }
    }
);

impl StreamOutput {
    fn new(slot: Arc<FrameSlot>) -> Retained<Self> {
        let this = Self::alloc().set_ivars(StreamOutputIvars { slot });
        unsafe { msg_send![super(this), init] }
    }
}

/// Copies one sample buffer's pixels out into a packed [`Frame`].
///
/// # Safety
///
/// `sample_buffer` must be a live screen sample buffer, as delivered by ScreenCaptureKit.
unsafe fn copy_frame(sample_buffer: &CMSampleBuffer) -> Option<Frame> {
    let image_buffer = unsafe { sample_buffer.image_buffer() }?;

    // A frame arrives whenever ScreenCaptureKit has something to say, including — on the very first
    // callback, and after a display configuration change — a buffer with no image at all. Locking
    // has to be checked rather than assumed.
    let locked = unsafe { CVPixelBufferLockBaseAddress(&image_buffer, CVPixelBufferLockFlags::ReadOnly) };
    if locked != 0 {
        return None;
    }

    let width = CVPixelBufferGetWidth(&image_buffer) as u32;
    let height = CVPixelBufferGetHeight(&image_buffer) as u32;
    let bytes_per_row = CVPixelBufferGetBytesPerRow(&image_buffer);
    let base = CVPixelBufferGetBaseAddress(&image_buffer);

    let frame = if base.is_null() || width == 0 || height == 0 {
        None
    } else {
        let row_bytes = width as usize * 4;
        let mut packed = Vec::with_capacity(row_bytes * height as usize);

        for row in 0..height as usize {
            // SAFETY: `base` points at `height` rows of at least `bytes_per_row` bytes each, and
            // `row_bytes <= bytes_per_row` because the stride is the width rounded *up* to an
            // alignment. The buffer stays locked for the whole copy.
            let source = unsafe { (base as *const u8).add(row * bytes_per_row) };
            packed.extend_from_slice(unsafe { std::slice::from_raw_parts(source, row_bytes) });
        }

        Some(Frame::new(packed, width, height))
    };

    // SAFETY: paired with the successful lock above.
    unsafe { CVPixelBufferUnlockBaseAddress(&image_buffer, CVPixelBufferLockFlags::ReadOnly) };

    frame
}

/// The geometry both ends need to agree on.
///
/// `origin` and the point size describe the display in macOS's own global coordinate space, which is
/// what input has to be posted into; the image size is what the JPEGs actually are. Keeping both and
/// never conflating them is what stops a click landing somewhere else on a Retina display or a
/// second monitor — see the note on [`DisplayInfo`].
#[derive(Debug, Clone, Copy, PartialEq)]
pub struct DisplayGeometry {
    pub origin_x: f64,
    pub origin_y: f64,
    pub point_width: f64,
    pub point_height: f64,
    pub image_width: u32,
    pub image_height: u32,
}

impl DisplayGeometry {
    pub fn to_display_info(self) -> DisplayInfo {
        DisplayInfo::new(self.point_width, self.point_height, self.image_width, self.image_height)
    }
}

/// A running ScreenCaptureKit stream over one display.
pub struct ScreenCapture {
    stream: Retained<SCStream>,
    /// Held for the stream's lifetime. ScreenCaptureKit does not retain the output object, so
    /// dropping this would leave the stream calling into freed memory.
    _output: Retained<StreamOutput>,
    slot: Arc<FrameSlot>,
    pub geometry: DisplayGeometry,
}

impl ScreenCapture {
    /// Starts capturing the main display.
    ///
    /// One display, not all of them: a viewer that can only draw one picture would have to be told
    /// which, and every host this is aimed at is somebody's laptop. Extending this means sending a
    /// display list to the viewer and letting it choose, which the protocol has room for.
    pub fn start(max_image_width: u32) -> Result<Self> {
        if !has_screen_recording_permission() {
            return Err(anyhow!(
                "this agent does not have the Screen Recording permission, so it cannot capture the screen \
                 (grant it via an MDM PPPC profile, or in System Settings > Privacy & Security > Screen Recording)"
            ));
        }

        let display = main_display().context("could not find a display to capture")?;

        // SCDisplay reports its frame in points, in the global coordinate space — which is exactly
        // the space CGEventPost wants, so this is the one conversion input needs.
        let frame = unsafe { display.frame() };
        let point_width = frame.size.width;
        let point_height = frame.size.height;

        if point_width <= 0.0 || point_height <= 0.0 {
            return Err(anyhow!("the display reported a zero-sized frame"));
        }

        // Scaled down only if it would otherwise be wider than the cap, and never scaled *up* — a
        // low-resolution display gains nothing from being sent as more pixels than it has.
        let scale = (max_image_width as f64 / point_width).min(1.0);
        let image_width = ((point_width * scale).round() as u32).max(1);
        let image_height = ((point_height * scale).round() as u32).max(1);

        let geometry = DisplayGeometry {
            origin_x: frame.origin.x,
            origin_y: frame.origin.y,
            point_width,
            point_height,
            image_width,
            image_height,
        };

        // SAFETY: every call below is a documented ScreenCaptureKit initialiser or setter, given
        // values of the types it declares.
        let (stream, output, slot) = unsafe {
            let filter = SCContentFilter::initWithDisplay_excludingWindows(
                SCContentFilter::alloc(),
                &display,
                &NSArray::new(),
            );

            let configuration = SCStreamConfiguration::new();
            configuration.setWidth(image_width as usize);
            configuration.setHeight(image_height as usize);
            configuration.setScalesToFit(true);
            configuration.setPreservesAspectRatio(true);
            configuration.setPixelFormat(PIXEL_FORMAT_BGRA);
            // The remote user is driving this mouse; not seeing the pointer would make the session
            // unusable in a way no amount of frame rate would make up for.
            configuration.setShowsCursor(true);
            // Shallow on purpose: queue depth is latency. Three is ScreenCaptureKit's own minimum
            // recommendation and there is nothing here that benefits from buffering more, since a
            // frame that arrives while another is being encoded is dropped anyway.
            configuration.setQueueDepth(3);
            configuration.setMinimumFrameInterval(CMTime {
                value: 1,
                timescale: DEFAULT_MAX_FPS as i32,
                flags: objc2_core_media::CMTimeFlags::Valid,
                epoch: 0,
            });

            let slot = Arc::new(FrameSlot::default());
            let output = StreamOutput::new(slot.clone());

            let stream = SCStream::initWithFilter_configuration_delegate(
                SCStream::alloc(),
                &filter,
                &configuration,
                // No SCStreamDelegate: its only useful callback is didStopWithError, and a stream
                // that has stopped is already observable here as frames no longer arriving, which
                // the session loop times out on. Adding one would mean a second custom class for
                // information the session already has.
                None,
            );

            // A serial queue of its own. Not the main queue: the main thread is running AppKit's
            // event loop for the menu bar (see tray_menu), and delivering a frame there would put
            // a pixel copy in front of every menu interaction.
            let queue = DispatchQueue::new("au.com.sharpblue.kintsugi.remote-control.capture", None);

            stream
                .addStreamOutput_type_sampleHandlerQueue_error(
                    ProtocolObject::from_ref(&*output),
                    SCStreamOutputType::Screen,
                    Some(&queue),
                )
                .map_err(|err| anyhow!("could not attach the capture output: {err}"))?;

            (stream, output, slot)
        };

        start_capture(&stream)?;

        logging::info(&format!(
            "remote control capture started: {}x{} points, sending {}x{} pixels",
            point_width, point_height, image_width, image_height
        ));

        Ok(Self { stream, _output: output, slot, geometry })
    }

    /// The most recent frame, or `None` if none arrived within `timeout`.
    ///
    /// `None` is not an error: ScreenCaptureKit sends nothing at all while the screen is unchanged,
    /// which is most of the time during a support call.
    pub fn next_frame(&self, timeout: Duration) -> Option<Frame> {
        self.slot.take(timeout)
    }

    pub fn stop(&self) {
        self.slot.stop();

        // SAFETY: a documented method on a live stream; the completion handler is optional and
        // nothing here needs to know when the stop finished — this object is being dropped either
        // way, and blocking a session teardown on it would only delay releasing the screen.
        unsafe { self.stream.stopCaptureWithCompletionHandler(None) };
    }
}

impl Drop for ScreenCapture {
    fn drop(&mut self) {
        self.stop();
    }
}

/// Starts the stream and waits for ScreenCaptureKit to say whether it worked.
///
/// Synchronous on purpose. `startCaptureWithCompletionHandler` reports permission and configuration
/// failures *in the completion handler* rather than by returning an error, so a fire-and-forget
/// start would report success and then simply never produce a frame — which the session loop can
/// only interpret as an idle screen.
fn start_capture(stream: &SCStream) -> Result<()> {
    let (sender, receiver) = std::sync::mpsc::channel::<Option<String>>();

    let handler = RcBlock::new(move |error: *mut NSError| {
        let message = if error.is_null() {
            None
        } else {
            // SAFETY: ScreenCaptureKit passes a valid NSError or null; checked above.
            Some(unsafe { &*error }.localizedDescription().to_string())
        };

        // A send failure means this agent stopped waiting, which only happens on the timeout path
        // below; there is nobody left to tell.
        let _ = sender.send(message);
    });

    // SAFETY: a documented method, given a block of the declared signature.
    unsafe { stream.startCaptureWithCompletionHandler(Some(&handler)) };

    match receiver.recv_timeout(Duration::from_secs(10)) {
        Ok(None) => Ok(()),
        Ok(Some(message)) => Err(anyhow!("ScreenCaptureKit refused to start capturing: {message}")),
        Err(_) => Err(anyhow!("ScreenCaptureKit did not answer the request to start capturing")),
    }
}

/// The display to capture.
///
/// `SCShareableContent` is asynchronous and there is no synchronous form of it, so this bridges to
/// a channel — the session is being set up on its own thread and has nothing else to do until it
/// knows what it is capturing.
fn main_display() -> Result<Retained<SCDisplay>> {
    let (sender, receiver) = std::sync::mpsc::channel::<Result<Retained<SCDisplay>, String>>();

    let handler = RcBlock::new(move |content: *mut SCShareableContent, error: *mut NSError| {
        let result = if !error.is_null() {
            // SAFETY: non-null checked; ScreenCaptureKit owns the error for the call's duration.
            Err(unsafe { &*error }.localizedDescription().to_string())
        } else if content.is_null() {
            Err("ScreenCaptureKit reported neither shareable content nor an error".to_string())
        } else {
            // SAFETY: non-null checked.
            let content = unsafe { &*content };
            // SAFETY: a documented accessor.
            let displays = unsafe { content.displays() };

            // firstObject, not "the one whose id matches CGMainDisplayID": ScreenCaptureKit lists
            // the main display first, and a Mac with the lid shut and no external monitor has no
            // display in this list at all — which is a clearer error than a lookup that misses.
            match displays.firstObject() {
                Some(display) => Ok(display),
                None => Err("this Mac reported no capturable displays".to_string()),
            }
        };

        let _ = sender.send(result);
    });

    // SAFETY: a documented class method, given a block of the declared signature.
    unsafe { SCShareableContent::getShareableContentWithCompletionHandler(&handler) };

    match receiver.recv_timeout(Duration::from_secs(10)) {
        Ok(Ok(display)) => Ok(display),
        Ok(Err(message)) => Err(anyhow!("{message}")),
        Err(_) => Err(anyhow!("ScreenCaptureKit did not answer the request for shareable content")),
    }
}

/// Silences an unused-import warning on a module that only touches the main thread indirectly.
#[allow(dead_code)]
fn _main_thread_marker_is_not_needed_here(_: Option<MainThreadMarker>) {}

// =================================================================================================
// The pure half: turning frames into wire messages.
// =================================================================================================

/// Encodes frames as JPEG tiles, sending only what changed.
///
/// Change detection is the difference between a remote session that is usable over a domestic
/// upload and one that is not: a typist changes a few hundred pixels per keystroke, and re-sending
/// a whole screen for that is roughly two hundred times the data.
pub struct FrameEncoder {
    previous: Option<Frame>,
    quality: u8,
    sequence: u32,
    /// Set when the next frame must be sent whole regardless of what changed — a new viewer has
    /// nothing to diff against, so anything less than a full frame leaves it with holes.
    force_full: bool,
}

impl FrameEncoder {
    pub fn new(quality: u8) -> Self {
        Self { previous: None, quality: quality.clamp(1, 100), sequence: 0, force_full: true }
    }

    pub fn set_quality(&mut self, quality: u8) {
        let quality = quality.clamp(1, 100);
        if quality != self.quality {
            self.quality = quality;
            // The picture is about to change everywhere, and a partial update at the new quality
            // next to old tiles at the previous one looks like a rendering fault.
            self.force_full = true;
        }
    }

    /// The wire messages for everything that changed since the last call. Empty when nothing did.
    pub fn encode_changes(&mut self, frame: &Frame) -> Vec<Vec<u8>> {
        // A resize invalidates every tile coordinate, so it is a full frame whether or not the
        // caller asked for one.
        let geometry_changed = self
            .previous
            .as_ref()
            .is_none_or(|previous| previous.width != frame.width || previous.height != frame.height);

        if self.force_full || geometry_changed {
            self.force_full = false;
            self.previous = Some(frame.clone());
            return self.encode_tile_at(frame, 0, 0, frame.width, frame.height).into_iter().collect();
        }

        let changed = self.changed_tiles(frame);
        if changed.is_empty() {
            return Vec::new();
        }

        let total_tiles = tiles_across(frame.width) * tiles_across(frame.height);
        let messages = if changed.len() as f64 >= total_tiles as f64 * FULL_FRAME_TILE_FRACTION {
            // A scroll, a window opening, a space switch. One image costs less than most of them
            // separately, each with its own JPEG headers.
            self.encode_tile_at(frame, 0, 0, frame.width, frame.height).into_iter().collect()
        } else {
            changed
                .into_iter()
                .filter_map(|(x, y, width, height)| self.encode_tile_at(frame, x, y, width, height))
                .collect()
        };

        self.previous = Some(frame.clone());
        messages
    }

    /// The tiles whose pixels differ from the previous frame, as `(x, y, width, height)`.
    fn changed_tiles(&self, frame: &Frame) -> Vec<(u32, u32, u32, u32)> {
        let Some(previous) = self.previous.as_ref() else {
            return Vec::new();
        };

        let mut changed = Vec::new();

        for tile_y in 0..tiles_across(frame.height) {
            for tile_x in 0..tiles_across(frame.width) {
                let x = tile_x * TILE_SIZE;
                let y = tile_y * TILE_SIZE;
                // The right and bottom edges are partial whenever the image is not a multiple of
                // the tile size, which is almost always.
                let width = TILE_SIZE.min(frame.width - x);
                let height = TILE_SIZE.min(frame.height - y);

                if tile_differs(previous, frame, x, y, width, height) {
                    changed.push((x, y, width, height));
                }
            }
        }

        changed
    }

    fn encode_tile_at(&mut self, frame: &Frame, x: u32, y: u32, width: u32, height: u32) -> Option<Vec<u8>> {
        let rgb = extract_rgb(frame, x, y, width, height)?;

        let mut jpeg = Vec::new();
        let mut encoder = image::codecs::jpeg::JpegEncoder::new_with_quality(&mut jpeg, self.quality);
        encoder
            .encode(&rgb, width, height, image::ExtendedColorType::Rgb8)
            .inspect_err(|err| logging::warn(&format!("could not JPEG-encode a screen tile: {err}")))
            .ok()?;

        self.sequence = self.sequence.wrapping_add(1);
        Some(encode_tile(
            x.min(u16::MAX as u32) as u16,
            y.min(u16::MAX as u32) as u16,
            width.min(u16::MAX as u32) as u16,
            height.min(u16::MAX as u32) as u16,
            self.sequence,
            &jpeg,
        ))
    }
}

fn tiles_across(length: u32) -> u32 {
    length.div_ceil(TILE_SIZE)
}

/// Whether one tile's pixels differ between two frames of identical dimensions.
fn tile_differs(previous: &Frame, current: &Frame, x: u32, y: u32, width: u32, height: u32) -> bool {
    let stride = current.width as usize * 4;
    let row_bytes = width as usize * 4;

    for row in 0..height as usize {
        let start = (y as usize + row) * stride + x as usize * 4;
        let end = start + row_bytes;

        if previous.bgra.len() < end || current.bgra.len() < end {
            // A short buffer is a bug rather than a change, but reporting "changed" makes it show
            // up as a redraw rather than as a frozen region.
            return true;
        }

        if previous.bgra[start..end] != current.bgra[start..end] {
            return true;
        }
    }

    false
}

/// Copies one rectangle out of a BGRA frame as tightly packed RGB.
///
/// Alpha is dropped because JPEG has none, and the channel order is swapped because the capture
/// format is BGRA and every image encoder here wants RGB — do this the other way round and the
/// remote screen is recognisable but blue.
fn extract_rgb(frame: &Frame, x: u32, y: u32, width: u32, height: u32) -> Option<Vec<u8>> {
    let stride = frame.width as usize * 4;
    let mut rgb = Vec::with_capacity(width as usize * height as usize * 3);

    for row in 0..height as usize {
        let start = (y as usize + row) * stride + x as usize * 4;
        let end = start + width as usize * 4;
        let source = frame.bgra.get(start..end)?;

        for pixel in source.chunks_exact(4) {
            rgb.push(pixel[2]);
            rgb.push(pixel[1]);
            rgb.push(pixel[0]);
        }
    }

    Some(rgb)
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::remote_protocol::TILE_HEADER_BYTES;

    /// A frame of one flat colour, so a change can be made deliberately in one place.
    fn flat_frame(width: u32, height: u32, colour: [u8; 4]) -> Frame {
        Frame::new(colour.repeat(width as usize * height as usize), width, height)
    }

    fn set_pixel(frame: &mut Frame, x: u32, y: u32, colour: [u8; 4]) {
        let offset = (y as usize * frame.width as usize + x as usize) * 4;
        frame.bgra[offset..offset + 4].copy_from_slice(&colour);
    }

    /// Reads the tile rectangle back out of a wire message.
    fn tile_rect(message: &[u8]) -> (u16, u16, u16, u16) {
        (
            u16::from_be_bytes([message[2], message[3]]),
            u16::from_be_bytes([message[4], message[5]]),
            u16::from_be_bytes([message[6], message[7]]),
            u16::from_be_bytes([message[8], message[9]]),
        )
    }

    #[test]
    fn the_first_frame_is_sent_whole() {
        // A viewer has nothing to diff against, so anything less leaves holes in the picture.
        let mut encoder = FrameEncoder::new(60);
        let frame = flat_frame(600, 400, [10, 20, 30, 255]);

        let messages = encoder.encode_changes(&frame);

        assert_eq!(messages.len(), 1);
        assert_eq!(tile_rect(&messages[0]), (0, 0, 600, 400));
    }

    #[test]
    fn an_unchanged_frame_sends_nothing_at_all() {
        // The idle case, and the one that decides whether this is usable on a metered link.
        let mut encoder = FrameEncoder::new(60);
        let frame = flat_frame(600, 400, [10, 20, 30, 255]);
        encoder.encode_changes(&frame);

        assert!(encoder.encode_changes(&frame).is_empty());
    }

    #[test]
    fn one_changed_pixel_sends_only_its_own_tile() {
        let mut encoder = FrameEncoder::new(60);
        let first = flat_frame(600, 400, [10, 20, 30, 255]);
        encoder.encode_changes(&first);

        let mut second = first.clone();
        set_pixel(&mut second, 300, 300, [200, 200, 200, 255]);
        let messages = encoder.encode_changes(&second);

        assert_eq!(messages.len(), 1);
        // (300, 300) is in the tile starting at (256, 256), which is 256 wide and 144 tall here
        // because the frame is only 400 pixels high.
        assert_eq!(tile_rect(&messages[0]), (256, 256, 256, 144));
    }

    #[test]
    fn a_change_in_two_places_sends_two_tiles() {
        let mut encoder = FrameEncoder::new(60);
        let first = flat_frame(600, 400, [10, 20, 30, 255]);
        encoder.encode_changes(&first);

        let mut second = first.clone();
        set_pixel(&mut second, 10, 10, [200, 200, 200, 255]);
        set_pixel(&mut second, 590, 390, [200, 200, 200, 255]);
        let messages = encoder.encode_changes(&second);

        assert_eq!(messages.len(), 2);
        assert_eq!(tile_rect(&messages[0]), (0, 0, 256, 256));
        assert_eq!(tile_rect(&messages[1]), (512, 256, 88, 144));
    }

    #[test]
    fn a_change_almost_everywhere_collapses_to_one_full_frame() {
        // What a scroll or a space switch looks like. Many tiles each carrying their own JPEG
        // tables costs more than one image.
        let mut encoder = FrameEncoder::new(60);
        let first = flat_frame(600, 400, [10, 20, 30, 255]);
        encoder.encode_changes(&first);

        let second = flat_frame(600, 400, [90, 90, 90, 255]);
        let messages = encoder.encode_changes(&second);

        assert_eq!(messages.len(), 1);
        assert_eq!(tile_rect(&messages[0]), (0, 0, 600, 400));
    }

    #[test]
    fn a_resized_screen_is_sent_whole_without_being_asked() {
        // Every tile coordinate the viewer holds is invalid after a resolution change.
        let mut encoder = FrameEncoder::new(60);
        encoder.encode_changes(&flat_frame(600, 400, [10, 20, 30, 255]));

        let messages = encoder.encode_changes(&flat_frame(800, 600, [10, 20, 30, 255]));

        assert_eq!(messages.len(), 1);
        assert_eq!(tile_rect(&messages[0]), (0, 0, 800, 600));
    }

    #[test]
    fn changing_quality_resends_everything() {
        // A partial update at the new quality beside tiles at the old one reads as a rendering
        // fault rather than as a setting.
        let mut encoder = FrameEncoder::new(60);
        let frame = flat_frame(600, 400, [10, 20, 30, 255]);
        encoder.encode_changes(&frame);

        encoder.set_quality(30);

        assert_eq!(encoder.encode_changes(&frame).len(), 1);
    }

    #[test]
    fn setting_the_same_quality_again_changes_nothing() {
        let mut encoder = FrameEncoder::new(60);
        let frame = flat_frame(600, 400, [10, 20, 30, 255]);
        encoder.encode_changes(&frame);

        encoder.set_quality(60);

        assert!(encoder.encode_changes(&frame).is_empty());
    }

    #[test]
    fn each_tile_carries_a_header_and_real_jpeg_bytes() {
        let mut encoder = FrameEncoder::new(60);
        let messages = encoder.encode_changes(&flat_frame(300, 200, [10, 20, 30, 255]));

        let message = &messages[0];
        assert!(message.len() > TILE_HEADER_BYTES);
        // JPEG's start-of-image marker, so this is a real image and not an empty buffer.
        assert_eq!(&message[TILE_HEADER_BYTES..TILE_HEADER_BYTES + 2], &[0xFF, 0xD8]);
    }

    #[test]
    fn bgra_becomes_rgb_in_the_right_order() {
        // Swap these and the remote screen is recognisable but blue, which is the kind of bug that
        // gets described as "the colours look odd" and takes an hour to find.
        let frame = Frame::new(vec![10, 20, 30, 255], 1, 1);

        assert_eq!(extract_rgb(&frame, 0, 0, 1, 1), Some(vec![30, 20, 10]));
    }

    #[test]
    fn extracting_a_rectangle_takes_the_right_pixels() {
        // A 2x2 frame with a distinct value per pixel, so a stride error shows up as the wrong one.
        let frame = Frame::new(
            vec![
                1, 1, 1, 255, 2, 2, 2, 255, // row 0
                3, 3, 3, 255, 4, 4, 4, 255, // row 1
            ],
            2,
            2,
        );

        assert_eq!(extract_rgb(&frame, 1, 1, 1, 1), Some(vec![4, 4, 4]));
        assert_eq!(extract_rgb(&frame, 0, 1, 2, 1), Some(vec![3, 3, 3, 4, 4, 4]));
    }

    #[test]
    fn tiles_across_covers_a_partial_last_tile() {
        assert_eq!(tiles_across(256), 1);
        assert_eq!(tiles_across(257), 2);
        assert_eq!(tiles_across(600), 3);
    }

    #[test]
    fn the_frame_slot_keeps_only_the_newest_frame() {
        // The whole reason it is a slot and not a queue: a remote user must see the screen as it is
        // now, not an ever-growing backlog of how it was.
        let slot = FrameSlot::default();
        slot.publish(flat_frame(2, 2, [1, 1, 1, 255]));
        slot.publish(flat_frame(2, 2, [2, 2, 2, 255]));

        let taken = slot.take(Duration::from_millis(10)).unwrap();

        assert_eq!(taken.bgra[0], 2);
        assert!(slot.take(Duration::from_millis(10)).is_none());
    }

    #[test]
    fn a_stopped_slot_does_not_block() {
        let slot = FrameSlot::default();
        slot.stop();

        // Would otherwise wait out the whole timeout on every call after capture ended.
        let started = std::time::Instant::now();
        assert!(slot.take(Duration::from_secs(30)).is_none());
        assert!(started.elapsed() < Duration::from_secs(1));
    }
}
