use std::path::{Path, PathBuf};
use std::process::{Command, Output};

use anyhow::{Context, Result};
use serde::Serialize;

use crate::config::Config;

/// The result of a standard OS-update check: whether one is pending, and the version it would
/// bring the host to, when the check can determine that.
#[derive(Debug, Clone, Default, PartialEq, Eq)]
pub struct OsUpdateStatus {
    pub available: bool,
    pub latest_version: Option<String>,
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
    let status = OsUpdateStatus { available: count > 0, latest_version: None };

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
        PackageManager::Apt => &["update", "-qq"],
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

    Ok(combined)
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
}

/// Tells the server this host's pending OS updates were just successfully installed, so its
/// pending-update flag clears immediately rather than waiting on this host's next check-in to
/// re-derive it. Best-effort, the same as `upgrade::report_patch_result`: the update already
/// succeeded locally by the time this is called, so a failure here is only logged, never treated
/// as undoing the install.
pub fn report_patched(client: &reqwest::blocking::Client, config: &Config, serial_number: &str) {
    let request = ReportOsPatchResultRequest { serial_number };

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

#[cfg(test)]
mod tests {
    use super::*;

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
        assert_eq!(OsUpdateStatus::default(), OsUpdateStatus { available: false, latest_version: None });
    }
}
