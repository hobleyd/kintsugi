//! The session helper: a SYSTEM process inside the logged-in session that asks the user, captures
//! the screen and posts the input.
//!
//! # Why this is a separate process, and why it is SYSTEM
//!
//! Two constraints meet here and neither can be worked around from anywhere else.
//!
//! **Desktops are per-session.** A UAC prompt and the lock screen are drawn on a different desktop
//! object (`Winlogon`) from everything the user runs (`Default`), and a thread can only be attached
//! to one. Desktops belong to a window station and window stations belong to a session, so the
//! service — which is in session 0 — cannot attach to session 1's desktops however privileged it is.
//! Something has to be *in* the session.
//!
//! **Integrity levels decide who may type.** `SendInput` cannot reach a window running at higher
//! integrity than the sender, and `OpenInputDesktop` on the secure desktop needs more than the
//! logged-in user has. A helper running as the *user* would be stuck exactly where the tray process
//! was: able to see an elevated installer and unable to click it.
//!
//! So the service launches this as SYSTEM into the console session (see
//! `remote_control::launch_session_helper`) and it lives only for the length of one session. The
//! result is that a remote operator can answer a UAC prompt, which is the single most common thing a
//! support session needs and the thing the first cut of this could not do.
//!
//! # What that buys on the security side, which is not obvious
//!
//! It is easy to read "SYSTEM process doing input injection" as a step backwards. It is the opposite,
//! for three reasons:
//!
//! - It exists **only while a session runs**, spawned by the one process holding this host's
//!   identity, and only after the server asked.
//! - The pipe between it and the service now has **no interactive user on either end**, so its ACL
//!   drops `IU` entirely — which removes the whole class of attack the tray design had to reason
//!   about, where a local process races to answer a consent request and feeds the operator a
//!   fabricated screen.
//! - The consent dialog is now a **SYSTEM-owned window**, and UIPI works in our favour: a
//!   medium-integrity process cannot send input to it, so user-level malware can neither click it
//!   nor suppress it.
//!
//! # Thread layout, which the desktop rules dictate
//!
//! `SetThreadDesktop` refuses a thread that owns any window, so the work is split by whether it
//! needs one:
//!
//! - **This thread** — pipe I/O, capture and input. Owns no window, so it can follow Windows onto
//!   the secure desktop and back (see `remote_desktop`).
//! - **The consent thread** — attaches to whichever desktop has input, shows the dialog, reports the
//!   answer and ends. Its own thread so the pipe keeps being read while the dialog is up, which is
//!   what lets an administrator who gives up cancel the request.
//! - **The banner thread** — the visible indicator. Inherits the process's startup desktop
//!   (`winsta0\default`) and stays there, so it is hidden while a UAC prompt is up and back
//!   afterwards. That is the right trade: during a prompt the prompt is what the user is looking at.

use std::sync::atomic::{AtomicBool, Ordering};
use std::sync::Arc;
use std::time::{Duration, Instant};

use anyhow::{Context, Result};

use crate::dialogs::{self, RemoteControlChoice};
use crate::input_injection::InputInjector;
use crate::logging;
use crate::remote_desktop::{self, InputDesktop};
use crate::remote_ipc::{self, FrameReader, IpcFrame, IpcMessage, PipeConnection};
use crate::remote_protocol::{parse_viewer_input, ConsentOutcome, ViewerInput};
use crate::screen_capture::{
    FrameEncoder, ScreenCapture, DEFAULT_JPEG_QUALITY, DEFAULT_MAX_FPS, DEFAULT_MAX_IMAGE_WIDTH,
};
use crate::session_banner;

/// How long this loop waits between passes when there is nothing to do. Also the upper bound on how
/// long a keystroke waits in the pipe.
const POLL_INTERVAL: Duration = Duration::from_millis(10);

/// How long to wait for the service's pipe on startup.
///
/// The service creates the pipe and *then* launches this process, so it is already listening — this
/// only covers process creation losing a race with a service that is shutting down.
const CONNECT_TIMEOUT: Duration = Duration::from_secs(10);
const CONNECT_RETRY_INTERVAL: Duration = Duration::from_millis(200);

/// How long to wait for a session request before giving up and exiting.
///
/// This process is launched *because* a request arrived, so the request should be the first thing on
/// the pipe. The timeout only stops an orphan helper living forever if the service died between
/// launching it and forwarding the request.
const REQUEST_TIMEOUT: Duration = Duration::from_secs(30);

/// Runs one session and returns. The process exits afterwards — see the module note on lifetime.
pub fn run() -> Result<()> {
    let mut ipc = connect()?;
    logging::info("session helper connected to the service");

    // This thread does capture and input, so it follows Windows between desktops. Attached before
    // anything else so a consent dialog on the lock screen is reachable.
    let mut desktop = InputDesktop::attach().context("could not attach to the session's input desktop")?;

    let mut reader = FrameReader::new();
    let deadline = Instant::now() + REQUEST_TIMEOUT;

    // 1. Wait for the request that caused this process to exist.
    let (session_id, requested_by, consent_timeout_seconds) = loop {
        if Instant::now() > deadline {
            return Err(anyhow::anyhow!("no session request arrived within {REQUEST_TIMEOUT:?}"));
        }

        let chunk = ipc.read_available().context("the service's pipe closed")?;
        if !chunk.is_empty() {
            reader.push(&chunk);
        }

        if let Some(IpcFrame::Json(IpcMessage::SessionRequested {
            session_id,
            requested_by,
            consent_timeout_seconds,
        })) = reader.next_frame()?
        {
            break (session_id, requested_by, consent_timeout_seconds);
        }

        std::thread::sleep(POLL_INTERVAL);
    };

    // 2. Ask, on its own thread so the pipe keeps being read while the dialog is up.
    let outcome = ask(&requested_by, consent_timeout_seconds, &mut ipc, &mut reader, &session_id)?;
    write(&mut ipc, &IpcMessage::Consent { session_id: session_id.clone(), outcome })?;

    if outcome != ConsentOutcome::Granted {
        logging::info(&format!("session {session_id} was not granted; the helper is exiting"));
        return Ok(());
    }

    // 3. Run it.
    serve(&mut ipc, &mut reader, &mut desktop, &session_id, &requested_by)
}

fn connect() -> Result<PipeConnection> {
    let deadline = Instant::now() + CONNECT_TIMEOUT;
    let mut last_error = None;

    while Instant::now() < deadline {
        match PipeConnection::connect() {
            Ok(connection) => return Ok(connection),
            Err(err) => last_error = Some(err),
        }
        std::thread::sleep(CONNECT_RETRY_INTERVAL);
    }

    Err(last_error.unwrap_or_else(|| anyhow::anyhow!("could not reach the service's remote control pipe")))
}

/// Shows the consent dialog on the input desktop while continuing to read the pipe.
///
/// The dialog blocks its thread for up to two minutes, and during that time the administrator may
/// give up — so the pipe has to keep being serviced or a cancellation would sit unread until after
/// the user had answered a question nobody was waiting on.
fn ask(
    requested_by: &str,
    consent_timeout_seconds: u64,
    ipc: &mut PipeConnection,
    reader: &mut FrameReader,
    session_id: &str,
) -> Result<ConsentOutcome> {
    // Bounded below as well as taken from the server: a zero or absurdly small timeout would put a
    // dialog on screen and take it away again before anybody could read it, which would read as a
    // refusal nobody made.
    let timeout = consent_timeout_seconds.clamp(15, 120);
    let requested_by = requested_by.to_string();
    let (answer_tx, answer_rx) = std::sync::mpsc::channel();

    std::thread::spawn(move || {
        // Its own attachment, because this thread is about to own a window and so can never follow
        // a desktop switch afterwards. Attaching to the *input* desktop rather than assuming
        // `Default` is what puts the dialog in front of somebody sitting at a locked screen.
        let _desktop = match InputDesktop::attach() {
            Ok(desktop) => desktop,
            Err(err) => {
                logging::error(&format!("could not show the consent dialog: {err:#}"));
                let _ = answer_tx.send(ConsentOutcome::Denied);
                return;
            }
        };

        let choice = dialogs::confirm_remote_control(&requested_by, &restrictions(), timeout);
        let _ = answer_tx.send(match choice {
            Ok(RemoteControlChoice::Allow) => ConsentOutcome::Granted,
            Ok(RemoteControlChoice::Deny) => ConsentOutcome::Denied,
            Ok(RemoteControlChoice::TimedOut) => ConsentOutcome::TimedOut,
            Err(err) => {
                // A dialog that could not be shown is not consent.
                logging::error(&format!("could not ask the console user for consent: {err:#}"));
                ConsentOutcome::Denied
            }
        });
    });

    loop {
        if let Ok(outcome) = answer_rx.try_recv() {
            return Ok(outcome);
        }

        let chunk = ipc.read_available().context("the service's pipe closed while asking for consent")?;
        if !chunk.is_empty() {
            reader.push(&chunk);
        }

        while let Some(frame) = reader.next_frame()? {
            if let IpcFrame::Json(IpcMessage::SessionEnded { session_id: ended, reason }) = frame {
                if ended == session_id {
                    // The administrator gave up. The dialog is still on screen and will time out on
                    // its own; reporting a refusal now is honest — nobody consented.
                    logging::info(&format!("session {ended} was withdrawn while the dialog was up: {reason}"));
                    return Ok(ConsentOutcome::Denied);
                }
            }
        }

        std::thread::sleep(POLL_INTERVAL);
    }
}

/// What this session will not be able to do.
///
/// **Much shorter than it was**, and that is the point of the helper: running as SYSTEM on the input
/// desktop, elevated windows and the UAC prompt both work now. What is left is the one thing no
/// process can do anything about.
fn restrictions() -> Vec<String> {
    vec![
        "While Windows shows a security prompt, only that prompt can be used remotely — \
         everything else on screen is unavailable until it is answered."
            .to_string(),
    ]
}

fn serve(
    ipc: &mut PipeConnection,
    reader: &mut FrameReader,
    desktop: &mut InputDesktop,
    session_id: &str,
    requested_by: &str,
) -> Result<()> {
    let mut capture = ScreenCapture::start(DEFAULT_MAX_IMAGE_WIDTH)?;
    let mut encoder = FrameEncoder::new(DEFAULT_JPEG_QUALITY);
    let mut injector = InputInjector::new(capture.geometry.point_width, capture.geometry.point_height);

    write(ipc, &IpcMessage::DisplayInfo {
        json: serde_json::to_string(&capture.geometry.to_display_info())
            .context("could not describe the display")?,
    })?;

    // The visible indicator, on its own thread with its own window — see the module note on why it
    // cannot be this thread and why it stays on the startup desktop.
    let ended_by_user = Arc::new(AtomicBool::new(false));
    let banner = session_banner::show(requested_by.to_string(), ended_by_user.clone());

    logging::info(&format!("session {session_id} started for {requested_by}"));

    let frame_interval = Duration::from_millis(1000 / u64::from(DEFAULT_MAX_FPS).max(1));
    let mut next_frame_at = Instant::now();
    let started = Instant::now();

    let reason = loop {
        if ended_by_user.load(Ordering::SeqCst) {
            break "the person at the keyboard ended the session".to_string();
        }

        let chunk = ipc.read_available().context("the service's pipe closed")?;
        if !chunk.is_empty() {
            reader.push(&chunk);
        }

        let mut ended = None;
        while let Some(frame) = reader.next_frame()? {
            match frame {
                IpcFrame::Json(IpcMessage::ViewerInput { json }) => {
                    if let Some(input) = parse_viewer_input(&json) {
                        match input {
                            ViewerInput::Quality { jpeg_quality, .. } => {
                                if let Some(quality) = jpeg_quality {
                                    encoder.set_quality(quality);
                                }
                            }
                            other => injector.apply(&other),
                        }
                    }
                }
                IpcFrame::Json(IpcMessage::SessionEnded { reason, .. }) => ended = Some(reason),
                other => logging::warn(&format!("ignoring an unexpected message from the service: {other:?}")),
            }
        }
        if let Some(reason) = ended {
            break reason;
        }

        if Instant::now() >= next_frame_at {
            next_frame_at = Instant::now() + frame_interval;

            // **The capture has to be rebuilt when Windows switches desktops.** Its device contexts
            // were created against the desktop this thread was attached to at the time; after
            // re-attaching, the old DC still refers to the old desktop and every frame from it is
            // stale. Rebuilding the encoder alongside it is what forces a full frame, since a fresh
            // `FrameEncoder` sends one by construction — the alternative would be diffing the
            // secure desktop against the last frame of the user's.
            if desktop.follow().unwrap_or(false) {
                match ScreenCapture::start(DEFAULT_MAX_IMAGE_WIDTH) {
                    Ok(rebuilt) => {
                        capture = rebuilt;
                        encoder = FrameEncoder::new(DEFAULT_JPEG_QUALITY);
                        injector = InputInjector::new(
                            capture.geometry.point_width,
                            capture.geometry.point_height,
                        );

                        write(ipc, &IpcMessage::DisplayInfo {
                            json: serde_json::to_string(&capture.geometry.to_display_info())
                                .context("could not describe the display")?,
                        })?;

                        if remote_desktop::is_secure_desktop(desktop.name()) {
                            logging::info(remote_desktop::secure_desktop_notice());
                        }
                    }
                    Err(err) => {
                        // The secure desktop occasionally refuses a capture while it is coming up.
                        // Skipping this frame and trying again is right; ending the session because
                        // a UAC prompt appeared would be the worst possible response.
                        logging::warn(&format!("could not capture the new desktop yet: {err:#}"));
                        continue;
                    }
                }
            }

            if let Some(frame) = capture.capture() {
                for tile in encoder.encode_changes(&frame) {
                    ipc.write_all(&remote_ipc::encode_tile(&tile))
                        .context("could not send a screen tile to the service")?;
                }
            }
        }

        std::thread::sleep(POLL_INTERVAL);
    };

    // Explicitly, before anything unwinds: a session that ends while the remote user happens to be
    // holding Alt must not leave this host's own user with Alt stuck down.
    injector.release_all();
    banner.close();

    logging::info(&format!(
        "session {session_id} ended after {}s: {reason}",
        started.elapsed().as_secs()
    ));

    write(ipc, &IpcMessage::EndedByHost { session_id: session_id.to_string(), reason })?;

    Ok(())
}

fn write(ipc: &mut PipeConnection, message: &IpcMessage) -> Result<()> {
    ipc.write_all(&remote_ipc::encode_json(message)?)
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn the_frame_interval_divides_into_something_sensible() {
        let interval = Duration::from_millis(1000 / u64::from(DEFAULT_MAX_FPS).max(1));

        assert!(interval >= Duration::from_millis(20), "{interval:?}");
        assert!(interval <= Duration::from_millis(200), "{interval:?}");
    }

    #[test]
    fn the_restrictions_no_longer_mention_elevated_windows() {
        // The whole point of the SYSTEM helper: UIPI and the elevated-window gap are gone, so the
        // dialog must stop claiming them. If this fails because somebody reinstated that wording,
        // check whether the helper is actually still running as SYSTEM.
        let listed = restrictions().join(" ");

        assert!(!listed.to_lowercase().contains("administrator"), "{listed}");
        assert!(listed.contains("security prompt"), "{listed}");
    }

    #[test]
    fn one_restriction_remains_because_no_process_can_fix_it() {
        // While the secure desktop has input, everything else is genuinely unreachable — that is
        // what a secure desktop is for. Saying so up front is the honest handling.
        assert_eq!(restrictions().len(), 1);
    }

    #[test]
    fn the_request_timeout_outlasts_the_pipe_connect_timeout() {
        // This process is launched because a request arrived, so the request should follow the
        // connection almost immediately. Inverting these would time out waiting for a request that
        // could not have arrived yet.
        assert!(REQUEST_TIMEOUT > CONNECT_TIMEOUT);
    }
}
