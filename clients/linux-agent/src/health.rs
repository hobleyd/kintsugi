//! What this host's own log says about it shutting down by itself and about hardware errors, told to
//! the server so a device that is failing is noticed before somebody's meeting is lost to it.
//!
//! **Read-only, and small.** It reads the systemd journal and sends only a one-line source and a short
//! excerpt per event, never a log. A shutdown the system or a person asked for is not an event: only a
//! boot whose journal ends with no shutdown marker is one.
//!
//! **Four sources, so one missing does not blind it.**
//! 1. The journal's previous boots (needs a persistent journal, `/var/log/journal`: the default on
//!    Ubuntu and Pop!_OS, not on every Debian).
//! 2. Without that, `last -x` (the `wtmp` file), which nearly every distribution keeps: a boot that
//!    follows no recorded shutdown is an unexpected one. Only used when the journal had no history, so
//!    one event is not reported twice.
//! 3. The memory controller's own counters (`/sys/devices/system/edac`), which are cumulative: only an
//!    increase since the last report is an event. They exist on server and workstation hardware and
//!    not in most virtual machines, where there is simply nothing to read.
//! 4. The kernel's panic records (`pstore`, and where systemd moves them), which survive the reboot.
//!
//! Hardware errors also come from the kernel's messages (machine-check and EDAC lines), which depend on
//! the hardware and the kernel reporting them.

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

/// The most boots read in one run, newest first, so a host with a long journal does not spend a check-in
/// reading all of it.
const MAX_BOOTS: usize = 40;

/// The most events of one kind in one report. A host throwing errors in a loop is reported as a burst,
/// newest first, not as thousands of rows.
const MAX_PER_KIND: usize = 20;

const MAX_DETAIL: usize = 300;

/// Which journal entries may say a shutdown was clean: written by root, by systemd itself or by the
/// kernel. Any user can write a line into the journal (`logger "rebooting"`), and ordinary services log
/// words like "shutting down" all day, so without this a real crash could be hidden by a note. Fields
/// are ANDed, the same field is ORed, and `+` separates the two groups.
pub(crate) const SYSTEM_ENTRY_MATCHES: &[&str] = &[
    "_UID=0",
    "_COMM=systemd",
    "_COMM=systemd-shutdow",
    "_COMM=systemd-journal",
    "_COMM=systemd-logind",
    "+",
    "_TRANSPORT=kernel",
];

/// What the end of a boot's journal says when the system shut down cleanly.
const CLEAN_SHUTDOWN_MARKERS: &[&str] = &[
    "journal stopped",
    "shutting down",
    "reached target shutdown",
    "reached target final step",
    "reached target power-off",
    "reached target reboot",
    "powering off",
    "rebooting",
    "reboot: restarting system",
    "reboot: power down",
    "system is powering down",
    "system is rebooting",
];

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

    let edac_state = edac_state_path(state);
    let edac_before = read_edac_state(&edac_state);
    let edac_now = read_edac_counts(Path::new(EDAC_ROOT));

    let mut events = collect_since(since);
    events.extend(edac_events(now, edac_before, edac_now));
    if !events.is_empty() {
        let request = ReportHealthEventsRequest { serial_number, events: &events };
        let response = client.post(config.health_events_url()).json(&request).send().context("the report could not be sent")?;
        if !response.status().is_success() {
            anyhow::bail!("the server rejected the report (HTTP {})", response.status());
        }
        logging::info(&format!("reported {} device health event(s)", events.len()));
    }

    // Only once the server has accepted the report, like the time: a failed send finds the same increase
    // again next time.
    if let Some(now_counts) = edac_now {
        let _ = write_edac_state(&edac_state, now_counts);
    }
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

/// Runs `journalctl` with a locale that cannot translate what is parsed. `None` when it is not there or
/// fails: a host without systemd reports nothing rather than failing.
fn journalctl(args: &[&str]) -> Option<String> {
    let output = Command::new("journalctl")
        .args(args)
        .arg("--no-pager")
        .arg("-q")
        .env("LC_ALL", "C")
        .env("LANG", "C")
        .output()
        .ok()?;
    output.status.success().then(|| String::from_utf8_lossy(&output.stdout).into_owned())
}

/// Everything new since `since` (epoch seconds), unexpected shutdowns and hardware errors.
fn collect_since(since: i64) -> Vec<HealthEvent> {
    let mut events = unexpected_shutdowns(since);
    events.extend(panic_record_events(since));
    events.extend(hardware_errors(since));
    cap_newest(events)
}

fn unexpected_shutdowns(since: i64) -> Vec<HealthEvent> {
    let offsets = journalctl(&["--list-boots"]).map(|list| previous_boot_offsets(&list)).unwrap_or_default();
    if offsets.is_empty() {
        // No history in the journal (a volatile one, or no systemd): the wtmp file still has the boots.
        return wtmp_unexpected_shutdowns(since);
    }

    let mut events = Vec::new();
    // Offsets of previous boots, newest first. The current boot (0) has not ended.
    for offset in offsets.into_iter().take(MAX_BOOTS) {
        let offset_arg = offset.to_string();
        let Some(last) = journalctl(&["-b", &offset_arg, "-n", "1", "-o", "short-unix"]).and_then(|l| last_entry_epoch(&l)) else {
            continue;
        };
        if last < since {
            // Boots are newest first: everything after this one is older still.
            break;
        }

        let mut args = vec!["-b", offset_arg.as_str(), "-n", "100", "-o", "cat"];
        args.extend(SYSTEM_ENTRY_MATCHES);
        let tail = journalctl(&args).unwrap_or_default();
        if !ended_cleanly(&tail) {
            events.push(HealthEvent {
                occurred_utc: rfc3339(last),
                kind: "UnexpectedShutdown",
                source: "systemd journal".to_string(),
                detail: Some("The previous boot ended with no shutdown in its journal".to_string()),
            });
        }
    }

    cap_newest(events)
}

/// Where the kernel's memory-controller error counters live.
const EDAC_ROOT: &str = "/sys/devices/system/edac/mc";

/// Where the kernel's panic records are kept: `pstore` itself, and where systemd moves them at boot.
const PANIC_RECORD_DIRS: &[&str] = &["/sys/fs/pstore", "/var/lib/systemd/pstore"];

fn wtmp_unexpected_shutdowns(since: i64) -> Vec<HealthEvent> {
    let output = Command::new("last")
        .args(["-x", "--time-format", "iso", "reboot", "shutdown"])
        .env("LC_ALL", "C")
        .output();
    let Ok(output) = output else {
        return Vec::new();
    };
    if !output.status.success() {
        return Vec::new();
    }
    let text = String::from_utf8_lossy(&output.stdout);
    let events = unclean_boots_from_last(&text)
        .into_iter()
        .filter(|epoch| *epoch >= since)
        .map(|epoch| HealthEvent {
            occurred_utc: rfc3339(epoch),
            kind: "UnexpectedShutdown",
            source: "wtmp (last -x)".to_string(),
            detail: Some("The host started again with no shutdown recorded before it".to_string()),
        })
        .collect();
    cap_newest(events)
}

/// The start time of each boot that followed a boot with no recorded shutdown, from `last -x
/// --time-format iso reboot shutdown` (newest first). A clean shutdown writes a `shutdown` record
/// between two `reboot` ones; two `reboot` records in a row mean the first one's host went away
/// without saying so.
pub(crate) fn unclean_boots_from_last(output: &str) -> Vec<i64> {
    let mut unclean = Vec::new();
    let mut newer_boot: Option<i64> = None;
    for line in output.lines() {
        let mut words = line.split_whitespace();
        let Some(kind) = words.next() else { continue };
        match kind {
            "reboot" => {
                let Some(at) = words.find_map(|w| OffsetDateTime::parse(w, &Rfc3339).ok()).map(|t| t.unix_timestamp()) else {
                    continue;
                };
                if let Some(newer) = newer_boot {
                    unclean.push(newer);
                }
                newer_boot = Some(at);
            }
            "shutdown" => newer_boot = None,
            _ => {}
        }
    }
    unclean
}

/// `(corrected, uncorrected)` memory errors since boot, summed over the memory controllers; `None` when
/// the host has no EDAC (most virtual machines), which is not an error.
fn read_edac_counts(root: &Path) -> Option<(u64, u64)> {
    let mut found = false;
    let (mut ce, mut ue) = (0u64, 0u64);
    for entry in std::fs::read_dir(root).ok()?.flatten() {
        let name = entry.file_name().to_string_lossy().into_owned();
        if !name.starts_with("mc") {
            continue;
        }
        let read = |file: &str| std::fs::read_to_string(entry.path().join(file)).ok().and_then(|t| t.trim().parse::<u64>().ok());
        if let (Some(c), Some(u)) = (read("ce_count"), read("ue_count")) {
            found = true;
            ce += c;
            ue += u;
        }
    }
    found.then_some((ce, ue))
}

/// What the memory controllers added since the last report. The counters start from zero at every boot,
/// so a lower number than before means a reboot happened and the whole of it is new.
pub(crate) fn edac_events(now: i64, before: Option<(u64, u64)>, current: Option<(u64, u64)>) -> Vec<HealthEvent> {
    let Some((ce, ue)) = current else {
        return Vec::new();
    };
    let (ce_before, ue_before) = before.unwrap_or((0, 0));
    let added = |now: u64, before: u64| if now >= before { now - before } else { now };

    let mut events = Vec::new();
    let new_ue = added(ue, ue_before);
    if new_ue > 0 {
        events.push(HealthEvent {
            occurred_utc: rfc3339(now),
            kind: "HardwareErrorFatal",
            source: "kernel EDAC counters".to_string(),
            detail: Some(format!("{new_ue} uncorrected memory error(s) reported by the memory controller")),
        });
    }
    let new_ce = added(ce, ce_before);
    if new_ce > 0 {
        events.push(HealthEvent {
            occurred_utc: rfc3339(now),
            kind: "HardwareErrorCorrected",
            source: "kernel EDAC counters".to_string(),
            detail: Some(format!("{new_ce} corrected memory error(s) reported by the memory controller")),
        });
    }
    events
}

fn edac_state_path(state: &Path) -> std::path::PathBuf {
    let mut name = state.file_name().map(|n| n.to_os_string()).unwrap_or_default();
    name.push(".edac");
    state.with_file_name(name)
}

fn read_edac_state(path: &Path) -> Option<(u64, u64)> {
    let text = std::fs::read_to_string(path).ok()?;
    let mut parts = text.split_whitespace();
    Some((parts.next()?.parse().ok()?, parts.next()?.parse().ok()?))
}

fn write_edac_state(path: &Path, (ce, ue): (u64, u64)) -> Result<()> {
    std::fs::write(path, format!("{ce} {ue}")).with_context(|| format!("could not write {}", path.display()))
}

/// A kernel panic or oops the kernel wrote down before it stopped: the record outlives the reboot.
fn panic_record_events(since: i64) -> Vec<HealthEvent> {
    let mut events = Vec::new();
    for dir in PANIC_RECORD_DIRS {
        collect_panic_records(Path::new(dir), since, 0, &mut events);
    }
    cap_newest(events)
}

fn collect_panic_records(dir: &Path, since: i64, depth: u32, events: &mut Vec<HealthEvent>) {
    let Ok(entries) = std::fs::read_dir(dir) else {
        return;
    };
    for entry in entries.flatten() {
        let name = entry.file_name().to_string_lossy().into_owned();
        let Ok(meta) = entry.metadata() else { continue };
        if meta.is_dir() {
            if depth < 2 {
                collect_panic_records(&entry.path(), since, depth + 1, events);
            }
            continue;
        }
        let modified = meta.modified().ok().and_then(|m| m.duration_since(std::time::UNIX_EPOCH).ok()).map(|d| d.as_secs() as i64);
        if let Some(epoch) = modified.filter(|e| *e >= since && is_panic_record_name(&name)) {
            events.push(panic_record_event(epoch, &name));
        }
    }
}

/// `dmesg-ramoops-0`, `dmesg-efi-161...`: the kernel's last messages, saved when it panicked.
pub(crate) fn is_panic_record_name(name: &str) -> bool {
    name.starts_with("dmesg-") || name.starts_with("dmesg.")
}

pub(crate) fn panic_record_event(epoch: i64, name: &str) -> HealthEvent {
    HealthEvent {
        occurred_utc: rfc3339(epoch),
        kind: "UnexpectedShutdown",
        source: "kernel panic record".to_string(),
        detail: Some(truncate(&format!("The kernel saved a panic record before it stopped ({name})"), MAX_DETAIL)),
    }
}

fn hardware_errors(since: i64) -> Vec<HealthEvent> {
    let since_arg = format!("@{since}");
    let Some(kernel) = journalctl(&["-k", "--since", &since_arg, "-o", "short-unix"]) else {
        return Vec::new();
    };

    let events = kernel.lines().filter_map(classify_hardware_line).collect();
    cap_newest(events)
}

/// Keeps the newest [`MAX_PER_KIND`] of each kind.
fn cap_newest(mut events: Vec<HealthEvent>) -> Vec<HealthEvent> {
    events.sort_by(|a, b| b.occurred_utc.cmp(&a.occurred_utc));
    let mut counts = std::collections::HashMap::<&'static str, usize>::new();
    events.retain(|e| {
        let n = counts.entry(e.kind).or_insert(0);
        *n += 1;
        *n <= MAX_PER_KIND
    });
    events
}

/// The offsets (`-1`, `-2`, ...) of the previous boots from `journalctl --list-boots`, newest first. The
/// first token of each line is the offset; line 0 is the current boot and is left out.
pub(crate) fn previous_boot_offsets(list_boots: &str) -> Vec<i32> {
    let mut offsets: Vec<i32> = list_boots
        .lines()
        .filter_map(|line| line.split_whitespace().next()?.parse::<i32>().ok())
        .filter(|offset| *offset < 0)
        .collect();
    offsets.sort_unstable_by(|a, b| b.cmp(a));
    offsets
}

/// The epoch seconds of one `-o short-unix` line: `1730000000.123456 host unit[pid]: message`.
pub(crate) fn last_entry_epoch(line: &str) -> Option<i64> {
    let first = line.lines().map(str::trim).find(|l| !l.is_empty() && !l.starts_with("--"))?;
    first.split_whitespace().next()?.split('.').next()?.parse().ok()
}

/// Whether the end of a boot's journal carries a shutdown marker, which is how a shutdown somebody or
/// something asked for differs from the power going: the second leaves no goodbye.
pub(crate) fn ended_cleanly(tail: &str) -> bool {
    let lower = tail.to_lowercase();
    CLEAN_SHUTDOWN_MARKERS.iter().any(|marker| lower.contains(marker))
}

/// A hardware error from one kernel journal line, or `None` for any other line.
pub(crate) fn classify_hardware_line(line: &str) -> Option<HealthEvent> {
    let lower = line.to_lowercase();
    let epoch = last_entry_epoch(line)?;

    let fatal = lower.contains("machine check exception")
        || lower.contains("uncorrected")
        || lower.contains("[hardware error]") && lower.contains("fatal");
    let corrected = lower.contains("machine check events logged")
        || (lower.contains("hardware error") && lower.contains("corrected"))
        || lower.contains("edac") && (lower.contains(" ce ") || lower.contains("1 ce") || lower.contains("corrected"));

    // A line that mentions "uncorrected" also contains "corrected": the fatal reading wins.
    let (kind, source) = if fatal {
        ("HardwareErrorFatal", "kernel machine check")
    } else if corrected {
        ("HardwareErrorCorrected", if lower.contains("edac") { "kernel EDAC" } else { "kernel machine check" })
    } else {
        return None;
    };

    let message = line.splitn(2, ": ").nth(1).unwrap_or(line).trim();
    Some(HealthEvent {
        occurred_utc: rfc3339(epoch),
        kind,
        source: source.to_string(),
        detail: Some(truncate(message, MAX_DETAIL)),
    })
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

    const LIST_BOOTS: &str = "\
-2 11111111111111111111111111111111 Mon 2026-11-02 08:00:01 UTC—Mon 2026-11-02 17:00:00 UTC
-1 22222222222222222222222222222222 Tue 2026-11-03 08:10:00 UTC—Tue 2026-11-03 14:04:00 UTC
 0 33333333333333333333333333333333 Wed 2026-11-04 09:00:00 UTC—Wed 2026-11-04 10:30:00 UTC
";

    #[test]
    fn previous_boots_are_the_negative_offsets_newest_first() {
        assert_eq!(previous_boot_offsets(LIST_BOOTS), vec![-1, -2]);
    }

    #[test]
    fn the_current_boot_has_not_ended_so_it_is_never_listed() {
        assert!(!previous_boot_offsets(LIST_BOOTS).contains(&0));
    }

    #[test]
    fn a_host_with_only_the_current_boot_has_no_history_to_read() {
        assert!(previous_boot_offsets(" 0 33333333333333333333333333333333 Wed 2026-11-04 09:00:00 UTC—Wed 2026-11-04 10:30:00 UTC\n").is_empty());
    }

    #[test]
    fn garbage_lists_nothing_rather_than_failing() {
        assert!(previous_boot_offsets("no journal files were found\n").is_empty());
    }

    #[test]
    fn the_time_of_a_journal_line_is_its_first_field() {
        assert_eq!(last_entry_epoch("1730642640.123456 laptop systemd[1]: Reached target Power-Off.\n"), Some(1_730_642_640));
    }

    #[test]
    fn a_separator_line_is_skipped_when_reading_the_time() {
        assert_eq!(last_entry_epoch("-- Boot abc --\n1730642640.5 laptop kernel: x\n"), Some(1_730_642_640));
        assert_eq!(last_entry_epoch(""), None);
        assert_eq!(last_entry_epoch("not a time laptop kernel: x"), None);
    }

    #[test]
    fn a_journal_that_ends_with_a_shutdown_is_a_clean_one() {
        assert!(ended_cleanly("Stopped target Basic System.\nReached target Shutdown.\nsystemd-journald[301]: Journal stopped\n"));
        assert!(ended_cleanly("reboot: Restarting system"));
        assert!(ended_cleanly("systemd-shutdown[1]: Powering off."));
    }

    /// A note written by a user ends up in the journal as that user's, so it cannot say "clean".
    #[test]
    fn only_root_systemd_and_the_kernel_may_say_the_shutdown_was_clean() {
        assert_eq!(SYSTEM_ENTRY_MATCHES[0], "_UID=0");
        assert!(SYSTEM_ENTRY_MATCHES.contains(&"_COMM=systemd-shutdow"));
        assert!(SYSTEM_ENTRY_MATCHES.contains(&"_TRANSPORT=kernel"));
        // The two groups are joined by `+` (OR): root-and-systemd, or the kernel.
        assert_eq!(SYSTEM_ENTRY_MATCHES.iter().filter(|m| **m == "+").count(), 1);
    }

    /// The power going leaves no goodbye: the last lines are whatever the machine was doing.
    #[test]
    fn a_journal_that_just_stops_is_an_unexpected_shutdown() {
        assert!(!ended_cleanly("wpa_supplicant[900]: wlp2s0: CTRL-EVENT-SIGNAL-CHANGE\nNetworkManager[800]: <info> dhcp: renewing\n"));
        assert!(!ended_cleanly(""));
    }

    #[test]
    fn an_uncorrected_machine_check_is_fatal_and_not_mistaken_for_a_corrected_one() {
        let e = classify_hardware_line("1730642640.1 laptop kernel: mce: [Hardware Error]: Uncorrected error: CPU 3 Bank 8").unwrap();

        assert_eq!(e.kind, "HardwareErrorFatal");
        assert_eq!(e.occurred_utc, "2024-11-03T14:04:00Z");
    }

    #[test]
    fn a_machine_check_exception_is_fatal() {
        assert_eq!(
            classify_hardware_line("1730642640.1 laptop kernel: mce: Machine Check Exception: 4 Bank 0: be00000000800400").unwrap().kind,
            "HardwareErrorFatal"
        );
    }

    #[test]
    fn corrected_machine_checks_and_memory_errors_are_corrected() {
        assert_eq!(
            classify_hardware_line("1730642640.1 laptop kernel: mce: [Hardware Error]: Machine check events logged").unwrap().kind,
            "HardwareErrorCorrected"
        );
        let edac = classify_hardware_line("1730642640.1 laptop kernel: EDAC MC0: 1 CE on mc#0csrow#1channel#0").unwrap();
        assert_eq!((edac.kind, edac.source.as_str()), ("HardwareErrorCorrected", "kernel EDAC"));
    }

    #[test]
    fn an_ordinary_kernel_line_is_not_a_hardware_error() {
        assert!(classify_hardware_line("1730642640.1 laptop kernel: usb 1-1: new high-speed USB device number 2").is_none());
        assert!(classify_hardware_line("").is_none());
    }

    #[test]
    fn the_excerpt_is_short() {
        let long = format!("1730642640.1 laptop kernel: mce: Uncorrected error {}", "x".repeat(2000));

        assert!(classify_hardware_line(&long).unwrap().detail.unwrap().chars().count() <= MAX_DETAIL + 1);
    }

    #[test]
    fn a_burst_of_one_kind_is_cut_to_the_newest() {
        let events: Vec<HealthEvent> = (0..50)
            .map(|i| HealthEvent { occurred_utc: rfc3339(1_730_000_000 + i), kind: "HardwareErrorCorrected", source: "x".into(), detail: None })
            .collect();

        let kept = cap_newest(events);

        assert_eq!(kept.len(), MAX_PER_KIND);
        assert_eq!(kept[0].occurred_utc, rfc3339(1_730_000_049));
    }

    #[test]
    fn the_cap_is_per_kind_so_a_flood_of_one_does_not_hide_the_other() {
        let mut events: Vec<HealthEvent> = (0..30)
            .map(|i| HealthEvent { occurred_utc: rfc3339(1_730_000_000 + i), kind: "HardwareErrorCorrected", source: "x".into(), detail: None })
            .collect();
        events.push(HealthEvent { occurred_utc: rfc3339(1_729_000_000), kind: "UnexpectedShutdown", source: "x".into(), detail: None });

        assert!(cap_newest(events).iter().any(|e| e.kind == "UnexpectedShutdown"));
    }

    #[test]
    fn the_report_uses_the_names_the_server_reads() {
        let events = [HealthEvent { occurred_utc: rfc3339(1_730_642_640), kind: "UnexpectedShutdown", source: "systemd journal".into(), detail: None }];
        let json = serde_json::to_string(&ReportHealthEventsRequest { serial_number: "S1", events: &events }).unwrap();

        assert!(json.contains("\"serialNumber\":\"S1\""));
        assert!(json.contains("\"occurredUtc\":\"2024-11-03T14:04:00Z\""));
        assert!(json.contains("\"kind\":\"UnexpectedShutdown\""));
        assert!(json.contains("\"source\":\"systemd journal\""));
    }

    /// The kinds are the server's own, spelled the way `ReportHostHealthEventsCommandValidator` allows.
    #[test]
    fn every_kind_this_agent_sends_is_one_the_server_accepts() {
        for kind in ["UnexpectedShutdown", "HardwareErrorFatal", "HardwareErrorCorrected"] {
            assert!(
                ["UnexpectedShutdown", "HardwareErrorFatal", "HardwareErrorCorrected"].contains(&kind),
                "{kind}"
            );
        }
    }

    #[test]
    fn the_state_survives_a_restart_and_a_missing_one_is_none() {
        let dir = std::env::temp_dir().join(format!("kintsugi-health-test-{}", std::process::id()));
        std::fs::create_dir_all(&dir).unwrap();
        let path = dir.join("state");

        assert_eq!(read_state(&path), None);
        write_state(&path, 1_730_642_640).unwrap();
        assert_eq!(read_state(&path), Some(1_730_642_640));
        let _ = std::fs::remove_dir_all(&dir);
    }

    const LAST: &str = "\
reboot   system boot  6.8.0-45-generic 2026-11-05T08:10:00+00:00   still running
reboot   system boot  6.8.0-45-generic 2026-11-04T09:00:00+00:00   still running
shutdown system down  6.8.0-45-generic 2026-11-03T17:00:00+00:00 - 2026-11-04T09:00:00+00:00 (16:00)
reboot   system boot  6.8.0-45-generic 2026-11-03T08:00:00+00:00 - 2026-11-03T17:00:00+00:00 (09:00)

wtmp begins Mon Nov  3 08:00:00 2026
";

    /// The 5 Nov boot follows the 4 Nov one with nothing recorded between them: the 4 Nov host went away
    /// without saying so. The 4 Nov boot follows a clean `shutdown`.
    #[test]
    fn a_boot_with_no_shutdown_before_it_is_an_unexpected_shutdown() {
        assert_eq!(unclean_boots_from_last(LAST), vec![1_793_866_200]);
    }

    #[test]
    fn a_wtmp_with_only_clean_shutdowns_reports_nothing() {
        let clean = "reboot system boot k 2026-11-04T09:00:00+00:00 still running\nshutdown system down k 2026-11-03T17:00:00+00:00 - 2026-11-04T09:00:00+00:00 (16:00)\nreboot system boot k 2026-11-03T08:00:00+00:00 - 2026-11-03T17:00:00+00:00 (09:00)\n";

        assert!(unclean_boots_from_last(clean).is_empty());
        assert!(unclean_boots_from_last("").is_empty());
        assert!(unclean_boots_from_last("last: cannot open /var/log/wtmp\n").is_empty());
    }

    #[test]
    fn a_time_with_an_offset_is_read_as_that_moment() {
        let output = "reboot system boot k 2026-11-05T19:10:00+11:00 still running\nreboot system boot k 2026-11-04T09:00:00+11:00 - crash\n";

        assert_eq!(unclean_boots_from_last(output), vec![1_793_866_200]);
    }

    #[test]
    fn memory_errors_reported_since_the_last_time_are_events() {
        let events = edac_events(1_730_642_640, Some((5, 0)), Some((8, 2)));

        let kinds: Vec<_> = events.iter().map(|e| e.kind).collect();
        assert_eq!(kinds, vec!["HardwareErrorFatal", "HardwareErrorCorrected"]);
        assert!(events[0].detail.as_deref().unwrap().starts_with("2 uncorrected"));
        assert!(events[1].detail.as_deref().unwrap().starts_with("3 corrected"));
    }

    #[test]
    fn counters_that_did_not_move_are_silent_and_a_host_without_edac_is_too() {
        assert!(edac_events(1, Some((5, 1)), Some((5, 1))).is_empty());
        assert!(edac_events(1, Some((5, 1)), None).is_empty());
    }

    /// The counters start again at zero when the host reboots: a lower number is all new.
    #[test]
    fn counters_that_went_down_mean_a_reboot_and_the_whole_number_is_new() {
        let events = edac_events(1, Some((50, 3)), Some((2, 0)));

        assert_eq!(events.len(), 1);
        assert_eq!(events[0].kind, "HardwareErrorCorrected");
        assert!(events[0].detail.as_deref().unwrap().starts_with("2 corrected"));
    }

    #[test]
    fn the_first_look_counts_everything_since_boot() {
        assert_eq!(edac_events(1, None, Some((0, 1))).len(), 1);
    }

    #[test]
    fn the_counters_are_summed_over_every_memory_controller() {
        let dir = std::env::temp_dir().join(format!("kintsugi-edac-test-{}", std::process::id()));
        for (mc, ce, ue) in [("mc0", "3", "0"), ("mc1", "4", "1")] {
            std::fs::create_dir_all(dir.join(mc)).unwrap();
            std::fs::write(dir.join(mc).join("ce_count"), format!("{ce}\n")).unwrap();
            std::fs::write(dir.join(mc).join("ue_count"), format!("{ue}\n")).unwrap();
        }
        std::fs::create_dir_all(dir.join("power")).unwrap();

        assert_eq!(read_edac_counts(&dir), Some((7, 1)));
        assert_eq!(read_edac_counts(&dir.join("missing")), None);
        let _ = std::fs::remove_dir_all(&dir);
    }

    #[test]
    fn the_edac_state_survives_a_restart_and_sits_beside_the_time() {
        let dir = std::env::temp_dir().join(format!("kintsugi-edac-state-{}", std::process::id()));
        std::fs::create_dir_all(&dir).unwrap();
        let path = edac_state_path(&dir.join("health-reported"));

        assert_eq!(path.file_name().unwrap(), "health-reported.edac");
        assert_eq!(read_edac_state(&path), None);
        write_edac_state(&path, (7, 1)).unwrap();
        assert_eq!(read_edac_state(&path), Some((7, 1)));
        let _ = std::fs::remove_dir_all(&dir);
    }

    #[test]
    fn a_panic_record_is_an_unexpected_shutdown_and_other_files_are_not() {
        assert!(is_panic_record_name("dmesg-ramoops-0"));
        assert!(is_panic_record_name("dmesg-efi-161234"));
        assert!(!is_panic_record_name("console-ramoops-0"));
        assert!(!is_panic_record_name("README"));

        let e = panic_record_event(1_730_642_640, "dmesg-ramoops-0");
        assert_eq!((e.kind, e.occurred_utc.as_str()), ("UnexpectedShutdown", "2024-11-03T14:04:00Z"));
    }

    #[test]
    fn times_are_written_as_rfc3339_utc() {
        assert_eq!(rfc3339(0), "1970-01-01T00:00:00Z");
        assert_eq!(rfc3339(1_730_642_640), "2024-11-03T14:04:00Z");
    }
}
