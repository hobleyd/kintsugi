//! Launching the session helper into the logged-in session, and deciding whether there is one.
//!
//! # The recipe, and why it is not the obvious one
//!
//! The obvious call is `WTSQueryUserToken` followed by `CreateProcessAsUser`, which is how you start
//! a process *as the logged-in user* in their session. That is deliberately **not** what happens
//! here: a process running as the user is medium integrity, which is precisely the limitation the
//! helper exists to escape — it could not type into an elevated window or attach to the secure
//! desktop.
//!
//! So instead the service duplicates **its own** token — it is SYSTEM — and moves the copy into the
//! target session with `SetTokenInformation(TokenSessionId)`. The result is a SYSTEM process running
//! inside the user's session, which is what `remote_desktop` needs. Changing a token's session id
//! requires `SE_TCB_NAME`, which SYSTEM has and nothing else does; that privilege requirement is the
//! reason this can only be done from the service.
//!
//! `WTSQueryUserToken` is still used, but only as a *question*: it succeeds exactly when somebody is
//! logged into the console session, which is how reachability is decided.
//!
//! # Reachability moved here
//!
//! In the first cut, a host was reachable while a tray process held the pipe. With capture in a
//! helper that only exists during a session, that signal is gone — so the service asks Windows
//! directly instead. The semantics are unchanged and arguably sharper: reachable means *there is a
//! console session with somebody logged into it*, which is the same thing as "there is a screen to
//! share and somebody who can be asked".
//!
//! A locked screen still counts, and that is correct rather than an oversight: the session exists,
//! the consent dialog will appear on the lock screen, and if nobody is there to answer it the
//! request times out and is recorded as such.

#[cfg(windows)]
pub use platform::{console_session_with_user, SessionHelper};

#[cfg(windows)]
mod platform {
    use std::ffi::c_void;
    use std::ptr;

    use anyhow::{anyhow, Context, Result};
    use windows_sys::Win32::Foundation::{CloseHandle, GetLastError, HANDLE};
    use windows_sys::Win32::Security::{
        DuplicateTokenEx, SecurityImpersonation, SetTokenInformation, TokenPrimary, TokenSessionId,
        TOKEN_ASSIGN_PRIMARY, TOKEN_DUPLICATE, TOKEN_QUERY,
    };
    use windows_sys::Win32::System::RemoteDesktop::{WTSGetActiveConsoleSessionId, WTSQueryUserToken};
    use windows_sys::Win32::System::Threading::{
        CreateProcessAsUserW, GetCurrentProcess, OpenProcessToken, TerminateProcess,
        WaitForSingleObject, CREATE_NO_WINDOW, CREATE_UNICODE_ENVIRONMENT, PROCESS_INFORMATION,
        STARTUPINFOW,
    };

    use crate::config;
    use crate::logging;
    use crate::win32::wide;

    /// `MAXIMUM_ALLOWED`. Not surfaced by windows-sys, and the documented value to pass to
    /// `DuplicateTokenEx` when the duplicate should carry everything the original had.
    const MAXIMUM_ALLOWED: u32 = 0x0200_0000;

    /// The window station and desktop the helper starts on.
    ///
    /// It has to be named explicitly: a process created with a token whose session was rewritten
    /// gets no desktop by default, and without one every window it tries to create fails — including
    /// the session banner. `Default` is the right starting point; the capture thread moves itself to
    /// whichever desktop has input once it is running (see `remote_desktop`).
    const STARTUP_DESKTOP: &str = r"winsta0\default";

    /// The mode argument the helper is launched with. Matched in `main`.
    pub const HELPER_ARGUMENT: &str = "--remote-session-helper";

    /// The console session id, if somebody is logged into it.
    ///
    /// Two questions in one, because either alone is misleading: there can be an active console
    /// session with no user (the machine is at the logon screen having never been signed into), and
    /// `WTSQueryUserToken` is the only thing that tells them apart.
    pub fn console_session_with_user() -> Option<u32> {
        // SAFETY: no arguments, no preconditions. 0xFFFFFFFF means there is no console session at
        // all right now — nobody logged in, or a session in transition.
        let session = unsafe { WTSGetActiveConsoleSessionId() };
        if session == u32::MAX {
            return None;
        }

        let mut token: HANDLE = ptr::null_mut();
        // SAFETY: a valid out-pointer. Requires SE_TCB_NAME, which the service has as SYSTEM.
        let queried = unsafe { WTSQueryUserToken(session, &mut token) };

        if queried == 0 {
            return None;
        }

        // Only ever asked as a question — the token itself is not what launches the helper, since a
        // user token would produce a medium-integrity process. See the module note.
        // SAFETY: handed to us by WTSQueryUserToken and not used again.
        unsafe { CloseHandle(token) };

        Some(session)
    }

    /// A running helper process.
    pub struct SessionHelper {
        process: HANDLE,
        pid: u32,
    }

    impl SessionHelper {
        /// Starts the helper as SYSTEM inside `session`.
        pub fn launch(session: u32) -> Result<Self> {
            let mut own_token: HANDLE = ptr::null_mut();

            // SAFETY: a pseudo-handle for this process and a valid out-pointer. ASSIGN_PRIMARY and
            // DUPLICATE are what CreateProcessAsUser and DuplicateTokenEx need; QUERY is for
            // SetTokenInformation.
            if unsafe {
                OpenProcessToken(
                    GetCurrentProcess(),
                    TOKEN_DUPLICATE | TOKEN_QUERY | TOKEN_ASSIGN_PRIMARY,
                    &mut own_token,
                )
            } == 0
            {
                // SAFETY: no preconditions.
                return Err(anyhow!("could not open the service's own token (error {})", unsafe {
                    GetLastError()
                }));
            }

            let token = duplicate_into_session(own_token, session);
            // SAFETY: opened above and no longer needed whichever way the duplication went.
            unsafe { CloseHandle(own_token) };
            let token = token?;

            let result = create_process(token, session);
            // SAFETY: the duplicate is ours and the new process has its own copy.
            unsafe { CloseHandle(token) };
            result
        }

        pub fn pid(&self) -> u32 {
            self.pid
        }

        /// Stops the helper if it is still running.
        ///
        /// Terminate rather than a graceful signal, and that is safe here in a way it would not be
        /// for the patching side: this process holds no lock, writes no file and is mid-way through
        /// nothing but a screen capture. The alternative — a shutdown handshake over the pipe — adds
        /// a message and a timeout to cover a case that already has one.
        pub fn stop(self) {
            // SAFETY: a process handle this struct owns. A non-zero exit code is conventional for
            // "terminated rather than exited"; failure means it has already gone, which is fine.
            unsafe {
                TerminateProcess(self.process, 1);
                // Briefly, so the pipe instance it held is released before the next accept. 2s is
                // generous for a process being killed outright.
                WaitForSingleObject(self.process, 2000);
                CloseHandle(self.process);
            }
        }
    }

    /// Copies the service's SYSTEM token and moves the copy into `session`.
    fn duplicate_into_session(own_token: HANDLE, session: u32) -> Result<HANDLE> {
        let mut duplicate: HANDLE = ptr::null_mut();

        // SAFETY: a valid token handle, a documented impersonation level and token type, and a valid
        // out-pointer. TokenPrimary is required — CreateProcessAsUser refuses an impersonation
        // token.
        if unsafe {
            DuplicateTokenEx(
                own_token,
                MAXIMUM_ALLOWED,
                ptr::null(),
                SecurityImpersonation,
                TokenPrimary,
                &mut duplicate,
            )
        } == 0
        {
            // SAFETY: no preconditions.
            return Err(anyhow!("could not duplicate the service's token (error {})", unsafe {
                GetLastError()
            }));
        }

        // The step that makes this a session-1 process rather than a session-0 one. Requires
        // SE_TCB_NAME, which is why only the service can do it.
        // SAFETY: a primary token we own, the documented information class, and a pointer to a u32
        // of the declared length.
        let moved = unsafe {
            SetTokenInformation(
                duplicate,
                TokenSessionId,
                &session as *const u32 as *const c_void,
                std::mem::size_of::<u32>() as u32,
            )
        };

        if moved == 0 {
            // SAFETY: no preconditions.
            let error = unsafe { GetLastError() };
            // SAFETY: duplicated above, not yet used.
            unsafe { CloseHandle(duplicate) };
            return Err(anyhow!(
                "could not move the token into session {session} (error {error}) — this needs \
                 SE_TCB_NAME, so it fails if the service is not running as LocalSystem"
            ));
        }

        Ok(duplicate)
    }

    fn create_process(token: HANDLE, session: u32) -> Result<SessionHelper> {
        let executable = config::installed_binary_path();
        let executable_wide = wide(&executable.to_string_lossy());
        // Quoted, because the install path contains no space today and `C:\Program Files` is one
        // rename away from being the path that does.
        let mut command_line = wide(&format!("\"{}\" {HELPER_ARGUMENT}", executable.to_string_lossy()));
        let mut desktop = wide(STARTUP_DESKTOP);

        let mut startup: STARTUPINFOW = unsafe { std::mem::zeroed() };
        startup.cb = std::mem::size_of::<STARTUPINFOW>() as u32;
        startup.lpDesktop = desktop.as_mut_ptr();

        let mut information: PROCESS_INFORMATION = unsafe { std::mem::zeroed() };

        // SAFETY: a primary token in the target session, a NUL-terminated image path and mutable
        // command line as CreateProcessAsUserW requires, and correctly-sized structures.
        //
        // CREATE_NO_WINDOW because this has no console to show; CREATE_UNICODE_ENVIRONMENT because
        // the inherited environment is the service's, which is already Unicode.
        let created = unsafe {
            CreateProcessAsUserW(
                token,
                executable_wide.as_ptr(),
                command_line.as_mut_ptr(),
                ptr::null(),
                ptr::null(),
                0,
                CREATE_NO_WINDOW | CREATE_UNICODE_ENVIRONMENT,
                ptr::null(),
                ptr::null(),
                &startup,
                &mut information,
            )
        };

        if created == 0 {
            // SAFETY: no preconditions.
            return Err(anyhow!(
                "could not start the session helper in session {session} (error {})",
                unsafe { GetLastError() }
            ))
            .context("launching the remote control session helper");
        }

        // The thread handle is of no use — the process handle is what `stop` waits on.
        // SAFETY: handed back by CreateProcessAsUserW and owned by this caller.
        unsafe { CloseHandle(information.hThread) };

        logging::info(&format!(
            "launched the remote control session helper as SYSTEM in session {session} (pid {})",
            information.dwProcessId
        ));

        Ok(SessionHelper { process: information.hProcess, pid: information.dwProcessId })
    }
}

#[cfg(test)]
mod tests {
    #[cfg(windows)]
    #[test]
    fn the_helper_argument_is_the_one_main_dispatches_on() {
        // A rename on one side only is a helper that starts, does not recognise its own mode, and
        // runs a check-in instead — which would look like a session that never produced a frame.
        assert_eq!(super::platform::HELPER_ARGUMENT, "--remote-session-helper");
    }
}
