use std::fs;
use std::path::{Path, PathBuf};
use std::time::{Duration, Instant, SystemTime, UNIX_EPOCH};

use anyhow::{Context, Result};
use serde::{Deserialize, Serialize};

/// The privilege handoff between this agent's two halves — the per-user `--agent` process that
/// decides *when* to patch and shows the dialogs, and the root LaunchDaemon that can actually write
/// to `/Applications` and run `softwareupdate`.
///
/// It started as an OS-update-only channel, because on macOS the per-user process could run every
/// application upgrade itself: it holds this host's identity (unlike the Windows and Linux per-user
/// halves), and Homebrew — the first upgrade mechanism this agent had — *refuses* to run as root.
/// Then AI-authored scripts arrived for applications Homebrew does not manage, and those install
/// into `/Applications` the way the server's prompt tells them to (`installer -pkg ... -target /`,
/// replacing a bundle in place). A bundle that arrived by MDM, by a `.pkg`, or by any installer that
/// asked for an administrator password is owned by `root:wheel`, and the logged-in user's `rm`
/// against it prints `Permission denied` sixty times and leaves the old version in place. So the
/// same split the other two agents have now applies here, for the rows that need it:
///
/// - A **Homebrew** row (`UpgradeStatus::package_manager` names the manager) runs in the per-user
///   process, as it always has. Homebrew's own refusal to run as root is not negotiable, and its
///   installs are user-owned anyway.
/// - An **AI-researched** row (no package manager) is handed to the root daemon through this queue.
///   `upgrade::runs_as_root` is the one place that decision is made, and the daemon re-checks it
///   before running anything: a request naming a Homebrew row is refused rather than run as root.
///
/// The security property the queue was built around holds for the new request kind exactly as it
/// did for the old: **a request never carries anything executable**. An OS-update request has no
/// body and the daemon always runs the same fixed `softwareupdate -i -a`; an app-patch request names
/// an application and nothing else, and the daemon independently fetches that application's upgrade
/// path from the server and verifies its signature against the pinned artifact-signing key before
/// running it (see `upgrade::patch_one`). A malicious or corrupted request file can, at worst, cause
/// an already-approved upgrade to run early — never arbitrary code as root. The Windows agent's
/// `queue.rs` is this module's sibling, and its comment says the same thing for the same reason.
///
/// The directory is `root:admin 0770` (see packaging/install.sh): the logged-in administrator can
/// drop a request, and only root acts on one. The LaunchDaemon's `WatchPaths` names it, so a request
/// wakes the daemon promptly rather than waiting for the hourly check-in — see `checkin_schedule`.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum RequestKind {
    /// "Run the (already server-signed) upgrade for this one application, as root." Body: the
    /// application name, exactly as reported in the inventory.
    AppPatch,
    /// "Install pending macOS updates." No body — the daemon always runs the same fixed install,
    /// so this can never carry instructions of its own.
    OsUpdate,
}

impl RequestKind {
    /// The file extension a request of this kind is written with. Part of the on-disk protocol
    /// between the two halves: the daemon dispatches on it (see `process_queue`), so these strings
    /// are load-bearing rather than cosmetic.
    fn extension(self) -> &'static str {
        match self {
            RequestKind::AppPatch => "app-patch.request",
            RequestKind::OsUpdate => "os-update.request",
        }
    }

    fn from_file_name(file_name: &str) -> Option<Self> {
        [RequestKind::AppPatch, RequestKind::OsUpdate]
            .into_iter()
            .find(|kind| file_name.ends_with(kind.extension()))
    }
}

/// What the daemon writes back for a completed request.
#[derive(Debug, Serialize, Deserialize)]
pub struct RequestResult {
    pub success: bool,
    pub output: String,
}

/// How long the per-user process waits for the daemon to answer any request, and therefore how old
/// a request can be while somebody is still waiting on it. A macOS update can legitimately spend
/// most of this downloading; an application upgrade is usually minutes, but a large `.dmg` on a slow
/// link is the same shape of wait, so one bound serves both rather than two constants that would
/// have to be kept in the right order.
///
/// The daemon uses the same value from the other side: a request older than this has an owner that
/// gave up (`submit` removes its request on timeout, so finding one means the owner did not get
/// that far — it was killed, or the Mac went down), and running it now would start a patch with no
/// progress window and no warning. See `is_stale`.
pub const REQUEST_TIMEOUT: Duration = Duration::from_secs(60 * 60);

fn now_epoch() -> u64 {
    SystemTime::now().duration_since(UNIX_EPOCH).unwrap_or_default().as_secs()
}

fn result_path_for(request_path: &Path) -> PathBuf {
    let mut path = request_path.as_os_str().to_os_string();
    path.push(".result.json");
    PathBuf::from(path)
}

/// Drops a request into the queue and returns its path. The file name carries the process id and a
/// monotonic counter alongside the timestamp so two requests made inside the same second — which a
/// patch cycle does constantly, one per application — can't collide on a name and read each other's
/// results.
fn write_request(queue_dir: &Path, kind: RequestKind, body: &str) -> Result<PathBuf> {
    use std::sync::atomic::{AtomicU64, Ordering};
    static COUNTER: AtomicU64 = AtomicU64::new(0);

    fs::create_dir_all(queue_dir).with_context(|| format!("could not create queue directory {}", queue_dir.display()))?;

    let sequence = COUNTER.fetch_add(1, Ordering::Relaxed);
    let request_path = queue_dir.join(format!("{}-{}-{sequence}.{}", now_epoch(), std::process::id(), kind.extension()));
    fs::write(&request_path, body).context("could not write the queue request")?;
    Ok(request_path)
}

/// Submits a request and blocks (polling) until the daemon writes a result, or `timeout` elapses.
///
/// Polling rather than a directory-change notification on this side on purpose: the per-user
/// process is already blocked on this one step and has nothing else to do, and a 1-second poll of a
/// single known path costs nothing next to the install it's waiting for. The *daemon* side is the
/// one that can't afford to poll, and doesn't — launchd's `WatchPaths` starts it.
pub fn submit(queue_dir: &Path, kind: RequestKind, body: &str, timeout: Duration) -> Result<RequestResult> {
    let request_path = write_request(queue_dir, kind, body)?;
    let result_path = result_path_for(&request_path);

    let started = Instant::now();
    loop {
        if let Ok(contents) = fs::read_to_string(&result_path) {
            let _ = fs::remove_file(&result_path);
            return serde_json::from_str(&contents).context("could not parse the queue result written by the root daemon");
        }

        if started.elapsed() >= timeout {
            // Removed so an abandoned request isn't executed long after this process stopped
            // caring about it — a patch starting with no progress window and no warning is worse
            // than one that didn't start.
            let _ = fs::remove_file(&request_path);
            anyhow::bail!("timed out waiting for the root daemon to answer a {kind:?} request");
        }

        std::thread::sleep(Duration::from_secs(1));
    }
}

/// What the daemon does with one request. Implemented by `main::DaemonRequestHandler` (which owns
/// the HTTP client and this host's identity); kept as a trait so `process_queue`'s dispatch,
/// ordering and staleness logic can be tested without any of that.
pub trait RequestHandler {
    fn patch_application(&mut self, application_name: &str) -> Result<()>;
    fn install_os_updates(&mut self) -> Result<()>;
}

/// The epoch second the request in `file_name` was written — the leading digits of the name, which
/// is what `write_request` puts first so that lexicographic order is chronological.
fn request_epoch(file_name: &str) -> Option<u64> {
    let digits = file_name.split(|c: char| !c.is_ascii_digit()).next()?;
    digits.parse().ok()
}

/// Whether nobody can still be waiting on this request: it predates the current boot (its owner
/// died with the previous one — this is the case the Windows service's `discard_stale` exists for,
/// which a oneshot daemon has to detect by date instead), or it is older than the longest any owner
/// waits (`REQUEST_TIMEOUT`). Either way the answer would be written for nobody and the work would
/// start unannounced. A name that carries no readable timestamp is treated as stale too: the
/// protocol always writes one, so its absence means the file is not this agent's.
fn is_stale(file_name: &str, now: u64, boot_epoch: Option<u64>) -> bool {
    let Some(written) = request_epoch(file_name) else {
        return true;
    };
    boot_epoch.is_some_and(|boot| written < boot) || now.saturating_sub(written) > REQUEST_TIMEOUT.as_secs()
}

/// When this Mac last booted, from the kernel. `None` if the kernel will not say, in which case the
/// age check alone decides staleness.
fn boot_epoch() -> Option<u64> {
    let mut boot_time = libc::timeval { tv_sec: 0, tv_usec: 0 };
    let mut length = std::mem::size_of::<libc::timeval>();
    // SAFETY: `kern.boottime` is a struct timeval, `length` names the buffer's real size, and the
    // NUL-terminated name outlives the call.
    let status = unsafe {
        libc::sysctlbyname(
            b"kern.boottime\0".as_ptr().cast::<libc::c_char>(),
            (&mut boot_time as *mut libc::timeval).cast::<libc::c_void>(),
            &mut length,
            std::ptr::null_mut(),
            0,
        )
    };
    (status == 0 && boot_time.tv_sec > 0).then(|| boot_time.tv_sec as u64)
}

/// The daemon's half of the handoff: run once per invocation (it's triggered on demand via
/// `WatchPaths` on the queue directory — see the LaunchDaemon plist — so it doesn't need its own
/// persistent loop). Processes every pending request found, oldest first, so a request dropped
/// while the daemon was already mid-run isn't silently skipped; discards, without running, any
/// request nobody can still be waiting on — see `is_stale`.
///
/// Runs to completion for each request before moving on, deliberately: two installers at once
/// would fight over `/Applications` and the per-user process asks for one application at a time
/// anyway.
pub fn process_queue(queue_dir: &Path, handler: &mut impl RequestHandler) {
    let Ok(entries) = fs::read_dir(queue_dir) else {
        return;
    };

    let mut requests: Vec<PathBuf> = entries
        .filter_map(|entry| entry.ok())
        .map(|entry| entry.path())
        .filter(|path| {
            path.file_name()
                .and_then(|name| name.to_str())
                .is_some_and(|name| RequestKind::from_file_name(name).is_some())
        })
        .collect();
    // Lexicographic order is chronological here because every name starts with a fixed-width epoch
    // — good until the year 2286, by which point the sort key gains a digit.
    requests.sort();

    let now = now_epoch();
    let boot = boot_epoch();

    for request_path in requests {
        let Some((file_name, kind)) = request_path
            .file_name()
            .and_then(|name| name.to_str())
            .and_then(|name| RequestKind::from_file_name(name).map(|kind| (name, kind)))
        else {
            continue;
        };

        if is_stale(file_name, now, boot) {
            crate::logging::warn(&format!(
                "discarding a {kind:?} request nobody is waiting on any more: {}",
                request_path.display()
            ));
            let _ = fs::remove_file(&request_path);
            continue;
        }

        crate::logging::info(&format!("processing {kind:?} request: {}", request_path.display()));

        let body = fs::read_to_string(&request_path).unwrap_or_default();
        let result = run_request(kind, body.trim(), handler);

        crate::logging::info(&format!(
            "{kind:?} request finished: success={} {}",
            result.success,
            result.output.trim()
        ));

        // The request is removed first, and the result written second: the per-user process waits
        // on the result file, so writing it while the request still existed would leave a window
        // where a crash here re-runs a request that has already been answered.
        let _ = fs::remove_file(&request_path);
        if let Ok(json) = serde_json::to_string(&result) {
            if let Err(err) = fs::write(result_path_for(&request_path), json) {
                crate::logging::warn(&format!("could not write the queue result: {err}"));
            }
        }
    }
}

fn run_request(kind: RequestKind, body: &str, handler: &mut impl RequestHandler) -> RequestResult {
    match kind {
        RequestKind::AppPatch => {
            if body.is_empty() {
                return RequestResult { success: false, output: "no application name in the request".to_string() };
            }
            match handler.patch_application(body) {
                Ok(()) => RequestResult { success: true, output: format!("patched {body}") },
                Err(err) => RequestResult { success: false, output: format!("{err:#}") },
            }
        }
        RequestKind::OsUpdate => match handler.install_os_updates() {
            Ok(()) => RequestResult { success: true, output: "installed pending macOS updates".to_string() },
            Err(err) => RequestResult { success: false, output: format!("{err:#}") },
        },
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    /// Records what it was asked to do and answers however the test needs — the whole point of
    /// `RequestHandler` being a trait.
    #[derive(Default)]
    struct RecordingHandler {
        patched: Vec<String>,
        os_updates_installed: usize,
        fail_patches: bool,
    }

    impl RequestHandler for RecordingHandler {
        fn patch_application(&mut self, application_name: &str) -> Result<()> {
            self.patched.push(application_name.to_string());
            if self.fail_patches {
                anyhow::bail!("the upgrade script exited non-zero");
            }
            Ok(())
        }

        fn install_os_updates(&mut self) -> Result<()> {
            self.os_updates_installed += 1;
            Ok(())
        }
    }

    fn scratch_dir(name: &str) -> PathBuf {
        let dir = std::env::temp_dir().join(format!("kintsugi-queue-test-{name}-{}", std::process::id()));
        let _ = fs::remove_dir_all(&dir);
        fs::create_dir_all(&dir).unwrap();
        dir
    }

    fn read_result(request: &Path) -> RequestResult {
        serde_json::from_str(&fs::read_to_string(result_path_for(request)).unwrap()).unwrap()
    }

    #[test]
    fn request_kind_round_trips_through_its_file_name() {
        for kind in [RequestKind::AppPatch, RequestKind::OsUpdate] {
            let name = format!("1700000000-42-0.{}", kind.extension());
            assert_eq!(RequestKind::from_file_name(&name), Some(kind));
        }
    }

    #[test]
    fn request_kind_ignores_a_result_file_and_anything_unrelated() {
        // process_queue must never treat its own result files as new work, or answering a request
        // would immediately queue another one.
        assert_eq!(RequestKind::from_file_name("1700000000-42-0.app-patch.request.result.json"), None);
        assert_eq!(RequestKind::from_file_name("notes.txt"), None);
    }

    #[test]
    fn process_queue_passes_the_application_name_through_for_an_app_patch() {
        let dir = scratch_dir("patch");
        let request = write_request(&dir, RequestKind::AppPatch, "Ollama").unwrap();
        let mut handler = RecordingHandler::default();

        process_queue(&dir, &mut handler);

        assert_eq!(handler.patched, vec!["Ollama".to_string()]);
        assert!(read_result(&request).success);
    }

    #[test]
    fn process_queue_reports_a_failed_patch_rather_than_swallowing_it() {
        let dir = scratch_dir("patch-fail");
        let request = write_request(&dir, RequestKind::AppPatch, "Ollama").unwrap();
        let mut handler = RecordingHandler { fail_patches: true, ..Default::default() };

        process_queue(&dir, &mut handler);

        let result = read_result(&request);
        assert!(!result.success);
        assert!(result.output.contains("exited non-zero"));
    }

    #[test]
    fn process_queue_rejects_an_app_patch_with_no_application_name() {
        let dir = scratch_dir("patch-empty");
        let request = write_request(&dir, RequestKind::AppPatch, "  ").unwrap();
        let mut handler = RecordingHandler::default();

        process_queue(&dir, &mut handler);

        assert!(!read_result(&request).success);
        assert!(handler.patched.is_empty());
    }

    #[test]
    fn process_queue_removes_every_request_it_handled() {
        // A request left behind would be re-run on the daemon's next pass — for an app patch, that
        // means installing the same upgrade over and over.
        let dir = scratch_dir("cleanup");
        write_request(&dir, RequestKind::OsUpdate, "").unwrap();
        let mut handler = RecordingHandler::default();

        process_queue(&dir, &mut handler);
        process_queue(&dir, &mut handler);

        assert_eq!(handler.os_updates_installed, 1);
    }

    #[test]
    fn process_queue_handles_requests_oldest_first() {
        let dir = scratch_dir("ordering");
        // Written by hand rather than via write_request so the timestamps are controlled — recent
        // enough not to be stale, whatever the clock says.
        let base = now_epoch() - 30;
        fs::write(dir.join(format!("{}-1-0.app-patch.request", base + 2)), "Third").unwrap();
        fs::write(dir.join(format!("{}-1-0.app-patch.request", base)), "First").unwrap();
        fs::write(dir.join(format!("{}-1-0.app-patch.request", base + 1)), "Second").unwrap();
        let mut handler = RecordingHandler::default();

        process_queue(&dir, &mut handler);

        assert_eq!(handler.patched, vec!["First".to_string(), "Second".to_string(), "Third".to_string()]);
    }

    #[test]
    fn process_queue_discards_a_request_nobody_is_waiting_on() {
        // The failure this guards: the Mac was shut down mid-patch, and at the next boot the daemon
        // finds the request its owner never got to remove. Running it would install an upgrade with
        // nothing on screen saying so.
        let dir = scratch_dir("stale");
        let path = dir.join(format!("{}-1-0.app-patch.request", now_epoch() - REQUEST_TIMEOUT.as_secs() - 60));
        fs::write(&path, "Ollama").unwrap();
        let mut handler = RecordingHandler::default();

        process_queue(&dir, &mut handler);

        assert!(handler.patched.is_empty());
        assert!(!path.exists(), "a stale request must be removed, not left for the next pass");
        assert!(!result_path_for(&path).exists(), "nobody is waiting, so no result is written");
    }

    #[test]
    fn is_stale_rejects_anything_from_before_this_boot_or_older_than_the_wait() {
        let now = 1_800_000_000;
        let boot = Some(1_799_990_000);

        assert!(!is_stale("1799998000-1-0.app-patch.request", now, boot), "half an hour old, written after boot");
        // Recent enough by age alone; only the boot check can catch it.
        assert!(is_stale("1799998999-1-0.app-patch.request", now, Some(1_799_999_000)), "written one second before boot");
        assert!(is_stale(&format!("{}-1-0.os-update.request", now - REQUEST_TIMEOUT.as_secs() - 1), now, None));
        assert!(!is_stale(&format!("{}-1-0.os-update.request", now - REQUEST_TIMEOUT.as_secs()), now, None));
        // The OS-update-only queue named its requests with the bare epoch; those files are still
        // dated, so a daemon self-updated mid-wait still answers one that is being waited on.
        assert!(!is_stale("1799998000.os-update.request", now, boot));
        assert!(is_stale("untimestamped.app-patch.request", now, boot));
    }

    #[test]
    fn submit_times_out_and_removes_its_request_when_no_daemon_is_running() {
        // The failure mode this guards: the daemon never answers. The per-user process has to give
        // up *and* clean up, or the request would be executed unannounced whenever the daemon next
        // runs.
        let dir = scratch_dir("timeout");

        let result = submit(&dir, RequestKind::OsUpdate, "", Duration::from_secs(0));

        assert!(result.is_err());
        assert_eq!(fs::read_dir(&dir).unwrap().count(), 0);
    }

    #[test]
    fn two_requests_made_in_the_same_second_do_not_collide() {
        // A patch cycle submits one request per application back to back; colliding names would
        // make one application read another's result.
        let dir = scratch_dir("collision");

        let first = write_request(&dir, RequestKind::AppPatch, "A").unwrap();
        let second = write_request(&dir, RequestKind::AppPatch, "B").unwrap();

        assert_ne!(first, second);
        assert_eq!(fs::read_dir(&dir).unwrap().count(), 2);
    }
}
