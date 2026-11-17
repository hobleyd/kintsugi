//! Collecting this Mac's log when an administrator asked for it from the admin UI, and sending it to the
//! server for the web UI to show.
//!
//! **Flow.** Every check-in asks `GET /api/log-collection-requests` (one cheap call; the server consumes
//! a request as it hands it over). With nothing pending that is all. With a request, the unified log is
//! read with `/usr/bin/log show --style ndjson --start/--end` **backwards in windows** (newest hour first,
//! see [`read_unified_log`]), the kernel panic reports in
//! `/Library/Logs/DiagnosticReports` newer than that are added, and the result is sent gzipped to
//! `/api/log-collection-results`. If the log cannot be read, `/api/log-collection-failures` gets one
//! plain sentence saying why.
//!
//! **Best-effort.** Nothing here fails a check-in: every error is logged and swallowed. Reading is held
//! to a hard budget of [`READ_BUDGET`]; after it the `log` process is killed and what was read so far is
//! sent with its oldest part marked missing (a failure is reported only when not even the newest hour
//! could be read).
//! A request that was claimed and then could not be answered is not retried, as the server already
//! consumed it; the administrator asks again.
//!
//! **Strictly read-only.** The only program run is `/usr/bin/log show`, and the only files read are
//! panic reports (regular files, never followed through a link, never waited on).
//!
//! **Severity.** `fault` is Critical, `error` is Error, `default` and `info` are Info. The unified log
//! has no warning level, so "Warning" is never produced. `debug` is never read. "WarningsAndErrors"
//! therefore means faults and errors only.
//!
//! **Caps.** At most [`MAX_ENTRIES`] entries (the newest) and a gzip body of at most [`MAX_GZIP_BYTES`].
//! When either drops entries, the oldest go and `truncatedBefore` says where the kept ones start.
//!
//! The persistent log keeps a limited history (days, not weeks, on most Macs), so a long period can
//! return less than was asked for. The field names of `log show --style ndjson` come from Apple's
//! format, which is not documented; a line that does not match is skipped, never guessed at.

use std::collections::VecDeque;
use std::io::{BufRead, BufReader, Write};
use std::path::Path;
use std::process::{Command, Stdio};
use std::time::{Duration, Instant};

use anyhow::{Context, Result};
use flate2::write::GzEncoder;
use flate2::Compression;
use serde::{Deserialize, Serialize};

use crate::config::Config;
use crate::health::{is_panic_report_name, line_epoch, rfc3339};
use crate::logging;

const LOG_BINARY: &str = "/usr/bin/log";
const PANIC_REPORT_DIR: &str = "/Library/Logs/DiagnosticReports";

/// How long reading the log may take, from starting `log show` to its last line.
const READ_BUDGET: Duration = Duration::from_secs(5 * 60);
const TIMEOUT_REASON: &str = "Reading the log took longer than 5 minutes.";
/// The results upload only. The daemon client's own 15 s limit is for small calls.
const UPLOAD_TIMEOUT: Duration = Duration::from_secs(120);

pub(crate) const MAX_ENTRIES: usize = 20_000;
pub(crate) const MAX_GZIP_BYTES: usize = 5 * 1024 * 1024;
const MAX_SRC_CHARS: usize = 120;
const MAX_MSG_CHARS: usize = 2000;
const MAX_REASON_CHARS: usize = 500;
const MAX_PERIOD_DAYS: i64 = 365;
/// How much of a panic report's text goes into its entry.
const PANIC_EXCERPT_CHARS: usize = 1000;

#[derive(Debug, Clone, Copy, PartialEq, Eq, Deserialize)]
pub(crate) enum Detail {
    WarningsAndErrors,
    Everything,
}

#[derive(Debug, Deserialize, PartialEq, Eq)]
#[serde(rename_all = "camelCase")]
pub(crate) struct LogCollectionRequest {
    pub id: String,
    pub period_days: i64,
    pub detail: Detail,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize)]
pub(crate) enum Severity {
    Critical,
    Error,
    #[allow(dead_code)] // the wire protocol has it; the unified log has no such level
    Warning,
    Info,
}

/// One log line, with its time as epoch seconds so entries can be ordered and cut.
#[derive(Debug, Clone, PartialEq, Eq)]
pub(crate) struct Entry {
    pub epoch: i64,
    pub sev: Severity,
    pub src: String,
    pub msg: String,
}

#[derive(Serialize)]
struct WireEntry<'a> {
    t: String,
    sev: Severity,
    src: &'a str,
    msg: &'a str,
}

#[derive(Serialize)]
#[serde(rename_all = "camelCase")]
struct WirePayload<'a> {
    entries: Vec<WireEntry<'a>>,
    truncated_before: Option<String>,
}

#[derive(Serialize)]
#[serde(rename_all = "camelCase")]
struct FailureBody<'a> {
    serial_number: &'a str,
    collection_id: &'a str,
    reason: String,
}

/// Answers a pending log request, if there is one. Never fails and never takes long when there is none.
pub fn collect_and_upload_if_requested(client: &reqwest::blocking::Client, config: &Config, serial_number: &str) {
    if let Err(err) = try_collect(client, config, serial_number) {
        logging::warn(&format!("log collection: {err:#}"));
    }
}

fn try_collect(client: &reqwest::blocking::Client, config: &Config, serial_number: &str) -> Result<()> {
    let response = client
        .get(config.log_collection_requests_url())
        .query(&[("serialNumber", serial_number)])
        .send()
        .context("could not ask for a log collection request")?;
    if !response.status().is_success() {
        anyhow::bail!("the server rejected the request check (HTTP {})", response.status());
    }
    let requests: Vec<LogCollectionRequest> = response.json().context("could not parse the request check")?;
    let Some(request) = requests.into_iter().next() else {
        return Ok(());
    };
    logging::info(&format!("log collection {} requested: {} day(s), {:?}", request.id, request.period_days, request.detail));

    match read_log(&request) {
        Ok((entries, dropped)) => {
            let (body, kept) = build_body(entries, dropped, MAX_ENTRIES, MAX_GZIP_BYTES)?;
            // The daemon's client times out at 15 s, which a few MB over a slow uplink does not meet: the
            // upload gets the 120 s Linux and Windows give theirs, or the reading is thrown away.
            let sent = client
                .post(config.log_collection_results_url())
                .query(&[("serialNumber", serial_number), ("collectionId", request.id.as_str())])
                .header("Content-Type", "application/gzip")
                .timeout(UPLOAD_TIMEOUT)
                .body(body)
                .send();
            // A log that was read but did not arrive is reported, so the page says so now instead of waiting
            // out its 30-minute general error. If the network is what failed, this report fails too.
            let rejected = match &sent {
                Ok(response) if response.status().is_success() => None,
                Ok(response) => Some(Some(response.status().as_u16())),
                Err(err) => {
                    logging::warn(&format!("log collection {}: the log could not be sent: {err:#}", request.id));
                    Some(None)
                }
            };
            if let Some(status) = rejected {
                return report_failure(client, config, serial_number, &request.id, &undelivered_reason(status));
            }
            logging::info(&format!("log collection {} sent: {kept} entries", request.id));
            Ok(())
        }
        Err(reason) => report_failure(client, config, serial_number, &request.id, &reason),
    }
}

fn report_failure(client: &reqwest::blocking::Client, config: &Config, serial_number: &str, id: &str, reason: &str) -> Result<()> {
    logging::warn(&format!("log collection {id} failed: {reason}"));
    let body = FailureBody { serial_number, collection_id: id, reason: clip(reason, MAX_REASON_CHARS) };
    let response = client.post(config.log_collection_failures_url()).json(&body).send().context("the failure could not be sent")?;
    if !response.status().is_success() {
        anyhow::bail!("the server rejected the failure report (HTTP {})", response.status());
    }
    Ok(())
}

/// What the administrator is told when the log was read but did not reach the server. A plain sentence:
/// the transport detail (a URL, a socket error) goes to the agent's own log, not to the page.
pub(crate) fn undelivered_reason(http_status: Option<u16>) -> String {
    match http_status {
        Some(code) => format!("The device read its log but the server rejected it (HTTP {code})."),
        None => "The device read its log but could not send it to the server.".to_string(),
    }
}

/// The entries (oldest first) and whether any were already dropped on the way for being too many. The
/// error is the plain sentence for the administrator.
fn read_log(request: &LogCollectionRequest) -> std::result::Result<(Vec<Entry>, bool), String> {
    let days = request.period_days.clamp(1, MAX_PERIOD_DAYS);
    let (mut entries, dropped) = read_unified_log(days, request.detail, READ_BUDGET)?;
    entries.extend(panic_report_entries(days, now_epoch()));
    Ok((entries, dropped))
}

/// What one window can come to: it ran out of the time left, or `log show` itself failed.
#[derive(Debug, PartialEq, Eq)]
pub(crate) enum WindowError {
    TimedOut,
    Failed(String),
}

/// The first window is an hour: "Everything" for an hour is about 700,000 lines and takes seconds, and
/// 20,000 entries are usually found in the first one or two.
const FIRST_WINDOW_SECS: i64 = 3600;
/// A quiet stretch (fewer entries than this) doubles the next window, up to a day, so a month on a quiet
/// Mac is not 720 separate `log show` runs.
const QUIET_WINDOW_ENTRIES: usize = 1_000;
const MAX_WINDOW_SECS: i64 = 24 * 3600;

/// How long the window before this one is: it doubles after a quiet window and goes back to an hour after
/// a busy one.
pub(crate) fn next_window_secs(current: i64, entries_in_window: usize) -> i64 {
    if entries_in_window < QUIET_WINDOW_ENTRIES {
        (current * 2).min(MAX_WINDOW_SECS)
    } else {
        FIRST_WINDOW_SECS
    }
}

/// `YYYY-MM-DD HH:MM:SS+0000`, the form `log show --start/--end` documents, in UTC so it does not depend on
/// this Mac's time zone.
pub(crate) fn log_time(epoch: i64) -> String {
    let days = epoch.div_euclid(86_400);
    let secs = epoch.rem_euclid(86_400);
    // Civil date from days since 1970-01-01 (Howard Hinnant's algorithm).
    let z = days + 719_468;
    let era = z.div_euclid(146_097);
    let doe = z.rem_euclid(146_097);
    let yoe = (doe - doe / 1_460 + doe / 36_524 - doe / 146_096) / 365;
    let doy = doe - (365 * yoe + yoe / 4 - yoe / 100);
    let mp = (5 * doy + 2) / 153;
    let day = doy - (153 * mp + 2) / 5 + 1;
    let month = if mp < 10 { mp + 3 } else { mp - 9 };
    let year = yoe + era * 400 + i64::from(month <= 2);
    format!("{year:04}-{month:02}-{day:02} {:02}:{:02}:{:02}+0000", secs / 3600, secs % 3600 / 60, secs % 60)
}

/// The newest [`MAX_ENTRIES`] entries of the last `days`, read **backwards** in windows, with whether any
/// were left out. `log show --last` streams oldest first, and the newest-20,000 cut can only be made when
/// the stream ends, so a long period or "Everything" never finished inside the budget and sent nothing.
/// Reading the newest hour first, then the hour before it, can stop as soon as it has enough, at the start
/// of the period, or when the time is up. If time runs out after at least one window, what was read is
/// sent with the oldest part marked as missing; only a first window that cannot finish is a failure.
fn read_unified_log(days: i64, detail: Detail, budget: Duration) -> std::result::Result<(Vec<Entry>, bool), String> {
    let deadline = Instant::now() + budget;
    let now = now_epoch();
    read_backwards(now, days, |start, end| read_window(start, end, detail, deadline))
}

/// The window loop, with the reading of one window passed in so it can be tested without `log`.
pub(crate) fn read_backwards<F>(now: i64, days: i64, mut read: F) -> std::result::Result<(Vec<Entry>, bool), String>
where
    F: FnMut(i64, i64) -> std::result::Result<(Vec<Entry>, bool), WindowError>,
{
    let period_start = now - days * 86_400;
    // A little past now, so an entry logged while this runs is not left on the wrong side of the first edge.
    let mut end = now + 60;
    let mut length = FIRST_WINDOW_SECS;
    let mut windows: Vec<Vec<Entry>> = Vec::new();
    let mut total = 0usize;
    let mut dropped = false;

    while end > period_start {
        let start = (end - length).max(period_start);
        match read(start, end) {
            Ok((entries, overflowed)) => {
                let count = entries.len();
                total += count;
                dropped |= overflowed;
                windows.push(entries);
                if total >= MAX_ENTRIES {
                    // Anything older would only be cut, so stop; older time left unread is a gap.
                    dropped |= start > period_start;
                    break;
                }
                length = next_window_secs(length, count);
                end = start;
            }
            Err(WindowError::TimedOut) if windows.is_empty() => return Err(TIMEOUT_REASON.to_string()),
            Err(WindowError::Failed(reason)) if windows.is_empty() => return Err(reason),
            Err(_) => {
                // The newest part is in hand; the older part could not be read in time.
                dropped = true;
                break;
            }
        }
    }

    windows.reverse(); // each window is oldest first, and the oldest window goes first
    let mut entries: Vec<Entry> = windows.into_iter().flatten().collect();
    if entries.len() > MAX_ENTRIES {
        entries.drain(..entries.len() - MAX_ENTRIES);
        dropped = true;
    }
    Ok((entries, dropped))
}

/// One window, `[start, end)` in epoch seconds: the entries oldest first and whether the window itself had
/// more than [`MAX_ENTRIES`] (only the newest are kept as the lines arrive, so an hour of everything does
/// not fill this Mac's memory). The `log` process is killed at `deadline`.
fn read_window(start: i64, end: i64, detail: Detail, deadline: Instant) -> std::result::Result<(Vec<Entry>, bool), WindowError> {
    let mut command = Command::new(LOG_BINARY);
    command.args(["show", "--style", "ndjson", "--start", &log_time(start), "--end", &log_time(end)]);
    if detail == Detail::Everything {
        // Without --info, `log show` prints default-level messages only, so the predicate's "info" term
        // would match nothing.
        command.arg("--info");
    }
    let mut child = command
        .args(["--predicate", predicate(detail)])
        .env("LC_ALL", "C")
        .stdin(Stdio::null())
        .stdout(Stdio::piped())
        .stderr(Stdio::null())
        .spawn()
        .map_err(|e| WindowError::Failed(format!("The log could not be read: {e}.")))?;
    let stdout = child.stdout.take().ok_or_else(|| WindowError::Failed("The log could not be read.".to_string()))?;

    let reader = std::thread::spawn(move || {
        let mut ring: VecDeque<Entry> = VecDeque::new();
        let mut overflowed = false;
        for line in BufReader::new(stdout).split(b'\n') {
            let Ok(bytes) = line else { break };
            if let Some(entry) = parse_line(&String::from_utf8_lossy(&bytes), detail) {
                if entry.epoch < start || entry.epoch >= end {
                    continue;
                }
                if ring.len() >= MAX_ENTRIES {
                    ring.pop_front();
                    overflowed = true;
                }
                ring.push_back(entry);
            }
        }
        (ring.into_iter().collect::<Vec<_>>(), overflowed)
    });

    let status = loop {
        match child.try_wait() {
            Ok(Some(status)) => break status,
            Ok(None) if Instant::now() >= deadline => {
                let _ = child.kill();
                let _ = child.wait();
                let _ = reader.join();
                return Err(WindowError::TimedOut);
            }
            Ok(None) => std::thread::sleep(Duration::from_millis(200)),
            Err(e) => {
                let _ = child.kill();
                let _ = child.wait();
                let _ = reader.join();
                return Err(WindowError::Failed(format!("The log could not be read: {e}.")));
            }
        }
    };
    let result = reader.join().map_err(|_| WindowError::Failed("The log could not be read.".to_string()))?;
    if !status.success() {
        return Err(WindowError::Failed(match status.code() {
            Some(code) => format!("The log command failed (exit code {code})."),
            None => "The log command was stopped before it finished.".to_string(),
        }));
    }
    Ok(result)
}

/// The `log show` predicate for a detail level. Debug messages are never read.
pub(crate) fn predicate(detail: Detail) -> &'static str {
    match detail {
        Detail::WarningsAndErrors => "messageType == \"error\" OR messageType == \"fault\"",
        Detail::Everything => "messageType == \"error\" OR messageType == \"fault\" OR messageType == \"default\" OR messageType == \"info\"",
    }
}

/// `fault` is Critical, `error` Error, `default` and `info` Info; anything else (debug, a level Apple
/// adds later) is not read. Case does not matter: `log show` writes them capitalised.
pub(crate) fn severity_of(message_type: &str) -> Option<Severity> {
    match message_type.to_ascii_lowercase().as_str() {
        "fault" => Some(Severity::Critical),
        "error" => Some(Severity::Error),
        "default" | "info" => Some(Severity::Info),
        _ => None,
    }
}

/// One line of `log show --style ndjson`. `None` for anything that is not a usable entry: the leading
/// "Filtering the log data using ..." line, a broken line, a missing time, message or level, or a level
/// the requested detail does not include.
pub(crate) fn parse_line(line: &str, detail: Detail) -> Option<Entry> {
    let line = line.trim();
    if !line.starts_with('{') {
        return None;
    }
    let value: serde_json::Value = serde_json::from_str(line).ok()?;
    let text = |key: &str| value.get(key).and_then(|v| v.as_str()).map(str::trim).filter(|s| !s.is_empty());

    let sev = severity_of(text("messageType")?)?;
    if detail == Detail::WarningsAndErrors && sev == Severity::Info {
        return None;
    }
    let epoch = line_epoch(text("timestamp")?)?;
    let msg = value.get("eventMessage").and_then(|v| v.as_str()).filter(|s| !s.trim().is_empty())?;

    let process = text("processImagePath").map(|p| p.rsplit('/').next().unwrap_or(p));
    let src = match (process, text("subsystem")) {
        (Some(p), Some(s)) => format!("{p} ({s})"),
        (Some(p), None) => p.to_string(),
        (None, Some(s)) => s.to_string(),
        (None, None) => "unknown".to_string(),
    };
    Some(Entry { epoch, sev, src: clip(&src, MAX_SRC_CHARS), msg: clip(msg, MAX_MSG_CHARS) })
}

/// Kernel panic reports newer than the period, one Critical entry each.
fn panic_report_entries(days: i64, now: i64) -> Vec<Entry> {
    let since = now - days * 86400;
    let Ok(dir) = std::fs::read_dir(PANIC_REPORT_DIR) else {
        return Vec::new();
    };
    dir.flatten()
        .filter_map(|entry| {
            let name = entry.file_name().to_string_lossy().into_owned();
            // A real file only, as in health.rs: `file_type` does not follow links.
            if !is_panic_report_name(&name) || !entry.file_type().is_ok_and(|t| t.is_file()) {
                return None;
            }
            let modified = entry.metadata().ok()?.modified().ok()?.duration_since(std::time::UNIX_EPOCH).ok()?.as_secs() as i64;
            if modified < since {
                return None;
            }
            let text = read_head(&entry.path()).unwrap_or_default();
            Some(panic_entry(modified, &name, &text))
        })
        .collect()
}

pub(crate) fn panic_entry(epoch: i64, file_name: &str, text: &str) -> Entry {
    let excerpt = clip(text.trim(), PANIC_EXCERPT_CHARS);
    Entry { epoch, sev: Severity::Critical, src: "panic report".to_string(), msg: clip(&format!("{file_name}\n{excerpt}"), MAX_MSG_CHARS) }
}

/// The start of a text file, never following a link and never waiting on a pipe. Text only: bytes that
/// are not UTF-8 are replaced.
fn read_head(path: &Path) -> Option<String> {
    use std::io::Read;
    use std::os::unix::fs::OpenOptionsExt;
    let mut buf = Vec::new();
    std::fs::OpenOptions::new()
        .read(true)
        .custom_flags(libc::O_NONBLOCK | libc::O_NOFOLLOW)
        .open(path)
        .ok()?
        .take(8 * 1024)
        .read_to_end(&mut buf)
        .ok()?;
    Some(String::from_utf8_lossy(&buf).into_owned())
}

/// Orders the entries, applies both caps and gzips them: the body to send and how many entries it holds.
/// `already_dropped` says older entries were discarded before this point.
pub(crate) fn build_body(mut entries: Vec<Entry>, already_dropped: bool, max_entries: usize, max_bytes: usize) -> Result<(Vec<u8>, usize)> {
    entries.sort_by_key(|e| e.epoch);
    let mut dropped = already_dropped;
    if entries.len() > max_entries {
        entries.drain(..entries.len() - max_entries);
        dropped = true;
    }
    loop {
        let body = encode(&entries, dropped)?;
        if body.len() <= max_bytes || entries.len() <= 1 {
            return Ok((body, entries.len()));
        }
        // Over the size cap: shed the oldest tenth (at least one) and try again.
        let cut = (entries.len() / 10).max(1);
        entries.drain(..cut);
        dropped = true;
    }
}

fn encode(entries: &[Entry], dropped: bool) -> Result<Vec<u8>> {
    let payload = WirePayload {
        entries: entries.iter().map(|e| WireEntry { t: rfc3339(e.epoch), sev: e.sev, src: &e.src, msg: &e.msg }).collect(),
        truncated_before: if dropped { entries.first().map(|e| rfc3339(e.epoch)) } else { None },
    };
    let json = serde_json::to_vec(&payload).context("could not serialise the log")?;
    let mut gz = GzEncoder::new(Vec::new(), Compression::default());
    gz.write_all(&json).context("could not compress the log")?;
    gz.finish().context("could not compress the log")
}

/// At most `max` characters, by characters rather than bytes so a multi-byte character is never cut.
pub(crate) fn clip(text: &str, max: usize) -> String {
    text.chars().take(max).collect()
}

fn now_epoch() -> i64 {
    std::time::SystemTime::now().duration_since(std::time::UNIX_EPOCH).map(|d| d.as_secs() as i64).unwrap_or(0)
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

    fn entry(epoch: i64, msg: &str) -> Entry {
        Entry { epoch, sev: Severity::Error, src: "kernel".into(), msg: msg.into() }
    }

    // ---- reading backwards in windows (finding 1 of the review) ----

    #[test]
    fn log_time_is_utc_in_the_form_log_show_documents() {
        assert_eq!(log_time(0), "1970-01-01 00:00:00+0000");
        assert_eq!(log_time(1_000_000_000), "2001-09-09 01:46:40+0000");
        assert_eq!(log_time(1_709_164_800), "2024-02-29 00:00:00+0000"); // a leap day
        assert_eq!(log_time(1_794_216_251), "2026-11-09 09:24:11+0000");
        assert_eq!(log_time(1_798_761_599), "2026-12-31 23:59:59+0000");
        // And back through the parser the agent already trusts for the panic-report and entry times.
        assert_eq!(line_epoch(&log_time(1_794_216_251)), Some(1_794_216_251));
    }

    #[test]
    fn a_quiet_window_doubles_the_next_up_to_a_day_and_a_busy_one_resets_it() {
        assert_eq!(next_window_secs(3600, 0), 7200);
        assert_eq!(next_window_secs(7200, 999), 14_400);
        assert_eq!(next_window_secs(60 * 3600, 0), 24 * 3600);
        assert_eq!(next_window_secs(24 * 3600, 0), 24 * 3600);
        assert_eq!(next_window_secs(14_400, 1_000), 3600);
        assert_eq!(next_window_secs(3600, 700_000), 3600);
    }

    const NOW: i64 = 1_794_216_251;

    /// A fake `log`: `per_window` entries in every window it is asked for, recording the windows.
    fn fake(per_window: usize, calls: &std::cell::RefCell<Vec<(i64, i64)>>) -> impl FnMut(i64, i64) -> std::result::Result<(Vec<Entry>, bool), WindowError> + '_ {
        move |start, end| {
            calls.borrow_mut().push((start, end));
            Ok(((0..per_window as i64).map(|i| entry(start + i, "m")).collect(), false))
        }
    }

    #[test]
    fn it_stops_at_the_first_window_that_holds_enough_and_says_older_time_was_left() {
        let calls = std::cell::RefCell::new(Vec::new());

        let (entries, dropped) = read_backwards(NOW, 30, fake(MAX_ENTRIES, &calls)).unwrap();

        assert_eq!(calls.borrow().len(), 1, "an hour held 20,000 entries; nothing older can be in the newest 20,000");
        assert_eq!(entries.len(), MAX_ENTRIES);
        assert!(dropped, "29 days and 23 hours were never read");
    }

    #[test]
    fn a_quiet_mac_is_read_to_the_start_of_the_period_without_a_flood_of_windows() {
        let calls = std::cell::RefCell::new(Vec::new());

        let (entries, dropped) = read_backwards(NOW, 30, fake(3, &calls)).unwrap();

        let calls = calls.borrow();
        assert!(!dropped);
        assert!(calls.len() < 50, "{} windows for 30 quiet days", calls.len());
        assert_eq!(calls.last().unwrap().0, NOW - 30 * 86_400, "the oldest window starts exactly at the period's start");
        // Contiguous, newest first: each window ends where the one before it began.
        assert!(calls.windows(2).all(|w| w[1].1 == w[0].0));
        assert_eq!(entries.len(), 3 * calls.len());
        assert!(entries.windows(2).all(|w| w[0].epoch <= w[1].epoch), "oldest first overall");
    }

    #[test]
    fn running_out_of_time_after_the_first_window_sends_what_was_read_instead_of_nothing() {
        let mut n = 0;
        let result = read_backwards(NOW, 30, |start, _| {
            n += 1;
            if n == 3 {
                return Err(WindowError::TimedOut);
            }
            Ok((vec![entry(start + 5, "m")], false))
        });

        let (entries, dropped) = result.unwrap();
        assert_eq!(entries.len(), 2);
        assert!(dropped, "the older part is marked missing");
    }

    #[test]
    fn not_even_the_newest_hour_finishing_is_a_failure_with_the_timeout_sentence() {
        assert_eq!(read_backwards(NOW, 7, |_, _| Err(WindowError::TimedOut)).unwrap_err(), TIMEOUT_REASON);
        assert_eq!(
            read_backwards(NOW, 7, |_, _| Err(WindowError::Failed("The log command failed (exit code 1).".to_string()))).unwrap_err(),
            "The log command failed (exit code 1)."
        );
    }

    #[test]
    fn a_window_that_overflowed_is_remembered_even_when_the_total_is_trimmed() {
        let mut n = 0;
        let (entries, dropped) = read_backwards(NOW, 1, |start, _| {
            n += 1;
            Ok(((0..10i64).map(|i| entry(start + i, "m")).collect(), n == 1))
        })
        .unwrap();

        assert!(dropped);
        assert!(entries.len() <= MAX_ENTRIES);
    }

    #[test]
    fn everything_asks_for_info_messages_and_errors_only_does_not() {
        // The wiring lives in read_window; this pins the predicate half it depends on.
        assert!(predicate(Detail::Everything).contains("messageType == \"info\""));
        assert!(!predicate(Detail::WarningsAndErrors).contains("info"));
    }

    fn decode(body: &[u8]) -> serde_json::Value {
        let mut text = String::new();
        GzDecoder::new(body).read_to_string(&mut text).unwrap();
        serde_json::from_str(&text).unwrap()
    }

    const LINE: &str = r#"{"timestamp":"2026-11-09 09:24:11.123456+1100","messageType":"Error","processImagePath":"/usr/libexec/trustd","subsystem":"com.apple.trust","eventMessage":"boom"}"#;

    #[test]
    fn parses_a_line() {
        let e = parse_line(LINE, Detail::WarningsAndErrors).unwrap();
        assert_eq!(rfc3339(e.epoch), "2026-11-08T22:24:11Z");
        assert_eq!(e.sev, Severity::Error);
        assert_eq!(e.src, "trustd (com.apple.trust)");
        assert_eq!(e.msg, "boom");
    }

    #[test]
    fn timestamp_offsets_either_way() {
        let east = LINE.to_string();
        let west = LINE.replace("09:24:11.123456+1100", "09:24:11.000000-0500");
        assert_eq!(rfc3339(parse_line(&east, Detail::Everything).unwrap().epoch), "2026-11-08T22:24:11Z");
        assert_eq!(rfc3339(parse_line(&west, Detail::Everything).unwrap().epoch), "2026-11-09T14:24:11Z");
    }

    #[test]
    fn skips_the_leading_line_and_garbage() {
        assert!(parse_line("Filtering the log data using \"messageType == error\"", Detail::Everything).is_none());
        assert!(parse_line("", Detail::Everything).is_none());
        assert!(parse_line("{ not json", Detail::Everything).is_none());
        assert!(parse_line("[1,2]", Detail::Everything).is_none());
    }

    #[test]
    fn skips_lines_missing_a_field() {
        for key in ["timestamp", "messageType", "eventMessage"] {
            let mut v: serde_json::Value = serde_json::from_str(LINE).unwrap();
            v.as_object_mut().unwrap().remove(key);
            assert!(parse_line(&v.to_string(), Detail::Everything).is_none(), "{key}");
        }
        assert!(parse_line(&LINE.replace("09:24:11.123456+1100", "garbage"), Detail::Everything).is_none());
    }

    #[test]
    fn source_is_process_and_subsystem_when_present() {
        let no_sub = r#"{"timestamp":"2026-11-09 09:24:11.1+0000","messageType":"Fault","processImagePath":"/kernel","eventMessage":"m"}"#;
        let e = parse_line(no_sub, Detail::WarningsAndErrors).unwrap();
        assert_eq!((e.src.as_str(), e.sev), ("kernel", Severity::Critical));
        let only_sub = r#"{"timestamp":"2026-11-09 09:24:11.1+0000","messageType":"Fault","subsystem":"com.x","eventMessage":"m"}"#;
        assert_eq!(parse_line(only_sub, Detail::Everything).unwrap().src, "com.x");
        let none = r#"{"timestamp":"2026-11-09 09:24:11.1+0000","messageType":"Fault","eventMessage":"m"}"#;
        assert_eq!(parse_line(none, Detail::Everything).unwrap().src, "unknown");
    }

    #[test]
    fn severity_mapping() {
        assert_eq!(severity_of("Fault"), Some(Severity::Critical));
        assert_eq!(severity_of("error"), Some(Severity::Error));
        assert_eq!(severity_of("Default"), Some(Severity::Info));
        assert_eq!(severity_of("Info"), Some(Severity::Info));
        assert_eq!(severity_of("Debug"), None);
        assert_eq!(severity_of(""), None);
    }

    #[test]
    fn detail_decides_whether_info_is_kept() {
        let info = LINE.replace("Error", "Info");
        assert!(parse_line(&info, Detail::WarningsAndErrors).is_none());
        assert_eq!(parse_line(&info, Detail::Everything).unwrap().sev, Severity::Info);
        assert!(parse_line(&LINE.replace("Error", "Debug"), Detail::Everything).is_none());
    }

    #[test]
    fn predicates() {
        assert_eq!(predicate(Detail::WarningsAndErrors), "messageType == \"error\" OR messageType == \"fault\"");
        let all = predicate(Detail::Everything);
        for t in ["error", "fault", "default", "info"] {
            assert!(all.contains(&format!("messageType == \"{t}\"")));
        }
        assert!(!all.contains("debug"));
    }

    #[test]
    fn limits_are_in_characters() {
        let long = format!(r#"{{"timestamp":"2026-11-09 09:24:11.1+0000","messageType":"Error","processImagePath":"/{}","eventMessage":"{}"}}"#, "é".repeat(300), "ü".repeat(5000));
        let e = parse_line(&long, Detail::Everything).unwrap();
        assert_eq!(e.src.chars().count(), 120);
        assert_eq!(e.msg.chars().count(), 2000);
    }

    #[test]
    fn request_shape() {
        let r: Vec<LogCollectionRequest> = serde_json::from_str(r#"[{"id":"abc","periodDays":30,"detail":"WarningsAndErrors"}]"#).unwrap();
        assert_eq!(r, vec![LogCollectionRequest { id: "abc".into(), period_days: 30, detail: Detail::WarningsAndErrors }]);
        let r: Vec<LogCollectionRequest> = serde_json::from_str(r#"[{"id":"x","periodDays":7,"detail":"Everything"}]"#).unwrap();
        assert_eq!(r[0].detail, Detail::Everything);
        assert!(serde_json::from_str::<Vec<LogCollectionRequest>>("[]").unwrap().is_empty());
    }

    #[test]
    fn failure_shape() {
        let json = serde_json::to_value(FailureBody { serial_number: "S", collection_id: "id", reason: "r".into() }).unwrap();
        assert_eq!(json, serde_json::json!({"serialNumber":"S","collectionId":"id","reason":"r"}));
    }

    #[test]
    fn result_shape_and_round_trip() {
        let (body, kept) = build_body(vec![entry(1_794_000_000, "b"), entry(1_793_000_000, "a")], false, MAX_ENTRIES, MAX_GZIP_BYTES).unwrap();
        assert_eq!(kept, 2);
        assert_eq!(&body[..2], &[0x1f, 0x8b]);
        let v = decode(&body);
        assert!(v["truncatedBefore"].is_null());
        assert_eq!(v["entries"][0], serde_json::json!({"t":"2026-10-26T07:33:20Z","sev":"Error","src":"kernel","msg":"a"}));
        assert_eq!(v["entries"][1]["msg"], "b");
    }

    #[test]
    fn entry_cap_keeps_the_newest() {
        let entries: Vec<Entry> = (0..10).map(|i| entry(1_790_000_000 + i, &format!("m{i}"))).collect();
        let (body, kept) = build_body(entries, false, 4, MAX_GZIP_BYTES).unwrap();
        assert_eq!(kept, 4);
        let v = decode(&body);
        assert_eq!(v["entries"][0]["msg"], "m6");
        assert_eq!(v["truncatedBefore"], rfc3339(1_790_000_006));
    }

    #[test]
    fn already_dropped_sets_truncated_before() {
        let (body, _) = build_body(vec![entry(1_790_000_000, "a")], true, 10, MAX_GZIP_BYTES).unwrap();
        assert_eq!(decode(&body)["truncatedBefore"], rfc3339(1_790_000_000));
    }

    #[test]
    fn size_cap_drops_the_oldest() {
        // Incompressible messages, so the gzip really is large.
        let mut x: u64 = 12345;
        let entries: Vec<Entry> = (0..400)
            .map(|i| {
                let msg: String = (0..200)
                    .map(|_| {
                        x = x.wrapping_mul(6364136223846793005).wrapping_add(1442695040888963407);
                        (b'a' + ((x >> 33) % 26) as u8) as char
                    })
                    .collect();
                entry(1_790_000_000 + i, &msg)
            })
            .collect();
        let (body, kept) = build_body(entries, false, MAX_ENTRIES, 20_000).unwrap();
        assert!(body.len() <= 20_000 && kept < 400 && kept > 0);
        let v = decode(&body);
        assert_eq!(v["entries"].as_array().unwrap().len(), kept);
        let first = v["entries"][0]["t"].as_str().unwrap().to_string();
        assert_eq!(v["truncatedBefore"], first);
        assert_eq!(v["entries"][kept - 1]["t"], rfc3339(1_790_000_399));
    }

    #[test]
    fn empty_is_valid() {
        let (body, kept) = build_body(Vec::new(), false, 10, 100_000).unwrap();
        assert_eq!(kept, 0);
        let v = decode(&body);
        assert_eq!(v["entries"].as_array().unwrap().len(), 0);
        assert!(v["truncatedBefore"].is_null());
    }

    #[test]
    fn panic_entry_shape() {
        let e = panic_entry(1_790_000_000, "Kernel_x.panic", &"p".repeat(5000));
        assert_eq!((e.sev, e.src.as_str()), (Severity::Critical, "panic report"));
        assert!(e.msg.starts_with("Kernel_x.panic\n"));
        assert_eq!(e.msg.chars().count(), "Kernel_x.panic\n".len() + 1000);
    }
}

/// Runs the real `log show` on the machine the tests run on. Only on macOS, and in practice only on the CI's
/// macOS runner: it is how this agent's reader meets Apple's real output without anyone needing a Mac. A
/// fresh CI machine has little to say about faults, so this reads everything (`default` and `info` as
/// well) for the last day, which it always has, and checks the format, not the failures.
#[cfg(all(test, target_os = "macos"))]
mod real_macos {
    use super::*;

    #[test]
    fn the_real_unified_log_is_read_and_every_entry_is_well_formed() {
        let (entries, _) = read_unified_log(1, Detail::Everything, Duration::from_secs(150))
            .expect("the real `log show` should run and its output should be readable");

        assert!(!entries.is_empty(), "a Mac that has been running for minutes has logged something: the reader found nothing, so the ndjson field names probably differ from the real ones");
        for e in &entries {
            let now = now_epoch();
            assert!(e.epoch > now - 2 * 86_400 && e.epoch <= now + 600, "the time should be within the last day: {}", e.epoch);
            assert!(!e.src.is_empty() && e.src.chars().count() <= MAX_SRC_CHARS + 1, "source: {:?}", e.src);
            assert!(!e.msg.is_empty() && e.msg.chars().count() <= MAX_MSG_CHARS + 1, "message too long or empty");
        }

        let (body, kept) = build_body(entries.clone(), false, MAX_ENTRIES, MAX_GZIP_BYTES).expect("the real entries should fit the caps");
        assert!(kept > 0 && body.len() <= MAX_GZIP_BYTES);
    }

    /// The default detail must not fail either, and it can legitimately find nothing on a quiet machine.
    #[test]
    fn warnings_and_errors_runs_on_the_real_log() {
        let (entries, _) = read_unified_log(1, Detail::WarningsAndErrors, Duration::from_secs(150)).expect("the real `log show` should run");

        assert!(entries.iter().all(|e| e.sev != Severity::Info), "warnings and errors must not include informational lines");
    }
    /// The window arguments (`--start`/`--end` in the documented form, plus `--info`) must be accepted by the
    /// real `log show`; a refused date format or flag would fail every collection on every Mac.
    #[test]
    fn the_real_log_show_accepts_a_one_hour_window() {
        let now = now_epoch();

        let (entries, _) = read_window(now - 3600, now + 60, Detail::Everything, Instant::now() + Duration::from_secs(150))
            .unwrap_or_else(|e| panic!("log show refused the window: {e:?}"));

        assert!(entries.iter().all(|e| e.epoch >= now - 3600 && e.epoch < now + 60));
    }

    /// The case the review measured: a long period of "Everything" must finish inside the budget with the
    /// newest entries, and say older ones were left out.
    #[test]
    fn a_week_of_everything_finishes_with_the_newest_twenty_thousand() {
        let started = Instant::now();

        let (entries, dropped) = read_unified_log(7, Detail::Everything, Duration::from_secs(5 * 60)).expect("a week of everything should finish");

        assert!(started.elapsed() < Duration::from_secs(5 * 60));
        assert!(!entries.is_empty() && entries.len() <= MAX_ENTRIES);
        assert!(dropped || entries.len() < MAX_ENTRIES);
    }

    #[test]
    fn info_messages_come_back_for_everything() {
        let (entries, _) = read_unified_log(1, Detail::Everything, Duration::from_secs(150)).expect("the real `log show` should run");

        assert!(entries.iter().any(|e| e.sev == Severity::Info), "--info should bring informational messages back for \"Everything\"");
    }
}
