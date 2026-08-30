use std::fs::{File, OpenOptions};
use std::os::fd::AsRawFd;
use std::os::unix::fs::OpenOptionsExt;
use std::path::PathBuf;
use std::time::{Duration, Instant};

use crate::config;
use crate::logging;

/// Held for as long as its owner is doing privileged work; releases the lock when dropped (which
/// includes the process exiting, since the kernel drops every `flock` a closed file held).
pub struct PrivilegedLock {
    _file: File,
}

/// Serializes the two root-side entry points against each other.
///
/// Neither of the other agents needs this. The macOS agent's periodic check-in and its queue
/// drain are *the same launchd job*, and launchd will not run two instances of one job at once;
/// the Windows agent's are both inside one resident service. Here they are two separate systemd
/// units — `kintsugi-agent.service` on a timer and `kintsugi-agent-queue.service` on a path watch
/// — and systemd's "one instance per unit" guarantee says nothing across units. Without this,
/// a queue-triggered application patch could land in the middle of a timer-triggered unattended
/// cycle, and two `apt-get` runs would deadlock on the dpkg lock with no useful error.
///
/// An advisory `flock` rather than a lock file whose existence is the lock: a process killed with
/// SIGKILL leaves no stale lock behind to clear, because the kernel releases it.
fn lock_path() -> PathBuf {
    config::state_dir().join("privileged.lock")
}

/// Takes the lock, waiting up to `timeout` for whoever holds it to finish. Returns `None` if it
/// couldn't — the caller decides what that means, and for both current callers it means "another
/// invocation is already doing this; there is nothing useful to add by doing it twice".
pub fn acquire(timeout: Duration) -> Option<PrivilegedLock> {
    let path = lock_path();
    if let Some(parent) = path.parent() {
        let _ = std::fs::create_dir_all(parent);
    }

    let file = match OpenOptions::new().create(true).write(true).truncate(false).mode(0o600).open(&path) {
        Ok(file) => file,
        Err(err) => {
            // Not fatal, and deliberately so: failing to *create a lock file* must never be the
            // reason a host stops patching. Proceeding unlocked is what this agent did before the
            // lock existed at all.
            logging::warn(&format!("could not open the privileged lock at {}: {err} — proceeding without it", path.display()));
            return Some(PrivilegedLock { _file: File::open("/dev/null").ok()? });
        }
    };

    let started = Instant::now();
    loop {
        // SAFETY: `flock` takes a file descriptor and a flag and returns a plain int; `file` owns
        // the descriptor and outlives the call.
        let result = unsafe { libc::flock(file.as_raw_fd(), libc::LOCK_EX | libc::LOCK_NB) };
        if result == 0 {
            return Some(PrivilegedLock { _file: file });
        }

        if started.elapsed() >= timeout {
            return None;
        }

        std::thread::sleep(Duration::from_millis(250));
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    /// The property the whole thing exists for: a second holder must not get in while the first
    /// still has it, and must get in once it doesn't.
    #[test]
    fn a_second_acquisition_waits_for_the_first_to_be_released() {
        let path = std::env::temp_dir().join(format!("kintsugi-lock-test-{}", std::process::id()));
        let first = OpenOptions::new().create(true).write(true).truncate(false).open(&path).unwrap();
        assert_eq!(unsafe { libc::flock(first.as_raw_fd(), libc::LOCK_EX | libc::LOCK_NB) }, 0);

        let second = OpenOptions::new().create(true).write(true).truncate(false).open(&path).unwrap();
        assert_ne!(
            unsafe { libc::flock(second.as_raw_fd(), libc::LOCK_EX | libc::LOCK_NB) },
            0,
            "the lock should be held by the first opener"
        );

        drop(first);
        assert_eq!(
            unsafe { libc::flock(second.as_raw_fd(), libc::LOCK_EX | libc::LOCK_NB) },
            0,
            "closing the first handle should release the lock"
        );

        drop(second);
        let _ = std::fs::remove_file(&path);
    }
}
