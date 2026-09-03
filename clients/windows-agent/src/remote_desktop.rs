//! Following Windows around its desktops, which is what makes a UAC prompt visible remotely.
//!
//! # The problem this solves
//!
//! A Windows *desktop* is a kernel object holding windows, and a thread can be attached to exactly
//! one at a time. A session normally has two that matter: `Default`, where everything the user runs
//! lives, and `Winlogon` — the **secure desktop** — which is where the UAC consent prompt and the
//! lock screen are drawn. Switching between them is what the screen flash is when a prompt appears.
//!
//! A thread attached to `Default` cannot see or type into `Winlogon`. So a remote session that never
//! re-attaches shows the last frame of `Default` and appears frozen for as long as the prompt is up,
//! and there is no way to answer it except at the machine. This module is what fixes that: the
//! capture and input thread asks the window station which desktop currently *has input* and attaches
//! to that one, re-checking as it goes.
//!
//! # Why this is the session helper's and nothing else's
//!
//! `OpenInputDesktop` on the secure desktop needs more than the logged-in user has, so the helper
//! runs as SYSTEM — and it has to run inside the user's *session*, because desktops belong to a
//! window station and window stations are per-session. A service in session 0 cannot attach to
//! session 1's desktops however privileged it is. That pair of constraints is the whole reason there
//! is a helper process at all rather than this living in the tray, which is medium integrity and
//! stuck on `Default`.
//!
//! # The one rule about which thread calls this
//!
//! **`SetThreadDesktop` fails if the calling thread owns any window or hook.** So the thread that
//! follows the input desktop must be window-free — which the capture and input path is, since
//! `GetDC(NULL)` and `SendInput` need no window of their own. Anything with a window (the consent
//! dialog, the session banner) gets its own thread and attaches once. The restriction is per-thread,
//! so other threads' windows are irrelevant.

#[cfg(windows)]
pub use platform::InputDesktop;

#[cfg(windows)]
mod platform {
    use std::ffi::c_void;
    use std::ptr;

    use anyhow::{anyhow, Result};
    use windows_sys::Win32::Foundation::{GetLastError, HANDLE};
    use windows_sys::Win32::System::StationsAndDesktops::{
        CloseDesktop, GetUserObjectInformationW, OpenInputDesktop, SetThreadDesktop, HDESK,
        UOI_NAME,
    };

    use crate::logging;

    /// `DESKTOP_READOBJECTS | DESKTOP_SWITCHDESKTOP | DESKTOP_WRITEOBJECTS | DESKTOP_ENUMERATE |
    /// DESKTOP_CREATEWINDOW | DESKTOP_CREATEMENU | DESKTOP_HOOKCONTROL | DESKTOP_JOURNALRECORD |
    /// DESKTOP_JOURNALPLAYBACK`, i.e. everything.
    ///
    /// Spelled numerically because windows-sys does not surface the `DESKTOP_*` constants, and
    /// asking for `GENERIC_ALL` on a desktop is not the same thing — the desktop-specific rights are
    /// what `SetThreadDesktop` and window creation actually check.
    const DESKTOP_ALL_ACCESS: u32 = 0x0000_01FF;

    /// The desktop this thread is currently attached to, and the machinery to keep it current.
    ///
    /// Holds the handle for as long as the thread is attached: closing a desktop a thread is still
    /// using is documented as undefined, and it is what makes a subsequent capture return black.
    pub struct InputDesktop {
        handle: HDESK,
        name: String,
    }

    impl InputDesktop {
        /// Attaches this thread to whichever desktop currently has input.
        pub fn attach() -> Result<Self> {
            // SAFETY: documented. The flags argument is zero (no inheritance), `inherit` false, and
            // the access mask is the desktop-specific "all" above.
            let handle = unsafe { OpenInputDesktop(0, 0, DESKTOP_ALL_ACCESS) };
            if handle.is_null() {
                // SAFETY: no preconditions.
                return Err(anyhow!("could not open the input desktop (error {})", unsafe { GetLastError() }));
            }

            // SAFETY: a live desktop handle this function owns; released below on the failure path
            // and in Drop otherwise.
            if unsafe { SetThreadDesktop(handle) } == 0 {
                // SAFETY: no preconditions.
                let error = unsafe { GetLastError() };
                // SAFETY: the handle is still ours and no thread is attached to it.
                unsafe { CloseDesktop(handle) };
                return Err(anyhow!(
                    "could not attach this thread to the input desktop (error {error}) — \
                     SetThreadDesktop refuses a thread that owns any window or hook, which is why \
                     the capture path deliberately creates none"
                ));
            }

            let name = desktop_name(handle).unwrap_or_else(|| "<unknown>".to_string());
            logging::info(&format!("attached to the \"{name}\" desktop"));

            Ok(Self { handle, name })
        }

        /// The desktop's own name — `Default` in ordinary use, `Winlogon` while a UAC prompt or the
        /// lock screen is up. Logged rather than acted on, but it is the one thing that makes a
        /// "the screen went blank" report diagnosable.
        pub fn name(&self) -> &str {
            &self.name
        }

        /// Re-attaches if Windows has switched desktops since the last check, returning true if it
        /// did.
        ///
        /// Cheap enough to call once per frame: `OpenInputDesktop` is a handle open against an
        /// object that already exists, and the common case compares two short strings and closes the
        /// new handle again.
        ///
        /// Compared by *name* rather than by handle value, because opening the same desktop twice
        /// yields two different handles — a handle comparison would report a switch on every call
        /// and re-attach constantly.
        pub fn follow(&mut self) -> Result<bool> {
            // SAFETY: as `attach`.
            let candidate = unsafe { OpenInputDesktop(0, 0, DESKTOP_ALL_ACCESS) };
            if candidate.is_null() {
                // Not an error worth failing a session over: this happens transiently while Windows
                // is switching desktops, which is exactly when it is called.
                return Ok(false);
            }

            let candidate_name = desktop_name(candidate);

            if candidate_name.as_deref() == Some(self.name.as_str()) {
                // SAFETY: opened just above, and no thread is attached to it.
                unsafe { CloseDesktop(candidate) };
                return Ok(false);
            }

            // SAFETY: a live desktop handle. On success this thread is attached to it and the old
            // one can be released; on failure the old attachment is still in force.
            if unsafe { SetThreadDesktop(candidate) } == 0 {
                // SAFETY: opened above, not attached.
                unsafe { CloseDesktop(candidate) };
                return Ok(false);
            }

            let previous = std::mem::replace(&mut self.handle, candidate);
            // SAFETY: nothing is attached to the previous desktop any more.
            unsafe { CloseDesktop(previous) };

            let name = candidate_name.unwrap_or_else(|| "<unknown>".to_string());
            logging::info(&format!("Windows switched desktops: \"{}\" -> \"{name}\"", self.name));
            self.name = name;

            Ok(true)
        }
    }

    impl Drop for InputDesktop {
        fn drop(&mut self) {
            // The thread stays attached to whatever it was; only the handle is released. Detaching
            // to a desktop that may itself be going away would be the more dangerous move, and this
            // type only ever lives for the length of a session on a thread that then ends.
            if !self.handle.is_null() {
                // SAFETY: opened by this type and no longer referenced.
                unsafe { CloseDesktop(self.handle) };
                self.handle = ptr::null_mut();
            }
        }
    }

    /// Reads a desktop's name, or `None` if it cannot be determined.
    fn desktop_name(handle: HDESK) -> Option<String> {
        let mut buffer = [0u16; 256];
        let mut needed: u32 = 0;

        // SAFETY: a live desktop handle, a correctly-sized buffer, and its byte length passed as
        // documented. UOI_NAME writes a NUL-terminated wide string.
        let read = unsafe {
            GetUserObjectInformationW(
                handle as HANDLE,
                UOI_NAME as i32,
                buffer.as_mut_ptr() as *mut c_void,
                std::mem::size_of_val(&buffer) as u32,
                &mut needed,
            )
        };

        if read == 0 {
            return None;
        }

        let length = buffer.iter().position(|unit| *unit == 0).unwrap_or(buffer.len());
        Some(String::from_utf16_lossy(&buffer[..length]))
    }
}

/// Whether a desktop name is the secure one.
///
/// `Winlogon` is where the UAC consent prompt and the lock screen are drawn. Compared
/// case-insensitively because the name comes back from the window station rather than from a
/// constant, and nothing documents its casing as stable.
pub fn is_secure_desktop(name: &str) -> bool {
    name.eq_ignore_ascii_case("Winlogon")
}

/// A sentence for the administrator when the session is on the secure desktop.
///
/// Worth saying rather than leaving them to guess: the picture is genuinely live, but everything on
/// it belongs to Windows rather than to the user's applications, and anything they type goes to a
/// consent prompt.
pub fn secure_desktop_notice() -> &'static str {
    "Windows is showing a security prompt. The screen and your keyboard are still connected, and \
     what you type goes to that prompt."
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn the_secure_desktop_is_recognised_whatever_its_casing() {
        // The name is read back from the window station, and nothing documents its casing as stable.
        assert!(is_secure_desktop("Winlogon"));
        assert!(is_secure_desktop("winlogon"));
        assert!(is_secure_desktop("WINLOGON"));
    }

    #[test]
    fn the_ordinary_desktop_is_not_the_secure_one() {
        assert!(!is_secure_desktop("Default"));
        assert!(!is_secure_desktop("Screen-saver"));
        assert!(!is_secure_desktop(""));
    }

    #[test]
    fn the_secure_desktop_notice_says_the_session_is_still_live() {
        // The failure this replaces is an administrator concluding the session broke, so the wording
        // has to say the opposite explicitly.
        let notice = secure_desktop_notice();

        assert!(notice.contains("security prompt"), "{notice}");
        assert!(notice.contains("still connected"), "{notice}");
    }
}
