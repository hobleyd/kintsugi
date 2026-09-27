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
