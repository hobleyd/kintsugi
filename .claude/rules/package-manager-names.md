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
  adding an identifier server-side. A `pkg` cask whose bundle is in /Applications is no longer
  reported as a Homebrew row at all — see the last bullet.
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
  passwordless if brew is already root. Root-requiring casks are therefore **not patchable through
  Homebrew**; do not try to route them through the root queue by having the daemon drive `brew`.
  The macOS agent's `system_info::cask_requires_root` is what keeps them off the patch list, by
  reporting them without an `applicationIdentifier` — see the identifier bullet above.
- **So a `pkg` cask whose bundle the scan can see leaves Homebrew, and is patched as a
  standalone bundle instead** (macOS agent 0.16.0, `system_info::pkg_casks_leaving_homebrew`).
  The same application *not* under Homebrew was always patchable: the scan reports it with its
  `CFBundleIdentifier`, the server researches a `macOS`-bucket script that fetches the vendor's
  `.pkg` and runs `installer -pkg … -target /`, and `upgrade::runs_as_root` sends the row to the
  root daemon, which asks nobody for a password. "Leaves" means `forget_cask` removes
  `$(brew --caskroom)/<token>` as Homebrew's owner — the record, not the files, and not `brew
  uninstall`, whose stanza is the very thing that needs the password — and the report in the same
  scan omits the Homebrew row and stops shielding the bundle from `scan_installed_bundles`. The
  server needs nothing but its prompt knowing where bundles live: the bundle arrives as a new
  standalone application (often under a different name — `Microsoft Teams`, where the cask was
  `microsoft-teams`; `temurin-26`, where the cask was `temurin`), `ResolvePath` finds no row for
  it, and "Find Upgrade Paths" researches one, unsigned, for a human to review. The old
  `pm:Homebrew` row for the token is left behind unused. Where the bundle is comes from the cask's
  own stanzas **and** from its `pkgutil` receipts (`pkgutil --files`; the stanzas drift —
  `displaylink` deletes a folder from an older layout while its receipt names `DisplayLink
  Manager.app`), with the receipt ids re-spelled for the *installed* version because `brew info`
  describes the catalog's cask (`net.temurin.27.jdk` on a Mac whose receipt is
  `net.temurin.26.jdk`). Three outcomes, each tested: a bundle on disk → leaves and the scan
  reports it; bundles named but none on disk → leaves as a ghost record (Adobe's updater moved
  Reader into `Adobe Acrobat DC/Adobe Acrobat.app` under a new name; the scan reports what is
  really there); nothing named anywhere the scan looks → stays, unpatchable and visible, as
  before. And only a cask with a `pkg`/`installer` artifact qualifies (`cask_installs_a_pkg`): one
  that needs root for its *uninstall* alone is user-owned on disk and stays in Homebrew.
- **"Where the scan looks" is one function, `system_info::scanned_bundle_path`, and the macOS
  research prompt repeats it in words.** `/Applications/*.app`, `/Applications/<Folder>/*.app`
  (one level — vendors ship suites in a folder, and Adobe *moves* Reader into one), and
  `/Library/Java/JavaVirtualMachines/*.jdk`. A JDK is named by its directory (`temurin-26`), not
  its plist — `CFBundleName` carries the version and every OpenJDK build shares one
  `CFBundleIdentifier` — and the prompt in `AiUpgradePathResearchClient` tells the script to find
  it at `/Library/Java/JavaVirtualMachines/<appName>.jdk` and to stay on that feature line. Adding
  a fourth place means all three: the constant list, `scanned_bundle_path`, and the prompt.
