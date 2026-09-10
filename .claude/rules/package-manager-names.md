---
paths:
  - "src/Kintsugi.Domain/**"
  - "src/Kintsugi.Application/**"
  - "clients/*/src/system_info.rs"
  - "clients/*/src/upgrade.rs"
---

# Couplings: package-manager names and patchability

The strings a manager is known by, and the rules deciding whether one of its rows can be patched at
all, are split between the server's catalog and what each agent reports. A rename on either side
silently stops an entire manager's applications resolving.

- `PackageManagerCatalog`'s names are the strings agents report in `InstalledApp.package_manager`.
  A rename on either side silently stops an entire manager's applications resolving.
- A package-manager row is only patchable if the *agent* reported an `applicationIdentifier` for that
  installed application — `is_patchable` requires one for any `Script` row, and it comes from the
  `InstalledApplication`, not from the `UpgradePath` (which always has one, falling back to the
  name). The Windows and Linux agents set it for every managed package; the macOS agent reports the
  Homebrew token for every formula and cask **except** one whose upgrade would make `brew` reach for
  `sudo` (`system_info::cask_requires_root`: a `pkg`/`installer` artifact, or a `pkgutil`, `kext`,
  `script` or `launchctl` uninstall). Leaving the identifier off is deliberately how a row is kept out
  of a patch cycle — the alternative is the Nextcloud loop, where every cycle quits the application,
  fails inside `brew`, and leaves it stopped. Do not "fix" a Homebrew row that never patches by
  adding an identifier server-side.
- **"Is this installation behind" is answered by the agent's package manager first and a version
  comparison second, and the Linux agent is the only one that supplies the first answer.**
  `InstalledApp.update_available` (→ `ApplicationEntry.UpdateAvailable` →
  `InstalledApplication.UpdateAvailable`) is what `flatpak remote-ls --updates` / `snap refresh
  --list` said about exactly this installation; `UpgradePathRepository.InstallationUpdateStatus`
  takes it whenever present and only otherwise compares `LatestVersion` against the installed
  version. It exists because the comparison is wrong for both Linux managers in ways that all read
  as "current": both ship rebuilds under an unchanged version string, and Flatpak prints a version
  in `remote-ls` only when the host's *cached* appstream data holds one — a cache nothing on a fleet
  host refreshes, since the agent is the thing that would run `flatpak update`. Worse, with that
  column empty flatpak's table printer drops the trailing tab too, so the line is the bare
  application id, and a parser requiring two columns (as `parse_flatpak_updates` did) discards the
  update entirely. That is how a Linux host with pending updates showed 0 app updates. Verified
  against Flatpak 1.14.10 in a container: `org.gnome.Calculator` alone before `flatpak update
  --appstream`, `org.gnome.Calculator\t50.0` after. Three things follow. The verdict is
  `Option<bool>` and a failed listing must stay `None` — an empty map would tell the server every
  app is current. A verdict of `true` with no version still seeds the `pm:` row
  (`UpsertPackageManagerUpgradePathsAsync`), or the installation is neither counted nor
  patchable, and it must not erase a `LatestVersion` the row already had from `--update-version`.
  And a `ReportPatchResult` clears the verdict along with recording the new version, or the host
  stays counted as behind until its next inventory report. macOS and Windows leave the field
  `None` on purpose: `brew outdated`, winget's Available column and `choco outdated` only ever name
  a package whose version string changed, so there `available_version` already is the verdict.
- **`brew info --json=v2` writes a cask stanza as a bare string when it names one item and as an
  array when it names several**, and reading only the array form is a security bug rather than a
  missed optimization. `strings_in` in the macOS agent's `system_info.rs` handles both. It shipped
  wrong: `nextcloud` declares `uninstall delete: "/Applications/Nextcloud.app"` as a single string,
  so the bundle name never reached `cask_app_bundle_names`, `scan_applications_folder` stopped
  recognizing it as cask-installed, and it was reported a *second* time as a standalone application
  — carrying a `CFBundleIdentifier`. That identifier is exactly what `is_patchable` requires before
  it will run a `Script` row, so a Homebrew row the per-user process **cannot** patch became
  eligible for patching. Every cycle then quit Nextcloud (Homebrew's `start_upgrade` quits the app
  before installing, and only reopens it on success), failed, and left the client stopped.
- **A `pkg`-artifact cask cannot be upgraded by the macOS agent at all, and the failure is
  disguised.** `Cask::Pkg#uninstall` pipes the NUL-joined BOM (371 KB for nextcloud) into
  `sudo -u root -E -- /usr/bin/xargs -0 -- /bin/rm --`; the per-user process has no TTY and no
  `SUDO_ASKPASS`, so sudo exits before reading and Ruby's `Errno::EPIPE` surfaces as
  `Error: <cask>: Broken pipe`. Homebrew's `SystemCommand#each_output_line` writes stdin *before*
  starting its output-reader thread, so sudo's real stderr is never reported. There is no
  arrangement that fixes this inside Homebrew: `brew` refuses to run as root (`brew.sh`'s
  `check-run-command-as-root`), `as-console-user` immediately drops back to the console user,
  `SUDO_ASKPASS` still needs a real password, and `HOMEBREW_SUDO_THROUGH_SUDO_USER` is only
  passwordless if brew is already root. Root-requiring casks are therefore **not agent-patchable**;
  do not try to route them through the root queue by having the daemon drive `brew`. The macOS
  agent's `system_info::cask_requires_root` is what keeps them off the patch list, by reporting
  them without an `applicationIdentifier` — see the identifier bullet above.
