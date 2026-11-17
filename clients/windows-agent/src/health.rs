//! What this PC's own Windows event log says about it shutting down by itself and about hardware
//! errors, told to the server so a device that is failing is noticed before somebody's meeting is lost
//! to it.
//!
//! **Read-only, and small.** It reads the System event log through PowerShell and sends only a one-line
//! source and a short excerpt per event, never a log. A restart that Windows, a person or Kintsugi asked
//! for is logged as "a process initiated the restart" and is not one of these: only what Windows
//! recorded as a shutdown nothing asked for is.
//!
//! The events: `Kernel-Power` 41 and `EventLog` 6008 (the machine went down without shutting down), the
//! `WER-SystemErrorReporting` 1001 bug check (a blue screen), and `WHEA-Logger` 1, 18 (a fatal hardware
//! error) and 19, 20 (a corrected one). The server drops one it already has, so a crash logged twice is
//! one event.

use std::path::Path;

use anyhow::{Context, Result};
use serde::Serialize;
use time::format_description::well_known::Rfc3339;
use time::OffsetDateTime;

use crate::config::{self, Config};
use crate::logging;
use crate::os_update::{run_powershell, UTF8_OUTPUT};

/// How far back the first report looks. Later ones start where the last one finished.
const FIRST_LOOKBACK_SECONDS: i64 = 30 * 24 * 3600;

/// The most events of one kind in one report: a PC throwing errors in a loop is reported as a burst,
/// newest first, not as thousands of rows.
const MAX_PER_KIND: usize = 20;

const MAX_DETAIL: usize = 300;

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

    let events = collect_since(since)?;
    if !events.is_empty() {
        let request = ReportHealthEventsRequest { serial_number, events: &events };
        let response = client.post(config.health_events_url()).json(&request).send().context("the report could not be sent")?;
        if !response.status().is_success() {
            anyhow::bail!("the server rejected the report (HTTP {})", response.status());
        }
        logging::info(&format!("reported {} device health event(s)", events.len()));
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

/// The PowerShell that reads the System log since `since` (epoch seconds, a number and nothing else, so
/// nothing can be injected through it). Prints a JSON array, empty when there is nothing, and changes
/// nothing on the machine.
pub(crate) fn script(since: i64) -> String {
    format!(
        r#"{UTF8_OUTPUT}$ErrorActionPreference = 'SilentlyContinue'
$start = [DateTimeOffset]::FromUnixTimeSeconds({since}).UtcDateTime
$items = @(Get-WinEvent -FilterHashtable @{{ LogName = 'System'; StartTime = $start; Id = 1, 18, 19, 20, 41, 1001, 6008 }} -MaxEvents 3000 |
  ForEach-Object {{
    $m = ($_.Message -replace '\s+', ' ')
    if ($m.Length -gt {MAX_DETAIL}) {{ $m = $m.Substring(0, {MAX_DETAIL}) }}
    [pscustomobject]@{{ Id = $_.Id; Provider = $_.ProviderName; Time = $_.TimeCreated.ToUniversalTime().ToString('o'); Message = $m }}
  }})
ConvertTo-Json -InputObject $items -Compress"#
    )
}

fn collect_since(since: i64) -> Result<Vec<HealthEvent>> {
    let output = run_powershell(&script(since))?;
    if !output.status.success() {
        anyhow::bail!(
            "reading the System log exited with {}: {}",
            output.status,
            String::from_utf8_lossy(&output.stderr).trim()
        );
    }

    Ok(cap_newest(parse_events(&String::from_utf8_lossy(&output.stdout))))
}

/// What one logged event is, if it is one of the ones that matter: its kind for the server and the
/// source shown on the timeline.
pub(crate) fn classify(id: u32, provider: &str) -> Option<(&'static str, String)> {
    let provider = provider.to_lowercase();

    if provider.contains("kernel-power") && id == 41 {
        Some(("UnexpectedShutdown", "Kernel-Power 41".to_string()))
    } else if provider.contains("eventlog") && id == 6008 {
        Some(("UnexpectedShutdown", "EventLog 6008".to_string()))
    } else if provider.contains("wer-systemerrorreporting") && id == 1001 {
        Some(("UnexpectedShutdown", "BugCheck 1001".to_string()))
    } else if provider.contains("whea") && matches!(id, 1 | 18) {
        Some(("HardwareErrorFatal", format!("WHEA-Logger {id}")))
    } else if provider.contains("whea") && matches!(id, 19 | 20) {
        Some(("HardwareErrorCorrected", format!("WHEA-Logger {id}")))
    } else {
        None
    }
}

/// Reads the script's JSON: an array of `{ Id, Provider, Time, Message }`. Anything unreadable gives
/// nothing rather than an error: a log with no events prints nothing at all on some versions.
pub(crate) fn parse_events(json: &str) -> Vec<HealthEvent> {
    let json = json.trim().trim_start_matches('\u{feff}');
    if json.is_empty() {
        return Vec::new();
    }

    let Ok(value) = serde_json::from_str::<serde_json::Value>(json) else {
        return Vec::new();
    };
    // A single event can come back as an object rather than a one-element array.
    let items: Vec<&serde_json::Value> = match &value {
        serde_json::Value::Array(items) => items.iter().collect(),
        serde_json::Value::Object(_) => vec![&value],
        _ => return Vec::new(),
    };

    items
        .into_iter()
        .filter_map(|item| {
            let id = item.get("Id")?.as_u64()? as u32;
            let provider = item.get("Provider")?.as_str()?;
            let time = item.get("Time")?.as_str()?;
            let (kind, source) = classify(id, provider)?;
            let occurred = OffsetDateTime::parse(time, &Rfc3339).ok()?;
            let message = item.get("Message").and_then(|m| m.as_str()).map(str::trim).filter(|m| !m.is_empty());

            Some(HealthEvent {
                occurred_utc: occurred.to_offset(time::UtcOffset::UTC).format(&Rfc3339).ok()?,
                kind,
                source,
                detail: message.map(|m| truncate(m, MAX_DETAIL)),
            })
        })
        .collect()
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

fn truncate(text: &str, max: usize) -> String {
    if text.chars().count() <= max {
        return text.to_string();
    }
    let mut out: String = text.chars().take(max).collect();
    out.push('…');
    out
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn the_events_that_matter_are_classified_by_what_the_server_expects() {
        assert_eq!(classify(41, "Microsoft-Windows-Kernel-Power"), Some(("UnexpectedShutdown", "Kernel-Power 41".to_string())));
        assert_eq!(classify(6008, "EventLog"), Some(("UnexpectedShutdown", "EventLog 6008".to_string())));
        assert_eq!(classify(1001, "Microsoft-Windows-WER-SystemErrorReporting"), Some(("UnexpectedShutdown", "BugCheck 1001".to_string())));
        assert_eq!(classify(1, "Microsoft-Windows-WHEA-Logger"), Some(("HardwareErrorFatal", "WHEA-Logger 1".to_string())));
        assert_eq!(classify(18, "Microsoft-Windows-WHEA-Logger"), Some(("HardwareErrorFatal", "WHEA-Logger 18".to_string())));
        assert_eq!(classify(19, "Microsoft-Windows-WHEA-Logger"), Some(("HardwareErrorCorrected", "WHEA-Logger 19".to_string())));
        assert_eq!(classify(20, "Microsoft-Windows-WHEA-Logger"), Some(("HardwareErrorCorrected", "WHEA-Logger 20".to_string())));
    }

    /// The same number from another provider is another event: 41 or 1 mean nothing without the
    /// provider, and the log is full of both.
    #[test]
    fn the_same_id_from_another_provider_is_not_one_of_them() {
        assert_eq!(classify(41, "Microsoft-Windows-Winlogon"), None);
        assert_eq!(classify(1, "Microsoft-Windows-Kernel-General"), None);
        assert_eq!(classify(1001, "Windows Error Reporting"), None);
        assert_eq!(classify(6008, "Microsoft-Windows-Kernel-Power"), None);
    }

    /// A restart a person, Windows or Kintsugi asked for is event 1074, which is not one of these.
    #[test]
    fn a_restart_somebody_asked_for_is_not_an_unexpected_shutdown() {
        assert_eq!(classify(1074, "User32"), None);
        assert_eq!(classify(1076, "User32"), None);
    }

    const TWO: &str = r#"[
      {"Id":41,"Provider":"Microsoft-Windows-Kernel-Power","Time":"2026-11-06T04:04:12.5000000Z","Message":"The system has rebooted without cleanly shutting down first."},
      {"Id":19,"Provider":"Microsoft-Windows-WHEA-Logger","Time":"2026-11-06T03:47:57.0000000Z","Message":"A corrected hardware error has occurred."},
      {"Id":1074,"Provider":"User32","Time":"2026-11-06T03:00:00.0000000Z","Message":"The process wuauclt.exe has initiated the restart"}
    ]"#;

    #[test]
    fn reads_the_scripts_json_and_keeps_only_the_events_that_matter() {
        let events = parse_events(TWO);

        assert_eq!(events.len(), 2);
        assert_eq!(events[0].kind, "UnexpectedShutdown");
        assert_eq!(events[0].occurred_utc, "2026-11-06T04:04:12.5Z");
        assert_eq!(events[0].detail.as_deref(), Some("The system has rebooted without cleanly shutting down first."));
        assert_eq!(events[1].kind, "HardwareErrorCorrected");
    }

    #[test]
    fn a_single_event_may_come_back_as_an_object() {
        let events = parse_events(r#"{"Id":41,"Provider":"Microsoft-Windows-Kernel-Power","Time":"2026-11-06T04:04:12Z","Message":"x"}"#);

        assert_eq!(events.len(), 1);
    }

    #[test]
    fn nothing_printed_or_an_empty_array_is_no_events() {
        assert!(parse_events("").is_empty());
        assert!(parse_events("   \n").is_empty());
        assert!(parse_events("[]").is_empty());
        assert!(parse_events("\u{feff}[]").is_empty());
    }

    #[test]
    fn unreadable_output_is_no_events_rather_than_an_error() {
        assert!(parse_events("not json").is_empty());
        assert!(parse_events("42").is_empty());
        assert!(parse_events(r#"[{"Id":41}]"#).is_empty());
        assert!(parse_events(r#"[{"Id":41,"Provider":"Microsoft-Windows-Kernel-Power","Time":"yesterday"}]"#).is_empty());
    }

    #[test]
    fn a_time_with_an_offset_is_written_in_utc() {
        let events = parse_events(r#"[{"Id":41,"Provider":"Microsoft-Windows-Kernel-Power","Time":"2026-11-06T14:04:00+10:00","Message":"x"}]"#);

        assert_eq!(events[0].occurred_utc, "2026-11-06T04:04:00Z");
    }

    #[test]
    fn the_excerpt_is_short() {
        let json = format!(r#"[{{"Id":41,"Provider":"Microsoft-Windows-Kernel-Power","Time":"2026-11-06T04:04:12Z","Message":"{}"}}]"#, "x".repeat(2000));

        assert!(parse_events(&json)[0].detail.as_ref().unwrap().chars().count() <= MAX_DETAIL + 1);
    }

    #[test]
    fn a_burst_of_one_kind_is_cut_to_the_newest_and_does_not_hide_the_other() {
        let mut events: Vec<HealthEvent> = (0..50)
            .map(|i| HealthEvent { occurred_utc: format!("2026-11-06T04:{:02}:00Z", i % 60), kind: "HardwareErrorCorrected", source: "x".into(), detail: None })
            .collect();
        events.push(HealthEvent { occurred_utc: "2026-10-01T00:00:00Z".into(), kind: "UnexpectedShutdown", source: "x".into(), detail: None });

        let kept = cap_newest(events);

        assert_eq!(kept.iter().filter(|e| e.kind == "HardwareErrorCorrected").count(), MAX_PER_KIND);
        assert!(kept.iter().any(|e| e.kind == "UnexpectedShutdown"));
    }

    #[test]
    fn the_script_reads_the_system_log_and_changes_nothing() {
        let s = script(1_730_000_000);

        assert!(s.starts_with(UTF8_OUTPUT), "decoded as UTF-8, so it must ask for UTF-8");
        assert!(s.contains("FromUnixTimeSeconds(1730000000)"));
        assert!(s.contains("LogName = 'System'"));
        assert!(s.contains("Get-WinEvent"));
        for forbidden in ["Remove-", "Set-", "Stop-", "Restart-", "Clear-", "Invoke-", "New-Item", "Out-File"] {
            assert!(!s.contains(forbidden), "{forbidden}");
        }
    }

    #[test]
    fn the_report_uses_the_names_the_server_reads() {
        let events = parse_events(TWO);
        let json = serde_json::to_string(&ReportHealthEventsRequest { serial_number: "S1", events: &events }).unwrap();

        assert!(json.contains("\"serialNumber\":\"S1\""));
        assert!(json.contains("\"occurredUtc\":\"2026-11-06T04:04:12.5Z\""));
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
        let _ = std::fs::remove_dir_all(&dir);
    }
}

/// The health script on the machine the tests run on (the CI's Windows runner): it must run and print
/// something the parser reads, even if a healthy machine has no events to report.
#[cfg(all(test, windows))]
mod real_windows {
    use super::*;

    #[test]
    fn the_real_health_script_runs_and_its_output_is_understood() {
        let since = std::time::SystemTime::now().duration_since(std::time::UNIX_EPOCH).unwrap().as_secs() as i64 - 30 * 86_400;

        let output = run_powershell(&script(since)).expect("PowerShell should start");

        assert!(output.status.success(), "the health script failed: {}", String::from_utf8_lossy(&output.stderr));
        let text = String::from_utf8_lossy(&output.stdout);
        let json = text.trim().trim_start_matches('\u{feff}');
        assert!(json.is_empty() || serde_json::from_str::<serde_json::Value>(json).is_ok(), "the script's output is not JSON: {json:?}");
        let _ = parse_events(&text);
    }
}
