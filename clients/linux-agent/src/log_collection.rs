//! On-demand collection of this host's own log, for an administrator who asked for it from the web UI.
//!
//! **Read-only.** The only thing run is `journalctl`; nothing else is read. **Best-effort:** it is called
//! on every check-in (root side, right after the device-health report) and never fails or noticeably
//! delays it. With nothing requested it is one small GET. The request is consumed server-side by that GET.
//!
//! Source: the systemd journal, newest first. Caps: 20,000 entries and a 5 MiB gzip body; whichever forces
//! entries to be dropped drops the oldest and reports the time of the oldest one kept as `truncatedBefore`.
//! Reading the log has a hard 5 minute budget; the `journalctl` child is killed when it runs out.

use std::io::{BufRead, BufReader, Write};
use std::process::{Command, Stdio};
use std::sync::mpsc;
use std::time::Duration;

use anyhow::{Context, Result};
use flate2::write::GzEncoder;
use flate2::Compression;
use serde::{Deserialize, Serialize};
use serde_json::Value;
use time::format_description::well_known::Rfc3339;
use time::OffsetDateTime;

use crate::config::Config;
use crate::logging;

/// Most entries sent. The newest are kept.
const MAX_ENTRIES: usize = 20_000;
/// Most lines asked of `journalctl` (a little over the cap, since lines can be skipped).
const JOURNAL_LINE_LIMIT: usize = 25_000;
/// Largest gzip body sent.
const MAX_GZIP_BYTES: usize = 5 * 1024 * 1024;
const MAX_SRC_CHARS: usize = 120;
const MAX_MSG_CHARS: usize = 2000;
const MAX_REASON_CHARS: usize = 500;
const READ_BUDGET: Duration = Duration::from_secs(5 * 60);
const REQUEST_TIMEOUT: Duration = Duration::from_secs(30);
const UPLOAD_TIMEOUT: Duration = Duration::from_secs(120);
const MAX_PERIOD_DAYS: i64 = 3650;

/// One pending request, as `GET /api/log-collection-requests` serves it.
#[derive(Debug, Clone, Deserialize, PartialEq, Eq)]
#[serde(rename_all = "camelCase")]
pub struct LogCollectionRequest {
    pub id: String,
    pub period_days: i64,
    /// "WarningsAndErrors" or "Everything".
    pub detail: String,
}

impl LogCollectionRequest {
    fn everything(&self) -> bool {
        self.detail == "Everything"
    }
}

#[derive(Debug, Clone, Serialize, PartialEq, Eq)]
pub struct LogEntry {
    /// RFC 3339, UTC.
    pub t: String,
    /// "Critical", "Error", "Warning" or "Info".
    pub sev: &'static str,
    pub src: String,
    pub msg: String,
}

#[derive(Debug, Serialize, PartialEq, Eq)]
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

/// Why reading the journal produced nothing usable.
enum ReadOutcome {
    /// Entries newest first, and whether the line limit was reached (older ones may exist).
    Entries(Vec<LogEntry>, bool),
    Failed(String),
}

/// Asks the server whether a collection is wanted and, if so, does it. Never fails the caller.
pub fn collect_and_upload_if_requested(client: &reqwest::blocking::Client, config: &Config, serial_number: &str) {
    match fetch_request(client, config, serial_number) {
        Ok(Some(request)) => {
            logging::info(&format!("log collection requested ({} days, {})", request.period_days, request.detail));
            if let Err(err) = fulfil(client, config, serial_number, &request) {
                logging::warn(&format!("could not deliver the collected log: {err:#}"));
            }
        }
        Ok(None) => {}
        Err(err) => logging::warn(&format!("could not check for a log collection request: {err:#}")),
    }
}

fn fetch_request(client: &reqwest::blocking::Client, config: &Config, serial_number: &str) -> Result<Option<LogCollectionRequest>> {
    let response = client
        .get(config.log_collection_requests_url())
        .query(&[("serialNumber", serial_number)])
        .timeout(REQUEST_TIMEOUT)
        .send()
        .context("the request could not be sent")?;
    if !response.status().is_success() {
        anyhow::bail!("request rejected (HTTP {})", response.status());
    }
    let requests = response.json::<Vec<LogCollectionRequest>>().context("could not parse response")?;
    Ok(requests.into_iter().next())
}

fn fulfil(client: &reqwest::blocking::Client, config: &Config, serial_number: &str, request: &LogCollectionRequest) -> Result<()> {
    let period = request.period_days.clamp(1, MAX_PERIOD_DAYS);
    let since = now_epoch() - period * 24 * 3600;

    match read_journal(since, request.everything(), READ_BUDGET) {
        ReadOutcome::Failed(reason) => {
            logging::warn(&format!("log collection failed: {reason}"));
            report_failure(client, config, serial_number, &request.id, &reason)
        }
        ReadOutcome::Entries(entries, hit_limit) => {
            let (body, kept) = encode_capped(&entries, hit_limit, MAX_ENTRIES, MAX_GZIP_BYTES)?;
            let sent = client
                .post(config.log_collection_results_url())
                .query(&[("serialNumber", serial_number), ("collectionId", request.id.as_str())])
                .header("Content-Type", "application/gzip")
                .body(body)
                .timeout(UPLOAD_TIMEOUT)
                .send();
            // A log that was read but did not arrive is reported, so the page says so now instead of waiting
            // out its 30-minute general error. If the network is what failed, this report fails too.
            let rejected = match &sent {
                Ok(response) if response.status().is_success() => None,
                Ok(response) => Some(Some(response.status().as_u16())),
                Err(err) => {
                    logging::warn(&format!("the log could not be sent: {err:#}"));
                    Some(None)
                }
            };
            if let Some(status) = rejected {
                let reason = undelivered_reason(status);
                logging::warn(&format!("log collection failed: {reason}"));
                return report_failure(client, config, serial_number, &request.id, &reason);
            }
            logging::info(&format!("sent {kept} log entr{}", if kept == 1 { "y" } else { "ies" }));
            Ok(())
        }
    }
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
    let body = FailureRequest { serial_number, collection_id: id, reason: truncate_chars(reason, MAX_REASON_CHARS) };
    let response = client
        .post(config.log_collection_failures_url())
        .json(&body)
        .timeout(REQUEST_TIMEOUT)
        .send()
        .context("the failure could not be sent")?;
    if !response.status().is_success() {
        anyhow::bail!("the server rejected the failure report (HTTP {})", response.status());
    }
    Ok(())
}

fn now_epoch() -> i64 {
    std::time::SystemTime::now()
        .duration_since(std::time::UNIX_EPOCH)
        .map(|d| d.as_secs() as i64)
        .unwrap_or(0)
}

/// The `journalctl` arguments for a collection: newest first, JSON, one object per line.
fn journal_args(since: i64, everything: bool) -> Vec<String> {
    vec![
        "--since".to_string(),
        format!("@{since}"),
        "-o".to_string(),
        "json".to_string(),
        "--no-pager".to_string(),
        "-q".to_string(),
        "-n".to_string(),
        JOURNAL_LINE_LIMIT.to_string(),
        "-r".to_string(),
        "-p".to_string(),
        // `-p info` is priorities 0..6 (no debug); `-p warning` is 0..4.
        if everything { "info" } else { "warning" }.to_string(),
    ]
}

/// Runs `journalctl` with a locale that cannot translate anything (as `health.rs` does), parsing on a
/// thread of its own so the wait can be bounded; on timeout the child is killed.
fn read_journal(since: i64, everything: bool, budget: Duration) -> ReadOutcome {
    let mut child = match Command::new("journalctl")
        .args(journal_args(since, everything))
        .env("LC_ALL", "C")
        .env("LANG", "C")
        .stdin(Stdio::null())
        .stdout(Stdio::piped())
        .stderr(Stdio::null())
        .spawn()
    {
        Ok(child) => child,
        // No systemd here: nothing to collect, which is an empty result and not a failure.
        Err(err) if err.kind() == std::io::ErrorKind::NotFound => return ReadOutcome::Entries(Vec::new(), false),
        Err(err) => return ReadOutcome::Failed(format!("The log reader could not be started ({}).", err.kind())),
    };

    let pid = child.id();
    let stdout = child.stdout.take();
    let (tx, rx) = mpsc::channel();
    std::thread::spawn(move || {
        let mut entries = Vec::new();
        let mut lines = 0usize;
        if let Some(stdout) = stdout {
            for line in BufReader::new(stdout).split(b'\n').map_while(|l| l.ok()) {
                lines += 1;
                if let Some(entry) = parse_journal_line(&String::from_utf8_lossy(&line)) {
                    entries.push(entry);
                }
            }
        }
        let status = child.wait();
        let outcome = match status {
            Ok(s) if s.success() => ReadOutcome::Entries(entries, lines >= JOURNAL_LINE_LIMIT),
            Ok(_) => ReadOutcome::Failed("The log reader reported an error.".to_string()),
            Err(_) => ReadOutcome::Failed("The log reader could not be waited for.".to_string()),
        };
        let _ = tx.send(outcome);
    });

    match rx.recv_timeout(budget) {
        Ok(outcome) => outcome,
        Err(_) => {
            // SAFETY: plain kill(2) on the child this function spawned and has not yet reaped (the
            // reader thread reaps it only after its stdout closes, which this causes).
            unsafe {
                libc::kill(pid as libc::pid_t, libc::SIGKILL);
            }
            ReadOutcome::Failed("Reading the log took longer than 5 minutes.".to_string())
        }
    }
}

/// Journal PRIORITY to this feature's severity. 7 (debug) and anything unknown are not collected.
pub(crate) fn severity_for_priority(priority: i64) -> Option<&'static str> {
    match priority {
        0..=2 => Some("Critical"),
        3 => Some("Error"),
        4 => Some("Warning"),
        5 | 6 => Some("Info"),
        _ => None,
    }
}

fn truncate_chars(text: &str, max: usize) -> String {
    match text.char_indices().nth(max) {
        Some((index, _)) => text[..index].to_string(),
        None => text.to_string(),
    }
}

/// A journal field is a string, or (when it holds binary) an array of byte values, or absent/null.
fn field_text(value: Option<&Value>) -> Option<String> {
    match value? {
        Value::String(s) => Some(s.clone()),
        Value::Array(items) => {
            let bytes: Vec<u8> = items.iter().filter_map(|v| v.as_u64().and_then(|n| u8::try_from(n).ok())).collect();
            Some(String::from_utf8_lossy(&bytes).into_owned())
        }
        Value::Number(n) => Some(n.to_string()),
        _ => None,
    }
}

/// One `journalctl -o json` line. `None` for a line that is not an object, has no usable timestamp, or
/// is debug (or has an unknown priority).
pub(crate) fn parse_journal_line(line: &str) -> Option<LogEntry> {
    let line = line.trim();
    if line.is_empty() {
        return None;
    }
    let value: Value = serde_json::from_str(line).ok()?;
    let object = value.as_object()?;

    // A missing PRIORITY is treated as informational rather than dropping the line.
    let priority = match field_text(object.get("PRIORITY")) {
        Some(p) => p.trim().parse::<i64>().ok()?,
        None => 6,
    };
    let sev = severity_for_priority(priority)?;

    let micros: i128 = field_text(object.get("__REALTIME_TIMESTAMP"))?.trim().parse().ok()?;
    let when = OffsetDateTime::from_unix_timestamp_nanos(micros.checked_mul(1000)?).ok()?;
    let t = when.format(&Rfc3339).ok()?;

    let kernel = field_text(object.get("_TRANSPORT")).as_deref() == Some("kernel");
    let src = if kernel {
        "kernel".to_string()
    } else {
        ["SYSLOG_IDENTIFIER", "_COMM", "_SYSTEMD_UNIT"]
            .iter()
            .filter_map(|key| field_text(object.get(*key)))
            .find(|s| !s.is_empty())
            .unwrap_or_else(|| "unknown".to_string())
    };
    let msg = field_text(object.get("MESSAGE")).unwrap_or_default();

    Some(LogEntry { t, sev, src: truncate_chars(&src, MAX_SRC_CHARS), msg: truncate_chars(&msg, MAX_MSG_CHARS) })
}

fn gzip_payload(entries: &[LogEntry], truncated_before: Option<String>) -> Result<Vec<u8>> {
    let json = serde_json::to_vec(&LogPayload { entries, truncated_before }).context("could not encode the log")?;
    let mut encoder = GzEncoder::new(Vec::new(), Compression::default());
    encoder.write_all(&json).context("could not compress the log")?;
    encoder.finish().context("could not compress the log")
}

/// Builds the gzip body from entries newest first. Keeps at most `max_entries` and at most `max_bytes` of
/// gzip, dropping the oldest; `truncatedBefore` is the oldest kept time when anything may have been
/// dropped (including by the journal line limit, `hit_limit`). Returns the body and entries kept.
pub(crate) fn encode_capped(newest_first: &[LogEntry], hit_limit: bool, max_entries: usize, max_bytes: usize) -> Result<(Vec<u8>, usize)> {
    let mut keep = newest_first.len().min(max_entries);
    loop {
        let kept = &newest_first[..keep];
        let dropped = keep < newest_first.len() || hit_limit;
        // Sent oldest first.
        let ordered: Vec<LogEntry> = kept.iter().rev().cloned().collect();
        let truncated = if dropped { ordered.first().map(|e| e.t.clone()) } else { None };
        let body = gzip_payload(&ordered, truncated)?;
        if body.len() <= max_bytes || keep == 0 {
            if body.len() > max_bytes {
                anyhow::bail!("the log does not fit the size limit even when empty");
            }
            return Ok((body, keep));
        }
        // Too big: drop the oldest tenth (at least one) and try again.
        keep -= (keep / 10).max(1);
    }
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

    use flate2::read::GzDecoder;
    use std::io::Read;

    fn entry(n: i64) -> LogEntry {
        let t = OffsetDateTime::from_unix_timestamp(1_700_000_000 + n).unwrap().format(&Rfc3339).unwrap();
        LogEntry { t, sev: "Info", src: "s".into(), msg: format!("message {n}") }
    }

    fn gunzip(body: &[u8]) -> Value {
        let mut text = String::new();
        GzDecoder::new(body).read_to_string(&mut text).unwrap();
        serde_json::from_str(&text).unwrap()
    }

    #[test]
    fn severity_mapping() {
        for p in 0..=2 {
            assert_eq!(severity_for_priority(p), Some("Critical"));
        }
        assert_eq!(severity_for_priority(3), Some("Error"));
        assert_eq!(severity_for_priority(4), Some("Warning"));
        assert_eq!(severity_for_priority(5), Some("Info"));
        assert_eq!(severity_for_priority(6), Some("Info"));
        assert_eq!(severity_for_priority(7), None);
        assert_eq!(severity_for_priority(-1), None);
        assert_eq!(severity_for_priority(9), None);
    }

    #[test]
    fn parses_string_message_and_microsecond_timestamp() {
        let line = r#"{"PRIORITY":"3","__REALTIME_TIMESTAMP":"1700000000123456","SYSLOG_IDENTIFIER":"sshd","_COMM":"x","MESSAGE":"boom"}"#;
        let e = parse_journal_line(line).unwrap();
        assert_eq!(e, LogEntry { t: "2023-11-14T22:13:20.123456Z".into(), sev: "Error", src: "sshd".into(), msg: "boom".into() });
    }

    #[test]
    fn parses_binary_array_message_lossy() {
        let line = r#"{"PRIORITY":"4","__REALTIME_TIMESTAMP":"1700000000000000","_COMM":"app","MESSAGE":[104,105,255]}"#;
        let e = parse_journal_line(line).unwrap();
        assert_eq!(e.msg, "hi\u{fffd}");
        assert_eq!(e.src, "app");
        assert_eq!(e.sev, "Warning");
    }

    #[test]
    fn null_or_missing_message_is_empty() {
        let a = parse_journal_line(r#"{"PRIORITY":"6","__REALTIME_TIMESTAMP":"1700000000000000","MESSAGE":null}"#).unwrap();
        assert_eq!(a.msg, "");
        let b = parse_journal_line(r#"{"PRIORITY":"6","__REALTIME_TIMESTAMP":"1700000000000000"}"#).unwrap();
        assert_eq!(b.msg, "");
        assert_eq!(b.src, "unknown");
    }

    #[test]
    fn source_fallbacks_and_kernel() {
        let unit = parse_journal_line(r#"{"PRIORITY":"3","__REALTIME_TIMESTAMP":"1700000000000000","_SYSTEMD_UNIT":"a.service","MESSAGE":"m"}"#).unwrap();
        assert_eq!(unit.src, "a.service");
        let kernel = parse_journal_line(
            r#"{"PRIORITY":"2","__REALTIME_TIMESTAMP":"1700000000000000","_TRANSPORT":"kernel","SYSLOG_IDENTIFIER":"kernel2","MESSAGE":"oops"}"#,
        )
        .unwrap();
        assert_eq!(kernel.src, "kernel");
        assert_eq!(kernel.sev, "Critical");
    }

    #[test]
    fn skips_debug_and_unparseable_lines() {
        assert!(parse_journal_line(r#"{"PRIORITY":"7","__REALTIME_TIMESTAMP":"1700000000000000","MESSAGE":"d"}"#).is_none());
        assert!(parse_journal_line("not json").is_none());
        assert!(parse_journal_line("").is_none());
        assert!(parse_journal_line("[1,2]").is_none());
        assert!(parse_journal_line(r#"{"PRIORITY":"3","MESSAGE":"no time"}"#).is_none());
        assert!(parse_journal_line(r#"{"PRIORITY":"3","__REALTIME_TIMESTAMP":"abc","MESSAGE":"x"}"#).is_none());
    }

    #[test]
    fn truncates_source_and_message() {
        let line = format!(
            r#"{{"PRIORITY":"3","__REALTIME_TIMESTAMP":"1700000000000000","SYSLOG_IDENTIFIER":"{}","MESSAGE":"{}"}}"#,
            "é".repeat(300),
            "m".repeat(5000)
        );
        let e = parse_journal_line(&line).unwrap();
        assert_eq!(e.src.chars().count(), 120);
        assert_eq!(e.msg.chars().count(), 2000);
        assert_eq!(truncate_chars("short", 10), "short");
    }

    #[test]
    fn journal_arguments_per_detail() {
        let w = journal_args(100, false);
        assert_eq!(&w[..2], ["--since", "@100"]);
        assert!(w.windows(2).any(|p| p == ["-p", "warning"]));
        assert!(w.contains(&"-r".to_string()) && w.contains(&"json".to_string()) && w.contains(&"25000".to_string()));
        assert!(journal_args(100, true).windows(2).any(|p| p == ["-p", "info"]));
    }

    #[test]
    fn request_and_result_json_shapes() {
        let r: Vec<LogCollectionRequest> = serde_json::from_str(r#"[{"id":"abc","periodDays":30,"detail":"Everything"}]"#).unwrap();
        assert_eq!(r[0], LogCollectionRequest { id: "abc".into(), period_days: 30, detail: "Everything".into() });
        assert!(r[0].everything());
        let none: Vec<LogCollectionRequest> = serde_json::from_str("[]").unwrap();
        assert!(none.is_empty());

        let json = serde_json::to_value(LogPayload { entries: &[entry(0)], truncated_before: None }).unwrap();
        assert_eq!(json["entries"][0]["sev"], "Info");
        assert!(json["entries"][0].get("t").is_some() && json["entries"][0].get("src").is_some() && json["entries"][0].get("msg").is_some());
        assert!(json["truncatedBefore"].is_null());

        let f = serde_json::to_value(FailureRequest { serial_number: "S", collection_id: "id", reason: "r".into() }).unwrap();
        assert_eq!(f, serde_json::json!({"serialNumber":"S","collectionId":"id","reason":"r"}));
    }

    #[test]
    fn gzip_round_trip_and_oldest_first_order() {
        let newest_first: Vec<LogEntry> = (0..5).rev().map(entry).collect();
        let (body, kept) = encode_capped(&newest_first, false, 100, 1 << 20).unwrap();
        assert_eq!(kept, 5);
        let v = gunzip(&body);
        assert!(v["truncatedBefore"].is_null());
        assert_eq!(v["entries"].as_array().unwrap().len(), 5);
        assert_eq!(v["entries"][0]["msg"], "message 0");
        assert_eq!(v["entries"][4]["msg"], "message 4");
    }

    #[test]
    fn entry_cap_keeps_newest_and_sets_truncated_before() {
        let newest_first: Vec<LogEntry> = (0..10).rev().map(entry).collect();
        let (body, kept) = encode_capped(&newest_first, false, 4, 1 << 20).unwrap();
        assert_eq!(kept, 4);
        let v = gunzip(&body);
        assert_eq!(v["entries"][0]["msg"], "message 6");
        assert_eq!(v["entries"][3]["msg"], "message 9");
        assert_eq!(v["truncatedBefore"], v["entries"][0]["t"]);
    }

    #[test]
    fn journal_line_limit_marks_truncation_even_when_all_kept() {
        let newest_first: Vec<LogEntry> = (0..3).rev().map(entry).collect();
        let v = gunzip(&encode_capped(&newest_first, true, 100, 1 << 20).unwrap().0);
        assert_eq!(v["truncatedBefore"], v["entries"][0]["t"]);
    }

    #[test]
    fn size_cap_drops_oldest_until_it_fits() {
        // Incompressible-ish messages so the gzip size tracks the entry count.
        let newest_first: Vec<LogEntry> = (0..400)
            .rev()
            .map(|n| {
                let mut e = entry(n);
                let mut x: u64 = 0x9E37_79B9_7F4A_7C15 ^ (n as u64 + 1);
                e.msg = (0..200)
                    .map(|_| {
                        x ^= x << 13;
                        x ^= x >> 7;
                        x ^= x << 17;
                        (b'a' + (x % 26) as u8) as char
                    })
                    .collect();
                e
            })
            .collect();
        let full = encode_capped(&newest_first, false, 20_000, usize::MAX).unwrap().0.len();
        let limit = full / 2;
        let (body, kept) = encode_capped(&newest_first, false, 20_000, limit).unwrap();
        assert!(body.len() <= limit);
        assert!(kept < 400 && kept > 0);
        let v = gunzip(&body);
        assert_eq!(v["entries"].as_array().unwrap().len(), kept);
        assert_eq!(v["entries"][kept - 1]["msg"], newest_first[0].msg);
        assert_eq!(v["truncatedBefore"], v["entries"][0]["t"]);
    }

    #[test]
    fn empty_log_is_a_valid_empty_result() {
        let (body, kept) = encode_capped(&[], false, 100, 1 << 20).unwrap();
        assert_eq!(kept, 0);
        let v = gunzip(&body);
        assert_eq!(v["entries"].as_array().unwrap().len(), 0);
        assert!(v["truncatedBefore"].is_null());
    }
}
