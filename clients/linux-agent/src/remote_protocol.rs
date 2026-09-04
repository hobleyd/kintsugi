//! The two wire protocols remote control uses, and the only place either is described on this side.
//!
//! **A copy of the macOS agent's `remote_protocol.rs`, deliberately, and it must not diverge.** The
//! viewer is the same browser talking the same protocol whatever the host is, so all three agents
//! are kept identical here the same way their module names, orderings and comments are kept
//! identical everywhere else. If one changes, the others and
//! `web/lib/data/models/remote_control_mapper.dart` change with it.
//!
//! `diff` the three and exactly one line differs outside this module comment: the cross-reference on
//! `ViewerInput::Key`, which names `input_injection::xtest_keycode_for_hid` here,
//! `scan_code_for_hid` on Windows and `virtual_key_for_hid` on macOS. That is not drift — the three
//! platforms reach the same positional key through differently-named APIs — but anything *else*
//! showing up in that diff is.
//!
//! As on Windows, the two halves of this protocol are spoken by *different processes*: the control
//! protocol is the root service's, because only it holds this host's identity, and the media
//! protocol is the per-user process's, because only a graphical session can capture a screen or post
//! input. `remote_ipc` is what joins them.
//!
//! **They are separate protocols with separate peers, and that is the whole architecture.** The
//! *control* protocol is between this agent and the Kintsugi server: session requests in, the host
//! user's answer out. Its C# counterpart is `RemoteControlProtocol.cs`, mirrored by hand here the
//! same way every other request/response struct in this agent mirrors a C# shape.
//!
//! The *media* protocol — screen frames out, keyboard and mouse in — is between this agent and the
//! administrator's **browser**. The server relays it byte for byte without parsing any of it (see
//! `RemoteControlSessionBroker`), so its counterpart is not C# at all: it is
//! `web/lib/presentation/remote_control/`. Nothing in the server needs to change to add a
//! capability to it, and nothing in the server will catch the two ends drifting apart either.
//!
//! Everything here is pure — bytes and strings in, values out — so the format can be tested
//! without a socket, a screen or a server. That matters more than usual: a header field written
//! big-endian on one side and read little-endian on the other produces a picture, just a garbled
//! one, which is exactly the kind of failure that survives a code review.

use serde::{Deserialize, Serialize};

// =================================================================================================
// The control protocol: this agent <-> the Kintsugi server.
// =================================================================================================

/// What the host user said. The names serialise to
/// `Kintsugi.Domain.Enums.RemoteControlConsent` member names, which is what the server parses them
/// as — so these strings are load-bearing and must not be prettified.
///
/// `Deserialize` as well as `Serialize`, which the macOS agent has no use for: on Windows and Linux
/// this value is read back off the local channel between the privileged half and the per-user one
/// (see those agents' `remote_ipc`). Derived in all three so the file stays identical between them.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
pub enum ConsentOutcome {
    Granted,
    Denied,
    /// Nobody answered before the dialog gave up. Reported as itself rather than flattened into
    /// `Denied`, because "refused" and "was away from the desk" are different facts about a host —
    /// and because the effect is the same either way, there is no temptation to conflate them.
    TimedOut,
}

/// Server to agent. Deliberately not a `#[serde(tag = "type")]` enum: serde fails the whole parse
/// on a tag it does not know, and a newer server sending a message this build has never heard of
/// must not take the socket down. See [`parse_server_message`].
#[derive(Debug, Clone, PartialEq, Eq)]
pub enum ServerMessage {
    /// Put the consent dialog up.
    SessionRequested {
        session_id: String,
        requested_by: String,
        consent_timeout_seconds: u64,
    },
    /// Stop: either the administrator hung up, or the server gave up waiting for one of the two
    /// sockets to arrive.
    SessionEnded { session_id: String, reason: String },
}

/// Agent to server.
#[derive(Debug, Clone, Serialize)]
#[serde(tag = "type")]
pub enum AgentMessage {
    /// Sent once on connecting. Nothing depends on it — the socket is already authenticated by the
    /// client certificate nginx verified — so it is diagnostics, and the server logs it to make
    /// "which build is this host running" answerable without waiting for a check-in.
    #[serde(rename = "hello")]
    Hello {
        #[serde(rename = "agentVersion")]
        agent_version: String,
        #[serde(rename = "consoleUser")]
        console_user: Option<String>,
    },

    #[serde(rename = "consent")]
    Consent {
        #[serde(rename = "sessionId")]
        session_id: String,
        outcome: ConsentOutcome,
    },

    /// The person at the keyboard ended it from the menu bar, or capture failed part-way through.
    #[serde(rename = "session-ended")]
    SessionEnded {
        #[serde(rename = "sessionId")]
        session_id: String,
        reason: String,
    },
}

/// Reads one control message. `Ok(None)` for a well-formed message of an unknown type, which the
/// caller should log and ignore; `Err` only for something that is not usable JSON at all.
pub fn parse_server_message(json: &str) -> Result<Option<ServerMessage>, serde_json::Error> {
    #[derive(Deserialize)]
    struct Envelope {
        #[serde(rename = "type")]
        message_type: String,
        #[serde(rename = "sessionId")]
        session_id: Option<String>,
        #[serde(rename = "requestedBy")]
        requested_by: Option<String>,
        #[serde(rename = "consentTimeoutSeconds")]
        consent_timeout_seconds: Option<u64>,
        reason: Option<String>,
    }

    let envelope: Envelope = serde_json::from_str(json)?;

    Ok(match envelope.message_type.as_str() {
        "session-requested" => envelope.session_id.map(|session_id| ServerMessage::SessionRequested {
            session_id,
            // A dialog that cannot say who is asking is unanswerable, so an absent value gets
            // wording that is honest rather than blank.
            requested_by: envelope.requested_by.unwrap_or_else(|| "an administrator".to_string()),
            // Falls back to the server's own default rather than to "no timeout": a dialog left up
            // forever is a dialog somebody eventually clicks to get rid of.
            consent_timeout_seconds: envelope.consent_timeout_seconds.unwrap_or(90),
        }),
        "session-ended" => envelope.session_id.map(|session_id| ServerMessage::SessionEnded {
            session_id,
            reason: envelope.reason.unwrap_or_else(|| "the session ended".to_string()),
        }),
        _ => None,
    })
}

// =================================================================================================
// The media protocol: this agent <-> the administrator's browser.
// =================================================================================================

/// Bumped only for a change the other end cannot ignore. The viewer checks it and refuses a frame
/// it does not understand rather than drawing noise.
pub const PROTOCOL_VERSION: u8 = 1;

/// A rectangle of the screen, JPEG-encoded.
pub const KIND_JPEG_TILE: u8 = 1;

/// `version, kind, x, y, width, height, sequence` — see [`encode_tile`].
pub const TILE_HEADER_BYTES: usize = 14;

/// Sent as a text message whenever the geometry changes, and always once before the first tile.
///
/// Two sizes, because they are genuinely different and conflating them is what puts the remote
/// cursor half an inch from where it should be on a Retina display. The *point* size is the display's
/// logical coordinate space, which is what pointer events are expressed in and what
/// `input_injection` posts into. The *image* size is what the JPEG tiles actually are, after
/// downscaling for the link — the viewer scales the picture to fit its canvas and must convert a
/// click back through the point size, never through the image size.
#[derive(Debug, Clone, PartialEq, Serialize)]
pub struct DisplayInfo {
    #[serde(rename = "type")]
    pub message_type: &'static str,
    #[serde(rename = "pointWidth")]
    pub point_width: f64,
    #[serde(rename = "pointHeight")]
    pub point_height: f64,
    #[serde(rename = "imageWidth")]
    pub image_width: u32,
    #[serde(rename = "imageHeight")]
    pub image_height: u32,

    /// Whether this session can accept keyboard and mouse, or is view-only.
    ///
    /// **Not a nicety — it is the difference between a limitation and an apparent fault.** Some
    /// hosts can be watched and not driven, and without being told, an operator sees a live picture
    /// that ignores the mouse and concludes the session is broken. The viewer says "view only" and
    /// hides the input hints instead.
    ///
    /// True on macOS and Windows in normal operation. The case it exists for is Linux under Wayland:
    /// a compositor may implement the portal's ScreenCast interface (so it can be captured) without
    /// RemoteDesktop (so it cannot be driven) — wlroots-based ones commonly do — and there is no way
    /// to know until the portal session is negotiated.
    #[serde(rename = "canControlInput")]
    pub can_control_input: bool,
}

impl DisplayInfo {
    /// Allowed to be unused, and that is deliberate rather than an oversight: this module is kept
    /// byte-identical across all three agents, and the Linux one now always states
    /// `can_control_input` because it is the only agent that can capture a host it cannot drive.
    /// Diverging the file to silence one warning would cost the property that makes a change to the
    /// protocol a three-way diff anyone can check.
    #[allow(dead_code)]
    pub fn new(point_width: f64, point_height: f64, image_width: u32, image_height: u32) -> Self {
        Self::with_input(point_width, point_height, image_width, image_height, true)
    }

    /// As [`Self::new`], but stating explicitly whether input works. Only the Wayland backend needs
    /// this; everything else is a session that can be driven.
    pub fn with_input(
        point_width: f64,
        point_height: f64,
        image_width: u32,
        image_height: u32,
        can_control_input: bool,
    ) -> Self {
        Self {
            message_type: "display",
            point_width,
            point_height,
            image_width,
            image_height,
            can_control_input,
        }
    }
}

/// Frames one JPEG tile for the wire.
///
/// Big-endian, because that is what `ByteData.getUint16` defaults to on the Dart side — matching the
/// reader's default rather than the writer's convenience is the version of this decision least
/// likely to be got wrong later.
pub fn encode_tile(x: u16, y: u16, width: u16, height: u16, sequence: u32, jpeg: &[u8]) -> Vec<u8> {
    let mut message = Vec::with_capacity(TILE_HEADER_BYTES + jpeg.len());
    message.push(PROTOCOL_VERSION);
    message.push(KIND_JPEG_TILE);
    message.extend_from_slice(&x.to_be_bytes());
    message.extend_from_slice(&y.to_be_bytes());
    message.extend_from_slice(&width.to_be_bytes());
    message.extend_from_slice(&height.to_be_bytes());
    message.extend_from_slice(&sequence.to_be_bytes());
    message.extend_from_slice(jpeg);
    message
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum PointerAction {
    Move,
    Down,
    Up,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum MouseButton {
    Left,
    Right,
    Middle,
}

/// Everything the browser can ask this agent to do. Input is low-volume next to the frame stream,
/// so it is JSON rather than packed binary — a session that behaves oddly can be diagnosed by
/// reading the messages.
#[derive(Debug, Clone, PartialEq)]
pub enum ViewerInput {
    Pointer {
        action: PointerAction,
        /// Display points, not image pixels, and not integers — a trackpad on a Retina display
        /// genuinely lands between points, and rounding here is a pixel of drift per event.
        x: f64,
        y: f64,
        button: MouseButton,
    },
    Scroll {
        x: f64,
        y: f64,
        delta_x: f64,
        delta_y: f64,
    },
    /// A USB HID usage code, as Flutter's `PhysicalKeyboardKey.usbHidUsage` reports it — the
    /// physical key, not the character it would produce. See `input_injection::xtest_keycode_for_hid`
    /// for why the physical key is the right thing to send.
    Key { hid: u32, down: bool },
    /// The viewer asking for a different trade between picture and bandwidth.
    Quality {
        max_width: Option<u32>,
        jpeg_quality: Option<u8>,
        max_fps: Option<u8>,
    },
}

/// Reads one input message, or `None` for anything unrecognised or malformed.
///
/// Never an error and never a panic. This is the one place in the agent that parses input arriving
/// from a browser, and a session dropping because of one odd message would be a worse failure than
/// the message being ignored — the person on the other end is holding a mouse and would see the
/// screen freeze with nothing to explain it.
pub fn parse_viewer_input(json: &str) -> Option<ViewerInput> {
    #[derive(Deserialize)]
    struct Envelope {
        #[serde(rename = "type")]
        message_type: String,
        action: Option<String>,
        x: Option<f64>,
        y: Option<f64>,
        button: Option<String>,
        #[serde(rename = "deltaX")]
        delta_x: Option<f64>,
        #[serde(rename = "deltaY")]
        delta_y: Option<f64>,
        hid: Option<u32>,
        down: Option<bool>,
        #[serde(rename = "maxWidth")]
        max_width: Option<u32>,
        #[serde(rename = "jpegQuality")]
        jpeg_quality: Option<u8>,
        #[serde(rename = "maxFps")]
        max_fps: Option<u8>,
    }

    let envelope: Envelope = serde_json::from_str(json).ok()?;

    match envelope.message_type.as_str() {
        "pointer" => {
            let action = match envelope.action.as_deref()? {
                "move" => PointerAction::Move,
                "down" => PointerAction::Down,
                "up" => PointerAction::Up,
                _ => return None,
            };

            Some(ViewerInput::Pointer {
                action,
                x: envelope.x?,
                y: envelope.y?,
                // An absent button means the left one: a plain move carries no button at all, and
                // that is the overwhelming majority of these messages.
                button: match envelope.button.as_deref() {
                    Some("right") => MouseButton::Right,
                    Some("middle") => MouseButton::Middle,
                    _ => MouseButton::Left,
                },
            })
        }

        "scroll" => Some(ViewerInput::Scroll {
            x: envelope.x?,
            y: envelope.y?,
            delta_x: envelope.delta_x.unwrap_or(0.0),
            delta_y: envelope.delta_y.unwrap_or(0.0),
        }),

        "key" => Some(ViewerInput::Key {
            hid: envelope.hid?,
            down: envelope.down?,
        }),

        "quality" => Some(ViewerInput::Quality {
            max_width: envelope.max_width,
            jpeg_quality: envelope.jpeg_quality,
            max_fps: envelope.max_fps,
        }),

        _ => None,
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn parses_a_session_request() {
        let message = parse_server_message(
            r#"{"type":"session-requested","sessionId":"abc","requestedBy":"admin@example.com","consentTimeoutSeconds":90}"#,
        )
        .unwrap()
        .unwrap();

        assert_eq!(
            message,
            ServerMessage::SessionRequested {
                session_id: "abc".to_string(),
                requested_by: "admin@example.com".to_string(),
                consent_timeout_seconds: 90,
            }
        );
    }

    #[test]
    fn names_an_administrator_even_when_the_server_did_not() {
        // The dialog has to say who is asking; blank would be worse than vague.
        let message = parse_server_message(r#"{"type":"session-requested","sessionId":"abc"}"#)
            .unwrap()
            .unwrap();

        match message {
            ServerMessage::SessionRequested { requested_by, consent_timeout_seconds, .. } => {
                assert_eq!(requested_by, "an administrator");
                assert_eq!(consent_timeout_seconds, 90);
            }
            other => panic!("expected a session request, got {other:?}"),
        }
    }

    #[test]
    fn parses_a_session_ending() {
        let message = parse_server_message(r#"{"type":"session-ended","sessionId":"abc","reason":"the administrator disconnected"}"#)
            .unwrap()
            .unwrap();

        assert_eq!(
            message,
            ServerMessage::SessionEnded {
                session_id: "abc".to_string(),
                reason: "the administrator disconnected".to_string(),
            }
        );
    }

    #[test]
    fn an_unknown_message_type_is_ignored_rather_than_fatal() {
        // A newer server must not be able to take this socket down by mentioning something this
        // build has never heard of.
        assert_eq!(parse_server_message(r#"{"type":"clipboard","data":"x"}"#).unwrap(), None);
    }

    #[test]
    fn a_session_request_without_an_id_is_ignored() {
        // There would be nothing to answer, and answering the wrong session is worse than silence.
        assert_eq!(parse_server_message(r#"{"type":"session-requested"}"#).unwrap(), None);
    }

    #[test]
    fn unusable_json_is_an_error_not_a_silent_skip() {
        assert!(parse_server_message("not json at all").is_err());
    }

    #[test]
    fn consent_outcomes_serialise_to_the_server_enum_names() {
        // These strings are parsed by name into Kintsugi.Domain.Enums.RemoteControlConsent. If this
        // test is failing because the names were made prettier, the server will read every answer
        // as unusable and no session will ever start.
        let message = AgentMessage::Consent {
            session_id: "abc".to_string(),
            outcome: ConsentOutcome::TimedOut,
        };

        assert_eq!(
            serde_json::to_string(&message).unwrap(),
            r#"{"type":"consent","sessionId":"abc","outcome":"TimedOut"}"#
        );
    }

    #[test]
    fn hello_omits_nothing_the_server_logs() {
        let message = AgentMessage::Hello {
            agent_version: "0.5.3".to_string(),
            console_user: Some("david".to_string()),
        };

        assert_eq!(
            serde_json::to_string(&message).unwrap(),
            r#"{"type":"hello","agentVersion":"0.5.3","consoleUser":"david"}"#
        );
    }

    #[test]
    fn display_info_defaults_to_a_session_that_can_be_driven() {
        // Every backend but Wayland can inject, so the common constructor must not make callers
        // remember to say so.
        let info = DisplayInfo::new(1512.0, 982.0, 1512, 982);

        assert!(info.can_control_input);
    }

    #[test]
    fn display_info_carries_the_view_only_flag_under_the_name_the_viewer_reads() {
        // The viewer keys on this exact name and the server relays the message without parsing it,
        // so a rename here is a viewer that silently treats every session as drivable.
        let json = serde_json::to_string(&DisplayInfo::with_input(1.0, 2.0, 1, 2, false)).unwrap();

        assert!(json.contains(r#""canControlInput":false"#), "{json}");
    }

    #[test]
    fn encodes_a_tile_header_big_endian() {
        // 0x0102 read little-endian is 0x0201 — a tile 513 pixels along instead of 258. The picture
        // still draws, which is why this is asserted byte by byte.
        let message = encode_tile(0x0102, 0x0304, 0x0506, 0x0708, 0x090A0B0C, &[0xFF, 0xD8]);

        assert_eq!(
            message,
            vec![
                PROTOCOL_VERSION,
                KIND_JPEG_TILE,
                0x01, 0x02,
                0x03, 0x04,
                0x05, 0x06,
                0x07, 0x08,
                0x09, 0x0A, 0x0B, 0x0C,
                0xFF, 0xD8,
            ]
        );
        assert_eq!(TILE_HEADER_BYTES, message.len() - 2);
    }

    #[test]
    fn parses_a_pointer_move() {
        assert_eq!(
            parse_viewer_input(r#"{"type":"pointer","action":"move","x":12.5,"y":34.25}"#),
            Some(ViewerInput::Pointer {
                action: PointerAction::Move,
                x: 12.5,
                y: 34.25,
                button: MouseButton::Left,
            })
        );
    }

    #[test]
    fn parses_a_right_button_press() {
        assert_eq!(
            parse_viewer_input(r#"{"type":"pointer","action":"down","x":1.0,"y":2.0,"button":"right"}"#),
            Some(ViewerInput::Pointer {
                action: PointerAction::Down,
                x: 1.0,
                y: 2.0,
                button: MouseButton::Right,
            })
        );
    }

    #[test]
    fn parses_a_scroll_with_only_one_axis() {
        assert_eq!(
            parse_viewer_input(r#"{"type":"scroll","x":1.0,"y":2.0,"deltaY":-3.0}"#),
            Some(ViewerInput::Scroll { x: 1.0, y: 2.0, delta_x: 0.0, delta_y: -3.0 })
        );
    }

    #[test]
    fn parses_a_key_event() {
        assert_eq!(
            parse_viewer_input(r#"{"type":"key","hid":458756,"down":true}"#),
            Some(ViewerInput::Key { hid: 458756, down: true })
        );
    }

    #[test]
    fn parses_a_partial_quality_request() {
        assert_eq!(
            parse_viewer_input(r#"{"type":"quality","jpegQuality":40}"#),
            Some(ViewerInput::Quality { max_width: None, jpeg_quality: Some(40), max_fps: None })
        );
    }

    #[test]
    fn malformed_input_is_ignored_rather_than_fatal() {
        // Every one of these would otherwise be a way for one odd message to freeze somebody's
        // screen with no explanation.
        assert_eq!(parse_viewer_input("}{"), None);
        assert_eq!(parse_viewer_input(r#"{"type":"pointer","action":"teleport","x":1.0,"y":2.0}"#), None);
        assert_eq!(parse_viewer_input(r#"{"type":"pointer","action":"move","y":2.0}"#), None);
        assert_eq!(parse_viewer_input(r#"{"type":"key","hid":4}"#), None);
        assert_eq!(parse_viewer_input(r#"{"type":"somethingelse"}"#), None);
    }
}
