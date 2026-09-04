//! Turning this host's screen into a stream of JPEG tiles.
//!
//! # GDI, not Desktop Duplication, and the reason is Remote Desktop
//!
//! DXGI Desktop Duplication is the modern API and the one Microsoft points at: it hands back the
//! composited desktop with dirty rectangles for free, on the hardware path. It is also
//! **unavailable in a Remote Desktop session** — `DuplicateOutput` returns
//! `DXGI_ERROR_UNSUPPORTED` there, because an RDP session has no physical output to duplicate. A
//! fleet-management agent is very often reached over RDP, and a remote-control feature that fails
//! precisely on the machines an administrator is already logged into remotely is worse than one that
//! is a little less efficient everywhere.
//!
//! So this is `StretchBlt` from the desktop DC, which works in a console session and an RDP session
//! alike, needs no GPU adapter, and adds no dependency — `Win32_Graphics_Gdi` was already here for
//! the tray icon and the progress window. The cost is real and worth naming: GDI does not compose
//! hardware video overlays, and it misses some layered windows (see the note on `CAPTUREBLT`
//! below). For looking at Settings, Explorer, a browser or an installer — which is what a support
//! session is — it is entirely adequate.
//!
//! The macOS agent uses ScreenCaptureKit for the same job, and that is not an inconsistency: there
//! the deprecated alternative had no compensating advantage, whereas here the modern API gives up a
//! whole class of host.
//!
//! # This runs in the tray process, and it has to
//!
//! Session 0 isolation means the service cannot see a logged-in user's desktop whatever privileges
//! it holds, so capture lives in the per-user process and the frames reach the server through
//! `remote_ipc`. That is the whole reason Windows remote control has two processes in it where
//! macOS has one.
//!
//! # No TCC equivalent, and no permission to ask for
//!
//! Unlike macOS there is nothing to grant: a process in the user's own session may read the screen
//! and post input without asking anybody. That removes the PPPC/code-signing problem the macOS
//! agent has — and it also removes the only thing that would have stopped this working, so the
//! consent dialog is the *sole* control here rather than one of two.

use crate::logging;
use crate::remote_protocol::{encode_tile, DisplayInfo};

/// How wide a captured image may be, in pixels, before it is scaled down. Same value the macOS
/// agent uses, and for the same reason: text stays readable and a full frame is a couple of hundred
/// kilobytes rather than a megabyte.
pub const DEFAULT_MAX_IMAGE_WIDTH: u32 = 1600;

/// How often the session loop grabs a frame.
///
/// A ceiling rather than a rate, but it means something different here than on macOS.
/// ScreenCaptureKit *pushes* and sends nothing when the screen has not changed, so an idle macOS
/// screen costs nothing at all. GDI is pull-based: this agent has to grab a frame to find out
/// whether anything moved, so an idle screen still costs one `StretchBlt` and one tile comparison
/// per tick. That is why this is 12 rather than the 15 macOS asks for — the difference is invisible
/// to the eye and it is a fifth off the cost of watching a screen that is doing nothing.
pub const DEFAULT_MAX_FPS: u32 = 12;

/// JPEG quality. As macOS.
pub const DEFAULT_JPEG_QUALITY: u8 = 60;

/// Side of a change-detection tile, in pixels. Kept identical to the macOS agent so the two produce
/// the same tile grid for the same screen size, which is what lets one viewer test cover both.
const TILE_SIZE: u32 = 256;

/// Above this fraction of changed tiles, one whole-image tile is sent instead of many small ones.
const FULL_FRAME_TILE_FRACTION: f64 = 0.6;

/// One captured frame, tightly packed BGRA.
///
/// Packed with no row padding, which a 32-bit DIB gives for free: a DIB's rows are DWORD-aligned and
/// `width * 4` always is. The macOS agent has to strip IOSurface's padding here; this one does not,
/// and the type is identical either way so the encoder below is shared verbatim between them.
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
/// On macOS the point size and the pixel size genuinely differ and conflating them puts the remote
/// cursor in the wrong place. On Windows they are the same number here, and that is a decision
/// rather than a coincidence: this process is left DPI-*unaware*, so `GetSystemMetrics` reports the
/// virtualised desktop size, `StretchBlt` captures that same virtualised desktop, and `SendInput`'s
/// absolute coordinates normalise over it too. All three agree.
///
/// Making the process per-monitor DPI aware would capture more actual pixels on a scaled display —
/// and would also change the size of every window the tray already draws (the progress window and
/// the dialogs in `win32`/`dialogs`), which would be a rendering regression in existing UI paid for
/// a sharper remote screen. Not worth it; see the decision log.
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

#[cfg(windows)]
pub use platform::ScreenCapture;

#[cfg(windows)]
mod platform {
    use std::ptr;

    use anyhow::{anyhow, Result};
    use windows_sys::Win32::Foundation::{GetLastError, HWND};
    use windows_sys::Win32::Graphics::Gdi::{
        BitBlt, CreateCompatibleDC, CreateDIBSection, DeleteDC, DeleteObject, GetDC, ReleaseDC,
        SelectObject, SetBrushOrgEx, SetStretchBltMode, StretchBlt, BITMAPINFO, BITMAPINFOHEADER,
        BI_RGB, DIB_RGB_COLORS, HALFTONE, HBITMAP, HDC, HGDIOBJ, SRCCOPY,
    };
    use windows_sys::Win32::UI::WindowsAndMessaging::{
        DrawIconEx, GetCursorInfo, GetIconInfo, GetSystemMetrics, CURSORINFO, CURSOR_SHOWING,
        DI_NORMAL, ICONINFO, SM_CXSCREEN, SM_CYSCREEN,
    };

    use crate::logging;

    use super::{DisplayGeometry, Frame};

    /// The primary display, captured and scaled into a DIB this struct owns for the whole session.
    ///
    /// Everything is allocated once. A `StretchBlt` into a bitmap that already exists is the whole
    /// per-frame cost; recreating the DC and the DIB each time would dominate it.
    pub struct ScreenCapture {
        screen_dc: HDC,
        memory_dc: HDC,
        bitmap: HBITMAP,
        previous_bitmap: HGDIOBJ,
        /// Points into the DIB section. Valid for as long as `bitmap` is, which is this struct's
        /// lifetime — `Drop` deletes the bitmap last.
        bits: *mut u8,
        screen_width: i32,
        screen_height: i32,
        pub geometry: DisplayGeometry,
    }

    impl ScreenCapture {
        pub fn start(max_image_width: u32) -> Result<Self> {
            // SAFETY: documented, no arguments beyond a constant.
            let (screen_width, screen_height) =
                unsafe { (GetSystemMetrics(SM_CXSCREEN), GetSystemMetrics(SM_CYSCREEN)) };

            if screen_width <= 0 || screen_height <= 0 {
                return Err(anyhow!(
                    "Windows reports a {screen_width}x{screen_height} primary display, which cannot be captured"
                ));
            }

            // Scaled down only if it would otherwise be wider than the cap, and never scaled up.
            let scale = (f64::from(max_image_width) / f64::from(screen_width)).min(1.0);
            let image_width = ((f64::from(screen_width) * scale).round() as u32).max(1);
            let image_height = ((f64::from(screen_height) * scale).round() as u32).max(1);

            // SAFETY: a NULL window handle asks for the whole screen, which is documented. Released
            // in Drop.
            let screen_dc = unsafe { GetDC(ptr::null_mut()) };
            if screen_dc.is_null() {
                return Err(anyhow!("could not open a device context for the screen"));
            }

            // SAFETY: `screen_dc` is a valid DC from the call above.
            let memory_dc = unsafe { CreateCompatibleDC(screen_dc) };
            if memory_dc.is_null() {
                // SAFETY: pairs with the GetDC above.
                unsafe { ReleaseDC(ptr::null_mut(), screen_dc) };
                return Err(anyhow!("could not create a memory device context"));
            }

            let info = BITMAPINFO {
                bmiHeader: BITMAPINFOHEADER {
                    biSize: std::mem::size_of::<BITMAPINFOHEADER>() as u32,
                    biWidth: image_width as i32,
                    // Negative, which asks for a top-down bitmap. A DIB is bottom-up by default and
                    // every consumer of `Frame` — the tile encoder, the viewer — assumes row 0 is
                    // the top, so flipping here costs nothing and flipping later would cost a copy
                    // per frame.
                    biHeight: -(image_height as i32),
                    biPlanes: 1,
                    biBitCount: 32,
                    biCompression: BI_RGB,
                    biSizeImage: 0,
                    biXPelsPerMeter: 0,
                    biYPelsPerMeter: 0,
                    biClrUsed: 0,
                    biClrImportant: 0,
                },
                // Zeroed explicitly: RGBQUAD has no Default, and a 32-bit BI_RGB DIB has no
                // palette anyway — this field exists only because BITMAPINFO is declared with one.
                bmiColors: [windows_sys::Win32::Graphics::Gdi::RGBQUAD {
                    rgbBlue: 0,
                    rgbGreen: 0,
                    rgbRed: 0,
                    rgbReserved: 0,
                }; 1],
            };

            let mut bits: *mut std::ffi::c_void = ptr::null_mut();
            // SAFETY: `info` describes a valid 32-bit DIB and `bits` is a valid out-pointer. The
            // returned bitmap and its pixels are owned by this struct until Drop.
            let bitmap = unsafe {
                CreateDIBSection(memory_dc, &info, DIB_RGB_COLORS, &mut bits, ptr::null_mut(), 0)
            };

            if bitmap.is_null() || bits.is_null() {
                // SAFETY: both handles are valid and are being released on the failure path.
                unsafe {
                    DeleteDC(memory_dc);
                    ReleaseDC(ptr::null_mut(), screen_dc);
                }
                // SAFETY: no preconditions.
                return Err(anyhow!("could not create a {image_width}x{image_height} capture bitmap (error {})", unsafe {
                    GetLastError()
                }));
            }

            // SAFETY: both are valid handles; the previously selected object is kept so Drop can
            // put it back before deleting ours, which is the documented lifecycle.
            let previous_bitmap = unsafe { SelectObject(memory_dc, bitmap as HGDIOBJ) };

            // HALFTONE is the only stretch mode that resamples rather than dropping rows and
            // columns, and dropped rows are what turns scaled-down text into noise. The
            // SetBrushOrgEx call after it is required by the documentation, not optional.
            // SAFETY: documented; both take a valid DC.
            unsafe {
                SetStretchBltMode(memory_dc, HALFTONE);
                SetBrushOrgEx(memory_dc, 0, 0, ptr::null_mut());
            }

            let geometry = DisplayGeometry {
                point_width: f64::from(screen_width),
                point_height: f64::from(screen_height),
                image_width,
                image_height,
            };

            logging::info(&format!(
                "remote control capture started: {screen_width}x{screen_height} screen, sending {image_width}x{image_height}"
            ));

            Ok(Self {
                screen_dc,
                memory_dc,
                bitmap,
                previous_bitmap,
                bits: bits as *mut u8,
                screen_width,
                screen_height,
                geometry,
            })
        }

        /// Grabs one frame. `None` means the blit failed, which the caller treats as a frame to skip
        /// rather than as the end of the session — a `StretchBlt` can fail transiently across a
        /// desktop switch (the lock screen, a UAC prompt, fast user switching).
        pub fn capture(&self) -> Option<Frame> {
            let width = self.geometry.image_width;
            let height = self.geometry.image_height;

            let blitted = if width as i32 == self.screen_width && height as i32 == self.screen_height {
                // SAFETY: both DCs are valid and the bitmap selected into `memory_dc` is exactly
                // this size. BitBlt rather than StretchBlt when no scaling is needed — it is the
                // cheaper path and avoids HALFTONE resampling a 1:1 copy.
                unsafe {
                    BitBlt(
                        self.memory_dc, 0, 0, width as i32, height as i32,
                        self.screen_dc, 0, 0, SRCCOPY,
                    )
                }
            } else {
                // SAFETY: as above; the source rectangle is the whole primary display.
                //
                // Deliberately *without* CAPTUREBLT. It would additionally include layered windows,
                // but Microsoft documents it as slow, it has a long history of making the screen
                // flash on some configurations, and a support session that strobes the user's
                // display twelve times a second is a worse failure than one that occasionally misses
                // a tooltip. With DWM composition on every supported Windows version, ordinary
                // windows are captured either way.
                unsafe {
                    StretchBlt(
                        self.memory_dc, 0, 0, width as i32, height as i32,
                        self.screen_dc, 0, 0, self.screen_width, self.screen_height, SRCCOPY,
                    )
                }
            };

            if blitted == 0 {
                return None;
            }

            self.draw_cursor();

            let length = width as usize * height as usize * 4;
            // SAFETY: the DIB section is exactly `width * height * 4` bytes — 32bpp with no row
            // padding, since `width * 4` is always DWORD-aligned — and `bits` stays valid until Drop
            // deletes the bitmap. Copied out rather than borrowed because the next blit overwrites
            // it in place.
            let bgra = unsafe { std::slice::from_raw_parts(self.bits, length) }.to_vec();

            Some(Frame::new(bgra, width, height))
        }

        /// Draws the mouse cursor into the captured frame.
        ///
        /// Required, not decorative: `BitBlt` from the screen DC does **not** include the cursor,
        /// and a remote session where the pointer is invisible is unusable — the person driving it
        /// has nothing to aim with. ScreenCaptureKit gives macOS this for free with
        /// `setShowsCursor`; GDI does not, so it is drawn by hand.
        ///
        /// Best-effort throughout: a frame with no cursor drawn on it is far better than no frame.
        fn draw_cursor(&self) {
            let mut cursor = CURSORINFO {
                cbSize: std::mem::size_of::<CURSORINFO>() as u32,
                flags: 0,
                hCursor: ptr::null_mut(),
                ptScreenPos: windows_sys::Win32::Foundation::POINT { x: 0, y: 0 },
            };

            // SAFETY: documented; `cursor.cbSize` is set as required.
            if unsafe { GetCursorInfo(&mut cursor) } == 0 {
                return;
            }

            if cursor.flags != CURSOR_SHOWING || cursor.hCursor.is_null() {
                return;
            }

            // The hotspot, so the drawn cursor's tip lands where the pointer actually is rather than
            // its top-left corner — an offset of a dozen pixels, which is exactly enough to make
            // clicking feel wrong.
            let mut icon = ICONINFO {
                fIcon: 0,
                xHotspot: 0,
                yHotspot: 0,
                hbmMask: ptr::null_mut(),
                hbmColor: ptr::null_mut(),
            };

            // SAFETY: documented; `cursor.hCursor` is non-null and `icon` is a valid out-pointer.
            // GetIconInfo hands back two bitmaps the caller owns, deleted below.
            let has_icon_info = unsafe { GetIconInfo(cursor.hCursor, &mut icon) } != 0;

            let scale_x = f64::from(self.geometry.image_width) / f64::from(self.screen_width);
            let scale_y = f64::from(self.geometry.image_height) / f64::from(self.screen_height);

            let x = ((f64::from(cursor.ptScreenPos.x) - f64::from(icon.xHotspot)) * scale_x).round() as i32;
            let y = ((f64::from(cursor.ptScreenPos.y) - f64::from(icon.yHotspot)) * scale_y).round() as i32;

            // SAFETY: a valid DC and a valid cursor handle. Zero width/height asks for the
            // cursor's own size, which is what is wanted — scaling the cursor itself would make it
            // blurry for no benefit, since it is a handful of pixels either way.
            unsafe {
                DrawIconEx(self.memory_dc, x, y, cursor.hCursor, 0, 0, 0, ptr::null_mut(), DI_NORMAL);
            }

            if has_icon_info {
                // SAFETY: both bitmaps were created by GetIconInfo and are owned by this caller.
                unsafe {
                    if !icon.hbmMask.is_null() {
                        DeleteObject(icon.hbmMask as HGDIOBJ);
                    }
                    if !icon.hbmColor.is_null() {
                        DeleteObject(icon.hbmColor as HGDIOBJ);
                    }
                }
            }
        }
    }

    impl Drop for ScreenCapture {
        fn drop(&mut self) {
            // In reverse order of acquisition, and the bitmap is put back before ours is deleted —
            // deleting an object that is still selected into a DC is documented as failing, which
            // would leak it for the life of the process.
            // SAFETY: every handle here was created by `start` and is still owned by this struct.
            unsafe {
                SelectObject(self.memory_dc, self.previous_bitmap);
                DeleteObject(self.bitmap as HGDIOBJ);
                DeleteDC(self.memory_dc);
                ReleaseDC(ptr::null_mut() as HWND, self.screen_dc);
            }
        }
    }
}

// =================================================================================================
// The pure half: turning frames into wire messages.
//
// Copied verbatim from the macOS agent's screen_capture.rs, and it must stay that way: both agents
// feed the same viewer, so the tile grid, the full-frame threshold and the BGRA-to-RGB conversion
// have to agree. Only the platform half above differs.
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
