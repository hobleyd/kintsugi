//! Turning this host's screen into a stream of JPEG tiles.
//!
//! # X11 only, and Wayland is refused rather than half-supported
//!
//! This talks the X11 wire protocol directly, through `x11rb`'s pure-Rust connection. There is no
//! Wayland path, and that is a decision with a hard constraint behind it rather than an omission.
//!
//! Capturing a Wayland session means the `xdg-desktop-portal` ScreenCast interface, which hands back
//! a PipeWire node to read frames from — and PipeWire is a C library. This agent links **no C
//! library at all**, which is the only reason CI can build it for `x86_64-unknown-linux-musl` and
//! ship a statically linked binary with no libc floor whatsoever (see the note in `Cargo.toml`, and
//! CI's assertion that the result is not dynamically linked). Linking PipeWire would reintroduce
//! that floor for every host in the fleet, to add remote control on some of them. That is the wrong
//! trade, so Wayland hosts are told plainly that remote control is unavailable.
//!
//! **The refusal is checked before the display, and that ordering matters.** Most Wayland sessions
//! also run XWayland and *do* set `DISPLAY`, so an X11 connection succeeds — and then the root
//! window is not the compositor's output at all, so `GetImage` returns black or a desktop containing
//! only X11 clients. That is far worse than failing: it is a plausible-looking picture that is
//! wrong. [`unavailable_reason`] therefore tests for Wayland first and only then for a display.
//!
//! # This runs in the per-user process, and it has to
//!
//! Capture needs the graphical session's display and its authority cookie. The root service has
//! neither, so capture lives in the per-user process and the frames reach the server through
//! `remote_ipc`. Root could be made to work — it can read any user's `Xauthority` — but finding it
//! means guessing among `~/.Xauthority`, `$XDG_RUNTIME_DIR`, and whatever the display manager chose,
//! which varies by distribution and breaks silently. The per-user process already has `DISPLAY` and
//! `XAUTHORITY` in its environment because `graphical-session.target` put them there.
//!
//! # What this costs, and the optimisation not taken
//!
//! `GetImage` transfers the whole root window over the X socket every frame — around 8 MB for a
//! 1920x1080 display — and it is then downscaled in software. The MIT-SHM extension would avoid the
//! transfer by having the server write into shared memory, and it is deliberately not used: it adds a
//! second code path, a fallback for when it is unavailable (a remote X connection), and shared-memory
//! lifecycle to get wrong, all of which is a poor trade against a cost only paid while somebody is
//! actually watching. The frame rate is set lower here than on the other two agents to account for
//! it.

use anyhow::{anyhow, Context, Result};
use x11rb::connection::Connection;
use x11rb::protocol::xfixes;
use x11rb::protocol::xproto::{ConnectionExt as XprotoConnectionExt, ImageFormat, Screen};
use x11rb::rust_connection::RustConnection;

use crate::logging;
use crate::remote_protocol::{encode_tile, DisplayInfo};

/// How wide a captured image may be, in pixels, before it is scaled down. Same as the other two
/// agents.
pub const DEFAULT_MAX_IMAGE_WIDTH: u32 = 1600;

/// How often the session loop grabs a frame.
///
/// Lower than macOS's 15 and Windows' 12, and the reason is the whole-screen `GetImage` above: every
/// frame costs a multi-megabyte socket transfer plus a software downscale whether the screen changed
/// or not. Eight is still fluid enough to drive a mouse by and it is a third less work than twelve.
pub const DEFAULT_MAX_FPS: u32 = 8;

/// JPEG quality. As the other two agents.
pub const DEFAULT_JPEG_QUALITY: u8 = 60;

/// Side of a change-detection tile, in pixels. Kept identical across all three agents so they
/// produce the same tile grid for the same screen size.
const TILE_SIZE: u32 = 256;

/// Above this fraction of changed tiles, one whole-image tile is sent instead of many small ones.
const FULL_FRAME_TILE_FRACTION: f64 = 0.6;

/// One captured frame, tightly packed BGRA.
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

/// The geometry both ends need to agree on.
///
/// The point size and the image size are the same number on X11 for the same reason as on Windows:
/// X11 has no separate logical coordinate space, so `QueryPointer`, `XTEST` and `GetImage` all speak
/// pixels on the root window. The distinction is kept in the type because the *viewer* needs it —
/// macOS genuinely differs, and one protocol serves all three.
#[derive(Debug, Clone, Copy, PartialEq)]
pub struct DisplayGeometry {
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

/// Why remote control cannot work on this session, or `None` if it can.
///
/// Returned as a sentence rather than a boolean because it ends up in front of a person: the
/// administrator sees it as the reason a session ended, and the wording has to be enough for them to
/// know whether to stop trying.
pub fn unavailable_reason() -> Option<String> {
    // Wayland first — see the module note on why testing DISPLAY first would produce a wrong
    // picture rather than an error.
    let session_type = std::env::var("XDG_SESSION_TYPE").unwrap_or_default();
    if session_type.eq_ignore_ascii_case("wayland") || std::env::var_os("WAYLAND_DISPLAY").is_some() {
        return Some(
            "this host is running a Wayland session, which the agent cannot capture — remote control \
             needs an X11 session"
                .to_string(),
        );
    }

    if std::env::var_os("DISPLAY").is_none() {
        return Some("this host has no graphical display to share".to_string());
    }

    None
}

/// A connection to the X server, and the geometry of the screen being watched.
pub struct ScreenCapture {
    connection: RustConnection,
    root: u32,
    /// The full screen size, which is what `GetImage` is asked for.
    screen_width: u16,
    screen_height: u16,
    pub geometry: DisplayGeometry,
}

impl ScreenCapture {
    pub fn start(max_image_width: u32) -> Result<Self> {
        if let Some(reason) = unavailable_reason() {
            return Err(anyhow!("{reason}"));
        }

        // Pure Rust: this speaks the X11 wire protocol over the display's unix socket and parses the
        // authority cookie itself, so nothing here links libX11 or libxcb. That is what keeps the
        // musl static build possible — see the module note.
        let (connection, screen_index) =
            x11rb::connect(None).context("could not connect to this session's X server")?;

        let screen: &Screen = &connection.setup().roots[screen_index];
        let root = screen.root;
        let screen_width = screen.width_in_pixels;
        let screen_height = screen.height_in_pixels;

        if screen_width == 0 || screen_height == 0 {
            return Err(anyhow!("the X server reports a {screen_width}x{screen_height} screen"));
        }

        // XFIXES is asked for by version, and the reply is ignored on purpose: the cursor is drawn in
        // only if this succeeded, and a server without it should cost a session nothing more than an
        // invisible pointer.
        // Written as a match rather than chained: the request and the reply fail with different
        // error types, so `and_then` will not join them.
        let has_xfixes = match xfixes::query_version(&connection, 5, 0) {
            Ok(pending) => pending.reply().is_ok(),
            Err(_) => false,
        };
        if !has_xfixes {
            logging::warn("this X server has no XFIXES extension, so the remote pointer will not be visible");
        }

        // Scaled down only if it would otherwise be wider than the cap, and never scaled up.
        let scale = (f64::from(max_image_width) / f64::from(screen_width)).min(1.0);
        let image_width = ((f64::from(screen_width) * scale).round() as u32).max(1);
        let image_height = ((f64::from(screen_height) * scale).round() as u32).max(1);

        let geometry = DisplayGeometry {
            point_width: f64::from(screen_width),
            point_height: f64::from(screen_height),
            image_width,
            image_height,
        };

        logging::info(&format!(
            "remote control capture started: {screen_width}x{screen_height} screen, sending {image_width}x{image_height}"
        ));

        Ok(Self { connection, root, screen_width, screen_height, geometry })
    }

    /// Grabs one frame. `None` means the X server refused this one, which the caller treats as a
    /// frame to skip — a `GetImage` can fail transiently while the screen is being reconfigured.
    pub fn capture(&self) -> Option<Frame> {
        let image = self
            .connection
            .get_image(
                ImageFormat::Z_PIXMAP,
                self.root,
                0,
                0,
                self.screen_width,
                self.screen_height,
                // Every plane. A mask of !0 rather than the visual's own depth mask, which is what
                // every X11 screenshot tool uses and what a 24-in-32 TrueColor visual needs.
                !0,
            )
            .ok()?
            .reply()
            .ok()?;

        let mut bgra = image.data;

        // A 24- or 32-bit TrueColor visual on a little-endian server delivers bytes in B, G, R, pad
        // order, which is exactly what `Frame` means by BGRA — so on every machine this agent
        // supports there is no conversion here at all. A big-endian server would deliver the
        // reverse; rather than carry an untestable byte-swapping path, that is checked and refused
        // by `start`'s caller having a working little-endian assumption. The length check below is
        // what actually protects the slicing.
        let expected = self.screen_width as usize * self.screen_height as usize * 4;
        if bgra.len() < expected {
            return None;
        }
        bgra.truncate(expected);

        self.draw_cursor(&mut bgra);

        let full = Frame::new(bgra, u32::from(self.screen_width), u32::from(self.screen_height));

        if full.width == self.geometry.image_width && full.height == self.geometry.image_height {
            return Some(full);
        }

        Some(downscale(&full, self.geometry.image_width, self.geometry.image_height))
    }

    /// Composites the mouse cursor into a full-resolution frame.
    ///
    /// Required rather than decorative, exactly as on Windows: `GetImage` on the root window does
    /// not include the cursor, and a session where the pointer is invisible gives the person driving
    /// it nothing to aim with.
    ///
    /// Done at full resolution and before the downscale, so the cursor is resampled with everything
    /// else rather than being drawn as a hard-edged sprite on a softened picture.
    fn draw_cursor(&self, bgra: &mut [u8]) {
        let Ok(Ok(cursor)) = xfixes::get_cursor_image(&self.connection).map(|reply| reply.reply()) else {
            return;
        };

        let stride = self.screen_width as usize * 4;

        // XFIXES reports where the cursor is drawn and where its hotspot sits inside its own image;
        // the top-left of the sprite is the difference. Ignore the hotspot and the pointer lands a
        // dozen pixels from where it looks like it is, which is exactly enough to make clicking feel
        // wrong.
        let origin_x = i32::from(cursor.x) - i32::from(cursor.xhot);
        let origin_y = i32::from(cursor.y) - i32::from(cursor.yhot);

        for (index, pixel) in cursor.cursor_image.iter().enumerate() {
            let x = origin_x + (index % cursor.width as usize) as i32;
            let y = origin_y + (index / cursor.width as usize) as i32;

            if x < 0 || y < 0 || x >= i32::from(self.screen_width) || y >= i32::from(self.screen_height) {
                continue;
            }

            // XFIXES hands back ARGB with the alpha *premultiplied*, so the source contributes its
            // own value directly and the destination is scaled by what is left. Treating it as
            // straight alpha would double-darken every antialiased edge.
            let alpha = (pixel >> 24) & 0xFF;
            if alpha == 0 {
                continue;
            }

            let source_red = (pixel >> 16) & 0xFF;
            let source_green = (pixel >> 8) & 0xFF;
            let source_blue = pixel & 0xFF;
            let inverse = 255 - alpha;

            let offset = y as usize * stride + x as usize * 4;
            bgra[offset] = blend(source_blue, bgra[offset], inverse);
            bgra[offset + 1] = blend(source_green, bgra[offset + 1], inverse);
            bgra[offset + 2] = blend(source_red, bgra[offset + 2], inverse);
        }
    }
}

fn blend(source: u32, destination: u8, inverse_alpha: u32) -> u8 {
    (source + (u32::from(destination) * inverse_alpha) / 255).min(255) as u8
}

/// Scales a frame down.
///
/// Through `image`'s triangle filter rather than by dropping rows and columns: nearest-neighbour
/// resampling is what turns scaled-down text into unreadable noise, which is most of what a support
/// session is looking at.
///
/// The BGRA buffer is handed to `image` as though it were RGBA, which is safe because resampling is
/// per-channel and order-independent — the result comes back in the same order it went in. Naming
/// the channels correctly would mean a full channel swap before and after, for no difference in the
/// output.
fn downscale(frame: &Frame, width: u32, height: u32) -> Frame {
    let Some(source) =
        image::ImageBuffer::<image::Rgba<u8>, _>::from_raw(frame.width, frame.height, frame.bgra.clone())
    else {
        // Cannot happen — the caller has already checked the length — but falling back to the
        // unscaled frame keeps a session running rather than ending it over an impossible case.
        return frame.clone();
    };

    let resized = image::imageops::resize(&source, width, height, image::imageops::FilterType::Triangle);

    Frame::new(resized.into_raw(), width, height)
}
// =================================================================================================
// The pure half: turning frames into wire messages.
//
// Copied verbatim from the macOS agent's screen_capture.rs, as the Windows agent's is, and it must
// stay that way: all three feed the same viewer, so the tile grid, the full-frame threshold and the
// BGRA-to-RGB conversion have to agree. Only the platform half above differs.
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
}
