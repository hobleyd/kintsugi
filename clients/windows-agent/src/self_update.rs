use std::fs;
use std::path::{Path, PathBuf};
use std::process::{Command, Stdio};
use std::time::{Duration, Instant};

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

/// What one `check_and_apply` pass did, so the caller can log it accurately.
enum Outcome {
    /// This build is the published one; nothing to do.
    UpToDate,
    /// A newer build was downloaded, verified and installed, and the restart handed off.
    Installed,
    /// A newer build was installed at an *earlier* check-in but this process is still the old one
    /// — the restart was handed off again. See `pending_restart`.
    RestartReissued,
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
        Ok(Outcome::Installed) => logging::info("self-update applied successfully"),
        Ok(Outcome::RestartReissued) => logging::info("handed off the outstanding service restart again"),
        Ok(Outcome::UpToDate) => {}
        Err(err) => logging::warn(&format!("self-update check failed, will retry at the next check-in: {err:#}")),
    }
}

fn check_and_apply_inner(client: &reqwest::blocking::Client, config: &Config, identity: &AgentIdentity, current_version: &str) -> Result<Outcome> {
    // Before asking the server anything: if an earlier check-in already put a newer build on disk,
    // the only useful thing this process can do is get out of its way. Re-downloading would fail
    // regardless — `replace_running_binary` cannot move a `.exe.old` that this very process is
    // still executing — and it did, once an hour for days, on a host whose restart helper never
    // ran, while the Hosts screen went on showing the old version. Whatever the server publishes
    // in the meantime is the *new* process's business, on its own first check-in.
    if let Some(installed_version) = pending_restart(&config::self_update_restart_marker_path()) {
        logging::warn(&format!(
            "kintsugi-agent {installed_version} was installed at an earlier check-in, but this service is still \
             running {current_version} — the restart never happened; handing it off again"
        ));
        restart_service();
        return Ok(Outcome::RestartReissued);
    }

    let info = fetch_latest(client, config)?;

    if !needs_update(current_version, &info.version) {
        return Ok(Outcome::UpToDate);
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

    // From here on the file at the installed path is no longer the program running this code, and
    // the marker is what lets the next check-in tell — see `pending_restart`. Written before the
    // hand-off so a helper that dies immediately still leaves the retry armed.
    if let Err(err) = record_pending_restart(&config::self_update_restart_marker_path(), &info.version) {
        logging::warn(&format!("could not record the pending service restart: {err}"));
    }

    restart_both_halves();

    Ok(Outcome::Installed)
}

/// Records that a newer build has been installed over this process's binary and the service is
/// waiting to be restarted onto it; the content is the version installed, for the log line that
/// reports a restart that never came.
fn record_pending_restart(marker: &Path, installed_version: &str) -> std::io::Result<()> {
    fs::write(marker, installed_version)
}

/// The version an earlier check-in installed, if the service has not been restarted since.
///
/// The marker's existence is the whole test — no version comparison against this process, and no
/// probing of `kintsugi-agent.exe.old` for a lock. `clean_up_previous_update` deletes the marker on
/// every service start, so a process that finds it at check-in time was already running when the
/// marker was written: it is the displaced build, by construction. A lock probe on `.exe.old` would
/// misread an antivirus scanner holding the old file open as "still running the old build" and
/// restart a perfectly current service once an hour. The content is only for the log, so a marker
/// whose write was cut short still counts.
fn pending_restart(marker: &Path) -> Option<String> {
    let version = fs::read_to_string(marker).ok()?;
    let version = version.trim();
    Some(if version.is_empty() { "(version not recorded)".to_string() } else { version.to_string() })
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

/// The argument the service passes to the freshly installed binary to make it act as the restart
/// helper — matched in `main`, which dispatches to [`run_restart_helper`]. One constant so the two
/// cannot drift: a helper started under a name `main` does not recognise would run as a service
/// entry point, fail to reach the SCM, and exit — with the old process left running exactly as if
/// no helper had been spawned at all.
pub const RESTART_HELPER_ARGUMENT: &str = "--restart-service";

/// How long the helper gives the displaced service to stop. Generous on purpose: the service winds
/// up whatever it is doing rather than being killed (see the stop handler in `main`), and what it
/// is doing may be a queued Windows update install. Nothing is waiting on the helper except the
/// helper, so a long wait costs nothing; exceeding it means the retry in `check_and_apply` runs.
const STOP_TIMEOUT: Duration = Duration::from_secs(30 * 60);

/// The service reports `Running` as soon as its control handler is registered, so a start that
/// takes longer than this is a start that has failed.
const START_TIMEOUT: Duration = Duration::from_secs(60);

/// Restarts this service, from a helper process that outlives it.
///
/// This cannot be done inline for the same reason the macOS agent can't reload its own LaunchDaemon
/// inline (see that agent's `checkin_schedule::reload_launchd`): stopping the service kills the
/// process executing the stop, with no guarantee execution ever reaches the start that follows —
/// which would leave the agent stopped until someone noticed. So the stop and start are performed
/// from outside, by the *newly installed* binary running as [`run_restart_helper`].
///
/// It used to be a detached PowerShell running `Restart-Service -Force -ErrorAction
/// SilentlyContinue`, and that shipped a host stuck on 0.5.2 for days with a 0.7.0 binary beside it:
/// the helper was spawned, the service never received a stop, and `SilentlyContinue` had made sure
/// nothing said why. Two things about it were wrong independently. A process created with
/// `DETACHED_PROCESS` has no console and null standard handles, which Windows PowerShell's console
/// host does not reliably survive — the WUA and CIM scripts this service runs work because
/// `Command::output` gives them pipes. And `Restart-Service` waits exactly two seconds for
/// `Stopped`, then gives up unless the service is reporting `StopPending`, and on giving up never
/// calls `Start` (PowerShell's `Service.cs`, `DoWaitForStatus`) — this service polls its shutdown
/// flag every two seconds. The helper here needs no console, talks to the SCM directly, waits as
/// long as the service needs, and logs every step to the service's own log.
fn restart_service() {
    use std::os::windows::process::CommandExt;
    use windows_sys::Win32::System::Threading::{CREATE_NEW_PROCESS_GROUP, DETACHED_PROCESS};

    let helper = config::installed_binary_path();
    logging::info(&format!("handing off a service restart to {} {RESTART_HELPER_ARGUMENT}", helper.display()));

    // spawn (not output): returns immediately rather than waiting for a process that is deliberately
    // going to outlive this one. DETACHED_PROCESS is safe for *this* program where it was not for
    // PowerShell: Rust discards writes to absent standard handles, and the helper's real output is
    // the log file. CREATE_NEW_PROCESS_GROUP keeps it out of this process's console group, the
    // counterpart to the macOS agent's `process_group(0)`.
    let spawned = Command::new(&helper)
        .arg(RESTART_HELPER_ARGUMENT)
        .stdin(Stdio::null())
        .stdout(Stdio::null())
        .stderr(Stdio::null())
        .creation_flags(DETACHED_PROCESS | CREATE_NEW_PROCESS_GROUP)
        .spawn();
    if let Err(err) = spawned {
        logging::warn(&format!("could not spawn the service restart helper: {err}"));
    }
}

/// The helper's whole job: stop the `KintsugiAgent` service, wait for it to actually stop, start
/// it again, and say what happened. Runs as SYSTEM, since it was spawned by the service.
///
/// The stop arrives while the displaced service is still finishing the check-in that spawned this
/// process. That is fine and intended — the service's control handler only sets a flag and reports
/// `StopPending`; the loop finishes the check-in, drains the queue once more, and exits — which is
/// why the wait below is bounded by what a check-in can take rather than by a fixed sleep.
pub fn run_restart_helper() -> Result<()> {
    use windows_service::service::{ServiceAccess, ServiceState};
    use windows_service::service_manager::{ServiceManager, ServiceManagerAccess};

    let manager = ServiceManager::local_computer(None::<&str>, ServiceManagerAccess::CONNECT)
        .context("could not connect to the Service Control Manager")?;
    let service = manager
        .open_service(config::SERVICE_NAME, ServiceAccess::QUERY_STATUS | ServiceAccess::STOP | ServiceAccess::START)
        .with_context(|| format!("could not open the {} service", config::SERVICE_NAME))?;

    let state = service.query_status().context("could not query the service status")?.current_state;
    if state != ServiceState::Stopped {
        logging::info(&format!("restart helper: asking the service to stop (currently {state:?})"));
        if let Err(err) = service.stop() {
            // Lost the race with a stop that was already under way — an administrator's, or the
            // service exiting on its own — which is the outcome wanted anyway.
            let now = service.query_status().context("could not query the service status")?.current_state;
            if now != ServiceState::Stopped && now != ServiceState::StopPending {
                return Err(err).context("could not ask the service to stop");
            }
        }
        let waited = wait_for_state(&service, ServiceState::Stopped, STOP_TIMEOUT)?;
        logging::info(&format!("restart helper: the service stopped after {}s", waited.as_secs()));
    }

    service.start::<&str>(&[]).context("could not start the service")?;
    wait_for_state(&service, ServiceState::Running, START_TIMEOUT)?;
    logging::info(&format!("restart helper: the service is running again (agent {})", env!("CARGO_PKG_VERSION")));

    Ok(())
}

/// Polls until the service reports `wanted`, returning how long that took; fails, naming the state
/// it was left in, once `timeout` has passed.
fn wait_for_state(service: &windows_service::service::Service, wanted: windows_service::service::ServiceState, timeout: Duration) -> Result<Duration> {
    let started = Instant::now();
    loop {
        let state = service.query_status().context("could not query the service status")?.current_state;
        if state == wanted {
            return Ok(started.elapsed());
        }
        if started.elapsed() >= timeout {
            anyhow::bail!("the service is still {state:?} after {}s", timeout.as_secs());
        }
        std::thread::sleep(Duration::from_secs(1));
    }
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

/// Deletes what the previous self-update left behind: the copy of the old binary that
/// `replace_running_binary` moved aside, and the pending-restart marker. Called once at service
/// startup, which is the first moment nothing can still be running the old binary — and the moment
/// that proves the restart the marker was waiting for has happened.
pub fn clean_up_previous_update() {
    let displaced_path = config::installed_binary_path().with_extension("exe.old");
    match fs::remove_file(&displaced_path) {
        Ok(()) => logging::info(&format!("removed the previous agent binary at {}", displaced_path.display())),
        Err(err) if err.kind() == std::io::ErrorKind::NotFound => {}
        Err(err) => logging::warn(&format!("could not remove {}: {err}", displaced_path.display())),
    }

    let marker = config::self_update_restart_marker_path();
    match fs::remove_file(&marker) {
        Ok(()) => logging::info("the service has restarted onto the build the previous self-update installed"),
        Err(err) if err.kind() == std::io::ErrorKind::NotFound => {}
        Err(err) => logging::warn(&format!("could not remove {}: {err}", marker.display())),
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

    #[test]
    fn a_recorded_pending_restart_names_the_installed_version_until_it_is_cleared() {
        // The retry in check_and_apply keys on this alone: a marker that did not survive to the next
        // check-in, or read back empty, would leave a never-restarted service re-downloading the
        // same package once an hour forever.
        let marker = std::env::temp_dir().join(format!("kintsugi-restart-marker-{}", std::process::id()));
        let _ = fs::remove_file(&marker);
        assert_eq!(pending_restart(&marker), None);

        record_pending_restart(&marker, "0.7.3").unwrap();
        assert_eq!(pending_restart(&marker), Some("0.7.3".to_string()));

        fs::remove_file(&marker).unwrap();
        assert_eq!(pending_restart(&marker), None);
    }

    #[test]
    fn an_empty_marker_is_still_a_pending_restart() {
        // Existence is the signal; the version is only for the log. A write cut short before any
        // bytes landed still means this process was displaced, and treating it as "nothing pending"
        // would recreate the very wedge the marker exists to break.
        let marker = std::env::temp_dir().join(format!("kintsugi-restart-marker-empty-{}", std::process::id()));
        fs::write(&marker, b"  \n").unwrap();

        assert_eq!(pending_restart(&marker), Some("(version not recorded)".to_string()));

        let _ = fs::remove_file(&marker);
    }
}
