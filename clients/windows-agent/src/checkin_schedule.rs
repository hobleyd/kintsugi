use std::fs;
use std::path::Path;
use std::time::{SystemTime, UNIX_EPOCH};

use serde::{Deserialize, Serialize};

use crate::logging;

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
}
