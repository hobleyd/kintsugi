//! Choosing between the X11 and Wayland ways of doing the same two things.
//!
//! # Two implementations, one shape
//!
//! Capture and input are one object here rather than two, and that is forced rather than tidy: on
//! Wayland they are one *process*. The portal will only position a pointer within a stream belonging
//! to the same session that produced it, so the thing that captures and the thing that injects
//! cannot be separated. X11 keeps two X connections internally for a latency reason of its own (see
//! `input_injection::InputInjector`), and that stays an implementation detail behind this.
//!
//! Everything downstream — `FrameEncoder`, the tiles, `remote_ipc`, the server, the viewer — is
//! unchanged by which of the two is running. The one thing that reaches the far end is
//! [`Backend::can_control_input`], because a Wayland host may be watchable and not drivable and the
//! operator has to be told.

use anyhow::Result;

use crate::input_injection::InputInjector;
use crate::remote_protocol::ViewerInput;
use crate::screen_capture::{self, DisplayGeometry, Frame, ScreenCapture};
use crate::wayland_backend::WaylandBackend;

/// Whether this is a Wayland session.
///
/// **Asked before anything asks about X11, and the order is the whole point.** A Wayland desktop
/// almost always runs Xwayland too, so `DISPLAY` is set and an X11 connection succeeds — it just
/// shows the Xwayland root window, which contains only whatever X11 applications happen to be
/// running and is usually a black rectangle. Testing X11 first therefore produces a plausible
/// picture of nothing rather than an error, which is the worst of the available outcomes: the
/// session connects, the administrator watches a blank screen, and nothing anywhere says why.
pub fn is_wayland_session() -> bool {
    if std::env::var_os("WAYLAND_DISPLAY").is_some() {
        return true;
    }

    std::env::var("XDG_SESSION_TYPE").is_ok_and(|kind| kind.eq_ignore_ascii_case("wayland"))
}

/// Why remote control cannot work on this session, or `None` if it can.
///
/// Returned as a sentence rather than a boolean because it ends up in front of a person: the
/// administrator sees it as the reason a session ended, and the wording has to be enough for them to
/// know whether to stop trying.
///
/// Note what this does *not* do on Wayland: it cannot tell whether the portal will grant anything,
/// because finding out means negotiating a session, which raises the compositor's own dialog. So a
/// Wayland host is reported as available here and may still fail at `start` — which is correct, and
/// is why that failure carries the portal's own words.
pub fn unavailable_reason() -> Option<String> {
    if is_wayland_session() {
        return crate::wayland_backend::unavailable_reason();
    }

    screen_capture::unavailable_reason()
}

/// The capture and input pair for whichever session this host is running.
pub enum Backend {
    X11 { capture: ScreenCapture, injector: InputInjector },
    Wayland(WaylandBackend),
}

impl Backend {
    pub fn start(max_image_width: u32) -> Result<Self> {
        if is_wayland_session() {
            return Ok(Self::Wayland(WaylandBackend::start(max_image_width)?));
        }

        // Capture first: it is the half that fails on a host with no display, and failing there
        // gives the better message. An injector that cannot open a connection on a host whose
        // capture just succeeded is a genuinely odd state and says so.
        let capture = ScreenCapture::start(max_image_width)?;
        let injector = InputInjector::new()?;

        Ok(Self::X11 { capture, injector })
    }

    pub fn geometry(&self) -> DisplayGeometry {
        match self {
            Self::X11 { capture, .. } => capture.geometry,
            Self::Wayland(wayland) => wayland.geometry(),
        }
    }

    /// Whether this host will accept keyboard and mouse, or can only be watched.
    ///
    /// Always true on X11: XTEST is either present, in which case input works, or absent, in which
    /// case `InputInjector::new` failed and there is no session at all.
    pub fn can_control_input(&self) -> bool {
        match self {
            Self::X11 { .. } => true,
            Self::Wayland(wayland) => wayland.can_control_input(),
        }
    }

    /// The newest frame, or `None` for a frame to skip.
    pub fn capture(&mut self) -> Option<Frame> {
        match self {
            Self::X11 { capture, .. } => capture.capture(),
            Self::Wayland(wayland) => wayland.capture(),
        }
    }

    pub fn apply(&mut self, input: &ViewerInput) {
        match self {
            Self::X11 { injector, .. } => injector.apply(input),
            Self::Wayland(wayland) => wayland.apply(input),
        }
    }

    /// Releases every key and button still held. Called on every path out of a session.
    pub fn release_all(&mut self) {
        match self {
            Self::X11 { injector, .. } => injector.release_all(),
            Self::Wayland(wayland) => wayland.release_all(),
        }
    }
}

#[cfg(test)]
mod tests {
    /// Sets the environment for one test and puts it back afterwards.
    ///
    /// Serialised through a mutex because the environment is process-wide and cargo runs tests in
    /// threads — two of these at once would each see the other's variables.
    fn with_session_environment(wayland_display: Option<&str>, session_type: Option<&str>, check: impl FnOnce()) {
        use std::sync::Mutex;
        static LOCK: Mutex<()> = Mutex::new(());
        let _guard = LOCK.lock().unwrap_or_else(|poisoned| poisoned.into_inner());

        let previous = (std::env::var_os("WAYLAND_DISPLAY"), std::env::var_os("XDG_SESSION_TYPE"));

        // SAFETY: the mutex above is the only thing in this crate's tests that touches these two
        // variables, so nothing else is reading them concurrently.
        unsafe {
            match wayland_display {
                Some(value) => std::env::set_var("WAYLAND_DISPLAY", value),
                None => std::env::remove_var("WAYLAND_DISPLAY"),
            }
            match session_type {
                Some(value) => std::env::set_var("XDG_SESSION_TYPE", value),
                None => std::env::remove_var("XDG_SESSION_TYPE"),
            }
        }

        check();

        // SAFETY: as above.
        unsafe {
            match &previous.0 {
                Some(value) => std::env::set_var("WAYLAND_DISPLAY", value),
                None => std::env::remove_var("WAYLAND_DISPLAY"),
            }
            match &previous.1 {
                Some(value) => std::env::set_var("XDG_SESSION_TYPE", value),
                None => std::env::remove_var("XDG_SESSION_TYPE"),
            }
        }
    }

    #[test]
    fn a_wayland_session_is_recognised_by_either_signal() {
        // Both, because neither is reliable alone: a systemd user unit gets XDG_SESSION_TYPE from
        // logind and may not inherit WAYLAND_DISPLAY, while a compositor started from a tty exports
        // WAYLAND_DISPLAY and leaves XDG_SESSION_TYPE saying "tty".
        with_session_environment(Some("wayland-0"), None, || {
            assert!(super::is_wayland_session());
        });
        with_session_environment(None, Some("wayland"), || {
            assert!(super::is_wayland_session());
        });
        with_session_environment(None, Some("Wayland"), || {
            assert!(super::is_wayland_session(), "the comparison should ignore case");
        });
    }

    #[test]
    fn an_x11_session_is_not_mistaken_for_a_wayland_one() {
        // The expensive direction to get wrong: a Wayland backend on an X11 host fails outright,
        // where an X11 backend on a Wayland host captures Xwayland's empty root window and looks
        // like it is working.
        with_session_environment(None, Some("x11"), || {
            assert!(!super::is_wayland_session());
        });
        with_session_environment(None, None, || {
            assert!(!super::is_wayland_session());
        });
    }
}
