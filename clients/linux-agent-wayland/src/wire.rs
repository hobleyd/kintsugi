//! The protocol between the agent and this helper, over the helper's stdin and stdout.
//!
//! # Why a private protocol rather than the media protocol
//!
//! The helper is deliberately **not** a second implementation of the agent's remote-control
//! protocol. It never sees the WebSocket, holds no identity, encodes no JPEG and knows nothing about
//! sessions or consent — it captures pixels and injects input, and the agent does everything else.
//! That is what keeps the reviewable surface small: the security decisions all stay in the agent,
//! which is the process holding the fleet private key.
//!
//! So this carries raw BGRA frames one way and input events the other, and nothing else.
//!
//! # Framing
//!
//! Every message on stdout is a one-byte kind, a big-endian `u32` length, then that many bytes —
//! the same shape as the agent's own IPC framing in `remote_ipc.rs`, for the same reason: a pipe is
//! a byte stream, so a length-prefixed frame is the only way to know where a message ends. Frames
//! are megabytes of binary, which is also why stdout is used rather than a line protocol.
//!
//! Input arrives on stdin as one JSON object per line, because it is small, infrequent and much
//! easier to read in a log when something is wrong.
//!
//! The other end of both is `clients/linux-agent/src/wayland_backend.rs`. There is no version
//! negotiation, and that is safe here in a way it is not for the media protocol: the agent and the
//! helper are shipped in the same archive and replaced together by `self_update`.

use std::io::{self, Write};

use serde::{Deserialize, Serialize};

/// A JSON description of the stream's current pixel layout. Sent before the first frame, and again
/// whenever PipeWire renegotiates — which really happens, on a resolution change or a monitor
/// hotplug, and a frame parsed with the previous stride is diagonal garbage.
pub const KIND_FORMAT: u8 = 1;

/// One frame, raw BGRA, `stride * height` bytes.
pub const KIND_FRAME: u8 = 2;

/// A JSON `{"message": …}` the agent logs and reports as the reason a session could not start.
pub const KIND_ERROR: u8 = 3;

#[derive(Serialize, Deserialize, Debug, Clone, PartialEq, Eq)]
pub struct FormatMessage {
    pub width: u32,
    pub height: u32,

    /// Bytes per row, which is **not** `width * 4`: PipeWire pads rows for alignment, so a 1366-wide
    /// stream commonly arrives with a 5472-byte stride and reading `width * 4` shears the picture.
    pub stride: u32,

    /// Whether the portal granted keyboard and pointer as well as capture.
    ///
    /// Reported here rather than assumed, because it is the whole reason the viewer has a view-only
    /// mode: wlroots compositors implement `ScreenCast` and not `RemoteDesktop`, so a Sway or
    /// Hyprland host can be watched and not driven. See `DisplayInfo::with_input` in
    /// `remote_protocol.rs`.
    pub can_control_input: bool,
}

#[derive(Serialize, Deserialize, Debug)]
pub struct ErrorMessage {
    pub message: String,
}

/// Something to do to the host, as the agent sends it.
///
/// Keycodes are **evdev** codes, already mapped from the USB HID usage by the agent's
/// `input_injection::evdev_keycode_for_hid`. Deliberately: that table is the one description of the
/// mapping and duplicating it here would let the two drift, with the symptom being wrong letters
/// on a Wayland host only. Note the portal wants the raw evdev code, where XTEST wants it plus 8 —
/// the agent applies that offset on the X11 path and not this one.
#[derive(Serialize, Deserialize, Debug)]
#[serde(tag = "type", rename_all = "lowercase")]
pub enum InputMessage {
    /// Absolute pointer position, in stream pixels.
    Pointer {
        x: f64,
        y: f64,
        action: PointerAction,
        #[serde(default)]
        button: PointerButton,
    },
    Key {
        evdev: i32,
        down: bool,
    },
    /// Wheel movement in discrete steps, positive being down and right.
    Scroll {
        #[serde(default)]
        steps_x: i32,
        #[serde(default)]
        steps_y: i32,
    },
}

#[derive(Serialize, Deserialize, Debug, Clone, Copy, PartialEq, Eq)]
#[serde(rename_all = "lowercase")]
pub enum PointerAction {
    Move,
    Down,
    Up,
}

#[derive(Serialize, Deserialize, Debug, Clone, Copy, Default, PartialEq, Eq)]
#[serde(rename_all = "lowercase")]
pub enum PointerButton {
    #[default]
    Left,
    Right,
    Middle,
}

impl PointerButton {
    /// The evdev `BTN_*` code the portal expects.
    ///
    /// These are `linux/input-event-codes.h` values, not X11 button numbers — the portal's
    /// `NotifyPointerButton` takes an evdev code, so passing X11's 1/2/3 would inject three
    /// keyboard-ish buttons nothing recognises rather than failing.
    pub fn evdev_code(self) -> i32 {
        match self {
            // BTN_LEFT, BTN_RIGHT, BTN_MIDDLE.
            PointerButton::Left => 0x110,
            PointerButton::Right => 0x111,
            PointerButton::Middle => 0x112,
        }
    }
}

/// Writes one framed message to stdout and flushes it.
///
/// Flushed every time because the agent is blocking on a read: a buffered frame is a session that
/// appears to have frozen.
pub fn write_message(out: &mut impl Write, kind: u8, payload: &[u8]) -> io::Result<()> {
    let length = u32::try_from(payload.len())
        .map_err(|_| io::Error::new(io::ErrorKind::InvalidInput, "message too long to frame"))?;

    out.write_all(&[kind])?;
    out.write_all(&length.to_be_bytes())?;
    out.write_all(payload)?;
    out.flush()
}

pub fn write_format(out: &mut impl Write, format: &FormatMessage) -> io::Result<()> {
    let json = serde_json::to_vec(format)
        .map_err(|error| io::Error::new(io::ErrorKind::InvalidData, error))?;
    write_message(out, KIND_FORMAT, &json)
}

pub fn write_error(out: &mut impl Write, message: &str) -> io::Result<()> {
    let json = serde_json::to_vec(&ErrorMessage { message: message.to_string() })
        .map_err(|error| io::Error::new(io::ErrorKind::InvalidData, error))?;
    write_message(out, KIND_ERROR, &json)
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn a_message_is_a_kind_a_big_endian_length_and_a_payload() {
        // The agent reads this by hand; a little-endian length would have it wait for 4 GB.
        let mut out = Vec::new();
        write_message(&mut out, KIND_FRAME, &[0xAA, 0xBB]).unwrap();

        assert_eq!(out, vec![KIND_FRAME, 0, 0, 0, 2, 0xAA, 0xBB]);
    }

    #[test]
    fn an_empty_payload_is_a_valid_message() {
        // Not currently sent, but the framing must not encode "zero length" as anything special —
        // the reader's loop would then stall rather than continue.
        let mut out = Vec::new();
        write_message(&mut out, KIND_FORMAT, &[]).unwrap();

        assert_eq!(out, vec![KIND_FORMAT, 0, 0, 0, 0]);
    }

    #[test]
    fn the_format_message_names_its_fields_the_way_the_agent_reads_them() {
        // A rename on one side only is a helper whose frames the agent cannot lay out, which shows
        // up as a sheared picture rather than an error.
        let json = serde_json::to_string(&FormatMessage {
            width: 1920,
            height: 1080,
            stride: 7680,
            can_control_input: false,
        })
        .unwrap();

        assert!(json.contains("\"width\":1920"), "{json}");
        assert!(json.contains("\"stride\":7680"), "{json}");
        assert!(json.contains("\"can_control_input\":false"), "{json}");
    }

    #[test]
    fn input_is_tagged_by_type_and_parses_the_agent_s_wording() {
        let pointer: InputMessage =
            serde_json::from_str(r#"{"type":"pointer","x":1.5,"y":2.5,"action":"down","button":"right"}"#)
                .unwrap();
        match pointer {
            InputMessage::Pointer { x, y, action, button } => {
                assert_eq!((x, y), (1.5, 2.5));
                assert_eq!(action, PointerAction::Down);
                assert_eq!(button, PointerButton::Right);
            }
            other => panic!("parsed as {other:?}"),
        }

        let key: InputMessage = serde_json::from_str(r#"{"type":"key","evdev":30,"down":true}"#).unwrap();
        match key {
            InputMessage::Key { evdev, down } => assert_eq!((evdev, down), (30, true)),
            other => panic!("parsed as {other:?}"),
        }
    }

    #[test]
    fn a_pointer_move_defaults_to_the_left_button() {
        // The agent omits it on a move, exactly as the viewer does on the wire.
        let parsed: InputMessage =
            serde_json::from_str(r#"{"type":"pointer","x":0,"y":0,"action":"move"}"#).unwrap();

        match parsed {
            InputMessage::Pointer { button, .. } => assert_eq!(button, PointerButton::Left),
            other => panic!("parsed as {other:?}"),
        }
    }

    #[test]
    fn a_scroll_with_one_axis_leaves_the_other_at_zero() {
        let parsed: InputMessage =
            serde_json::from_str(r#"{"type":"scroll","steps_y":-2}"#).unwrap();

        match parsed {
            InputMessage::Scroll { steps_x, steps_y } => assert_eq!((steps_x, steps_y), (0, -2)),
            other => panic!("parsed as {other:?}"),
        }
    }

    #[test]
    fn the_mouse_buttons_are_evdev_codes_rather_than_x11_button_numbers() {
        // X11's 1/2/3 would be injected as BTN_ codes 1, 2 and 3, which are keyboard keys — the
        // click would silently do something else entirely rather than fail.
        assert_eq!(PointerButton::Left.evdev_code(), 0x110);
        assert_eq!(PointerButton::Middle.evdev_code(), 0x112);
        assert_eq!(PointerButton::Right.evdev_code(), 0x111);
    }
}
