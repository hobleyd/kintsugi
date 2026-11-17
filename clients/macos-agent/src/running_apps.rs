//! Whether an application with a patch waiting is open right now — the one question that decides
//! whether patching it can go ahead without asking anybody.
//!
//! An upgrade quits what it replaces: an AI-researched script is told to quit the application
//! first, and Homebrew's own `start_upgrade` quits a cask's app before installing it. Done to an
//! application nobody has open, that costs the person at the desk nothing, so `patch_cycle`
//! patches those silently — at every check-in (`patch_cycle::run_silent`) and at the front of a
//! due cycle (`patch_cycle::run`) — and only the applications this module finds open are left for
//! the confirm/delay prompt.
//!
//! **Not knowing is answered as "open".** A process listing that failed, a Homebrew cask whose
//! bundles `brew info` would not name, a row this module has no way to match: each one leaves the
//! row to the prompt, which is exactly where every row went before this module existed. The
//! failure this guards against — quitting somebody's open application with no warning — is the
//! one thing the feature must never do, and a row prompted for unnecessarily costs a dialog.
//!
//! What "open" means, by the kind of row (each in the terms the inventory reported it in):
//!
//! - **A bundle** (standalone, App Store, a JDK): some process's executable lives inside a bundle
//!   whose inventory name, or `CFBundleIdentifier`, is the row's. The bundle is the *outermost*
//!   `.app`/`.jdk` in the path, because helpers are bundles inside bundles —
//!   `Google Chrome.app/Contents/Frameworks/…/Google Chrome Helper.app` is Chrome being open — and
//!   it is read through `system_info::bundle_name_and_identifier`, the inventory's own reader.
//!   Matching the identifier too is deliberately generous: every OpenJDK shares
//!   `net.java.openjdk.jdk`, so one running `java` holds back every JDK row, which costs a prompt.
//! - **A Homebrew cask**: a process is running from one of the bundles that cask places
//!   (`system_info::homebrew_cask_bundles`). A cask that names no bundle is not known to be shut.
//! - **A Homebrew formula**: a process's executable is under `<prefix>/Cellar/<formula>/`, which
//!   is where every keg lives and what `proc_pidpath` reports even for a binary launched through
//!   the `bin/` symlink.
//!
//! Every process on the Mac counts, not only this user's: an application left open in another
//! user's fast-user-switching session is quit by the upgrade all the same.
//!
//! Kept identical in intent in the other two agents; what each platform matches on differs and is
//! described in each one's copy. [`partition`] is the shared rule, and is the same in all three.

use std::collections::{HashMap, HashSet};
use std::path::{Component, Path, PathBuf};

use crate::system_info;
use crate::upgrade::UpgradeStatus;

/// Splits `apps` into those `is_open` says are shut — patchable now, without asking — and the
/// rest, in their original order. `None` from `is_open` ("could not tell") lands with the open
/// ones; see the module docs for why that is the rule the whole feature rests on. Kept identical
/// in the other two agents.
pub fn partition<T>(apps: Vec<T>, is_open: impl Fn(&T) -> Option<bool>) -> (Vec<T>, Vec<T>) {
    apps.into_iter().partition(|app| is_open(app) == Some(false))
}

/// What a process listing found: the outermost bundle each running executable sits in, with the
/// name and identifier the inventory gives that bundle, and the formulae running out of a keg.
#[derive(Debug, Default)]
struct Snapshot {
    bundle_paths: HashSet<PathBuf>,
    bundle_names: HashSet<String>,
    bundle_identifiers: HashSet<String>,
    formulae: HashSet<String>,
}

impl Snapshot {
    /// Builds a snapshot from executable paths, reading each distinct bundle once through
    /// `read_bundle`. Split from [`take_snapshot`] so the matching is tested without a process
    /// table or a real bundle.
    fn from_paths(paths: impl IntoIterator<Item = PathBuf>, read_bundle: impl Fn(&Path) -> Option<(String, Option<String>)>) -> Self {
        let mut snapshot = Snapshot::default();
        for path in paths {
            if let Some(formula) = cellar_formula(&path) {
                snapshot.formulae.insert(formula);
            }
            let Some(bundle) = outermost_bundle(&path) else { continue };
            if !snapshot.bundle_paths.insert(bundle.clone()) {
                continue;
            }
            if let Some((name, identifier)) = read_bundle(&bundle) {
                snapshot.bundle_names.insert(name);
                snapshot.bundle_identifiers.extend(identifier);
            }
        }
        snapshot
    }

    /// `Some(true)`/`Some(false)` when this snapshot can say, `None` when it cannot.
    fn is_open(&self, app: &UpgradeStatus, cask_bundles: Option<&HashMap<String, Vec<PathBuf>>>) -> Option<bool> {
        if app.package_manager.as_deref() == Some(system_info::HOMEBREW_NAME) {
            let token = app.application_identifier.as_deref().unwrap_or(&app.application_name);
            if self.formulae.contains(token) {
                return Some(true);
            }
            return match cask_bundles {
                Some(casks) => match casks.get(token) {
                    // A cask that names nothing the scan looks at could be running from anywhere.
                    Some(bundles) if bundles.is_empty() => None,
                    Some(bundles) => Some(bundles.iter().any(|bundle| self.bundle_paths.contains(bundle))),
                    // Not a cask, so a formula — and not one running out of its keg.
                    None => Some(false),
                },
                // Formula or cask cannot be told apart without `brew info`, and a cask cannot be
                // matched without it either.
                None => None,
            };
        }

        let by_identifier = app.application_identifier.as_ref().is_some_and(|id| self.bundle_identifiers.contains(id));
        Some(by_identifier || self.bundle_names.contains(&app.application_name))
    }
}

/// The outermost `.app` or `.jdk` directory on `path`, if any.
fn outermost_bundle(path: &Path) -> Option<PathBuf> {
    let mut prefix = PathBuf::new();
    for component in path.components() {
        prefix.push(component);
        if let Component::Normal(name) = component {
            let name = Path::new(name);
            if matches!(name.extension().and_then(|ext| ext.to_str()), Some("app" | "jdk")) {
                return Some(prefix);
            }
        }
    }
    None
}

/// The formula a keg path belongs to: the component after `Cellar`.
fn cellar_formula(path: &Path) -> Option<String> {
    let mut components = path.components();
    while let Some(component) = components.next() {
        if component == Component::Normal("Cellar".as_ref()) {
            return components.next().and_then(|next| next.as_os_str().to_str()).map(str::to_string);
        }
    }
    None
}

/// Every running process's executable path. A process this one may not inspect, or one that
/// exits between the two calls, is simply absent; only the listing itself failing is an error.
fn running_executables() -> anyhow::Result<Vec<PathBuf>> {
    // SAFETY: a null buffer asks for the count only.
    let count = unsafe { libc::proc_listallpids(std::ptr::null_mut(), 0) };
    if count <= 0 {
        anyhow::bail!("proc_listallpids returned {count}");
    }
    // Headroom for processes started between the two calls.
    let mut pids = vec![0 as libc::pid_t; count as usize + 64];
    let bytes = (pids.len() * std::mem::size_of::<libc::pid_t>()) as libc::c_int;
    // SAFETY: the buffer is `bytes` long and lives across the call.
    let filled = unsafe { libc::proc_listallpids(pids.as_mut_ptr().cast(), bytes) };
    if filled <= 0 {
        anyhow::bail!("proc_listallpids returned {filled}");
    }
    pids.truncate(filled as usize);

    let mut paths = Vec::with_capacity(pids.len());
    let mut buffer = vec![0u8; libc::PROC_PIDPATHINFO_MAXSIZE as usize];
    for pid in pids.into_iter().filter(|pid| *pid > 0) {
        // SAFETY: the buffer is PROC_PIDPATHINFO_MAXSIZE long and lives across the call.
        let length = unsafe { libc::proc_pidpath(pid, buffer.as_mut_ptr().cast(), buffer.len() as u32) };
        if length > 0 {
            paths.push(PathBuf::from(String::from_utf8_lossy(&buffer[..length as usize]).into_owned()));
        }
    }
    Ok(paths)
}

fn take_snapshot() -> anyhow::Result<Snapshot> {
    Ok(Snapshot::from_paths(running_executables()?, system_info::bundle_name_and_identifier))
}

/// Answers "is this open?" for one pass of patching. The cask map is read once per pass —
/// casks do not move mid-cycle, and `brew info` takes a second or two — while the process table
/// is read afresh for every question, because a pass can run for many minutes and somebody may
/// open an application between the moment the list was split and the moment its turn comes.
pub struct Probe {
    cask_bundles: Option<HashMap<String, Vec<PathBuf>>>,
}

impl Probe {
    /// `apps` are the rows about to be asked about; `brew info` is only run when one of them is a
    /// Homebrew row.
    pub fn new(apps: &[UpgradeStatus]) -> Self {
        let any_homebrew = apps.iter().any(|app| app.package_manager.as_deref() == Some(system_info::HOMEBREW_NAME));
        Probe { cask_bundles: if any_homebrew { system_info::homebrew_cask_bundles() } else { None } }
    }

    /// `None` when it could not tell — see [`partition`] for what that means.
    pub fn is_open(&self, app: &UpgradeStatus) -> Option<bool> {
        match take_snapshot() {
            Ok(snapshot) => snapshot.is_open(app, self.cask_bundles.as_ref()),
            Err(err) => {
                crate::logging::warn(&format!("could not list running processes, so {} is treated as open: {err:#}", app.application_name));
                None
            }
        }
    }
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
    use crate::upgrade::UpgradeMethod;

    fn row(name: &str, identifier: Option<&str>, package_manager: Option<&str>) -> UpgradeStatus {
        UpgradeStatus {
            application_name: name.to_string(),
            installed_version: "1.0".to_string(),
            latest_version: Some("2.0".to_string()),
            update_available: true,
            method: UpgradeMethod::Script,
            application_identifier: identifier.map(str::to_string),
            command: None,
            notes: None,
            script: None,
            script_signature: None,
            command_signature: None,
            package_manager: package_manager.map(str::to_string),
        }
    }

    fn snapshot(paths: &[&str]) -> Snapshot {
        Snapshot::from_paths(paths.iter().map(PathBuf::from), |bundle| {
            let stem = bundle.file_stem()?.to_str()?.to_string();
            Some((stem.clone(), Some(format!("com.example.{}", stem.to_lowercase().replace(' ', "")))))
        })
    }

    #[test]
    fn a_helper_inside_a_bundle_counts_as_the_bundle_being_open() {
        let running = snapshot(&["/Applications/Google Chrome.app/Contents/Frameworks/Chrome.framework/Helpers/Google Chrome Helper.app/Contents/MacOS/Google Chrome Helper"]);
        assert_eq!(running.is_open(&row("Google Chrome", None, None), None), Some(true));
        assert_eq!(running.is_open(&row("Google Chrome Helper", None, None), None), Some(false));
    }

    #[test]
    fn a_bundle_is_matched_by_identifier_as_well_as_by_name() {
        let running = snapshot(&["/Applications/Folder/Slack.app/Contents/MacOS/Slack"]);
        assert_eq!(running.is_open(&row("Slack for Desktop", Some("com.example.slack"), None), None), Some(true));
        assert_eq!(running.is_open(&row("Zoom", Some("us.zoom.xos"), None), None), Some(false));
    }

    #[test]
    fn a_formula_is_open_when_something_runs_out_of_its_keg() {
        let running = snapshot(&["/opt/homebrew/Cellar/node/24.1.0/bin/node"]);
        let casks = HashMap::new();
        assert_eq!(running.is_open(&row("node", Some("node"), Some("Homebrew")), Some(&casks)), Some(true));
        assert_eq!(running.is_open(&row("git", Some("git"), Some("Homebrew")), Some(&casks)), Some(false));
    }

    #[test]
    fn a_cask_is_open_when_one_of_its_bundles_is() {
        let running = snapshot(&["/Applications/Firefox.app/Contents/MacOS/firefox"]);
        let casks = HashMap::from([
            ("firefox".to_string(), vec![PathBuf::from("/Applications/Firefox.app")]),
            ("iterm2".to_string(), vec![PathBuf::from("/Applications/iTerm.app")]),
        ]);
        assert_eq!(running.is_open(&row("firefox", Some("firefox"), Some("Homebrew")), Some(&casks)), Some(true));
        assert_eq!(running.is_open(&row("iterm2", Some("iterm2"), Some("Homebrew")), Some(&casks)), Some(false));
    }

    #[test]
    fn a_homebrew_row_is_not_known_to_be_shut_without_brew_info_or_a_bundle_to_check() {
        let running = snapshot(&[]);
        assert_eq!(running.is_open(&row("firefox", Some("firefox"), Some("Homebrew")), None), None);
        let casks = HashMap::from([("font-fira-code".to_string(), Vec::new())]);
        assert_eq!(running.is_open(&row("font-fira-code", Some("font-fira-code"), Some("Homebrew")), Some(&casks)), None);
    }

    #[test]
    fn a_jdk_is_its_directory() {
        assert_eq!(
            outermost_bundle(Path::new("/Library/Java/JavaVirtualMachines/temurin-26.jdk/Contents/Home/bin/java")),
            Some(PathBuf::from("/Library/Java/JavaVirtualMachines/temurin-26.jdk"))
        );
        assert_eq!(outermost_bundle(Path::new("/usr/bin/ssh")), None);
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
        let apps = vec![row("Shut", None, None), row("Open", None, None), row("Unknown", None, None)];
        let (shut, rest) = partition(apps, |app| match app.application_name.as_str() {
            "Shut" => Some(false),
            "Open" => Some(true),
            _ => None,
        });
        assert_eq!(shut.iter().map(|app| app.application_name.as_str()).collect::<Vec<_>>(), ["Shut"]);
        assert_eq!(rest.iter().map(|app| app.application_name.as_str()).collect::<Vec<_>>(), ["Open", "Unknown"]);
    }

    /// Against this Mac's real process table: the test binary itself is running, and lists at
    /// least itself.
    #[test]
    fn the_real_process_table_lists_this_process() {
        let paths = running_executables().expect("proc_listallpids works");
        let me = std::env::current_exe().unwrap();
        assert!(paths.iter().any(|path| path == &me || path.canonicalize().ok() == me.canonicalize().ok()));
    }
}
