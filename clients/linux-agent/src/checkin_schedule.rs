use std::fs;
use std::os::unix::fs::PermissionsExt;
use std::path::Path;
use std::process::Command;
use std::time::{Duration, SystemTime, UNIX_EPOCH};

use serde::{Deserialize, Serialize};

use crate::config;
use crate::dialogs;
use crate::logging;
use crate::queue::{self, RequestKind};

/// How long the per-user process waits for the root service to answer a "Check In Now" request. A
/// check-in is the OS update check (`apt-get --just-print upgrade` and friends), the Flatpak and
/// Snap inventory, two POSTs and possibly an agent download (`self_update::DOWNLOAD_TIMEOUT`), so
/// minutes rather than seconds — but well short of `main::APP_PATCH_TIMEOUT`, because the menu reads
/// "Checking in…" for the whole wait and an hour of that for a service that never answered would
/// be worse than a reported timeout. Longer than `queue::MAX_REQUEST_AGE` is fine: that age is
/// measured on a request nobody has *picked up* yet, and the `.path` unit picks one up in seconds.
pub const CHECK_IN_TIMEOUT: Duration = Duration::from_secs(10 * 60);

#[derive(Debug, Clone, Serialize, Deserialize)]
struct PersistedSchedule {
    minute: u8,
}

/// Loads this host's assigned check-in minute-of-hour, assigning (and persisting) a fresh
/// pseudo-random one on first run. Deliberately doesn't touch the timer unit itself — see
/// `apply`, which the caller invokes separately once the whole check-in has actually completed,
/// so a unit rewrite (and the systemd reload that goes with it) never happens in the middle of a
/// check-in that isn't done yet.
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
            return;
        }
        // Explicitly, rather than left to root's umask, for the same reason `policy::fetch` does it
        // to the policy cache: the per-user process reads this file for the menu's "Next check-in"
        // line (see `next_check_in_epoch`), and it carries nothing but a minute.
        let _ = fs::set_permissions(path, fs::Permissions::from_mode(0o644));
    }
}

/// How long to wait until the next occurrence of `minute` past the hour — the same arithmetic the
/// Windows service uses to time its own loop (its `checkin_schedule::seconds_until`); here systemd
/// owns the timing and this only predicts it for the menu.
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

/// When the root service next checks in, for the menu's "Next check-in" line: the next occurrence
/// of this host's persisted minute, or `None` before the service's first run has assigned one.
///
/// Read by the per-user process, which is not root — so `CHECKIN_SCHEDULE_PATH` has to be reachable
/// and readable by it. It is: the state directory is `0711` (traverse-only, see
/// `config::STATE_DIR_MODE`) and `persist` writes the file `0644`. A host whose service predates
/// that mode shows "not yet scheduled" until its next check-in rewrites the file, which
/// `self_update` guarantees happens without anyone reinstalling.
pub fn next_check_in_epoch(schedule_path: &Path) -> Option<u64> {
    let now = SystemTime::now().duration_since(UNIX_EPOCH).unwrap_or_default().as_secs();
    next_check_in_epoch_at(schedule_path, now)
}

/// The testable half of [`next_check_in_epoch`] — `now` is a parameter so the arithmetic can be
/// checked against a fixed clock.
fn next_check_in_epoch_at(schedule_path: &Path, now_epoch_seconds: u64) -> Option<u64> {
    load(schedule_path).map(|minute| now_epoch_seconds + seconds_until(now_epoch_seconds, minute))
}

/// The menu's "Check In Now": asks the root service to check in immediately rather than at this
/// host's next hourly minute — re-registering, re-reporting the inventory, and installing any newer
/// build of this agent that has been published — and tells the user how it went.
///
/// A round trip through the same queue every other privileged step uses, because a check-in is the
/// root service's job and this process cannot do one: it holds no identity to register with. The
/// request carries nothing and asks for nothing the service would not do on its own within the
/// hour, so the queue's security property holds trivially — the worst a forged one can do is a
/// check-in early.
///
/// As on Windows and unlike macOS (where the request is only a wake-up for a daemon that checks in
/// on every start), the queue service runs the check-in *inside* the request (`main::check_in`,
/// under the privileged lock it already holds), so the answer reflects that check-in. If it installs
/// a newer agent, `self_update` restarts this process to pick it up — so the notification below may
/// never show, and the new version in the menu is the confirmation instead.
///
/// Blocks the scheduler thread for the duration. That is what keeps a second click (or a due patch
/// cycle) from running underneath a check-in — the menu greys both actions meanwhile, see
/// `tray_menu::report_check_in`.
pub fn request_now(queue_dir: &Path) {
    logging::info("asking the kintsugi-agent service to check in now");
    match queue::submit(queue_dir, RequestKind::CheckIn, "", CHECK_IN_TIMEOUT) {
        Ok(result) if result.success => {
            logging::info("the kintsugi-agent service checked in with the server");
            dialogs::notify("Kintsugi Patching", "Checked in with the server.");
        }
        Ok(result) => {
            logging::warn(&format!("the kintsugi-agent service could not check in: {}", result.output.trim()));
            dialogs::notify("Kintsugi Patching", &format!("Check-in failed: {}", result.output.trim()));
        }
        Err(err) => {
            logging::warn(&format!("could not get the kintsugi-agent service to check in: {err:#}"));
            dialogs::notify("Kintsugi Patching", &format!("Check-in failed: {err:#}"));
        }
    }
}

/// Applies a (possibly new) check-in minute: persists it locally and, only if the installed timer
/// unit doesn't already reflect it, rewrites that unit and reloads it with systemd so the new
/// schedule actually takes effect. Called once, last, at the end of every check-in (see
/// `main::run_daemon`) — both for a first-ever assignment (the freshly installed timer has no
/// per-host minute baked in yet) and whenever the server hands back a different minute because
/// this one is carrying more load than others.
pub fn apply(schedule_path: &Path, minute: u8) {
    persist(schedule_path, minute);

    let unit_path = config::timer_unit_path();
    let desired = render_timer_unit(minute);

    let already_current = fs::read_to_string(&unit_path).map(|current| current == desired).unwrap_or(false);
    if already_current {
        return;
    }

    if let Err(err) = fs::write(&unit_path, &desired) {
        logging::warn(&format!("could not update the timer unit at {}: {err}", unit_path.display()));
        return;
    }

    logging::info(&format!("check-in schedule changed to :{minute:02} past every hour; reloading the timer"));
    reload_timer();
}

/// Fully regenerates the timer unit rather than patching the on-disk copy in place — every other
/// field is fixed and owned by this agent, not something an admin is expected to hand-edit, so
/// there's nothing else to preserve.
///
/// `OnCalendar=*-*-* *:MM:00` is systemd's spelling of the LaunchDaemon plist's
/// `StartCalendarInterval` with only a `Minute` key: once an hour, at this host's own minute.
/// `OnBootSec` is the `RunAtLoad` half — a check-in shortly after boot, delayed just long enough
/// that the network is actually up. `Persistent=true` is what covers a host that was switched off
/// at its minute, the same way launchd runs a missed job as soon as the Mac next wakes.
fn render_timer_unit(minute: u8) -> String {
    format!(
        r#"# Managed by kintsugi-agent — regenerated whenever this host's check-in minute changes.
# Hand edits are overwritten; see clients/linux-agent/src/checkin_schedule.rs.
[Unit]
Description=Kintsugi patching agent check-in schedule

[Timer]
# Once an hour, at this host's own assigned minute (see checkin_schedule.rs) — deliberately not
# the same minute on every host, so the fleet's check-ins spread across the hour instead of
# everyone hitting the server at once.
OnCalendar=*-*-* *:{minute:02}:00

# And once shortly after boot, far enough in that the network is up.
OnBootSec=2min

# If the host was off at its minute, run as soon as it next comes up rather than skipping the
# hour entirely.
Persistent=true

Unit={service}

[Install]
WantedBy=timers.target
"#,
        minute = minute,
        service = config::SERVICE_UNIT,
    )
}

/// Reloads systemd's view of the unit files and restarts the timer so the new `OnCalendar` takes
/// effect.
///
/// The macOS agent has to hand its equivalent off to a detached helper process, because
/// `launchctl bootout` on the daemon's own job kills the very process making the call. There is
/// no such hazard here: the timer is a *separate unit* from the service running this code, so
/// restarting it doesn't touch this process at all. `--no-block` on the restart is still worth
/// having — it means this call returns as soon as the job is queued rather than waiting on
/// systemd to run it, which is what would otherwise let a busy job queue stall the tail end of a
/// check-in.
fn reload_timer() {
    // Must be synchronous and must come first: systemd serves the old, cached copy of the unit
    // file until it's told to re-read from disk, so a restart without this would faithfully
    // restart the *previous* schedule.
    run_systemctl(&["daemon-reload"]);
    run_systemctl(&["--no-block", "restart", config::TIMER_UNIT]);
}

fn run_systemctl(args: &[&str]) {
    match Command::new("systemctl").args(args).output() {
        Ok(output) if output.status.success() => {}
        Ok(output) => logging::warn(&format!(
            "systemctl {} exited with {}: {}",
            args.join(" "),
            output.status,
            String::from_utf8_lossy(&output.stderr).trim()
        )),
        Err(err) => logging::warn(&format!("failed to run systemctl {}: {err}", args.join(" "))),
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
    fn persist_leaves_the_schedule_readable_by_the_per_user_process() {
        let path = scratch_path("mode");
        persist(&path, 5);

        let mode = fs::metadata(&path).unwrap().permissions().mode() & 0o777;
        assert_eq!(mode, 0o644, "the menu's \"Next check-in\" line reads this file as an unprivileged user");

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
    fn next_check_in_epoch_is_none_before_the_service_has_assigned_a_minute() {
        let path = scratch_path("next-check-in-missing");
        let _ = fs::remove_file(&path);

        assert_eq!(next_check_in_epoch_at(&path, 600), None);
    }

    #[test]
    fn render_timer_unit_fires_hourly_at_the_given_minute() {
        let unit = render_timer_unit(37);

        assert!(unit.contains("OnCalendar=*-*-* *:37:00"));
        assert!(unit.contains("Persistent=true"));
        assert!(unit.contains(&format!("Unit={}", config::SERVICE_UNIT)));
    }

    /// systemd's calendar syntax is positional: ":7:00" would be hour 7, not minute 7. The
    /// zero-padding in `render_timer_unit` is what keeps a single-digit minute in the right field.
    #[test]
    fn render_timer_unit_zero_pads_a_single_digit_minute() {
        assert!(render_timer_unit(7).contains("OnCalendar=*-*-* *:07:00"));
        assert!(render_timer_unit(0).contains("OnCalendar=*-*-* *:00:00"));
    }
}
