//! What this Mac's own log says about it shutting down by itself, told to the server so a device that
//! is failing is noticed before somebody's meeting is lost to it.
//!
//! **Read-only, and small.** It reads the unified log's "Previous shutdown cause" line (written at every
//! boot) and the kernel panic reports macOS keeps, and sends only a one-line source and a short excerpt
//! per event, never a log. A shutdown the system or a person asked for is not an event.
//!
//! **Hardware.** macOS has no equivalent of Windows' WHEA or Linux's machine-check lines, so hardware
//! comes from three places that do exist:
//! - a kernel panic report that names a hardware part (the SSD controller, the SMC, a thermal sensor,
//!   uncorrectable memory) is also reported as a fatal hardware error, with that part named;
//! - the internal disk's S.M.A.R.T. status, when it says the disk is failing (fatal);
//! - the battery's condition, when it is anything but Normal (to watch).
//! The last two are a state, not an event, so each is reported when it appears and again every 7 days
//! while it lasts, so a device that stays unhealthy does not age out of the 30-day window.
//!
//! **What it cannot see.** Corrected errors in memory or the CPU are not exposed to a process at all.
//! The unified log keeps a limited history (days, not months), so the first report may reach back less
//! than 30 days; the server keeps what it has been sent.
//!
//! Wording and layout of `system_profiler` and of panic reports are Apple's and change between macOS
//! versions; anything that does not match is ignored, never guessed at.
//!
//! Shutdown cause codes are Apple's and are not documented: `5` is a normal shutdown. `0` (power lost)
//! and `3` (the power button held down) and every negative code (a watchdog, a temperature or power
//! fault) are reported. Any other positive code is left alone rather than guessed at.

use std::path::Path;
use std::process::Command;

use anyhow::{Context, Result};
use serde::Serialize;
use time::format_description::well_known::Rfc3339;
use time::OffsetDateTime;

use crate::config::{self, Config};
use crate::logging;

/// How far back the first report looks. Later ones start where the last one finished.
const FIRST_LOOKBACK_SECONDS: i64 = 30 * 24 * 3600;

/// The most events of one kind in one report, newest first.
const MAX_PER_KIND: usize = 20;

const MAX_DETAIL: usize = 300;

/// Only the kernel writes this line at boot. Without `process == "kernel"` any program on the Mac could
/// log the same words (`logger "Previous shutdown cause: -62"`) and make the device look unstable.
pub(crate) const SHUTDOWN_CAUSE_PREDICATE: &str = "process == \"kernel\" AND eventMessage CONTAINS \"Previous shutdown cause\"";

/// Where macOS keeps the reports of kernel panics.
const PANIC_REPORT_DIR: &str = "/Library/Logs/DiagnosticReports";

#[derive(Debug, Clone, Serialize, PartialEq, Eq)]
#[serde(rename_all = "camelCase")]
pub struct HealthEvent {
    /// RFC 3339, UTC.
    pub occurred_utc: String,
    /// "UnexpectedShutdown", "HardwareErrorFatal" or "HardwareErrorCorrected": the server's own names.
    pub kind: &'static str,
    pub source: String,
    pub detail: Option<String>,
}

#[derive(Debug, Serialize)]
#[serde(rename_all = "camelCase")]
struct ReportHealthEventsRequest<'a> {
    serial_number: &'a str,
    events: &'a [HealthEvent],
}

/// Reads anything new, and tells the server. Best-effort: a failure here is logged and never fails the
/// check-in it rides on. The point in time reached is only remembered after the server accepted the
/// report, so a failed send is tried again with the same window.
pub fn report_new_events(client: &reqwest::blocking::Client, config: &Config, serial_number: &str) {
    if let Err(err) = try_report(client, config, serial_number, &config::health_state_path()) {
        logging::warn(&format!("could not report device health: {err:#}"));
    }
}

fn try_report(client: &reqwest::blocking::Client, config: &Config, serial_number: &str, state: &Path) -> Result<()> {
    let now = now_epoch();
    let since = read_state(state).unwrap_or(now - FIRST_LOOKBACK_SECONDS).max(now - FIRST_LOOKBACK_SECONDS);

    let mut events = collect_since(since, now);

    // A disk or battery that is unhealthy is a state: reported when it appears, and again each week.
    let conditions_path = conditions_state_path(state);
    let conditions = hardware_conditions(now);
    let reported_before = read_conditions_state(&conditions_path);
    let due = conditions_due(&conditions, &reported_before, now);
    events.extend(due.iter().cloned());
    if !events.is_empty() {
        let request = ReportHealthEventsRequest { serial_number, events: &events };
        let response = client.post(config.health_events_url()).json(&request).send().context("the report could not be sent")?;
        if !response.status().is_success() {
            anyhow::bail!("the server rejected the report (HTTP {})", response.status());
        }
        logging::info(&format!("reported {} device health event(s)", events.len()));
    }

    // Only once the server has accepted the report, like the time.
    let _ = write_conditions_state(&conditions_path, &next_conditions_state(&conditions, &reported_before, &due, now));
    write_state(state, now)
}

fn now_epoch() -> i64 {
    std::time::SystemTime::now()
        .duration_since(std::time::UNIX_EPOCH)
        .map(|d| d.as_secs() as i64)
        .unwrap_or(0)
}

fn read_state(path: &Path) -> Option<i64> {
    std::fs::read_to_string(path).ok()?.trim().parse().ok()
}

fn write_state(path: &Path, epoch: i64) -> Result<()> {
    std::fs::write(path, epoch.to_string()).with_context(|| format!("could not write {}", path.display()))
}

/// Everything new since `since` (epoch seconds).
fn collect_since(since: i64, now: i64) -> Vec<HealthEvent> {
    let mut events = shutdown_cause_events(since, now);
    events.extend(panic_report_events(since));
    cap_newest(events)
}

fn shutdown_cause_events(since: i64, now: i64) -> Vec<HealthEvent> {
    // `log show` takes a span back from now, not a point in time. A whole hour more than needed, and the
    // lines older than `since` are dropped below.
    let hours = ((now - since).max(0) / 3600) + 2;
    let output = Command::new("/usr/bin/log")
        .args(["show", "--style", "syslog", "--last", &format!("{hours}h"), "--predicate", SHUTDOWN_CAUSE_PREDICATE])
        .env("LC_ALL", "C")
        .output();
    let Ok(output) = output else {
        return Vec::new();
    };
    if !output.status.success() {
        return Vec::new();
    }
    String::from_utf8_lossy(&output.stdout)
        .lines()
        .filter_map(classify_shutdown_line)
        .filter(|event| epoch_of(&event.occurred_utc).is_some_and(|t| t >= since))
        .collect()
}

fn panic_report_events(since: i64) -> Vec<HealthEvent> {
    let Ok(entries) = std::fs::read_dir(PANIC_REPORT_DIR) else {
        return Vec::new();
    };
    entries
        .flatten()
        .flat_map(|entry| {
            let name = entry.file_name().to_string_lossy().into_owned();
            // A real file only: the folder is writable by administrators, and a named pipe or a link to
            // another file under a panic report's name must not be opened (a pipe would block this
            // check-in for good, a link would have a root process read a file it was not pointed at).
            // `DirEntry::file_type` does not follow links.
            if !is_panic_report_name(&name) || !entry.file_type().is_ok_and(|t| t.is_file()) {
                return Vec::new();
            }
            let Some(modified) = entry.metadata().ok().and_then(|m| m.modified().ok()).and_then(|m| m.duration_since(std::time::UNIX_EPOCH).ok()).map(|d| d.as_secs() as i64) else {
                return Vec::new();
            };
            if modified < since {
                return Vec::new();
            }
            let mut events = vec![panic_event(modified, &name)];
            if let Some(part) = read_head(&entry.path()).as_deref().and_then(hardware_part_in_panic) {
                events.push(panic_hardware_event(modified, part, &name));
            }
            events
        })
        .collect()
}

/// The first 64 KB of a panic report, which is where the panic string and the faulting part are.
fn read_head(path: &Path) -> Option<String> {
    use std::io::Read;
    use std::os::unix::fs::OpenOptionsExt;
    // Never follow a link and never wait on a pipe, even if the file was swapped after it was checked.
    let mut buf = Vec::new();
    std::fs::OpenOptions::new().read(true).custom_flags(libc::O_NONBLOCK | libc::O_NOFOLLOW).open(path).ok()?.take(64 * 1024).read_to_end(&mut buf).ok()?;
    Some(String::from_utf8_lossy(&buf).into_owned())
}

/// The part of the hardware a panic report blames, when its text names one clearly. Software panics (a
/// driver, a watchdog with no part named) are not claimed as hardware.
pub(crate) fn hardware_part_in_panic(text: &str) -> Option<&'static str> {
    let lower = text.to_lowercase();
    const PARTS: &[(&str, &str)] = &[
        ("uncorrectable", "memory (uncorrectable error)"),
        ("ecc error", "memory (ECC error)"),
        ("nvme", "the internal SSD (NVMe)"),
        ("ans2", "the internal SSD controller"),
        ("applesmc", "the system management controller (SMC)"),
        ("smc panic", "the system management controller (SMC)"),
        ("thermal", "a thermal sensor or fan"),
        ("serror", "the processor (SError)"),
        ("l2c", "the processor cache"),
    ];
    PARTS.iter().find(|(needle, _)| lower.contains(needle)).map(|(_, part)| *part)
}

pub(crate) fn panic_hardware_event(epoch: i64, part: &str, file_name: &str) -> HealthEvent {
    HealthEvent {
        occurred_utc: rfc3339(epoch),
        kind: "HardwareErrorFatal",
        source: "macOS kernel panic report".to_string(),
        detail: Some(truncate(&format!("The panic report points at {part} ({file_name})"), MAX_DETAIL)),
    }
}

/// How long a disk or battery condition stays quiet after it was reported.
const CONDITION_REPEAT_SECONDS: i64 = 7 * 24 * 3600;

/// The disk and the battery as events, each with a key that says which one it is. Empty when
/// `system_profiler` is not there or says nothing worth reporting.
fn hardware_conditions(now: i64) -> Vec<(String, HealthEvent)> {
    let profile = |types: &[&str]| -> String {
        Command::new("/usr/sbin/system_profiler")
            .args(types)
            .env("LC_ALL", "C")
            .output()
            .ok()
            .filter(|o| o.status.success())
            .map(|o| String::from_utf8_lossy(&o.stdout).into_owned())
            .unwrap_or_default()
    };
    let mut out = disk_condition(&profile(&["SPNVMeDataType", "SPSerialATADataType", "SPStorageDataType"]), now);
    out.extend(battery_condition(&profile(&["SPPowerDataType"]), now));
    out
}

/// `("disk", fatal event)` when a S.M.A.R.T. status line says the disk is failing. "Verified" and "Not
/// Supported" (most external drives) say nothing.
pub(crate) fn disk_condition(text: &str, now: i64) -> Vec<(String, HealthEvent)> {
    let failing = text.lines().any(|line| {
        let lower = line.to_lowercase();
        lower.contains("s.m.a.r.t. status") && (lower.contains("fail") || lower.contains("about to fail"))
    });
    if !failing {
        return Vec::new();
    }
    vec![(
        "disk".to_string(),
        HealthEvent {
            occurred_utc: rfc3339(now),
            kind: "HardwareErrorFatal",
            source: "macOS disk S.M.A.R.T.".to_string(),
            detail: Some("The disk's S.M.A.R.T. status says it is failing".to_string()),
        },
    )]
}

/// `("battery", event to watch)` when the battery's condition is anything but Normal. A Mac with no
/// battery has no such line.
pub(crate) fn battery_condition(text: &str, now: i64) -> Vec<(String, HealthEvent)> {
    let condition = text.lines().find_map(|line| {
        let (key, value) = line.split_once(':')?;
        (key.trim().eq_ignore_ascii_case("condition")).then(|| value.trim().to_string())
    });
    let Some(condition) = condition.filter(|c| !c.is_empty() && !c.eq_ignore_ascii_case("normal")) else {
        return Vec::new();
    };
    vec![(
        "battery".to_string(),
        HealthEvent {
            occurred_utc: rfc3339(now),
            kind: "HardwareErrorCorrected",
            source: "macOS battery condition".to_string(),
            detail: Some(truncate(&format!("The battery's condition is \"{condition}\""), MAX_DETAIL)),
        },
    )]
}

/// The conditions that are due: new ones, and ones last reported a week or more ago.
pub(crate) fn conditions_due(current: &[(String, HealthEvent)], reported: &[(String, i64)], now: i64) -> Vec<HealthEvent> {
    current
        .iter()
        .filter(|(key, _)| reported.iter().find(|(k, _)| k == key).is_none_or(|(_, at)| now - at >= CONDITION_REPEAT_SECONDS))
        .map(|(_, event)| event.clone())
        .collect()
}

/// What to remember after a report: the time each present condition was last told, and nothing for one
/// that has cleared (so it is reported at once if it comes back).
pub(crate) fn next_conditions_state(current: &[(String, HealthEvent)], reported: &[(String, i64)], due: &[HealthEvent], now: i64) -> Vec<(String, i64)> {
    current
        .iter()
        .map(|(key, event)| {
            let was_due = due.iter().any(|d| d == event);
            let at = if was_due { now } else { reported.iter().find(|(k, _)| k == key).map(|(_, at)| *at).unwrap_or(now) };
            (key.clone(), at)
        })
        .collect()
}

fn conditions_state_path(state: &Path) -> std::path::PathBuf {
    let mut name = state.file_name().map(|n| n.to_os_string()).unwrap_or_default();
    name.push(".conditions");
    state.with_file_name(name)
}

fn read_conditions_state(path: &Path) -> Vec<(String, i64)> {
    std::fs::read_to_string(path)
        .map(|text| {
            text.lines()
                .filter_map(|line| {
                    let (key, at) = line.split_once(' ')?;
                    Some((key.to_string(), at.trim().parse().ok()?))
                })
                .collect()
        })
        .unwrap_or_default()
}

fn write_conditions_state(path: &Path, state: &[(String, i64)]) -> Result<()> {
    let text: String = state.iter().map(|(k, at)| format!("{k} {at}\n")).collect();
    std::fs::write(path, text).with_context(|| format!("could not write {}", path.display()))
}

/// A kernel panic report's file name, `Kernel_2026-11-03-140400_host.panic` (or `.ips` on newer macOS),
/// as opposed to an application crash report kept in the same folder.
pub(crate) fn is_panic_report_name(name: &str) -> bool {
    let lower = name.to_lowercase();
    (lower.starts_with("kernel") || lower.starts_with("panic")) && (lower.ends_with(".panic") || lower.ends_with(".ips"))
}

pub(crate) fn panic_event(epoch: i64, file_name: &str) -> HealthEvent {
    HealthEvent {
        occurred_utc: rfc3339(epoch),
        kind: "UnexpectedShutdown",
        source: "macOS kernel panic report".to_string(),
        detail: Some(truncate(&format!("The Mac panicked and restarted ({file_name})"), MAX_DETAIL)),
    }
}

/// The shutdown cause that is worth reporting, from the number in "Previous shutdown cause: N".
pub(crate) fn is_unexpected_cause(cause: i32) -> bool {
    cause <= 0 || cause == 3
}

fn describe_cause(cause: i32) -> &'static str {
    match cause {
        0 => "power was lost",
        3 => "the power button was held down",
        -60 => "a bad master directory block",
        -62 => "a watchdog reset",
        -64 | -95 => "a temperature fault",
        -74 | -75 => "the battery",
        _ if cause < 0 => "a hardware fault",
        _ => "an unexplained cause",
    }
}

/// One `log show --style syslog` line carrying "Previous shutdown cause: N", as an event when N is one
/// worth reporting, and `None` for any other line or a normal shutdown.
pub(crate) fn classify_shutdown_line(line: &str) -> Option<HealthEvent> {
    let marker = "Previous shutdown cause:";
    let after = &line[line.find(marker)? + marker.len()..];
    let cause: i32 = after.trim().split_whitespace().next()?.parse().ok()?;
    if !is_unexpected_cause(cause) {
        return None;
    }
    let epoch = line_epoch(line)?;
    Some(HealthEvent {
        occurred_utc: rfc3339(epoch),
        kind: "UnexpectedShutdown",
        source: "macOS shutdown cause".to_string(),
        detail: Some(truncate(&format!("The Mac did not shut down normally: {} (cause code {cause})", describe_cause(cause)), MAX_DETAIL)),
    })
}

/// The time at the start of a `log show --style syslog` line: `2026-11-03 14:04:00.123456+1100`.
pub(crate) fn line_epoch(line: &str) -> Option<i64> {
    let mut parts = line.split_whitespace();
    let date = parts.next()?;
    let time = parts.next()?;
    // "14:04:00.123456+1100": the clock, then the offset from UTC.
    let sign_at = time.rfind(['+', '-'])?;
    let (clock, offset) = time.split_at(sign_at);
    let clock = clock.split('.').next()?;

    let mut d = date.split('-');
    let (year, month, day): (i64, i64, i64) = (d.next()?.parse().ok()?, d.next()?.parse().ok()?, d.next()?.parse().ok()?);
    let mut c = clock.split(':');
    let (hour, minute, second): (i64, i64, i64) = (c.next()?.parse().ok()?, c.next()?.parse().ok()?, c.next()?.parse().ok()?);

    let sign = if offset.starts_with('-') { -1 } else { 1 };
    let digits = &offset[1..];
    if digits.len() != 4 {
        return None;
    }
    let offset_seconds = sign * (digits[..2].parse::<i64>().ok()? * 3600 + digits[2..].parse::<i64>().ok()? * 60);

    Some(days_from_civil(year, month, day) * 86400 + hour * 3600 + minute * 60 + second - offset_seconds)
}

/// Days since 1970-01-01 of a calendar date (Howard Hinnant's algorithm).
fn days_from_civil(year: i64, month: i64, day: i64) -> i64 {
    let y = if month <= 2 { year - 1 } else { year };
    let era = y.div_euclid(400);
    let yoe = y - era * 400;
    let doy = (153 * (if month > 2 { month - 3 } else { month + 9 }) + 2) / 5 + day - 1;
    let doe = yoe * 365 + yoe / 4 - yoe / 100 + doy;
    era * 146097 + doe - 719468
}

fn epoch_of(rfc3339_text: &str) -> Option<i64> {
    OffsetDateTime::parse(rfc3339_text, &Rfc3339).ok().map(|t| t.unix_timestamp())
}

/// Keeps the newest [`MAX_PER_KIND`] of each kind, and drops a second report of the same kind and moment (a panic
/// is also written down as a shutdown cause a few seconds later).
fn cap_newest(mut events: Vec<HealthEvent>) -> Vec<HealthEvent> {
    events.sort_by(|a, b| b.occurred_utc.cmp(&a.occurred_utc));
    let mut kept: Vec<HealthEvent> = Vec::new();
    for event in events {
        let near = kept.iter().any(|k| k.kind == event.kind && match (epoch_of(&k.occurred_utc), epoch_of(&event.occurred_utc)) {
            (Some(a), Some(b)) => (a - b).abs() <= 120,
            _ => false,
        });
        if !near && kept.iter().filter(|k| k.kind == event.kind).count() < MAX_PER_KIND {
            kept.push(event);
        }
    }
    kept
}

fn truncate(text: &str, max: usize) -> String {
    if text.chars().count() <= max {
        return text.to_string();
    }
    let mut out: String = text.chars().take(max).collect();
    out.push('…');
    out
}

pub(crate) fn rfc3339(epoch: i64) -> String {
    OffsetDateTime::from_unix_timestamp(epoch)
        .ok()
        .and_then(|t| t.format(&Rfc3339).ok())
        .unwrap_or_else(|| "1970-01-01T00:00:00Z".to_string())
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn a_normal_shutdown_is_not_an_event() {
        assert!(classify_shutdown_line("2026-11-03 14:04:00.123456+1100 localhost kernel[0]: Previous shutdown cause: 5").is_none());
    }

    #[test]
    fn power_lost_the_button_held_and_every_negative_code_are_unexpected() {
        for cause in [0, 3, -60, -62, -95, -128] {
            assert!(is_unexpected_cause(cause), "{cause}");
        }
    }

    #[test]
    fn other_positive_codes_are_left_alone_rather_than_guessed_at() {
        for cause in [1, 2, 4, 5, 6, 7] {
            assert!(!is_unexpected_cause(cause), "{cause}");
        }
    }

    #[test]
    fn a_watchdog_reset_is_reported_with_its_time_in_utc() {
        let e = classify_shutdown_line("2026-11-03 14:04:00.123456+1100 localhost kernel[0]: Previous shutdown cause: -62").unwrap();

        assert_eq!(e.kind, "UnexpectedShutdown");
        assert_eq!(e.occurred_utc, "2026-11-03T03:04:00Z");
        assert_eq!(e.source, "macOS shutdown cause");
        assert!(e.detail.unwrap().contains("watchdog"));
    }

    #[test]
    fn a_line_without_a_cause_is_not_an_event() {
        assert!(classify_shutdown_line("2026-11-03 14:04:00.1+1100 localhost kernel[0]: something else").is_none());
        assert!(classify_shutdown_line("Previous shutdown cause: notanumber").is_none());
        assert!(classify_shutdown_line("").is_none());
    }

    #[test]
    fn the_time_of_a_line_honours_its_offset_either_way() {
        assert_eq!(line_epoch("2024-11-03 14:04:00.5+0000 host x: y"), Some(1_730_642_640));
        assert_eq!(line_epoch("2024-11-03 15:04:00.5+0100 host x: y"), Some(1_730_642_640));
        assert_eq!(line_epoch("2024-11-03 09:04:00.5-0500 host x: y"), Some(1_730_642_640));
        assert_eq!(line_epoch("2024-11-03 19:34:00.5+0530 host x: y"), Some(1_730_642_640));
    }

    #[test]
    fn a_line_with_no_readable_time_has_none() {
        assert_eq!(line_epoch("nonsense"), None);
        assert_eq!(line_epoch("2024-11-03 14:04:00.5 host x: y"), None);
        assert_eq!(line_epoch(""), None);
    }

    #[test]
    fn kernel_panic_reports_are_told_apart_from_application_crashes() {
        assert!(is_panic_report_name("Kernel_2026-11-03-140400_host.panic"));
        assert!(is_panic_report_name("panic-full-2026-11-03-140400.0003.ips"));
        assert!(is_panic_report_name("Kernel-2026-11-03-140400.ips"));
        assert!(!is_panic_report_name("Safari_2026-11-03-140400_host.ips"));
        assert!(!is_panic_report_name("Kernel_notes.txt"));
    }

    #[test]
    fn a_panic_is_an_unexpected_shutdown_named_after_its_report() {
        let e = panic_event(1_730_642_640, "Kernel_x.panic");

        assert_eq!((e.kind, e.occurred_utc.as_str()), ("UnexpectedShutdown", "2024-11-03T14:04:00Z"));
        assert!(e.detail.unwrap().contains("Kernel_x.panic"));
    }

    /// A panic is written down twice, once as a report and once as the cause of the next boot.
    #[test]
    fn the_same_shutdown_seen_twice_a_few_seconds_apart_is_one_event() {
        let events = vec![panic_event(1_730_642_640, "a.panic"), panic_event(1_730_642_700, "b")];

        assert_eq!(cap_newest(events).len(), 1);
    }

    #[test]
    fn a_panic_that_names_the_ssd_is_also_a_fatal_hardware_error() {
        let text = "panic(cpu 0 caller 0xfffffe): \"NVMe: controller fatal status 0x1\" @AppleANS3NVMeController.cpp";

        assert_eq!(hardware_part_in_panic(text), Some("the internal SSD (NVMe)"));
        let e = panic_hardware_event(1_730_642_640, "the internal SSD (NVMe)", "Kernel_x.panic");
        assert_eq!((e.kind, e.occurred_utc.as_str()), ("HardwareErrorFatal", "2024-11-03T14:04:00Z"));
        assert!(e.detail.unwrap().contains("internal SSD"));
    }

    #[test]
    fn only_the_kernels_own_shutdown_line_is_read() {
        assert!(SHUTDOWN_CAUSE_PREDICATE.starts_with("process == \"kernel\" AND "));
        assert!(SHUTDOWN_CAUSE_PREDICATE.contains("Previous shutdown cause"));
    }

    /// A pipe under a panic report's name must not hang the check-in: it is skipped, never opened.
    #[test]
    fn a_pipe_or_a_link_under_a_panic_reports_name_is_not_opened() {
        let dir = std::env::temp_dir().join(format!("kintsugi-panic-test-{}", std::process::id()));
        std::fs::create_dir_all(&dir).unwrap();
        let real = dir.join("real.txt");
        std::fs::write(&real, "NVMe controller fatal").unwrap();
        let link = dir.join("Kernel_link.panic");
        std::os::unix::fs::symlink(&real, &link).unwrap();
        let fifo = dir.join("Kernel_pipe.panic");
        assert!(std::process::Command::new("mkfifo").arg(&fifo).status().unwrap().success());

        // Both are refused by the file-type check, and even opened directly neither hangs or follows.
        for name in ["Kernel_link.panic", "Kernel_pipe.panic"] {
            let kind = std::fs::read_dir(&dir).unwrap().flatten().find(|e| e.file_name() == name).unwrap().file_type().unwrap();
            assert!(!kind.is_file(), "{name}");
        }
        assert_eq!(read_head(&link), None);
        assert!(read_head(&fifo).is_none_or(|t| t.is_empty()));
        assert_eq!(read_head(&real).as_deref(), Some("NVMe controller fatal"));
        let _ = std::fs::remove_dir_all(&dir);
    }

    #[test]
    fn a_panic_that_blames_software_is_not_claimed_as_hardware() {
        assert_eq!(hardware_part_in_panic("panic: Kernel trap at 0x0, type 14=page fault, in com.vendor.kext 1.2"), None);
        assert_eq!(hardware_part_in_panic(""), None);
    }

    #[test]
    fn a_failing_disk_is_fatal_and_a_verified_or_unsupported_one_says_nothing() {
        let failing = "      Capacity: 500 GB\n      S.M.A.R.T. Status: Failing\n";
        assert_eq!(disk_condition(failing, 1_730_642_640)[0].1.kind, "HardwareErrorFatal");
        assert_eq!(disk_condition("      S.M.A.R.T. Status: About to Fail\n", 1)[0].0, "disk");
        assert!(disk_condition("      S.M.A.R.T. Status: Verified\n", 1).is_empty());
        assert!(disk_condition("      S.M.A.R.T. Status: Not Supported\n", 1).is_empty());
        assert!(disk_condition("", 1).is_empty());
    }

    #[test]
    fn a_battery_that_is_not_normal_is_to_watch_and_a_normal_one_or_none_says_nothing() {
        let service = "    Battery Information:\n      Cycle Count: 1250\n      Condition: Service Recommended\n";
        let e = &battery_condition(service, 1_730_642_640)[0];
        assert_eq!((e.0.as_str(), e.1.kind), ("battery", "HardwareErrorCorrected"));
        assert!(e.1.detail.as_deref().unwrap().contains("Service Recommended"));
        assert!(battery_condition("      Condition: Normal\n", 1).is_empty());
        assert!(battery_condition("    Power Adapter: yes\n", 1).is_empty());
    }

    #[test]
    fn a_condition_is_reported_when_new_and_again_after_a_week_and_not_in_between() {
        let current = disk_condition("S.M.A.R.T. Status: Failing", 1_000_000);
        let day = 24 * 3600;

        assert_eq!(conditions_due(&current, &[], 1_000_000).len(), 1);
        assert!(conditions_due(&current, &[("disk".into(), 1_000_000 - 2 * day)], 1_000_000).is_empty());
        assert_eq!(conditions_due(&current, &[("disk".into(), 1_000_000 - 8 * day)], 1_000_000).len(), 1);
    }

    #[test]
    fn what_is_remembered_keeps_the_old_time_when_quiet_and_forgets_what_cleared() {
        let current = disk_condition("S.M.A.R.T. Status: Failing", 1_000_000);
        let reported = vec![("disk".to_string(), 900_000), ("battery".to_string(), 800_000)];

        // Quiet: not due, so the old time stays, and the battery (no longer present) is dropped.
        assert_eq!(next_conditions_state(&current, &reported, &[], 1_000_000), vec![("disk".to_string(), 900_000)]);
        // Due: the time moves to now.
        let due = conditions_due(&current, &[], 1_000_000);
        assert_eq!(next_conditions_state(&current, &[], &due, 1_000_000), vec![("disk".to_string(), 1_000_000)]);
    }

    #[test]
    fn the_conditions_state_survives_a_restart_and_sits_beside_the_time() {
        let dir = std::env::temp_dir().join(format!("kintsugi-cond-test-{}", std::process::id()));
        std::fs::create_dir_all(&dir).unwrap();
        let path = conditions_state_path(&dir.join("health-reported"));

        assert_eq!(path.file_name().unwrap(), "health-reported.conditions");
        assert!(read_conditions_state(&path).is_empty());
        write_conditions_state(&path, &[("disk".to_string(), 5), ("battery".to_string(), 7)]).unwrap();
        assert_eq!(read_conditions_state(&path), vec![("disk".to_string(), 5), ("battery".to_string(), 7)]);
        let _ = std::fs::remove_dir_all(&dir);
    }

    /// A panic and the hardware error it names are two kinds at one moment: both are kept.
    #[test]
    fn a_panic_and_its_hardware_error_at_the_same_moment_are_both_kept() {
        let events = vec![panic_event(1_730_642_640, "a.panic"), panic_hardware_event(1_730_642_640, "memory (uncorrectable error)", "a.panic")];

        assert_eq!(cap_newest(events).len(), 2);
    }

    #[test]
    fn two_shutdowns_hours_apart_are_two_events() {
        let events = vec![panic_event(1_730_642_640, "a"), panic_event(1_730_642_640 + 7200, "b")];

        assert_eq!(cap_newest(events).len(), 2);
    }

    #[test]
    fn a_burst_is_cut_to_the_newest() {
        let events: Vec<HealthEvent> = (0..50).map(|i| panic_event(1_730_000_000 + i * 600, "x")).collect();

        let kept = cap_newest(events);

        assert_eq!(kept.len(), MAX_PER_KIND);
        assert_eq!(kept[0].occurred_utc, rfc3339(1_730_000_000 + 49 * 600));
    }

    #[test]
    fn the_excerpt_is_short() {
        assert!(panic_event(1, &"x".repeat(2000)).detail.unwrap().chars().count() <= MAX_DETAIL + 1);
    }

    #[test]
    fn the_report_uses_the_names_the_server_reads() {
        let events = [panic_event(1_730_642_640, "a")];
        let json = serde_json::to_string(&ReportHealthEventsRequest { serial_number: "S1", events: &events }).unwrap();

        assert!(json.contains("\"serialNumber\":\"S1\""));
        assert!(json.contains("\"occurredUtc\":\"2024-11-03T14:04:00Z\""));
        assert!(json.contains("\"kind\":\"UnexpectedShutdown\""));
    }

    #[test]
    fn the_state_survives_a_restart_and_a_missing_one_is_none() {
        let dir = std::env::temp_dir().join(format!("kintsugi-health-test-{}", std::process::id()));
        std::fs::create_dir_all(&dir).unwrap();
        let path = dir.join("state");

        assert_eq!(read_state(&path), None);
        write_state(&path, 1_730_642_640).unwrap();
        assert_eq!(read_state(&path), Some(1_730_642_640));
        std::fs::remove_dir_all(&dir).ok();
    }
}

/// The same idea as `log_collection::real_macos`: the real system commands, on the CI's macOS runner.
#[cfg(all(test, target_os = "macos"))]
mod real_macos {
    use super::*;

    fn run(program: &str, args: &[&str]) -> String {
        let out = Command::new(program).args(args).env("LC_ALL", "C").output().unwrap_or_else(|e| panic!("{program} could not run: {e}"));
        assert!(out.status.success(), "{program} {args:?} failed with {}", out.status);
        String::from_utf8_lossy(&out.stdout).into_owned()
    }

    /// `line_epoch` has to read the time the real `log show --style syslog` writes at the start of a line.
    #[test]
    fn the_real_syslog_style_timestamp_is_understood() {
        let text = run("/usr/bin/log", &["show", "--style", "syslog", "--last", "5m"]);
        let line = text
            .lines()
            .find(|l| l.len() > 30 && l.as_bytes()[0].is_ascii_digit() && l.contains(':'))
            .expect("a Mac that has been running for minutes has logged something in 5 minutes");

        let epoch = line_epoch(line).unwrap_or_else(|| panic!("line_epoch could not read the time of a real line: {line:?}"));
        let now = now_epoch();
        assert!((now - epoch).abs() < 3600, "the time read ({epoch}) should be within the last few minutes ({now}): {line:?}");
    }

    /// Whatever the previous-shutdown-cause lines look like on this machine, the classifier reads them.
    #[test]
    fn the_real_previous_shutdown_cause_lines_are_understood() {
        let text = run("/usr/bin/log", &["show", "--style", "syslog", "--last", "1d", "--predicate", "eventMessage CONTAINS \"Previous shutdown cause\""]);
        for line in text.lines().filter(|l| l.contains("Previous shutdown cause")) {
            assert!(line_epoch(line).is_some(), "the time of a real shutdown-cause line could not be read: {line:?}");
            let _ = classify_shutdown_line(line);
        }
    }

    #[test]
    fn the_real_system_profiler_types_exist_and_nothing_is_reported_on_a_healthy_machine() {
        let storage = run("/usr/sbin/system_profiler", &["SPStorageDataType"]);
        assert!(!storage.trim().is_empty(), "SPStorageDataType printed nothing");
        let _ = run("/usr/sbin/system_profiler", &["SPNVMeDataType", "SPSerialATADataType", "SPPowerDataType"]);

        // The CI machine's disk and battery are healthy or absent: no condition is claimed.
        let conditions = hardware_conditions(now_epoch());
        assert!(conditions.is_empty(), "a healthy CI runner should report no disk or battery condition: {:?}", conditions.iter().map(|c| &c.1.detail).collect::<Vec<_>>());
    }
}
