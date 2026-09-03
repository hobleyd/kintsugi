//! Posting the remote keyboard and mouse into this Mac's own event stream.
//!
//! **This needs the Accessibility permission** (`kTCCServiceAccessibility`) and there is no way
//! around it: `CGEventPost` from a process without it is silently dropped — no error, no return
//! code, the events simply never arrive. That failure mode is why [`has_accessibility_permission`]
//! is checked before a session is offered rather than discovered once somebody is already watching
//! a screen their mouse cannot move.
//!
//! Unlike Screen Recording, Accessibility **cannot be granted from the prompt**: macOS only offers
//! to open System Settings, and a human then has to find this binary in the list. So on a fleet
//! without an MDM PPPC profile pre-granting it, remote control needs a visit to every Mac. See
//! `packaging/kintsugi-remote-control.mobileconfig`.
//!
//! CoreGraphics is bound by hand below rather than through the `core-graphics` crate. That crate
//! brings its own `core-foundation`, and this binary already cannot tolerate two copies of a
//! dependency in its tree — see the objc2/winit note in Cargo.toml, where exactly that linked two
//! `libobjc.A.dylib`s and dyld refused to load the result. Seven functions and a handful of
//! constants is the smaller risk.

use crate::remote_protocol::{MouseButton, PointerAction, ViewerInput};

// =================================================================================================
// The CoreGraphics surface this module needs, and nothing more.
// =================================================================================================

#[repr(C)]
#[derive(Debug, Clone, Copy, PartialEq)]
pub struct CGPoint {
    pub x: f64,
    pub y: f64,
}

type CGEventRef = *mut std::ffi::c_void;
type CGEventSourceRef = *mut std::ffi::c_void;

// CGEventType. Only the ones posted here.
const K_CG_EVENT_LEFT_MOUSE_DOWN: u32 = 1;
const K_CG_EVENT_LEFT_MOUSE_UP: u32 = 2;
const K_CG_EVENT_RIGHT_MOUSE_DOWN: u32 = 3;
const K_CG_EVENT_RIGHT_MOUSE_UP: u32 = 4;
const K_CG_EVENT_MOUSE_MOVED: u32 = 5;
const K_CG_EVENT_LEFT_MOUSE_DRAGGED: u32 = 6;
const K_CG_EVENT_RIGHT_MOUSE_DRAGGED: u32 = 7;
const K_CG_EVENT_OTHER_MOUSE_DOWN: u32 = 25;
const K_CG_EVENT_OTHER_MOUSE_UP: u32 = 26;
const K_CG_EVENT_OTHER_MOUSE_DRAGGED: u32 = 27;

// CGMouseButton.
const K_CG_MOUSE_BUTTON_LEFT: u32 = 0;
const K_CG_MOUSE_BUTTON_RIGHT: u32 = 1;
const K_CG_MOUSE_BUTTON_CENTER: u32 = 2;

/// `kCGHIDEventTap`: posted as low in the stack as possible, so the events reach whatever has focus
/// rather than only the session that posted them.
const K_CG_HID_EVENT_TAP: u32 = 0;

/// `kCGEventSourceStateHIDSystemState` — the source a real keyboard's events come from, which is
/// what makes key repeat and modifier tracking behave as they would locally.
const K_CG_EVENT_SOURCE_STATE_HID_SYSTEM_STATE: i32 = 1;

/// `kCGScrollEventUnitPixel`. Pixels rather than lines because a browser's wheel deltas are already
/// pixel-ish, and line units turn a gentle trackpad scroll into a jump of several lines.
const K_CG_SCROLL_EVENT_UNIT_PIXEL: u32 = 0;

/// `kCGMouseEventClickState` — 1 for a single click, 2 for a double. Set explicitly because a
/// synthesised click with no click state never registers as a double-click however fast the two
/// arrive, which makes it impossible to open anything in Finder.
const K_CG_MOUSE_EVENT_CLICK_STATE: u32 = 23;

// CGEventFlags.
const K_CG_EVENT_FLAG_MASK_SHIFT: u64 = 0x0002_0000;
const K_CG_EVENT_FLAG_MASK_CONTROL: u64 = 0x0004_0000;
const K_CG_EVENT_FLAG_MASK_ALTERNATE: u64 = 0x0008_0000;
const K_CG_EVENT_FLAG_MASK_COMMAND: u64 = 0x0010_0000;
const K_CG_EVENT_FLAG_MASK_SECONDARY_FN: u64 = 0x0080_0000;

#[link(name = "CoreGraphics", kind = "framework")]
extern "C" {
    fn CGEventSourceCreate(state_id: i32) -> CGEventSourceRef;
    fn CGEventCreateMouseEvent(
        source: CGEventSourceRef,
        mouse_type: u32,
        mouse_cursor_position: CGPoint,
        mouse_button: u32,
    ) -> CGEventRef;
    fn CGEventCreateKeyboardEvent(source: CGEventSourceRef, virtual_key: u16, key_down: bool) -> CGEventRef;
    fn CGEventCreateScrollWheelEvent(
        source: CGEventSourceRef,
        units: u32,
        wheel_count: u32,
        wheel1: i32,
        wheel2: i32,
    ) -> CGEventRef;
    fn CGEventPost(tap: u32, event: CGEventRef);
    fn CGEventSetFlags(event: CGEventRef, flags: u64);
    fn CGEventSetIntegerValueField(event: CGEventRef, field: u32, value: i64);
}

#[link(name = "CoreFoundation", kind = "framework")]
extern "C" {
    fn CFRelease(cf: *mut std::ffi::c_void);
}

#[link(name = "ApplicationServices", kind = "framework")]
extern "C" {
    fn AXIsProcessTrusted() -> bool;
}

/// Whether this process may post events at all.
///
/// `AXIsProcessTrusted` rather than `AXIsProcessTrustedWithOptions` with a prompt: the prompt cannot
/// actually grant anything (it only offers to open System Settings), and calling it from a
/// background LaunchAgent would put a dialog on screen at an arbitrary moment with no context. The
/// honest thing is to check, report, and let the consent dialog explain why control is unavailable.
pub fn has_accessibility_permission() -> bool {
    // SAFETY: takes no arguments, returns a Boolean, and has no preconditions.
    unsafe { AXIsProcessTrusted() }
}

// =================================================================================================
// USB HID usage -> macOS virtual keycode.
// =================================================================================================

/// Maps a USB HID keyboard usage code to the macOS virtual keycode for the same *physical* key.
///
/// The viewer sends physical keys (Flutter's `PhysicalKeyboardKey.usbHidUsage`) rather than
/// characters, and that is the only mapping that can be correct: a virtual keycode names a position
/// on the keyboard, and macOS applies the host's own layout to it. Send the *character* instead and
/// an administrator on a US keyboard controlling a French host produces the wrong letters — while
/// sending the position produces whatever that host's user would get by pressing there, which is
/// what remote control means.
///
/// `hid` is accepted as the full 32-bit value Flutter reports (`0x0007_0004` for A) as well as the
/// bare usage (`0x04`); anything outside the keyboard usage page returns `None` and is ignored.
pub fn virtual_key_for_hid(hid: u32) -> Option<u16> {
    // Flutter encodes the usage page in the high 16 bits. Page 0x0007 is the keyboard/keypad page;
    // page 0x000C is consumer control (volume, brightness) which has no virtual keycode at all.
    let usage = match hid >> 16 {
        0x0000 => hid,
        0x0007 => hid & 0xFFFF,
        _ => return None,
    };

    Some(match usage {
        // Letters. Deliberately spelled out rather than computed: the macOS values are in no
        // arithmetic order at all (A is 0x00, B is 0x0B, C is 0x08), so a clever formula would just
        // be wrong.
        0x04 => 0x00, // A
        0x05 => 0x0B, // B
        0x06 => 0x08, // C
        0x07 => 0x02, // D
        0x08 => 0x0E, // E
        0x09 => 0x03, // F
        0x0A => 0x05, // G
        0x0B => 0x04, // H
        0x0C => 0x22, // I
        0x0D => 0x26, // J
        0x0E => 0x28, // K
        0x0F => 0x25, // L
        0x10 => 0x2E, // M
        0x11 => 0x2D, // N
        0x12 => 0x1F, // O
        0x13 => 0x23, // P
        0x14 => 0x0C, // Q
        0x15 => 0x0F, // R
        0x16 => 0x01, // S
        0x17 => 0x11, // T
        0x18 => 0x20, // U
        0x19 => 0x09, // V
        0x1A => 0x0D, // W
        0x1B => 0x07, // X
        0x1C => 0x10, // Y
        0x1D => 0x06, // Z

        // Digit row.
        0x1E => 0x12, // 1
        0x1F => 0x13, // 2
        0x20 => 0x14, // 3
        0x21 => 0x15, // 4
        0x22 => 0x17, // 5
        0x23 => 0x16, // 6
        0x24 => 0x1A, // 7
        0x25 => 0x1C, // 8
        0x26 => 0x19, // 9
        0x27 => 0x1D, // 0

        0x28 => 0x24, // Return
        0x29 => 0x35, // Escape
        0x2A => 0x33, // Backspace (kVK_Delete)
        0x2B => 0x30, // Tab
        0x2C => 0x31, // Space
        0x2D => 0x1B, // Minus
        0x2E => 0x18, // Equal
        0x2F => 0x21, // LeftBracket
        0x30 => 0x1E, // RightBracket
        0x31 => 0x2A, // Backslash
        0x32 => 0x2A, // NonUsHash — same physical key as Backslash on an ANSI board
        0x33 => 0x29, // Semicolon
        0x34 => 0x27, // Quote
        0x35 => 0x32, // Grave
        0x36 => 0x2B, // Comma
        0x37 => 0x2F, // Period
        0x38 => 0x2C, // Slash
        0x39 => 0x39, // CapsLock

        // Function row. Also in no arithmetic order.
        0x3A => 0x7A, // F1
        0x3B => 0x78, // F2
        0x3C => 0x63, // F3
        0x3D => 0x76, // F4
        0x3E => 0x60, // F5
        0x3F => 0x61, // F6
        0x40 => 0x62, // F7
        0x41 => 0x64, // F8
        0x42 => 0x65, // F9
        0x43 => 0x6D, // F10
        0x44 => 0x67, // F11
        0x45 => 0x6F, // F12

        0x49 => 0x72, // Insert -> kVK_Help, the key in that position on an Apple board
        0x4A => 0x73, // Home
        0x4B => 0x74, // PageUp
        0x4C => 0x75, // Delete (forward)
        0x4D => 0x77, // End
        0x4E => 0x79, // PageDown
        0x4F => 0x7C, // Right
        0x50 => 0x7B, // Left
        0x51 => 0x7D, // Down
        0x52 => 0x7E, // Up

        // Keypad.
        0x53 => 0x47, // NumLock -> kVK_ANSI_KeypadClear
        0x54 => 0x4B, // Keypad /
        0x55 => 0x43, // Keypad *
        0x56 => 0x4E, // Keypad -
        0x57 => 0x45, // Keypad +
        0x58 => 0x4C, // Keypad Enter
        0x59 => 0x53, // Keypad 1
        0x5A => 0x54, // Keypad 2
        0x5B => 0x55, // Keypad 3
        0x5C => 0x56, // Keypad 4
        0x5D => 0x57, // Keypad 5
        0x5E => 0x58, // Keypad 6
        0x5F => 0x59, // Keypad 7
        0x60 => 0x5B, // Keypad 8
        0x61 => 0x5C, // Keypad 9
        0x62 => 0x52, // Keypad 0
        0x63 => 0x41, // Keypad .
        0x64 => 0x2A, // NonUsBackslash
        0x67 => 0x51, // Keypad =

        // F13-F20. Present on full-size Apple keyboards, and what a PC's PrintScreen/ScrollLock/
        // Pause map onto in macOS's own view of the world.
        0x68 => 0x69, // F13
        0x69 => 0x6B, // F14
        0x6A => 0x71, // F15
        0x6B => 0x6A, // F16
        0x6C => 0x40, // F17
        0x6D => 0x4F, // F18
        0x6E => 0x50, // F19
        0x6F => 0x5A, // F20

        // Modifiers. Left and right are distinct keys with distinct codes, and keeping them apart
        // matters for anything that watches for one specifically.
        0xE0 => 0x3B, // LeftControl
        0xE1 => 0x38, // LeftShift
        0xE2 => 0x3A, // LeftAlt / Option
        0xE3 => 0x37, // LeftGUI / Command
        0xE4 => 0x3E, // RightControl
        0xE5 => 0x3C, // RightShift
        0xE6 => 0x3D, // RightAlt / Option
        0xE7 => 0x36, // RightGUI / Command

        _ => return None,
    })
}

/// The modifier flag a HID usage contributes, if it is a modifier at all.
///
/// Held separately from the keycode because `CGEventPost` does not derive modifier state from the
/// key events you have already posted: every event carries its own flags, so a Command-C is a `C`
/// keystroke *with the command flag set*, not a `C` after a `Command` down. Miss this and every
/// shortcut arrives as a bare letter — the single most confusing way for a remote session to look
/// connected and be useless.
fn modifier_flag_for_hid(hid: u32) -> Option<u64> {
    let usage = match hid >> 16 {
        0x0000 => hid,
        0x0007 => hid & 0xFFFF,
        _ => return None,
    };

    Some(match usage {
        0xE0 | 0xE4 => K_CG_EVENT_FLAG_MASK_CONTROL,
        0xE1 | 0xE5 => K_CG_EVENT_FLAG_MASK_SHIFT,
        0xE2 | 0xE6 => K_CG_EVENT_FLAG_MASK_ALTERNATE,
        0xE3 | 0xE7 => K_CG_EVENT_FLAG_MASK_COMMAND,
        // Fn has no HID usage of its own on the keyboard page; included so a viewer that invents
        // one still produces something sensible rather than a bare keystroke.
        0x65 => K_CG_EVENT_FLAG_MASK_SECONDARY_FN,
        _ => return None,
    })
}

/// Which modifiers the remote keyboard is currently holding down.
///
/// Tracked as a set of individual keys rather than a flag word so that releasing right-shift while
/// left-shift is still held does not clear the shift flag — which is what a naive "clear the bit on
/// key up" would do, and it strands the host in a state the person at the far end cannot see.
#[derive(Debug, Default, Clone, PartialEq, Eq)]
pub struct ModifierState {
    held: Vec<u32>,
}

impl ModifierState {
    pub fn press(&mut self, hid: u32) {
        if modifier_flag_for_hid(hid).is_some() && !self.held.contains(&hid) {
            self.held.push(hid);
        }
    }

    pub fn release(&mut self, hid: u32) {
        self.held.retain(|held| *held != hid);
    }

    pub fn flags(&self) -> u64 {
        self.held.iter().filter_map(|hid| modifier_flag_for_hid(*hid)).fold(0, |all, flag| all | flag)
    }

    pub fn held_keys(&self) -> Vec<u32> {
        self.held.clone()
    }
}

/// How far apart two clicks can be and still count as a double click. macOS's own default is 500ms
/// and is user-configurable; a fixed value is used here because the alternative is reading a
/// preference out of the console user's domain from a process that may not be them.
const DOUBLE_CLICK_WINDOW: std::time::Duration = std::time::Duration::from_millis(500);

/// How far the pointer may move between two clicks and still count as a double click, in points.
const DOUBLE_CLICK_SLOP: f64 = 4.0;

/// Posts the remote pointer and keyboard into this Mac.
///
/// Deliberately not `Send`: it holds a `CGEventSourceRef` and is owned by the one thread running a
/// session. There is nothing to gain from moving it, and event ordering is the whole point of a
/// remote input stream.
pub struct InputInjector {
    source: CGEventSourceRef,

    /// The captured display's top-left corner in global display coordinates. Pointer positions
    /// arrive relative to the display being watched, and `CGEventCreateMouseEvent` wants global
    /// coordinates — so on a Mac whose second monitor is the one being controlled, everything
    /// lands on the wrong screen without this.
    origin: CGPoint,

    modifiers: ModifierState,
    position: CGPoint,
    buttons_down: [bool; 3],
    last_click: Option<(MouseButton, std::time::Instant, CGPoint)>,
    click_state: i64,
}

impl InputInjector {
    /// `origin` is the captured display's top-left in global display coordinates — see the field.
    pub fn new(origin: CGPoint) -> Option<Self> {
        // SAFETY: a documented constructor taking one enum value; returns null on failure, which is
        // checked below.
        let source = unsafe { CGEventSourceCreate(K_CG_EVENT_SOURCE_STATE_HID_SYSTEM_STATE) };
        if source.is_null() {
            return None;
        }

        Some(Self {
            source,
            origin,
            modifiers: ModifierState::default(),
            position: CGPoint { x: origin.x, y: origin.y },
            buttons_down: [false; 3],
            last_click: None,
            click_state: 1,
        })
    }

    pub fn apply(&mut self, input: &ViewerInput) {
        match input {
            ViewerInput::Pointer { action, x, y, button } => self.pointer(*action, *x, *y, *button),
            ViewerInput::Scroll { x, y, delta_x, delta_y } => self.scroll(*x, *y, *delta_x, *delta_y),
            ViewerInput::Key { hid, down } => self.key(*hid, *down),
            // Handled by the capture side, not here.
            ViewerInput::Quality { .. } => {}
        }
    }

    /// Lets go of everything the remote end was holding.
    ///
    /// **Called on every path out of a session, including a dropped socket**, and it is not
    /// housekeeping. A session that ends while the remote user happens to be holding Command leaves
    /// this Mac with Command stuck down for its actual owner — every subsequent keystroke becomes a
    /// shortcut, and nothing on screen explains why. The same goes for a held mouse button, which
    /// leaves the desktop in a drag.
    pub fn release_all(&mut self) {
        for button in [MouseButton::Left, MouseButton::Right, MouseButton::Middle] {
            if self.buttons_down[button_index(button)] {
                let position = self.position;
                self.post_mouse(mouse_up_type(button), position, cg_button(button));
                self.buttons_down[button_index(button)] = false;
            }
        }

        for hid in self.modifiers.held_keys() {
            if let Some(virtual_key) = virtual_key_for_hid(hid) {
                self.modifiers.release(hid);
                self.post_key(virtual_key, false);
            }
        }
    }

    fn pointer(&mut self, action: PointerAction, x: f64, y: f64, button: MouseButton) {
        let position = CGPoint { x: self.origin.x + x, y: self.origin.y + y };
        self.position = position;
        let index = button_index(button);

        match action {
            PointerAction::Move => {
                // A move with a button held has to be posted as a *drag* of that button, not as a
                // plain move: a plain move mid-drag ends the drag as far as most applications are
                // concerned, so text selection and window dragging both stop dead after one event.
                let event_type = if self.buttons_down[0] {
                    K_CG_EVENT_LEFT_MOUSE_DRAGGED
                } else if self.buttons_down[1] {
                    K_CG_EVENT_RIGHT_MOUSE_DRAGGED
                } else if self.buttons_down[2] {
                    K_CG_EVENT_OTHER_MOUSE_DRAGGED
                } else {
                    K_CG_EVENT_MOUSE_MOVED
                };

                self.post_mouse(event_type, position, cg_button(button));
            }

            PointerAction::Down => {
                self.click_state = self.next_click_state(button, position);
                self.buttons_down[index] = true;
                self.post_mouse(mouse_down_type(button), position, cg_button(button));
            }

            PointerAction::Up => {
                self.buttons_down[index] = false;
                self.post_mouse(mouse_up_type(button), position, cg_button(button));
            }
        }
    }

    /// Decides whether this press continues a double click. Same test the window server applies:
    /// same button, close enough in time, and the pointer has not wandered.
    fn next_click_state(&mut self, button: MouseButton, position: CGPoint) -> i64 {
        let now = std::time::Instant::now();

        let state = match self.last_click {
            Some((last_button, at, at_position))
                if last_button == button
                    && now.duration_since(at) <= DOUBLE_CLICK_WINDOW
                    && (at_position.x - position.x).abs() <= DOUBLE_CLICK_SLOP
                    && (at_position.y - position.y).abs() <= DOUBLE_CLICK_SLOP =>
            {
                // Capped at 3: a quadruple click means nothing to anything, and letting this grow
                // makes a fast typist's repeated clicking increasingly strange.
                (self.click_state + 1).min(3)
            }
            _ => 1,
        };

        self.last_click = Some((button, now, position));
        state
    }

    fn scroll(&mut self, x: f64, y: f64, delta_x: f64, delta_y: f64) {
        // Scroll events are delivered wherever the pointer is, so it has to be moved there first —
        // a browser reports the position with the wheel event precisely because the two are
        // separate on the far end.
        let position = CGPoint { x: self.origin.x + x, y: self.origin.y + y };
        if position != self.position {
            self.position = position;
            self.post_mouse(K_CG_EVENT_MOUSE_MOVED, position, K_CG_MOUSE_BUTTON_LEFT);
        }

        // Negated because the two ends disagree about which way is positive: a browser's
        // `deltaY` grows as content scrolls *down*, and CoreGraphics' wheel value grows as the
        // content moves *up*. Getting this wrong is the classic inverted-scrolling bug.
        let wheel_vertical = -delta_y.round().clamp(i32::MIN as f64, i32::MAX as f64) as i32;
        let wheel_horizontal = -delta_x.round().clamp(i32::MIN as f64, i32::MAX as f64) as i32;

        if wheel_vertical == 0 && wheel_horizontal == 0 {
            return;
        }

        // SAFETY: `source` is non-null for this injector's lifetime (checked in `new`). The event is
        // released below whether or not it was posted.
        unsafe {
            let event = CGEventCreateScrollWheelEvent(
                self.source,
                K_CG_SCROLL_EVENT_UNIT_PIXEL,
                2,
                wheel_vertical,
                wheel_horizontal,
            );

            if !event.is_null() {
                CGEventSetFlags(event, self.modifiers.flags());
                CGEventPost(K_CG_HID_EVENT_TAP, event);
                CFRelease(event);
            }
        }
    }

    fn key(&mut self, hid: u32, down: bool) {
        let Some(virtual_key) = virtual_key_for_hid(hid) else {
            return;
        };

        // The modifier set is updated *before* posting a press and *after* posting a release, so the
        // flags on the event itself describe the state the host should see while that key is down.
        if down {
            self.modifiers.press(hid);
            self.post_key(virtual_key, true);
        } else {
            self.post_key(virtual_key, false);
            self.modifiers.release(hid);
        }
    }

    fn post_key(&self, virtual_key: u16, down: bool) {
        // SAFETY: as `scroll` — non-null source, and the event is released on both paths.
        unsafe {
            let event = CGEventCreateKeyboardEvent(self.source, virtual_key, down);
            if !event.is_null() {
                CGEventSetFlags(event, self.modifiers.flags());
                CGEventPost(K_CG_HID_EVENT_TAP, event);
                CFRelease(event);
            }
        }
    }

    fn post_mouse(&self, event_type: u32, position: CGPoint, button: u32) {
        // SAFETY: as `scroll` — non-null source, and the event is released on both paths.
        unsafe {
            let event = CGEventCreateMouseEvent(self.source, event_type, position, button);
            if !event.is_null() {
                CGEventSetFlags(event, self.modifiers.flags());
                CGEventSetIntegerValueField(event, K_CG_MOUSE_EVENT_CLICK_STATE, self.click_state);
                CGEventPost(K_CG_HID_EVENT_TAP, event);
                CFRelease(event);
            }
        }
    }
}

impl Drop for InputInjector {
    fn drop(&mut self) {
        // Belt and braces over `release_all`, which the session loop calls explicitly: a panic on
        // the session thread would otherwise unwind past it and leave a modifier held.
        self.release_all();

        if !self.source.is_null() {
            // SAFETY: created by CGEventSourceCreate and owned solely by this struct.
            unsafe { CFRelease(self.source) };
            self.source = std::ptr::null_mut();
        }
    }
}

fn button_index(button: MouseButton) -> usize {
    match button {
        MouseButton::Left => 0,
        MouseButton::Right => 1,
        MouseButton::Middle => 2,
    }
}

fn cg_button(button: MouseButton) -> u32 {
    match button {
        MouseButton::Left => K_CG_MOUSE_BUTTON_LEFT,
        MouseButton::Right => K_CG_MOUSE_BUTTON_RIGHT,
        MouseButton::Middle => K_CG_MOUSE_BUTTON_CENTER,
    }
}

fn mouse_down_type(button: MouseButton) -> u32 {
    match button {
        MouseButton::Left => K_CG_EVENT_LEFT_MOUSE_DOWN,
        MouseButton::Right => K_CG_EVENT_RIGHT_MOUSE_DOWN,
        MouseButton::Middle => K_CG_EVENT_OTHER_MOUSE_DOWN,
    }
}

fn mouse_up_type(button: MouseButton) -> u32 {
    match button {
        MouseButton::Left => K_CG_EVENT_LEFT_MOUSE_UP,
        MouseButton::Right => K_CG_EVENT_RIGHT_MOUSE_UP,
        MouseButton::Middle => K_CG_EVENT_OTHER_MOUSE_UP,
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    // These cover the pure mapping and state tracking only. Posting an event is not testable
    // without the Accessibility permission and a window server, and asserting that a real keystroke
    // arrived would mean typing into whatever happened to have focus on the machine running the
    // suite.

    #[test]
    fn maps_flutters_full_usage_value_and_the_bare_one_alike() {
        // Flutter reports 0x0007_0004 for A; a hand-written client might send 0x04.
        assert_eq!(virtual_key_for_hid(0x0007_0004), Some(0x00));
        assert_eq!(virtual_key_for_hid(0x04), Some(0x00));
    }

    #[test]
    fn maps_the_letters_that_are_not_in_order() {
        // The three that catch a clever formula out.
        assert_eq!(virtual_key_for_hid(0x04), Some(0x00)); // A
        assert_eq!(virtual_key_for_hid(0x05), Some(0x0B)); // B
        assert_eq!(virtual_key_for_hid(0x06), Some(0x08)); // C
        assert_eq!(virtual_key_for_hid(0x1D), Some(0x06)); // Z
    }

    #[test]
    fn maps_the_keys_a_remote_session_is_useless_without() {
        assert_eq!(virtual_key_for_hid(0x28), Some(0x24)); // Return
        assert_eq!(virtual_key_for_hid(0x2A), Some(0x33)); // Backspace
        assert_eq!(virtual_key_for_hid(0x2B), Some(0x30)); // Tab
        assert_eq!(virtual_key_for_hid(0x2C), Some(0x31)); // Space
        assert_eq!(virtual_key_for_hid(0x29), Some(0x35)); // Escape
        assert_eq!(virtual_key_for_hid(0x4F), Some(0x7C)); // Right arrow
        assert_eq!(virtual_key_for_hid(0x50), Some(0x7B)); // Left arrow
    }

    #[test]
    fn keeps_left_and_right_modifiers_distinct() {
        assert_eq!(virtual_key_for_hid(0xE1), Some(0x38)); // LeftShift
        assert_eq!(virtual_key_for_hid(0xE5), Some(0x3C)); // RightShift
        assert_ne!(virtual_key_for_hid(0xE1), virtual_key_for_hid(0xE5));
    }

    #[test]
    fn ignores_usage_pages_with_no_virtual_keycode() {
        // Consumer control (volume, brightness) is page 0x000C and has no keycode at all.
        assert_eq!(virtual_key_for_hid(0x000C_00E9), None);
        // And an unassigned keyboard usage.
        assert_eq!(virtual_key_for_hid(0xFE), None);
    }

    #[test]
    fn modifier_flags_accumulate() {
        let mut state = ModifierState::default();
        state.press(0xE3); // LeftCommand
        state.press(0xE1); // LeftShift

        assert_eq!(state.flags(), K_CG_EVENT_FLAG_MASK_COMMAND | K_CG_EVENT_FLAG_MASK_SHIFT);
    }

    #[test]
    fn releasing_one_shift_keeps_the_flag_while_the_other_is_held() {
        // The reason modifiers are tracked as keys rather than as a flag word.
        let mut state = ModifierState::default();
        state.press(0xE1); // LeftShift
        state.press(0xE5); // RightShift

        state.release(0xE5);

        assert_eq!(state.flags(), K_CG_EVENT_FLAG_MASK_SHIFT);

        state.release(0xE1);
        assert_eq!(state.flags(), 0);
        assert!(state.held_keys().is_empty());
    }

    #[test]
    fn pressing_the_same_modifier_twice_is_not_two_keys_to_release() {
        // Key repeat sends repeated downs; without this, held_keys grows and release_all posts a
        // release per repeat.
        let mut state = ModifierState::default();
        state.press(0xE3);
        state.press(0xE3);

        assert_eq!(state.held_keys().len(), 1);
    }

    #[test]
    fn non_modifiers_do_not_enter_the_modifier_state() {
        let mut state = ModifierState::default();
        state.press(0x04); // A

        assert!(state.held_keys().is_empty());
        assert_eq!(state.flags(), 0);
    }

    #[test]
    fn modifier_flags_cover_both_sides_of_the_keyboard() {
        assert_eq!(modifier_flag_for_hid(0xE0), Some(K_CG_EVENT_FLAG_MASK_CONTROL));
        assert_eq!(modifier_flag_for_hid(0xE4), Some(K_CG_EVENT_FLAG_MASK_CONTROL));
        assert_eq!(modifier_flag_for_hid(0xE2), Some(K_CG_EVENT_FLAG_MASK_ALTERNATE));
        assert_eq!(modifier_flag_for_hid(0xE6), Some(K_CG_EVENT_FLAG_MASK_ALTERNATE));
        assert_eq!(modifier_flag_for_hid(0x04), None);
    }
}
