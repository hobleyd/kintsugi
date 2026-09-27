//! Whether a dialog put up right now would be seen by anybody.
//!
//! The scheduler (`main::run_scheduler`) asks this before starting an automatic patch cycle, and
//! holds the cycle while the answer is no. The delay countdown runs on the wall clock from the
//! moment the confirmation dialog appears (see `dialogs::run_until`), so the dialog has to appear
//! to somebody: a prompt raised to a locked or blanked screen would spend the whole delay budget
//! before anyone knew it had been asked, and the person would come back to "patching will begin in
//! 5 minutes" without ever having been offered a delay. The macOS agent is where this was measured
//! (its `presence` module tells the story); on Linux the per-user process does not tick through a
//! suspend, so the case that remains is the lock screen a laptop resumes onto and a display the
//! session has blanked with the desk empty.
//!
//! Two signals, each read from the thing that owns it rather than guessed from the clock:
//!
//! - **logind's `LockedHint`** on this session, over the system bus. Every desktop's screen locker
//!   sets it (GNOME, KDE, and anything using `loginctl lock-session`), it is the same property on
//!   X11 and Wayland, and `session/auto` resolves to this user's display session from inside a
//!   systemd user unit, which is where this process runs. Asked through `busctl` rather than a
//!   D-Bus crate of our own: `busctl` ships with systemd, so it is present on exactly the hosts
//!   that have logind at all, and a host without either answers "nothing in the way" — the
//!   behaviour this agent had before the check existed.
//! - **DPMS on X11**, read over the display connection this process already has for everything
//!   else. Wayland has no equivalent a client may ask, so a blanked-but-unlocked Wayland screen
//!   reads as visible; every desktop this fleet runs locks when it blanks, which the first signal
//!   catches.
//!
//! Kept identical in intent in the other two agents; what each platform can actually read differs
//! and is described in each one's copy.

use std::process::Command;

use x11rb::protocol::dpms::{self, DPMSMode};

/// Why a dialog shown right now would go unseen, or `None` when the screen is unlocked and, on
/// X11, lit. The reason is worded for the log line the scheduler writes when it holds a cycle.
pub fn obstruction() -> Option<String> {
    if session_is_locked() {
        return Some("the screen is locked".to_string());
    }
    if x11_display_is_off() {
        return Some("the display is off".to_string());
    }
    None
}

/// logind's `LockedHint` for this user's session. `false` when it cannot be read, for the reason
/// the module docs give.
fn session_is_locked() -> bool {
    let output = Command::new("busctl")
        .args([
            "--system",
            "get-property",
            "org.freedesktop.login1",
            "/org/freedesktop/login1/session/auto",
            "org.freedesktop.login1.Session",
            "LockedHint",
        ])
        .output();

    match output {
        Ok(output) if output.status.success() => parse_busctl_bool(&String::from_utf8_lossy(&output.stdout)).unwrap_or(false),
        _ => false,
    }
}

/// Reads `busctl get-property`'s rendering of a boolean, which is the type signature followed by
/// the value: `b true`. Anything else is "unknown", which callers treat as "nothing in the way".
fn parse_busctl_bool(output: &str) -> Option<bool> {
    let mut words = output.split_whitespace();
    match (words.next(), words.next()) {
        (Some("b"), Some("true")) => Some(true),
        (Some("b"), Some("false")) => Some(false),
        _ => None,
    }
}

/// Whether the X server reports its display in any DPMS state but "on". Only meaningful with a
/// `DISPLAY`; under Xwayland the server always says "on", which is the harmless answer.
fn x11_display_is_off() -> bool {
    if std::env::var_os("DISPLAY").is_none() {
        return false;
    }
    let Ok((connection, _)) = x11rb::connect(None) else {
        return false;
    };
    let Ok(cookie) = dpms::info(&connection) else {
        return false;
    };
    match cookie.reply() {
        // `state` is whether DPMS is enabled at all; a disabled DPMS never turns the display off.
        Ok(info) => info.state && info.power_level != DPMSMode::ON,
        Err(_) => false,
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn parse_busctl_bool_reads_the_signature_and_value() {
        assert_eq!(parse_busctl_bool("b true\n"), Some(true));
        assert_eq!(parse_busctl_bool("b false\n"), Some(false));
    }

    /// An error, an empty answer, or a property of another type must read as unknown rather than
    /// as locked: holding every cycle on a host whose logind answers oddly would mean it never
    /// patches, which is the worse failure.
    #[test]
    fn parse_busctl_bool_treats_anything_else_as_unknown() {
        assert_eq!(parse_busctl_bool(""), None);
        assert_eq!(parse_busctl_bool("s \"locked\""), None);
        assert_eq!(parse_busctl_bool("Failed to get property LockedHint"), None);
    }
}
