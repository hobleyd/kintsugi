use std::fs;
use std::path::{Path, PathBuf};
use std::process::Command;
use std::time::Duration;

use anyhow::{Context, Result};
use serde::Deserialize;
use sha2::{Digest, Sha256};

use crate::config::{self, Config};
use crate::identity::{self, AgentIdentity};
use crate::logging;

/// The agent-package platform this build publishes and downloads under. Deliberately a separate
/// namespace from `PlatformBucket`'s upgrade-path buckets on the server — this one names a build of
/// this agent, not an operating system family whose applications share upgrade paths.
const PLATFORM: &str = "windows";

/// Longer than the service's usual 15s HTTP timeout — that's sized for the small, fast
/// host/application registration calls this shares a client with; downloading a whole package needs
/// enough headroom for a slow link, not just a slow server.
const DOWNLOAD_TIMEOUT: Duration = Duration::from_secs(120);

/// Mirrors the backend's `AgentPackageDto` — see
/// Kintsugi.Application/AgentPackages/AgentPackageDto.cs. Fields this agent has no use for
/// (fileSizeBytes, releaseNotes, publishedUtc) stay omitted, since serde only requires the fields
/// actually named here to be present.
#[derive(Debug, Clone, Deserialize)]
#[serde(rename_all = "camelCase")]
struct AgentPackageInfo {
    version: String,
    sha256: String,
    sha256_signature: String,
}

/// Checks whether a newer kintsugi-agent build than `current_version` has been published for this
/// platform, and — if so — downloads, verifies, and installs it over this agent's own binary, then
/// restarts both halves so the update actually takes effect. Called once at the end of every
/// check-in (see `service`) — there's no policy/schedule gating this the way application patching
/// is; a self-update always applies immediately, the moment it's noticed.
///
/// Best-effort throughout: any failure (server unreachable, checksum/signature mismatch, ...) is
/// logged and swallowed rather than propagated — a self-update failing should never make it look
/// like the rest of this check-in (host/application registration, the queue) didn't already succeed.
pub fn check_and_apply(client: &reqwest::blocking::Client, config: &Config, identity: Option<&AgentIdentity>, current_version: &str) {
    let Some(identity) = identity else {
        logging::info("skipping self-update check: no enrolled agent identity yet");
        return;
    };

    match check_and_apply_inner(client, config, identity, current_version) {
        Ok(true) => logging::info("self-update applied successfully"),
        Ok(false) => {}
        Err(err) => logging::warn(&format!("self-update check failed, will retry at the next check-in: {err:#}")),
    }
}

fn check_and_apply_inner(client: &reqwest::blocking::Client, config: &Config, identity: &AgentIdentity, current_version: &str) -> Result<bool> {
    let info = fetch_latest(client, config)?;

    if !needs_update(current_version, &info.version) {
        return Ok(false);
    }

    logging::info(&format!("self-update available: {current_version} -> {}", info.version));

    // The gate between "the server said this is the current build" and actually installing it —
    // same principle as `upgrade::is_patchable` verifying a Script/Command's signature before
    // running it. Signing the checksum (rather than the whole archive) reuses the existing
    // string-content signing path (see ArtifactSigningService.Sign) without needing new plumbing on
    // either end.
    identity::verify_artifact_signature(identity, &info.sha256, &info.sha256_signature)
        .context("refusing to trust the published package's checksum")?;

    let download_client = identity::build_client(DOWNLOAD_TIMEOUT, Some(identity)).context("failed to build a client for the package download")?;
    let downloaded_path = download_to_temp_file(&download_client, &config.agent_package_download_url(PLATFORM))?;

    let actual_sha256 = sha256_of_file(&downloaded_path)?;
    if !actual_sha256.eq_ignore_ascii_case(&info.sha256) {
        let _ = fs::remove_file(&downloaded_path);
        anyhow::bail!("downloaded package checksum {actual_sha256} does not match the signed checksum {}", info.sha256);
    }

    let install_result = install_binary(&downloaded_path);
    let _ = fs::remove_file(&downloaded_path);
    install_result?;

    restart_both_halves();

    Ok(true)
}

fn needs_update(current_version: &str, latest_version: &str) -> bool {
    current_version != latest_version
}

fn fetch_latest(client: &reqwest::blocking::Client, config: &Config) -> Result<AgentPackageInfo> {
    let response = client.get(config.agent_package_latest_url(PLATFORM)).send().context("request failed")?;

    if response.status() == reqwest::StatusCode::NOT_FOUND {
        anyhow::bail!("no {PLATFORM} package has been published yet");
    }
    if !response.status().is_success() {
        anyhow::bail!("request rejected (HTTP {})", response.status());
    }

    response.json::<AgentPackageInfo>().context("could not parse response")
}

fn download_to_temp_file(client: &reqwest::blocking::Client, url: &str) -> Result<PathBuf> {
    let mut response = client.get(url).send().context("download request failed")?;
    if !response.status().is_success() {
        anyhow::bail!("download rejected (HTTP {})", response.status());
    }

    let path = std::env::temp_dir().join(format!("kintsugi-agent-update-{}.tar.gz", std::process::id()));
    let mut file = fs::File::create(&path).context("failed to create a temp file for the downloaded package")?;
    response.copy_to(&mut file).context("failed to write the downloaded package to disk")?;

    Ok(path)
}

/// Hashes in-process rather than shelling out. The macOS agent uses `shasum`, a builtin there;
/// Windows' nearest equivalent, `certutil -hashfile`, prints a three-line human-readable report
/// that has to be scraped, and a scrape that silently returns the wrong digest would defeat the
/// point of the check entirely.
fn sha256_of_file(path: &Path) -> Result<String> {
    let mut file = fs::File::open(path).with_context(|| format!("could not open {} to hash it", path.display()))?;
    let mut hasher = Sha256::new();
    std::io::copy(&mut file, &mut hasher).context("failed to read the downloaded package while hashing it")?;
    Ok(format!("{:x}", hasher.finalize()))
}

/// Extracts the downloaded tarball — the same full install bundle a human downloads from the
/// Clients page (binary + config.toml + install/uninstall scripts, see
/// packaging/publish-release.ps1) — and installs whatever it finds at `kintsugi-agent.exe` at its
/// top level over this agent's own binary, ignoring everything else in the bundle.
///
/// The package is a `.tar.gz` rather than a `.zip` for two reasons, neither cosmetic: the server's
/// `AgentPackageArchiveRewriter` reads and rewrites gzip-tar specifically (it substitutes the
/// current enrollment token into the archive's `config.toml` on every download), and `tar.exe` has
/// shipped in Windows since 10 1803, so extracting one needs nothing installed.
fn install_binary(downloaded_path: &Path) -> Result<()> {
    let extract_dir = std::env::temp_dir().join(format!("kintsugi-agent-update-extract-{}", std::process::id()));
    fs::create_dir_all(&extract_dir).context("failed to create a temp directory to extract the package into")?;

    let result = extract_and_install(downloaded_path, &extract_dir);
    let _ = fs::remove_dir_all(&extract_dir);
    result
}

fn extract_and_install(downloaded_path: &Path, extract_dir: &Path) -> Result<()> {
    let output = Command::new("tar")
        .arg("-xzf")
        .arg(downloaded_path)
        .arg("-C")
        .arg(extract_dir)
        .output()
        .context("failed to run tar")?;

    if !output.status.success() {
        anyhow::bail!("tar exited with {}: {}", output.status, String::from_utf8_lossy(&output.stderr).trim());
    }

    let extracted_binary = extract_dir.join("kintsugi-agent.exe");
    if !extracted_binary.is_file() {
        anyhow::bail!("the published package does not contain a kintsugi-agent.exe at its top level");
    }

    let installed_path = config::installed_binary_path();
    replace_running_binary(&extracted_binary, &installed_path)?;

    logging::info(&format!("installed new kintsugi-agent binary at {}", installed_path.display()));
    Ok(())
}

/// Puts `new_binary` in place of `installed_path` — which is the very executable running this code.
///
/// This is the one place a Windows agent genuinely cannot copy the macOS approach. There,
/// `self_update` stages the new binary next to the old one and renames it over the top, because a
/// same-filesystem rename is atomic and Unix is perfectly happy to unlink a file that a running
/// process has open. Windows holds a running image locked: renaming *onto* it fails outright.
///
/// What Windows does allow is renaming the running image *out of the way* — the lock follows the
/// file, not the path — so the sequence is: move the old binary aside, copy the new one into the
/// now-free path, and leave the displaced copy to be deleted once nothing is running it. That
/// leaves no window in which the path doesn't exist.
fn replace_running_binary(new_binary: &Path, installed_path: &Path) -> Result<()> {
    let displaced_path = installed_path.with_extension("exe.old");

    // A previous update's displaced copy, now that nothing is running it. Best-effort: if it's
    // somehow still locked, the rename below fails and the update is retried at the next check-in.
    let _ = fs::remove_file(&displaced_path);

    fs::rename(installed_path, &displaced_path)
        .with_context(|| format!("could not move the running binary aside to {}", displaced_path.display()))?;

    match fs::copy(new_binary, installed_path) {
        Ok(_) => Ok(()),
        Err(err) => {
            // Put it back. Leaving nothing at the installed path would break both halves of this
            // agent permanently — the service would fail to start and never get another chance to
            // fix itself.
            if let Err(restore_err) = fs::rename(&displaced_path, installed_path) {
                logging::error(&format!(
                    "could not install the new binary ({err}) AND could not restore the previous one ({restore_err}) — \
                     the agent binary is at {}",
                    displaced_path.display()
                ));
            }
            Err(err).context("failed to install the new binary")
        }
    }
}

/// Restarts both halves of the agent so the replaced binary actually takes effect: the per-user
/// tray process, then this service.
///
/// Order matters, exactly as it does on macOS: restarting the service restarts the very process
/// running this code, so anything after it is not guaranteed to run at all. The tray task is an
/// independent scheduled task and isn't affected by that, so it goes first; the service restarts
/// itself last, once there's nothing left to do.
fn restart_both_halves() {
    restart_ui_task();
    restart_service();
}

/// Ends and re-runs the logon-triggered task that hosts the tray process, so a logged-in user picks
/// up the new build immediately rather than at next logon.
///
/// `/Run` starts the task in the session of whichever user it's registered for; if nobody is logged
/// in there is nothing to restart, and the task fires on its own at the next logon.
fn restart_ui_task() {
    logging::info("restarting the per-user agent task to pick up the new binary");
    run_command("schtasks", &["/End", "/TN", config::UI_TASK_NAME]);
    run_command("schtasks", &["/Run", "/TN", config::UI_TASK_NAME]);
}

/// Restarts this service, from a detached helper process that outlives it.
///
/// This cannot be done inline for the same reason the macOS agent can't reload its own LaunchDaemon
/// inline (see that agent's `checkin_schedule::reload_launchd`): stopping the service kills the
/// process executing the stop, with no guarantee execution ever reaches the start that follows —
/// which would leave the agent stopped until someone noticed. So the restart is handed to a
/// short-lived PowerShell process that sleeps first, letting this check-in finish and this process
/// exit normally, then performs the stop and start from outside.
fn restart_service() {
    let service = config::SERVICE_NAME;
    let script = format!(
        "Start-Sleep -Seconds 5; Restart-Service -Name '{service}' -Force -ErrorAction SilentlyContinue"
    );

    logging::info("handing off a service restart to a detached helper");

    // spawn (not output): returns immediately rather than waiting for a process that is deliberately
    // going to outlive this one.
    match detached_powershell(&script).spawn() {
        Ok(_) => {}
        Err(err) => logging::warn(&format!("could not spawn the service restart helper: {err}")),
    }
}

/// Builds a PowerShell invocation detached from this process, so it survives this process exiting —
/// and, crucially, being *stopped by the SCM*, which terminates the service's whole process tree.
pub fn detached_powershell(script: &str) -> Command {
    use std::os::windows::process::CommandExt;
    use windows_sys::Win32::System::Threading::{CREATE_NEW_PROCESS_GROUP, CREATE_NO_WINDOW, DETACHED_PROCESS};

    let mut command = Command::new(crate::os_update::POWERSHELL);
    command
        .args(["-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-Command", script])
        // DETACHED_PROCESS + CREATE_NEW_PROCESS_GROUP is the Windows counterpart to the macOS
        // agent's `process_group(0)`: it takes the helper out of this process's group so the
        // cleanup that follows a service stop doesn't take it out too. CREATE_NO_WINDOW keeps a
        // console from flashing up in a logged-in user's face.
        .creation_flags(DETACHED_PROCESS | CREATE_NEW_PROCESS_GROUP | CREATE_NO_WINDOW);
    command
}

fn run_command(program: &str, args: &[&str]) {
    match Command::new(program).args(args).output() {
        Ok(output) if output.status.success() => {}
        Ok(output) => logging::warn(&format!(
            "{program} {} exited with {}: {}",
            args.join(" "),
            output.status,
            String::from_utf8_lossy(&output.stderr).trim()
        )),
        Err(err) => logging::warn(&format!("failed to run {program} {}: {err}", args.join(" "))),
    }
}

/// Deletes the copy of the previous binary that `replace_running_binary` moved aside. Called once at
/// service startup, which is the first moment nothing can still be running it.
pub fn clean_up_displaced_binary() {
    let displaced_path = config::installed_binary_path().with_extension("exe.old");
    match fs::remove_file(&displaced_path) {
        Ok(()) => logging::info(&format!("removed the previous agent binary at {}", displaced_path.display())),
        Err(err) if err.kind() == std::io::ErrorKind::NotFound => {}
        Err(err) => logging::warn(&format!("could not remove {}: {err}", displaced_path.display())),
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn needs_update_is_true_when_versions_differ() {
        assert!(needs_update("0.1.0", "0.2.0"));
    }

    #[test]
    fn needs_update_is_false_when_versions_match() {
        assert!(!needs_update("0.2.0", "0.2.0"));
    }

    #[test]
    fn sha256_of_file_matches_the_known_digest_of_its_contents() {
        // The published checksum is what the server signed, and this is the value compared against
        // it — a wrong digest here would either reject every genuine package or, worse, accept a
        // tampered one.
        let path = std::env::temp_dir().join(format!("kintsugi-sha256-test-{}.bin", std::process::id()));
        fs::write(&path, b"abc").unwrap();

        let digest = sha256_of_file(&path).unwrap();

        assert_eq!(digest, "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad");
        let _ = fs::remove_file(&path);
    }

    #[test]
    fn sha256_of_file_is_lowercase_hex() {
        // Compared with eq_ignore_ascii_case, but the log line that prints a mismatch reads better
        // when both sides are in the same case as the server's own value.
        let path = std::env::temp_dir().join(format!("kintsugi-sha256-case-{}.bin", std::process::id()));
        fs::write(&path, b"").unwrap();

        let digest = sha256_of_file(&path).unwrap();

        assert_eq!(digest, "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855");
        let _ = fs::remove_file(&path);
    }

    #[test]
    fn sha256_of_file_reports_a_missing_file_rather_than_returning_a_digest() {
        let result = sha256_of_file(Path::new(r"C:\this\does\not\exist.bin"));

        assert!(result.is_err());
    }

    #[test]
    fn replace_running_binary_leaves_the_previous_copy_aside_for_later_cleanup() {
        // The core of the Windows-specific dance: after the swap, the installed path holds the new
        // bytes and the old ones are parked at .exe.old rather than deleted (they may still be
        // mapped by the running process).
        let dir = std::env::temp_dir().join(format!("kintsugi-replace-test-{}", std::process::id()));
        let _ = fs::remove_dir_all(&dir);
        fs::create_dir_all(&dir).unwrap();

        let installed = dir.join("kintsugi-agent.exe");
        let new_binary = dir.join("new-kintsugi-agent.exe");
        fs::write(&installed, b"old").unwrap();
        fs::write(&new_binary, b"new").unwrap();

        replace_running_binary(&new_binary, &installed).unwrap();

        assert_eq!(fs::read(&installed).unwrap(), b"new");
        assert_eq!(fs::read(installed.with_extension("exe.old")).unwrap(), b"old");

        let _ = fs::remove_dir_all(&dir);
    }

    #[test]
    fn replace_running_binary_restores_the_previous_copy_when_the_new_one_cannot_be_installed() {
        // If this didn't restore, the installed path would be left empty — the service would fail
        // to start and never get another chance to repair itself.
        let dir = std::env::temp_dir().join(format!("kintsugi-replace-fail-{}", std::process::id()));
        let _ = fs::remove_dir_all(&dir);
        fs::create_dir_all(&dir).unwrap();

        let installed = dir.join("kintsugi-agent.exe");
        fs::write(&installed, b"old").unwrap();
        let missing_new_binary = dir.join("not-downloaded.exe");

        let result = replace_running_binary(&missing_new_binary, &installed);

        assert!(result.is_err());
        assert_eq!(fs::read(&installed).unwrap(), b"old");

        let _ = fs::remove_dir_all(&dir);
    }
}
