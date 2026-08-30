use std::fs;
use std::process::Command;
use std::time::{SystemTime, UNIX_EPOCH};

use anyhow::{Context, Result};
use serde::{Deserialize, Serialize};

use crate::config::Config;
use crate::identity::{self, AgentIdentity};
use crate::logging;
use crate::os_update::POWERSHELL;

/// Mirrors the backend's `UpgradeStatusDto` — see
/// Kintsugi.Application/UpgradePaths/UpgradeStatusDto.cs. `latestVersion`/`updateAvailable` are
/// trusted straight from the backend: the server runs each script's own `--update-version` mode
/// itself (under `pwsh` for a Windows row — see ScriptLanguages), so its answer is the
/// authoritative one. Fields this agent has no use for (hostname, serialNumber, sourceUrl,
/// checkedUtc) stay omitted, since serde only requires the fields actually named here to be present.
#[derive(Debug, Clone, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct UpgradeStatus {
    pub application_name: String,
    #[allow(dead_code)]
    pub installed_version: String,
    pub latest_version: Option<String>,
    pub update_available: bool,
    pub method: UpgradeMethod,
    /// The application's platform identifier — its uninstall-registry key name, or a
    /// winget/Chocolatey package id. Required to invoke `script` at all, since every script's CLI
    /// contract takes `--appId` and addresses the application by it.
    pub application_identifier: Option<String>,
    /// The package-manager command for a `Method::PackageManagerCommand` row — run as-is via
    /// PowerShell. Only ever populated for an unrecognized package manager's legacy row now — a
    /// recognized one (winget, Chocolatey) gets a `Method::Script` row instead, see
    /// PackageManagerCatalog.
    pub command: Option<String>,
    #[allow(dead_code)]
    pub notes: Option<String>,
    /// A PowerShell script implementing a durable `--update-version` / `--update` CLI for a
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

/// Mirrors Kintsugi.Domain.Enums.UpgradeMethod. Deserializes from the backend's plain enum member
/// names (`"Script"`, `"PackageManagerCommand"`, ...), which is `serde`'s default for a unit-only
/// enum — unlike `policy::TimeUnit`, no manual ordinal mapping is needed here.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Deserialize)]
pub enum UpgradeMethod {
    Unknown,
    DirectDownload,
    PackageManagerCommand,
    ManualSteps,
    Script,
}

/// Fetches this host's known upgrade paths from the backend. Only ever called from the SYSTEM
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

    response.json::<Vec<UpgradeStatus>>().context("could not parse response")
}

#[derive(Debug, Serialize)]
#[serde(rename_all = "camelCase")]
struct ReportPatchResultRequest<'a> {
    serial_number: &'a str,
    application_name: &'a str,
    new_version: &'a str,
}

/// Tells the server this application was just successfully patched to `new_version`, so its record
/// of what's installed reflects that immediately rather than waiting on this host's next full
/// inventory report. Best-effort: the patch itself already succeeded locally by the time this is
/// called, so a failure here is only logged — there is nothing to roll back, and the next full
/// inventory report reconciles the server's record regardless.
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
/// cycle will actually work through (and size its progress bar against), rather than counting e.g.
/// an unresolved or manual-steps-only path that can never advance it. A `Script` or
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

/// Runs the actual update for one already-selected (`is_patchable`) application: a package-manager
/// command for `Method::PackageManagerCommand`, or the generated script's `--update` mode for
/// `Method::Script`. Re-verifies the signature right before running it, rather than trusting that
/// the caller already checked via `is_patchable` — the one function that actually executes something
/// is the one that shouldn't ever skip that check, even if every current caller happens to call it
/// correctly.
///
/// Runs as SYSTEM, from the service. Unlike the macOS agent — where the patch runs as the logged-in
/// user precisely because Homebrew refuses to run as root — everything a Windows upgrade does
/// (writing under `%ProgramFiles%`, `msiexec /qn`, `winget`, `choco`) requires elevation, so there
/// is no unprivileged option to prefer. See `queue`.
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
    logging::info(&format!("running command: powershell -Command {command:?}"));

    let output = Command::new(POWERSHELL)
        .args(["-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-Command", command])
        .output()
        .context("failed to run command")?;

    log_output("command", command, &output);

    if !output.status.success() {
        anyhow::bail!("exited with {}: {}", output.status, String::from_utf8_lossy(&output.stderr).trim());
    }

    Ok(())
}

/// Logs the exit status and full stdout/stderr of a completed command/script — regardless of
/// whether it succeeded, since even a "successful" run's output can matter for diagnosing something
/// that looked fine but wasn't. Each stream is capped defensively (a runaway script printing
/// megabytes shouldn't be able to blow out the log file) rather than left unbounded.
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

    // Back up to the nearest char boundary — a plain byte-index slice could otherwise land inside a
    // multi-byte UTF-8 character and panic.
    let mut cut = MAX_LOGGED_OUTPUT_BYTES;
    while !trimmed.is_char_boundary(cut) {
        cut -= 1;
    }

    format!("{}\n... [truncated, {} more byte(s)]", &trimmed[..cut], trimmed.len() - cut)
}

/// A UTF-8 byte-order mark, written ahead of every script this agent puts on disk.
///
/// Without it, Windows PowerShell 5.1 decodes a `.ps1` file using the system ANSI code page rather
/// than UTF-8 — so any non-ASCII character an AI-authored script happens to contain (a vendor name,
/// an em dash in a comment, a non-Latin path) arrives mangled, and the script either misbehaves or
/// fails to parse. The BOM is what tells it to decode as UTF-8. The scripts the server writes
/// itself are kept ASCII-only for the same reason (see `PowerShellUpgradeScript`), but nothing can
/// guarantee that of a generated one.
const UTF8_BOM: &[u8] = &[0xEF, 0xBB, 0xBF];

/// Writes `script` to a private temp file, runs it with `args` under Windows PowerShell, and removes
/// it afterward regardless of outcome — an AI-generated script left lying around is unnecessary
/// exposure once it's done running. Returns captured stdout on success.
fn run_script(application_name: &str, script: &str, args: &[&str]) -> Result<String> {
    let timestamp = SystemTime::now().duration_since(UNIX_EPOCH).unwrap_or_default().as_secs();
    let sanitized_name: String = application_name.chars().filter(|c| c.is_ascii_alphanumeric()).collect();
    // The .ps1 extension is required, not cosmetic: PowerShell refuses to run a script file with
    // any other extension, whether via -File or by invoking the path.
    let script_path = std::env::temp_dir().join(format!("kintsugi-upgrade-{sanitized_name}-{timestamp}.ps1"));

    let mut contents = UTF8_BOM.to_vec();
    contents.extend_from_slice(script.as_bytes());
    fs::write(&script_path, contents).context("failed to write script to a temp file")?;

    let invocation = format!("{} {}", script_path.display(), args.join(" "));
    logging::info(&format!("running script for {application_name}: {invocation}"));

    // -ExecutionPolicy Bypass because the script is signed by *this system's* artifact-signing key,
    // already verified above — Authenticode, which the execution policy actually checks, has
    // nothing to say about it, and a machine policy of AllSigned would otherwise block every
    // upgrade. -File rather than -Command so the script's own `exit <code>` becomes PowerShell's
    // exit code, which is what the success check below reads.
    let result = Command::new(POWERSHELL)
        .args(["-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File"])
        .arg(&script_path)
        .args(args)
        .output()
        .context("failed to execute script");

    let _ = fs::remove_file(&script_path);

    let output = result?;

    log_output(&format!("script for {application_name}"), &invocation, &output);

    if !output.status.success() {
        anyhow::bail!("exited with {}: {}", output.status, String::from_utf8_lossy(&output.stderr).trim());
    }

    Ok(String::from_utf8_lossy(&output.stdout).to_string())
}

#[cfg(test)]
mod tests {
    use super::*;
    use p256::ecdsa::signature::Signer;
    use p256::ecdsa::{Signature, SigningKey};
    use p256::pkcs8::EncodePublicKey;

    fn signing_key() -> SigningKey {
        SigningKey::from_bytes(&[0x11; 32].into()).expect("32 fixed bytes is always a valid P-256 scalar")
    }

    fn identity_for(key: &SigningKey) -> AgentIdentity {
        AgentIdentity {
            certificate_pem: String::new(),
            private_key_pem: String::new(),
            artifact_signing_public_key_pem: key
                .verifying_key()
                .to_public_key_pem(Default::default())
                .expect("encoding a P-256 public key to PEM never fails"),
        }
    }

    fn sign(key: &SigningKey, content: &str) -> String {
        use base64::Engine;
        let signature: Signature = key.sign(content.as_bytes());
        base64::engine::general_purpose::STANDARD.encode(signature.to_der().as_bytes())
    }

    fn script_status(script: Option<&str>, signature: Option<String>, application_identifier: Option<&str>) -> UpgradeStatus {
        UpgradeStatus {
            application_name: "Firefox".to_string(),
            installed_version: "153.0".to_string(),
            latest_version: Some("154.0.1".to_string()),
            update_available: true,
            method: UpgradeMethod::Script,
            application_identifier: application_identifier.map(str::to_string),
            command: None,
            notes: None,
            script: script.map(str::to_string),
            script_signature: signature,
            command_signature: None,
        }
    }

    #[test]
    fn is_patchable_accepts_a_properly_signed_script_row() {
        let key = signing_key();
        let script = "Set-StrictMode -Version Latest\n";
        let status = script_status(Some(script), Some(sign(&key, script)), Some("Mozilla Firefox"));

        assert!(is_patchable(&status, &identity_for(&key)));
    }

    #[test]
    fn is_patchable_rejects_a_script_row_with_no_signature() {
        // A row written straight to the database, bypassing the server's signing step, must never
        // be runnable — this is the gate that enforces it.
        let key = signing_key();
        let status = script_status(Some("Set-StrictMode -Version Latest\n"), None, Some("Mozilla Firefox"));

        assert!(!is_patchable(&status, &identity_for(&key)));
    }

    #[test]
    fn is_patchable_rejects_a_script_signed_by_a_different_key() {
        let script = "Set-StrictMode -Version Latest\n";
        let other_key = SigningKey::from_bytes(&[0x22; 32].into()).unwrap();
        let status = script_status(Some(script), Some(sign(&other_key, script)), Some("Mozilla Firefox"));

        assert!(!is_patchable(&status, &identity_for(&signing_key())));
    }

    #[test]
    fn is_patchable_rejects_a_script_row_with_no_application_identifier() {
        // Every script's CLI contract requires --appId, and the script uses it to confirm it's
        // acting on the right application before touching anything.
        let key = signing_key();
        let script = "Set-StrictMode -Version Latest\n";
        let status = script_status(Some(script), Some(sign(&key, script)), None);

        assert!(!is_patchable(&status, &identity_for(&key)));
    }

    #[test]
    fn is_patchable_rejects_a_row_with_no_update_available() {
        let key = signing_key();
        let script = "Set-StrictMode -Version Latest\n";
        let mut status = script_status(Some(script), Some(sign(&key, script)), Some("Mozilla Firefox"));
        status.update_available = false;

        assert!(!is_patchable(&status, &identity_for(&key)));
    }

    #[test]
    fn is_patchable_rejects_a_method_with_nothing_to_run() {
        let key = signing_key();
        for method in [UpgradeMethod::Unknown, UpgradeMethod::DirectDownload, UpgradeMethod::ManualSteps] {
            let mut status = script_status(Some("x"), Some(sign(&key, "x")), Some("id"));
            status.method = method;
            assert!(!is_patchable(&status, &identity_for(&key)), "{method:?} should not be patchable");
        }
    }

    #[test]
    fn upgrade_method_deserializes_from_the_backends_member_names() {
        // These arrive as plain enum names from UpgradeStatusDto; a mismatch would silently make
        // every row unpatchable rather than failing loudly.
        let parsed: Vec<UpgradeMethod> =
            serde_json::from_str(r#"["Unknown","DirectDownload","PackageManagerCommand","ManualSteps","Script"]"#).unwrap();

        assert_eq!(
            parsed,
            vec![
                UpgradeMethod::Unknown,
                UpgradeMethod::DirectDownload,
                UpgradeMethod::PackageManagerCommand,
                UpgradeMethod::ManualSteps,
                UpgradeMethod::Script,
            ]
        );
    }

    #[test]
    fn upgrade_status_deserializes_from_the_backends_camel_case_dto() {
        // Hand-mirrored from UpgradeStatusDto — a renamed field on that side surfaces here as a
        // parse failure, so this pins the shape the agent actually depends on.
        let json = r#"{
            "applicationName": "Firefox",
            "hostname": "pc-1",
            "serialNumber": "SERIAL-1",
            "installedVersion": "153.0",
            "latestVersion": "154.0.1",
            "updateAvailable": true,
            "status": "Found",
            "method": "Script",
            "downloadUrl": null,
            "command": null,
            "instructions": null,
            "sourceUrl": null,
            "notes": null,
            "checkedUtc": "2026-08-30T00:00:00+00:00",
            "script": "Set-StrictMode -Version Latest",
            "applicationIdentifier": "Mozilla Firefox",
            "scriptSignature": "AAAA",
            "commandSignature": null
        }"#;

        let status: UpgradeStatus = serde_json::from_str(json).unwrap();

        assert_eq!(status.application_name, "Firefox");
        assert_eq!(status.method, UpgradeMethod::Script);
        assert_eq!(status.application_identifier.as_deref(), Some("Mozilla Firefox"));
        assert_eq!(status.script_signature.as_deref(), Some("AAAA"));
    }

    #[test]
    fn truncate_for_log_leaves_short_output_alone() {
        assert_eq!(truncate_for_log("  done  "), "done");
    }

    #[test]
    fn truncate_for_log_caps_runaway_output_without_splitting_a_character() {
        let text = "é".repeat(MAX_LOGGED_OUTPUT_BYTES);

        let truncated = truncate_for_log(&text);

        assert!(truncated.contains("truncated"));
        // The assertion that matters: it's still valid UTF-8 and didn't panic on a byte index
        // landing inside a two-byte character.
        assert!(truncated.starts_with('é'));
    }
}
