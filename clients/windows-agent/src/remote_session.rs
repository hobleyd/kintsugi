//! The tray process's half of remote control: asking the user, capturing the screen, posting the
//! input.
//!
//! # Why this half exists
//!
//! Everything here needs the logged-in user's desktop, and the service — which holds this host's
//! identity and therefore the sockets — cannot reach it: session 0 isolation means a Windows service
//! cannot see, capture or type into a user's session whatever privileges it has. So this process
//! does the three things that touch the desktop and nothing that touches the network. See
//! `remote_ipc` for the boundary and why it is drawn there.
//!
//! The macOS agent has all of this in one process because there the per-user half holds the
//! identity. Reading the two side by side, this file is that agent's `remote_control.rs` with the
//! socket replaced by a pipe and the consent/capture/input parts unchanged in shape.
//!
//! # Only the console session runs this
//!
//! Every logged-in user gets a tray process, and only one of them is sitting at the screen an
//! administrator can ask to see. Each checks whether it is the active console session before
//! connecting, which is what keeps the service side down to a single pipe instance and means fast
//! user switching hands remote control over without either side coordinating it.

use std::time::{Duration, Instant};

use anyhow::{Context, Result};

use crate::dialogs::{self, RemoteControlChoice};
use crate::input_injection::InputInjector;
use crate::logging;
use crate::remote_ipc::{self, FrameReader, IpcFrame, IpcMessage, PipeConnection};
use crate::remote_protocol::{parse_viewer_input, ConsentOutcome, ViewerInput};
use crate::screen_capture::{
    FrameEncoder, ScreenCapture, DEFAULT_JPEG_QUALITY, DEFAULT_MAX_FPS, DEFAULT_MAX_IMAGE_WIDTH,
};
use crate::tray_menu;

/// How long this loop waits between passes when there is nothing to do. Also the upper bound on how
/// long a keystroke waits in the pipe.
const POLL_INTERVAL: Duration = Duration::from_millis(10);

/// How long to wait before trying the pipe again — either because this is not the console session,
/// or because the service is not listening yet.
///
/// Five seconds rather than something snappier because both causes are long-lived: a user who is not
/// at the console will not become the console user in the next 100ms, and a service that is not up
/// is usually a service that is starting or stopped. The cost of the delay is that remote control
/// becomes available a few seconds after login, which nobody notices.
const RECONNECT_INTERVAL: Duration = Duration::from_secs(5);

/// Runs for the life of the tray process. Never returns.
pub fn run() {
    loop {
        if !remote_ipc::in_console_session() {
            std::thread::sleep(RECONNECT_INTERVAL);
            continue;
        }

        match PipeConnection::connect() {
            Ok(pipe) => {
                logging::info("connected to the service for remote control");
                if let Err(err) = serve(pipe) {
                    logging::warn(&format!("the remote control connection to the service ended: {err:#}"));
                }
                // Whatever happened, nothing is being watched now.
                tray_menu::report_remote_session(None);
            }
            Err(err) => {
                // At debug rather than warn: on a host where the service has not started yet this
                // is the ordinary case, once every five seconds, and it is not a fault.
                logging::info(&format!("waiting for the service's remote control pipe: {err:#}"));
            }
        }

        std::thread::sleep(RECONNECT_INTERVAL);
    }
}

/// Everything in flight for one running session.
struct ActiveSession {
    session_id: String,
    capture: ScreenCapture,
    injector: InputInjector,
    encoder: FrameEncoder,
    /// When the next frame is due. A deadline rather than a sleep, so the time spent capturing and
    /// encoding comes out of the interval instead of being added to it — otherwise the real frame
    /// rate is whatever is left after the work, which on a busy screen is half what was asked for.
    next_frame_at: Instant,
    started: Instant,
}

fn serve(mut pipe: PipeConnection) -> Result<()> {
    let mut reader = FrameReader::new();
    let mut active: Option<ActiveSession> = None;
    let frame_interval = Duration::from_millis(1000 / u64::from(DEFAULT_MAX_FPS).max(1));

    loop {
        let chunk = pipe.read_available().context("the service's pipe closed")?;
        if !chunk.is_empty() {
            reader.push(&chunk);
        }

        while let Some(frame) = reader.next_frame()? {
            handle(frame, &mut pipe, &mut active)?;
        }

        // The person at the keyboard pressing "End Remote Session". Checked every pass so it is
        // honoured within a frame rather than at the next session event.
        if tray_menu::take_end_remote_session_request() {
            if let Some(session) = active.take() {
                logging::info("ending the remote control session at the console user's request");
                let session_id = session.session_id.clone();
                finish(session);
                write(
                    &mut pipe,
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

                // `None` is a frame to skip rather than a failure: a blit can fail transiently
                // across a desktop switch — the lock screen, a UAC prompt, fast user switching —
                // and the right response is to try again next tick, not to end somebody's session.
                if let Some(frame) = session.capture.capture() {
                    for tile in session.encoder.encode_changes(&frame) {
                        pipe.write_all(&remote_ipc::encode_tile(&tile))
                            .context("could not send a screen tile to the service")?;
                    }
                }
            }
        }

        std::thread::sleep(POLL_INTERVAL);
    }
}

fn handle(
    frame: IpcFrame,
    pipe: &mut PipeConnection,
    active: &mut Option<ActiveSession>,
) -> Result<()> {
    match frame {
        IpcFrame::Json(IpcMessage::SessionRequested { session_id, requested_by, consent_timeout_seconds }) => {
            if active.is_some() {
                // The server allows one session per host, so this should not happen. Refusing is the
                // answer that cannot go wrong, and it is reported rather than dropped so the
                // administrator gets an answer instead of a dialog that never appears.
                logging::warn("refusing a remote control request: a session is already running");
                return write(pipe, &IpcMessage::Consent { session_id, outcome: ConsentOutcome::Denied });
            }

            let outcome = ask(&requested_by, consent_timeout_seconds);
            write(pipe, &IpcMessage::Consent { session_id: session_id.clone(), outcome })?;

            if outcome != ConsentOutcome::Granted {
                return Ok(());
            }

            match start(&session_id, &requested_by) {
                Ok(session) => {
                    write(pipe, &IpcMessage::DisplayInfo {
                        json: serde_json::to_string(&session.capture.geometry.to_display_info())
                            .context("could not describe the display")?,
                    })?;
                    *active = Some(session);
                }
                Err(err) => {
                    // Consent was given and capture failed anyway. The administrator is told why
                    // rather than left watching a blank screen.
                    logging::error(&format!("could not start capturing this host's screen: {err:#}"));
                    write(pipe, &IpcMessage::EndedByHost { session_id, reason: format!("{err:#}") })?;
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
                other => session.injector.apply(&other),
            }

            Ok(())
        }

        IpcFrame::Json(IpcMessage::SessionEnded { session_id, reason }) => {
            logging::info(&format!("the service ended remote control session {session_id}: {reason}"));
            if let Some(session) = active.take() {
                finish(session);
            }
            Ok(())
        }

        // The tray-to-service variants, which never arrive in this direction, and tiles, which only
        // ever go the other way. Ignored rather than fatal so a version skew between the two halves
        // cannot take the connection down.
        other => {
            logging::warn(&format!("ignoring an unexpected message from the service: {other:?}"));
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
            // A dialog that could not be shown is not consent. Refused, and loudly, because the
            // administrator will otherwise see a refusal they cannot explain.
            logging::error(&format!("could not ask the console user for consent: {err:#}"));
            ConsentOutcome::Denied
        }
    }
}

/// What this session will not be able to do, in words the person deciding can act on.
///
/// Unlike macOS there is no permission to check — a process in the user's own session may capture
/// and inject freely — so this is a fixed statement rather than a probe. It is still said up front
/// rather than discovered mid-call, because "the mouse stopped working when the installer appeared"
/// is a confusing way to learn about User Interface Privilege Isolation.
fn restrictions() -> Vec<String> {
    vec![
        "Programs already running as an administrator cannot be clicked or typed into, and \
         Windows security prompts will freeze the picture until you answer them here."
            .to_string(),
    ]
}

fn start(session_id: &str, requested_by: &str) -> Result<ActiveSession> {
    let capture = ScreenCapture::start(DEFAULT_MAX_IMAGE_WIDTH)?;
    let geometry = capture.geometry;

    // Only now does anything appear in the notification area, because only now is anything actually
    // being watched.
    tray_menu::report_remote_session(Some(requested_by.to_string()));
    logging::info(&format!("remote control session {session_id} started for {requested_by}"));

    Ok(ActiveSession {
        session_id: session_id.to_string(),
        // The injector is given the *point* size, which on Windows is the same number as the screen
        // size — see DisplayGeometry. It must never be given the image size: that would scale every
        // click by however much the picture was shrunk for the link.
        injector: InputInjector::new(geometry.point_width, geometry.point_height),
        capture,
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
    session.injector.release_all();
    tray_menu::report_remote_session(None);

    logging::info(&format!(
        "remote control session {} ended after {}s",
        session.session_id,
        session.started.elapsed().as_secs()
    ));

    // `capture` releases its device contexts and its bitmap in Drop, which happens here.
}

fn write(pipe: &mut PipeConnection, message: &IpcMessage) -> Result<()> {
    pipe.write_all(&remote_ipc::encode_json(message)?)
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
    fn the_restrictions_name_both_things_windows_will_not_allow() {
        // Said before consent rather than discovered during the call — see `restrictions`.
        let listed = restrictions().join(" ");

        assert!(listed.contains("administrator"), "{listed}");
        assert!(listed.contains("security prompts"), "{listed}");
    }

    #[test]
    fn the_restrictions_are_never_empty_on_windows() {
        // Unlike macOS, where an empty list means both permissions are granted, there is always
        // something to say here — so a caller can rely on the dialog having a bullet list.
        assert!(!restrictions().is_empty());
    }
}
