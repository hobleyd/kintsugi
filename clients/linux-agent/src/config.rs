use std::ffi::CStr;
use std::fs;
use std::os::unix::fs::PermissionsExt;
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

/// The modes `packaging/install.sh` gives the two directories the privilege split rests on, kept
/// here as well because the installer is not the only thing that has to get them right — see
/// `repair_directory_modes`, which re-asserts them on every check-in.
///
/// `0711` on the state directory is traverse-only: root remains the only one who can *list* it or
/// read the identity inside it, but any user can walk through it to reach the queue below. It has
/// to be at least that, because a `0700` parent makes the queue's own `1733` meaningless — the
/// drop-box is unreachable no matter what mode it carries, and the per-user process cannot write a
/// request or a heartbeat into it. That shipped in 0.5.0, and the visible symptom was the
/// misleading "the root service is not installed" warning in `main::run_ui_agent`.
pub const STATE_DIR_MODE: u32 = 0o711;

/// See `QUEUE_DIR` and `queue`'s module documentation for what each bit of this is carrying.
pub const QUEUE_DIR_MODE: u32 = 0o1733;

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

/// The resident root unit that holds the remote control socket.
///
/// A **fourth** systemd unit, and the only long-running root one. The others are a oneshot on a
/// timer, a oneshot on a path watch, and the per-user process; none of them can hold a standing
/// connection, and remote control needs one because the server has to be able to reach the host
/// within seconds rather than at the next hourly check-in.
///
/// It deliberately does **not** take `lock.rs`'s advisory flock. That lock exists to stop two
/// package-manager runs colliding on the dpkg lock, and this unit never installs anything — it
/// relays bytes. Taking the lock would mean a remote session blocked an unattended patch cycle for
/// its whole duration.
/// The Wayland capture and input backend, installed beside the agent binary.
///
/// Named in one place because three of them have to agree: `wayland_backend::helper_path` looks for
/// it, `self_update` installs it out of the archive under this name, and `packaging/install.sh`
/// writes it there. It is optional — an archive built without libpipewire carries no such entry, and
/// an X11 fleet is unaffected.
pub const WAYLAND_BACKEND_BINARY: &str = "kintsugi-agent-wayland";

pub const REMOTE_CONTROL_UNIT: &str = "kintsugi-agent-remote.service";

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

    /// The remote control socket's address.
    ///
    /// One path for both sockets — the standing control one (no `session_id`) and a session's media
    /// one (with) — because nginx gates this route with an exact match on a single path segment and
    /// tells the two apart by query string. See nginx/default.conf and RemoteControlController.
    ///
    /// The scheme is rewritten because `api_base_url` is an HTTP address and tungstenite will not
    /// accept one: it requires `ws`/`wss` and refuses the request outright rather than assuming.
    pub fn remote_control_url(&self, serial_number: &str, session_id: Option<&str>) -> String {
        let base = self.api_base_url.trim_end_matches('/');
        let base = match base.split_once("://") {
            Some(("https", rest)) => format!("wss://{rest}"),
            Some(("http", rest)) => format!("ws://{rest}"),
            // Already a socket scheme, or something unrecognised — passed through so a
            // misconfiguration fails at connect time with the address in the message.
            _ => base.to_string(),
        };

        match session_id {
            Some(session_id) => format!("{base}/api/remote-control?serialNumber={serial_number}&sessionId={session_id}"),
            None => format!("{base}/api/remote-control?serialNumber={serial_number}"),
        }
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

pub fn remote_control_unit_path() -> PathBuf {
    PathBuf::from("/etc/systemd/system").join(REMOTE_CONTROL_UNIT)
}

/// The local channel between the root service and the per-user process — see `remote_ipc`.
///
/// Inside the state directory, which is `0711`: traverse-only, so an unprivileged process can reach
/// this known path and still cannot list what else is in there (the identity, notably).
pub fn remote_control_socket_path() -> PathBuf {
    state_dir().join("remote-control.sock")
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

/// Where the root service writes the fleet-wide patching policy it fetched, for the per-user
/// `--agent` process to read. Machine-wide and world-readable rather than per-user, because that
/// process has no identity of its own to fetch it with (see `IDENTITY_DIR`) — and the policy is a
/// schedule, not a secret. This is the Windows agent's arrangement exactly (see its
/// `config::policy_cache_path`); the macOS per-user process is the odd one out, fetching this
/// itself because it holds credentials the other two deliberately withhold.
///
/// `/api/patching-policy` is inside nginx's client-certificate regex (see `nginx/default.conf`),
/// so there is no such thing as fetching it without an identity: 0.5.0 tried, and every Linux host
/// with a graphical session 403'd on it once a minute forever while the root service, having
/// deferred to that process, patched nothing.
pub fn policy_cache_path() -> PathBuf {
    state_dir().join("policy.json")
}

/// Re-asserts the two directory modes `packaging/install.sh` sets, on every root check-in.
///
/// Not belt-and-braces: `self_update` replaces the binary in place and never re-runs the
/// installer, so a packaging mistake in these modes is otherwise permanent on every host already
/// in the field — there is no upgrade path that would repair it. Doing it here means the fix
/// arrives with the next check-in instead of with a manual reinstall of the whole fleet.
///
/// Deliberately narrow: only widens the two directories whose modes the privilege handoff depends
/// on, only when they differ from what is expected, and never touches `IDENTITY_DIR` — that one is
/// `0700` on purpose and nothing here should be able to loosen it.
pub fn repair_directory_modes() {
    for (path, mode) in [(state_dir(), STATE_DIR_MODE), (queue_dir(), QUEUE_DIR_MODE)] {
        repair_mode(&path, mode);
    }
}

/// The testable half of [`repair_directory_modes`] — the paths it works on are absolute and
/// system-owned, so this takes one directory and the mode it is supposed to have. A directory that
/// isn't there is left alone: the installer creates both, and creating one here (as the wrong user,
/// with the wrong owner) would be worse than reporting nothing.
fn repair_mode(path: &Path, mode: u32) {
    let Ok(metadata) = fs::metadata(path) else {
        return;
    };

    let current = metadata.permissions().mode() & 0o7777;
    if current == mode {
        return;
    }

    match fs::set_permissions(path, fs::Permissions::from_mode(mode)) {
        Ok(()) => crate::logging::info(&format!("corrected the mode on {} from {current:04o} to {mode:04o}", path.display())),
        Err(err) => crate::logging::warn(&format!("could not correct the mode on {} (currently {current:04o}): {err}", path.display())),
    }
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

    fn scratch_dir(name: &str, mode: u32) -> PathBuf {
        let path = std::env::temp_dir().join(format!("kintsugi-config-{name}-{}", std::process::id()));
        let _ = fs::remove_dir_all(&path);
        fs::create_dir_all(&path).unwrap();
        fs::set_permissions(&path, fs::Permissions::from_mode(mode)).unwrap();
        path
    }

    fn mode_of(path: &Path) -> u32 {
        fs::metadata(path).unwrap().permissions().mode() & 0o7777
    }

    /// The 0.5.0 packaging bug, in the one form that can be tested without root: a state directory
    /// left at 0700 has no execute bit for others, so nothing can traverse into the queue below it
    /// whatever mode that queue carries. Widening it is what makes the drop-box reachable again,
    /// and it has to happen here because `self_update` never re-runs the installer.
    #[test]
    fn repair_mode_widens_a_state_directory_left_at_0700_by_an_older_installer() {
        let path = scratch_dir("repair-0700", 0o700);

        repair_mode(&path, STATE_DIR_MODE);

        assert_eq!(mode_of(&path), 0o711);
        let _ = fs::remove_dir_all(&path);
    }

    /// Including the sticky bit — a queue repaired to a plain 0733 would let one user delete
    /// another's pending request.
    #[test]
    fn repair_mode_restores_the_queues_sticky_bit() {
        let path = scratch_dir("repair-sticky", 0o733);

        repair_mode(&path, QUEUE_DIR_MODE);

        assert_eq!(mode_of(&path), 0o1733);
        let _ = fs::remove_dir_all(&path);
    }

    /// The repair pass runs on every check-in, so the overwhelmingly common case is a directory
    /// that is already right — it must be a no-op there rather than something that logs a
    /// correction once an hour forever.
    #[test]
    fn repair_mode_leaves_a_correct_directory_untouched() {
        let path = scratch_dir("repair-correct", 0o711);

        repair_mode(&path, STATE_DIR_MODE);

        assert_eq!(mode_of(&path), 0o711);
        let _ = fs::remove_dir_all(&path);
    }

    /// Nothing here may create a system directory: it would land with this process's ownership
    /// rather than the installer's, which for the queue is precisely the situation the drop-box
    /// design exists to avoid.
    #[test]
    fn repair_mode_does_not_create_a_directory_that_is_not_there() {
        let missing = std::env::temp_dir().join(format!("kintsugi-config-repair-missing-{}", std::process::id()));
        let _ = fs::remove_dir_all(&missing);

        repair_mode(&missing, STATE_DIR_MODE);

        assert!(!missing.exists());
    }

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
