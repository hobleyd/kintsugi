use std::fs;
use std::os::unix::process::CommandExt;
use std::path::Path;
use std::process::Command;
use std::time::{SystemTime, UNIX_EPOCH};

use serde::{Deserialize, Serialize};

use crate::config;
use crate::logging;

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
    fn render_plist_fires_hourly_at_the_given_minute_with_no_hour_key() {
        let plist = render_plist(37);

        assert!(plist.contains("<key>Minute</key>"));
        assert!(plist.contains("<integer>37</integer>"));
        assert!(!plist.contains("<key>Hour</key>"));
        assert!(plist.contains(config::DAEMON_LAUNCHD_LABEL));
    }
}
