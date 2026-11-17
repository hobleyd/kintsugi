//! Sends an administrator the System and Application event logs of this PC, when one has asked.
//!
//! **Read-only.** It reads the two event logs through PowerShell and nothing else: no files, no
//! registry writes. **Best-effort.** Everything here is logged and swallowed; it never fails a check-in,
//! and with no request pending it costs one GET.
//!
//! The GET claims the request on the server, so the log is read and sent at the check-in that claimed
//! it (hourly, so an administrator waits up to an hour). The work gets a hard budget of
//! [`TIME_BUDGET`]; past it the server is told the log could not be read.

use std::io::Write;
use std::sync::mpsc;
use std::time::Duration;

use anyhow::{Context, Result};
use flate2::write::GzEncoder;
use flate2::Compression;
use serde::{Deserialize, Serialize};
use time::format_description::well_known::Rfc3339;
use time::OffsetDateTime;

use crate::config::Config;
use crate::logging;
use crate::os_update::{run_powershell_within, timed_out, UTF8_OUTPUT};

const TIME_BUDGET: Duration = Duration::from_secs(5 * 60);
/// The reading thread is waited for this much longer than the interpreter is given, so PowerShell is
/// killed (and says so) before the wait gives up.
const WAIT_GRACE: Duration = Duration::from_secs(15);
const TIMED_OUT_REASON: &str = "Reading the log took longer than 5 minutes.";
const MAX_ENTRIES: usize = 20_000;
const MAX_GZIP_BYTES: usize = 5 * 1024 * 1024;
/// How many events the script asks Windows for, before the entry cap is applied.
const MAX_EVENTS_READ: u32 = 25_000;
const MAX_SRC: usize = 120;
const MAX_MSG: usize = 2000;
const MAX_REASON: usize = 500;
const MIN_DAYS: i64 = 1;
const MAX_DAYS: i64 = 365;
/// Per HTTP call, so a dead server cannot hold the check-in.
const GET_TIMEOUT: Duration = Duration::from_secs(30);
const POST_TIMEOUT: Duration = Duration::from_secs(120);

#[derive(Debug, Clone, Copy, Deserialize, PartialEq, Eq)]
pub(crate) enum Detail {
    WarningsAndErrors,
    Everything,
}

#[derive(Debug, Clone, Deserialize, PartialEq, Eq)]
#[serde(rename_all = "camelCase")]
pub(crate) struct LogCollectionRequest {
    pub id: String,
    pub period_days: i64,
    pub detail: Detail,
}

#[derive(Debug, Clone, Serialize, PartialEq, Eq)]
pub(crate) struct LogEntry {
    /// RFC 3339, UTC, whole seconds.
    pub t: String,
    pub sev: &'static str,
    pub src: String,
    pub msg: String,
}

#[derive(Debug, Serialize)]
#[serde(rename_all = "camelCase")]
struct LogPayload<'a> {
    entries: &'a [LogEntry],
    truncated_before: Option<String>,
}

#[derive(Debug, Serialize)]
#[serde(rename_all = "camelCase")]
struct FailureRequest<'a> {
    serial_number: &'a str,
    collection_id: &'a str,
    reason: String,
}

/// Checks for a pending request and, if there is one, reads and uploads the log. Never fails.
pub fn collect_and_upload_if_requested(client: &reqwest::blocking::Client, config: &Config, serial_number: &str) {
    if let Err(err) = try_collect(client, config, serial_number) {
        logging::warn(&format!("log collection: {err:#}"));
    }
}

fn try_collect(client: &reqwest::blocking::Client, config: &Config, serial_number: &str) -> Result<()> {
    let Some(request) = fetch_request(client, config, serial_number)? else {
        return Ok(());
    };
    logging::info(&format!(
        "log collection {} requested: {} day(s), {:?}",
        request.id, request.period_days, request.detail
    ));

    let since = now_epoch() - request.period_days.clamp(MIN_DAYS, MAX_DAYS) * 86_400;
    let detail = request.detail;
    let read = read_with_budget(TIME_BUDGET + WAIT_GRACE, move || read_log(since, detail));

    let entries = match read {
        Ok(entries) => entries,
        Err(reason) => {
            logging::warn(&format!("log collection {}: {reason}", request.id));
            return report_failure(client, config, serial_number, &request.id, &reason);
        }
    };

    let (body, kept, truncated_before) = build_payload(entries, MAX_ENTRIES, MAX_GZIP_BYTES)?;
    let sent = client
        .post(config.log_collection_results_url())
        .query(&[("serialNumber", serial_number), ("collectionId", request.id.as_str())])
        .header("Content-Type", "application/gzip")
        .timeout(POST_TIMEOUT)
        .body(body)
        .send();
    // A log that was read but did not arrive is reported as a failure, so the page says so now instead of
    // waiting out its 30-minute general error. (If the network is what failed, that report fails too, and
    // the page falls back to the timeout as before.)
    let rejected = match &sent {
        Ok(response) if response.status().is_success() => None,
        Ok(response) => Some(Some(response.status().as_u16())),
        Err(err) => {
            logging::warn(&format!("log collection {}: the log could not be sent: {err:#}", request.id));
            Some(None)
        }
    };
    if let Some(status) = rejected {
        let reason = undelivered_reason(status);
        logging::warn(&format!("log collection {}: {reason}", request.id));
        return report_failure(client, config, serial_number, &request.id, &reason);
    }
    logging::info(&format!(
        "log collection {} uploaded: {kept} entries{}",
        request.id,
        if truncated_before.is_some() { " (older ones dropped)" } else { "" }
    ));
    Ok(())
}

fn fetch_request(client: &reqwest::blocking::Client, config: &Config, serial_number: &str) -> Result<Option<LogCollectionRequest>> {
    let response = client
        .get(config.log_collection_requests_url())
        .query(&[("serialNumber", serial_number)])
        .timeout(GET_TIMEOUT)
        .send()
        .context("could not ask for a log collection request")?;
    if !response.status().is_success() {
        anyhow::bail!("request rejected (HTTP {})", response.status());
    }
    let text = response.text().context("could not read response")?;
    parse_requests(&text)
}

/// The response is an array of zero or one request.
pub(crate) fn parse_requests(json: &str) -> Result<Option<LogCollectionRequest>> {
    let requests: Vec<LogCollectionRequest> = serde_json::from_str(json).context("could not parse response")?;
    Ok(requests.into_iter().next())
}

/// What the administrator is told when the log was read but did not reach the server. A plain sentence:
/// the transport detail (a URL, a socket error) goes to the agent's own log, not to the page.
pub(crate) fn undelivered_reason(http_status: Option<u16>) -> String {
    match http_status {
        Some(code) => format!("The device read its log but the server rejected it (HTTP {code})."),
        None => "The device read its log but could not send it to the server.".to_string(),
    }
}

fn report_failure(client: &reqwest::blocking::Client, config: &Config, serial_number: &str, id: &str, reason: &str) -> Result<()> {
    let body = failure_body(serial_number, id, reason);
    let response = client
        .post(config.log_collection_failures_url())
        .header("Content-Type", "application/json")
        .timeout(GET_TIMEOUT)
        .body(body)
        .send()
        .context("the failure could not be sent")?;
    if !response.status().is_success() {
        anyhow::bail!("the server rejected the failure report (HTTP {})", response.status());
    }
    Ok(())
}

pub(crate) fn failure_body(serial_number: &str, id: &str, reason: &str) -> String {
    let request = FailureRequest { serial_number, collection_id: id, reason: truncate(reason.trim(), MAX_REASON) };
    serde_json::to_string(&request).unwrap_or_default()
}

/// Runs `work` on a thread and waits at most `budget` for it. A thread that outlives the budget is left
/// to finish by itself; its result goes nowhere.
pub(crate) fn read_with_budget<F>(budget: Duration, work: F) -> std::result::Result<Vec<LogEntry>, String>
where
    F: FnOnce() -> std::result::Result<Vec<LogEntry>, String> + Send + 'static,
{
    let (tx, rx) = mpsc::channel();
    std::thread::spawn(move || {
        let _ = tx.send(work());
    });
    match rx.recv_timeout(budget) {
        Ok(result) => result,
        Err(mpsc::RecvTimeoutError::Timeout) => Err(TIMED_OUT_REASON.to_string()),
        Err(mpsc::RecvTimeoutError::Disconnected) => Err("Reading the log stopped unexpectedly.".to_string()),
    }
}

fn read_log(since: i64, detail: Detail) -> std::result::Result<Vec<LogEntry>, String> {
    let output = run_powershell_within(&script(since, detail), TIME_BUDGET).map_err(|e| {
        if timed_out(&e) {
            TIMED_OUT_REASON.to_string()
        } else {
            format!("Windows PowerShell could not be started: {e:#}")
        }
    })?;
    if !output.status.success() {
        let stderr = String::from_utf8_lossy(&output.stderr);
        return Err(format!("Reading the event log failed: {}", stderr.trim()));
    }
    parse_entries(&String::from_utf8_lossy(&output.stdout), detail)
}

fn now_epoch() -> i64 {
    std::time::SystemTime::now()
        .duration_since(std::time::UNIX_EPOCH)
        .map(|d| d.as_secs() as i64)
        .unwrap_or(0)
}

/// The PowerShell that reads the System and Application logs since `since` (epoch seconds). The only
/// interpolations are that integer, a fixed level list and fixed numbers; nothing from the server is
/// ever text in the script. Prints a JSON array of `{ T, P, I, L, M }`, and changes nothing.
pub(crate) fn script(since: i64, detail: Detail) -> String {
    let levels = match detail {
        Detail::WarningsAndErrors => "1, 2, 3",
        Detail::Everything => "1, 2, 3, 4",
    };
    format!(
        r#"{UTF8_OUTPUT}$ErrorActionPreference = 'SilentlyContinue'
$start = [DateTimeOffset]::FromUnixTimeSeconds({since}).UtcDateTime
$items = @(Get-WinEvent -FilterHashtable @{{ LogName = 'System', 'Application'; StartTime = $start; Level = {levels} }} -MaxEvents {MAX_EVENTS_READ} |
  ForEach-Object {{
    $m = $_.Message
    if ($m -and $m.Length -gt {MAX_MSG}) {{ $m = $m.Substring(0, {MAX_MSG}) }}
    [pscustomobject]@{{ T = $_.TimeCreated.ToUniversalTime().ToString('o'); P = $_.ProviderName; I = $_.Id; L = $_.Level; M = $m }}
  }})
ConvertTo-Json -InputObject $items -Compress"#
    )
}

/// Windows' event level to the wire's severity. Verbose (5), "LogAlways" (0) and anything else is none.
pub(crate) fn severity(level: u64) -> Option<&'static str> {
    match level {
        1 => Some("Critical"),
        2 => Some("Error"),
        3 => Some("Warning"),
        4 => Some("Info"),
        _ => None,
    }
}

/// Reads the script's JSON (an array, or a lone object) into entries, newest first. An event without a
/// time or a usable level is skipped. Empty or unreadable output is an **error**, not an empty log: the
/// script prints `[]` when it finds nothing, and with `SilentlyContinue` a script that broke would
/// otherwise look like a quiet machine and go up as a successful empty log.
pub(crate) fn parse_entries(json: &str, detail: Detail) -> std::result::Result<Vec<LogEntry>, String> {
    let json = json.trim().trim_start_matches('\u{feff}');
    if json.is_empty() {
        return Err("Windows returned no output when the event log was read.".to_string());
    }
    let Ok(value) = serde_json::from_str::<serde_json::Value>(json) else {
        return Err("Windows returned output that was not the event log.".to_string());
    };
    let items: Vec<&serde_json::Value> = match &value {
        serde_json::Value::Array(items) => items.iter().collect(),
        serde_json::Value::Object(_) => vec![&value],
        _ => return Err("Windows returned output that was not the event log.".to_string()),
    };

    let mut entries: Vec<LogEntry> = items
        .into_iter()
        .filter_map(|item| {
            let sev = severity(item.get("L")?.as_u64()?)?;
            if sev == "Info" && detail == Detail::WarningsAndErrors {
                return None;
            }
            let time = OffsetDateTime::parse(item.get("T")?.as_str()?, &Rfc3339).ok()?;
            let t = time.to_offset(time::UtcOffset::UTC).replace_nanosecond(0).ok()?.format(&Rfc3339).ok()?;

            let provider = item.get("P").and_then(|v| v.as_str()).map(str::trim).filter(|s| !s.is_empty());
            let id = item.get("I").and_then(|v| v.as_u64());
            let src = match (provider, id) {
                (Some(p), Some(i)) => format!("{p} {i}"),
                (Some(p), None) => p.to_string(),
                (None, Some(i)) => format!("unknown {i}"),
                (None, None) => "unknown".to_string(),
            };
            let msg = item.get("M").and_then(|v| v.as_str()).unwrap_or("").trim();

            Some(LogEntry { t, sev, src: cut(&src, MAX_SRC), msg: cut(msg, MAX_MSG) })
        })
        .collect();

    // RFC 3339 UTC at one precision sorts as text; stable, so same-second events keep their order.
    entries.sort_by(|a, b| b.t.cmp(&a.t));
    Ok(entries)
}

fn cut(text: &str, max: usize) -> String {
    text.chars().take(max).collect()
}

fn truncate(text: &str, max: usize) -> String {
    cut(text, max)
}

fn gzip_json(entries: &[LogEntry], truncated_before: Option<String>) -> Result<Vec<u8>> {
    let json = serde_json::to_vec(&LogPayload { entries, truncated_before }).context("could not encode the log")?;
    let mut encoder = GzEncoder::new(Vec::new(), Compression::default());
    encoder.write_all(&json).context("could not compress the log")?;
    encoder.finish().context("could not compress the log")
}

/// Applies the caps to `entries` (newest first) and returns the gzip body, how many entries it holds
/// and `truncatedBefore`. Entries go out oldest first. Only the newest `max_entries` are kept, and if
/// the gzip is still over `max_bytes`, the most that fit are kept, again dropping the oldest.
pub(crate) fn build_payload(mut entries: Vec<LogEntry>, max_entries: usize, max_bytes: usize) -> Result<(Vec<u8>, usize, Option<String>)> {
    let mut dropped = entries.len() > max_entries;
    entries.truncate(max_entries);
    entries.reverse(); // oldest first; the newest `n` are the last `n`

    let build = |n: usize, dropped: bool| -> Result<Vec<u8>> {
        let slice = &entries[entries.len() - n..];
        let truncated_before = if dropped { slice.first().map(|e| e.t.clone()) } else { None };
        gzip_json(slice, truncated_before)
    };

    let mut n = entries.len();
    let mut body = build(n, dropped)?;
    if body.len() > max_bytes {
        dropped = true;
        // Largest n in [0, len) whose gzip fits; n = 0 is the floor and always fits in practice.
        let (mut lo, mut hi) = (0usize, n - 1);
        while lo < hi {
            let mid = (lo + hi + 1) / 2;
            if build(mid, true)?.len() <= max_bytes {
                lo = mid;
            } else {
                hi = mid - 1;
            }
        }
        n = lo;
        body = build(n, dropped)?;
    }
    let truncated_before = if dropped { entries[entries.len() - n..].first().map(|e| e.t.clone()) } else { None };
    Ok((body, n, truncated_before))
}

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn an_undelivered_log_is_reported_in_a_plain_sentence_without_transport_detail() {
        assert_eq!(undelivered_reason(Some(400)), "The device read its log but the server rejected it (HTTP 400).");
        let unreachable = undelivered_reason(None);
        assert_eq!(unreachable, "The device read its log but could not send it to the server.");
        assert!(!unreachable.contains("http") && !unreachable.contains("://"));
    }

    use std::io::Read;

    fn entry(secs: i64, sev: &'static str) -> LogEntry {
        let t = OffsetDateTime::from_unix_timestamp(1_700_000_000 + secs).unwrap().format(&Rfc3339).unwrap();
        LogEntry { t, sev, src: format!("Src {secs}"), msg: format!("message {secs}") }
    }

    fn unzip(body: &[u8]) -> serde_json::Value {
        let mut text = String::new();
        flate2::read::GzDecoder::new(body).read_to_string(&mut text).unwrap();
        serde_json::from_str(&text).unwrap()
    }

    #[test]
    fn severity_mapping() {
        assert_eq!(severity(1), Some("Critical"));
        assert_eq!(severity(2), Some("Error"));
        assert_eq!(severity(3), Some("Warning"));
        assert_eq!(severity(4), Some("Info"));
        assert_eq!(severity(0), None);
        assert_eq!(severity(5), None);
    }

    #[test]
    fn parses_an_array_newest_first() {
        let json = r#"[{"T":"2026-11-09T09:24:11.1234567Z","P":"WHEA-Logger","I":18,"L":1,"M":" boom \r\n"},
                       {"T":"2026-11-09T10:00:00.0000000Z","P":"Disk","I":7,"L":3,"M":"bad block"}]"#;
        let e = parse_entries(json, Detail::WarningsAndErrors).unwrap();
        assert_eq!(e.len(), 2);
        assert_eq!(e[0].t, "2026-11-09T10:00:00Z");
        assert_eq!(e[0].src, "Disk 7");
        assert_eq!(e[1].t, "2026-11-09T09:24:11Z");
        assert_eq!(e[1].sev, "Critical");
        assert_eq!(e[1].src, "WHEA-Logger 18");
        assert_eq!(e[1].msg, "boom");
    }

    #[test]
    fn parses_a_single_object_and_offsets() {
        let e = parse_entries(r#"{"T":"2026-11-09T11:00:00+02:00","P":"X","I":1,"L":2,"M":"m"}"#, Detail::Everything).unwrap();
        assert_eq!(e.len(), 1);
        assert_eq!(e[0].t, "2026-11-09T09:00:00Z");
    }

    #[test]
    fn tolerates_null_and_missing_fields() {
        let json = r#"[{"T":"2026-11-09T09:00:00Z","P":null,"I":null,"L":2,"M":null},
                       {"T":"2026-11-09T09:01:00Z","P":"OnlyProvider","L":2},
                       {"P":"NoTime","I":1,"L":2},
                       {"T":"2026-11-09T09:02:00Z","P":"NoLevel","I":1},
                       {"T":"2026-11-09T09:03:00Z","P":"Verbose","I":1,"L":5},
                       {"T":"2026-11-09T09:04:00Z","P":"Always","I":1,"L":0},
                       {"T":"not a time","P":"Bad","I":1,"L":2}]"#;
        let e = parse_entries(json, Detail::Everything).unwrap();
        assert_eq!(e.len(), 2);
        assert_eq!(e[0].src, "OnlyProvider");
        assert_eq!(e[0].msg, "");
        assert_eq!(e[1].src, "unknown");
    }

    #[test]
    fn empty_and_unreadable_output_is_an_error_but_an_empty_array_is_a_quiet_machine() {
        assert!(parse_entries("", Detail::Everything).is_err());
        assert!(parse_entries("\u{feff}  ", Detail::Everything).is_err());
        assert!(parse_entries("not json", Detail::Everything).is_err());
        assert!(parse_entries("42", Detail::Everything).is_err());
        // The script prints [] when it finds nothing; that is a real, empty log.
        assert!(parse_entries("[]", Detail::Everything).unwrap().is_empty());
    }

    #[test]
    fn info_only_with_everything() {
        let json = r#"[{"T":"2026-11-09T09:00:00Z","P":"Windows Error Reporting","I":1001,"L":4,"M":"Fault bucket"}]"#;
        assert!(parse_entries(json, Detail::WarningsAndErrors).unwrap().is_empty());
        let e = parse_entries(json, Detail::Everything).unwrap();
        assert_eq!(e[0].sev, "Info");
        assert_eq!(e[0].src, "Windows Error Reporting 1001");
    }

    #[test]
    fn truncates_src_and_msg_keeping_newlines() {
        let json = serde_json::json!([{
            "T": "2026-11-09T09:00:00Z",
            "P": "p".repeat(500),
            "I": 1,
            "L": 2,
            "M": format!("line1\nline2{}", "é".repeat(5000)),
        }])
        .to_string();
        let e = parse_entries(&json, Detail::Everything).unwrap();
        assert_eq!(e[0].src.chars().count(), 120);
        assert_eq!(e[0].msg.chars().count(), 2000);
        assert!(e[0].msg.starts_with("line1\nline2"));
    }

    #[test]
    fn request_shape() {
        let r = parse_requests(r#"[{"id":"abc","periodDays":30,"detail":"WarningsAndErrors"}]"#).unwrap().unwrap();
        assert_eq!(r, LogCollectionRequest { id: "abc".into(), period_days: 30, detail: Detail::WarningsAndErrors });
        let r = parse_requests(r#"[{"id":"abc","periodDays":7,"detail":"Everything"}]"#).unwrap().unwrap();
        assert_eq!(r.detail, Detail::Everything);
        assert!(parse_requests("[]").unwrap().is_none());
        assert!(parse_requests("nonsense").is_err());
    }

    #[test]
    fn failure_body_shape_and_cap() {
        let v: serde_json::Value = serde_json::from_str(&failure_body("S1", "id-1", "It broke.")).unwrap();
        assert_eq!(v, serde_json::json!({"serialNumber":"S1","collectionId":"id-1","reason":"It broke."}));
        let long: serde_json::Value = serde_json::from_str(&failure_body("S", "i", &"x".repeat(900))).unwrap();
        assert_eq!(long["reason"].as_str().unwrap().len(), 500);
    }

    #[test]
    fn payload_shape_and_round_trip() {
        let entries = vec![entry(20, "Error"), entry(10, "Warning")];
        let (body, kept, tb) = build_payload(entries.clone(), 100, 1 << 20).unwrap();
        assert_eq!(kept, 2);
        assert_eq!(tb, None);
        let v = unzip(&body);
        assert!(v["truncatedBefore"].is_null());
        let out = v["entries"].as_array().unwrap();
        assert_eq!(out.len(), 2);
        assert_eq!(out[0]["t"], entries[1].t); // oldest first
        assert_eq!(out[0]["sev"], "Warning");
        assert_eq!(out[0]["src"], "Src 10");
        assert_eq!(out[0]["msg"], "message 10");
        assert_eq!(out[1]["sev"], "Error");
    }

    #[test]
    fn empty_payload_is_valid() {
        let (body, kept, tb) = build_payload(Vec::new(), 10, 1 << 20).unwrap();
        assert_eq!((kept, tb), (0, None));
        let v = unzip(&body);
        assert_eq!(v["entries"].as_array().unwrap().len(), 0);
        assert!(v["truncatedBefore"].is_null());
    }

    #[test]
    fn entry_cap_keeps_newest_and_sets_truncated_before() {
        let entries: Vec<LogEntry> = (0..10).rev().map(|i| entry(i * 10, "Error")).collect(); // newest first
        let (body, kept, tb) = build_payload(entries.clone(), 4, 1 << 20).unwrap();
        assert_eq!(kept, 4);
        let oldest_kept = entries[3].t.clone();
        assert_eq!(tb.as_deref(), Some(oldest_kept.as_str()));
        let v = unzip(&body);
        assert_eq!(v["truncatedBefore"], oldest_kept);
        let out = v["entries"].as_array().unwrap();
        assert_eq!(out.len(), 4);
        assert_eq!(out[0]["t"], oldest_kept);
        assert_eq!(out[3]["t"], entries[0].t);
    }

    #[test]
    fn exactly_at_entry_cap_is_not_truncated() {
        let entries: Vec<LogEntry> = (0..4).rev().map(|i| entry(i, "Error")).collect();
        let (_, kept, tb) = build_payload(entries, 4, 1 << 20).unwrap();
        assert_eq!((kept, tb), (4, None));
    }

    #[test]
    fn real_cap_is_20000() {
        let entries: Vec<LogEntry> = (0..20_005).rev().map(|i| entry(i, "Error")).collect();
        let (_, kept, tb) = build_payload(entries, MAX_ENTRIES, MAX_GZIP_BYTES).unwrap();
        assert_eq!(kept, 20_000);
        assert!(tb.is_some());
    }

    #[test]
    fn byte_cap_drops_oldest() {
        // Incompressible-ish messages so the gzip size grows with the entry count.
        let mut seed = 12345u64;
        let mut noise = || {
            (0..200)
                .map(|_| {
                    seed = seed.wrapping_mul(6364136223846793005).wrapping_add(1442695040888963407);
                    (b'a' + ((seed >> 33) % 26) as u8) as char
                })
                .collect::<String>()
        };
        let entries: Vec<LogEntry> = (0..500)
            .rev()
            .map(|i| {
                let mut e = entry(i, "Error");
                e.msg = noise();
                e
            })
            .collect();
        let limit = 40_000;
        let (full, ..) = build_payload(entries.clone(), 1000, usize::MAX).unwrap();
        assert!(full.len() > limit, "test data must exceed the limit, got {}", full.len());

        let (body, kept, tb) = build_payload(entries.clone(), 1000, limit).unwrap();
        assert!(body.len() <= limit);
        assert!(kept > 0 && kept < 500);
        let v = unzip(&body);
        let out = v["entries"].as_array().unwrap();
        assert_eq!(out.len(), kept);
        assert_eq!(out[0]["t"], entries[kept - 1].t);
        assert_eq!(v["truncatedBefore"], out[0]["t"]);
        assert_eq!(tb.as_deref(), out[0]["t"].as_str());
        assert_eq!(out[kept - 1]["t"], entries[0].t); // the newest survived
        // One more entry would not have fit.
        let (bigger, ..) = build_payload(entries.clone(), kept + 1, usize::MAX).unwrap();
        assert!(bigger.len() > limit);
    }

    #[test]
    fn time_budget_reports_a_timeout() {
        let r = read_with_budget(Duration::from_millis(50), || {
            std::thread::sleep(Duration::from_millis(1000));
            Ok(Vec::new())
        });
        assert_eq!(r.unwrap_err(), "Reading the log took longer than 5 minutes.");
        let ok = read_with_budget(Duration::from_secs(5), || Ok(vec![entry(1, "Error")]));
        assert_eq!(ok.unwrap().len(), 1);
        let err = read_with_budget(Duration::from_secs(5), || Err("nope".to_string()));
        assert_eq!(err.unwrap_err(), "nope");
    }

    #[test]
    fn the_script_asks_powershell_for_utf8_output_first() {
        assert!(script(1, Detail::Everything).starts_with(UTF8_OUTPUT));
        assert!(UTF8_OUTPUT.contains("[Console]::OutputEncoding") && UTF8_OUTPUT.contains("UTF8Encoding]::new($false)"));
        // A host with no console to set must not stop the script.
        assert!(UTF8_OUTPUT.starts_with("try {") && UTF8_OUTPUT.contains("catch"));
    }

    #[test]
    fn script_only_interpolates_the_integer_and_fixed_levels() {
        let a = script(1_234_567_890, Detail::WarningsAndErrors);
        let b = script(987_654_321, Detail::WarningsAndErrors);
        assert!(a.contains("FromUnixTimeSeconds(1234567890)"));
        assert_eq!(a.replace("1234567890", "#"), b.replace("987654321", "#"));
        assert!(a.contains("LogName = 'System', 'Application'"));
        assert!(a.contains("Level = 1, 2, 3 }"));
        assert!(!a.contains("{since}") && !a.contains("{levels}"));
        let e = script(1_234_567_890, Detail::Everything);
        assert!(e.contains("Level = 1, 2, 3, 4 }"));
        assert_eq!(a.replace("1, 2, 3 }", "L"), e.replace("1, 2, 3, 4 }", "L"));
        assert!(a.contains("-MaxEvents 25000"));
    }
}

/// Runs the real PowerShell script on the machine the tests run on (the CI's Windows runner): the
/// real `Get-WinEvent`, the real JSON, the real parser. Everything is read, so a machine that has been
/// up for minutes always has something; the failures are not the point, the format is.
#[cfg(all(test, windows))]
mod real_windows {
    use super::*;

    #[test]
    fn the_real_event_log_is_read_and_every_entry_is_well_formed() {
        let since = now_epoch() - 7 * 86_400;

        let entries = read_log(since, Detail::Everything).expect("the real event log should be readable");

        assert!(!entries.is_empty(), "a Windows machine has events in a week: the reader found nothing");
        for e in &entries {
            assert!(e.t.len() == 20 && e.t.ends_with('Z'), "time should be RFC 3339 UTC, whole seconds: {:?}", e.t);
            assert!(["Critical", "Error", "Warning", "Info"].contains(&e.sev));
            assert!(!e.src.is_empty() && e.src.chars().count() <= MAX_SRC + 1, "source: {:?}", e.src);
            assert!(e.msg.chars().count() <= MAX_MSG + 1);
        }
        let (body, kept, _) = build_payload(entries, MAX_ENTRIES, MAX_GZIP_BYTES).expect("the real entries should fit the caps");
        assert!(kept > 0 && body.len() <= MAX_GZIP_BYTES);
    }

    /// A script that never finishes is killed at the deadline, not just left behind.
    #[test]
    fn a_runaway_powershell_is_killed_at_its_deadline() {
        let started = std::time::Instant::now();

        let result = run_powershell_within("Start-Sleep -Seconds 120", Duration::from_secs(3));

        assert!(timed_out(&result.expect_err("it should time out")));
        assert!(started.elapsed() < Duration::from_secs(30), "the deadline was not enforced");
    }

    /// Non-ASCII text survives the trip: an accented letter, a curly quote and a dash.
    #[test]
    fn utf8_text_round_trips_through_the_real_powershell() {
        let script = format!("{UTF8_OUTPUT}[Console]::Out.Write([string]::Join('', @([char]0x00E9, [char]0x2019, [char]0x2013)))");

        let output = run_powershell_within(&script, Duration::from_secs(60)).expect("PowerShell should run");

        assert_eq!(String::from_utf8_lossy(&output.stdout), "\u{e9}\u{2019}\u{2013}");
    }

    #[test]
    fn warnings_and_errors_runs_on_the_real_log() {
        let entries = read_log(now_epoch() - 7 * 86_400, Detail::WarningsAndErrors).expect("the real event log should be readable");

        assert!(entries.iter().all(|e| e.sev != "Info"));
    }
}
