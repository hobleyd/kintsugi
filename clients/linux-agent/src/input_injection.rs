//! Posting the remote keyboard and mouse into this host's own X session.
//!
//! # XTEST, over the wire, with no C library
//!
//! Input goes in through the XTEST extension's `FakeInput`, spoken directly over the X11 socket by
//! `x11rb`. The obvious alternative — `libXtst`'s `XTestFakeKeyEvent` — would link a C library, and
//! this agent links none: that is the only reason CI can ship a statically linked musl binary with
//! no libc floor. See the same note in `screen_capture`, which is the other half of the same
//! constraint.
//!
//! # Keycodes, not keysyms
//!
//! A key goes in as an **X11 keycode**, which on every modern Linux X server is the kernel's evdev
//! code plus 8. A keycode names a position on the keyboard and the X server applies the session's
//! own keymap to it — which is the only thing that can be correct here: the viewer sends the
//! physical key the administrator pressed, and an administrator on a US keyboard controlling a host
//! set to a French layout must produce what *that host's* user would get by pressing there.
//!
//! Going through keysyms instead would mean choosing a character and then hunting for a keycode that
//! currently produces it — which is both layout-dependent and, for anything not on the host's
//! layout at all, impossible. All three agents take the positional route; only the name of the
//! positional thing differs (`virtual_key_for_hid` on macOS, `scan_code_for_hid` on Windows).
//!
//! # The +8, and why it is not configurable
//!
//! The offset is a historical constant of the X protocol: keycodes 0-7 are reserved, so the evdev
//! driver maps kernel code 0 to keycode 8. Every X server using the evdev or libinput driver does
//! this, which is every Linux X server this agent will meet. A host using a genuinely different
//! keyboard driver would need its own map, and there is no way to discover the offset at runtime
//! short of reading the keymap and pattern-matching it — not worth carrying for a case that does not
//! occur.

use anyhow::{Context, Result};
use x11rb::connection::Connection;
use x11rb::protocol::xproto::Window;
use x11rb::protocol::xtest::ConnectionExt as XtestConnectionExt;
use x11rb::rust_connection::RustConnection;

use crate::logging;
use crate::remote_protocol::{MouseButton, PointerAction, ViewerInput};

/// X11 reserves keycodes 0-7, so the evdev driver offsets every kernel code by this. See the module
/// note.
const EVDEV_KEYCODE_OFFSET: u8 = 8;

/// X core event types, which `FakeInput` takes as its `type` argument. Spelled out because they are
/// protocol constants rather than anything x11rb surfaces as an enum for this call.
const KEY_PRESS: u8 = 2;
const KEY_RELEASE: u8 = 3;
const BUTTON_PRESS: u8 = 4;
const BUTTON_RELEASE: u8 = 5;
const MOTION_NOTIFY: u8 = 6;

/// X11 button numbers. 1-3 are the physical buttons; 4-7 are the four wheel directions, which X11
/// models as buttons that are pressed and released rather than as an axis.
const BUTTON_LEFT: u8 = 1;
const BUTTON_MIDDLE: u8 = 2;
const BUTTON_RIGHT: u8 = 3;
const BUTTON_WHEEL_UP: u8 = 4;
const BUTTON_WHEEL_DOWN: u8 = 5;
const BUTTON_WHEEL_LEFT: u8 = 6;
const BUTTON_WHEEL_RIGHT: u8 = 7;

/// How many pixels of browser scroll make one wheel click. A browser reports wheel deltas in pixels
/// and a trackpad reports small ones continuously; treating every pixel as a click would make a
/// gentle two-finger scroll fly down a page.
pub(crate) const PIXELS_PER_CLICK: f64 = 100.0;

/// The most wheel clicks one scroll message may turn into.
///
/// X11 has no scroll *amount* — each click is a discrete button press — so a large delta becomes a
/// burst of round trips to the X server. Without a cap, one flick of a high-resolution trackpad
/// could queue hundreds and stall the session loop for a visible moment.
pub(crate) const MAX_WHEEL_CLICKS: u32 = 10;

/// Maps a USB HID keyboard usage code to an X11 keycode.
///
/// The evdev code plus [`EVDEV_KEYCODE_OFFSET`], which is what XTEST wants. The Wayland path wants
/// the evdev code *un*-offset — see [`evdev_keycode_for_hid`] — so the two are kept as one table and
/// one addition rather than two tables that could disagree by eight.
pub fn xtest_keycode_for_hid(hid: u32) -> Option<u8> {
    // F13-F20 are above 190, so evdev + 8 still fits in a u8 — but only just, and a keycode is a
    // u8 in the protocol. Checked rather than assumed so a future addition above 247 fails here
    // instead of wrapping into an unrelated key.
    evdev_keycode_for_hid(hid)?.checked_add(EVDEV_KEYCODE_OFFSET)
}

/// Maps a USB HID keyboard usage code to a **kernel evdev** code.
///
/// `hid` is accepted as the full 32-bit value Flutter reports (`0x0007_0004` for A) as well as the
/// bare usage (`0x04`); anything outside the keyboard usage page returns `None` and is ignored.
///
/// The values below can be checked against `linux/input-event-codes.h` directly, which is the whole
/// reason this is the base rather than the X11 form. Two callers want different ends of it: XTEST
/// needs the offset added (above), and xdg-desktop-portal's `NotifyKeyboardKeycode` wants exactly
/// this — see `wire.rs` in `clients/linux-agent-wayland`. Adding the offset for the portal would
/// type a key eight positions along the physical keyboard, which on a QWERTY layout turns `a` into
/// `f` and looks like a broken keymap rather than a broken constant.
pub fn evdev_keycode_for_hid(hid: u32) -> Option<u8> {
    // Flutter encodes the usage page in the high 16 bits. Page 0x0007 is keyboard/keypad; page
    // 0x000C is consumer control (volume, brightness), which the kernel does have codes for but
    // which no keymap binds to a position.
    let usage = match hid >> 16 {
        0x0000 => hid,
        0x0007 => hid & 0xFFFF,
        _ => return None,
    };

    let evdev: u8 = match usage {
        // Letters. The evdev codes follow the physical rows, so they are in no alphabetical order
        // and a formula would be wrong.
        0x04 => 30, // A
        0x05 => 48, // B
        0x06 => 46, // C
        0x07 => 32, // D
        0x08 => 18, // E
        0x09 => 33, // F
        0x0A => 34, // G
        0x0B => 35, // H
        0x0C => 23, // I
        0x0D => 36, // J
        0x0E => 37, // K
        0x0F => 38, // L
        0x10 => 50, // M
        0x11 => 49, // N
        0x12 => 24, // O
        0x13 => 25, // P
        0x14 => 16, // Q
        0x15 => 19, // R
        0x16 => 31, // S
        0x17 => 20, // T
        0x18 => 22, // U
        0x19 => 47, // V
        0x1A => 17, // W
        0x1B => 45, // X
        0x1C => 21, // Y
        0x1D => 44, // Z

        // Digit row.
        0x1E => 2,  // 1
        0x1F => 3,  // 2
        0x20 => 4,  // 3
        0x21 => 5,  // 4
        0x22 => 6,  // 5
        0x23 => 7,  // 6
        0x24 => 8,  // 7
        0x25 => 9,  // 8
        0x26 => 10, // 9
        0x27 => 11, // 0

        0x28 => 28, // Enter
        0x29 => 1,  // Escape
        0x2A => 14, // Backspace
        0x2B => 15, // Tab
        0x2C => 57, // Space
        0x2D => 12, // Minus
        0x2E => 13, // Equal
        0x2F => 26, // LeftBracket
        0x30 => 27, // RightBracket
        0x31 => 43, // Backslash
        0x32 => 43, // NonUsHash — the same physical key on an ANSI board
        0x33 => 39, // Semicolon
        0x34 => 40, // Apostrophe
        0x35 => 41, // Grave
        0x36 => 51, // Comma
        0x37 => 52, // Period
        0x38 => 53, // Slash
        0x39 => 58, // CapsLock

        // Function row. F11 and F12 are not a continuation of F1-F10 — they were added later and
        // sit at 87/88, well away from the rest.
        0x3A => 59, // F1
        0x3B => 60, // F2
        0x3C => 61, // F3
        0x3D => 62, // F4
        0x3E => 63, // F5
        0x3F => 64, // F6
        0x40 => 65, // F7
        0x41 => 66, // F8
        0x42 => 67, // F9
        0x43 => 68, // F10
        0x44 => 87, // F11
        0x45 => 88, // F12

        0x46 => 99,  // PrintScreen (KEY_SYSRQ)
        0x47 => 70,  // ScrollLock
        0x48 => 119, // Pause — unlike Windows, evdev has a single code for it
        0x49 => 110, // Insert
        0x4A => 102, // Home
        0x4B => 104, // PageUp
        0x4C => 111, // Delete
        0x4D => 107, // End
        0x4E => 109, // PageDown
        0x4F => 106, // Right
        0x50 => 105, // Left
        0x51 => 108, // Down
        0x52 => 103, // Up

        0x53 => 69,  // NumLock
        0x54 => 98,  // Keypad /
        0x55 => 55,  // Keypad *
        0x56 => 74,  // Keypad -
        0x57 => 78,  // Keypad +
        0x58 => 96,  // Keypad Enter
        0x59 => 79,  // Keypad 1
        0x5A => 80,  // Keypad 2
        0x5B => 81,  // Keypad 3
        0x5C => 75,  // Keypad 4
        0x5D => 76,  // Keypad 5
        0x5E => 77,  // Keypad 6
        0x5F => 71,  // Keypad 7
        0x60 => 72,  // Keypad 8
        0x61 => 73,  // Keypad 9
        0x62 => 82,  // Keypad 0
        0x63 => 83,  // Keypad .
        0x64 => 86,  // NonUsBackslash (KEY_102ND)
        0x65 => 127, // Application (KEY_COMPOSE, the context-menu key)
        0x67 => 117, // Keypad =

        0x68 => 183, // F13
        0x69 => 184, // F14
        0x6A => 185, // F15
        0x6B => 186, // F16
        0x6C => 187, // F17
        0x6D => 188, // F18
        0x6E => 189, // F19
        0x6F => 190, // F20

        // Modifiers. Unlike Windows' set 1, evdev gives left and right their own codes throughout,
        // so nothing here needs an extended flag.
        0xE0 => 29,  // LeftControl
        0xE1 => 42,  // LeftShift
        0xE2 => 56,  // LeftAlt
        0xE3 => 125, // LeftMeta
        0xE4 => 97,  // RightControl
        0xE5 => 54,  // RightShift
        0xE6 => 100, // RightAlt
        0xE7 => 126, // RightMeta

        _ => return None,
    };

    Some(evdev)
}

/// Posts the remote pointer and keyboard into this host's X session.
///
/// Holds its own X connection rather than sharing the capture one. They are used from the same
/// thread, so sharing would work — but XTEST requests and multi-megabyte `GetImage` replies on one
/// connection means every input event queues behind whatever frame is in flight, which is the one
/// latency a remote session is judged on.
pub struct InputInjector {
    connection: RustConnection,
    root: Window,

    /// Every key currently down, as a HID usage, and every button.
    ///
    /// Tracked only so they can be released when the session ends. X11 needs no modifier state on
    /// each event — the server derives it from the press and release events it has been given — so
    /// unlike macOS there are no flags to attach.
    keys_down: Vec<u32>,
    buttons_down: [bool; 3],
}

impl InputInjector {
    pub fn new() -> Result<Self> {
        let (connection, screen_index) =
            x11rb::connect(None).context("could not connect to this session's X server to post input")?;

        // XTEST has to be present, and unlike XFIXES in the capture path this is not optional: a
        // session where the screen is visible but the mouse does nothing is worse than one that
        // refuses, because there is nothing on screen to explain it.
        let version = connection
            .xtest_get_version(2, 2)
            .context("this X server did not answer an XTEST version query")?
            .reply()
            .context("this X server has no XTEST extension, so remote input is impossible")?;

        logging::info(&format!(
            "remote input available through XTEST {}.{}",
            version.major_version, version.minor_version
        ));

        let root = connection.setup().roots[screen_index].root;

        Ok(Self { connection, root, keys_down: Vec::new(), buttons_down: [false; 3] })
    }

    pub fn apply(&mut self, input: &ViewerInput) {
        let result = match input {
            ViewerInput::Pointer { action, x, y, button } => self.pointer(*action, *x, *y, *button),
            ViewerInput::Scroll { x, y, delta_x, delta_y } => self.scroll(*x, *y, *delta_x, *delta_y),
            ViewerInput::Key { hid, down } => self.key(*hid, *down),
            // The capture side's business, not this one's.
            ViewerInput::Quality { .. } => Ok(()),
        };

        if let Err(err) = result {
            // One event that could not be posted is not a reason to end a session: the X server may
            // have been momentarily busy, and the next event will say so again if it is really gone.
            // The session loop notices a dead connection through its own reads.
            logging::warn(&format!("could not post a remote input event: {err:#}"));
        }
    }

    /// Lets go of everything the remote end was holding.
    ///
    /// Called on every path out of a session, including a dropped socket. A session that ends while
    /// the remote user happens to be holding Alt leaves this host's own user with Alt stuck down,
    /// and nothing on screen explaining why every keystroke has become a menu shortcut.
    pub fn release_all(&mut self) {
        for button in [MouseButton::Left, MouseButton::Right, MouseButton::Middle] {
            if self.buttons_down[button_index(button)] {
                let _ = self.fake_input(BUTTON_RELEASE, x11_button(button), 0, 0);
                self.buttons_down[button_index(button)] = false;
            }
        }

        for hid in std::mem::take(&mut self.keys_down) {
            if let Some(keycode) = xtest_keycode_for_hid(hid) {
                let _ = self.fake_input(KEY_RELEASE, keycode, 0, 0);
            }
        }

        // Flushed explicitly: everything above is queued on the connection, and this is the last
        // thing that happens before it is dropped — without a flush the releases would never reach
        // the server at all.
        let _ = self.connection.flush();
    }

    fn pointer(&mut self, action: PointerAction, x: f64, y: f64, button: MouseButton) -> Result<()> {
        // Clamped to the screen, and rounded rather than truncated: X11 takes integer pixels and
        // truncation would bias every movement up and to the left by up to a pixel.
        let x = x.round().clamp(0.0, f64::from(i16::MAX)) as i16;
        let y = y.round().clamp(0.0, f64::from(i16::MAX)) as i16;

        match action {
            PointerAction::Move => self.fake_input(MOTION_NOTIFY, 0, x, y),
            PointerAction::Down => {
                // Moved and then pressed, as two requests, because XTEST's button events carry no
                // position of their own — unlike Windows' SendInput, where the two can be one event.
                self.fake_input(MOTION_NOTIFY, 0, x, y)?;
                self.buttons_down[button_index(button)] = true;
                self.fake_input(BUTTON_PRESS, x11_button(button), 0, 0)
            }
            PointerAction::Up => {
                self.fake_input(MOTION_NOTIFY, 0, x, y)?;
                self.buttons_down[button_index(button)] = false;
                self.fake_input(BUTTON_RELEASE, x11_button(button), 0, 0)
            }
        }
    }

    fn scroll(&mut self, x: f64, y: f64, delta_x: f64, delta_y: f64) -> Result<()> {
        // Wheel events go wherever the pointer is, so it has to be there first.
        self.pointer(PointerAction::Move, x, y, MouseButton::Left)?;

        // A browser's deltaY grows as the content scrolls down, which is wheel-*down* on X11 — so
        // unlike macOS and Windows there is no sign inversion here, because X11 models the direction
        // as two different buttons rather than as a signed amount.
        let vertical = (delta_y.abs() / PIXELS_PER_CLICK).round() as u32;
        let horizontal = (delta_x.abs() / PIXELS_PER_CLICK).round() as u32;

        let vertical_button = if delta_y > 0.0 { BUTTON_WHEEL_DOWN } else { BUTTON_WHEEL_UP };
        let horizontal_button = if delta_x > 0.0 { BUTTON_WHEEL_RIGHT } else { BUTTON_WHEEL_LEFT };

        for _ in 0..vertical.min(MAX_WHEEL_CLICKS) {
            self.fake_input(BUTTON_PRESS, vertical_button, 0, 0)?;
            self.fake_input(BUTTON_RELEASE, vertical_button, 0, 0)?;
        }

        for _ in 0..horizontal.min(MAX_WHEEL_CLICKS) {
            self.fake_input(BUTTON_PRESS, horizontal_button, 0, 0)?;
            self.fake_input(BUTTON_RELEASE, horizontal_button, 0, 0)?;
        }

        Ok(())
    }

    fn key(&mut self, hid: u32, down: bool) -> Result<()> {
        let Some(keycode) = xtest_keycode_for_hid(hid) else {
            return Ok(());
        };

        if down {
            if !self.keys_down.contains(&hid) {
                self.keys_down.push(hid);
            }
        } else {
            self.keys_down.retain(|held| *held != hid);
        }

        self.fake_input(if down { KEY_PRESS } else { KEY_RELEASE }, keycode, 0, 0)
    }

    /// One XTEST request, flushed immediately.
    ///
    /// Flushed rather than batched because x11rb buffers requests until something forces them out,
    /// and a keystroke sitting in a local buffer waiting for the next frame's `GetImage` is exactly
    /// the latency this feature is judged on. The cost is a write syscall per event, which against
    /// human typing speed is nothing.
    fn fake_input(&self, event_type: u8, detail: u8, x: i16, y: i16) -> Result<()> {
        self.connection
            .xtest_fake_input(
                event_type,
                detail,
                // No delay: the server should deliver it now rather than schedule it.
                0,
                self.root,
                x,
                y,
                // The device id, which is meaningless for the core protocol's XTEST.
                0,
            )
            .context("could not send an XTEST request")?
            .check()
            .context("the X server rejected an XTEST request")?;

        self.connection.flush().context("could not flush an XTEST request")?;
        Ok(())
    }
}

impl Drop for InputInjector {
    fn drop(&mut self) {
        // A backstop for a panic on the session thread unwinding past the explicit call.
        self.release_all();
    }
}

fn button_index(button: MouseButton) -> usize {
    match button {
        MouseButton::Left => 0,
        MouseButton::Right => 1,
        MouseButton::Middle => 2,
    }
}

fn x11_button(button: MouseButton) -> u8 {
    match button {
        MouseButton::Left => BUTTON_LEFT,
        MouseButton::Right => BUTTON_RIGHT,
        MouseButton::Middle => BUTTON_MIDDLE,
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    // The mapping only. Posting an event needs an X server with XTEST, which the test environment
    // does not have — and asserting that a real keystroke arrived would mean typing into whatever
    // had focus on the machine running the suite.

    #[test]
    fn maps_flutters_full_usage_value_and_the_bare_one_alike() {
        // A is evdev 30, so keycode 38.
        assert_eq!(xtest_keycode_for_hid(0x0007_0004), Some(38));
        assert_eq!(xtest_keycode_for_hid(0x04), Some(38));
    }

    #[test]
    fn every_keycode_is_the_evdev_code_plus_eight() {
        // The one arithmetic fact this whole table rests on. Spot-checked against
        // linux/input-event-codes.h at both ends of the range.
        assert_eq!(xtest_keycode_for_hid(0x29), Some(1 + 8)); // Escape, KEY_ESC = 1
        assert_eq!(xtest_keycode_for_hid(0x04), Some(30 + 8)); // A, KEY_A = 30
        assert_eq!(xtest_keycode_for_hid(0x6F), Some(190 + 8)); // F20, KEY_F20 = 190
    }

    #[test]
    fn maps_the_keys_a_remote_session_is_useless_without() {
        assert_eq!(xtest_keycode_for_hid(0x28), Some(28 + 8)); // Enter
        assert_eq!(xtest_keycode_for_hid(0x2A), Some(14 + 8)); // Backspace
        assert_eq!(xtest_keycode_for_hid(0x2B), Some(15 + 8)); // Tab
        assert_eq!(xtest_keycode_for_hid(0x2C), Some(57 + 8)); // Space
        assert_eq!(xtest_keycode_for_hid(0x4F), Some(106 + 8)); // Right arrow
        assert_eq!(xtest_keycode_for_hid(0x50), Some(105 + 8)); // Left arrow
    }

    #[test]
    fn left_and_right_modifiers_have_their_own_codes_unlike_windows() {
        // evdev distinguishes them natively, so nothing here needs Windows' extended-key flag.
        assert_eq!(xtest_keycode_for_hid(0xE0), Some(29 + 8)); // LeftControl
        assert_eq!(xtest_keycode_for_hid(0xE4), Some(97 + 8)); // RightControl
        assert_eq!(xtest_keycode_for_hid(0xE1), Some(42 + 8)); // LeftShift
        assert_eq!(xtest_keycode_for_hid(0xE5), Some(54 + 8)); // RightShift
        assert_ne!(xtest_keycode_for_hid(0xE2), xtest_keycode_for_hid(0xE6)); // Left/Right Alt
    }

    #[test]
    fn the_two_enters_are_told_apart_without_a_flag() {
        // On Windows both are make code 0x1C and only the extended bit separates them; evdev gives
        // the keypad its own code.
        assert_eq!(xtest_keycode_for_hid(0x28), Some(28 + 8)); // Enter
        assert_eq!(xtest_keycode_for_hid(0x58), Some(96 + 8)); // Keypad Enter
    }

    #[test]
    fn the_arrows_and_the_keypad_digits_do_not_collide() {
        // The collision that forces the extended flag on Windows simply does not exist here.
        assert_ne!(xtest_keycode_for_hid(0x50), xtest_keycode_for_hid(0x5C)); // Left vs Keypad 4
        assert_ne!(xtest_keycode_for_hid(0x52), xtest_keycode_for_hid(0x60)); // Up vs Keypad 8
    }

    #[test]
    fn pause_is_mappable_here_unlike_windows() {
        // Windows has to refuse it, because a real Pause is a three-code sequence in set 1. evdev
        // has a single code, so it just works.
        assert_eq!(xtest_keycode_for_hid(0x48), Some(119 + 8));
    }

    #[test]
    fn ignores_usage_pages_with_no_positional_key() {
        assert_eq!(xtest_keycode_for_hid(0x000C_00E9), None);
        assert_eq!(xtest_keycode_for_hid(0xFE), None);
    }

    #[test]
    fn every_mapped_usage_stays_inside_a_keycode() {
        // A keycode is a u8 in the protocol, and F20 is already at 198 — so an addition above 247
        // would wrap into an unrelated key rather than failing. `checked_add` is what prevents that
        // and this is what proves the current table is inside it.
        for usage in 0x04u32..=0xE7 {
            if let Some(keycode) = xtest_keycode_for_hid(usage) {
                assert!(keycode >= EVDEV_KEYCODE_OFFSET, "usage {usage:#x} mapped below the offset");
            }
        }
    }

    #[test]
    fn the_portal_form_is_the_xtest_form_less_the_offset_for_every_key() {
        // The two are one table and one addition, and this is what keeps them that way. If they ever
        // become separate tables, a key that disagrees by eight types a letter eight positions along
        // the physical keyboard — a wrong letter on Wayland hosts only, which reads as a keymap
        // problem on the host rather than a bug here.
        for usage in 0x00u32..=0xFF {
            match (evdev_keycode_for_hid(usage), xtest_keycode_for_hid(usage)) {
                (Some(evdev), Some(xtest)) => assert_eq!(
                    u32::from(evdev) + u32::from(EVDEV_KEYCODE_OFFSET),
                    u32::from(xtest),
                    "usage {usage:#x}"
                ),
                (None, None) => {}
                (evdev, xtest) => {
                    panic!("usage {usage:#x} mapped to {evdev:?} for the portal and {xtest:?} for XTEST")
                }
            }
        }
    }

    #[test]
    fn the_portal_gets_the_kernel_code_that_input_event_codes_h_names() {
        // Spot-checked against linux/input-event-codes.h rather than derived, because the whole
        // point of the un-offset form is that it can be read straight out of that header: KEY_A is
        // 30, KEY_ENTER is 28. XTEST's 38 and 36 are the same keys plus eight.
        assert_eq!(evdev_keycode_for_hid(0x0007_0004), Some(30));
        assert_eq!(evdev_keycode_for_hid(0x0007_0028), Some(28));
    }

    #[test]
    fn wheel_directions_use_the_four_button_numbers_x11_reserves_for_them() {
        // X11 has no scroll axis: direction is the button number, which is why the scroll path here
        // inverts nothing while macOS and Windows both do.
        assert_eq!((BUTTON_WHEEL_UP, BUTTON_WHEEL_DOWN), (4, 5));
        assert_eq!((BUTTON_WHEEL_LEFT, BUTTON_WHEEL_RIGHT), (6, 7));
    }
}
