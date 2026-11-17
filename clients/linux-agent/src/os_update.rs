use std::path::{Path, PathBuf};
use std::process::{Command, Output};
use std::time::Duration;

use anyhow::{Context, Result};
use serde::Serialize;

use crate::config::Config;

/// The result of a standard OS-update check: whether one is pending, the version it would bring
/// the host to when the check can determine that, and which binary packages are pending.
#[derive(Debug, Clone, Default, PartialEq, Eq)]
pub struct OsUpdateStatus {
    pub available: bool,
    pub latest_version: Option<String>,
    /// The *binary* package names (`libssl3`, not `openssl`) this manager's listing says are
    /// pending — `system_info::scan_os_packages` resolves each to the source package it belongs
    /// to before attributing an "update available" verdict to a reported package. `Some` (possibly
    /// empty) for a manager whose format names individual packages; `None` for one that does not,
    /// exactly as `latest_version` is already `None` on every manager for a different reason (see
    /// its own doc comment) — `scan_os_packages` never reports pacman or apk packages at all, so
    /// there is nothing here for their pending names to be attributed to.
    pub pending_package_names: Option<Vec<String>>,
}

/// The distribution's own package manager — this agent's counterpart to `softwareupdate` on macOS
/// and the Windows Update Agent on Windows.
///
/// The mapping is exact rather than approximate, and it is why Linux needs no separate notion of
/// "distro packages" in the application inventory (see `main::collect_installed_applications`):
/// `softwareupdate` patches macOS *and* the software Apple ships with it; `apt`/`dnf` patch the
/// distribution *and* the software it ships. Same role, same module. What's left over on each
/// platform — App Store-less third-party apps and Homebrew there, Flatpak and Snap here — is what
/// the application inventory covers.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum PackageManager {
    Apt,
    Dnf,
    Yum,
    Zypper,
    Pacman,
    Apk,
}

impl PackageManager {
    fn binary(self) -> &'static str {
        match self {
            PackageManager::Apt => "apt-get",
            PackageManager::Dnf => "dnf",
            PackageManager::Yum => "yum",
            PackageManager::Zypper => "zypper",
            PackageManager::Pacman => "pacman",
            PackageManager::Apk => "apk",
        }
    }
}

/// Checked in order, so a host with both `dnf` and its `yum` compatibility shim uses `dnf`.
const CANDIDATES: &[PackageManager] = &[
    PackageManager::Apt,
    PackageManager::Dnf,
    PackageManager::Yum,
    PackageManager::Zypper,
    PackageManager::Pacman,
    PackageManager::Apk,
];

/// The directories a root systemd service can count on — see `system_info::find_binary`, which
/// makes the same choice for the same reason (a unit does not inherit an interactive shell's
/// `PATH`). Package managers live in `sbin` more often than not, hence the ordering.
const SEARCH_DIRS: &[&str] = &["/usr/bin", "/bin", "/usr/sbin", "/sbin", "/usr/local/bin"];

/// Locates this host's package manager, or `None` on a distribution using something none of the
/// above covers. Every caller treats `None` as "no OS updates to report", never as an error: an
/// unrecognized distribution should still register, still report its applications, and still
/// patch those — it just has no OS-update step.
pub fn detect() -> Option<(PackageManager, PathBuf)> {
    CANDIDATES.iter().find_map(|&manager| {
        SEARCH_DIRS
            .iter()
            .map(|dir| Path::new(dir).join(manager.binary()))
            .find(|path| path.is_file())
            .map(|path| (manager, path))
    })
}

/// Whether the distribution has pending package updates.
///
/// Unlike macOS — where `softwareupdate -l` names one concrete thing the host would move to, and
/// so a `latest_version` to report — this is a rolling set of individually-versioned packages
/// with no single target version between them. `latest_version` is therefore always `None` here,
/// and deliberately: inventing one (the count, the distribution's own release number) would put
/// something in `Host.OperatingSystemLatestVersion` that isn't a version of anything.
///
/// Runs as root, always: this is only ever called from the root service (during a check-in, or
/// while answering a `queue::RequestKind::Plan`), never from the per-user process, which on Linux
/// makes no privileged call at all.
pub fn check() -> Result<OsUpdateStatus> {
    let Some((manager, path)) = detect() else {
        crate::logging::info("no supported distribution package manager found; not reporting an OS-update status");
        return Ok(OsUpdateStatus::default());
    };

    refresh_package_lists(manager, &path);

    let output = list_updates(manager, &path)?;
    let combined = format!(
        "{}{}",
        String::from_utf8_lossy(&output.stdout),
        String::from_utf8_lossy(&output.stderr)
    );

    let count = count_pending_updates(manager, &combined);
    let pending_package_names = extract_pending_package_names(manager, &combined);
    let status = OsUpdateStatus { available: count > 0, latest_version: None, pending_package_names };

    crate::logging::info(&format!(
        "checked for OS updates with {}: {count} package(s) pending",
        path.display()
    ));

    Ok(status)
}

pub fn check_available() -> Result<bool> {
    Ok(check()?.available)
}

/// Refreshes the package index before counting. Best-effort — a failure here (no network, a
/// repository temporarily 503ing) leaves the counting below working off whatever index the host
/// already has, which is a stale answer rather than no answer.
///
/// `pacman` is the exception and is deliberately not refreshed: `pacman -Sy` without a full
/// `-Syu` immediately afterward creates the partial-upgrade state Arch explicitly warns against,
/// and this function has no business doing that to a host merely to count something. Arch users
/// running `checkupdates` (pacman-contrib) get a safely-refreshed count; without it the count
/// comes from the existing sync database.
fn refresh_package_lists(manager: PackageManager, path: &Path) {
    let args: &[&str] = match manager {
        PackageManager::Apt => &["-o", APT_LOCK_WAIT, "update", "-qq"],
        PackageManager::Dnf | PackageManager::Yum => &["-q", "makecache"],
        PackageManager::Zypper => &["--non-interactive", "refresh"],
        PackageManager::Pacman => return,
        PackageManager::Apk => &["update", "--quiet"],
    };

    match run(path, args) {
        Ok(output) if output.status.success() => {}
        Ok(output) => crate::logging::warn(&format!(
            "refreshing the package index with {} exited with {}: {}",
            path.display(),
            output.status,
            String::from_utf8_lossy(&output.stderr).trim()
        )),
        Err(err) => crate::logging::warn(&format!("could not refresh the package index: {err:#}")),
    }
}

/// Asks the package manager what it *would* upgrade, without upgrading anything. Every one of
/// these is a dry run or a plain query; none of them changes a thing on the host.
fn list_updates(manager: PackageManager, path: &Path) -> Result<Output> {
    let output = match manager {
        // `--just-print` is apt-get's dry run. `Debug::NoLocking` lets it run alongside whatever
        // else might hold the dpkg lock, since it isn't going to write anything anyway.
        PackageManager::Apt => run(path, &["--just-print", "-o", "Debug::NoLocking=true", "upgrade"])?,
        // `check-update` exits 100 (not 0) when updates exist, which is why the caller looks at
        // the output rather than the status.
        PackageManager::Dnf | PackageManager::Yum => run(path, &["-q", "check-update"])?,
        PackageManager::Zypper => run(path, &["--non-interactive", "list-updates"])?,
        // `checkupdates` refreshes into a private database first, so it neither lies nor risks a
        // partial upgrade; `pacman -Qu` is the fallback when pacman-contrib isn't installed.
        PackageManager::Pacman => match SEARCH_DIRS.iter().map(|dir| Path::new(dir).join("checkupdates")).find(|p| p.is_file()) {
            Some(checkupdates) => run(&checkupdates, &[])?,
            None => run(path, &["-Qu"])?,
        },
        PackageManager::Apk => run(path, &["version", "-l", "<"])?,
    };

    Ok(output)
}

/// Counts pending updates in one package manager's listing output. Each format is different
/// enough that a shared parser would be a worse lie than five small ones — and being pure `&str`
/// functions, they're the part of this module a test can actually exercise off a Linux host with
/// no packages pending.
fn count_pending_updates(manager: PackageManager, output: &str) -> usize {
    match manager {
        // "Inst libssl3 [3.0.11-1] (3.0.13-1 Ubuntu:24.04 [amd64])"
        PackageManager::Apt => output.lines().filter(|line| line.starts_with("Inst ")).count(),
        // A table of "<name>.<arch>  <version>  <repo>", after any "Obsoleting Packages" or
        // "Last metadata expiration check" preamble. Blank lines and the section headers are what
        // has to be excluded, so a line is counted only when it looks like three-plus columns
        // whose first carries a dot-separated architecture suffix.
        PackageManager::Dnf | PackageManager::Yum => output
            .lines()
            .filter(|line| !line.starts_with(char::is_whitespace))
            .filter(|line| {
                let mut fields = line.split_whitespace();
                let name = fields.next().unwrap_or_default();
                name.contains('.') && !name.contains(':') && fields.count() >= 2
            })
            .count(),
        // zypper's table rows all start with the status column "v |".
        PackageManager::Zypper => output.lines().filter(|line| line.trim_start().starts_with("v |")).count(),
        // Both `checkupdates` and `pacman -Qu` print one "<name> <old> -> <new>" line per package.
        PackageManager::Pacman => output.lines().filter(|line| line.contains("->")).count(),
        // "apk version -l '<'" prints a header line then "<pkg>-<ver> < <newver>" per package.
        PackageManager::Apk => output.lines().filter(|line| line.contains('<') && !line.starts_with("Installed")).count(),
    }
}

/// Extracts the *binary* package names a manager's listing says are pending — the same lines
/// `count_pending_updates` counts, with one more field pulled out. `None` for pacman and apk:
/// `system_info::scan_os_packages` never reports a package from either (it only ever reads dpkg
/// or rpm), so there is no installed-package inventory here for a pending name to be attributed
/// to, and reporting one anyway would invite a resolution against packages that do not exist.
fn extract_pending_package_names(manager: PackageManager, output: &str) -> Option<Vec<String>> {
    match manager {
        // "Inst libssl3 [3.0.11-1] (3.0.13-1 Ubuntu:24.04 [amd64])" -> "libssl3"
        PackageManager::Apt => Some(
            output
                .lines()
                .filter(|line| line.starts_with("Inst "))
                .filter_map(|line| line.split_whitespace().nth(1))
                .map(str::to_string)
                .collect(),
        ),
        // "curl.x86_64  8.6.0-3.fc40  updates" -> "curl". The arch suffix is split at the *last*
        // dot, since a package name may itself contain one ("python3.11.x86_64" -> "python3.11"),
        // and the same dot is what `count_pending_updates` already requires a row to have.
        PackageManager::Dnf | PackageManager::Yum => Some(
            output
                .lines()
                .filter(|line| !line.starts_with(char::is_whitespace))
                .filter_map(|line| {
                    let mut fields = line.split_whitespace();
                    let name = fields.next()?;
                    (name.contains('.') && !name.contains(':') && fields.count() >= 2)
                        .then(|| name.rsplit_once('.').map_or(name, |(pkg, _arch)| pkg).to_string())
                })
                .collect(),
        ),
        // "v | Update | curl  | 8.0.1-150400    | 8.6.0-150400      | x86_64" -> "curl"
        PackageManager::Zypper => Some(
            output
                .lines()
                .filter(|line| line.trim_start().starts_with("v |"))
                .filter_map(|line| line.split('|').nth(2))
                .map(|name| name.trim().to_string())
                .collect(),
        ),
        PackageManager::Pacman | PackageManager::Apk => None,
    }
}

/// Installs every pending package update. Run only by the root service, only in response to a
/// `queue::RequestKind::OsUpdate` — which carries no body at all, so a forged or corrupted
/// request can at worst start this early, never run anything of its own choosing.
///
/// Deliberately the conservative verb in every case (`apt-get upgrade`, not `dist-upgrade`;
/// `zypper update`, not `dup`): those are the ones that will not remove an installed package to
/// satisfy a dependency. An unattended agent that can uninstall software to complete a patch run
/// is not a patch-management system, and the one exception — `pacman -Syu` — is exception only
/// because a rolling distribution has no non-full upgrade that is even coherent.
pub fn install() -> Result<String> {
    let Some((manager, path)) = detect() else {
        anyhow::bail!("no supported distribution package manager found on this host");
    };

    // Before anything else: a previous run that was killed half-way leaves dpkg unable to install
    // anything until it is repaired, and nobody is there to run the repair by hand. dpkg itself has
    // no lock wait, so this waits for the lock (`run_waiting_for_dpkg_lock`) instead of giving up at
    // once — which is what it did, so a host busy with `unattended-upgrades` skipped the repair on
    // exactly the runs most likely to need it.
    repair_interrupted_dpkg(manager);

    refresh_package_lists(manager, &path);

    let args: &[&str] = match manager {
        // The two `--force-conf*` options are what stop dpkg stopping: without them a package
        // whose shipped config file differs from the one on disk opens an interactive prompt that
        // nothing is there to answer, and the run hangs until the timeout. Together they mean
        // "keep the existing file, install the new one alongside as .dpkg-dist" — the choice that
        // never overwrites an administrator's edit.
        PackageManager::Apt => &[
            "-y",
            "-o",
            APT_LOCK_WAIT,
            "-o",
            "Dpkg::Options::=--force-confdef",
            "-o",
            "Dpkg::Options::=--force-confold",
            "upgrade",
        ],
        PackageManager::Dnf | PackageManager::Yum => &["-y", "upgrade"],
        PackageManager::Zypper => &["--non-interactive", "update", "--auto-agree-with-licenses"],
        PackageManager::Pacman => &["-Syu", "--noconfirm"],
        PackageManager::Apk => &["upgrade"],
    };

    crate::logging::info(&format!("installing OS updates: {} {}", path.display(), args.join(" ")));

    let output = run(&path, args)?;
    let combined = format!(
        "{}{}",
        String::from_utf8_lossy(&output.stdout),
        String::from_utf8_lossy(&output.stderr)
    );

    if !output.status.success() {
        anyhow::bail!("{} {} exited with {}: {}", path.display(), args.join(" "), output.status, combined.trim());
    }

    clear_package_cache(manager, &path);

    Ok(combined)
}

/// How long `apt-get` waits for another process to let go of the dpkg lock before giving up.
/// `unattended-upgrades`, the software centre or somebody's own `apt` holds it for the length of
/// an install, and without a wait this agent failed at once with "Could not get lock" and filed a
/// failed update for something that would have cleared itself in a minute or two. Five minutes
/// covers a normal run; a lock held longer than that is a stuck process, which is reported.
///
/// A `-o` option, so an `apt` too old to know it (before 1.9.11) ignores it rather than refusing
/// to run: the behaviour is then the old one, not a new failure.
const APT_LOCK_WAIT: &str = "DPkg::Lock::Timeout=300";

/// Finishes an install that was interrupted (`dpkg --configure -a`). Apt hosts only: nothing else
/// here has the state. Best-effort and silent when there is nothing to do, which is almost always:
/// the command is a no-op on a healthy system. A failure is logged and the install carries on,
/// because the install itself will then fail with dpkg's own explanation, which the server turns
/// into "dpkg was interrupted" for the administrator.
///
/// The two `--force-conf*` options are the same ones the install uses, for the same reason: a
/// configuration-file prompt with nobody to answer it hangs the run.
///
/// Unlike `apt-get`, dpkg has no `Lock::Timeout`: while anything else holds the lock it exits at
/// once with "dpkg frontend lock was locked by another process". So it is retried for as long as
/// apt itself would wait ([`DPKG_LOCK_WAIT`]) and only while the failure is that one. Waiting is
/// all this does about a held lock — it never removes a lock file or signals the holder, because
/// a lock file deleted from under a running dpkg is how a database gets corrupted.
fn repair_interrupted_dpkg(manager: PackageManager) {
    if manager != PackageManager::Apt {
        return;
    }

    let Some(dpkg) = SEARCH_DIRS.iter().map(|dir| Path::new(dir).join("dpkg")).find(|path| path.is_file()) else {
        return;
    };

    let attempt = || run(&dpkg, &["--configure", "-a", "--force-confdef", "--force-confold"]);
    match run_waiting_for_dpkg_lock(attempt, std::thread::sleep, DPKG_LOCK_WAIT, DPKG_LOCK_RETRY_INTERVAL) {
        Ok(output) if output.status.success() => {}
        Ok(output) => crate::logging::warn(&format!(
            "repairing an interrupted dpkg state exited with {}: {}",
            output.status,
            String::from_utf8_lossy(&output.stderr).trim()
        )),
        Err(err) => crate::logging::warn(&format!("could not run dpkg --configure -a: {err:#}")),
    }
}

/// How long the repair waits for the dpkg lock: the same five minutes as [`APT_LOCK_WAIT`], so a
/// lock held longer than that is the same "stuck process" to both, and the install's own failure
/// then says so.
const DPKG_LOCK_WAIT: Duration = Duration::from_secs(300);

/// How often a repair blocked on the lock tries again.
const DPKG_LOCK_RETRY_INTERVAL: Duration = Duration::from_secs(5);

/// Runs `attempt` until it either succeeds, fails for a reason other than the dpkg lock, or has
/// been retried for `wait`. Whatever the last attempt returned is returned, so a lock still held
/// at the end reaches the caller's log as dpkg's own message. `sleep` is a parameter so the tests
/// can count the waits instead of sitting through them.
fn run_waiting_for_dpkg_lock(
    mut attempt: impl FnMut() -> Result<Output>,
    mut sleep: impl FnMut(Duration),
    wait: Duration,
    interval: Duration,
) -> Result<Output> {
    let mut waited = Duration::ZERO;
    loop {
        let output = attempt()?;
        if output.status.success() || !is_dpkg_lock_error(&String::from_utf8_lossy(&output.stderr)) || waited >= wait {
            return Ok(output);
        }
        sleep(interval);
        waited += interval;
    }
}

/// Whether dpkg failed because another process holds its lock, rather than for a reason waiting
/// will not fix. dpkg 1.20+ says "dpkg frontend lock was locked by another process with pid N"
/// (or "...is locked by another process" before it learned to name the pid); older ones and the
/// inner database lock say "dpkg status database is locked by another process". `run` forces
/// `LC_ALL=C`, so these are never translated.
fn is_dpkg_lock_error(stderr: &str) -> bool {
    stderr.contains("locked by another process") || stderr.contains("unable to lock dpkg status database")
}

/// Deletes the downloaded `.deb` files after a successful install (`apt-get clean`). Apt hosts
/// only, and only the cache: nothing installed is touched. They are never read again, and on a
/// small disk a few hundred megabytes of them is the difference between the next update fitting
/// or failing for lack of space. Best-effort: failing to tidy up never undoes an install.
///
/// Deliberately no `autoremove` here. That is the one apt command that uninstalls software, and an
/// unattended agent should not do that unasked — see the note on `install`.
fn clear_package_cache(manager: PackageManager, path: &Path) {
    if manager != PackageManager::Apt {
        return;
    }

    // `run` only fails when the command could not be started; `apt-get clean` exiting non-zero
    // (the archive lock held, a read-only cache) comes back as `Ok`, and was dropped unseen.
    match run(path, &["clean"]) {
        Ok(output) if output.status.success() => {}
        Ok(output) => crate::logging::warn(&format!(
            "clearing the package cache ({} clean) exited with {}: {}",
            path.display(),
            output.status,
            String::from_utf8_lossy(&output.stderr).trim()
        )),
        Err(err) => crate::logging::warn(&format!("could not clear the package cache: {err:#}")),
    }
}

/// Whether the host needs a restart before the updates it has installed are all in effect, and
/// what for.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct RestartState {
    pub required: bool,

    /// The packages that asked for it, for the administrator ("linux-image-6.8.0-52-generic"), or
    /// null when nothing named them.
    pub because: Option<String>,
}

/// Where Debian and Ubuntu's `update-notifier-common` (and `needrestart` on some) leave the flag
/// and the packages behind it. `/var/run` is a symlink to `/run`; either spelling works.
const REBOOT_REQUIRED_FILE: &str = "/var/run/reboot-required";
const REBOOT_REQUIRED_PACKAGES_FILE: &str = "/var/run/reboot-required.pkgs";

/// The script `update-notifier-common` ships to write the flag above. Its presence is how this
/// agent knows an absent flag file means "no restart needed" rather than "nothing here would have
/// written one". See [`restart_state_from`].
const NOTIFY_REBOOT_HOOK: &str = "/usr/share/update-notifier/notify-reboot-required";

/// Reads the host's restart state. `None` for anything but an apt host: nothing is reported there,
/// and the server treats "not reported" as "leave what you have", not as "no restart needed".
pub fn restart_state() -> Option<RestartState> {
    let (manager, _) = detect()?;
    if manager != PackageManager::Apt {
        return None;
    }

    let running = std::fs::read_to_string("/proc/sys/kernel/osrelease").ok();
    let installed: Vec<String> = std::fs::read_dir("/boot")
        .map(|entries| entries.filter_map(|e| e.ok()?.file_name().into_string().ok()).collect())
        .unwrap_or_default();

    Some(restart_state_from(
        Path::new(REBOOT_REQUIRED_FILE).exists(),
        std::fs::read_to_string(REBOOT_REQUIRED_PACKAGES_FILE).ok().as_deref(),
        Path::new(NOTIFY_REBOOT_HOOK).exists(),
        running.as_deref().map(str::trim),
        &installed,
    ))
}

/// The decision, separated from the file reads so it can be tested.
///
/// The flag file is the answer on Debian derivatives that have `update-notifier-common`: Ubuntu,
/// Pop!_OS, Mint. On plain Debian there is no such package, so a missing file proves nothing; there
/// the answer is whether a newer kernel is installed than the one running. That comparison is used
/// **only when the hook is absent**: on Ubuntu a person may have chosen an older kernel at boot on
/// purpose, and "a newer one exists" would then be a restart nagging them forever.
pub(crate) fn restart_state_from(
    flag_file_present: bool,
    packages_file: Option<&str>,
    notify_hook_present: bool,
    running_kernel: Option<&str>,
    boot_entries: &[String],
) -> RestartState {
    if flag_file_present {
        let mut names: Vec<&str> = packages_file.unwrap_or("").lines().map(str::trim).filter(|l| !l.is_empty()).collect();
        names.sort_unstable();
        names.dedup();
        let because = (!names.is_empty()).then(|| summarise_packages(&names, MAX_RESTART_BECAUSE_BYTES));
        return RestartState { required: true, because };
    }

    if !notify_hook_present {
        if let Some(running) = running_kernel {
            let (_, running_flavour) = split_kernel_release(running);

            // Only kernels of the running one's own flavour: Raspberry Pi OS installs every
            // flavour side by side (`rpi-v8` and `rpi-2712`; `rpi-v6`/`v7`/`v7l`/`v8` on 32-bit),
            // and the host boots the one its board needs. Comparing across them read the "2712"
            // in a Pi 5 kernel's name as a version number and told every Pi 4 it needed a restart
            // that no restart would ever clear.
            let newest = boot_entries
                .iter()
                .filter_map(|name| name.strip_prefix("vmlinuz-"))
                .filter(|release| split_kernel_release(release).1 == running_flavour)
                .max_by_key(|release| version_key(release));

            if let Some(newest) = newest {
                if version_key(newest) > version_key(running) {
                    return RestartState { required: true, because: Some(format!("linux-image-{newest}")) };
                }
            }
        }
    }

    RestartState { required: false, because: None }
}

/// The most `restart_because` is allowed to be, in bytes. The server's column (and, before 1.41.0,
/// its validators — `CreateHostCommandValidator`, `ReportOperatingSystemPatchedCommandValidator`)
/// stop at `Host.MaxRestartBecauseLength`, 500 UTF-16 units; a byte count is never smaller than
/// that, so staying under it in bytes stays under it there too. Well under, deliberately: the
/// validators rejected the *whole check-in* over this one field, nothing retried, and a host that
/// had gone months without a reboot simply stopped checking in.
const MAX_RESTART_BECAUSE_BYTES: usize = 400;

/// Joins the package names that asked for a restart, cut to at most `max_bytes` as whole names
/// followed by "and N more" — never a name cut in half, and never mid-character. Should not even
/// one name fit, it says only how many there are.
fn summarise_packages(names: &[&str], max_bytes: usize) -> String {
    let full = names.join(", ");
    if full.len() <= max_bytes {
        return full;
    }

    let mut shown = String::new();
    let mut count = 0;
    for name in names {
        let candidate = if shown.is_empty() { (*name).to_string() } else { format!("{shown}, {name}") };
        let rest = names.len() - count - 1;
        if candidate.len() + format!(" and {rest} more").len() > max_bytes {
            break;
        }
        shown = candidate;
        count += 1;
    }

    if count == 0 {
        return format!("{} packages", names.len());
    }
    format!("{shown} and {} more", names.len() - count)
}

/// Splits a kernel release into the part that versions it and the flavour it was built for:
/// "6.1.0-26-amd64" is ("6.1.0-26", "amd64"), "6.6.51+rpt-rpi-2712" is ("6.6.51+rpt", "rpi-2712"),
/// "6.1.0-rpi7-rpi-v8" is ("6.1.0-rpi7", "rpi-v8").
///
/// The first `-` field is always the upstream version. After it comes an ABI field in the older
/// Debian and Raspberry Pi scheme ("-26-", "-rpi7-", always carrying a digit), and none in the newer
/// one, which marks the version with a `+` suffix instead ("+deb13", "+bpo", "+rpt"). Everything
/// after that is the flavour, digits and all ("rpi-2712", "686-pae"), which is the point: a digit in
/// a flavour is a board or an architecture, not a version.
fn split_kernel_release(release: &str) -> (&str, &str) {
    let Some((upstream, rest)) = release.split_once('-') else {
        return (release, "");
    };
    if upstream.contains('+') {
        return (upstream, rest);
    }

    let abi_len = match rest.split_once('-') {
        Some((abi, _)) if abi.contains(|c: char| c.is_ascii_digit()) => abi.len(),
        // A lone field is a flavour unless it is all digits: "6.1.21-v8+" is the v8 flavour.
        None if !rest.is_empty() && rest.chars().all(|c| c.is_ascii_digit()) => rest.len(),
        _ => return (upstream, rest),
    };

    let version_len = upstream.len() + 1 + abi_len;
    (&release[..version_len], release[version_len..].trim_start_matches('-'))
}

/// The numbers in a kernel release's version part, in order: "6.8.0-52-generic" is [6, 8, 0, 52].
/// The flavour is left out (see [`split_kernel_release`]) — only releases of one flavour are ever
/// compared, and a flavour's digits ("2712", "v8") are not a version.
fn version_key(release: &str) -> Vec<u64> {
    split_kernel_release(release)
        .0
        .split(|c: char| !c.is_ascii_digit())
        .filter(|part| !part.is_empty())
        .filter_map(|part| part.parse().ok())
        .collect()
}

/// Runs a package-manager command with an environment that cannot prompt. `DEBIAN_FRONTEND`
/// covers debconf on Debian derivatives; forcing `LC_ALL=C` matters everywhere, because every
/// parser in `count_pending_updates` reads output that would otherwise be translated into
/// whatever locale the host is set to.
fn run(path: &Path, args: &[&str]) -> Result<Output> {
    Command::new(path)
        .args(args)
        .env("DEBIAN_FRONTEND", "noninteractive")
        .env("LC_ALL", "C")
        .env("LANG", "C")
        .output()
        .with_context(|| format!("failed to run {}", path.display()))
}

#[derive(Debug, Serialize)]
#[serde(rename_all = "camelCase")]
struct ReportOsPatchResultRequest<'a> {
    serial_number: &'a str,

    /// Whether the host now needs a restart, read straight after the install. Left out when this
    /// host cannot say (not an apt host), which the server reads as "leave what you have".
    #[serde(skip_serializing_if = "Option::is_none")]
    restart_required: Option<bool>,
    #[serde(skip_serializing_if = "Option::is_none")]
    restart_because: Option<&'a str>,
}

/// Tells the server this host's pending OS updates were just successfully installed, so its
/// pending-update flag clears immediately rather than waiting on this host's next check-in to
/// re-derive it. Best-effort, the same as `upgrade::report_patch_result`: the update already
/// succeeded locally by the time this is called, so a failure here is only logged, never treated
/// as undoing the install.
pub fn report_patched(
    client: &reqwest::blocking::Client,
    config: &Config,
    serial_number: &str,
    restart: Option<&RestartState>,
) {
    let request = ReportOsPatchResultRequest {
        serial_number,
        restart_required: restart.map(|r| r.required),
        restart_because: restart.and_then(|r| r.because.as_deref()),
    };

    match client.post(config.os_patch_result_url()).json(&request).send() {
        Ok(response) if response.status().is_success() => {
            crate::logging::info("reported successful OS update install to the server");
        }
        Ok(response) => {
            crate::logging::warn(&format!("server rejected OS patch-result report (HTTP {})", response.status()));
        }
        Err(err) => {
            crate::logging::warn(&format!("could not report the successful OS update install to the server: {err:#}"));
        }
    }
}

/// The name a failed system package update is filed under on the admin UI's Failed Updates screen.
///
/// The same arrangement the macOS and Windows agents have: the application-failure route is already
/// in nginx's agent-certificate regex and already carries `[RequireAgentIdentity]`, and
/// `ReportPatchFailureCommandHandler` files a name that resolves to no upgrade path with a null
/// platform, which the screen shows as a failure it cannot offer an AI repair for. That is right
/// for an OS update, which has no script to repair. `ReportOperatingSystemPatchedCommandHandler`
/// closes the row when a success is reported, so the two have to agree on this string.
pub const OS_FAILURE_APPLICATION_NAME: &str = "Linux";

/// Files a failed system package update on the Failed Updates screen, under
/// [`OS_FAILURE_APPLICATION_NAME`], with the package manager's captured output.
///
/// Called from the root service only (`ServiceHandler::install_os_updates`), the one place
/// `install` runs and the side holding the identity; the per-user half reaches it through `queue`
/// and reports nothing, so a failure is never recorded twice.
pub fn report_failed(
    client: &reqwest::blocking::Client,
    config: &Config,
    serial_number: &str,
    error: &anyhow::Error,
) {
    let installed_version = crate::system_info::operating_system().unwrap_or_else(|_| "unknown".to_string());

    crate::upgrade::report_failure(client, config, serial_number, OS_FAILURE_APPLICATION_NAME, &installed_version, None, error);
}

#[cfg(test)]
mod tests {
    use super::*;

    /// `ReportOperatingSystemPatchedCommandHandler.LinuxFailureApplicationName` closes the rows
    /// filed under this name when a success is reported, and nothing checks the two agree.
    #[test]
    fn a_failed_install_is_filed_under_the_name_the_server_closes_on_success() {
        assert_eq!(OS_FAILURE_APPLICATION_NAME, "Linux");
    }

    #[test]
    fn count_pending_updates_apt_counts_only_inst_lines() {
        let output = concat!(
            "NOTE: This is only a simulation!\n",
            "Reading package lists...\n",
            "Inst libssl3 [3.0.11-1] (3.0.13-1 Ubuntu:24.04 [amd64])\n",
            "Conf libssl3 (3.0.13-1 Ubuntu:24.04 [amd64])\n",
            "Inst curl [8.5.0] (8.5.1 Ubuntu:24.04 [amd64])\n",
            "Conf curl (8.5.1 Ubuntu:24.04 [amd64])\n",
        );

        assert_eq!(count_pending_updates(PackageManager::Apt, output), 2);
    }

    #[test]
    fn count_pending_updates_apt_reports_nothing_when_up_to_date() {
        let output = "NOTE: This is only a simulation!\nReading package lists...\nBuilding dependency tree...\n";

        assert_eq!(count_pending_updates(PackageManager::Apt, output), 0);
    }

    #[test]
    fn count_pending_updates_dnf_counts_table_rows_and_skips_the_preamble() {
        let output = concat!(
            "Last metadata expiration check: 0:12:31 ago on Fri 30 Aug 2026.\n",
            "\n",
            "curl.x86_64                    8.6.0-3.fc40                    updates\n",
            "kernel-core.x86_64             6.9.4-200.fc40                  updates\n",
        );

        assert_eq!(count_pending_updates(PackageManager::Dnf, output), 2);
    }

    #[test]
    fn count_pending_updates_dnf_reports_nothing_for_empty_output() {
        assert_eq!(count_pending_updates(PackageManager::Dnf, ""), 0);
    }

    #[test]
    fn count_pending_updates_zypper_counts_status_rows_only() {
        let output = concat!(
            "S | Repository | Name  | Current Version | Available Version | Arch\n",
            "--+------------+-------+-----------------+-------------------+-------\n",
            "v | Update     | curl  | 8.0.1-150400    | 8.6.0-150400      | x86_64\n",
            "v | Update     | glibc | 2.38-150600     | 2.38-150600.1     | x86_64\n",
        );

        assert_eq!(count_pending_updates(PackageManager::Zypper, output), 2);
    }

    #[test]
    fn count_pending_updates_pacman_counts_arrow_lines() {
        let output = "curl 8.7.1-1 -> 8.8.0-1\nlinux 6.9.3.arch1-1 -> 6.9.4.arch1-1\n";

        assert_eq!(count_pending_updates(PackageManager::Pacman, output), 2);
    }

    #[test]
    fn count_pending_updates_apk_skips_the_header() {
        let output = "Installed:                Available:\ncurl-8.5.0-r0         <  8.6.0-r0\n";

        assert_eq!(count_pending_updates(PackageManager::Apk, output), 1);
    }

    /// An unrecognized distribution must report "nothing pending" rather than failing the whole
    /// check-in — see `detect`'s own doc comment.
    #[test]
    fn os_update_status_defaults_to_nothing_available() {
        assert_eq!(
            OsUpdateStatus::default(),
            OsUpdateStatus { available: false, latest_version: None, pending_package_names: None }
        );
    }

    #[test]
    fn extract_pending_package_names_apt_reads_the_binary_name() {
        let output = concat!(
            "NOTE: This is only a simulation!\n",
            "Reading package lists...\n",
            "Inst libssl3 [3.0.11-1] (3.0.13-1 Ubuntu:24.04 [amd64])\n",
            "Conf libssl3 (3.0.13-1 Ubuntu:24.04 [amd64])\n",
            "Inst curl [8.5.0] (8.5.1 Ubuntu:24.04 [amd64])\n",
        );

        let names = extract_pending_package_names(PackageManager::Apt, output).unwrap();

        assert_eq!(names, vec!["libssl3", "curl"]);
    }

    #[test]
    fn extract_pending_package_names_apt_reports_an_empty_list_when_up_to_date() {
        let output = "NOTE: This is only a simulation!\nReading package lists...\n";

        assert_eq!(extract_pending_package_names(PackageManager::Apt, output), Some(Vec::new()));
    }

    #[test]
    fn extract_pending_package_names_dnf_strips_the_arch_suffix_at_the_last_dot() {
        let output = concat!(
            "Last metadata expiration check: 0:12:31 ago on Fri 30 Aug 2026.\n",
            "\n",
            "curl.x86_64                    8.6.0-3.fc40                    updates\n",
            "python3.11.x86_64              3.11.9-1.fc40                   updates\n",
        );

        let names = extract_pending_package_names(PackageManager::Dnf, output).unwrap();

        assert_eq!(names, vec!["curl", "python3.11"]);
    }

    #[test]
    fn extract_pending_package_names_zypper_reads_the_name_column() {
        let output = concat!(
            "S | Repository | Name  | Current Version | Available Version | Arch\n",
            "--+------------+-------+-----------------+-------------------+-------\n",
            "v | Update     | curl  | 8.0.1-150400    | 8.6.0-150400      | x86_64\n",
        );

        let names = extract_pending_package_names(PackageManager::Zypper, output).unwrap();

        assert_eq!(names, vec!["curl"]);
    }

    #[test]
    fn extract_pending_package_names_is_none_for_pacman_and_apk() {
        // `scan_os_packages` never reports a package from either manager's distribution, so there
        // is no installed-package inventory to attribute a pending name to.
        assert_eq!(extract_pending_package_names(PackageManager::Pacman, "curl 8.7.1-1 -> 8.8.0-1\n"), None);
        assert_eq!(extract_pending_package_names(PackageManager::Apk, "curl-8.5.0-r0 < 8.6.0-r0\n"), None);
    }

    // ----- restart state -----

    fn boot(names: &[&str]) -> Vec<String> {
        names.iter().map(|n| n.to_string()).collect()
    }

    #[test]
    fn a_flag_file_means_a_restart_and_names_the_packages_once_each() {
        let state = restart_state_from(true, Some("linux-image-6.8.0-52-generic\nlinux-base\nlinux-base\n"), true, None, &[]);

        assert!(state.required);
        assert_eq!(state.because.as_deref(), Some("linux-base, linux-image-6.8.0-52-generic"));
    }

    #[test]
    fn a_flag_file_with_no_package_list_still_means_a_restart() {
        let state = restart_state_from(true, None, true, None, &[]);

        assert_eq!(state, RestartState { required: true, because: None });
    }

    #[test]
    fn no_flag_file_on_a_host_that_would_write_one_means_no_restart_even_with_a_newer_kernel_installed() {
        // Ubuntu, with an older kernel picked at the boot menu on purpose.
        let state = restart_state_from(false, None, true, Some("6.5.0-1-generic"), &boot(&["vmlinuz-6.8.0-52-generic"]));

        assert!(!state.required);
    }

    #[test]
    fn plain_debian_with_a_newer_kernel_than_the_running_one_needs_a_restart() {
        let state = restart_state_from(
            false,
            None,
            false,
            Some("6.1.0-25-amd64"),
            &boot(&["vmlinuz-6.1.0-23-amd64", "vmlinuz-6.1.0-26-amd64", "config-6.1.0-26-amd64"]),
        );

        assert_eq!(state, RestartState { required: true, because: Some("linux-image-6.1.0-26-amd64".to_string()) });
    }

    #[test]
    fn plain_debian_running_its_newest_kernel_needs_no_restart() {
        let state = restart_state_from(false, None, false, Some("6.1.0-26-amd64"), &boot(&["vmlinuz-6.1.0-23-amd64", "vmlinuz-6.1.0-26-amd64"]));

        assert!(!state.required);
    }

    #[test]
    fn kernel_versions_compare_as_numbers_not_text() {
        // As text "6.1.0-9" sorts after "6.1.0-10".
        let state = restart_state_from(false, None, false, Some("6.1.0-9-amd64"), &boot(&["vmlinuz-6.1.0-10-amd64"]));

        assert!(state.required);
    }

    #[test]
    fn nothing_readable_means_no_restart_rather_than_a_guess() {
        assert!(!restart_state_from(false, None, false, None, &[]).required);
        assert!(!restart_state_from(false, None, false, Some("6.1.0-26-amd64"), &[]).required);
    }

    // ----- restart cause length -----

    #[test]
    fn a_short_package_list_is_reported_whole() {
        assert_eq!(summarise_packages(&["linux-base", "linux-image-6.8.0-52-generic"], 400), "linux-base, linux-image-6.8.0-52-generic");
    }

    /// A host that has gone months without a reboot can have hundreds of names in
    /// `reboot-required.pkgs`. Joined whole, that went past the server's 500-character limit, the
    /// check-in was refused, and the host went stale.
    #[test]
    fn a_long_package_list_is_cut_to_whole_names_and_a_count() {
        let owned: Vec<String> = (0..300).map(|i| format!("libexample-package-{i:03}")).collect();
        let names: Vec<&str> = owned.iter().map(String::as_str).collect();
        let packages_file = owned.join("\n");

        let state = restart_state_from(true, Some(&packages_file), true, None, &[]);
        let because = state.because.expect("the packages are still named");

        assert!(because.len() <= MAX_RESTART_BECAUSE_BYTES, "{} bytes", because.len());
        assert!(because.encode_utf16().count() < 500, "under the server's Host.MaxRestartBecauseLength");
        assert!(because.starts_with("libexample-package-000, libexample-package-001, "));
        let (shown, more) = because.rsplit_once(" and ").unwrap();
        let shown_count = shown.split(", ").count();
        assert!(shown.split(", ").all(|name| names.contains(&name)), "no name is cut in half: {shown}");
        assert_eq!(more, format!("{} more", 300 - shown_count));
    }

    #[test]
    fn a_cut_package_list_never_splits_a_multibyte_character() {
        let names = ["paquet-é-un", "paquet-é-deux", "paquet-é-trois"];

        let summary = summarise_packages(&names, 30);

        assert!(summary.len() <= 30);
        assert_eq!(summary, "paquet-é-un and 2 more");
    }

    #[test]
    fn a_single_name_too_long_to_show_is_reported_as_a_count() {
        let long = "x".repeat(450);

        assert_eq!(summarise_packages(&[long.as_str(), "linux-base"], 400), "2 packages");
    }

    // ----- kernel flavours -----

    #[test]
    fn kernel_releases_split_into_version_and_flavour() {
        assert_eq!(split_kernel_release("6.1.0-26-amd64"), ("6.1.0-26", "amd64"));
        assert_eq!(split_kernel_release("6.1.0-26-686-pae"), ("6.1.0-26", "686-pae"));
        assert_eq!(split_kernel_release("6.8.0-52-generic"), ("6.8.0-52", "generic"));
        assert_eq!(split_kernel_release("6.12.38+deb13-amd64"), ("6.12.38+deb13", "amd64"));
        assert_eq!(split_kernel_release("6.12.38+deb13-686-pae"), ("6.12.38+deb13", "686-pae"));
        assert_eq!(split_kernel_release("6.6.51+rpt-rpi-2712"), ("6.6.51+rpt", "rpi-2712"));
        assert_eq!(split_kernel_release("6.6.51+rpt-rpi-v8"), ("6.6.51+rpt", "rpi-v8"));
        assert_eq!(split_kernel_release("6.1.0-rpi7-rpi-v8"), ("6.1.0-rpi7", "rpi-v8"));
        assert_eq!(split_kernel_release("6.1.21-v8+"), ("6.1.21", "v8+"));
        assert_eq!(split_kernel_release("6.1.21+"), ("6.1.21+", ""));
        assert_eq!(split_kernel_release("6.1.0"), ("6.1.0", ""));
    }

    /// The bug: a Pi 4 boots `rpi-v8`, the same package also installs `rpi-2712` for the Pi 5,
    /// and "2712" read as a version part made the Pi 5 kernel look newer forever.
    #[test]
    fn a_pi_4_running_its_own_flavour_needs_no_restart_with_the_pi_5_kernel_beside_it() {
        let boot_dir = boot(&[
            "vmlinuz-6.6.51+rpt-rpi-2712",
            "vmlinuz-6.6.51+rpt-rpi-v8",
            "initrd.img-6.6.51+rpt-rpi-2712",
            "initrd.img-6.6.51+rpt-rpi-v8",
            "config-6.6.51+rpt-rpi-v8",
        ]);

        let state = restart_state_from(false, None, false, Some("6.6.51+rpt-rpi-v8"), &boot_dir);

        assert_eq!(state, RestartState { required: false, because: None });
    }

    #[test]
    fn a_pi_5_running_its_own_flavour_needs_no_restart() {
        let boot_dir = boot(&["vmlinuz-6.6.51+rpt-rpi-2712", "vmlinuz-6.6.51+rpt-rpi-v8"]);

        assert!(!restart_state_from(false, None, false, Some("6.6.51+rpt-rpi-2712"), &boot_dir).required);
    }

    #[test]
    fn a_32_bit_pi_with_every_flavour_installed_needs_no_restart() {
        let boot_dir = boot(&[
            "vmlinuz-6.6.51+rpt-rpi-v6",
            "vmlinuz-6.6.51+rpt-rpi-v7",
            "vmlinuz-6.6.51+rpt-rpi-v7l",
            "vmlinuz-6.6.51+rpt-rpi-v8",
        ]);

        assert!(!restart_state_from(false, None, false, Some("6.6.51+rpt-rpi-v7"), &boot_dir).required);
        assert!(!restart_state_from(false, None, false, Some("6.6.51+rpt-rpi-v7l"), &boot_dir).required);
    }

    #[test]
    fn a_pi_with_a_newer_kernel_of_its_own_flavour_needs_a_restart_for_that_one() {
        let boot_dir = boot(&[
            "vmlinuz-6.6.51+rpt-rpi-2712",
            "vmlinuz-6.6.51+rpt-rpi-v8",
            "vmlinuz-6.6.62+rpt-rpi-2712",
            "vmlinuz-6.6.62+rpt-rpi-v8",
        ]);

        let state = restart_state_from(false, None, false, Some("6.6.51+rpt-rpi-v8"), &boot_dir);

        assert_eq!(state, RestartState { required: true, because: Some("linux-image-6.6.62+rpt-rpi-v8".to_string()) });
    }

    /// Early Bookworm images name the ABI ("rpi7") in its own field; its digit is a version, the
    /// flavour's is not.
    #[test]
    fn an_early_bookworm_pi_compares_the_abi_field_but_not_the_flavour() {
        let boot_dir = boot(&["vmlinuz-6.1.0-rpi7-rpi-2712", "vmlinuz-6.1.0-rpi7-rpi-v8", "vmlinuz-6.1.0-rpi8-rpi-2712", "vmlinuz-6.1.0-rpi8-rpi-v8"]);

        let behind = restart_state_from(false, None, false, Some("6.1.0-rpi7-rpi-v8"), &boot_dir);
        assert_eq!(behind.because.as_deref(), Some("linux-image-6.1.0-rpi8-rpi-v8"));

        assert!(!restart_state_from(false, None, false, Some("6.1.0-rpi8-rpi-v8"), &boot_dir).required);
    }

    #[test]
    fn debian_compares_across_its_old_and_new_kernel_naming() {
        let boot_dir = boot(&["vmlinuz-6.1.0-26-amd64", "vmlinuz-6.12.38+deb13-amd64"]);

        let state = restart_state_from(false, None, false, Some("6.1.0-26-amd64"), &boot_dir);

        assert_eq!(state.because.as_deref(), Some("linux-image-6.12.38+deb13-amd64"));
    }

    #[test]
    fn a_kernel_of_another_flavour_is_never_the_newer_one() {
        // A realtime kernel installed alongside the standard one, at a higher ABI.
        let boot_dir = boot(&["vmlinuz-6.1.0-26-amd64", "vmlinuz-6.1.0-27-rt-amd64"]);

        assert!(!restart_state_from(false, None, false, Some("6.1.0-26-amd64"), &boot_dir).required);
    }

    // ----- dpkg lock -----

    fn exited(code: i32, stderr: &str) -> Output {
        use std::os::unix::process::ExitStatusExt;
        Output { status: std::process::ExitStatus::from_raw(code << 8), stdout: Vec::new(), stderr: stderr.as_bytes().to_vec() }
    }

    const FRONTEND_LOCKED: &str = "dpkg: error: dpkg frontend lock was locked by another process with pid 4242\nNote: removing the lock file is always wrong, and can end up damaging the locked area\nand the entire system. See <https://wiki.debian.org/Teams/Dpkg/FAQ#db-lock>.\n";

    #[test]
    fn dpkg_lock_errors_are_recognised_and_other_failures_are_not() {
        assert!(is_dpkg_lock_error(FRONTEND_LOCKED));
        assert!(is_dpkg_lock_error("dpkg: error: dpkg frontend lock is locked by another process\n"));
        assert!(is_dpkg_lock_error("dpkg: error: dpkg status database is locked by another process\n"));
        assert!(!is_dpkg_lock_error("dpkg: error: requested operation requires superuser privilege\n"));
        assert!(!is_dpkg_lock_error("dpkg: error processing package foo (--configure):\n installed foo package post-installation script subprocess returned error exit status 1\n"));
    }

    /// The finding: the repair used to run once, before anything waited for the lock, so on a host
    /// busy with `unattended-upgrades` it failed at once and was skipped.
    #[test]
    fn the_repair_waits_for_a_held_lock_and_then_runs() {
        let mut attempts = 0;
        let mut sleeps = 0;

        let output = run_waiting_for_dpkg_lock(
            || {
                attempts += 1;
                Ok(if attempts < 4 { exited(2, FRONTEND_LOCKED) } else { exited(0, "") })
            },
            |_| sleeps += 1,
            DPKG_LOCK_WAIT,
            DPKG_LOCK_RETRY_INTERVAL,
        )
        .unwrap();

        assert!(output.status.success());
        assert_eq!(attempts, 4);
        assert_eq!(sleeps, 3);
    }

    #[test]
    fn the_repair_gives_up_waiting_after_the_lock_timeout_and_returns_dpkgs_message() {
        let mut attempts = 0;
        let mut slept = Duration::ZERO;

        let output = run_waiting_for_dpkg_lock(
            || {
                attempts += 1;
                Ok(exited(2, FRONTEND_LOCKED))
            },
            |d| slept += d,
            DPKG_LOCK_WAIT,
            DPKG_LOCK_RETRY_INTERVAL,
        )
        .unwrap();

        assert!(!output.status.success());
        assert_eq!(slept, DPKG_LOCK_WAIT);
        assert_eq!(attempts, (DPKG_LOCK_WAIT.as_secs() / DPKG_LOCK_RETRY_INTERVAL.as_secs()) as usize + 1);
    }

    #[test]
    fn the_repair_does_not_retry_a_failure_that_is_not_the_lock() {
        let mut attempts = 0;

        let output = run_waiting_for_dpkg_lock(
            || {
                attempts += 1;
                Ok(exited(1, "dpkg: error processing package foo (--configure):\n"))
            },
            |_| panic!("must not wait on a failure waiting cannot fix"),
            DPKG_LOCK_WAIT,
            DPKG_LOCK_RETRY_INTERVAL,
        )
        .unwrap();

        assert!(!output.status.success());
        assert_eq!(attempts, 1);
    }

    #[test]
    fn the_repair_waits_exactly_as_long_as_apt_does() {
        assert_eq!(APT_LOCK_WAIT, format!("DPkg::Lock::Timeout={}", DPKG_LOCK_WAIT.as_secs()));
    }

    #[test]
    fn apt_waits_for_the_package_lock_instead_of_failing_at_once() {
        assert_eq!(APT_LOCK_WAIT, "DPkg::Lock::Timeout=300");
    }
}
