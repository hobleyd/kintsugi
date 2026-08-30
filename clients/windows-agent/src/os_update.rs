use std::process::Command;

use anyhow::{Context, Result};
use serde::Serialize;

use crate::config::Config;

/// The result of a standard OS-update check: whether one is pending, and a short description of
/// what it would bring the host to, when the check can determine that.
#[derive(Debug, Clone, Default, PartialEq, Eq)]
pub struct OsUpdateStatus {
    pub available: bool,
    pub latest_version: Option<String>,
}

/// `Host.OperatingSystemLatestVersion` is `varchar(64)` and its command validator enforces the same
/// (see HostConfiguration / CreateHostCommandValidator) — a longer value is rejected outright, so
/// the description below is truncated here rather than failing the whole check-in.
const MAX_LATEST_VERSION_LENGTH: usize = 64;

/// Windows PowerShell (5.1), not `pwsh`: it ships with every supported version of Windows, whereas
/// PowerShell 7 has to be installed. The API server has the opposite constraint — see
/// `ScriptLanguages.Interpreter` — which is why the same script has to work under both.
pub const POWERSHELL: &str = "powershell";

/// The Windows Update Agent COM API, driven from PowerShell. This is the platform's own standard
/// way of asking what's outstanding — the direct counterpart to `softwareupdate -l` on macOS and a
/// distro's package manager on Linux.
///
/// Driven through PowerShell rather than through COM bindings in Rust on purpose: `IUpdateSearcher`
/// is a late-bound automation interface whose results are `IDispatch` collections, so calling it
/// from Rust means hand-rolling `IDispatch::Invoke` for every property read. PowerShell already
/// does exactly that, ships in the box, and leaves an invocation an administrator can paste into a
/// console to see the same answer this agent got.
const SEARCH_SCRIPT: &str = r#"
$ErrorActionPreference = 'Stop'
$session = New-Object -ComObject Microsoft.Update.Session
$searcher = $session.CreateUpdateSearcher()
# IsInstalled=0 and IsHidden=0: outstanding, and not something an administrator explicitly hid.
# Type='Software' excludes driver updates, which are not what a patching policy is about and which
# an unattended install has no business replacing.
$result = $searcher.Search("IsInstalled=0 and Type='Software' and IsHidden=0")
$updates = @($result.Updates)
Write-Output ("COUNT=" + $updates.Count)
foreach ($update in $updates) {
    Write-Output ("TITLE=" + $update.Title)
}
"#;

/// The install half. Downloads and installs everything the same search finds — deliberately a fixed
/// script with nothing parameterized, so the queue request that triggers it (see `queue`) carries
/// no instructions of its own and can never be used to install something else.
const INSTALL_SCRIPT: &str = r#"
$ErrorActionPreference = 'Stop'
$session = New-Object -ComObject Microsoft.Update.Session
$searcher = $session.CreateUpdateSearcher()
$result = $searcher.Search("IsInstalled=0 and Type='Software' and IsHidden=0")
if ($result.Updates.Count -eq 0) {
    Write-Output 'nothing to install'
    exit 0
}

# EULA acceptance has to happen before the download, and only for updates that ask for one — this
# runs unattended, so an unaccepted EULA would otherwise stall the whole install silently.
$toInstall = New-Object -ComObject Microsoft.Update.UpdateColl
foreach ($update in $result.Updates) {
    if (-not $update.EulaAccepted) { $update.AcceptEula() }
    $null = $toInstall.Add($update)
}

$downloader = $session.CreateUpdateDownloader()
$downloader.Updates = $toInstall
$downloadResult = $downloader.Download()
# ResultCode 2 = Succeeded, 3 = SucceededWithErrors (some updates applied, others didn't).
if ($downloadResult.ResultCode -ne 2 -and $downloadResult.ResultCode -ne 3) {
    Write-Error ("download failed with result code " + $downloadResult.ResultCode)
    exit 1
}

$installer = $session.CreateUpdateInstaller()
$installer.Updates = $toInstall
$installResult = $installer.Install()
Write-Output ("installed " + $toInstall.Count + " update(s), result code " + $installResult.ResultCode)
if ($installResult.RebootRequired) { Write-Output 'a reboot is required to finish' }
if ($installResult.ResultCode -ne 2 -and $installResult.ResultCode -ne 3) {
    Write-Error ("install failed with result code " + $installResult.ResultCode)
    exit 1
}
exit 0
"#;

fn run_powershell(script: &str) -> Result<std::process::Output> {
    Command::new(POWERSHELL)
        .args(["-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-Command", script])
        .output()
        .context("failed to run powershell")
}

/// Checks whether Windows Update has anything outstanding for this host.
///
/// Only ever called from the SYSTEM service. The macOS agent runs its equivalent from the *per-user*
/// process too (`softwareupdate -l` needs no elevation there), but on Windows the tray process asks
/// the service instead, via a `Plan` request — see `queue` for why that split exists at all.
pub fn check() -> Result<OsUpdateStatus> {
    let output = run_powershell(SEARCH_SCRIPT)?;

    if !output.status.success() {
        anyhow::bail!(
            "the Windows Update search exited with {}: {}",
            output.status,
            String::from_utf8_lossy(&output.stderr).trim()
        );
    }

    let status = parse_search_output(&String::from_utf8_lossy(&output.stdout));
    crate::logging::info(&format!(
        "checked for Windows updates: available={} latest_version={:?}",
        status.available, status.latest_version
    ));
    Ok(status)
}

/// The pure text-parsing half of [`check`], split out so it can be exercised directly against
/// sample output rather than only via a real (Windows-only) subprocess call — the same split the
/// macOS agent makes around `softwareupdate -l`.
fn parse_search_output(stdout: &str) -> OsUpdateStatus {
    let count = stdout
        .lines()
        .find_map(|line| line.trim().strip_prefix("COUNT="))
        .and_then(|value| value.trim().parse::<usize>().ok())
        .unwrap_or(0);

    if count == 0 {
        return OsUpdateStatus::default();
    }

    // The first title is the most significant update in the search result. There's no single
    // "version" a set of Windows updates brings the host to the way a macOS point release has one,
    // so what's reported is the closest honest equivalent: what the administrator would see at the
    // top of the Windows Update page, plus how many others are queued behind it.
    let first_title = stdout
        .lines()
        .find_map(|line| line.trim().strip_prefix("TITLE="))
        .map(str::trim)
        .filter(|title| !title.is_empty());

    let latest_version = first_title.map(|title| {
        let description = if count > 1 { format!("{title} (+{} more)", count - 1) } else { title.to_string() };
        truncate_to_char_boundary(&description, MAX_LATEST_VERSION_LENGTH)
    });

    OsUpdateStatus { available: true, latest_version }
}

/// Trims `value` to at most `max_bytes`, never mid-character. A Windows update title routinely runs
/// well past the column's 64 characters ("2026-08 Cumulative Update for Windows 11 Version 24H2 for
/// x64-based Systems (KB5012345)"), and a plain byte slice could land inside a multi-byte character
/// and panic.
fn truncate_to_char_boundary(value: &str, max_bytes: usize) -> String {
    if value.len() <= max_bytes {
        return value.to_string();
    }

    let mut cut = max_bytes;
    while cut > 0 && !value.is_char_boundary(cut) {
        cut -= 1;
    }
    value[..cut].trim_end().to_string()
}

/// Installs everything the search finds. Runs as SYSTEM, from the service — the one privileged step
/// the tray process explicitly hands off (see `queue`).
pub fn install() -> Result<()> {
    crate::logging::info("installing pending Windows updates");

    let output = run_powershell(INSTALL_SCRIPT)?;
    let combined = format!(
        "{}{}",
        String::from_utf8_lossy(&output.stdout),
        String::from_utf8_lossy(&output.stderr)
    );

    crate::logging::info(&format!(
        "Windows update install finished (exit {}): {}",
        output.status,
        combined.trim()
    ));

    if !output.status.success() {
        anyhow::bail!("the Windows Update install exited with {}: {}", output.status, combined.trim());
    }

    Ok(())
}

#[derive(Debug, Serialize)]
#[serde(rename_all = "camelCase")]
struct ReportOsPatchResultRequest<'a> {
    serial_number: &'a str,
}

/// Tells the server this host's pending Windows updates were just successfully installed, so its
/// pending-update flag and target version clear immediately rather than waiting on this host's next
/// check-in to re-derive them from a fresh search. Best-effort, the same as
/// `upgrade::report_patch_result`: the update already succeeded locally by the time this is called,
/// so a failure here is only logged, never treated as undoing the install.
pub fn report_patched(client: &reqwest::blocking::Client, config: &Config, serial_number: &str) {
    let request = ReportOsPatchResultRequest { serial_number };

    match client.post(config.os_patch_result_url()).json(&request).send() {
        Ok(response) if response.status().is_success() => {
            crate::logging::info("reported successful Windows update install to the server");
        }
        Ok(response) => {
            crate::logging::warn(&format!("server rejected OS patch-result report (HTTP {})", response.status()));
        }
        Err(err) => {
            crate::logging::warn(&format!("could not report the successful Windows update install to the server: {err:#}"));
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn parse_search_output_no_updates_available() {
        let status = parse_search_output("COUNT=0\n");

        assert_eq!(status, OsUpdateStatus::default());
        assert!(!status.available);
    }

    #[test]
    fn parse_search_output_one_update_reports_its_title() {
        let stdout = "COUNT=1\nTITLE=2026-08 Cumulative Update (KB5012345)\n";

        let status = parse_search_output(stdout);

        assert!(status.available);
        assert_eq!(status.latest_version.as_deref(), Some("2026-08 Cumulative Update (KB5012345)"));
    }

    #[test]
    fn parse_search_output_several_updates_names_the_first_and_counts_the_rest() {
        let stdout = "COUNT=3\nTITLE=Cumulative Update\nTITLE=Defender definitions\nTITLE=.NET rollup\n";

        let status = parse_search_output(stdout);

        assert_eq!(status.latest_version.as_deref(), Some("Cumulative Update (+2 more)"));
    }

    #[test]
    fn parse_search_output_truncates_a_title_to_the_columns_length() {
        // The server's own validator rejects anything longer, which would fail the entire check-in
        // — not just this one field — over a long update title.
        let long_title = "2026-08 Cumulative Update for Windows 11 Version 24H2 for x64-based Systems (KB5012345)";
        let stdout = format!("COUNT=1\nTITLE={long_title}\n");

        let status = parse_search_output(&stdout);

        let reported = status.latest_version.expect("a title should still be reported");
        assert!(reported.len() <= MAX_LATEST_VERSION_LENGTH);
        assert!(long_title.starts_with(&reported));
    }

    #[test]
    fn parse_search_output_reports_available_even_with_no_parseable_title() {
        // Available but undescribed is still available — the same shape the macOS agent's own
        // "available but no parseable version" case has.
        let status = parse_search_output("COUNT=2\n");

        assert!(status.available);
        assert_eq!(status.latest_version, None);
    }

    #[test]
    fn parse_search_output_unrecognized_text_reports_not_available() {
        // Neither a count nor a title — treated as "nothing available" rather than a false
        // positive that would prompt the user to patch nothing.
        let status = parse_search_output("Access is denied.\n");

        assert!(!status.available);
    }

    #[test]
    fn truncate_to_char_boundary_never_splits_a_character() {
        let value = "Mise à jour cumulative pour Windows 11 — version 24H2 (KB5012345)";

        for limit in 1..value.len() {
            let truncated = truncate_to_char_boundary(value, limit);
            assert!(truncated.len() <= limit);
            assert!(value.starts_with(truncated.trim_end()));
        }
    }

    #[test]
    fn truncate_to_char_boundary_leaves_a_short_value_alone() {
        assert_eq!(truncate_to_char_boundary("KB5012345", MAX_LATEST_VERSION_LENGTH), "KB5012345");
    }
}
