use std::fs;
use std::os::unix::fs::PermissionsExt;
use std::path::PathBuf;
use std::process::Command;
use std::time::{SystemTime, UNIX_EPOCH};

use anyhow::{Context, Result};
use serde::{Deserialize, Serialize};

use crate::config::{self, Config};
use crate::identity::{self, AgentIdentity};
use crate::logging;

/// Mirrors the backend's `UpgradeStatusDto` — see
/// Kintsugi.Application/UpgradePaths/UpgradeStatusDto.cs. `latestVersion`/`updateAvailable`
/// are trusted straight from the backend: the server runs each script's own `--update-version`
/// mode itself (no AI call, and no need for this agent to duplicate that check), so its answer is
/// the authoritative one. Fields this agent has no use for (hostname, serialNumber, sourceUrl,
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
    /// The manager's own identifier for this application — a Flatpak application ID or a snap
    /// name (see `system_info::InstalledApp`) — required to invoke `script` at all, since it needs
    /// `--appId` for both its own safety check and its CLI contract.
    pub application_identifier: Option<String>,
    /// The package-manager command for a `Method::PackageManagerCommand` row — run as-is via a
    /// shell. Only ever populated for an unrecognized package manager's legacy row now; a
    /// recognized one (Flatpak, Snap) gets a `Method::Script` row instead, see below.
    pub command: Option<String>,
    #[allow(dead_code)]
    pub notes: Option<String>,
    /// A bash script implementing a durable `--update-version` / `--update` CLI for a
    /// `Method::Script` row — see `run_script` — absent for every other method. Either AI-authored,
    /// or a fixed one the server writes itself for a recognized package manager.
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

/// Fetches this host's known upgrade paths from the backend. Only ever called from the root
/// service, which is the only half of this agent holding the mutual-TLS identity these routes
/// require — see `queue`.
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

/// Finds the one patchable row for `application_name` among everything the server returned.
///
/// The lookup exists because of `queue`: an `AppPatch` request carries only a name, never anything
/// executable, so this is where that name is turned back into something runnable — against a fresh
/// server response and a fresh signature check, not against anything the requester supplied. Names
/// are compared case-insensitively for the same reason the server's own lookups are (see
/// `UpgradePathRepository.BuildByNameAndPlatformLookup`).
pub fn find_patchable<'a>(statuses: &'a [UpgradeStatus], application_name: &str, identity: &AgentIdentity) -> Option<&'a UpgradeStatus> {
    statuses
        .iter()
        .find(|status| status.application_name.eq_ignore_ascii_case(application_name) && is_patchable(status, identity))
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
/// package-manager command for `Method::PackageManagerCommand`, or the generated script's
/// `--update` mode for `Method::Script`. Re-verifies the signature right before running it, rather
/// than trusting that the caller already checked via `is_patchable` — the one function that
/// actually executes something is the one that shouldn't ever skip that check, even if every
/// current caller happens to call it correctly.
///
/// Always runs as root here. The macOS agent runs the same step as the logged-in user because
/// Homebrew refuses to run as root; on Linux every manager this agent handles requires it (see
/// `queue`), which is exactly why this function lives on the service side of the handoff.
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
                .context("no application identifier known, but the script requires --appId")?;
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

    let output = Command::new("sh")
        .arg("-c")
        .arg(command)
        .env("DEBIAN_FRONTEND", "noninteractive")
        .env("LC_ALL", "C")
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

/// Where a script is staged before it runs. Not `/tmp`, which is where the macOS agent stages
/// its copy: there, this runs as the logged-in user, so a world-writable staging directory is no
/// worse than that user's own privileges. Here it runs as root, and a root process creating a
/// semi-predictable path under a world-writable, non-sticky-safe directory is the textbook setup
/// for another local user to win the race with a symlink and have root write — then execute —
/// wherever they point it. This directory is inside the agent's own root-only state directory
/// (`/var/lib/kintsugi-agent`, mode 0700), so nobody else can create anything in it at all.
fn script_staging_dir() -> PathBuf {
    config::state_dir().join("scripts")
}

/// Writes `script` to a private file, runs it with `args`, and removes it afterward regardless of
/// outcome — an AI-generated script left lying around is unnecessary exposure once it's done
/// running. Returns captured stdout on success (the caller trims it, since `--update-version` is
/// specified to print only the bare version string).
fn run_script(application_name: &str, script: &str, args: &[&str]) -> Result<String> {
    let timestamp = SystemTime::now()
        .duration_since(UNIX_EPOCH)
        .unwrap_or_default()
        .as_secs();
    let sanitized_name: String = application_name
        .chars()
        .filter(|c| c.is_ascii_alphanumeric())
        .collect();

    let staging_dir = script_staging_dir();
    fs::create_dir_all(&staging_dir).with_context(|| format!("failed to create the script staging directory {}", staging_dir.display()))?;
    fs::set_permissions(&staging_dir, fs::Permissions::from_mode(0o700)).context("failed to lock down the script staging directory")?;

    let script_path = staging_dir.join(format!("kintsugi-upgrade-{sanitized_name}-{timestamp}.sh"));

    fs::write(&script_path, script).context("failed to write the script to its staging file")?;
    fs::set_permissions(&script_path, fs::Permissions::from_mode(0o700)).context("failed to make the script executable")?;

    let invocation = format!("{} {}", script_path.display(), args.join(" "));
    logging::info(&format!("running script for {application_name}: {invocation}"));

    let result = Command::new(&script_path)
        .args(args)
        // The same non-interactive, C-locale environment `os_update` runs its own package-manager
        // commands under: an AI-authored `--update` script is free to call `apt-get`/`dnf`, and
        // one that stops at a debconf prompt nothing is there to answer would hang the patch cycle.
        .env("DEBIAN_FRONTEND", "noninteractive")
        .env("LC_ALL", "C")
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

    fn status(name: &str, method: UpgradeMethod, update_available: bool) -> UpgradeStatus {
        UpgradeStatus {
            application_name: name.to_string(),
            installed_version: "1.0".to_string(),
            latest_version: Some("2.0".to_string()),
            update_available,
            method,
            application_identifier: Some("org.example.App".to_string()),
            command: None,
            notes: None,
            script: Some("#!/bin/bash\ntrue\n".to_string()),
            script_signature: Some("not-a-real-signature".to_string()),
            command_signature: None,
        }
    }

    /// A signature that can't be verified is exactly as unrunnable as no script at all — the
    /// property the whole signing chain rests on. Built with an identity holding an unparsable
    /// public key so verification fails for certain, whatever the signature bytes are.
    fn identity_that_verifies_nothing() -> AgentIdentity {
        AgentIdentity {
            certificate_pem: String::new(),
            private_key_pem: String::new(),
            artifact_signing_public_key_pem: "not a PEM-encoded key at all".to_string(),
        }
    }

    #[test]
    fn is_patchable_rejects_a_script_row_whose_signature_does_not_verify() {
        let identity = identity_that_verifies_nothing();

        assert!(!is_patchable(&status("Firefox", UpgradeMethod::Script, true), &identity));
    }

    #[test]
    fn is_patchable_rejects_a_row_with_no_update_available_before_looking_at_anything_else() {
        let identity = identity_that_verifies_nothing();

        assert!(!is_patchable(&status("Firefox", UpgradeMethod::Script, false), &identity));
    }

    #[test]
    fn is_patchable_rejects_methods_that_have_nothing_to_run() {
        let identity = identity_that_verifies_nothing();

        for method in [UpgradeMethod::Unknown, UpgradeMethod::DirectDownload, UpgradeMethod::ManualSteps] {
            assert!(!is_patchable(&status("Firefox", method, true), &identity));
        }
    }

    #[test]
    fn find_patchable_returns_nothing_when_no_row_survives_verification() {
        let identity = identity_that_verifies_nothing();
        let statuses = vec![status("Firefox", UpgradeMethod::Script, true)];

        assert!(find_patchable(&statuses, "Firefox", &identity).is_none());
    }

    #[test]
    fn patch_one_refuses_a_script_row_carrying_no_signature() {
        let mut status = status("Firefox", UpgradeMethod::Script, true);
        status.script_signature = None;

        let error = patch_one(&status, &identity_that_verifies_nothing()).unwrap_err();

        assert!(error.to_string().contains("no signature"), "unexpected error: {error}");
    }

    #[test]
    fn patch_one_refuses_a_script_row_whose_signature_does_not_verify() {
        let error = patch_one(&status("Firefox", UpgradeMethod::Script, true), &identity_that_verifies_nothing()).unwrap_err();

        assert!(
            error.to_string().contains("signature verification failed"),
            "unexpected error: {error}"
        );
    }

    #[test]
    fn patch_one_refuses_a_method_with_nothing_runnable() {
        let error = patch_one(&status("Firefox", UpgradeMethod::ManualSteps, true), &identity_that_verifies_nothing()).unwrap_err();

        assert!(error.to_string().contains("no runnable upgrade action"), "unexpected error: {error}");
    }

    #[test]
    fn truncate_for_log_leaves_short_output_alone() {
        assert_eq!(truncate_for_log("  hello\n"), "hello");
    }

    #[test]
    fn truncate_for_log_caps_runaway_output_without_splitting_a_character() {
        // A multi-byte character straddling the cap is the case a plain byte slice would panic on.
        let text = "é".repeat(MAX_LOGGED_OUTPUT_BYTES);

        let truncated = truncate_for_log(&text);

        assert!(truncated.contains("truncated"));
        assert!(truncated.len() < text.len());
    }
}
