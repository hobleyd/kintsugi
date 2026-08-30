use std::ffi::CStr;
use std::path::{Path, PathBuf};

use anyhow::{Context, Result};
use serde::Deserialize;

/// Default backend base URL, used when no config file or environment
/// override is present. This will change once the backend has a stable,
/// non-development address.
const DEFAULT_API_BASE_URL: &str = "https://kintsugi.example.com:8443";

/// Configuration and mutable state live in two different places here, where the macOS agent keeps
/// both under one `/Library/Application Support/kintsugi-agent`. That's the FHS: `/etc` is for
/// configuration a human (or a config-management tool) edits, `/var/lib` is for state a program
/// owns and rewrites. Keeping them apart is also what lets the packaging mark `/etc` as
/// configuration and leave it alone on an upgrade while `/var/lib` — the enrolled identity in
/// particular — survives untouched.
const CONFIG_PATH: &str = "/etc/kintsugi-agent/config.toml";
const CONFIG_DIR: &str = "/etc/kintsugi-agent";
const STATE_DIR: &str = "/var/lib/kintsugi-agent";

const ENV_OVERRIDE: &str = "PATCHING_AGENT_API_BASE_URL";
const ENROLLMENT_TOKEN_ENV_OVERRIDE: &str = "PATCHING_AGENT_ENROLLMENT_TOKEN";

/// Where this agent's mutual-TLS identity (certificate, private key, pinned CA and
/// artifact-signing public key — see `identity`) is persisted once enrolled.
///
/// Owned `root:root 0700`, tighter than the macOS agent's `root:admin 0770`, and deliberately so:
/// there, the per-user process talks to the server itself and so has to be able to read this. Here
/// it never does — every privileged action and every authenticated request belongs to the root
/// service, and the per-user process reaches them through `queue` instead. Nothing outside root
/// has any reason to read this directory, so nothing outside root can. See packaging/install.sh.
const IDENTITY_DIR: &str = "/var/lib/kintsugi-agent/identity";

/// Shared handoff directory between the root service and the per-user `--agent` process — see
/// `queue`, which explains at length why on Linux (as on Windows, and unlike macOS) *everything*
/// privileged goes through here rather than only OS updates. Owned `root:root 1733` by the
/// installer: any logged-in user may drop a request in, nobody but root may list or read the
/// directory, and the sticky bit stops one user removing another's request.
const QUEUE_DIR: &str = "/var/lib/kintsugi-agent/queue";

/// The root service's own durable action log — a guaranteed location regardless of how the process
/// was invoked. systemd captures stdout/stderr into the journal on top of this, which is where a
/// Linux admin looks first (`journalctl -u kintsugi-agent`); the file exists so the agent's logs
/// are in the same place on every platform, and survive a journal that's volatile by default.
const DAEMON_LOG_PATH: &str = "/var/lib/kintsugi-agent/daemon.log";

/// This host's assigned check-in minute-of-hour (0-59), persisted so it survives across
/// invocations — see `checkin_schedule`. Deliberately separate from `CONFIG_PATH`: that file is
/// always overwritten wholesale on every install/reinstall (see packaging/install.sh), whereas
/// this needs to survive one.
const CHECKIN_SCHEDULE_PATH: &str = "/var/lib/kintsugi-agent/checkin-schedule.json";

/// Where the systemd timer that drives this host's hourly check-in lives — `checkin_schedule`
/// rewrites this file in place (and reloads it with systemd) whenever this host's assigned
/// check-in minute changes, exactly as the macOS agent rewrites its LaunchDaemon plist.
const TIMER_UNIT_PATH: &str = "/etc/systemd/system/kintsugi-agent.timer";

/// The rest of the units packaging/install.sh installs, listed here (rather than only as shell
/// variables in that script) so `self_removal` can tear down precisely what was installed.
const SERVICE_UNIT_PATH: &str = "/etc/systemd/system/kintsugi-agent.service";
const QUEUE_SERVICE_UNIT_PATH: &str = "/etc/systemd/system/kintsugi-agent-queue.service";
const QUEUE_PATH_UNIT_PATH: &str = "/etc/systemd/system/kintsugi-agent-queue.path";
const UI_UNIT_PATH: &str = "/etc/systemd/user/kintsugi-agent-ui.service";

/// Where the root service installs itself (see packaging/install.sh) — also this agent's own
/// self-update target: `self_update::check_and_apply` replaces exactly this path.
const INSTALLED_BINARY_PATH: &str = "/usr/local/bin/kintsugi-agent";

/// systemd unit names for everything packaging/install.sh installs — kept here (rather than only
/// as shell variables in that script) so `self_update` can restart them by name once it has
/// replaced the binary they all run, and `checkin_schedule` can reload the timer.
pub const SERVICE_UNIT: &str = "kintsugi-agent.service";
pub const TIMER_UNIT: &str = "kintsugi-agent.timer";
pub const QUEUE_PATH_UNIT: &str = "kintsugi-agent-queue.path";
pub const UI_UNIT: &str = "kintsugi-agent-ui.service";

#[derive(Debug, Deserialize, Default)]
struct FileConfig {
    api_base_url: Option<String>,
    enrollment_token: Option<String>,
}

#[derive(Debug, Clone)]
pub struct Config {
    pub api_base_url: String,
    /// The one-time shared secret this agent presents to enroll (see `identity::enroll`). Only
    /// needed until enrollment succeeds; safe to remove from config.toml afterward, though leaving
    /// it is harmless since it's never used again once an identity exists on disk.
    pub enrollment_token: Option<String>,
}

impl Config {
    /// Resolves configuration in priority order: environment variable,
    /// then config file, then the built-in default. Each field is resolved
    /// independently, so an environment override for one doesn't suppress
    /// the file for the other.
    pub fn load() -> Self {
        Self::load_from(Path::new(CONFIG_PATH))
    }

    fn load_from(config_path: &Path) -> Self {
        let file_config = std::fs::read_to_string(config_path)
            .ok()
            .and_then(|contents| toml::from_str::<FileConfig>(&contents).ok())
            .unwrap_or_default();

        let api_base_url = std::env::var(ENV_OVERRIDE)
            .ok()
            .filter(|v| !v.trim().is_empty())
            .or(file_config.api_base_url)
            .unwrap_or_else(|| DEFAULT_API_BASE_URL.to_string());

        let enrollment_token = std::env::var(ENROLLMENT_TOKEN_ENV_OVERRIDE)
            .ok()
            .filter(|v| !v.trim().is_empty())
            // The packaged config.toml ships with an empty placeholder (`enrollment_token = ""`)
            // for whoever builds the installer to fill in — filtered here the same as the env
            // override above, so a still-blank placeholder is treated as "not configured" and
            // fails with a clear error, rather than silently POSTing an empty token to the server
            // and getting back a generic, hard-to-diagnose 400.
            .or(file_config.enrollment_token.filter(|v| !v.trim().is_empty()));

        Self { api_base_url, enrollment_token }
    }

    pub fn enroll_url(&self) -> String {
        format!("{}/api/host/enroll", self.api_base_url.trim_end_matches('/'))
    }

    pub fn register_host_url(&self) -> String {
        format!("{}/api/host", self.api_base_url.trim_end_matches('/'))
    }

    pub fn register_applications_url(&self) -> String {
        format!("{}/api/applications", self.api_base_url.trim_end_matches('/'))
    }

    /// Base URL only — the caller adds `?serialNumber=` via the HTTP client's own query-building
    /// (`RequestBuilder::query`) rather than manual string formatting, so the serial number gets
    /// properly percent-encoded.
    pub fn upgrade_paths_url(&self) -> String {
        format!("{}/api/upgrade-paths", self.api_base_url.trim_end_matches('/'))
    }

    /// The fleet-wide patching schedule (how often, delay length, max delays) — see
    /// Kintsugi.WebApi/Controllers/PatchingPolicyController.cs.
    pub fn patching_policy_url(&self) -> String {
        format!("{}/api/patching-policy", self.api_base_url.trim_end_matches('/'))
    }

    /// The latest published kintsugi-agent build for one platform — see
    /// Kintsugi.WebApi/Controllers/AgentPackagesController.cs and `self_update`.
    pub fn agent_package_latest_url(&self, platform: &str) -> String {
        format!("{}/api/agent-packages/{platform}/latest", self.api_base_url.trim_end_matches('/'))
    }

    /// Downloads the latest published package file for one platform — see `self_update`.
    pub fn agent_package_download_url(&self, platform: &str) -> String {
        format!("{}/api/agent-packages/{platform}/download", self.api_base_url.trim_end_matches('/'))
    }

    /// Tells the server an application was just successfully patched — see
    /// `upgrade::report_patch_result` and Kintsugi.WebApi/Controllers/ApplicationsController.cs.
    pub fn patch_result_url(&self) -> String {
        format!("{}/api/patch-results", self.api_base_url.trim_end_matches('/'))
    }

    /// Tells the server a pending OS update was just successfully installed — see
    /// `os_update::report_patched` and Kintsugi.WebApi/Controllers/HostsController.cs.
    pub fn os_patch_result_url(&self) -> String {
        format!("{}/api/os-patch-results", self.api_base_url.trim_end_matches('/'))
    }

    /// Tells the server this host finished uninstalling itself completely, after a check-in
    /// response marked it for removal — see `self_removal::run` and
    /// Kintsugi.WebApi/Controllers/HostsController.cs.
    pub fn host_removed_url(&self) -> String {
        format!("{}/api/host-removed", self.api_base_url.trim_end_matches('/'))
    }
}

pub fn default_config_path() -> PathBuf {
    PathBuf::from(CONFIG_PATH)
}

/// The root service / per-user agent handoff directory — see `queue`. A plain constant rather than
/// something `Config` resolves, since (unlike `api_base_url`) it isn't meant to be
/// end-user-configurable.
pub fn queue_dir() -> PathBuf {
    PathBuf::from(QUEUE_DIR)
}

pub fn identity_dir() -> PathBuf {
    PathBuf::from(IDENTITY_DIR)
}

pub fn daemon_log_path() -> PathBuf {
    PathBuf::from(DAEMON_LOG_PATH)
}

pub fn checkin_schedule_path() -> PathBuf {
    PathBuf::from(CHECKIN_SCHEDULE_PATH)
}

pub fn timer_unit_path() -> PathBuf {
    PathBuf::from(TIMER_UNIT_PATH)
}

pub fn service_unit_path() -> PathBuf {
    PathBuf::from(SERVICE_UNIT_PATH)
}

pub fn queue_service_unit_path() -> PathBuf {
    PathBuf::from(QUEUE_SERVICE_UNIT_PATH)
}

pub fn queue_path_unit_path() -> PathBuf {
    PathBuf::from(QUEUE_PATH_UNIT_PATH)
}

pub fn ui_unit_path() -> PathBuf {
    PathBuf::from(UI_UNIT_PATH)
}

pub fn installed_binary_path() -> PathBuf {
    PathBuf::from(INSTALLED_BINARY_PATH)
}

/// Everything under `/etc` this agent owns — just config.toml today, but removed as a directory
/// (see `self_removal`) so a future addition alongside it doesn't need remembering there.
pub fn config_dir() -> PathBuf {
    PathBuf::from(CONFIG_DIR)
}

/// The parent of every mutable path this agent writes as root: the identity, the queue, the daemon
/// log, and the check-in schedule. A full removal (`self_removal`) deletes this one directory
/// rather than each file individually.
pub fn state_dir() -> PathBuf {
    PathBuf::from(STATE_DIR)
}

/// Where the `--agent` (per-user) process keeps its own state: the cached patching policy and
/// scheduling state (next due time, delays used). Lives under the invoking user's home directory
/// since, unlike the root service's state, this process never runs as root.
///
/// `$XDG_STATE_HOME` (falling back to `~/.local/state`) rather than `~/.config`, per the XDG base
/// directory spec's own definition: this is state the program itself writes and would regenerate
/// if lost, not user configuration.
///
/// Doesn't rely on `$HOME` alone, for the same reason the macOS agent doesn't: a user service can
/// be started from a context that isn't a full login session, and `$HOME` has been observed
/// missing there. Falling back to the current effective user's actual passwd entry sidesteps
/// environment variables altogether.
pub fn user_state_dir() -> Result<PathBuf> {
    if let Some(xdg_state_home) = std::env::var("XDG_STATE_HOME").ok().filter(|v| v.starts_with('/')) {
        return Ok(PathBuf::from(xdg_state_home).join("kintsugi-agent"));
    }

    let home = std::env::var("HOME")
        .ok()
        .filter(|v| !v.trim().is_empty())
        .map(PathBuf::from)
        .or_else(home_dir_from_passwd)
        .context("could not determine the current user's home directory (checked $XDG_STATE_HOME, $HOME and the passwd database)")?;

    Ok(home.join(".local/state/kintsugi-agent"))
}

fn home_dir_from_passwd() -> Option<PathBuf> {
    // SAFETY: getuid() cannot fail; getpwuid() returns either a valid pointer into a
    // thread-local static buffer (which we only read from, immediately, before any other libc
    // call in this thread could invalidate it) or null.
    unsafe {
        let passwd = libc::getpwuid(libc::getuid());
        if passwd.is_null() {
            return None;
        }
        let home_dir = CStr::from_ptr((*passwd).pw_dir).to_str().ok()?;
        if home_dir.is_empty() {
            return None;
        }
        Some(PathBuf::from(home_dir))
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn load_from_falls_back_to_the_built_in_default_when_no_file_exists() {
        let config = Config::load_from(Path::new("/nonexistent/kintsugi-agent/config.toml"));

        assert_eq!(config.api_base_url, DEFAULT_API_BASE_URL);
        assert_eq!(config.enrollment_token, None);
    }

    #[test]
    fn load_from_reads_both_fields_out_of_the_file() {
        let path = std::env::temp_dir().join(format!("kintsugi-config-test-{}.toml", std::process::id()));
        std::fs::write(&path, "api_base_url = \"https://example.test:9443\"\nenrollment_token = \"abc123\"\n").unwrap();

        let config = Config::load_from(&path);

        assert_eq!(config.api_base_url, "https://example.test:9443");
        assert_eq!(config.enrollment_token.as_deref(), Some("abc123"));

        let _ = std::fs::remove_file(&path);
    }

    #[test]
    fn load_from_treats_the_blank_packaged_placeholder_as_no_token() {
        let path = std::env::temp_dir().join(format!("kintsugi-config-blank-test-{}.toml", std::process::id()));
        std::fs::write(&path, "enrollment_token = \"\"\n").unwrap();

        assert_eq!(Config::load_from(&path).enrollment_token, None);

        let _ = std::fs::remove_file(&path);
    }

    #[test]
    fn urls_never_double_up_a_slash_when_the_base_url_has_a_trailing_one() {
        let config = Config { api_base_url: "https://example.test:8443/".to_string(), enrollment_token: None };

        assert_eq!(config.register_host_url(), "https://example.test:8443/api/host");
        assert_eq!(config.agent_package_latest_url("linux"), "https://example.test:8443/api/agent-packages/linux/latest");
    }
}
