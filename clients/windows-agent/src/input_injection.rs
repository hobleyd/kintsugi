//! Posting the remote keyboard and mouse into this host's own input stream.
//!
//! # Scan codes, not virtual keys
//!
//! Every key goes in as a **set 1 scan code** with `KEYEVENTF_SCANCODE`, never as a virtual-key
//! code. A scan code names a position on the keyboard and Windows applies the host's own layout to
//! it, which is the only thing that can be correct here: the viewer sends the physical key the
//! administrator pressed (Flutter's `PhysicalKeyboardKey.usbHidUsage`), and an administrator on a US
//! keyboard controlling a host set to a French layout must produce what *that host's* user would get
//! by pressing there. Send a virtual key instead and you have silently chosen the administrator's
//! layout for somebody else's machine.
//!
//! This is the same reasoning as the macOS agent's `virtual_key_for_hid` — there a virtual keycode
//! *is* the positional form, so the two agents pick opposite-sounding APIs to do the same thing.
//!
//! # Two things this cannot do, and neither is fixable from here
//!
//! **User Interface Privilege Isolation.** The tray process runs as the logged-in user at medium
//! integrity, and `SendInput` cannot post into a window at a higher integrity level. So a remote
//! session can see an elevated application (capture is not restricted the same way) and cannot type
//! or click into it. In practice: an administrator can drive Explorer and a browser but not an
//! installer that has already elevated.
//!
//! **The UAC secure desktop.** When a consent prompt appears, Windows switches to a separate desktop
//! that no ordinary process can capture or inject into at all. The remote screen freezes on the last
//! frame until the prompt is answered *at the machine*.
//!
//! # No click-state to set, unlike macOS
//!
//! Nothing here tracks double-clicks. Windows derives them itself from the timing of the button
//! events it is given, so posting two clicks close together *is* a double click. The macOS agent has
//! to set `kCGMouseEventClickState` explicitly because CoreGraphics infers nothing — which is why
//! that injector carries a click timer and this one does not.
//!
//! Both would need an elevated helper running inside the user's session — something `install.ps1`
//! could register as a scheduled task with highest privileges. That is a real option and it is
//! deliberately not taken here: it would put a permanently elevated, input-injecting process in
//! every logged-in session, which is a much larger thing to justify than the feature it would
//! complete. Recorded in the decision log rather than half-built.

/// Maps a USB HID keyboard usage code to a set 1 scan code, and whether it is an extended key.
///
/// `hid` is accepted as the full 32-bit value Flutter reports (`0x0007_0004` for A) as well as the
/// bare usage (`0x04`); anything outside the keyboard usage page returns `None` and is ignored.
///
/// The extended flag is not decoration. Set 1 reuses the same make code for a pair of keys — right
/// Control and left Control are both `0x1D`, the numeric-keypad Enter and the main Enter are both
/// `0x1C`, and the arrow keys share codes with the keypad digits. `KEYEVENTF_EXTENDEDKEY` is the
/// only thing that tells them apart, so dropping it turns every arrow key into a number.
pub fn scan_code_for_hid(hid: u32) -> Option<(u16, bool)> {
    // Flutter encodes the usage page in the high 16 bits. Page 0x0007 is keyboard/keypad; page
    // 0x000C is consumer control (volume, brightness), which has no scan code at all.
    let usage = match hid >> 16 {
        0x0000 => hid,
        0x0007 => hid & 0xFFFF,
        _ => return None,
    };

    let plain = |code: u16| Some((code, false));
    let extended = |code: u16| Some((code, true));

    match usage {
        // Letters, in HID order. The set 1 codes follow the physical rows, so they are in no
        // alphabetical order whatsoever and a formula would be wrong.
        0x04 => plain(0x1E), // A
        0x05 => plain(0x30), // B
        0x06 => plain(0x2E), // C
        0x07 => plain(0x20), // D
        0x08 => plain(0x12), // E
        0x09 => plain(0x21), // F
        0x0A => plain(0x22), // G
        0x0B => plain(0x23), // H
        0x0C => plain(0x17), // I
        0x0D => plain(0x24), // J
        0x0E => plain(0x25), // K
        0x0F => plain(0x26), // L
        0x10 => plain(0x32), // M
        0x11 => plain(0x31), // N
        0x12 => plain(0x18), // O
        0x13 => plain(0x19), // P
        0x14 => plain(0x10), // Q
        0x15 => plain(0x13), // R
        0x16 => plain(0x1F), // S
        0x17 => plain(0x14), // T
        0x18 => plain(0x16), // U
        0x19 => plain(0x2F), // V
        0x1A => plain(0x11), // W
        0x1B => plain(0x2D), // X
        0x1C => plain(0x15), // Y
        0x1D => plain(0x2C), // Z

        // Digit row.
        0x1E => plain(0x02), // 1
        0x1F => plain(0x03), // 2
        0x20 => plain(0x04), // 3
        0x21 => plain(0x05), // 4
        0x22 => plain(0x06), // 5
        0x23 => plain(0x07), // 6
        0x24 => plain(0x08), // 7
        0x25 => plain(0x09), // 8
        0x26 => plain(0x0A), // 9
        0x27 => plain(0x0B), // 0

        0x28 => plain(0x1C), // Enter
        0x29 => plain(0x01), // Escape
        0x2A => plain(0x0E), // Backspace
        0x2B => plain(0x0F), // Tab
        0x2C => plain(0x39), // Space
        0x2D => plain(0x0C), // Minus
        0x2E => plain(0x0D), // Equal
        0x2F => plain(0x1A), // LeftBracket
        0x30 => plain(0x1B), // RightBracket
        0x31 => plain(0x2B), // Backslash
        0x32 => plain(0x2B), // NonUsHash — the same physical key on an ANSI board
        0x33 => plain(0x27), // Semicolon
        0x34 => plain(0x28), // Quote
        0x35 => plain(0x29), // Grave
        0x36 => plain(0x33), // Comma
        0x37 => plain(0x34), // Period
        0x38 => plain(0x35), // Slash
        0x39 => plain(0x3A), // CapsLock

        // Function row. F11 and F12 are not a continuation of F1-F10 — they were added later and
        // sit at 0x57/0x58, well away from the rest.
        0x3A => plain(0x3B), // F1
        0x3B => plain(0x3C), // F2
        0x3C => plain(0x3D), // F3
        0x3D => plain(0x3E), // F4
        0x3E => plain(0x3F), // F5
        0x3F => plain(0x40), // F6
        0x40 => plain(0x41), // F7
        0x41 => plain(0x42), // F8
        0x42 => plain(0x43), // F9
        0x43 => plain(0x44), // F10
        0x44 => plain(0x57), // F11
        0x45 => plain(0x58), // F12

        0x46 => extended(0x37), // PrintScreen
        0x47 => plain(0x46),    // ScrollLock
        // Pause is deliberately absent. It is the one key with no single scan code: a real Pause is
        // the sequence 0xE1 0x1D 0x45, and faking it with 0x45 alone is indistinguishable from
        // NumLock, which is directly below. Nothing in a support session needs it.
        0x48 => None,

        0x49 => extended(0x52), // Insert
        0x4A => extended(0x47), // Home
        0x4B => extended(0x49), // PageUp
        0x4C => extended(0x53), // Delete
        0x4D => extended(0x4F), // End
        0x4E => extended(0x51), // PageDown
        0x4F => extended(0x4D), // Right
        0x50 => extended(0x4B), // Left
        0x51 => extended(0x50), // Down
        0x52 => extended(0x48), // Up

        0x53 => plain(0x45),    // NumLock
        0x54 => extended(0x35), // Keypad /
        0x55 => plain(0x37),    // Keypad *
        0x56 => plain(0x4A),    // Keypad -
        0x57 => plain(0x4E),    // Keypad +
        0x58 => extended(0x1C), // Keypad Enter
        0x59 => plain(0x4F),    // Keypad 1
        0x5A => plain(0x50),    // Keypad 2
        0x5B => plain(0x51),    // Keypad 3
        0x5C => plain(0x4B),    // Keypad 4
        0x5D => plain(0x4C),    // Keypad 5
        0x5E => plain(0x4D),    // Keypad 6
        0x5F => plain(0x47),    // Keypad 7
        0x60 => plain(0x48),    // Keypad 8
        0x61 => plain(0x49),    // Keypad 9
        0x62 => plain(0x52),    // Keypad 0
        0x63 => plain(0x53),    // Keypad .
        0x64 => plain(0x56),    // NonUsBackslash
        0x65 => extended(0x5D), // Application (the context-menu key)
        0x67 => plain(0x59),    // Keypad =

        0x68 => plain(0x64), // F13
        0x69 => plain(0x65), // F14
        0x6A => plain(0x66), // F15
        0x6B => plain(0x67), // F16
        0x6C => plain(0x68), // F17
        0x6D => plain(0x69), // F18
        0x6E => plain(0x6A), // F19
        0x6F => plain(0x6B), // F20

        // Modifiers. Left and right share make codes for Control and Alt, so the extended flag is
        // the only thing distinguishing them.
        0xE0 => plain(0x1D),    // LeftControl
        0xE1 => plain(0x2A),    // LeftShift
        0xE2 => plain(0x38),    // LeftAlt
        0xE3 => extended(0x5B), // LeftGUI (Windows key)
        0xE4 => extended(0x1D), // RightControl
        0xE5 => plain(0x36),    // RightShift
        0xE6 => extended(0x38), // RightAlt (AltGr)
        0xE7 => extended(0x5C), // RightGUI

        _ => None,
    }
}

#[cfg(windows)]
pub use platform::InputInjector;

#[cfg(windows)]
mod platform {
    use windows_sys::Win32::UI::Input::KeyboardAndMouse::{
        SendInput, INPUT, INPUT_0, INPUT_KEYBOARD, INPUT_MOUSE, KEYBDINPUT, KEYEVENTF_EXTENDEDKEY,
        KEYEVENTF_KEYUP, KEYEVENTF_SCANCODE, MOUSEEVENTF_ABSOLUTE, MOUSEEVENTF_HWHEEL,
        MOUSEEVENTF_LEFTDOWN, MOUSEEVENTF_LEFTUP, MOUSEEVENTF_MIDDLEDOWN, MOUSEEVENTF_MIDDLEUP,
        MOUSEEVENTF_MOVE, MOUSEEVENTF_RIGHTDOWN, MOUSEEVENTF_RIGHTUP, MOUSEEVENTF_WHEEL, MOUSEINPUT,
    };

    use crate::logging;
    use crate::remote_protocol::{MouseButton, PointerAction, ViewerInput};

    use super::scan_code_for_hid;

    /// One notch of a mouse wheel, as Windows defines it.
    const WHEEL_DELTA: f64 = 120.0;

    /// How many pixels of browser scroll make one notch. A browser reports wheel deltas in pixels
    /// and a trackpad reports small ones continuously; treating every pixel as a notch would make a
    /// gentle two-finger scroll fly down a page.
    const PIXELS_PER_NOTCH: f64 = 100.0;

    /// `MOUSEEVENTF_ABSOLUTE` coordinates are normalised over the primary monitor into this range,
    /// inclusive, rather than being pixels.
    const ABSOLUTE_RANGE: f64 = 65535.0;

    /// Posts the remote pointer and keyboard into this host.
    ///
    /// Owned by the one thread running a session, like the macOS injector — but for a different
    /// reason. There is no handle to keep here; `SendInput` is stateless. What must not be shared is
    /// the record of what is currently held down, because that is what lets go at the end.
    pub struct InputInjector {
        screen_width: f64,
        screen_height: f64,

        /// Every key currently down, as a HID usage.
        ///
        /// **All keys, not just modifiers**, which is where this differs from macOS. There,
        /// `CGEventPost` takes the modifier flags on every event so the injector has to know them;
        /// here Windows tracks modifier state itself from the scan codes posted, so the only reason
        /// to remember anything is to release it when the session ends.
        keys_down: Vec<u32>,
        buttons_down: [bool; 3],
    }

    impl InputInjector {
        pub fn new(screen_width: f64, screen_height: f64) -> Self {
            Self {
                screen_width: screen_width.max(1.0),
                screen_height: screen_height.max(1.0),
                keys_down: Vec::new(),
                buttons_down: [false; 3],
            }
        }

        pub fn apply(&mut self, input: &ViewerInput) {
            match input {
                ViewerInput::Pointer { action, x, y, button } => self.pointer(*action, *x, *y, *button),
                ViewerInput::Scroll { x, y, delta_x, delta_y } => self.scroll(*x, *y, *delta_x, *delta_y),
                ViewerInput::Key { hid, down } => self.key(*hid, *down),
                // The capture side's business, not this one's.
                ViewerInput::Quality { .. } => {}
            }
        }

        /// Lets go of everything the remote end was holding.
        ///
        /// Called on every path out of a session, including a dropped pipe. A session that ends while
        /// the remote user happens to be holding Alt leaves this host's own user with Alt stuck
        /// down, and nothing on screen explaining why every keystroke has become a menu shortcut.
        pub fn release_all(&mut self) {
            for button in [MouseButton::Left, MouseButton::Right, MouseButton::Middle] {
                if self.buttons_down[button_index(button)] {
                    self.send_mouse(mouse_up_flag(button), 0, None);
                    self.buttons_down[button_index(button)] = false;
                }
            }

            for hid in std::mem::take(&mut self.keys_down) {
                if let Some((scan_code, extended)) = scan_code_for_hid(hid) {
                    self.send_key(scan_code, extended, false);
                }
            }
        }

        fn pointer(&mut self, action: PointerAction, x: f64, y: f64, button: MouseButton) {
            // Normalised over the primary monitor, which is also what was captured — see
            // DisplayGeometry on why the two coordinate spaces are the same number on Windows.
            //
            // The divisor is width - 1, not width: the range is inclusive at both ends, so a click
            // on the rightmost pixel has to reach 65535 exactly or the far edge of the screen is
            // unreachable.
            let normalised_x = ((x / (self.screen_width - 1.0).max(1.0)) * ABSOLUTE_RANGE)
                .round()
                .clamp(0.0, ABSOLUTE_RANGE) as i32;
            let normalised_y = ((y / (self.screen_height - 1.0).max(1.0)) * ABSOLUTE_RANGE)
                .round()
                .clamp(0.0, ABSOLUTE_RANGE) as i32;
            let position = Some((normalised_x, normalised_y));

            match action {
                PointerAction::Move => self.send_mouse(MOUSEEVENTF_MOVE, 0, position),
                PointerAction::Down => {
                    self.buttons_down[button_index(button)] = true;
                    // Move and press in one event rather than two: a separate move followed by a
                    // press can be delivered with another application's own mouse movement in
                    // between, which lands the click somewhere else.
                    self.send_mouse(MOUSEEVENTF_MOVE | mouse_down_flag(button), 0, position);
                }
                PointerAction::Up => {
                    self.buttons_down[button_index(button)] = false;
                    self.send_mouse(MOUSEEVENTF_MOVE | mouse_up_flag(button), 0, position);
                }
            }
        }

        fn scroll(&mut self, x: f64, y: f64, delta_x: f64, delta_y: f64) {
            // Wheel events go wherever the pointer is, so it has to be there first.
            self.pointer(PointerAction::Move, x, y, MouseButton::Left);

            // Vertical is negated and horizontal is not, which looks inconsistent and is not. A
            // browser's deltaY grows as the content scrolls *down*; Windows' wheel value grows as
            // the wheel rotates *away* from the user, which scrolls content up. Horizontal agrees
            // between the two: positive is rightwards for both.
            let vertical = -(delta_y / PIXELS_PER_NOTCH * WHEEL_DELTA).round();
            let horizontal = (delta_x / PIXELS_PER_NOTCH * WHEEL_DELTA).round();

            if vertical != 0.0 {
                self.send_mouse(MOUSEEVENTF_WHEEL, vertical as i32, None);
            }
            if horizontal != 0.0 {
                self.send_mouse(MOUSEEVENTF_HWHEEL, horizontal as i32, None);
            }
        }

        fn key(&mut self, hid: u32, down: bool) {
            let Some((scan_code, extended)) = scan_code_for_hid(hid) else {
                return;
            };

            if down {
                if !self.keys_down.contains(&hid) {
                    self.keys_down.push(hid);
                }
            } else {
                self.keys_down.retain(|held| *held != hid);
            }

            self.send_key(scan_code, extended, down);
        }

        fn send_key(&self, scan_code: u16, extended: bool, down: bool) {
            let mut flags = KEYEVENTF_SCANCODE;
            if extended {
                flags |= KEYEVENTF_EXTENDEDKEY;
            }
            if !down {
                flags |= KEYEVENTF_KEYUP;
            }

            let input = INPUT {
                r#type: INPUT_KEYBOARD,
                Anonymous: INPUT_0 {
                    ki: KEYBDINPUT {
                        // Zero, and that is the point: with KEYEVENTF_SCANCODE the virtual key is
                        // ignored and Windows derives it from the scan code through the *host's*
                        // keyboard layout. Filling it in would reintroduce the layout assumption
                        // this whole approach exists to avoid.
                        wVk: 0,
                        wScan: scan_code,
                        dwFlags: flags,
                        time: 0,
                        dwExtraInfo: 0,
                    },
                },
            };

            self.send(&[input]);
        }

        fn send_mouse(&self, flags: u32, wheel_delta: i32, position: Option<(i32, i32)>) {
            let (dx, dy, flags) = match position {
                Some((x, y)) => (x, y, flags | MOUSEEVENTF_ABSOLUTE),
                None => (0, 0, flags),
            };

            let input = INPUT {
                r#type: INPUT_MOUSE,
                Anonymous: INPUT_0 {
                    mi: MOUSEINPUT {
                        dx,
                        dy,
                        mouseData: wheel_delta as u32,
                        dwFlags: flags,
                        time: 0,
                        dwExtraInfo: 0,
                    },
                },
            };

            self.send(&[input]);
        }

        fn send(&self, inputs: &[INPUT]) {
            // SAFETY: `inputs` is a valid slice of correctly-sized INPUT structures, and the size
            // argument is taken from the type rather than assumed.
            let sent = unsafe {
                SendInput(inputs.len() as u32, inputs.as_ptr(), std::mem::size_of::<INPUT>() as i32)
            };

            if sent as usize != inputs.len() {
                // Blocked rather than broken, almost always: UIPI refuses an injection aimed at a
                // window running at higher integrity than this process. Logged once per event would
                // be a flood, so this is at warn and the module note explains the cause.
                logging::warn("Windows refused a remote control input event (see input_injection on UIPI)");
            }
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

    fn mouse_down_flag(button: MouseButton) -> u32 {
        match button {
            MouseButton::Left => MOUSEEVENTF_LEFTDOWN,
            MouseButton::Right => MOUSEEVENTF_RIGHTDOWN,
            MouseButton::Middle => MOUSEEVENTF_MIDDLEDOWN,
        }
    }

    fn mouse_up_flag(button: MouseButton) -> u32 {
        match button {
            MouseButton::Left => MOUSEEVENTF_LEFTUP,
            MouseButton::Right => MOUSEEVENTF_RIGHTUP,
            MouseButton::Middle => MOUSEEVENTF_MIDDLEUP,
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    // The mapping only. Posting an event is not testable without a desktop, and asserting that a
    // real keystroke arrived would mean typing into whatever had focus on the machine running the
    // suite.

    #[test]
    fn maps_flutters_full_usage_value_and_the_bare_one_alike() {
        assert_eq!(scan_code_for_hid(0x0007_0004), Some((0x1E, false)));
        assert_eq!(scan_code_for_hid(0x04), Some((0x1E, false)));
    }

    #[test]
    fn maps_the_letters_that_follow_the_rows_rather_than_the_alphabet() {
        assert_eq!(scan_code_for_hid(0x04), Some((0x1E, false))); // A
        assert_eq!(scan_code_for_hid(0x14), Some((0x10, false))); // Q, first on the top row
        assert_eq!(scan_code_for_hid(0x1D), Some((0x2C, false))); // Z, first on the bottom row
    }

    #[test]
    fn maps_the_keys_a_remote_session_is_useless_without() {
        assert_eq!(scan_code_for_hid(0x28), Some((0x1C, false))); // Enter
        assert_eq!(scan_code_for_hid(0x2A), Some((0x0E, false))); // Backspace
        assert_eq!(scan_code_for_hid(0x2B), Some((0x0F, false))); // Tab
        assert_eq!(scan_code_for_hid(0x2C), Some((0x39, false))); // Space
        assert_eq!(scan_code_for_hid(0x29), Some((0x01, false))); // Escape
    }

    #[test]
    fn the_arrow_keys_are_extended_or_they_are_the_keypad_digits() {
        // The single most consequential extended flag: 0x4B is both Left and keypad 4.
        assert_eq!(scan_code_for_hid(0x50), Some((0x4B, true))); // Left
        assert_eq!(scan_code_for_hid(0x5C), Some((0x4B, false))); // Keypad 4
        assert_eq!(scan_code_for_hid(0x52), Some((0x48, true))); // Up
        assert_eq!(scan_code_for_hid(0x60), Some((0x48, false))); // Keypad 8
    }

    #[test]
    fn the_navigation_cluster_is_extended_too() {
        assert_eq!(scan_code_for_hid(0x4C), Some((0x53, true))); // Delete
        assert_eq!(scan_code_for_hid(0x63), Some((0x53, false))); // Keypad .
        assert_eq!(scan_code_for_hid(0x4A), Some((0x47, true))); // Home
        assert_eq!(scan_code_for_hid(0x5F), Some((0x47, false))); // Keypad 7
    }

    #[test]
    fn left_and_right_modifiers_share_a_code_and_differ_by_the_extended_flag() {
        assert_eq!(scan_code_for_hid(0xE0), Some((0x1D, false))); // LeftControl
        assert_eq!(scan_code_for_hid(0xE4), Some((0x1D, true))); // RightControl
        assert_eq!(scan_code_for_hid(0xE2), Some((0x38, false))); // LeftAlt
        assert_eq!(scan_code_for_hid(0xE6), Some((0x38, true))); // RightAlt
    }

    #[test]
    fn shift_does_not_share_a_code_between_the_two_sides() {
        // Unlike Control and Alt — worth pinning so a later tidy-up does not "fix" the asymmetry.
        assert_eq!(scan_code_for_hid(0xE1), Some((0x2A, false))); // LeftShift
        assert_eq!(scan_code_for_hid(0xE5), Some((0x36, false))); // RightShift
    }

    #[test]
    fn both_windows_keys_are_extended() {
        assert_eq!(scan_code_for_hid(0xE3), Some((0x5B, true)));
        assert_eq!(scan_code_for_hid(0xE7), Some((0x5C, true)));
    }

    #[test]
    fn the_two_enters_are_told_apart() {
        assert_eq!(scan_code_for_hid(0x28), Some((0x1C, false))); // Enter
        assert_eq!(scan_code_for_hid(0x58), Some((0x1C, true))); // Keypad Enter
    }

    #[test]
    fn f11_and_f12_are_not_a_continuation_of_the_function_row() {
        assert_eq!(scan_code_for_hid(0x43), Some((0x44, false))); // F10
        assert_eq!(scan_code_for_hid(0x44), Some((0x57, false))); // F11
        assert_eq!(scan_code_for_hid(0x45), Some((0x58, false))); // F12
    }

    #[test]
    fn pause_is_refused_rather_than_faked_as_numlock() {
        // A real Pause is a three-code sequence; 0x45 alone is NumLock, which is a different key
        // with a visible side effect.
        assert_eq!(scan_code_for_hid(0x48), None);
        assert_eq!(scan_code_for_hid(0x53), Some((0x45, false))); // NumLock
    }

    #[test]
    fn ignores_usage_pages_with_no_scan_code() {
        // Consumer control (volume, brightness) is page 0x000C.
        assert_eq!(scan_code_for_hid(0x000C_00E9), None);
        assert_eq!(scan_code_for_hid(0xFE), None);
    }

    #[test]
    fn every_mapped_usage_has_a_plausible_set_one_code() {
        // A blanket check against a typo putting a zero or an out-of-range value in the table: set 1
        // make codes are one byte and 0x00 is not one of them.
        for usage in 0x04u32..=0xE7 {
            if let Some((code, _)) = scan_code_for_hid(usage) {
                assert!(code > 0 && code <= 0xFF, "usage {usage:#x} mapped to {code:#x}");
            }
        }
    }
}
