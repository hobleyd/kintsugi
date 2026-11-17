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

/// `pub(crate)` because `system_info::read_firmware_identity` drives CIM through PowerShell too,
/// for the same reason this module does — one place naming the interpreter and its flags means the
/// 5.1-vs-`pwsh` decision above is made once rather than per caller.
pub(crate) fn run_powershell(script: &str) -> Result<std::process::Output> {
    Command::new(POWERSHELL)
        .args(["-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-Command", script])
        .output()
        .context("failed to run powershell")
}

/// First line of any script whose output the agent decodes as UTF-8. Windows PowerShell 5.1 writes
/// redirected output in the console's OEM code page, so without this accented names, non-English event
/// text and curly quotes arrive as `?` or U+FFFD. The `try` is for a host with no console to set it on;
/// the script then still runs, in whatever encoding it has.
pub(crate) const UTF8_OUTPUT: &str = "try { [Console]::OutputEncoding = [Text.UTF8Encoding]::new($false) } catch { }\n";

/// [`run_powershell`] with a deadline: the interpreter is **killed** when `budget` runs out, and the
/// error's [`std::io::ErrorKind`] is `TimedOut`. `output()` cannot do that, and a read that merely stops
/// being waited for leaves `powershell.exe` running as SYSTEM after the failure has been reported, with
/// the next request starting a second one beside it.
pub(crate) fn run_powershell_within(script: &str, budget: std::time::Duration) -> Result<std::process::Output> {
    let mut command = Command::new(POWERSHELL);
    command.args(["-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-Command", script]);
    run_within(command, budget)
}

/// Runs `command` with its output captured, polls for it to finish and kills it at the deadline. Both
/// pipes are drained on threads while it runs, so a script that prints more than a pipe holds cannot
/// stall waiting for a reader.
pub(crate) fn run_within(mut command: Command, budget: std::time::Duration) -> Result<std::process::Output> {
    use std::io::Read;
    use std::process::Stdio;
    use std::time::Instant;

    let mut child = command
        .stdin(Stdio::null())
        .stdout(Stdio::piped())
        .stderr(Stdio::piped())
        .spawn()
        .context("failed to run powershell")?;
    let mut stdout = child.stdout.take().context("no stdout pipe")?;
    let mut stderr = child.stderr.take().context("no stderr pipe")?;
    let out = std::thread::spawn(move || {
        let mut bytes = Vec::new();
        let _ = stdout.read_to_end(&mut bytes);
        bytes
    });
    let err = std::thread::spawn(move || {
        let mut bytes = Vec::new();
        let _ = stderr.read_to_end(&mut bytes);
        bytes
    });

    let deadline = Instant::now() + budget;
    let status = loop {
        if let Some(status) = child.try_wait().context("failed to wait for powershell")? {
            break status;
        }
        if Instant::now() >= deadline {
            let _ = child.kill();
            let _ = child.wait();
            // The pipes close with the process, so the readers end.
            let _ = out.join();
            let _ = err.join();
            return Err(std::io::Error::new(std::io::ErrorKind::TimedOut, "powershell did not finish in time").into());
        }
        std::thread::sleep(std::time::Duration::from_millis(50));
    };

    Ok(std::process::Output { status, stdout: out.join().unwrap_or_default(), stderr: err.join().unwrap_or_default() })
}

/// True when [`run_within`] gave up on the command at its deadline.
pub(crate) fn timed_out(error: &anyhow::Error) -> bool {
    error.downcast_ref::<std::io::Error>().is_some_and(|e| e.kind() == std::io::ErrorKind::TimedOut)
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

/// What the install script prints when Windows says the updates it just installed need a restart to
/// finish — `$installResult.RebootRequired`. [`install`] reads it back from the output, so the text
/// here and the line in [`INSTALL_SCRIPT`] have to agree; a test checks they do.
const REBOOT_REQUIRED_MARKER: &str = "a reboot is required to finish";

/// What the install did, beyond "it worked".
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct InstallOutcome {
    /// Whether Windows said a restart is needed to finish. Most updates do not — a Defender
    /// definitions update never does — which is why a forced run restarts only when this is true.
    pub reboot_required: bool,
}

/// Installs everything the search finds. Runs as SYSTEM, from the service — the one privileged step
/// the tray process explicitly hands off (see `queue`).
pub fn install() -> Result<InstallOutcome> {
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

    Ok(InstallOutcome { reboot_required: combined.contains(REBOOT_REQUIRED_MARKER) })
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

/// The name a failed Windows update install is filed under on the admin UI's Failed Updates screen.
///
/// The same arrangement the macOS agent has: the application-failure route is already in nginx's
/// agent-certificate regex and already carries `[RequireAgentIdentity]`, and
/// `ReportPatchFailureCommandHandler` files a name that resolves to no upgrade path with a null
/// platform, which the screen shows as a failure it cannot offer an AI repair for — right for an
/// OS update, which has no script to repair. `ReportOperatingSystemPatchedCommandHandler` closes
/// the row when a success is reported, so the two have to agree on this string.
pub const OS_FAILURE_APPLICATION_NAME: &str = "Windows";

/// Starts a restart of this host in `delay_seconds`, with Windows' own on-screen countdown.
///
/// Run by the service only (`queue::RequestKind::Restart`), after an administrator's forced Windows
/// update has installed *and Windows said it needs a restart to finish*: an update that is waiting on
/// a restart is not finished, and "patched" with the old files still in use is the thing a forced run
/// exists to avoid. Not asked for after an update that does not need one — a Defender definitions
/// update is the common case, and restarting a computer for a signature file buys nothing. The delay is
/// chosen by the service, never by the request (`queue::restart_decision`): five minutes when everybody
/// logged on is at their desk, one when nobody is logged on at all — and no restart at all while anybody
/// is logged on but locked or disconnected.
///
/// `shutdown.exe /r /t` rather than a restart API, so Windows itself shows the countdown and the
/// message to whoever is signed in, with nothing for this agent to draw. The command line is fixed
/// and the message has no parameters, so nothing a request carries can change what runs. Whoever is
/// at the machine can still run `shutdown /a` to abort it, as with any Windows restart notice; this
/// is a warning, not a lock.
pub fn restart(delay_seconds: u64) -> Result<()> {
    // The last line, whatever the caller decided: `/t 0` closes every open application at once with
    // no chance to save, so nothing below the minimum reaches shutdown.exe.
    if !crate::queue::restart_delay_in_bounds(delay_seconds) {
        anyhow::bail!(
            "refusing a restart delay of {delay_seconds} second(s); it must be between {} and {}",
            crate::queue::MIN_RESTART_DELAY_SECONDS,
            crate::queue::MAX_RESTART_DELAY_SECONDS
        );
    }

    let system_root = std::env::var("SystemRoot").unwrap_or_else(|_| r"C:\Windows".to_string());
    let shutdown = std::path::Path::new(&system_root).join("System32").join("shutdown.exe");

    crate::logging::info(&format!("restarting this host in {delay_seconds} second(s) to finish the Windows update"));

    let output = Command::new(&shutdown)
        .args([
            "/r",
            "/t",
            &delay_seconds.to_string(),
            "/c",
            "Kintsugi is restarting this computer to finish installing Windows updates. Please save your work.",
        ])
        .output()
        .with_context(|| format!("could not run {}", shutdown.display()))?;

    if !output.status.success() {
        anyhow::bail!(
            "shutdown.exe exited with {}: {}{}",
            output.status,
            String::from_utf8_lossy(&output.stdout).trim(),
            String::from_utf8_lossy(&output.stderr).trim()
        );
    }

    Ok(())
}

/// Files a failed Windows update install on the Failed Updates screen, under
/// [`OS_FAILURE_APPLICATION_NAME`], with the install's captured output.
///
/// Before this existed the failure was logged on the host and nowhere else, so the Hosts screen
/// showed a machine that simply never updated, indistinguishable from one waiting for its next
/// patch cycle.
///
/// Called from the service only (`service::AgentService::install_os_updates`), which is the one
/// place `install` runs and the side holding the identity — the tray reaches it through `queue` and
/// reports nothing, so a failure is never recorded twice.
pub fn report_failed(
    client: &reqwest::blocking::Client,
    config: &Config,
    serial_number: &str,
    error: &anyhow::Error,
) {
    let installed_version = crate::system_info::operating_system().unwrap_or_else(|_| "unknown".to_string());

    crate::upgrade::report_failure(client, config, serial_number, OS_FAILURE_APPLICATION_NAME, &installed_version, None, error);
}

#[cfg(test)]
mod tests {
    /// The deadline is enforced by killing the child, not by ceasing to wait for it. `ping -n` against
    /// localhost is a command every Windows has that runs for about a second per count.
    #[test]
    fn a_command_that_outlives_its_deadline_is_killed_and_reported_as_timed_out() {
        let started = std::time::Instant::now();
        let mut command = Command::new("ping");
        command.args(["-n", "60", "127.0.0.1"]);

        let result = run_within(command, std::time::Duration::from_millis(500));

        assert!(timed_out(&result.expect_err("it should time out")));
        assert!(started.elapsed() < std::time::Duration::from_secs(20), "it waited for the command to finish");
    }

    #[test]
    fn a_command_that_finishes_gives_its_output_and_exit_status() {
        let mut command = Command::new("cmd");
        command.args(["/C", "echo hello"]);

        let output = run_within(command, std::time::Duration::from_secs(30)).expect("cmd should run");

        assert!(output.status.success());
        assert!(String::from_utf8_lossy(&output.stdout).contains("hello"));
    }

    use super::*;

    #[test]
    fn the_install_script_prints_the_line_install_reads_back() {
        assert!(INSTALL_SCRIPT.contains(REBOOT_REQUIRED_MARKER), "or a restart that Windows asked for would never be seen");
    }

    /// `ReportOperatingSystemPatchedCommandHandler.WindowsFailureApplicationName` closes the rows
    /// filed under this name when a success is reported, and nothing checks the two agree.
    #[test]
    fn a_failed_install_is_filed_under_the_name_the_server_closes_on_success() {
        assert_eq!(OS_FAILURE_APPLICATION_NAME, "Windows");
    }

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
