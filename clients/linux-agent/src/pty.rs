//! A pseudo-terminal with a program in it: the host end of a shell session.
//!
//! **Identical in the macOS and Linux agents, deliberately.** Both are Unix and both reach a PTY
//! through the same three calls — `openpty`, `setsid` + `TIOCSCTTY` in the child, `TIOCSWINSZ` on
//! resize — so the file is one file twice, the way `remote_protocol.rs` is one file three times. The
//! Windows agent's `pty.rs` presents the same API over ConPTY and shares nothing below it. What the
//! program *is* (which shell, which account, which environment) is the caller's decision and lives in
//! each agent's `remote_control.rs`, because it is exactly where the three differ: the logged-in
//! user's shell on macOS, root's on Linux, `powershell.exe` as SYSTEM on Windows.
//!
//! Nothing here knows about sessions, sockets or the server. It is `spawn`, `read_available`,
//! `write_all`, `resize`, `try_wait` and `terminate` — the same shape as `remote_ipc::IpcConnection`,
//! so the relay loop that already pumps one non-blocking byte stream can pump this one the same way.
//!
//! Two decisions worth stating. The child is started through `std::process::Command` with a
//! `pre_exec` hook rather than `forkpty`, because `forkpty` forks a multi-threaded process and then
//! runs arbitrary code before exec — `Command` does the fork/exec dance with only async-signal-safe
//! calls in between, and the hook is the sanctioned place for `setsid`. And the master is
//! non-blocking, because the relay loop that reads it is the one that also services two WebSockets
//! and must never park on a shell that has nothing to say.

use std::ffi::OsStr;
use std::io::{self, ErrorKind};
use std::os::fd::{AsRawFd, FromRawFd, OwnedFd};
use std::os::unix::process::CommandExt;
use std::path::{Path, PathBuf};
use std::process::{Child, Command, ExitStatus, Stdio};
use std::time::Duration;

/// What to run in the terminal. Built by the caller; this module does not choose shells.
#[derive(Debug, Clone)]
pub struct ProgramSpec {
    pub program: PathBuf,
    pub args: Vec<String>,
    /// Set on top of an otherwise *empty* environment — a shell inherits nothing from the agent
    /// process, so it does not see the agent's own variables (and on the per-user agent, nothing a
    /// LaunchAgent happened to be started with). `TERM` is added by [`Pty::spawn`] regardless.
    pub env: Vec<(String, String)>,
    pub cwd: PathBuf,
}

/// The terminal the browser reports on first layout is not known when the shell is started, so it
/// begins at the classic size and is resized the moment the viewer says otherwise. Shells redraw on
/// the resulting `SIGWINCH`, so nothing is lost.
pub const INITIAL_COLS: u16 = 80;
pub const INITIAL_ROWS: u16 = 24;

/// What the terminal is told it is. `xterm-256color` because that is what the viewer implements
/// (`xterm` on the Dart side), and because every shell and full-screen program on both platforms
/// has a terminfo entry for it.
pub const TERM: &str = "xterm-256color";

/// How long [`Pty::terminate`] gives the process after `SIGHUP` before `SIGKILL`. A shell with a
/// foreground job reacts to the hangup by killing the job and exiting; anything still there after
/// this is not going quietly.
const TERMINATE_GRACE: Duration = Duration::from_millis(500);

pub struct Pty {
    master: OwnedFd,
    child: Child,
}

impl Pty {
    /// Opens a pseudo-terminal of the given size and starts `spec` in it as the session leader with
    /// the PTY as its controlling terminal. All three standard streams are the PTY.
    pub fn spawn(spec: &ProgramSpec, cols: u16, rows: u16) -> io::Result<Pty> {
        let (master, slave) = open_pty(cols, rows)?;

        let mut command = Command::new(&spec.program);
        command
            .args(&spec.args)
            .env_clear()
            .env("TERM", TERM)
            .envs(spec.env.iter().map(|(k, v)| (OsStr::new(k), OsStr::new(v))))
            .current_dir(&spec.cwd)
            .stdin(Stdio::from(slave.try_clone()?))
            .stdout(Stdio::from(slave.try_clone()?))
            .stderr(Stdio::from(slave));

        // SAFETY: only async-signal-safe calls (`setsid`, `ioctl`), no allocation, no locks — the
        // constraints `pre_exec` documents for code that runs between fork and exec.
        unsafe {
            command.pre_exec(|| {
                if libc::setsid() < 0 {
                    return Err(io::Error::last_os_error());
                }
                // Stdin is the slave by now (Command dup2s it before running this hook). Without a
                // controlling terminal, job control does not work and `sudo` cannot read a password.
                if libc::ioctl(0, libc::TIOCSCTTY as _, 0) < 0 {
                    return Err(io::Error::last_os_error());
                }
                Ok(())
            });
        }

        let child = command.spawn()?;
        // The parent's copy of the slave is dropped with `command`; the child holds the only one
        // that matters. From here on, EOF on the master means the last process holding the slave
        // has gone.

        Ok(Pty { master, child })
    }

    /// Reads whatever the program has written since last time. `Ok(None)` when there is nothing yet;
    /// `Ok(Some(0))` once the program has closed its end, which is how a shell exiting is noticed.
    pub fn read_available(&mut self, buffer: &mut [u8]) -> io::Result<Option<usize>> {
        // SAFETY: a valid fd and a buffer of exactly the length passed.
        let read = unsafe { libc::read(self.master.as_raw_fd(), buffer.as_mut_ptr() as *mut libc::c_void, buffer.len()) };
        if read >= 0 {
            return Ok(Some(read as usize));
        }

        let error = io::Error::last_os_error();
        match error.raw_os_error() {
            Some(libc::EAGAIN) => Ok(None),
            // Linux reports EIO on a master whose slave side has been closed; macOS reports EOF. Both
            // mean the same thing, and the caller should see one answer.
            Some(libc::EIO) => Ok(Some(0)),
            _ => Err(error),
        }
    }

    /// Writes typed input to the program, all of it. Blocks briefly on a full PTY input queue — a
    /// paste of several kilobytes into a shell that is busy — rather than dropping any of it, which
    /// for keystrokes is the worse outcome.
    pub fn write_all(&mut self, mut bytes: &[u8]) -> io::Result<()> {
        while !bytes.is_empty() {
            // SAFETY: a valid fd and a slice of exactly the length passed.
            let written = unsafe { libc::write(self.master.as_raw_fd(), bytes.as_ptr() as *const libc::c_void, bytes.len()) };
            if written >= 0 {
                bytes = &bytes[written as usize..];
                continue;
            }

            let error = io::Error::last_os_error();
            match error.raw_os_error() {
                Some(libc::EAGAIN) => std::thread::sleep(Duration::from_millis(1)),
                Some(libc::EINTR) => {}
                _ => return Err(error),
            }
        }
        Ok(())
    }

    /// Tells the terminal its new size. The kernel sends the foreground process group `SIGWINCH`.
    pub fn resize(&self, cols: u16, rows: u16) -> io::Result<()> {
        let size = winsize(cols, rows);
        // SAFETY: a valid fd and a pointer to a correctly-typed winsize, as TIOCSWINSZ requires.
        if unsafe { libc::ioctl(self.master.as_raw_fd(), libc::TIOCSWINSZ as _, &size) } < 0 {
            return Err(io::Error::last_os_error());
        }
        Ok(())
    }

    /// Whether the program has exited, without waiting for it.
    pub fn try_wait(&mut self) -> io::Result<Option<ExitStatus>> {
        self.child.try_wait()
    }

    /// Ends the program: hangup first, so a shell can kill its foreground job and leave the way it
    /// would if its terminal window closed, then `SIGKILL` for anything that stays. Always reaps, so
    /// an ended session never leaves a zombie behind in a process that lives for months.
    pub fn terminate(mut self) -> io::Result<()> {
        if self.child.try_wait()?.is_some() {
            return Ok(());
        }

        // SAFETY: signalling our own child by the pid we were given for it.
        unsafe {
            libc::kill(self.child.id() as libc::pid_t, libc::SIGHUP);
        }

        let deadline = std::time::Instant::now() + TERMINATE_GRACE;
        while std::time::Instant::now() < deadline {
            if self.child.try_wait()?.is_some() {
                return Ok(());
            }
            std::thread::sleep(Duration::from_millis(10));
        }

        self.child.kill()?;
        self.child.wait()?;
        Ok(())
    }
}

impl Drop for Pty {
    /// A backstop for every path that does not reach [`Pty::terminate`] — a panic, or any `?` after
    /// `spawn` succeeded. There are several of those in each agent's `remote_control.rs`: opening the
    /// session socket, setting it non-blocking, and writing the shell banner all come *after* the
    /// terminal exists. Without this each one leaves a shell running and an unreaped child in a
    /// process that lives for months, which is exactly what `terminate`'s own note promises cannot
    /// happen.
    ///
    /// Deliberately not `terminate`'s full grace period: a drop is not a place to sleep, so this
    /// hangs up and reaps without waiting to be polite about it.
    fn drop(&mut self) {
        if self.child.try_wait().is_ok_and(|status| status.is_some()) {
            return;
        }

        // SAFETY: signalling our own child by the pid we were given for it.
        unsafe {
            libc::kill(self.child.id() as libc::pid_t, libc::SIGHUP);
        }

        let _ = self.child.kill();
        let _ = self.child.wait();
    }
}

/// `openpty` with the size set at creation, both ends `CLOEXEC` so neither leaks into any *other*
/// child this process spawns, and the master non-blocking.
fn open_pty(cols: u16, rows: u16) -> io::Result<(OwnedFd, OwnedFd)> {
    let mut master: libc::c_int = -1;
    let mut slave: libc::c_int = -1;
    let mut size = winsize(cols, rows);

    // SAFETY: two out-parameters for the fds, no name buffer (null is documented as "don't want it"),
    // no termios (inherit defaults), and a valid winsize. `null_mut` and `&mut` because the libc
    // crate declares the last two as `*mut` on Apple and `*const` on Linux, and only a mutable
    // pointer coerces to both.
    let result = unsafe {
        libc::openpty(&mut master, &mut slave, std::ptr::null_mut(), std::ptr::null_mut(), &mut size)
    };
    if result < 0 {
        return Err(io::Error::last_os_error());
    }

    // SAFETY: openpty succeeded, so both are open fds this process owns and nothing else holds.
    let (master, slave) = unsafe { (OwnedFd::from_raw_fd(master), OwnedFd::from_raw_fd(slave)) };

    set_cloexec(&master)?;
    set_cloexec(&slave)?;
    set_nonblocking(&master)?;

    Ok((master, slave))
}

fn set_cloexec(fd: &OwnedFd) -> io::Result<()> {
    set_fd_flag(fd, libc::F_GETFD, libc::F_SETFD, libc::FD_CLOEXEC)
}

fn set_nonblocking(fd: &OwnedFd) -> io::Result<()> {
    set_fd_flag(fd, libc::F_GETFL, libc::F_SETFL, libc::O_NONBLOCK)
}

fn set_fd_flag(fd: &OwnedFd, get: libc::c_int, set: libc::c_int, flag: libc::c_int) -> io::Result<()> {
    // SAFETY: fcntl on an fd we own, with the get/set pair the flag belongs to.
    unsafe {
        let flags = libc::fcntl(fd.as_raw_fd(), get);
        if flags < 0 || libc::fcntl(fd.as_raw_fd(), set, flags | flag) < 0 {
            return Err(io::Error::last_os_error());
        }
    }
    Ok(())
}

fn winsize(cols: u16, rows: u16) -> libc::winsize {
    libc::winsize {
        ws_row: rows,
        ws_col: cols,
        ws_xpixel: 0,
        ws_ypixel: 0,
    }
}

/// The first of `candidates` that exists, for callers choosing a shell. Kept here so the two Unix
/// agents make the choice the same way even though they choose from different lists.
pub fn first_existing(candidates: &[&str]) -> Option<PathBuf> {
    candidates.iter().map(Path::new).find(|path| path.is_file()).map(Path::to_path_buf)
}

/// The `ErrorKind` a caller should treat as "the program is simply gone" rather than as a fault.
#[allow(dead_code)]
pub fn is_gone(error: &io::Error) -> bool {
    matches!(error.kind(), ErrorKind::BrokenPipe | ErrorKind::NotConnected)
        || error.raw_os_error() == Some(libc::EIO)
}

#[cfg(test)]
mod tests {
    use super::*;

    fn sh() -> ProgramSpec {
        ProgramSpec {
            program: first_existing(&["/bin/sh"]).expect("/bin/sh exists on every Unix"),
            args: Vec::new(),
            env: vec![("PATH".to_string(), "/usr/bin:/bin".to_string())],
            cwd: PathBuf::from("/"),
        }
    }

    fn read_until(pty: &mut Pty, needle: &[u8], timeout: Duration) -> Vec<u8> {
        let deadline = std::time::Instant::now() + timeout;
        let mut collected = Vec::new();
        let mut buffer = [0u8; 4096];
        while std::time::Instant::now() < deadline {
            match pty.read_available(&mut buffer).expect("read") {
                Some(0) => break,
                Some(n) => collected.extend_from_slice(&buffer[..n]),
                None => std::thread::sleep(Duration::from_millis(5)),
            }
            if collected.windows(needle.len()).any(|w| w == needle) {
                break;
            }
        }
        collected
    }

    #[test]
    fn runs_a_shell_that_can_see_its_terminal() {
        // `tty` prints the terminal's device path only when stdin really is a terminal; a shell
        // whose stdin were a plain pipe would print "not a tty" and every full-screen program the
        // administrator ran would misbehave in the same way.
        let mut pty = Pty::spawn(&sh(), 100, 30).expect("spawn");
        pty.write_all(b"tty; stty size; echo TERM=$TERM; exit\n").expect("write");

        // Wait for the *answer*, not the echo: "TERM=" appears in the echoed command line too.
        let output = read_until(&mut pty, TERM.as_bytes(), Duration::from_secs(5));
        let text = String::from_utf8_lossy(&output);
        assert!(text.contains("/dev/"), "{text}");
        assert!(text.contains("30 100"), "{text}");
        assert!(text.contains(&format!("TERM={TERM}")), "{text}");

        let deadline = std::time::Instant::now() + Duration::from_secs(5);
        while pty.try_wait().expect("wait").is_none() {
            assert!(std::time::Instant::now() < deadline, "shell did not exit after `exit`");
            std::thread::sleep(Duration::from_millis(10));
        }
        pty.terminate().expect("terminate after exit is a no-op");
    }

    #[test]
    fn resizing_reaches_the_program() {
        let mut pty = Pty::spawn(&sh(), INITIAL_COLS, INITIAL_ROWS).expect("spawn");
        pty.resize(132, 50).expect("resize");
        pty.write_all(b"stty size\n").expect("write");

        let output = read_until(&mut pty, b"50 132", Duration::from_secs(5));
        assert!(String::from_utf8_lossy(&output).contains("50 132"));
        pty.terminate().expect("terminate");
    }

    #[test]
    fn terminate_reaps_a_shell_that_is_still_running() {
        let pty = Pty::spawn(&sh(), INITIAL_COLS, INITIAL_ROWS).expect("spawn");
        let pid = pty.child.id() as libc::pid_t;
        pty.terminate().expect("terminate");

        // SAFETY: signal 0 checks for existence without sending anything. ESRCH means reaped.
        let alive = unsafe { libc::kill(pid, 0) } == 0;
        assert!(!alive, "shell {pid} was not reaped");
    }

    #[test]
    fn reading_a_closed_terminal_reports_end_of_file_not_an_error() {
        let mut pty = Pty::spawn(&sh(), INITIAL_COLS, INITIAL_ROWS).expect("spawn");
        pty.write_all(b"exit\n").expect("write");

        let deadline = std::time::Instant::now() + Duration::from_secs(5);
        let mut buffer = [0u8; 256];
        loop {
            assert!(std::time::Instant::now() < deadline, "never saw EOF");
            match pty.read_available(&mut buffer).expect("EOF must not surface as an error") {
                Some(0) => break,
                _ => std::thread::sleep(Duration::from_millis(5)),
            }
        }
        pty.terminate().expect("terminate");
    }
}
