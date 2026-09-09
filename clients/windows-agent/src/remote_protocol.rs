//! The two wire protocols remote control uses, and the only place either is described on this side.
//!
//! **A copy of the macOS agent's `remote_protocol.rs`, deliberately, and it must not diverge.** The
//! viewer is the same browser talking the same protocol whatever the host is, so all three agents
//! are kept identical here the same way their module names, orderings and comments are kept
//! identical everywhere else. If one changes, the others and
//! `web/lib/data/models/remote_control_mapper.dart` change with it.
//!
//! `diff` the three and exactly one line differs outside this module comment: the cross-reference on
//! `ViewerInput::Key`, which names `input_injection::scan_code_for_hid` here,
//! `xtest_keycode_for_hid` on Linux and `virtual_key_for_hid` on macOS. That is not drift — the three
//! platforms reach the same positional key through differently-named APIs — but anything *else*
//! showing up in that diff is.
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
//! As on Linux, the two halves of this protocol are spoken by *different processes*: the control
//! protocol is the service's, because only the service holds this host's identity, and the media
//! protocol is the SYSTEM session helper's, because only a process inside the user's session can
//! capture a screen or post input. `remote_ipc` is what joins them.
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
    /// A shell session, which opens without asking anyone. This is not a consent — nobody at the
    /// host was consulted, by design — and the server refuses it for any other session kind, so a
    /// screen session can never be started by an agent claiming it needed no permission.
    NotRequired,
    /// The agent is connected but cannot provide this kind of session right now: a screen session
    /// on a host where nobody is logged in (so there is no desktop to capture and nobody to ask),
    /// or a kind this build has never heard of. Reported instead of `TimedOut` because the
    /// administrator should hear "there is no one there" at once rather than after 90 seconds.
    Unavailable,
}

/// Which kind of session the server is asking for. `Screen` is remote control as it has always
/// been — consent, capture, input. `Shell` is an interactive terminal: no dialog, no desktop needed,
/// the process holding the fleet identity spawns a PTY and pumps its bytes. `Unknown` is a kind this
/// build does not implement, answered with [`ConsentOutcome::Unavailable`] rather than ignored so the
/// administrator is told rather than left waiting.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum SessionKind {
    Screen,
    Shell,
    Unknown,
}

/// Server to agent. Deliberately not a `#[serde(tag = "type")]` enum: serde fails the whole parse
/// on a tag it does not know, and a newer server sending a message this build has never heard of
/// must not take the socket down. See [`parse_server_message`].
#[derive(Debug, Clone, PartialEq, Eq)]
pub enum ServerMessage {
    /// Put the consent dialog up (a screen session), or open a shell (no dialog).
    SessionRequested {
        session_id: String,
        kind: SessionKind,
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
        kind: Option<String>,
        reason: Option<String>,
    }

    let envelope: Envelope = serde_json::from_str(json)?;

    Ok(match envelope.message_type.as_str() {
        "session-requested" => envelope.session_id.map(|session_id| ServerMessage::SessionRequested {
            session_id,
            // Absent means a screen session: that is what every server before shell sessions
            // existed asked for, and the only thing it could have meant.
            kind: match envelope.kind.as_deref() {
                None | Some("screen") => SessionKind::Screen,
                Some("shell") => SessionKind::Shell,
                Some(_) => SessionKind::Unknown,
            },
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

/// A rectangle of the screen, JPEG-encoded. Agent to browser.
pub const KIND_JPEG_TILE: u8 = 1;

/// Bytes the shell wrote to its terminal. Agent to browser.
pub const KIND_SHELL_OUTPUT: u8 = 2;

/// Bytes typed (or pasted) into the terminal. Browser to agent — the only binary message that
/// travels in that direction.
pub const KIND_SHELL_INPUT: u8 = 3;

/// `version, kind, x, y, width, height, sequence` — see [`encode_tile`].
pub const TILE_HEADER_BYTES: usize = 14;

/// `version, kind` — see [`encode_shell_output`] and [`decode_shell_input`]. A shell frame carries
/// raw terminal bytes and needs no geometry, but it keeps the same two leading bytes every binary
/// message in this protocol has, so a frame of the wrong kind is refused rather than drawn or typed.
pub const SHELL_FRAME_HEADER_BYTES: usize = 2;

/// One display the host could show, as offered to the viewer's picker.
///
/// [`id`](Self::id) is **opaque to the viewer** and means whatever the agent that produced it says:
/// a `CGDirectDisplayID` on macOS, an index into Windows' monitor enumeration, a RandR monitor (or
/// zero for the whole virtual screen) on Linux. The viewer never interprets one — it echoes it back
/// in [`ViewerInput::SelectDisplay`] — so making the three agree would be one more thing to keep in
/// step for no reader's benefit.
#[derive(Debug, Clone, PartialEq, Eq, Serialize)]
pub struct DisplayOption {
    pub id: u32,

    /// What the picker shows. Built by the agent rather than composed by the viewer from the size,
    /// because only the host knows what its displays are called — and a label built from a size has
    /// nothing to say about the two identical monitors that are the common office desk.
    pub label: String,

    pub width: u32,
    pub height: u32,

    /// Whether this is the host's primary display: the one a session captures when the viewer has
    /// asked for nothing in particular.
    #[serde(rename = "isPrimary")]
    pub is_primary: bool,
}

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

    /// Every display this session could show, and which of them it is showing.
    ///
    /// **An empty list means "offer no picker", not "this host has no screen".** An agent from
    /// before display switching existed sends neither field, and a host with one display sends one
    /// entry — so the viewer offers the choice only where there is one, and a laptop gets no control
    /// it cannot use.
    ///
    /// A switch is announced by resending this whole message. The *size* may be unchanged when it
    /// happens — two identical monitors is the common case — so
    /// [`active_display_id`](Self::active_display_id) is what says the picture is now of somewhere
    /// else.
    pub displays: Vec<DisplayOption>,

    /// The [`DisplayOption::id`] these tiles are of. Zero when [`displays`](Self::displays) is
    /// empty, which is what an agent that cannot switch reports.
    #[serde(rename = "activeDisplayId")]
    pub active_display_id: u32,
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
            displays: Vec::new(),
            active_display_id: 0,
        }
    }

    /// Names the displays this session could show and which one it is showing.
    ///
    /// Chained onto one of the constructors above rather than folded into them, because the two
    /// facts are discovered in different places: the geometry comes off the capture that is running,
    /// the list off the platform's display enumeration. It is also what keeps
    /// `screen_capture::DisplayGeometry` `Copy` on all three agents — a `Vec` on that struct would
    /// not be, and the session loops hold it by value.
    pub fn showing(mut self, displays: Vec<DisplayOption>, active_display_id: u32) -> Self {
        self.displays = displays;
        self.active_display_id = active_display_id;
        self
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

/// Frames terminal output for the wire. The bytes are whatever the PTY produced — UTF-8 in practice,
/// but a frame may end mid-codepoint, and the viewer decodes them as a stream for that reason.
pub fn encode_shell_output(bytes: &[u8]) -> Vec<u8> {
    let mut message = Vec::with_capacity(SHELL_FRAME_HEADER_BYTES + bytes.len());
    message.push(PROTOCOL_VERSION);
    message.push(KIND_SHELL_OUTPUT);
    message.extend_from_slice(bytes);
    message
}

/// The bytes to write to the PTY, or `None` for a binary frame that is not shell input. Never a
/// panic: this is the one place the agent reads binary data from a browser.
pub fn decode_shell_input(message: &[u8]) -> Option<&[u8]> {
    match message {
        [PROTOCOL_VERSION, KIND_SHELL_INPUT, input @ ..] => Some(input),
        _ => None,
    }
}

/// Sent as a text message once, before the first output frame, so the viewer can say what it is
/// connected to. The shell analogue of [`DisplayInfo`].
#[derive(Debug, Clone, PartialEq, Serialize)]
pub struct ShellInfo {
    #[serde(rename = "type")]
    pub message_type: &'static str,
    /// The program running — `/bin/zsh`, `/bin/bash`, `powershell.exe`.
    pub shell: String,
    /// The account it runs as: `root` on macOS and Linux, `SYSTEM` on Windows. Stated rather than
    /// left to be inferred, because an administrator about to type a command should not have to
    /// remember which — and because a host that ever answered with a *user* account would be worth
    /// noticing rather than being reported as if it were the same thing.
    pub user: String,
}

impl ShellInfo {
    pub fn new(shell: impl Into<String>, user: impl Into<String>) -> Self {
        Self {
            message_type: "shell",
            shell: shell.into(),
            user: user.into(),
        }
    }
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
    /// physical key, not the character it would produce. See `input_injection::scan_code_for_hid`
    /// for why the physical key is the right thing to send.
    Key { hid: u32, down: bool },
    /// The viewer asking for a different trade between picture and bandwidth.
    Quality {
        max_width: Option<u32>,
        jpeg_quality: Option<u8>,
        max_fps: Option<u8>,
    },
    /// The viewer asking to watch a different display, naming one of the [`DisplayOption::id`]s the
    /// agent offered. The agent restarts capture there and resends [`DisplayInfo`] before the first
    /// tile of the new display.
    SelectDisplay { id: u32 },

    /// The terminal in the browser changed size; the PTY's window size follows so the shell
    /// re-wraps. Shell sessions only.
    Resize { cols: u16, rows: u16 },
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
        cols: Option<u16>,
        rows: Option<u16>,
        id: Option<u32>,
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

        // "select-display", not "display": `DisplayInfo`'s own `type` is already "display", and one
        // string meaning two unrelated messages in two different parsers is exactly the coincidence
        // that survives a review and then wastes an afternoon of somebody reading a session log.
        "select-display" => Some(ViewerInput::SelectDisplay { id: envelope.id? }),

        "resize" => {
            let (cols, rows) = (envelope.cols?, envelope.rows?);
            // A zero-sized terminal is a viewer that has not laid out yet, and a PTY told it is zero
            // columns wide makes some shells loop redrawing.
            (cols > 0 && rows > 0).then_some(ViewerInput::Resize { cols, rows })
        }

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
                kind: SessionKind::Screen,
                requested_by: "admin@example.com".to_string(),
                consent_timeout_seconds: 90,
            }
        );
    }

    #[test]
    fn reads_the_session_kind_and_defaults_it_to_a_screen() {
        // A server from before shell sessions sends no kind at all, and only ever meant a screen.
        let kind_of = |json: &str| match parse_server_message(json).unwrap().unwrap() {
            ServerMessage::SessionRequested { kind, .. } => kind,
            other => panic!("expected a session request, got {other:?}"),
        };

        assert_eq!(kind_of(r#"{"type":"session-requested","sessionId":"a"}"#), SessionKind::Screen);
        assert_eq!(kind_of(r#"{"type":"session-requested","sessionId":"a","kind":"screen"}"#), SessionKind::Screen);
        assert_eq!(kind_of(r#"{"type":"session-requested","sessionId":"a","kind":"shell"}"#), SessionKind::Shell);
        // Not None: an unknown kind gets answered Unavailable, so it has to reach the caller.
        assert_eq!(kind_of(r#"{"type":"session-requested","sessionId":"a","kind":"clipboard"}"#), SessionKind::Unknown);
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
    fn the_two_new_outcomes_serialise_to_the_server_enum_names_too() {
        // Same trap as above, for the two values the server added with shell sessions.
        let json_for = |outcome| {
            serde_json::to_string(&AgentMessage::Consent { session_id: "abc".to_string(), outcome }).unwrap()
        };

        assert!(json_for(ConsentOutcome::NotRequired).contains(r#""outcome":"NotRequired""#));
        assert!(json_for(ConsentOutcome::Unavailable).contains(r#""outcome":"Unavailable""#));
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
    fn display_info_offers_no_picker_until_it_is_told_about_the_displays() {
        // An empty list is what an agent that cannot switch reports, and the viewer reads it as
        // "offer nothing" rather than as "this host has no screen". Both constructors must start
        // there, or a backend that forgets to call `showing` would offer a picker naming nothing.
        assert!(DisplayInfo::new(1.0, 2.0, 1, 2).displays.is_empty());
        assert_eq!(DisplayInfo::new(1.0, 2.0, 1, 2).active_display_id, 0);
        assert!(DisplayInfo::with_input(1.0, 2.0, 1, 2, false).displays.is_empty());
    }

    #[test]
    fn display_info_names_the_displays_under_the_names_the_viewer_reads() {
        // The viewer keys on these exact names and the server relays the message without parsing it,
        // so a rename here is a picker that never appears — with nothing anywhere reporting it.
        let json = serde_json::to_string(
            &DisplayInfo::new(2560.0, 1440.0, 1280, 720).showing(
                vec![
                    DisplayOption {
                        id: 1,
                        label: "Built-in Retina Display (2560 x 1440)".to_string(),
                        width: 2560,
                        height: 1440,
                        is_primary: true,
                    },
                    DisplayOption {
                        id: 7,
                        label: "Display 2 (1920 x 1080)".to_string(),
                        width: 1920,
                        height: 1080,
                        is_primary: false,
                    },
                ],
                7,
            ),
        )
        .unwrap();

        assert!(json.contains(r#""activeDisplayId":7"#), "{json}");
        assert!(json.contains(r#""isPrimary":true"#), "{json}");
        assert!(json.contains(r#""label":"Display 2 (1920 x 1080)""#), "{json}");
        assert!(json.contains(r#""displays":[{"id":1"#), "{json}");
    }

    #[test]
    fn parses_a_display_selection() {
        assert_eq!(
            parse_viewer_input(r#"{"type":"select-display","id":7}"#),
            Some(ViewerInput::SelectDisplay { id: 7 })
        );
        // Nothing to switch to. Ignored rather than guessed at: the alternative is a session that
        // silently changes to the primary display because one message arrived malformed.
        assert_eq!(parse_viewer_input(r#"{"type":"select-display"}"#), None);
    }

    #[test]
    fn a_display_selection_is_not_spelled_the_same_as_the_geometry_message() {
        // The two travel in opposite directions through two different parsers, and sharing "display"
        // between them would make a session log ambiguous about which end sent what. This asserts
        // the viewer's own `type` is not accepted as a selection.
        assert_eq!(parse_viewer_input(r#"{"type":"display","id":7}"#), None);
        assert_eq!(DisplayInfo::new(1.0, 1.0, 1, 1).message_type, "display");
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
    fn frames_shell_output_behind_the_shared_two_byte_header() {
        // Mirrored by web/test/data/remote_control_mapper_test.dart, which asserts these exact bytes.
        assert_eq!(encode_shell_output(b"$ "), vec![PROTOCOL_VERSION, KIND_SHELL_OUTPUT, b'$', b' ']);
        assert_eq!(encode_shell_output(b"").len(), SHELL_FRAME_HEADER_BYTES);
    }

    #[test]
    fn decodes_shell_input_and_refuses_every_other_binary_frame() {
        assert_eq!(decode_shell_input(&[PROTOCOL_VERSION, KIND_SHELL_INPUT, b'l', b's', b'\n']), Some(&b"ls\n"[..]));
        assert_eq!(decode_shell_input(&[PROTOCOL_VERSION, KIND_SHELL_INPUT]), Some(&b""[..]));
        // A tile, a shell *output* frame echoed back, a stale version, and nothing at all — none of
        // these may reach the PTY.
        assert_eq!(decode_shell_input(&encode_tile(0, 0, 1, 1, 0, &[0xFF])), None);
        assert_eq!(decode_shell_input(&encode_shell_output(b"x")), None);
        assert_eq!(decode_shell_input(&[PROTOCOL_VERSION + 1, KIND_SHELL_INPUT, b'x']), None);
        assert_eq!(decode_shell_input(&[]), None);
    }

    #[test]
    fn shell_info_names_the_shell_and_the_account_under_the_names_the_viewer_reads() {
        assert_eq!(
            serde_json::to_string(&ShellInfo::new("/bin/zsh", "david")).unwrap(),
            r#"{"type":"shell","shell":"/bin/zsh","user":"david"}"#
        );
    }

    #[test]
    fn parses_a_resize_and_refuses_a_zero_sized_one() {
        assert_eq!(
            parse_viewer_input(r#"{"type":"resize","cols":120,"rows":40}"#),
            Some(ViewerInput::Resize { cols: 120, rows: 40 })
        );
        assert_eq!(parse_viewer_input(r#"{"type":"resize","cols":0,"rows":40}"#), None);
        assert_eq!(parse_viewer_input(r#"{"type":"resize","cols":80}"#), None);
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
