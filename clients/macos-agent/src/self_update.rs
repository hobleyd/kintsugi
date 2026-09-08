use std::fs;
use std::os::unix::fs::{MetadataExt, PermissionsExt};
use std::path::{Path, PathBuf};
use std::process::Command;
use std::time::Duration;

use anyhow::{Context, Result};
use serde::Deserialize;

use crate::config::{self, Config};
use crate::identity::{self, AgentIdentity};
use crate::logging;

const PLATFORM: &str = "macos";

/// Longer than the daemon's usual 15s HTTP timeout (see `main::run_daemon`) — that's sized for the
/// small, fast host/application registration calls this shares a client with; downloading a whole
/// package needs enough headroom for a slow link, not just a slow server.
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
/// restarts both launchd jobs so the update actually takes effect. Called once at the end of every
/// root daemon check-in (see `main::run_daemon`) — there's no policy/schedule gating this the way
/// application patching is; a self-update always applies immediately, the moment it's noticed.
///
/// Best-effort throughout: any failure (server unreachable, checksum/signature mismatch, ...) is
/// logged and swallowed rather than propagated — a self-update failing should never make it look
/// like the rest of this check-in (host/application registration, the request queue) didn't
/// already succeed.
pub fn check_and_apply(
    client: &reqwest::blocking::Client,
    config: &Config,
    identity: Option<&AgentIdentity>,
    current_version: &str,
) {
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

fn check_and_apply_inner(
    client: &reqwest::blocking::Client,
    config: &Config,
    identity: &AgentIdentity,
    current_version: &str,
) -> Result<bool> {
    let info = fetch_latest(client, config)?;

    if !needs_update(current_version, &info.version) {
        return Ok(false);
    }

    logging::info(&format!("self-update available: {current_version} -> {}", info.version));

    // The gate between "the server said this is the current build" and actually installing it —
    // same principle as `upgrade::is_patchable` verifying a Script/Command's signature before
    // running it. Signing the checksum (rather than the whole archive) reuses the existing
    // string-content signing path (see ArtifactSigningService.Sign) without needing new plumbing
    // on either end.
    identity::verify_artifact_signature(identity, &info.sha256, &info.sha256_signature)
        .context("refusing to trust the published package's checksum")?;

    let download_client =
        identity::build_client(DOWNLOAD_TIMEOUT, Some(identity)).context("failed to build a client for the package download")?;
    let downloaded_path = download_to_temp_file(&download_client, &config.agent_package_download_url(PLATFORM))?;

    let actual_sha256 = sha256_of_file(&downloaded_path)?;
    if !actual_sha256.eq_ignore_ascii_case(&info.sha256) {
        let _ = fs::remove_file(&downloaded_path);
        anyhow::bail!("downloaded package checksum {actual_sha256} does not match the signed checksum {}", info.sha256);
    }

    let install_result = install_binary(&downloaded_path);
    let _ = fs::remove_file(&downloaded_path);
    install_result?;

    restart_launchd_jobs();

    Ok(true)
}

fn needs_update(current_version: &str, latest_version: &str) -> bool {
    current_version != latest_version
}

fn fetch_latest(client: &reqwest::blocking::Client, config: &Config) -> Result<AgentPackageInfo> {
    let response = client
        .get(config.agent_package_latest_url(PLATFORM))
        .send()
        .context("request failed")?;

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

/// Shells out to `shasum` (a macOS builtin) rather than pulling in a hashing crate — the same
/// "reuse what the OS already provides" choice this agent already makes for `date` (see
/// `tray_menu::format_due`) and `tar`/`osascript` elsewhere.
fn sha256_of_file(path: &Path) -> Result<String> {
    let output = Command::new("shasum").arg("-a").arg("256").arg(path).output().context("failed to run shasum")?;

    if !output.status.success() {
        anyhow::bail!("shasum exited with {}", output.status);
    }

    parse_shasum_output(&String::from_utf8_lossy(&output.stdout)).context("shasum produced no output")
}

fn parse_shasum_output(stdout: &str) -> Option<String> {
    stdout.split_whitespace().next().map(|digest| digest.to_lowercase())
}

/// Extracts the downloaded tarball — the same full install bundle a human downloads from the
/// Clients page (binary + config.toml + plists + install/uninstall scripts, see
/// packaging/publish-release.sh) — and installs whatever it finds at `kintsugi-agent` at its top
/// level over this agent's own binary, ignoring everything else in the bundle except
/// `kintsugi-mas`, which is installed beside it when the archive carries one.
fn install_binary(downloaded_path: &Path) -> Result<()> {
    let extract_dir = std::env::temp_dir().join(format!("kintsugi-agent-update-extract-{}", std::process::id()));
    fs::create_dir_all(&extract_dir).context("failed to create a temp directory to extract the package into")?;

    let result = extract_and_install(downloaded_path, &extract_dir);
    let _ = fs::remove_dir_all(&extract_dir);
    result
}

fn extract_and_install(downloaded_path: &Path, extract_dir: &Path) -> Result<()> {
    // `--no-same-owner` is load-bearing, and its absence was invisible for months. Extracting as
    // root, tar restores the uid and gid *recorded in the archive* — which is whichever account
    // built the release, not root. Everything below then installs those files with `fs::copy`,
    // which on APFS is `fclonefileat` and clones the owner and the timestamps along with the
    // bytes, so a self-update quietly replaced three root-owned files with user-owned ones:
    // the daemon's own binary (a local user could then rewrite the code launchd runs as root),
    // `kintsugi-mas` (whose owner AppStoreUpgradeScript checks before executing, so App Store rows
    // stopped patching), and the remote-shell plist (which launchd refuses to load unless root owns
    // it, so terminal sessions were requested and never served). packaging/install.sh had it right
    // with `install -o root -g wheel`; every self-update since has undone that. `install_over`
    // asserts the ownership as well, because this flag only fixes hosts from here on.
    let output = Command::new("tar")
        .arg("--no-same-owner")
        .arg("-xzf")
        .arg(downloaded_path)
        .arg("-C")
        .arg(extract_dir)
        .output()
        .context("failed to run tar")?;

    if !output.status.success() {
        anyhow::bail!("tar exited with {}: {}", output.status, String::from_utf8_lossy(&output.stderr).trim());
    }

    let extracted_binary = extract_dir.join("kintsugi-agent");
    if !extracted_binary.is_file() {
        anyhow::bail!("the published package does not contain a kintsugi-agent binary at its top level");
    }

    // The agent's own `mas` (see `config::MAS_BINARY_NAME`) travels in the same archive and is
    // replaced together with the agent, which is what lets the server's AppStoreUpgradeScript assume
    // the two agree. Installed first: a host first installed from a release that predates it gains
    // App Store patching on its next self-update, with no reinstall — the same gap the Linux agent's
    // `restart_remote_control_unit` closes for its fourth unit. A release built without it (see
    // publish-release.sh, which warns loudly) leaves whatever copy is already installed in place
    // rather than removing it.
    let extracted_mas = extract_dir.join(config::MAS_BINARY_NAME);
    if extracted_mas.is_file() {
        install_over(&extracted_mas, &config::mas_binary_path())?;
    }

    // The remote-shell LaunchDaemon is deliberately *not* installed from here, though it travels in
    // this archive for packaging/install.sh's benefit. A self-update runs under the binary that is
    // already installed, so an update path can only ever install a job the *previous* release knew
    // about — which is why 0.9.5's own copy of this code reached no host that self-updated into it.
    // `remote_shell::install_job_if_absent`, on every root check-in, is what actually closes that
    // gap, and it carries the plist rather than reading it from here.

    install_over(&extracted_binary, &config::installed_binary_path())
}

/// Staged next to the final destination, then renamed over it — a same-filesystem rename is atomic,
/// so nothing (launchd included) ever observes a partially-written binary at the real path.
fn install_over(extracted: &Path, installed_path: &Path) -> Result<()> {
    let staged_path = installed_path.with_extension("new");
    fs::copy(extracted, &staged_path).with_context(|| format!("failed to stage the new {}", installed_path.display()))?;

    let mut permissions = fs::metadata(&staged_path).context("failed to read staged binary permissions")?.permissions();
    permissions.set_mode(0o755);
    fs::set_permissions(&staged_path, permissions).context("failed to make the staged binary executable")?;

    fs::rename(&staged_path, installed_path).with_context(|| format!("failed to install {}", installed_path.display()))?;

    // Ownership is asserted rather than assumed. `fs::copy` above is `fclonefileat` on APFS, which
    // clones the source's owner along with its bytes, and the source came out of a tarball that
    // records whoever built the release — so without this a self-update hands root's own binary to
    // a local account. See the `--no-same-owner` note in `extract_and_install`, which is the other
    // half of the same defect.
    chown_root_wheel(installed_path);

    logging::info(&format!("installed new binary at {}", installed_path.display()));
    Ok(())
}

/// `root:wheel` on a path, the ownership packaging/install.sh gives everything it installs.
///
/// Shelled out to for the same reason `remote_shell`'s `root:admin` twin is, and kept beside it in
/// spirit: one place per ownership this agent has to assert.
fn chown_root_wheel(path: &Path) {
    match Command::new("/usr/sbin/chown").arg("root:wheel").arg(path).output() {
        Ok(output) if output.status.success() => {}
        Ok(output) => logging::warn(&format!(
            "chown root:wheel {} exited with {}: {}",
            path.display(),
            output.status,
            String::from_utf8_lossy(&output.stderr).trim()
        )),
        Err(err) => logging::warn(&format!("failed to run chown on {}: {err}", path.display())),
    }
}

/// Puts `root:wheel` back on the two files this agent installs outside its own state directory, on
/// every root check-in.
///
/// The repair half of the `--no-same-owner` defect, and it is needed for the same reason
/// `remote_shell::ensure_job_installed` is: a fix that only applies to future self-updates leaves
/// every host that has *already* self-updated holding user-owned copies, and nothing re-runs the
/// installer. What it repairs is not cosmetic — `/usr/local/bin/kintsugi-agent` is executed as root
/// by launchd, so an account that owns it owns this host's root daemon, and `kintsugi-mas` is
/// refused outright by AppStoreUpgradeScript unless root owns it.
///
/// Silent when there is nothing to do, which is every check-in after the first.
pub fn repair_installed_ownership() {
    for path in [config::installed_binary_path(), config::mas_binary_path()] {
        let Ok(metadata) = fs::metadata(&path) else {
            continue;
        };
        if metadata.uid() == 0 && metadata.gid() == 0 {
            continue;
        }

        chown_root_wheel(&path);
        logging::info(&format!(
            "corrected the ownership of {} from uid {} gid {} to root:wheel",
            path.display(),
            metadata.uid(),
            metadata.gid()
        ));
    }
}

/// Restarts both launchd jobs that run this binary so the update actually takes effect: the root
/// daemon (this process) and, for whichever user is at the console right now, the per-user menu
/// bar agent. By the time this runs, `run_daemon` has already done its real work for this
/// check-in (registration, the request queue), so a kickstart failing here is logged, not fatal.
///
/// Order matters: `launchctl kickstart -k` on the daemon's own job (`system/...`) restarts the
/// very process running this code, so launchd can tear it down before it gets back from that call
/// — anything after it is not guaranteed to run at all. The UI job lives in a completely separate
/// `gui/<uid>` launchd domain and isn't affected by that, so it has to go first; the daemon
/// restarts itself last, once there's nothing left to do.
fn restart_launchd_jobs() {
    match console_user_uid() {
        Some(uid) => kickstart(&format!("gui/{uid}/{}", config::UI_LAUNCHD_LABEL)),
        None => logging::info("no console user logged in; the menu bar agent will pick up the update at next login"),
    }

    kickstart(&format!("system/{}", config::DAEMON_LAUNCHD_LABEL));
}

fn kickstart(target: &str) {
    logging::info(&format!("restarting {target} to pick up the new binary"));
    match Command::new("launchctl").arg("kickstart").arg("-k").arg(target).output() {
        Ok(output) if output.status.success() => {}
        Ok(output) => logging::warn(&format!(
            "launchctl kickstart -k {target} exited with {}: {}",
            output.status,
            String::from_utf8_lossy(&output.stderr).trim()
        )),
        Err(err) => logging::warn(&format!("failed to run launchctl kickstart -k {target}: {err}")),
    }
}

/// Mirrors packaging/install.sh's own `stat -f '%Su' /dev/console` / `id -u` pattern for finding
/// the currently logged-in console user, so the per-user menu bar agent can be restarted
/// immediately rather than only at next login.
fn console_user_uid() -> Option<u32> {
    let user_output = Command::new("stat").arg("-f").arg("%Su").arg("/dev/console").output().ok()?;
    if !user_output.status.success() {
        return None;
    }
    let username = String::from_utf8_lossy(&user_output.stdout).trim().to_string();
    if username.is_empty() || username == "root" {
        return None;
    }

    let uid_output = Command::new("id").arg("-u").arg(&username).output().ok()?;
    if !uid_output.status.success() {
        return None;
    }
    String::from_utf8_lossy(&uid_output.stdout).trim().parse().ok()
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
    fn parse_shasum_output_extracts_the_leading_hex_digest() {
        let stdout = "abcdef0123456789  kintsugi-agent-update-1234.tar.gz\n";
        assert_eq!(parse_shasum_output(stdout), Some("abcdef0123456789".to_string()));
    }

    #[test]
    fn parse_shasum_output_lowercases_the_digest() {
        assert_eq!(parse_shasum_output("ABCDEF"), Some("abcdef".to_string()));
    }

    #[test]
    fn parse_shasum_output_returns_none_for_empty_output() {
        assert_eq!(parse_shasum_output(""), None);
    }
}
