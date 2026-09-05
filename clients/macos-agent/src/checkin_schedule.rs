use std::fs;
use std::os::unix::process::CommandExt;
use std::path::Path;
use std::process::Command;
use std::time::{Duration, SystemTime, UNIX_EPOCH};

use serde::{Deserialize, Serialize};

use crate::config;
use crate::dialogs;
use crate::logging;
use crate::queue::{self, RequestKind};

/// How long the per-user process waits for the daemon to answer a "Check In Now" request. A
/// check-in is `softwareupdate -l`, the Homebrew inventory, two POSTs and possibly an agent
/// download (`self_update::DOWNLOAD_TIMEOUT`), so minutes rather than seconds — but well short of
/// `queue::REQUEST_TIMEOUT`, because the menu bar reads "Checking in…" for the whole wait and an
/// hour of that for a daemon that never answered would be worse than a reported timeout.
pub const CHECK_IN_TIMEOUT: Duration = Duration::from_secs(10 * 60);

#[derive(Debug, Clone, Serialize, Deserialize)]
struct PersistedSchedule {
    minute: u8,
}

/// Loads this host's assigned check-in minute-of-hour, assigning (and persisting) a fresh
/// pseudo-random one on first run. Deliberately doesn't touch the LaunchDaemon plist itself —
/// see `apply`, which the caller invokes separately once the whole check-in has actually
/// completed, so a plist rewrite (and the launchd reload that goes with it) never happens in the
/// middle of a check-in that isn't done yet.
pub fn load_or_assign(path: &Path) -> u8 {
    if let Some(minute) = load(path) {
        return minute;
    }

    let minute = random_minute();
    persist(path, minute);
    logging::info(&format!("assigned this host a new check-in minute: :{minute:02}"));
    minute
}

/// Picks a starting minute-of-hour pseudo-randomly — this only needs to spread the fleet's
/// check-ins across the hour so they don't all land on the same minute, not be
/// cryptographically random, so it's derived from wall-clock nanoseconds rather than pulling in a
/// `rand` dependency for one dice roll.
fn random_minute() -> u8 {
    let nanos = SystemTime::now().duration_since(UNIX_EPOCH).map(|d| d.subsec_nanos()).unwrap_or(0);
    (nanos % 60) as u8
}

fn load(path: &Path) -> Option<u8> {
    let contents = fs::read_to_string(path).ok()?;
    let schedule: PersistedSchedule = serde_json::from_str(&contents).ok()?;
    (schedule.minute < 60).then_some(schedule.minute)
}

fn persist(path: &Path, minute: u8) {
    if let Some(parent) = path.parent() {
        let _ = fs::create_dir_all(parent);
    }
    if let Ok(json) = serde_json::to_string_pretty(&PersistedSchedule { minute }) {
        if let Err(err) = fs::write(path, json) {
            logging::warn(&format!("could not persist the check-in schedule to {}: {err}", path.display()));
        }
    }
}

/// How long to wait until the next occurrence of `minute` past the hour — the same arithmetic the
/// Windows service uses to time its own loop (its `checkin_schedule::seconds_until`); here launchd
/// owns the timing and this only predicts it for the menu bar.
///
/// Never returns zero: exactly on the minute, the firing that matters is the next hour's.
pub fn seconds_until(now_epoch_seconds: u64, minute: u8) -> u64 {
    const HOUR: u64 = 3600;
    let target_second_of_hour = u64::from(minute) * 60;
    let current_second_of_hour = now_epoch_seconds % HOUR;

    if target_second_of_hour > current_second_of_hour {
        target_second_of_hour - current_second_of_hour
    } else {
        HOUR - (current_second_of_hour - target_second_of_hour)
    }
}

/// When the daemon next checks in, for the menu bar's "Next check-in" line: the next occurrence of
/// this host's persisted minute, or `None` before the daemon's first run has assigned one.
///
/// Read by the per-user process, which is not root — so `CHECKIN_SCHEDULE_PATH` has to be readable
/// by it. It is: the daemon writes it under root's default umask (`0644`) into a `root:wheel 0755`
/// directory (see packaging/install.sh). It carries nothing but a minute.
pub fn next_check_in_epoch(schedule_path: &Path) -> Option<u64> {
    let now = SystemTime::now().duration_since(UNIX_EPOCH).unwrap_or_default().as_secs();
    next_check_in_epoch_at(schedule_path, now)
}

/// The testable half of [`next_check_in_epoch`] — `now` is a parameter so the arithmetic can be
/// checked against a fixed clock.
fn next_check_in_epoch_at(schedule_path: &Path, now_epoch_seconds: u64) -> Option<u64> {
    load(schedule_path).map(|minute| now_epoch_seconds + seconds_until(now_epoch_seconds, minute))
}

/// The menu bar's "Check In Now": asks the root daemon to check in immediately rather than at this
/// host's next hourly minute — re-registering, re-reporting the inventory, and installing any newer
/// build of this agent that has been published — and tells the user how it went.
///
/// A round trip through the same queue OS updates use, because a check-in is the daemon's job and
/// this process cannot do one: registration is what carries the check-in minute the server load
/// balances, and self-update replaces a root-owned binary. The request carries nothing and asks for
/// nothing the daemon would not do on its own within the hour, so the queue's security property
/// holds trivially — the worst a forged one can do is a check-in early.
///
/// On macOS the request is really only a wake-up. launchd's `WatchPaths` starts the daemon the moment
/// the file lands, and a daemon invocation *is* a check-in (see `main::run_daemon`), so by the time
/// `queue::process_queue` reaches the request the registration has already happened and the answer
/// is a confirmation; the agent's own update check runs right after the queue drains. If that finds
/// a newer build, this process is restarted to pick it up — so the notification below may never
/// show, and the new version in the menu is the confirmation instead.
///
/// Blocks the scheduler thread for the duration. That is what keeps a second click (or a due patch
/// cycle) from running underneath a check-in — the menu greys both actions meanwhile, see
/// `tray_menu::report_check_in`.
pub fn request_now(queue_dir: &Path) {
    logging::info("asking the root daemon to check in now");
    match queue::submit(queue_dir, RequestKind::CheckIn, "", CHECK_IN_TIMEOUT) {
        Ok(result) if result.success => {
            logging::info("the root daemon checked in with the server");
            dialogs::notify("Kintsugi Patching", "Checked in with the server.");
        }
        Ok(result) => {
            logging::warn(&format!("the root daemon could not check in: {}", result.output.trim()));
            dialogs::notify("Kintsugi Patching", &format!("Check-in failed: {}", result.output.trim()));
        }
        Err(err) => {
            logging::warn(&format!("could not get the root daemon to check in: {err:#}"));
            dialogs::notify("Kintsugi Patching", &format!("Check-in failed: {err:#}"));
        }
    }
}

/// Applies a (possibly new) check-in minute: persists it locally and, only if the installed
/// LaunchDaemon plist doesn't already reflect it, rewrites that plist and reloads the job with
/// launchd so the new schedule actually takes effect. Called once, last, at the end of every
/// check-in (see `main::run_daemon`) — both for a first-ever assignment (the freshly installed
/// plist has no per-host minute baked in yet) and whenever the server hands back a different
/// minute because this one is carrying more load than others.
///
/// Safe to potentially restart the very job that's running this code only because it's always the
/// last thing a check-in does: by this point registration, application reporting, the request
/// queue, and self-update have already finished, so there's nothing left for this invocation to
/// do even if launchd tears it down right underneath it.
pub fn apply(schedule_path: &Path, minute: u8) {
    persist(schedule_path, minute);

    let plist_path = config::daemon_plist_path();
    let desired = render_plist(minute);

    let already_current = fs::read_to_string(&plist_path).map(|current| current == desired).unwrap_or(false);
    if already_current {
        return;
    }

    if let Err(err) = fs::write(&plist_path, &desired) {
        logging::warn(&format!("could not update the LaunchDaemon plist at {}: {err}", plist_path.display()));
        return;
    }

    logging::info(&format!("check-in schedule changed to :{minute:02} past every hour; reloading the LaunchDaemon"));
    reload_launchd(&plist_path);
}

/// Fully regenerates the LaunchDaemon plist rather than patching the on-disk copy in place — every
/// other field (label, binary path, log paths, `WatchPaths`, `KeepAlive`) is fixed and owned by
/// this agent, not something an admin is expected to hand-edit, so there's nothing else to
/// preserve. Omitting `Hour` from `StartCalendarInterval` and specifying only `Minute` is what
/// makes launchd fire this once every hour at that minute, rather than once a day.
fn render_plist(minute: u8) -> String {
    format!(
        r#"<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
    <key>Label</key>
    <string>{label}</string>

    <key>ProgramArguments</key>
    <array>
        <string>{binary}</string>
    </array>

    <!-- Register immediately when the daemon is loaded (i.e. on every boot). -->
    <key>RunAtLoad</key>
    <true/>

    <!-- Also register once every hour, at this host's own assigned minute (see
         checkin_schedule.rs) — deliberately not the same minute on every host, so the fleet's
         check-ins spread across the hour instead of everyone hitting the server at once. If the
         Mac is asleep at that minute, launchd runs the job as soon as it next wakes. -->
    <key>StartCalendarInterval</key>
    <dict>
        <key>Minute</key>
        <integer>{minute}</integer>
    </dict>

    <!-- Also run on demand whenever the per-user kintsugi-agent (kintsugiagent-ui, which never
         runs as root) drops a request here for a privileged step it can't do itself: installing a
         macOS software update, or running an application's upgrade script against a root-owned
         /Applications bundle. See queue::process_queue. -->
    <key>WatchPaths</key>
    <array>
        <string>{queue_dir}</string>
    </array>

    <key>StandardOutPath</key>
    <string>/var/log/kintsugi-agent.log</string>
    <key>StandardErrorPath</key>
    <string>/var/log/kintsugi-agent.err.log</string>

    <key>UserName</key>
    <string>root</string>

    <!-- Each run is a short-lived process; don't restart on exit. -->
    <key>KeepAlive</key>
    <false/>
</dict>
</plist>
"#,
        label = config::DAEMON_LAUNCHD_LABEL,
        binary = config::installed_binary_path().display(),
        minute = minute,
        queue_dir = config::queue_dir().display(),
    )
}

/// A plain `kickstart` (see `self_update::restart_launchd_jobs`) restarts the job but doesn't
/// re-read its plist from disk — only unloading and reloading it does, which is why this needs
/// the heavier `bootout` + `bootstrap` pair rather than reusing that helper.
///
/// Crucially, this can't just run `bootout` then `bootstrap` directly from here: `bootout` on the
/// daemon's own job (`system/...`) unloads and kills the very process running this code, with no
/// guarantee execution ever reaches the `bootstrap` call that actually re-registers it — which is
/// exactly what happened the first time this shipped, leaving the job unloaded entirely until it
/// was manually re-bootstrapped. `self_update::restart_launchd_jobs` can dodge this by reordering
/// two *independent* jobs, but there's no such trick available when reloading this same job — so
/// instead, the reload is handed off to a short-lived helper process that outlives this one:
/// `spawn` (not `output`) returns immediately, letting `run_daemon` finish and this process exit
/// normally first, and `process_group(0)` moves the helper into its own process group so launchd's
/// default "kill the whole process group when the job's process exits" cleanup doesn't take it out
/// too. The helper's own `sleep 1` is just a safety margin on top of that.
fn reload_launchd(plist_path: &Path) {
    let target = format!("system/{}", config::DAEMON_LAUNCHD_LABEL);
    let script = format!(
        "sleep 1; launchctl bootout '{target}' >/dev/null 2>&1; launchctl bootstrap system '{}'",
        plist_path.display()
    );

    logging::info("handing off a launchd reload to a detached helper (this process is about to exit)");

    if let Err(err) = Command::new("sh").arg("-c").arg(&script).process_group(0).spawn() {
        logging::warn(&format!("could not spawn the launchd reload helper: {err}"));
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn scratch_path(name: &str) -> std::path::PathBuf {
        std::env::temp_dir().join(format!("kintsugi-checkin-schedule-test-{name}-{}.json", std::process::id()))
    }

    #[test]
    fn random_minute_is_always_within_the_hour() {
        for _ in 0..1000 {
            assert!(random_minute() < 60);
        }
    }

    #[test]
    fn load_or_assign_persists_a_fresh_minute_when_nothing_is_saved_yet() {
        let path = scratch_path("fresh");
        let _ = fs::remove_file(&path);

        let minute = load_or_assign(&path);

        assert!(minute < 60);
        assert_eq!(load(&path), Some(minute));

        let _ = fs::remove_file(&path);
    }

    #[test]
    fn load_or_assign_returns_the_previously_persisted_minute() {
        let path = scratch_path("existing");
        persist(&path, 42);

        assert_eq!(load_or_assign(&path), 42);

        let _ = fs::remove_file(&path);
    }

    #[test]
    fn load_returns_none_for_an_out_of_range_value() {
        let path = scratch_path("out-of-range");
        fs::write(&path, r#"{"minute":60}"#).unwrap();

        assert_eq!(load(&path), None);

        let _ = fs::remove_file(&path);
    }

    #[test]
    fn seconds_until_waits_for_the_target_minute_later_this_hour() {
        // 00:10:00 UTC, target :30 -> 20 minutes.
        assert_eq!(seconds_until(600, 30), 20 * 60);
    }

    #[test]
    fn seconds_until_rolls_over_to_the_next_hour_when_the_minute_has_passed() {
        // 00:40:00 UTC, target :30 -> 50 minutes (next hour's :30).
        assert_eq!(seconds_until(40 * 60, 30), 50 * 60);
    }

    #[test]
    fn seconds_until_never_returns_zero() {
        assert_eq!(seconds_until(30 * 60, 30), 3600);
    }

    #[test]
    fn next_check_in_epoch_is_the_next_occurrence_of_the_persisted_minute() {
        let path = scratch_path("next-check-in");
        persist(&path, 30);

        // 00:10:00 UTC -> 00:30:00 UTC.
        assert_eq!(next_check_in_epoch_at(&path, 600), Some(30 * 60));

        let _ = fs::remove_file(&path);
    }

    #[test]
    fn next_check_in_epoch_is_none_before_the_daemon_has_assigned_a_minute() {
        let path = scratch_path("next-check-in-missing");
        let _ = fs::remove_file(&path);

        assert_eq!(next_check_in_epoch_at(&path, 600), None);
    }

    #[test]
    fn render_plist_fires_hourly_at_the_given_minute_with_no_hour_key() {
        let plist = render_plist(37);

        assert!(plist.contains("<key>Minute</key>"));
        assert!(plist.contains("<integer>37</integer>"));
        assert!(!plist.contains("<key>Hour</key>"));
        assert!(plist.contains(config::DAEMON_LAUNCHD_LABEL));
    }
}
