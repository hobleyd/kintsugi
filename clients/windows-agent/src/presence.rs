//! Whether a dialog put up right now would be seen by anybody.
//!
//! The scheduler (`main::run_scheduler`) asks this before starting an automatic patch cycle, and
//! holds the cycle while the answer is no. The delay countdown runs on the wall clock from the
//! moment the confirmation dialog appears (see `dialogs::DEADLINE_POLL_MS`), so the dialog has to
//! appear to somebody: a prompt raised behind the lock screen or to a monitor the power plan has
//! switched off would spend the whole delay budget before anyone knew it had been asked, and the
//! person would come back to "patching will begin in 5 minutes" without ever having been offered
//! a delay. The macOS agent is where this was measured (its `presence` module tells the story);
//! on Windows the per-user process does not tick through a sleep, so the case that remains is the
//! lock screen Windows resumes onto and a display the power plan has turned off at an empty desk.
//!
//! Three signals, each read from the thing that owns it rather than guessed from the clock:
//!
//! - **The input desktop.** When the workstation is locked the interactive window station's input
//!   desktop is `Winlogon`, which a process in the user's session cannot open — so
//!   `OpenInputDesktop` failing is the lock screen (or a UAC prompt, which is over as soon as it
//!   is answered). The same test `remote_desktop` uses to follow Windows onto the secure desktop,
//!   read here for the opposite purpose.
//! - **The console display's power state**, from `GUID_CONSOLE_DISPLAY_STATE` notifications the
//!   notification-area window registers for (`tray_menu::run`). Windows sends the current state
//!   on registration and every change after, so the atomic here is always what the display is
//!   doing; it starts as "on" so a registration that failed reads as the behaviour this agent had
//!   before the check existed.
//! - **The screen saver**, which covers the display without turning it off.
//!
//! Kept identical in intent in the other two agents; what each platform can actually read differs
//! and is described in each one's copy.

//! The service half ([`logged_on_sessions`]) asks a different question, from session 0, before it
//! restarts the host: is anybody logged on anywhere? See [`LogonState`].

use std::sync::atomic::{AtomicU8, Ordering};

use windows_sys::Win32::Foundation::BOOL;
use windows_sys::Win32::System::StationsAndDesktops::{CloseDesktop, OpenInputDesktop, DESKTOP_READOBJECTS};
use windows_sys::Win32::UI::WindowsAndMessaging::{SystemParametersInfoW, SPI_GETSCREENSAVERRUNNING};

/// The `Data` byte of a `GUID_CONSOLE_DISPLAY_STATE` notification: 0 off, 1 on, 2 dimmed.
pub const DISPLAY_OFF: u8 = 0;
pub const DISPLAY_ON: u8 = 1;

static DISPLAY_STATE: AtomicU8 = AtomicU8::new(DISPLAY_ON);

/// Records what Windows last said the console display is doing. Called from the tray window's
/// procedure on every `PBT_POWERSETTINGCHANGE` for the display-state GUID.
pub fn record_display_state(state: u8) {
    DISPLAY_STATE.store(state, Ordering::SeqCst);
}

/// Why a dialog shown right now would go unseen, or `None` when the display is on, the
/// workstation is unlocked and no screen saver is up. The reason is worded for the log line the
/// scheduler writes when it holds a cycle.
pub fn obstruction() -> Option<String> {
    if DISPLAY_STATE.load(Ordering::SeqCst) == DISPLAY_OFF {
        return Some("the display is off".to_string());
    }
    if workstation_is_locked() {
        return Some("the workstation is locked".to_string());
    }
    if screen_saver_is_running() {
        return Some("the screen saver is running".to_string());
    }
    None
}

fn workstation_is_locked() -> bool {
    // SAFETY: documented. No inheritance, and the least access that still tells us whether the
    // desktop can be opened at all; a non-null handle is closed at once.
    unsafe {
        let desktop = OpenInputDesktop(0, 0, DESKTOP_READOBJECTS);
        if desktop.is_null() {
            return true;
        }
        CloseDesktop(desktop);
        false
    }
}

fn screen_saver_is_running() -> bool {
    let mut running: BOOL = 0;
    // SAFETY: SPI_GETSCREENSAVERRUNNING writes one BOOL through pvParam, which points at a live
    // local for the length of the call. A failed call leaves it zero, which reads as "not running".
    unsafe {
        SystemParametersInfoW(SPI_GETSCREENSAVERRUNNING, 0, (&mut running as *mut BOOL).cast(), 0);
    }
    running != 0
}

/// One interactive logon on this host, as the *service* sees it from outside every session.
///
/// [`obstruction`] above answers "would a dialog in *this* session be seen?" and only works from
/// inside a session (it opens that session's input desktop). The decision to restart the machine
/// needs a different question answered from session 0: "is there anybody logged on to this host,
/// in any session, who could lose unsaved work?" — because a restart closes every session's
/// applications, not only the session of whichever tray happened to pick up the forced run. On a
/// shared machine a switched-away user's tray would otherwise authorise a restart under somebody
/// else who is working at the console.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum LogonState {
    /// Connected and unlocked: a person is (or can be) looking at it, and Windows' own restart
    /// countdown will be shown to them.
    AtTheDesk,
    /// Logged on but locked, disconnected, or in a state the service cannot read. Nobody would see
    /// a countdown, and whatever is open there may be unsaved — so a restart is not safe.
    Away,
}

/// Reads one session's `WTSSessionInfoEx` into a [`LogonState`], or `None` for a session nobody is
/// logged on to (session 0, an RDP listener, the console before anyone signs in). Split out from
/// [`logged_on_sessions`] so the classification is pinned by tests rather than by a comment.
///
/// `lock_flags` is `WTSINFOEX_LEVEL1_W::SessionFlags`: 0 locked, 1 unlocked, anything else unknown.
/// Unknown is read as away — the safe side for a question whose wrong answer closes somebody's
/// unsaved work. (Windows 7 and Server 2008 R2 report the two flags inverted; neither is a
/// platform this agent supports.)
pub fn classify_session(session_id: u32, has_user: bool, connect_state: i32, lock_flags: i64) -> Option<LogonState> {
    use windows_sys::Win32::System::RemoteDesktop::WTSActive;

    if session_id == 0 || !has_user {
        return None;
    }
    if connect_state == WTSActive as i32 && lock_flags == 1 {
        Some(LogonState::AtTheDesk)
    } else {
        Some(LogonState::Away)
    }
}

/// Every interactive logon on this host, read by the service with `WTSEnumerateSessionsW` and
/// `WTSQuerySessionInformationW(WTSSessionInfoEx)`. An error means the answer is unknown, which
/// the caller (`queue::restart_decision`) treats as "somebody may be there".
pub fn logged_on_sessions() -> anyhow::Result<Vec<LogonState>> {
    use windows_sys::Win32::System::RemoteDesktop::{
        WTSEnumerateSessionsW, WTSFreeMemory, WTSQuerySessionInformationW, WTSSessionInfoEx, WTSINFOEXW,
        WTS_CURRENT_SERVER_HANDLE, WTS_SESSION_INFOW,
    };

    let mut sessions: *mut WTS_SESSION_INFOW = std::ptr::null_mut();
    let mut count: u32 = 0;
    // SAFETY: documented call; on success Windows allocates `count` entries at `sessions`, which
    // are read only within that bound and freed with WTSFreeMemory below.
    if unsafe { WTSEnumerateSessionsW(WTS_CURRENT_SERVER_HANDLE, 0, 1, &mut sessions, &mut count) } == 0 {
        anyhow::bail!("WTSEnumerateSessionsW failed: {}", std::io::Error::last_os_error());
    }

    let mut states = Vec::new();
    let mut failure = None;
    for index in 0..count as usize {
        // SAFETY: index < count, the length Windows reported for this allocation.
        let session_id = unsafe { (*sessions.add(index)).SessionId };
        if session_id == 0 {
            continue;
        }

        let mut buffer: windows_sys::core::PWSTR = std::ptr::null_mut();
        let mut bytes: u32 = 0;
        // SAFETY: documented call; on success `buffer` points at a WTSINFOEXW Windows allocated,
        // read only when it is at least that large and freed with WTSFreeMemory straight after.
        let queried = unsafe { WTSQuerySessionInformationW(WTS_CURRENT_SERVER_HANDLE, session_id, WTSSessionInfoEx, &mut buffer, &mut bytes) };
        if queried == 0 || buffer.is_null() || (bytes as usize) < std::mem::size_of::<WTSINFOEXW>() {
            // Any failure — even a session that logged off between the two calls — makes the whole
            // answer unknown rather than quietly one session short: the caller defers the restart,
            // and a deferred restart costs nothing but time.
            failure.get_or_insert_with(|| format!("WTSQuerySessionInformationW failed for session {session_id}: {}", std::io::Error::last_os_error()));
            if !buffer.is_null() {
                unsafe { WTSFreeMemory(buffer.cast()) };
            }
            continue;
        }

        let Some(info_ptr) = std::ptr::NonNull::new(buffer.cast::<WTSINFOEXW>()) else {
            continue;
        };
        // SAFETY: non-null (above) and Windows reported at least size_of::<WTSINFOEXW>() bytes, so
        // one whole struct is readable; copied out unaligned so nothing depends on the allocation's
        // alignment, and the buffer is freed straight after. Level 1 is the only level Windows
        // defines, so the union's one member is the one to read.
        let info: WTSINFOEXW = unsafe { std::ptr::read_unaligned(info_ptr.as_ptr()) };
        unsafe { WTSFreeMemory(buffer.cast()) };
        // SAFETY: reading the union's only defined member (Level 1), from a plain-data copy.
        let level1 = unsafe { info.Data.WTSInfoExLevel1 };
        let has_user = level1.UserName.first().is_some_and(|&c| c != 0);
        states.extend(classify_session(level1.SessionId, has_user, level1.SessionState as i32, level1.SessionFlags as i64));
    }
    // SAFETY: allocated by WTSEnumerateSessionsW above and not used again.
    unsafe { WTSFreeMemory(sessions.cast()) };

    match failure {
        Some(message) => Err(anyhow::anyhow!(message)),
        None => Ok(states),
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use windows_sys::Win32::System::RemoteDesktop::{WTSActive, WTSConnected, WTSDisconnected};

    #[test]
    fn an_unlocked_active_session_is_somebody_at_the_desk() {
        assert_eq!(classify_session(1, true, WTSActive as i32, 1), Some(LogonState::AtTheDesk));
    }

    /// The finding this exists for: a locked screen is somebody logged on with work open, not an
    /// empty desk.
    #[test]
    fn a_locked_or_disconnected_session_is_somebody_away_not_nobody() {
        assert_eq!(classify_session(1, true, WTSActive as i32, 0), Some(LogonState::Away), "locked");
        assert_eq!(classify_session(2, true, WTSDisconnected as i32, 1), Some(LogonState::Away), "switched away / RDP dropped");
        assert_eq!(classify_session(3, true, WTSActive as i32, -1), Some(LogonState::Away), "lock state unknown");
    }

    #[test]
    fn session_zero_and_sessions_without_a_user_are_nobody() {
        assert_eq!(classify_session(0, true, WTSActive as i32, 1), None, "services");
        assert_eq!(classify_session(1, false, WTSConnected as i32, -1), None, "the console before anyone signs in");
    }
}
