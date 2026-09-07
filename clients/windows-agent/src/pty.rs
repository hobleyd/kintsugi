//! A pseudo-terminal with a program in it: the host end of a shell session.
//!
//! **The same API as the macOS and Linux agents' `pty.rs`, over completely different machinery.**
//! Those two are one file twice — both are Unix and both reach a PTY through `openpty`, `setsid`
//! and `TIOCSWINSZ`. Windows has none of that, so this shares nothing below `spawn`,
//! `read_available`, `write_all`, `resize`, `try_wait` and `terminate`; what is kept identical is
//! the shape those six present, because the relay loop above them is meant to read the same on all
//! three. What the program *is* stays the caller's decision and lives in `remote_control.rs`, which
//! is exactly where the three platforms differ: root's shell on macOS and Linux, and
//! `powershell.exe` as SYSTEM here.
//!
//! # ConPTY, and the two pipes around it
//!
//! `CreatePseudoConsole` takes a *read* handle it will consume input from and a *write* handle it
//! will produce output on, so this makes two anonymous pipes and keeps the opposite ends: writing
//! to `input_write` is typing, reading from `output_read` is what the terminal drew. The console
//! host (`conhost.exe`) is started by Windows behind that handle and does the VT interpretation, so
//! what comes back out is an ANSI stream a terminal emulator understands — the same bytes the
//! Unix agents send, which is why one viewer serves all three.
//!
//! The child is attached to it through `PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE` on an extended
//! `STARTUPINFOEXW`, which is the only supported way — a console program launched without it gets
//! the service's own (nonexistent) console and writes nowhere.
//!
//! # Two things here that fail quietly if changed
//!
//! Reads go through `PeekNamedPipe` first. `ReadFile` on an anonymous pipe **blocks** until at
//! least one byte arrives, and this handle is read by the same loop that services two WebSockets —
//! so a blocking read parks the relay on a shell that has nothing to say, which is most of the
//! time. There is no non-blocking mode to set on an anonymous pipe; peeking first is the way.
//!
//! And the inherited ends are closed immediately after `CreatePseudoConsole` duplicates them. Left
//! open, this process is itself a writer on the output pipe, so the pipe never reports broken and
//! a shell that has exited looks exactly like one sitting at a prompt — the session hangs instead
//! of ending.

use std::io::{self, ErrorKind};
use std::path::{Path, PathBuf};
use std::time::Duration;

use windows_sys::Win32::Foundation::{
    CloseHandle, GetLastError, ERROR_BROKEN_PIPE, ERROR_NO_DATA, HANDLE, INVALID_HANDLE_VALUE, STILL_ACTIVE,
};
use windows_sys::Win32::Security::SECURITY_ATTRIBUTES;
use windows_sys::Win32::Storage::FileSystem::{ReadFile, WriteFile};
use windows_sys::Win32::System::Console::{ClosePseudoConsole, CreatePseudoConsole, ResizePseudoConsole, COORD, HPCON};
use windows_sys::Win32::System::Pipes::{CreatePipe, PeekNamedPipe};
use windows_sys::Win32::System::Threading::{
    CreateProcessW, DeleteProcThreadAttributeList, GetExitCodeProcess, InitializeProcThreadAttributeList,
    TerminateProcess, UpdateProcThreadAttribute, WaitForSingleObject, CREATE_UNICODE_ENVIRONMENT,
    EXTENDED_STARTUPINFO_PRESENT, PROCESS_INFORMATION, STARTUPINFOEXW,
};

use crate::win32::wide;

/// `PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE`, spelled out rather than imported.
///
/// windows-sys does not export it in every release, and a build that fails on a missing constant is
/// a poor trade for a value that has been fixed since Windows 10 1809 — it is
/// `ProcThreadAttributeValue(22, false, true, false)`, which is what this number is.
const PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE: usize = 0x0002_0016;

/// What to run in the terminal. Built by the caller; this module does not choose shells.
///
/// Field for field the Unix agents' `ProgramSpec`, so the callers read alike, though `env` here is
/// turned into a UTF-16 environment block rather than an `envp` array.
#[derive(Debug, Clone)]
pub struct ProgramSpec {
    pub program: PathBuf,
    pub args: Vec<String>,
    /// Set on top of an otherwise *empty* environment, so the shell inherits nothing from the
    /// service — including anything the SCM happened to start it with. `TERM` is added by
    /// [`Pty::spawn`] regardless.
    pub env: Vec<(String, String)>,
    pub cwd: PathBuf,
}

/// The terminal the browser reports on first layout is not known when the shell is started, so it
/// begins at the classic size and is resized the moment the viewer says otherwise.
pub const INITIAL_COLS: u16 = 80;
pub const INITIAL_ROWS: u16 = 24;

/// What the terminal is told it is. `xterm-256color` because that is what the viewer implements,
/// and because it is what PowerShell's own VT rendering assumes when it is not talking to a real
/// console.
pub const TERM: &str = "xterm-256color";

/// How long [`Pty::terminate`] gives the program after the pseudo-console is closed before killing
/// it. Closing the console is Windows' equivalent of the hangup the Unix agents send: a shell reacts
/// by ending, and anything still here after this is not going quietly.
const TERMINATE_GRACE: Duration = Duration::from_millis(500);

/// `GetExitCodeProcess` reports this while the process is still running, which is why "has it
/// exited" is not simply "did the call succeed".
const STILL_RUNNING: u32 = STILL_ACTIVE as u32;

pub struct Pty {
    console: HPCON,
    /// This end types into the terminal.
    input_write: HANDLE,
    /// This end reads what the terminal drew.
    output_read: HANDLE,
    process: HANDLE,
    thread: HANDLE,
    exited: bool,
}

// SAFETY: every field is a kernel handle, which is process-wide rather than thread-affine, and
// nothing here is shared between threads without an owner — the relay loop owns one `Pty` outright.
// The marker is needed because raw pointers are not `Send` by default.
unsafe impl Send for Pty {}

impl Pty {
    /// Opens a pseudo-console of the given size and starts `spec` attached to it.
    pub fn spawn(spec: &ProgramSpec, cols: u16, rows: u16) -> io::Result<Pty> {
        let (input_read, input_write) = create_pipe()?;
        let (output_read, output_write) = create_pipe()?;

        // HPCON is an isize in windows-sys rather than a pointer type, so 0 is its "none".
        let mut console: HPCON = 0;
        // SAFETY: two live pipe handles and an out-pointer for the console. CreatePseudoConsole
        // duplicates both handles, so the two given here are ours to close immediately below.
        let created = unsafe {
            CreatePseudoConsole(
                COORD { X: cols as i16, Y: rows as i16 },
                input_read,
                output_write,
                0,
                &mut console,
            )
        };

        // Closed whatever happened: on success ConPTY holds its own duplicates, and on failure
        // these are all that is left of the pipes. Leaving the output writer open is the quiet
        // failure described in the module note — this process would be a writer on the pipe it
        // reads, so a shell that exits never breaks it.
        close(input_read);
        close(output_write);

        if created != 0 {
            close(input_write);
            close(output_read);
            return Err(io::Error::from_raw_os_error(created));
        }

        match start_process(spec, console) {
            Ok((process, thread)) => Ok(Pty {
                console,
                input_write,
                output_read,
                process,
                thread,
                exited: false,
            }),
            Err(err) => {
                // SAFETY: a console this function created and nothing else holds.
                unsafe { ClosePseudoConsole(console) };
                close(input_write);
                close(output_read);
                Err(err)
            }
        }
    }

    /// Reads whatever the program has written since last time. `Ok(None)` when there is nothing yet;
    /// `Ok(Some(0))` once the program has closed its end, which is how a shell exiting is noticed.
    pub fn read_available(&mut self, buffer: &mut [u8]) -> io::Result<Option<usize>> {
        let mut available: u32 = 0;

        // SAFETY: a live pipe handle; every out-parameter this does not want is null, which
        // PeekNamedPipe documents as "not interested".
        let peeked = unsafe {
            PeekNamedPipe(
                self.output_read,
                std::ptr::null_mut(),
                0,
                std::ptr::null_mut(),
                &mut available,
                std::ptr::null_mut(),
            )
        };

        if peeked == 0 {
            return match last_error() {
                // The write end has gone: the shell and its console host have both exited. Reported
                // as end of file rather than as a fault, which is what the Unix agents' EIO arm
                // does for the same situation.
                ERROR_BROKEN_PIPE | ERROR_NO_DATA => Ok(Some(0)),
                code => Err(io::Error::from_raw_os_error(code as i32)),
            };
        }

        if available == 0 {
            return Ok(None);
        }

        let wanted = (available as usize).min(buffer.len());
        let mut read: u32 = 0;

        // SAFETY: a live handle and a buffer of at least `wanted` bytes. Peeked first, so this
        // cannot block — which is the whole reason for the peek.
        let ok = unsafe {
            ReadFile(
                self.output_read,
                buffer.as_mut_ptr().cast(),
                wanted as u32,
                &mut read,
                std::ptr::null_mut(),
            )
        };

        if ok == 0 {
            return match last_error() {
                ERROR_BROKEN_PIPE | ERROR_NO_DATA => Ok(Some(0)),
                code => Err(io::Error::from_raw_os_error(code as i32)),
            };
        }

        Ok(Some(read as usize))
    }

    /// Writes typed input to the program, all of it. A pipe write blocks on a full buffer rather
    /// than dropping anything, which for keystrokes is the outcome to want.
    pub fn write_all(&mut self, mut bytes: &[u8]) -> io::Result<()> {
        while !bytes.is_empty() {
            let mut written: u32 = 0;

            // SAFETY: a live handle and a slice of exactly the length passed.
            let ok = unsafe {
                WriteFile(
                    self.input_write,
                    bytes.as_ptr(),
                    bytes.len() as u32,
                    &mut written,
                    std::ptr::null_mut(),
                )
            };

            if ok == 0 {
                return Err(io::Error::from_raw_os_error(last_error() as i32));
            }

            if written == 0 {
                return Err(io::Error::new(ErrorKind::WriteZero, "the terminal accepted no input"));
            }

            bytes = &bytes[written as usize..];
        }

        Ok(())
    }

    /// Tells the terminal its new size. ConPTY reflows and the program is told, which is the
    /// equivalent of the `SIGWINCH` the Unix agents' `TIOCSWINSZ` causes.
    pub fn resize(&self, cols: u16, rows: u16) -> io::Result<()> {
        // SAFETY: a console this struct owns and a by-value size.
        let result = unsafe { ResizePseudoConsole(self.console, COORD { X: cols as i16, Y: rows as i16 }) };
        if result != 0 {
            return Err(io::Error::from_raw_os_error(result));
        }
        Ok(())
    }

    /// Whether the program has exited, without waiting for it.
    ///
    /// `Option<u32>` rather than the Unix agents' `ExitStatus`, because there is no such value to
    /// build here without pulling in `std::os::windows::process`; the callers only ever ask whether
    /// it is `Some`.
    pub fn try_wait(&mut self) -> io::Result<Option<u32>> {
        if self.exited {
            return Ok(Some(0));
        }

        let mut code: u32 = 0;
        // SAFETY: a live process handle opened with PROCESS_QUERY_INFORMATION, which
        // CreateProcessW's handle always carries, and an out-parameter.
        if unsafe { GetExitCodeProcess(self.process, &mut code) } == 0 {
            return Err(io::Error::from_raw_os_error(last_error() as i32));
        }

        if code == STILL_RUNNING {
            return Ok(None);
        }

        self.exited = true;
        Ok(Some(code))
    }

    /// Ends the program and releases everything.
    ///
    /// Closing the pseudo-console first is deliberate and is the counterpart of the Unix agents'
    /// `SIGHUP`: it tells the client its console has gone, which a shell answers by exiting the way
    /// it would if its window had been closed. `TerminateProcess` is only for what stays.
    pub fn terminate(mut self) -> io::Result<()> {
        // Before the console closes, and this ordering matters: `ClosePseudoConsole` waits for the
        // console host to drain its output, and the output pipe's reader is *this* struct — so
        // closing the read end first is what stops that wait from depending on a loop that has
        // already finished reading.
        close(self.output_read);
        self.output_read = INVALID_HANDLE_VALUE;

        // SAFETY: a console this struct owns; called exactly once, since `terminate` takes self and
        // the field is not read again.
        unsafe { ClosePseudoConsole(self.console) };
        self.console = 0;

        let deadline = std::time::Instant::now() + TERMINATE_GRACE;
        while std::time::Instant::now() < deadline {
            if self.try_wait()?.is_some() {
                return Ok(());
            }
            std::thread::sleep(Duration::from_millis(10));
        }

        // SAFETY: a live process handle. The exit code is arbitrary; nothing reads it.
        unsafe { TerminateProcess(self.process, 1) };
        // Reaped, so the handle can be closed without leaving a process object behind.
        unsafe { WaitForSingleObject(self.process, 1_000) };
        Ok(())
    }
}

impl Drop for Pty {
    /// A backstop for the paths that do not call [`Pty::terminate`] — a panic, or an error on the
    /// way out of a session. Without it a shell survives its session and holds a console host with
    /// it, in a service that runs for months.
    fn drop(&mut self) {
        close(self.output_read);
        close(self.input_write);

        if self.console != 0 {
            // SAFETY: owned by this struct and closed at most once — `terminate` nulls it.
            unsafe { ClosePseudoConsole(self.console) };
        }

        if !self.exited {
            // SAFETY: a live process handle; harmless on one that has already exited.
            unsafe { TerminateProcess(self.process, 1) };
        }

        close(self.thread);
        close(self.process);
    }
}

/// Starts `spec` with the pseudo-console attached, returning its process and thread handles.
fn start_process(spec: &ProgramSpec, console: HPCON) -> io::Result<(HANDLE, HANDLE)> {
    let mut command_line = wide(&command_line_for(spec));
    let directory = wide(&spec.cwd.to_string_lossy());
    let mut environment = environment_block(&spec.env);

    let mut startup: STARTUPINFOEXW = unsafe { std::mem::zeroed() };
    startup.StartupInfo.cb = std::mem::size_of::<STARTUPINFOEXW>() as u32;

    // The attribute list is variable-length: asked for its size, allocated, then initialised. The
    // buffer has to outlive CreateProcessW, which is why it is bound here rather than inline.
    let mut size: usize = 0;
    // SAFETY: the documented two-call idiom — the first call fails with ERROR_INSUFFICIENT_BUFFER
    // and fills in `size`, which is the point of it.
    unsafe { InitializeProcThreadAttributeList(std::ptr::null_mut(), 1, 0, &mut size) };
    if size == 0 {
        return Err(io::Error::from_raw_os_error(last_error() as i32));
    }

    let mut attributes = vec![0u8; size];
    startup.lpAttributeList = attributes.as_mut_ptr().cast();

    // SAFETY: a buffer of exactly the size the call above asked for, for one attribute.
    if unsafe { InitializeProcThreadAttributeList(startup.lpAttributeList, 1, 0, &mut size) } == 0 {
        return Err(io::Error::from_raw_os_error(last_error() as i32));
    }

    // SAFETY: an initialised list with room for one attribute, and a console handle that outlives
    // the CreateProcessW call below — UpdateProcThreadAttribute stores the pointer, it copies
    // nothing.
    let updated = unsafe {
        UpdateProcThreadAttribute(
            startup.lpAttributeList,
            0,
            PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE,
            console as *const std::ffi::c_void,
            std::mem::size_of::<HPCON>(),
            std::ptr::null_mut(),
            std::ptr::null_mut(),
        )
    };

    if updated == 0 {
        let error = last_error();
        // SAFETY: initialised above, freed exactly once on this path.
        unsafe { DeleteProcThreadAttributeList(startup.lpAttributeList) };
        return Err(io::Error::from_raw_os_error(error as i32));
    }

    let mut information: PROCESS_INFORMATION = unsafe { std::mem::zeroed() };

    // SAFETY: a mutable command line as CreateProcessW requires, a correctly-sized STARTUPINFOEXW
    // with EXTENDED_STARTUPINFO_PRESENT set, and a UTF-16 environment block matching
    // CREATE_UNICODE_ENVIRONMENT. Handle inheritance is off: the child reaches its terminal through
    // the pseudo-console attribute, so it needs none of this process's handles — and a service's
    // handles are exactly what should not leak into a shell.
    let started = unsafe {
        CreateProcessW(
            std::ptr::null(),
            command_line.as_mut_ptr(),
            std::ptr::null(),
            std::ptr::null(),
            0,
            EXTENDED_STARTUPINFO_PRESENT | CREATE_UNICODE_ENVIRONMENT,
            environment.as_mut_ptr().cast(),
            directory.as_ptr(),
            &mut startup.StartupInfo,
            &mut information,
        )
    };

    let error = last_error();
    // SAFETY: initialised above and freed exactly once, on every path out of this function.
    unsafe { DeleteProcThreadAttributeList(startup.lpAttributeList) };

    if started == 0 {
        return Err(io::Error::from_raw_os_error(error as i32));
    }

    Ok((information.hProcess, information.hThread))
}

/// The program and its arguments as one command line, quoting anything containing a space.
///
/// Windows hands a process its command line as a single string and lets it parse it, so this is
/// where the argument vector the callers build has to be flattened. The quoting is deliberately
/// simple — the callers pass fixed switches and a path, never anything containing a quote.
fn command_line_for(spec: &ProgramSpec) -> String {
    let mut parts = vec![quote(&spec.program.to_string_lossy())];
    parts.extend(spec.args.iter().map(|argument| quote(argument)));
    parts.join(" ")
}

fn quote(value: &str) -> String {
    if value.contains(' ') && !value.starts_with('"') {
        format!("\"{value}\"")
    } else {
        value.to_string()
    }
}

/// The environment as `CREATE_UNICODE_ENVIRONMENT` wants it: `NAME=value` runs separated by NULs
/// and terminated by an empty one.
///
/// `TERM` is added here rather than left to the caller, so every terminal this module opens
/// announces itself the same way on all three platforms.
fn environment_block(env: &[(String, String)]) -> Vec<u16> {
    let mut block = Vec::new();

    for (name, value) in std::iter::once(&(String::from("TERM"), String::from(TERM))).chain(env.iter()) {
        block.extend(format!("{name}={value}").encode_utf16());
        block.push(0);
    }

    // The terminating empty string. A block that is otherwise empty still needs one, which is why
    // this is unconditional.
    block.push(0);
    block
}

/// An anonymous pipe whose handles are inheritable, as `CreatePseudoConsole` requires.
fn create_pipe() -> io::Result<(HANDLE, HANDLE)> {
    let mut read: HANDLE = INVALID_HANDLE_VALUE;
    let mut write: HANDLE = INVALID_HANDLE_VALUE;

    let mut attributes: SECURITY_ATTRIBUTES = unsafe { std::mem::zeroed() };
    attributes.nLength = std::mem::size_of::<SECURITY_ATTRIBUTES>() as u32;
    attributes.bInheritHandle = 1;

    // SAFETY: two out-parameters and a correctly-sized SECURITY_ATTRIBUTES. A zero size argument
    // asks for the system default buffer.
    if unsafe { CreatePipe(&mut read, &mut write, &attributes, 0) } == 0 {
        return Err(io::Error::from_raw_os_error(last_error() as i32));
    }

    Ok((read, write))
}

/// Closes a handle, tolerating one that was never opened.
fn close(handle: HANDLE) {
    if !handle.is_null() && handle != INVALID_HANDLE_VALUE {
        // SAFETY: a handle this module owns; the guard above covers the two sentinel values.
        unsafe { CloseHandle(handle) };
    }
}

fn last_error() -> u32 {
    // SAFETY: reads this thread's own last-error value and takes no arguments.
    unsafe { GetLastError() }
}

/// The first of `candidates` that exists, for callers choosing a shell. Kept here so all three
/// agents make the choice the same way even though they choose from different lists.
pub fn first_existing(candidates: &[&str]) -> Option<PathBuf> {
    candidates.iter().map(Path::new).find(|path| path.is_file()).map(Path::to_path_buf)
}

/// The `ErrorKind` a caller should treat as "the program is simply gone" rather than as a fault.
#[allow(dead_code)]
pub fn is_gone(error: &io::Error) -> bool {
    matches!(error.kind(), ErrorKind::BrokenPipe | ErrorKind::NotConnected)
        || matches!(error.raw_os_error(), Some(code) if code as u32 == ERROR_BROKEN_PIPE || code as u32 == ERROR_NO_DATA)
}

#[cfg(test)]
mod tests {
    use super::*;

    // These cover the pure half only. The ConPTY half needs a real console host, which is not
    // something the mingw + wine arrangement this agent is checked under provides — see CLAUDE.md.
    // The Unix agents' `pty.rs` has live tests against a real shell for the behaviour those two
    // share with this one.

    #[test]
    fn quotes_only_a_path_that_needs_it() {
        // `powershell.exe` lives under `C:\Program Files\...` on a host with PowerShell 7, and an
        // unquoted space there starts `C:\Program` with `Files\...` as its first argument.
        let spec = ProgramSpec {
            program: PathBuf::from(r"C:\Program Files\PowerShell\7\pwsh.exe"),
            args: vec!["-NoLogo".to_string()],
            env: Vec::new(),
            cwd: PathBuf::from(r"C:\"),
        };

        assert_eq!(
            command_line_for(&spec),
            r#""C:\Program Files\PowerShell\7\pwsh.exe" -NoLogo"#
        );
    }

    #[test]
    fn leaves_an_unspaced_path_alone() {
        let spec = ProgramSpec {
            program: PathBuf::from(r"C:\Windows\System32\cmd.exe"),
            args: Vec::new(),
            env: Vec::new(),
            cwd: PathBuf::from(r"C:\"),
        };

        assert_eq!(command_line_for(&spec), r"C:\Windows\System32\cmd.exe");
    }

    #[test]
    fn the_environment_block_is_nul_separated_and_double_nul_terminated() {
        let block = environment_block(&[("HOME".to_string(), "C:\\Users\\x".to_string())]);
        let text = String::from_utf16(&block).expect("utf-16");

        // TERM first, because it is prepended, then the caller's own — and the whole thing ends
        // with the empty string that tells Windows the block is over.
        assert_eq!(text, format!("TERM={TERM}\0HOME=C:\\Users\\x\0\0"));
    }

    #[test]
    fn an_empty_environment_is_still_terminated() {
        // Not a degenerate case worth skipping: a block missing its terminator is read past its
        // end, and TERM is the only reason this one is never actually empty.
        let block = environment_block(&[]);
        assert_eq!(String::from_utf16(&block).expect("utf-16"), format!("TERM={TERM}\0\0"));
    }
}
