//! The per-user process's half of remote control: asking the user, capturing the screen, posting the
//! input.
//!
//! # Why this half exists
//!
//! Everything here needs the graphical session's display and its authority cookie, which the root
//! side has neither of. And this process holds no identity and makes no network call at all — the
//! design that its 0.5.0 policy fetch violated, at the cost of a 403 once a minute on every Linux
//! host with a desktop. So it does the three things that touch the display and nothing that touches
//! the network, and `remote_ipc` carries the rest.
//!
//! Read side by side with `clients/windows-agent/src/remote_session.rs` and this is the same
//! program: only the reason the two halves are split differs (session 0 isolation there, display
//! authority here), and only the primitive joining them.
//!
//! # A host this cannot work on never connects at all
//!
//! If [`screen_capture::unavailable_reason`] has something to say — a Wayland session, or no display
//! — this process never opens the local socket. The root side therefore never opens its control
//! socket, and the server reports the host as unreachable, which the remote-control screen explains.
//!
//! That is deliberately the same mechanism as "nobody is logged in": reachability follows whether a
//! per-user process is connected, so there is exactly one way for a host to be unavailable rather
//! than a working session that produces a black screen. See the module note in `screen_capture` on
//! why a Wayland host would otherwise produce a plausible-looking wrong picture rather than an error.

use std::time::{Duration, Instant};

use anyhow::{Context, Result};

use crate::dialogs::{self, RemoteControlChoice};
use crate::backend::{self, Backend};
use crate::logging;
use crate::remote_ipc::{self, FrameReader, IpcConnection, IpcFrame, IpcMessage};
use crate::remote_protocol::{parse_viewer_input, ConsentOutcome, ViewerInput};
use crate::screen_capture::{
    FrameEncoder, DEFAULT_JPEG_QUALITY, DEFAULT_MAX_FPS, DEFAULT_MAX_IMAGE_WIDTH,
};
use crate::tray_menu;

/// How long this loop waits between passes when there is nothing to do. Also the upper bound on how
/// long a keystroke waits in the local socket.
const POLL_INTERVAL: Duration = Duration::from_millis(10);

/// How long to wait before trying the socket again when the root unit is not listening yet.
///
/// Five seconds rather than something snappier because that is a long-lived condition — a unit that
/// is not up is usually one that is starting or masked — and the cost is that remote control becomes
/// available a few seconds after login, which nobody notices.
const RECONNECT_INTERVAL: Duration = Duration::from_secs(5);

/// Runs for the life of the per-user process. Never returns.
pub fn run() {
    // Checked once, and returning here is permanent for this process. That is sound rather than
    // convenient, but only because of how narrow the reachable case is:
    //
    // - **No display at all** never gets here. `main::has_a_display` exits the whole process 0 when
    //   neither DISPLAY nor WAYLAND_DISPLAY is set, which is the case this agent already guards
    //   against for a process started before the desktop exported them.
    // - **A session type change** cannot happen under this process either: it means a new session,
    //   and the unit is `PartOf=graphical-session.target`, so systemd stops it with the old one.
    //   So an X11 host stays X11 and a Wayland host stays Wayland for as long as this runs.
    //
    // So the only way to reach this line is a session that genuinely cannot be captured for as long
    // as it exists, and re-checking on a timer would burn a wakeup to reach the same answer forever.
    if let Some(reason) = backend::unavailable_reason() {
        logging::info(&format!("remote control is not available on this session: {reason}"));
        return;
    }

    let socket_path = crate::config::remote_control_socket_path();

    loop {
        match IpcConnection::connect(&socket_path) {
            Ok(connection) => {
                logging::info("connected to the root agent for remote control");
                if let Err(err) = serve(connection) {
                    logging::warn(&format!("the remote control connection to the root agent ended: {err:#}"));
                }
                // Whatever happened, nothing is being watched now.
                tray_menu::report_remote_session(None);
            }
            Err(err) => {
                // On a host where the root unit has not started yet this is the ordinary case, once
                // every five seconds, and it is not a fault.
                logging::info(&format!("waiting for the root agent's remote control socket: {err:#}"));
            }
        }

        std::thread::sleep(RECONNECT_INTERVAL);
    }
}

/// Everything in flight for one running session.
struct ActiveSession {
    session_id: String,
    /// Capture and input together, because on Wayland they are one process — see `backend`.
    backend: Backend,
    encoder: FrameEncoder,
    /// When the next frame is due. A deadline rather than a sleep, so the time spent capturing and
    /// encoding comes out of the interval instead of being added to it — which matters more here
    /// than on the other two agents, because a whole-screen `GetImage` plus a software downscale is
    /// a bigger share of the interval than either of theirs.
    next_frame_at: Instant,
    started: Instant,
}

fn serve(mut ipc: IpcConnection) -> Result<()> {
    let mut reader = FrameReader::new();
    let mut active: Option<ActiveSession> = None;
    let frame_interval = Duration::from_millis(1000 / u64::from(DEFAULT_MAX_FPS).max(1));

    loop {
        let chunk = ipc.read_available().context("the root agent's socket closed")?;
        if !chunk.is_empty() {
            reader.push(&chunk);
        }

        while let Some(frame) = reader.next_frame()? {
            handle(frame, &mut ipc, &mut active)?;
        }

        // The person at the keyboard choosing "End Remote Session". Checked every pass so it is
        // honoured within a frame rather than at the next session event.
        if tray_menu::take_end_remote_session_request() {
            if let Some(session) = active.take() {
                logging::info("ending the remote control session at the console user's request");
                let session_id = session.session_id.clone();
                finish(session);
                write(
                    &mut ipc,
                    &IpcMessage::EndedByHost {
                        session_id,
                        reason: "the person at the keyboard ended the session".to_string(),
                    },
                )?;
            }
        }

        if let Some(session) = active.as_mut() {
            if Instant::now() >= session.next_frame_at {
                session.next_frame_at = Instant::now() + frame_interval;

                // `None` is a frame to skip rather than a failure: a `GetImage` can fail
                // transiently while the screen is being reconfigured, and on Wayland it simply means
                // no new frame has arrived from the helper since the last tick — a still desktop
                // produces none at all. The right response to both is to try again next tick.
                if let Some(frame) = session.backend.capture() {
                    for tile in session.encoder.encode_changes(&frame) {
                        ipc.write_all(&remote_ipc::encode_tile(&tile))
                            .context("could not send a screen tile to the root agent")?;
                    }
                }
            }
        }

        std::thread::sleep(POLL_INTERVAL);
    }
}

fn handle(
    frame: IpcFrame,
    ipc: &mut IpcConnection,
    active: &mut Option<ActiveSession>,
) -> Result<()> {
    match frame {
        IpcFrame::Json(IpcMessage::SessionRequested { session_id, requested_by, consent_timeout_seconds }) => {
            if active.is_some() {
                // The server allows one session per host, so this should not happen. Refusing is the
                // answer that cannot go wrong, and it is reported rather than dropped so the
                // administrator gets an answer instead of a dialog that never appears.
                logging::warn("refusing a remote control request: a session is already running");
                return write(ipc, &IpcMessage::Consent { session_id, outcome: ConsentOutcome::Denied });
            }

            let outcome = ask(&requested_by, consent_timeout_seconds);
            write(ipc, &IpcMessage::Consent { session_id: session_id.clone(), outcome })?;

            if outcome != ConsentOutcome::Granted {
                return Ok(());
            }

            match start(&session_id, &requested_by) {
                Ok(session) => {
                    // Sent only now, and only from a started session, because `can_control_input`
                    // is not knowable until the portal has answered. Announcing the geometry any
                    // earlier would mean claiming a Wayland host was drivable and then finding out
                    // it was not, with nothing on the wire able to correct it.
                    let geometry = session.backend.geometry();
                    let display = geometry.to_display_info(session.backend.can_control_input());
                    write(ipc, &IpcMessage::DisplayInfo {
                        json: serde_json::to_string(&display).context("could not describe the display")?,
                    })?;
                    *active = Some(session);
                }
                Err(err) => {
                    // Consent was given and capture or input failed anyway — an X server without
                    // XTEST reaches here, as does a Wayland compositor whose portal refused, or one
                    // whose permission dialog nobody answered. The administrator is told why rather
                    // than left watching a blank screen.
                    logging::error(&format!("could not start a remote control session: {err:#}"));
                    write(ipc, &IpcMessage::EndedByHost { session_id, reason: format!("{err:#}") })?;
                }
            }

            Ok(())
        }

        IpcFrame::Json(IpcMessage::ViewerInput { json }) => {
            let Some(session) = active.as_mut() else { return Ok(()) };
            let Some(input) = parse_viewer_input(&json) else { return Ok(()) };

            match input {
                // Handled here rather than in the injector, because it is about pictures rather than
                // input — and changing it forces a full frame, which only the encoder can do.
                ViewerInput::Quality { jpeg_quality, .. } => {
                    if let Some(quality) = jpeg_quality {
                        session.encoder.set_quality(quality);
                    }
                }
                other => session.backend.apply(&other),
            }

            Ok(())
        }

        IpcFrame::Json(IpcMessage::SessionEnded { session_id, reason }) => {
            logging::info(&format!("the root agent ended remote control session {session_id}: {reason}"));
            if let Some(session) = active.take() {
                finish(session);
            }
            Ok(())
        }

        // The per-user-to-root variants, which never arrive in this direction, and tiles, which only
        // ever go the other way. Ignored rather than fatal so a version skew between the two halves
        // cannot take the connection down.
        other => {
            logging::warn(&format!("ignoring an unexpected message from the root agent: {other:?}"));
            Ok(())
        }
    }
}

/// Asks the person at the keyboard, and turns their answer into the protocol's own vocabulary.
fn ask(requested_by: &str, consent_timeout_seconds: u64) -> ConsentOutcome {
    // Bounded below as well as taken from the server: a zero or absurdly small timeout would put a
    // dialog on screen and take it away again before anybody could read it, which would read as a
    // refusal nobody made.
    let timeout = consent_timeout_seconds.clamp(15, 120);

    match dialogs::confirm_remote_control(requested_by, &restrictions(), timeout) {
        Ok(RemoteControlChoice::Allow) => ConsentOutcome::Granted,
        Ok(RemoteControlChoice::Deny) => ConsentOutcome::Denied,
        Ok(RemoteControlChoice::TimedOut) => ConsentOutcome::TimedOut,
        Err(err) => {
            // A dialog that could not be shown is not consent — including the case this platform
            // has and the other two do not, where no dialog program is installed at all. Refused,
            // and loudly, because the administrator will otherwise see a refusal they cannot
            // explain.
            logging::error(&format!("could not ask the console user for consent: {err:#}"));
            ConsentOutcome::Denied
        }
    }
}

/// What this session will not be able to do, in words the person deciding can act on.
///
/// Empty on an ordinary X11 desktop, which is the difference from Windows: there is no UIPI here, so
/// XTEST reaches every window including one running as another user's `sudo` GUI. The list exists
/// because the dialog is shared with the other two agents and because a future restriction has
/// somewhere to go — not because there is currently anything to say.
///
/// Nothing about Wayland appears here: a Wayland session never gets this far, because
/// [`run`] refuses to connect at all. See its note on why that is better than a session that
/// produces a black screen.
fn restrictions() -> Vec<String> {
    Vec::new()
}

fn start(session_id: &str, requested_by: &str) -> Result<ActiveSession> {
    // Capture *and* input, both before the session is announced. On X11 this is the call that fails
    // on a server without XTEST; on Wayland it is where the portal is negotiated and where the
    // compositor's own dialog is answered. Either way a session that can be seen but not driven has
    // to be a *deliberate* view-only one, reported as such — not a half-started session with
    // nothing on screen explaining it.
    let backend = Backend::start(DEFAULT_MAX_IMAGE_WIDTH)?;

    // Only now does anything appear in the notification area, because only now is anything actually
    // being watched.
    tray_menu::report_remote_session(Some(requested_by.to_string()));
    logging::info(&format!("remote control session {session_id} started for {requested_by}"));

    Ok(ActiveSession {
        session_id: session_id.to_string(),
        backend,
        encoder: FrameEncoder::new(DEFAULT_JPEG_QUALITY),
        next_frame_at: Instant::now(),
        started: Instant::now(),
    })
}

/// Tears a session down.
///
/// Takes the session by value so there is no way to keep using one that has been finished, and so
/// `release_all` cannot be skipped: a session that ends while the remote user happens to be holding
/// Alt would otherwise leave this host's own user with Alt stuck down and nothing on screen
/// explaining why every keystroke has become a menu shortcut.
fn finish(mut session: ActiveSession) {
    session.backend.release_all();
    tray_menu::report_remote_session(None);

    logging::info(&format!(
        "remote control session {} ended after {}s",
        session.session_id,
        session.started.elapsed().as_secs()
    ));

    // The X connections close in Drop, which happens here — as does killing the Wayland helper,
    // which is what stops the compositor still capturing for a session that has ended.
}

fn write(ipc: &mut IpcConnection, message: &IpcMessage) -> Result<()> {
    ipc.write_all(&remote_ipc::encode_json(message)?)
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn the_frame_interval_divides_into_something_sensible() {
        // A zero here would be a divide-by-zero at startup; a large one would make the session feel
        // broken. Pinned because the constant lives in another module.
        let interval = Duration::from_millis(1000 / u64::from(DEFAULT_MAX_FPS).max(1));

        assert!(interval >= Duration::from_millis(20), "{interval:?}");
        assert!(interval <= Duration::from_millis(200), "{interval:?}");
    }

    #[test]
    fn the_frame_rate_is_lower_here_than_on_the_other_agents() {
        // Deliberate: a whole-screen GetImage plus a software downscale costs more per frame than
        // ScreenCaptureKit's push or a GDI blit. Pinned so a later "consistency" change does not
        // quietly triple the cost of watching an idle Linux desktop.
        assert!(DEFAULT_MAX_FPS < 12, "{DEFAULT_MAX_FPS}");
    }

    #[test]
    fn there_are_no_restrictions_to_report_on_an_x11_desktop() {
        // Unlike Windows, where UIPI and the secure desktop both have to be said out loud. If this
        // ever gains an entry, the dialog already renders it as a bullet list.
        assert!(restrictions().is_empty());
    }
}
