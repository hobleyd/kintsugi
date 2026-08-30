use std::fs;
use std::os::unix::fs::PermissionsExt;
use std::process::Command;
use std::time::{SystemTime, UNIX_EPOCH};

use anyhow::{Context, Result};
use serde::{Deserialize, Serialize};

use crate::config::Config;
use crate::identity::{self, AgentIdentity};
use crate::logging;

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
    #[allow(dead_code)]
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
    let response = client
        .get(config.upgrade_paths_url())
        .query(&[("serialNumber", serial_number)])
        .send()
        .context("request failed")?;

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

/// Runs the actual update for one already-selected (`is_patchable`) application: a
/// package-manager command (e.g. `brew upgrade firefox`) for `Method::PackageManagerCommand`, run
/// as whichever user this process itself runs as — deliberately never as root, since Homebrew
/// refuses to run under root at all — or the generated script's `--update` mode for
/// `Method::Script`. Re-verifies the signature right before running it, rather than trusting that
/// the caller already checked via `is_patchable` — the one function that actually executes
/// something is the one that shouldn't ever skip that check, even if every current caller happens
/// to call it correctly.
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

fn run_shell_command(command: &str) -> Result<()> {
    logging::info(&format!("running command: sh -c {command:?}"));

    let output = Command::new("sh").arg("-c").arg(command).output().context("failed to run command")?;

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

