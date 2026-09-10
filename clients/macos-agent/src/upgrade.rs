use std::fs;
use std::os::unix::fs::PermissionsExt;
use std::process::Command;
use std::time::{SystemTime, UNIX_EPOCH};

use anyhow::{Context, Result};
use serde::{Deserialize, Serialize};
use time::format_description::well_known::Rfc3339;
use time::OffsetDateTime;

use crate::config::Config;
use crate::identity::{self, AgentIdentity};
use crate::logging;
use crate::system_info;

/// Mirrors the backend's `UpgradeStatusDto` — see
/// Kintsugi.Application/UpgradePaths/UpgradeStatusDto.cs. `latestVersion`/`updateAvailable`
/// are trusted straight from the backend now: the server runs each script's own
/// `--update-version` mode itself (no AI call, and no need for this agent to duplicate that
/// check), so its answer is the authoritative one, not just a starting point this agent used to
/// re-verify. Fields this agent still has no use for (hostname, serialNumber, sourceUrl,
/// checkedUtc) stay omitted, since serde only requires the fields actually named here to be
/// present.
#[derive(Debug, Clone, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct UpgradeStatus {
    pub application_name: String,
    /// Reported back alongside a failure, so the Failed Updates screen can say what was installed
    /// when the upgrade was attempted — see `report_patch_failure`.
    pub installed_version: String,
    pub latest_version: Option<String>,
    pub update_available: bool,
    pub method: UpgradeMethod,
    /// CFBundleIdentifier, when known — required to invoke `script` at all, since it needs
    /// `--appId` for both its own safety check and its CLI contract.
    pub application_identifier: Option<String>,
    /// The package-manager command for a `Method::PackageManagerCommand` row (e.g. `brew upgrade
    /// firefox`) — run as-is via a shell, as the logged-in (admin) user, never as root: Homebrew
    /// itself refuses to run as root. Only ever populated for an unrecognized package manager's
    /// legacy row now — a recognized one (Homebrew) gets a `Method::Script` row instead, see below.
    pub command: Option<String>,
    #[allow(dead_code)]
    pub notes: Option<String>,
    /// A bash script implementing a durable `--update-version` / `--update` CLI for a
    /// `Method::Script` row — see `run_script` — absent for every other method. Either AI-authored,
    /// or a fixed one the server writes itself for a recognized package manager (e.g. Homebrew).
    pub script: Option<String>,
    /// Base64 DER ECDSA-SHA256 signature over `script`'s bytes, from the server's own
    /// artifact-signing key — see `identity::verify_artifact_signature`. `is_patchable` refuses to
    /// treat a `Script` row as runnable at all unless this checks out.
    pub script_signature: Option<String>,
    /// Same as `script_signature`, but over `command`.
    pub command_signature: Option<String>,
    /// The package manager that owns this installation ("Homebrew"), or `None` for a standalone
    /// application whose script was AI-researched. Decides *which process* runs a `Script` row —
    /// see `runs_as_root`.
    pub package_manager: Option<String>,
}

/// Whether this row's upgrade has to be run by the root daemon (through `queue`) rather than by the
/// per-user process asking. The dividing line is who owns the installation:
///
/// - A **Homebrew** row stays with the logged-in user: Homebrew refuses to run as root outright
///   ("Running Homebrew as root is extremely dangerous and no longer supported"), and its installs
///   are user-owned, so the logged-in user is the right — and only — process for it. A legacy
///   `PackageManagerCommand` row is a bare `brew upgrade ...` for the same reason. That covers
///   Homebrew's *own* row too, which is recognized by name (`system_info::HOMEBREW_NAME`) rather
///   than by owner: the inventory reports the manager with no `packageManager` of its own, so on
///   that field alone it reads as an AI-researched row and was queued to the daemon — which
///   rightly refused it, and every patch cycle then logged Homebrew as failed. Its upgrade is the
///   same shared script every formula runs (`brew update` is Homebrew upgrading itself — see
///   `HomebrewUpgradeScript` on the server), as the same user.
/// - An **AI-researched** row (no package manager) installs into `/Applications` the way the
///   server's prompt tells it to — replace the bundle in place, or `installer -pkg ... -target /` —
///   and a bundle that arrived by MDM, `.pkg` or any installer that asked for a password is owned
///   by `root:wheel`. Run as the user, the script's `rm` prints `Permission denied` for every file
///   in the bundle and the old version stays; that is what took Ollama down on the fleet's own
///   Macs. The prompt now writes these scripts *for* root (see
///   `AiUpgradePathResearchClient.BuildScriptGenerationPrompt`), so this is also the only context
///   they are tested in.
/// - An **App Store** row goes to root as well, for a reason that is the mirror image of Homebrew's.
///   Since Apple's fix for CVE-2025-43411 the install half of a store update needs root, while the
///   download half needs the logged-in user's store session; the per-user process is one of those
///   and has no way to become the other (its `sudo` has no TTY to answer), whereas root can be both
///   — `launchctl asuser` puts it inside the user's session and `mas` drops to that user for the
///   store half. The server's `AppStoreUpgradeScript` does exactly that dance, and it is written for
///   root: run as the user it refuses on its first line. Recognized by the same name the inventory
///   reports the manager under (`system_info::APP_STORE_NAME`), which is also the string the
///   server's `PackageManagerCatalog.AppStore` keys the row's bucket by.
///
/// The daemon asks this same question before running a request (see `main::DaemonRequestHandler`)
/// and refuses a Homebrew row, so a forged request cannot get `brew` run as root either.
pub fn runs_as_root(status: &UpgradeStatus) -> bool {
    status.method == UpgradeMethod::Script
        && !status.application_name.eq_ignore_ascii_case(system_info::HOMEBREW_NAME)
        && status.package_manager.as_deref().is_none_or(|manager| manager.eq_ignore_ascii_case(system_info::APP_STORE_NAME))
}

/// Mirrors Kintsugi.Domain.Enums.UpgradeMethod. Deserializes from the backend's plain enum
/// member names (`"Script"`, `"PackageManagerCommand"`, ...), which is `serde`'s default for a
/// unit-only enum — unlike `policy::TimeUnit`, no manual ordinal mapping is needed here.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Deserialize)]
pub enum UpgradeMethod {
    Unknown,
    DirectDownload,
    PackageManagerCommand,
    ManualSteps,
    Script,
}

/// Fetches this host's known upgrade paths from the backend. Failures here are the caller's to
/// handle as best-effort: a bad or unreachable response shouldn't stop the agent from having
/// already registered the host and its installed applications, which every other consumer (the
/// Applications page) depends on regardless of whether upgrade research has run yet.
pub fn fetch_upgrade_statuses(
    client: &reqwest::blocking::Client,
    config: &Config,
    serial_number: &str,
) -> Result<Vec<UpgradeStatus>> {
    let response = crate::get_with_retry("this host's upgrade paths", || {
        client
            .get(config.upgrade_paths_url())
            .query(&[("serialNumber", serial_number)])
    })?;

    if !response.status().is_success() {
        anyhow::bail!("request rejected (HTTP {})", response.status());
    }

    response
        .json::<Vec<UpgradeStatus>>()
        .context("could not parse response")
}

#[derive(Debug, Serialize)]
#[serde(rename_all = "camelCase")]
struct ReportPatchResultRequest<'a> {
    serial_number: &'a str,
    application_name: &'a str,
    new_version: &'a str,
}

/// Tells the server this application was just successfully patched to `new_version`, so its
/// record of what's installed reflects that immediately rather than waiting on this host's next
/// full inventory report (see `main::collect_installed_applications`). Best-effort: the patch
/// itself already succeeded locally by the time this is called, so a failure here is only logged
/// — there is nothing to roll back, and the next full inventory report reconciles the server's
/// record regardless.
pub fn report_patch_result(client: &reqwest::blocking::Client, config: &Config, serial_number: &str, application_name: &str, new_version: &str) {
    let request = ReportPatchResultRequest { serial_number, application_name, new_version };

    match client.post(config.patch_result_url()).json(&request).send() {
        Ok(response) if response.status().is_success() => {
            logging::info(&format!("reported successful patch of {application_name} to {new_version}"));
        }
        Ok(response) => {
            logging::warn(&format!(
                "server rejected patch-result report for {application_name} (HTTP {})",
                response.status()
            ));
        }
        Err(err) => {
            logging::warn(&format!("could not report successful patch of {application_name} to the server: {err:#}"));
        }
    }
}

#[derive(Debug, Serialize)]
#[serde(rename_all = "camelCase")]
struct ReportPatchFailureRequest<'a> {
    serial_number: &'a str,
    application_name: &'a str,
    installed_version: &'a str,
    attempted_version: Option<&'a str>,
    failed_utc: String,
    details: String,
}

/// The most of a failing script's output this agent will send to the server.
///
/// A failing script's stderr is unbounded — a loop printing a permission error per file in a
/// bundle runs to megabytes — and it arrives here untruncated, since `run_script` bails with the
/// whole of it. This has to stay comfortably *below* the server's own ceiling
/// (`ReportPatchFailureCommandValidator.MaxDetailsLength`, currently 16000): a report longer than
/// the validator accepts comes back as a 400 and the failure is lost, silently, for exactly the
/// noisiest failures — which are the ones most worth reading. Raise the server's figure before
/// raising this one, never after. Kept identical in the other two agents.
const MAX_REPORTED_FAILURE_BYTES: usize = 4000;

/// Tells the server this application's upgrade ran and failed, so it turns up on the admin UI's
/// Failed Updates screen with the date and the output — where the script can be repaired by the AI
/// or by hand and re-signed.
///
/// Called by whichever process actually ran the thing that failed, exactly like
/// `report_patch_result`: the root daemon for an AI-researched script (see
/// `main::DaemonRequestHandler::patch_application`), this process for a Homebrew one. The per-user
/// side must **not** also report a failure that came back from the daemon through `queue`, or every
/// root-run failure is recorded twice.
///
/// Deliberately not called for a patch that never ran — an application with no signed, patchable
/// path, or a row the daemon refuses on `runs_as_root` grounds. Those are configuration problems
/// rather than bugs in a script, and a screen full of them hides the ones the AI can actually fix.
///
/// Best-effort, like every other report: the failure has already happened and been logged locally,
/// and the next patch cycle reports it again if the server was unreachable this time.
pub fn report_patch_failure(
    client: &reqwest::blocking::Client,
    config: &Config,
    serial_number: &str,
    status: &UpgradeStatus,
    error: &anyhow::Error,
) {
    // The host's own clock, not the server's arrival time: a Mac that patched overnight and could
    // not reach the server until morning would otherwise report the failure as having happened
    // when the network came back.
    let failed_utc = match OffsetDateTime::now_utc().format(&Rfc3339) {
        Ok(formatted) => formatted,
        Err(err) => {
            logging::warn(&format!("could not format the failure timestamp, not reporting: {err:#}"));
            return;
        }
    };

    let request = ReportPatchFailureRequest {
        serial_number,
        application_name: &status.application_name,
        installed_version: &status.installed_version,
        attempted_version: status.latest_version.as_deref(),
        failed_utc,
        details: truncate_for_report(&format!("{error:#}")),
    };

    match client.post(config.patch_failure_url()).json(&request).send() {
        Ok(response) if response.status().is_success() => {
            logging::info(&format!("reported the failed patch of {} to the server", status.application_name));
        }
        Ok(response) => {
            logging::warn(&format!(
                "server rejected the patch-failure report for {} (HTTP {})",
                status.application_name,
                response.status()
            ));
        }
        Err(err) => {
            logging::warn(&format!(
                "could not report the failed patch of {} to the server: {err:#}",
                status.application_name
            ));
        }
    }
}

/// `truncate_for_log`'s rule, at the reporting limit rather than the logging one, and keeping the
/// *tail* rather than the head: a script's last words are where its failure is, while its first
/// are the same preamble every run prints.
fn truncate_for_report(text: &str) -> String {
    let trimmed = text.trim();
    if trimmed.len() <= MAX_REPORTED_FAILURE_BYTES {
        return trimmed.to_string();
    }

    // Back up to the nearest char boundary — a plain byte-index slice could otherwise land inside
    // a multi-byte UTF-8 character and panic.
    let mut cut = trimmed.len() - MAX_REPORTED_FAILURE_BYTES;
    while !trimmed.is_char_boundary(cut) {
        cut += 1;
    }

    format!("... [truncated, {cut} earlier byte(s) omitted]\n{}", &trimmed[cut..])
}

/// Whether `patch_one` has anything to actually do for this row — used to build the list a patch
/// cycle will actually work through (and size its progress bar against), rather than counting
/// e.g. an unresolved or manual-steps-only path that can never advance it. A `Script` or
/// `PackageManagerCommand` row whose content doesn't carry a signature that verifies against
/// `identity`'s pinned artifact-signing key is treated as not patchable at all, the same as if it
/// had no command/script present — content that never went through the server's real signing step
/// (e.g. a row written straight to the database) is never eligible to run.
pub fn is_patchable(status: &UpgradeStatus, identity: &AgentIdentity) -> bool {
    status.update_available
        && match status.method {
            UpgradeMethod::PackageManagerCommand => status.command.is_some() && verify_signed(identity, &status.command, &status.command_signature),
            UpgradeMethod::Script => {
                status.script.is_some()
                    && status.application_identifier.is_some()
                    && verify_signed(identity, &status.script, &status.script_signature)
            }
            _ => false,
        }
}

fn verify_signed(identity: &AgentIdentity, content: &Option<String>, signature: &Option<String>) -> bool {
    match (content, signature) {
        (Some(content), Some(signature)) => match identity::verify_artifact_signature(identity, content, signature) {
            Ok(()) => true,
            Err(err) => {
                logging::error(&format!("refusing to trust unsigned/tampered content: {err:#}"));
                false
            }
        },
        _ => false,
    }
}

/// Runs the actual update for one already-selected (`is_patchable`) application, as whichever user
/// this process itself runs as: a package-manager command (e.g. `brew upgrade firefox`) for
/// `Method::PackageManagerCommand`, or the script's `--update` mode for `Method::Script`. Which
/// process that is — the logged-in user for a Homebrew row, the root daemon for an AI-researched
/// one — is `runs_as_root`'s decision, made by both callers (`patch_cycle::run_patches` and
/// `main::DaemonRequestHandler`); this function does not check it, because it cannot tell which
/// process it is in and the daemon's refusal has to happen before a request is trusted at all.
/// Re-verifies the signature right before running it, rather than trusting that the caller already
/// checked via `is_patchable` — the one function that actually executes something is the one that
/// shouldn't ever skip that check, even if every current caller happens to call it correctly.
pub fn patch_one(status: &UpgradeStatus, identity: &AgentIdentity) -> Result<()> {
    match status.method {
        UpgradeMethod::PackageManagerCommand => {
            let command = status
                .command
                .as_deref()
                .context("no command was provided for this package-manager path")?;
            let signature = status
                .command_signature
                .as_deref()
                .context("no signature was provided for this command — refusing to run it")?;
            identity::verify_artifact_signature(identity, command, signature)
                .context("command signature verification failed — refusing to run it")?;
            run_shell_command(command)
        }
        UpgradeMethod::Script => {
            let script = status.script.as_deref().context("no script was provided for this application")?;
            let signature = status
                .script_signature
                .as_deref()
                .context("no signature was provided for this script — refusing to run it")?;
            identity::verify_artifact_signature(identity, script, signature)
                .context("script signature verification failed — refusing to run it")?;
            let app_id = status
                .application_identifier
                .as_deref()
                .context("no bundle identifier known, but the script requires --appId")?;
            run_script(
                &status.application_name,
                script,
                &["--appName", &status.application_name, "--appId", app_id, "--update"],
            )
            .map(|_| ())
        }
        other => anyhow::bail!("no runnable upgrade action for method {other:?}"),
    }
}

/// The two fixed prefixes Homebrew installs into — `/opt/homebrew` on Apple Silicon, `/usr/local`
/// on Intel. The same pair `system_info::find_brew_binary` probes, and for the same reason.
const HOMEBREW_BIN_DIRS: [&str; 2] = ["/opt/homebrew/bin", "/usr/local/bin"];

/// What launchd hands a job when nothing sets `PATH` — used only if the inherited value is missing
/// or empty, so a script still finds the system tools it can normally take for granted.
const LAUNCHD_DEFAULT_PATH: &str = "/usr/bin:/bin:/usr/sbin:/sbin";

/// `PATH` for everything this module spawns.
///
/// Both entry points below run under launchd, which gives a job the bare
/// `/usr/bin:/bin:/usr/sbin:/sbin` and nothing of the logged-in user's shell configuration — so
/// `brew` is invisible on `PATH` even on a Mac that plainly has it, since neither of Homebrew's
/// prefixes is on that list. That is exactly why `system_info::find_brew_binary` locates the
/// binary by hand for the inventory scan; the upgrade path never got the same treatment, so the
/// Homebrew upgrade script's own `command -v brew` guard (see the server's
/// `HomebrewUpgradeScript.Build`) reported "homebrew is not installed" and *every*
/// Homebrew-managed application failed to patch, on every Mac in the fleet.
///
/// Prepended rather than substituted: an AI-authored `--update` script is told it may use whatever
/// the platform provides, so it must still see everything launchd offered. The other two agents
/// need no equivalent — a SYSTEM service inherits the machine-wide `PATH` both winget and
/// Chocolatey install themselves onto (see the Windows agent's
/// `system_info::scan_package_managers`), and systemd's compiled-in default already covers
/// `/usr/local/bin` (see the Linux agent's `system_info::find_binary`).
fn spawn_path() -> String {
    let inherited = std::env::var("PATH").unwrap_or_default();
    prepend_homebrew_prefixes(&inherited)
}

fn prepend_homebrew_prefixes(inherited: &str) -> String {
    let inherited = if inherited.is_empty() { LAUNCHD_DEFAULT_PATH } else { inherited };

    // Skipping the entries already there keeps a PATH that happens to name a Homebrew prefix from
    // growing a duplicate every time this runs.
    HOMEBREW_BIN_DIRS
        .into_iter()
        .chain(
            inherited
                .split(':')
                .filter(|dir| !dir.is_empty() && !HOMEBREW_BIN_DIRS.contains(dir)),
        )
        .collect::<Vec<_>>()
        .join(":")
}

fn run_shell_command(command: &str) -> Result<()> {
    logging::info(&format!("running command: sh -c {command:?}"));

    let output = Command::new("sh")
        .arg("-c")
        .arg(command)
        // A legacy PackageManagerCommand row is typically a bare `brew upgrade <formula>`, which
        // launchd's own PATH cannot resolve — see spawn_path.
        .env("PATH", spawn_path())
        .output()
        .context("failed to run command")?;

    log_output("command", command, &output);

    if !output.status.success() {
        anyhow::bail!(
            "exited with {}: {}",
            output.status,
            String::from_utf8_lossy(&output.stderr).trim()
        );
    }

    Ok(())
}

/// Logs the exit status and full stdout/stderr of a completed command/script — regardless of
/// whether it succeeded, since even a "successful" run's output can matter for diagnosing
/// something that looked fine but wasn't, and a failure's own error message otherwise only ever
/// carried a truncated last line of stderr, not the full picture. Each stream is capped
/// defensively (a runaway script printing megabytes shouldn't be able to blow out the log file)
/// rather than left unbounded.
const MAX_LOGGED_OUTPUT_BYTES: usize = 8000;

fn log_output(kind: &str, invocation: &str, output: &std::process::Output) {
    let stdout = truncate_for_log(&String::from_utf8_lossy(&output.stdout));
    let stderr = truncate_for_log(&String::from_utf8_lossy(&output.stderr));
    logging::info(&format!(
        "{kind} finished (exit {}): {invocation}\n--- stdout ---\n{stdout}\n--- stderr ---\n{stderr}",
        output.status
    ));
}

fn truncate_for_log(text: &str) -> String {
    let trimmed = text.trim();
    if trimmed.len() <= MAX_LOGGED_OUTPUT_BYTES {
        return trimmed.to_string();
    }

    // Back up to the nearest char boundary — a plain byte-index slice could otherwise land
    // inside a multi-byte UTF-8 character and panic.
    let mut cut = MAX_LOGGED_OUTPUT_BYTES;
    while !trimmed.is_char_boundary(cut) {
        cut -= 1;
    }

    format!("{}\n... [truncated, {} more byte(s)]", &trimmed[..cut], trimmed.len() - cut)
}

/// Writes `script` to a private temp file, runs it with `args`, and removes it afterward
/// regardless of outcome — an AI-generated script left lying around in /tmp is unnecessary
/// exposure once it's done running. Returns captured stdout on success (the caller trims it, since
/// `--update-version` is specified to print only the bare version string).
fn run_script(application_name: &str, script: &str, args: &[&str]) -> Result<String> {
    let timestamp = SystemTime::now()
        .duration_since(UNIX_EPOCH)
        .unwrap_or_default()
        .as_secs();
    let sanitized_name: String = application_name
        .chars()
        .filter(|c| c.is_ascii_alphanumeric())
        .collect();
    let script_path = std::env::temp_dir().join(format!("kintsugi-upgrade-{sanitized_name}-{timestamp}.sh"));

    fs::write(&script_path, script).context("failed to write script to a temp file")?;

    let mut permissions = fs::metadata(&script_path)
        .context("failed to read temp script permissions")?
        .permissions();
    permissions.set_mode(0o700);
    fs::set_permissions(&script_path, permissions).context("failed to make script executable")?;

    let invocation = format!("{} {}", script_path.display(), args.join(" "));
    logging::info(&format!("running script for {application_name}: {invocation}"));

    let result = Command::new(&script_path)
        .args(args)
        // Homebrew's prefixes are not on the PATH launchd hands this process, and a `--update`
        // script reaching for `brew` (or anything else Homebrew provides) has no other way to
        // find it — see spawn_path.
        .env("PATH", spawn_path())
        .output()
        .context("failed to execute script");

    let _ = fs::remove_file(&script_path);

    let output = result?;

    log_output(&format!("script for {application_name}"), &invocation, &output);

    if !output.status.success() {
        anyhow::bail!(
            "exited with {}: {}",
            output.status,
            String::from_utf8_lossy(&output.stderr).trim()
        );
    }

    Ok(String::from_utf8_lossy(&output.stdout).to_string())
}

#[cfg(test)]
mod tests {
    use super::*;

    fn status(method: UpgradeMethod, package_manager: Option<&str>) -> UpgradeStatus {
        UpgradeStatus {
            application_name: "Ollama".to_string(),
            installed_version: "0.32.14".to_string(),
            latest_version: Some("0.33.3".to_string()),
            update_available: true,
            method,
            application_identifier: Some("com.electron.ollama".to_string()),
            command: None,
            notes: None,
            script: Some("#!/bin/bash\n".to_string()),
            script_signature: None,
            command_signature: None,
            package_manager: package_manager.map(str::to_string),
        }
    }

    #[test]
    fn a_short_failure_is_reported_whole() {
        assert_eq!(truncate_for_report("  exited with 1: Permission denied  "), "exited with 1: Permission denied");
    }

    /// The tail, not the head: a script's last words are where its failure is, while its first are
    /// the same preamble every run prints.
    #[test]
    fn a_long_failure_keeps_its_end_rather_than_its_beginning() {
        let text = format!("{}THE ACTUAL ERROR", "preamble ".repeat(MAX_REPORTED_FAILURE_BYTES));

        let reported = truncate_for_report(&text);

        assert!(reported.ends_with("THE ACTUAL ERROR"));
        assert!(reported.starts_with("... [truncated,"));
    }

    /// The server rejects anything over ReportPatchFailureCommandValidator.MaxDetailsLength, and a
    /// rejected report loses the failure silently — so what is sent has to stay under it however
    /// much a script printed.
    #[test]
    fn a_reported_failure_never_exceeds_the_limit_by_more_than_its_own_notice() {
        let text = "x".repeat(MAX_REPORTED_FAILURE_BYTES * 10);

        let reported = truncate_for_report(&text);

        assert!(reported.len() < MAX_REPORTED_FAILURE_BYTES + 100, "was {} bytes", reported.len());
    }

    /// A byte-index slice landing inside a multi-byte character panics, which would take the whole
    /// patch cycle down while reporting that something else had already gone wrong.
    #[test]
    fn truncating_never_splits_a_multi_byte_character() {
        // 3 bytes each, so the cut point lands mid-character for two thirds of possible lengths.
        let text = "\u{2014}".repeat(MAX_REPORTED_FAILURE_BYTES);

        let reported = truncate_for_report(&text);

        assert!(reported.ends_with('\u{2014}'));
    }

    #[test]
    fn an_ai_researched_script_runs_as_root() {
        // The Ollama case: a root-owned /Applications bundle the logged-in user cannot replace.
        assert!(runs_as_root(&status(UpgradeMethod::Script, None)));
    }

    #[test]
    fn a_homebrew_script_stays_with_the_logged_in_user() {
        // Homebrew refuses to run as root, so this must never reach the daemon.
        assert!(!runs_as_root(&status(UpgradeMethod::Script, Some("Homebrew"))));
    }

    #[test]
    fn homebrews_own_row_stays_with_the_logged_in_user() {
        // The manager's own row is reported with no package manager (it *is* the manager — see
        // `system_info::scan_homebrew`), so on that field alone it looks AI-researched. It was
        // queued to the daemon, which refused it, and every cycle logged "failed to patch
        // Homebrew". Matched by name, case-insensitively like every other row name.
        let mut homebrew = status(UpgradeMethod::Script, None);
        homebrew.application_name = "Homebrew".to_string();
        homebrew.application_identifier = Some("brew".to_string());
        assert!(!runs_as_root(&homebrew));

        homebrew.application_name = "homebrew".to_string();
        assert!(!runs_as_root(&homebrew));
    }

    #[test]
    fn an_app_store_script_runs_as_root() {
        // The install half of a store update needs root and the download half needs the console
        // user's session; only root can be both (via `launchctl asuser`), so this must reach the
        // daemon. Matched case-insensitively like every other row name.
        assert!(runs_as_root(&status(UpgradeMethod::Script, Some("App Store"))));
        assert!(runs_as_root(&status(UpgradeMethod::Script, Some("app store"))));
    }

    #[test]
    fn a_legacy_package_manager_command_stays_with_the_logged_in_user() {
        // A bare `brew upgrade <formula>`, whatever the server knows about its owner.
        assert!(!runs_as_root(&status(UpgradeMethod::PackageManagerCommand, None)));
    }

    #[test]
    fn a_status_without_the_package_manager_field_deserializes_as_standalone() {
        // A server older than the field: the other agents' structs ignore unknown fields the same
        // way, and an absent Option is None under serde without any attribute.
        let json = r#"{"applicationName":"Ollama","installedVersion":"1","latestVersion":null,"updateAvailable":true,"method":"Script","applicationIdentifier":null,"command":null,"notes":null,"script":null,"scriptSignature":null,"commandSignature":null}"#;

        let status: UpgradeStatus = serde_json::from_str(json).unwrap();

        assert_eq!(status.package_manager, None);
    }

    #[test]
    fn prepend_homebrew_prefixes_puts_both_prefixes_ahead_of_launchds_own_path() {
        // The exact value `launchctl print gui/<uid>/au.com.sharpblue.kintsugiagent-ui` reports as
        // this job's default environment, which is what made the Homebrew script's `command -v
        // brew` guard fail on a Mac with Homebrew installed.
        let path = prepend_homebrew_prefixes("/usr/bin:/bin:/usr/sbin:/sbin");

        assert_eq!(path, "/opt/homebrew/bin:/usr/local/bin:/usr/bin:/bin:/usr/sbin:/sbin");
    }

    #[test]
    fn prepend_homebrew_prefixes_keeps_everything_the_process_inherited() {
        let path = prepend_homebrew_prefixes("/usr/bin:/somewhere/custom");

        assert!(path.split(':').any(|dir| dir == "/somewhere/custom"));
    }

    #[test]
    fn prepend_homebrew_prefixes_does_not_duplicate_a_prefix_already_present() {
        // A PATH that already names a prefix (an interactive `brew`-configured shell, say) would
        // otherwise grow a duplicate entry on every call.
        let path = prepend_homebrew_prefixes("/usr/local/bin:/usr/bin:/opt/homebrew/bin");

        assert_eq!(path, "/opt/homebrew/bin:/usr/local/bin:/usr/bin");
    }

    #[test]
    fn prepend_homebrew_prefixes_falls_back_to_the_system_path_when_nothing_was_inherited() {
        // An empty result would leave a script unable to find even `/usr/bin/curl`, which is a
        // worse failure than the one being fixed.
        let path = prepend_homebrew_prefixes("");

        assert_eq!(path, "/opt/homebrew/bin:/usr/local/bin:/usr/bin:/bin:/usr/sbin:/sbin");
    }
}
