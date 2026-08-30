use std::fs;
use std::path::{Path, PathBuf};
use std::process::Command;
use std::time::{Duration, Instant, SystemTime, UNIX_EPOCH};

use anyhow::{Context, Result};
use serde::{Deserialize, Serialize};

use crate::config::Config;

/// The result of a standard OS-update check: whether one is pending, and the version it would
/// bring the host to, when the check can determine that.
#[derive(Debug, Clone, Default, PartialEq, Eq)]
pub struct OsUpdateStatus {
    pub available: bool,
    pub latest_version: Option<String>,
}

/// `softwareupdate -l` doesn't require elevation and is safe to run from the (non-root) `--agent`
/// process just to decide whether an OS-update step is even needed — only the actual install
/// (`process_queue`, below) needs root, via the handoff queue. This is macOS's own standard way
/// of telling whether an OS update is outstanding, the same one Windows (Windows Update) and
/// Linux (the distro's package manager) each have their own equivalent of.
pub fn check() -> Result<OsUpdateStatus> {
    let output = Command::new("softwareupdate")
        .arg("-l")
        .output()
        .context("failed to run softwareupdate -l")?;

    // softwareupdate -l exits non-zero when nothing is available on some macOS versions, so the
    // stdout text (not the exit code) is what actually distinguishes "nothing found" from a real
    // failure — matching either of the two phrasings macOS has used across versions.
    let combined = format!(
        "{}{}",
        String::from_utf8_lossy(&output.stdout),
        String::from_utf8_lossy(&output.stderr)
    );

    let status = parse_check_output(&combined);
    crate::logging::info(&format!(
        "checked for macOS updates: available={} latest_version={:?}",
        status.available, status.latest_version
    ));
    Ok(status)
}

pub fn check_available() -> Result<bool> {
    Ok(check()?.available)
}

/// The pure text-parsing half of [`check`], split out so it can be exercised directly against
/// sample `softwareupdate -l` output rather than only via a real (macOS-only) subprocess call.
fn parse_check_output(combined: &str) -> OsUpdateStatus {
    if combined.contains("No new software available") {
        return OsUpdateStatus::default();
    }

    let available = combined.contains("Software Update found") || combined.contains("* Label:");
    let latest_version = if available { parse_latest_version(combined) } else { None };
    OsUpdateStatus { available, latest_version }
}

/// Pulls the version out of a `softwareupdate -l` listing's "Title: macOS Sequoia 15.1, Version:
/// 15.1, ..." line — the version number `softwareupdate` itself considers the update to be,
/// rather than trying to parse one back out of the free-text title.
fn parse_latest_version(text: &str) -> Option<String> {
    text.lines().find_map(|line| {
        let after = line.split_once("Version:")?.1;
        let version = after.split(',').next()?.trim();
        (!version.is_empty()).then(|| version.to_string())
    })
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn parse_check_output_no_updates_available() {
        let combined = "Software Update Tool\n\nNo new software available.\n";

        let status = parse_check_output(combined);

        assert_eq!(status, OsUpdateStatus::default());
        assert!(!status.available);
        assert_eq!(status.latest_version, None);
    }

    #[test]
    fn parse_check_output_one_update_available_modern_phrasing() {
        // The multi-line listing format seen on recent macOS versions.
        let combined = concat!(
            "Software Update Tool\n\n",
            "Finding available software\n",
            "Software Update found the following new or updated software:\n",
            "* Label: macOS Sequoia 15.1-24B83\n",
            "\tTitle: macOS Sequoia 15.1, Version: 15.1, Size: 3319833KiB, Recommended: YES,\n",
        );

        let status = parse_check_output(combined);

        assert!(status.available);
        assert_eq!(status.latest_version.as_deref(), Some("15.1"));
    }

    #[test]
    fn parse_check_output_one_update_available_legacy_phrasing() {
        // Older macOS versions omit the "Software Update found ..." sentence and only ever print
        // the "* Label:" listing lines directly.
        let combined = "* Label: macOS Ventura 13.6-22G120\n\tTitle: macOS Ventura 13.6, Version: 13.6, Size: 1234567KiB, Recommended: YES,\n";

        let status = parse_check_output(combined);

        assert!(status.available);
        assert_eq!(status.latest_version.as_deref(), Some("13.6"));
    }

    #[test]
    fn parse_check_output_available_but_no_parseable_version() {
        // A listing that trips the "available" detection but has no "Version:" field at all —
        // should still report available, just without a version.
        let combined = "Software Update found the following new or updated software:\n* Label: SomeUpdate\n";

        let status = parse_check_output(combined);

        assert!(status.available);
        assert_eq!(status.latest_version, None);
    }

    #[test]
    fn parse_check_output_unrecognized_text_reports_not_available() {
        // Neither sentinel phrase present at all (e.g. an unexpected error message) — treated as
        // "nothing available" rather than a false positive.
        let combined = "softwareupdate: command not found\n";

        let status = parse_check_output(combined);

        assert!(!status.available);
    }

    #[test]
    fn parse_latest_version_takes_the_first_version_field_only() {
        let text = "Title: A, Version: 1.0, Size: 1\nTitle: B, Version: 2.0, Size: 2\n";

        assert_eq!(parse_latest_version(text).as_deref(), Some("1.0"));
    }

    #[test]
    fn parse_latest_version_trims_whitespace_around_the_value() {
        let text = "Title: A, Version:   15.1  , Size: 1\n";

        assert_eq!(parse_latest_version(text).as_deref(), Some("15.1"));
    }

    #[test]
    fn parse_latest_version_returns_none_when_the_field_is_empty() {
        let text = "Title: A, Version: , Size: 1\n";

        assert_eq!(parse_latest_version(text), None);
    }

    #[test]
    fn parse_latest_version_returns_none_when_no_version_field_exists() {
        let text = "Title: A, Size: 1\n";

        assert_eq!(parse_latest_version(text), None);
    }
}

#[derive(Debug, Serialize, Deserialize)]
struct InstallResult {
    success: bool,
    output: String,
}

fn now_epoch() -> u64 {
    SystemTime::now().duration_since(UNIX_EPOCH).unwrap_or_default().as_secs()
}

/// Drops a marker file into the handoff queue asking the root daemon to install pending macOS
/// updates, and returns the path it will look for the matching result under.
///
/// Deliberately carries no instructions of its own (no script, no command) — the daemon always
/// runs the same fixed `softwareupdate -i -a`, so a malicious or buggy request file can, at
/// worst, trigger an OS update early, never arbitrary code as root.
fn request_install(queue_dir: &Path) -> Result<PathBuf> {
    fs::create_dir_all(queue_dir).with_context(|| format!("could not create queue directory {}", queue_dir.display()))?;

    let request_path = queue_dir.join(format!("{}.os-update.request", now_epoch()));
    fs::write(&request_path, b"").context("could not write install request")?;
    Ok(request_path)
}

fn result_path_for(request_path: &Path) -> PathBuf {
    request_path.with_extension("request.result.json")
}

/// Requests an OS-update install from the root daemon and blocks (polling) until it reports
/// back, or `timeout` elapses. macOS updates can legitimately take a long time to download and
/// install, so the caller should pass a generous timeout.
pub fn install_via_daemon(queue_dir: &Path, timeout: Duration) -> Result<bool> {
    let request_path = request_install(queue_dir)?;
    let result_path = result_path_for(&request_path);

    let started = Instant::now();
    loop {
        if let Ok(contents) = fs::read_to_string(&result_path) {
            let result: InstallResult = serde_json::from_str(&contents).unwrap_or(InstallResult {
                success: false,
                output: contents,
            });
            let _ = fs::remove_file(&result_path);
            crate::logging::info(&format!("OS update install result: success={} output={}", result.success, result.output.trim()));
            return Ok(result.success);
        }

        if started.elapsed() >= timeout {
            let _ = fs::remove_file(&request_path);
            anyhow::bail!("timed out waiting for the root daemon to install OS updates");
        }

        std::thread::sleep(Duration::from_secs(5));
    }
}

#[derive(Debug, Serialize)]
#[serde(rename_all = "camelCase")]
struct ReportOsPatchResultRequest<'a> {
    serial_number: &'a str,
}

/// Tells the server this host's pending macOS update was just successfully installed, so its
/// pending-update flag and target version clear immediately rather than waiting on this host's
/// next check-in to re-derive them from a fresh `softwareupdate -l` run. Best-effort, the same as
/// `upgrade::report_patch_result`: the update already succeeded locally by the time this is
/// called, so a failure here is only logged, never treated as undoing the install.
pub fn report_patched(client: &reqwest::blocking::Client, config: &Config, serial_number: &str) {
    let request = ReportOsPatchResultRequest { serial_number };

    match client.post(config.os_patch_result_url()).json(&request).send() {
        Ok(response) if response.status().is_success() => {
            crate::logging::info("reported successful macOS update install to the server");
        }
        Ok(response) => {
            crate::logging::warn(&format!("server rejected OS patch-result report (HTTP {})", response.status()));
        }
        Err(err) => {
            crate::logging::warn(&format!("could not report the successful macOS update install to the server: {err:#}"));
        }
    }
}

/// The root daemon's half of the handoff: run once per invocation (it's triggered on demand via
/// `WatchPaths` on the queue directory — see the LaunchDaemon plist — so it doesn't need its own
/// persistent loop). Processes every pending request found, oldest first, so a request dropped
/// while the daemon was already mid-run isn't silently skipped.
pub fn process_queue(queue_dir: &Path) {
    let Ok(entries) = fs::read_dir(queue_dir) else {
        return;
    };

    let mut requests: Vec<PathBuf> = entries
        .filter_map(|entry| entry.ok())
        .map(|entry| entry.path())
        .filter(|path| path.extension().is_some_and(|ext| ext == "request"))
        .collect();
    requests.sort();

    for request_path in requests {
        crate::logging::info(&format!("processing OS update request: {}", request_path.display()));

        let output = Command::new("softwareupdate").args(["-i", "-a"]).output();
        let result = match output {
            Ok(output) => InstallResult {
                success: output.status.success(),
                output: format!(
                    "{}{}",
                    String::from_utf8_lossy(&output.stdout),
                    String::from_utf8_lossy(&output.stderr)
                ),
            },
            Err(err) => InstallResult {
                success: false,
                output: format!("failed to run softwareupdate: {err}"),
            },
        };

        crate::logging::info(&format!("OS update install finished: success={} output={}", result.success, result.output.trim()));

        let result_path = result_path_for(&request_path);
        if let Ok(json) = serde_json::to_string(&result) {
            if let Err(err) = fs::write(&result_path, json) {
                crate::logging::warn(&format!("could not write OS update result: {err}"));
            }
        }
        let _ = fs::remove_file(&request_path);
    }
}
