//! Whether an application with a patch waiting is open right now — the one question that decides
//! whether patching it can go ahead without asking anybody.
//!
//! An upgrade can take an application out from under whoever is using it, so `patch_cycle`
//! patches the ones nobody has open silently — at every root check-in
//! (`patch_cycle::run_silent`) and at the front of a due cycle (`patch_cycle::run`) — and only the
//! applications this module finds open are left for the confirm/delay prompt.
//!
//! **Not knowing is answered as "open".** A row with no identifier, a `/proc` that could not be
//! read: each leaves the row to the prompt, which is exactly where every row went before this
//! module existed. Quitting somebody's open application with no warning is the one thing the
//! feature must never do; a row prompted for unnecessarily costs a dialog.
//!
//! What "open" means here, read from `/proc` by the root service (which can see every process,
//! whoever owns it — an application left open in another user's session counts):
//!
//! - **A Flatpak application** (`org.mozilla.firefox`): some process's root has a `/.flatpak-info`
//!   naming it. Every Flatpak sandbox gets that file, and `/proc/<pid>/root` reaches into the
//!   sandbox's mount namespace, so this needs neither `flatpak ps` (which lists only the invoking
//!   user's instances, and root has none) nor a systemd scope name (absent without a user manager).
//! - **A snap** (`firefox`): some process's cgroup names a `snap.<name>.` scope or service, or its
//!   executable lives under `/snap/<name>/`. A snap's daemon counts as open, which on a server holds
//!   back e.g. `lxd` until the cycle — where `run_unattended` patches it anyway.
//!
//! The two are told apart by the identifier's shape, which is a property of each manager rather
//! than a guess: a Flatpak id is reverse-DNS and always has a dot, and a snap name may not contain
//! one (`snap` refuses it).
//!
//! Kept identical in intent in the other two agents; what each platform matches on differs and is
//! described in each one's copy. [`partition`] is the shared rule, and is the same in all three.

use std::collections::HashSet;
use std::fs;
use std::path::Path;

/// Splits `apps` into those `is_open` says are shut — patchable now, without asking — and the
/// rest, in their original order. `None` from `is_open` ("could not tell") lands with the open
/// ones; see the module docs for why that is the rule the whole feature rests on. Kept identical
/// in the other two agents.
pub fn partition<T>(apps: Vec<T>, is_open: impl Fn(&T) -> Option<bool>) -> (Vec<T>, Vec<T>) {
    apps.into_iter().partition(|app| is_open(app) == Some(false))
}

/// What one pass over `/proc` found.
#[derive(Debug, Default)]
pub struct Snapshot {
    flatpaks: HashSet<String>,
    snaps: HashSet<String>,
}

impl Snapshot {
    /// Reads every process in `/proc`. A process that exits mid-read, or one this process may not
    /// inspect, is simply absent; only `/proc` itself being unreadable is an error.
    pub fn take() -> anyhow::Result<Self> {
        let mut snapshot = Snapshot::default();
        for entry in fs::read_dir("/proc")?.flatten() {
            let name = entry.file_name();
            let Some(pid) = name.to_str().filter(|name| name.bytes().all(|byte| byte.is_ascii_digit())) else { continue };
            let proc_dir = Path::new("/proc").join(pid);

            if let Ok(info) = fs::read_to_string(proc_dir.join("root/.flatpak-info")) {
                snapshot.flatpaks.extend(flatpak_from_info(&info));
            }
            if let Ok(cgroup) = fs::read_to_string(proc_dir.join("cgroup")) {
                snapshot.snaps.extend(snaps_from_cgroup(&cgroup));
            }
            if let Ok(exe) = fs::read_link(proc_dir.join("exe")) {
                snapshot.snaps.extend(snap_from_exe(&exe));
            }
        }
        Ok(snapshot)
    }

    /// `Some(true)`/`Some(false)` when this snapshot can say, `None` when it cannot.
    pub fn is_open(&self, application_identifier: Option<&str>) -> Option<bool> {
        let identifier = application_identifier?;
        if identifier.contains('.') {
            Some(self.flatpaks.contains(identifier))
        } else {
            Some(self.snaps.contains(identifier))
        }
    }
}

/// A fresh snapshot's answer for one row — for the re-check immediately before each silent patch.
pub fn is_open_now(application_identifier: Option<&str>) -> Option<bool> {
    match Snapshot::take() {
        Ok(snapshot) => snapshot.is_open(application_identifier),
        Err(err) => {
            crate::logging::warn(&format!("could not read /proc, so the application is treated as open: {err:#}"));
            None
        }
    }
}

/// The application id in a `/.flatpak-info` keyfile: `name=` under `[Application]`. A runtime's
/// sandbox (`[Runtime]`) is not an application anybody has open.
fn flatpak_from_info(info: &str) -> Option<String> {
    let mut in_application = false;
    for line in info.lines().map(str::trim) {
        if line.starts_with('[') {
            in_application = line == "[Application]";
        } else if in_application {
            if let Some(name) = line.strip_prefix("name=") {
                return Some(name.trim().to_string()).filter(|name| !name.is_empty());
            }
        }
    }
    None
}

/// Every snap a `/proc/<pid>/cgroup` names: a path component `snap.<name>.<app>…` — a scope
/// (`snap.firefox.firefox-1a2b.scope`) for a launched app, a service (`snap.lxd.daemon.service`)
/// for a daemon.
fn snaps_from_cgroup(cgroup: &str) -> Vec<String> {
    cgroup
        .lines()
        .flat_map(|line| line.rsplit(':').next().unwrap_or("").split('/'))
        .filter_map(|component| component.strip_prefix("snap."))
        .filter_map(|rest| rest.split('.').next())
        .filter(|name| !name.is_empty())
        .map(str::to_string)
        .collect()
}

/// The snap an executable belongs to, when it lives under `/snap/<name>/`.
fn snap_from_exe(exe: &Path) -> Option<String> {
    let mut components = exe.strip_prefix("/snap").ok()?.components();
    components.next()?.as_os_str().to_str().map(str::to_string)
}

/// What the silent pass has already tried, so a row it cannot patch is not retried every hour.
///
/// The silent pass runs hourly; the prompted cycle runs once per patching interval. Without this, an
/// upgrade that fails — or a row the server keeps counting behind while its script correctly finds
/// nothing to do — would be retried, re-reported and (for anything an upgrade quits before failing)
/// re-quit every hour rather than once per cycle. So each `(application, target version)` the pass
/// attempts is written down, and later passes skip it; the row stays with the due cycle, which goes
/// on trying it once per cycle exactly as before. A new target version is a new attempt, and an
/// entry whose row is no longer pending is dropped. Kept identical in the other two agents.
#[derive(Debug, Default, PartialEq, Eq, serde::Serialize, serde::Deserialize)]
pub struct SilentAttempts {
    attempted: Vec<(String, Option<String>)>,
}

impl SilentAttempts {
    /// What is on disk, or nothing tried yet when the file is missing or unreadable — which costs at
    /// most one extra attempt per row.
    pub fn load(path: &std::path::Path) -> Self {
        std::fs::read_to_string(path).ok().and_then(|text| serde_json::from_str(&text).ok()).unwrap_or_default()
    }

    pub fn save(&self, path: &std::path::Path) {
        let written = serde_json::to_string(self).map_err(anyhow::Error::from).and_then(|text| Ok(std::fs::write(path, text)?));
        if let Err(err) = written {
            crate::logging::warn(&format!("could not record the silent pass's attempts at {}: {err:#}", path.display()));
        }
    }

    /// Forgets every attempt whose `(name, version)` is not among `pending` — patched since, or
    /// moved on to a newer target.
    pub fn retain_pending<'a>(&mut self, pending: impl IntoIterator<Item = (&'a str, Option<&'a str>)>) {
        let pending: Vec<(&str, Option<&str>)> = pending.into_iter().collect();
        self.attempted.retain(|(name, version)| pending.iter().any(|(n, v)| n.eq_ignore_ascii_case(name) && *v == version.as_deref()));
    }

    pub fn already_tried(&self, name: &str, version: Option<&str>) -> bool {
        self.attempted.iter().any(|(n, v)| n.eq_ignore_ascii_case(name) && v.as_deref() == version)
    }

    pub fn record(&mut self, name: &str, version: Option<&str>) {
        if !self.already_tried(name, version) {
            self.attempted.push((name.to_string(), version.map(str::to_string)));
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn a_flatpak_info_names_the_application_and_not_the_runtime() {
        let info = "[Application]\nname=org.mozilla.firefox\nruntime=runtime/org.freedesktop.Platform/x86_64/24.08\n\n[Instance]\ninstance-id=123\n";
        assert_eq!(flatpak_from_info(info).as_deref(), Some("org.mozilla.firefox"));
        assert_eq!(flatpak_from_info("[Runtime]\nname=org.freedesktop.Platform\n"), None);
    }

    #[test]
    fn a_cgroup_names_a_launched_snap_and_a_snap_daemon() {
        let launched = "0::/user.slice/user-1000.slice/user@1000.service/app.slice/snap.firefox.firefox-0b1c2d.scope\n";
        assert_eq!(snaps_from_cgroup(launched), ["firefox"]);
        let daemon = "0::/system.slice/snap.lxd.daemon.service\n";
        assert_eq!(snaps_from_cgroup(daemon), ["lxd"]);
        assert!(snaps_from_cgroup("0::/user.slice/user-1000.slice/session-2.scope\n").is_empty());
    }

    #[test]
    fn an_executable_under_snap_names_its_snap() {
        assert_eq!(snap_from_exe(Path::new("/snap/code/190/usr/share/code/code")).as_deref(), Some("code"));
        assert_eq!(snap_from_exe(Path::new("/usr/bin/bash")), None);
    }

    #[test]
    fn the_identifier_says_which_manager_to_ask() {
        let snapshot = Snapshot {
            flatpaks: HashSet::from(["org.gimp.GIMP".to_string()]),
            snaps: HashSet::from(["firefox".to_string()]),
        };
        assert_eq!(snapshot.is_open(Some("org.gimp.GIMP")), Some(true));
        assert_eq!(snapshot.is_open(Some("org.mozilla.firefox")), Some(false));
        assert_eq!(snapshot.is_open(Some("firefox")), Some(true));
        assert_eq!(snapshot.is_open(Some("code")), Some(false));
        assert_eq!(snapshot.is_open(None), None, "no identifier is no way to tell");
    }

    /// Against this machine's real `/proc`: readable, and nothing in a test container is a snap.
    #[cfg(target_os = "linux")]
    #[test]
    fn the_real_proc_can_be_read() {
        let snapshot = Snapshot::take().expect("/proc is readable");
        assert_eq!(snapshot.is_open(Some("definitely-not-a-snap")), Some(false));
    }

    #[test]
    fn an_attempt_is_skipped_until_its_target_moves_on_or_it_stops_being_pending() {
        let mut attempts = SilentAttempts::default();
        attempts.record("Firefox", Some("2.0"));
        attempts.record("GIMP", None);
        assert!(attempts.already_tried("firefox", Some("2.0")));
        assert!(!attempts.already_tried("Firefox", Some("3.0")), "a new target version is a new attempt");

        attempts.retain_pending([("Firefox", Some("3.0")), ("GIMP", None)]);
        assert!(!attempts.already_tried("Firefox", Some("2.0")), "the old target is forgotten");
        assert!(attempts.already_tried("GIMP", None), "still pending, still skipped");

        attempts.retain_pending(std::iter::empty());
        assert_eq!(attempts, SilentAttempts::default(), "nothing pending, nothing remembered");
    }

    #[test]
    fn partition_sends_could_not_tell_to_the_prompt() {
        let (shut, rest) = partition(vec![("Shut", Some(false)), ("Open", Some(true)), ("Unknown", None)], |app| app.1);
        assert_eq!(shut.iter().map(|app| app.0).collect::<Vec<_>>(), ["Shut"]);
        assert_eq!(rest.iter().map(|app| app.0).collect::<Vec<_>>(), ["Open", "Unknown"]);
    }
}
