use std::fs;
use std::io::Read;
use std::os::unix::fs::PermissionsExt;
use std::path::{Path, PathBuf};
use std::process::Command;
use std::time::Duration;

use anyhow::{Context, Result};
use serde::Deserialize;
use sha2::{Digest, Sha256};

use crate::config::{self, Config};
use crate::identity::{self, AgentIdentity};
use crate::logging;

/// The agent-package platform namespace ("macos", "windows", "linux"), which is *not*
/// `PlatformBucket`'s upgrade-path namespace ("macOS", "Windows", "Linux", "pm:..."). They name
/// different things — see Kintsugi.Domain.Entities.AgentPackage.
const PLATFORM: &str = "linux";

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
/// restarts the per-user agents so the update actually takes effect. Called once at the end of
/// every root check-in (see `main::run_daemon`) — there's no policy/schedule gating this the way
/// application patching is; a self-update always applies immediately, the moment it's noticed.
///
/// Best-effort throughout: any failure (server unreachable, checksum/signature mismatch, ...) is
/// logged and swallowed rather than propagated — a self-update failing should never make it look
/// like the rest of this check-in (host/application registration, the queue) didn't already
/// succeed.
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

    restart_user_agents();

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

/// Downloads into the agent's own root-only state directory rather than `/tmp`, for the same
/// reason `upgrade::script_staging_dir` does: this runs as root, and a predictable path under a
/// world-writable directory is where a local user gets to point root's writes somewhere else via
/// a symlink. The macOS agent can use the temp directory because its equivalent code path is the
/// only one on that platform and `/tmp` there is per-user by default.
fn staging_dir() -> Result<PathBuf> {
    let dir = config::state_dir().join("updates");
    fs::create_dir_all(&dir).with_context(|| format!("failed to create the update staging directory {}", dir.display()))?;
    fs::set_permissions(&dir, fs::Permissions::from_mode(0o700)).context("failed to lock down the update staging directory")?;
    Ok(dir)
}

fn download_to_temp_file(client: &reqwest::blocking::Client, url: &str) -> Result<PathBuf> {
    let mut response = client.get(url).send().context("download request failed")?;
    if !response.status().is_success() {
        anyhow::bail!("download rejected (HTTP {})", response.status());
    }

    let path = staging_dir()?.join(format!("kintsugi-agent-update-{}.tar.gz", std::process::id()));
    let mut file = fs::File::create(&path).context("failed to create a staging file for the downloaded package")?;
    response.copy_to(&mut file).context("failed to write the downloaded package to disk")?;

    Ok(path)
}

/// Hashes in-process rather than shelling out to `sha256sum`.
///
/// The macOS agent calls `shasum`, which is part of that OS's base install and always present.
/// `sha256sum` is coreutils, which *almost* always is — and this agent has to keep working on a
/// minimal image or a busybox userland where it isn't, since the failure mode would be a silent
/// end to self-updates on exactly the hosts nobody is looking at. Same call the Windows agent
/// made about `certutil`, for a different reason.
fn sha256_of_file(path: &Path) -> Result<String> {
    let mut file = fs::File::open(path).with_context(|| format!("failed to open {}", path.display()))?;
    let mut hasher = Sha256::new();
    let mut buffer = [0u8; 64 * 1024];

    loop {
        let read = file.read(&mut buffer).context("failed to read the downloaded package")?;
        if read == 0 {
            break;
        }
        hasher.update(&buffer[..read]);
    }

    Ok(hex_lower(&hasher.finalize()))
}

fn hex_lower(bytes: &[u8]) -> String {
    bytes.iter().map(|byte| format!("{byte:02x}")).collect()
}

/// Extracts the downloaded tarball — the same full install bundle a human downloads from the
/// Clients page (binary + config.toml + systemd units + install/uninstall scripts, see
/// packaging/publish-release.sh) — and installs whatever it finds at `kintsugi-agent` at its top
/// level over this agent's own binary, ignoring everything else in the bundle.
fn install_binary(downloaded_path: &Path) -> Result<()> {
    let extract_dir = staging_dir()?.join(format!("extract-{}", std::process::id()));
    let _ = fs::remove_dir_all(&extract_dir);
    fs::create_dir_all(&extract_dir).context("failed to create a directory to extract the package into")?;

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

    let extracted_binary = extract_dir.join("kintsugi-agent");
    if !extracted_binary.is_file() {
        anyhow::bail!("the published package does not contain a kintsugi-agent binary at its top level");
    }

    let installed_path = config::installed_binary_path();

    // Staged next to the final destination, then renamed over it — a same-filesystem rename is
    // atomic, so nothing ever observes a partially-written binary at the real path, and Unix
    // happily unlinks a file some process is still executing. (The Windows agent has to rename the
    // *old* binary aside instead, because Windows locks a running image; this is the one place
    // where being a Unix makes Linux the macOS agent's twin rather than the Windows agent's.)
    let staged_path = installed_path.with_extension("new");
    fs::copy(&extracted_binary, &staged_path).context("failed to stage the new binary")?;
    fs::set_permissions(&staged_path, fs::Permissions::from_mode(0o755)).context("failed to make the staged binary executable")?;
    fs::rename(&staged_path, &installed_path).context("failed to install the new binary")?;

    logging::info(&format!("installed new kintsugi-agent binary at {}", installed_path.display()));
    Ok(())
}

/// Restarts the per-user agent for every user currently logged in, so the new binary takes effect
/// without waiting for them to log out and back in.
///
/// Nothing restarts the root half, and nothing needs to. On macOS the root job is a long-lived
/// launchd daemon that has to be kicked; here it is a systemd *oneshot* driven by a timer — this
/// very process is about to exit on its own, and the next firing execs whatever is at
/// `installed_binary_path` by then. Restarting it would mean `systemctl restart` on the unit
/// running this code, which is the self-kill hazard the macOS agent's detached-helper dance exists
/// to work around, for no benefit at all here.
fn restart_user_agents() {
    let users = logged_in_users();
    if users.is_empty() {
        logging::info("no users are logged in; the per-user agent will pick up the update at next login");
        return;
    }

    for (uid, username) in users {
        logging::info(&format!("restarting {} for {username} (uid {uid}) to pick up the new binary", config::UI_UNIT));
        run_user_systemctl(uid, &username, &["restart", config::UI_UNIT]);
    }
}

/// Runs `systemctl --user` as another user. A user manager listens on its own socket under
/// `/run/user/<uid>`, so `XDG_RUNTIME_DIR` has to be set explicitly — root's own environment
/// doesn't have one, and without it systemctl reports "Failed to connect to bus" rather than
/// doing anything.
///
/// Kept as a shared helper here (rather than duplicated the way the macOS agent duplicates its
/// console-user lookup) because `self_removal` needs the identical incantation and getting it
/// subtly different in the two places would mean the uninstall path leaving a process running.
pub fn run_user_systemctl(uid: u32, username: &str, args: &[&str]) {
    let Some(runuser) = ["/usr/sbin/runuser", "/sbin/runuser", "/usr/bin/runuser"]
        .iter()
        .map(Path::new)
        .find(|path| path.is_file())
    else {
        logging::warn("runuser is not installed; cannot reach the per-user agent's systemd manager");
        return;
    };

    let mut command = Command::new(runuser);
    command
        .arg("-u")
        .arg(username)
        .arg("--")
        .arg("systemctl")
        .arg("--user")
        .args(args)
        .env("XDG_RUNTIME_DIR", format!("/run/user/{uid}"));

    match command.output() {
        Ok(output) if output.status.success() => {}
        Ok(output) => logging::warn(&format!(
            "systemctl --user {} for {username} exited with {}: {}",
            args.join(" "),
            output.status,
            String::from_utf8_lossy(&output.stderr).trim()
        )),
        Err(err) => logging::warn(&format!("failed to run systemctl --user {} for {username}: {err}", args.join(" "))),
    }
}

/// Every user with a live login session, as (uid, username).
///
/// The Linux counterpart to the macOS agent's `console_user_uid`, and deliberately plural where
/// that one is singular: a Mac has one console user, while a Linux host can have several
/// simultaneous graphical and remote sessions, each with its own per-user agent.
pub fn logged_in_users() -> Vec<(u32, String)> {
    let output = Command::new("loginctl").args(["list-users", "--no-legend"]).output().ok();

    let Some(output) = output.filter(|output| output.status.success()) else {
        return Vec::new();
    };

    parse_loginctl_users(&String::from_utf8_lossy(&output.stdout))
}

/// Parses `loginctl list-users --no-legend`, whose rows begin "<uid> <username>". Later systemd
/// versions add further columns (linger state, session count); taking only the first two fields
/// is what keeps this working across both.
///
/// `root` is excluded: a root login is not a desktop session, and the per-user agent is never
/// installed for it (see packaging/install.sh, which enables the user unit globally for real
/// users). Including it would mean a `systemctl --user` call that can only fail.
fn parse_loginctl_users(stdout: &str) -> Vec<(u32, String)> {
    stdout
        .lines()
        .filter_map(|line| {
            let mut fields = line.split_whitespace();
            let uid: u32 = fields.next()?.parse().ok()?;
            let username = fields.next()?;
            (username != "root").then(|| (uid, username.to_string()))
        })
        .collect()
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

    /// The published checksum is lowercase hex; a mismatch in case alone must not look like a
    /// tampered download (the comparison itself is case-insensitive, but the digest this produces
    /// should be the canonical form regardless).
    #[test]
    fn sha256_of_file_produces_the_canonical_lowercase_digest() {
        let path = std::env::temp_dir().join(format!("kintsugi-sha256-test-{}", std::process::id()));
        fs::write(&path, b"abc").unwrap();

        // The published SHA-256 of "abc".
        assert_eq!(
            sha256_of_file(&path).unwrap(),
            "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad"
        );

        let _ = fs::remove_file(&path);
    }

    /// Larger than the read buffer, so the streaming loop is actually exercised rather than a
    /// single read.
    #[test]
    fn sha256_of_file_hashes_a_file_larger_than_its_read_buffer() {
        let path = std::env::temp_dir().join(format!("kintsugi-sha256-big-test-{}", std::process::id()));
        fs::write(&path, vec![0u8; 200 * 1024]).unwrap();

        let digest = sha256_of_file(&path).unwrap();

        assert_eq!(digest.len(), 64);
        assert!(digest.chars().all(|c| c.is_ascii_hexdigit() && !c.is_ascii_uppercase()));

        let _ = fs::remove_file(&path);
    }

    #[test]
    fn parse_loginctl_users_reads_uid_and_username() {
        let stdout = "1000 alice\n1001 bob\n";

        assert_eq!(
            parse_loginctl_users(stdout),
            vec![(1000, "alice".to_string()), (1001, "bob".to_string())]
        );
    }

    #[test]
    fn parse_loginctl_users_tolerates_the_extra_columns_newer_systemd_prints() {
        let stdout = "1000 alice no  1\n1001 bob   yes 2\n";

        assert_eq!(
            parse_loginctl_users(stdout),
            vec![(1000, "alice".to_string()), (1001, "bob".to_string())]
        );
    }

    #[test]
    fn parse_loginctl_users_skips_root() {
        assert_eq!(parse_loginctl_users("0 root\n1000 alice\n"), vec![(1000, "alice".to_string())]);
    }

    #[test]
    fn parse_loginctl_users_returns_nothing_for_an_empty_listing() {
        assert!(parse_loginctl_users("").is_empty());
    }
}
