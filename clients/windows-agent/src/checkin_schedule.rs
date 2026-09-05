use std::fs;
use std::path::Path;
use std::time::{Duration, SystemTime, UNIX_EPOCH};

use serde::{Deserialize, Serialize};

use crate::dialogs;
use crate::logging;
use crate::queue::{self, RequestKind};

/// How long the tray process waits for the service to answer a "Check In Now" request. A check-in
/// is a Windows Update search, the winget and Chocolatey inventory, two POSTs and possibly an agent
/// download (`self_update::DOWNLOAD_TIMEOUT`), so minutes rather than seconds — the same shape as
/// `patch_cycle::PLAN_TIMEOUT`, with headroom for the download. The menu reads "Checking in…" for
/// the whole wait, so an hour of that for a service that never answered would be worse than a
/// reported timeout.
pub const CHECK_IN_TIMEOUT: Duration = Duration::from_secs(10 * 60);

#[derive(Debug, Clone, Serialize, Deserialize)]
struct PersistedSchedule {
    minute: u8,
}

/// Loads this host's assigned check-in minute-of-hour, assigning (and persisting) a fresh
/// pseudo-random one on first run.
///
/// Persisted, rather than recomputed each start, for the same reason as on macOS: the point of a
/// per-host minute is that the fleet's check-ins spread evenly across the hour, which only holds if
/// each host keeps the minute it was given. A machine that rerolled on every service restart would
/// drift, and the server's own load balancing (see `ICheckInLoadBalancer`) would be reacting to a
/// number that no longer means anything.
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
/// check-ins across the hour so they don't all land on the same minute, not be cryptographically
/// random, so it's derived from wall-clock nanoseconds rather than pulling in a `rand` dependency
/// for one dice roll.
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

/// Applies a (possibly new) check-in minute the server handed back.
///
/// Considerably simpler than the macOS agent's equivalent, and the difference is worth naming.
/// There, the daemon is a short-lived process that launchd re-invokes on a schedule baked into a
/// plist, so changing the minute means rewriting that plist and asking launchd to reload the job —
/// which, since the job being reloaded is the one running the code, has to be handed off to a
/// detached helper to avoid the process being killed mid-reload. A Windows service is resident: it
/// owns its own timing (see `service::next_checkin_delay`), so a new minute takes effect on the
/// next tick of a loop that is already running, and there is nothing to rewrite or restart.
///
/// Still called last in a check-in, matching the macOS ordering, so a minute change never lands
/// halfway through one.
pub fn apply(schedule_path: &Path, current_minute: u8, target_minute: u8) -> u8 {
    if target_minute >= 60 || target_minute == current_minute {
        return current_minute;
    }

    persist(schedule_path, target_minute);
    logging::info(&format!(
        "the server moved this host's check-in from :{current_minute:02} to :{target_minute:02} past the hour"
    ));
    target_minute
}

/// How long to wait until the next occurrence of `minute` past the hour.
///
/// Split out from the service loop so it can be tested against a fixed clock — a bug here means
/// either a host that never checks in or one that hammers the server every second, and neither is
/// something to discover in production.
///
/// Never returns zero: landing exactly on the target minute would otherwise busy-loop through that
/// whole minute, re-running a full check-in each time round.
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

/// When the service next checks in, for the menu's "Next check-in" line: the next occurrence of
/// this host's persisted minute, or `None` before the service's first run has assigned one. The
/// same arithmetic `service::run_loop` uses to time itself, so the two agree by construction.
///
/// Read by the tray process, which is not SYSTEM — so `checkin_schedule_path` has to be readable by
/// it. It is, the same way `policy_cache_path` is: a file the service creates under `%ProgramData%`
/// inherits that directory's default ACL, which grants `BUILTIN\Users` read. It carries nothing
/// but a minute.
pub fn next_check_in_epoch(schedule_path: &Path) -> Option<u64> {
    let now = SystemTime::now().duration_since(UNIX_EPOCH).unwrap_or_default().as_secs();
    next_check_in_epoch_at(schedule_path, now)
}

/// The testable half of [`next_check_in_epoch`] — `now` is a parameter so the arithmetic can be
/// checked against a fixed clock.
fn next_check_in_epoch_at(schedule_path: &Path, now_epoch_seconds: u64) -> Option<u64> {
    load(schedule_path).map(|minute| now_epoch_seconds + seconds_until(now_epoch_seconds, minute))
}

/// The menu's "Check In Now": asks the service to check in immediately rather than at this host's
/// next hourly minute — re-registering, re-reporting the inventory, and installing any newer build
/// of this agent that has been published — and tells the user how it went.
///
/// A round trip through the same queue every other privileged step uses, because a check-in is the
/// service's job and this process cannot do one: it holds no identity to register with. The request
/// carries nothing and asks for nothing the service would not do on its own within the hour, so the
/// queue's security property holds trivially — the worst a forged one can do is a check-in early.
///
/// Unlike the macOS agent, where the request is only a wake-up for a daemon that checks in on every
/// start, the service here is resident and runs the check-in *inside* the request
/// (`service::Agent::check_in`), so the answer reflects that check-in. If it installs a newer agent,
/// `self_update` restarts this process to pick it up — so the notification below may never show,
/// and the new version in the menu is the confirmation instead.
///
/// Blocks the scheduler thread for the duration. That is what keeps a second click (or a due patch
/// cycle) from running underneath a check-in — the menu greys both actions meanwhile, see
/// `tray_menu::report_check_in`.
pub fn request_now(queue_dir: &Path) {
    logging::info("asking the agent service to check in now");
    match queue::submit(queue_dir, RequestKind::CheckIn, "", CHECK_IN_TIMEOUT) {
        Ok(result) if result.success => {
            logging::info("the agent service checked in with the server");
            dialogs::notify("Kintsugi Patching", "Checked in with the server.");
        }
        Ok(result) => {
            logging::warn(&format!("the agent service could not check in: {}", result.output.trim()));
            dialogs::notify("Kintsugi Patching", &format!("Check-in failed: {}", result.output.trim()));
        }
        Err(err) => {
            logging::warn(&format!("could not get the agent service to check in: {err:#}"));
            dialogs::notify("Kintsugi Patching", &format!("Check-in failed: {err:#}"));
        }
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
    fn apply_persists_and_returns_a_new_minute() {
        let path = scratch_path("apply-new");
        persist(&path, 10);

        assert_eq!(apply(&path, 10, 25), 25);
        assert_eq!(load(&path), Some(25));

        let _ = fs::remove_file(&path);
    }

    #[test]
    fn apply_is_a_no_op_when_the_minute_is_unchanged_or_invalid() {
        let path = scratch_path("apply-noop");
        persist(&path, 10);

        assert_eq!(apply(&path, 10, 10), 10);
        assert_eq!(apply(&path, 10, 60), 10);
        assert_eq!(load(&path), Some(10));

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
        // Exactly on the target minute: waiting zero would re-run a full check-in on every loop
        // iteration for the whole of that minute.
        assert_eq!(seconds_until(30 * 60, 30), 3600);
    }

    #[test]
    fn seconds_until_is_always_within_the_hour_for_every_minute_and_offset() {
        for minute in 0u8..60 {
            for second_of_hour in [0u64, 1, 59, 1799, 3599] {
                let wait = seconds_until(second_of_hour, minute);
                assert!(wait > 0 && wait <= 3600, "minute={minute} second_of_hour={second_of_hour} wait={wait}");
            }
        }
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
}
