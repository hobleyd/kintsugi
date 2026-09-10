---
paths:
  - "clients/*/packaging/**"
  - "clients/*/src/self_update.rs"
  - "clients/*/Cargo.toml"
  - ".github/workflows/**"
  - ".github/scripts/**"
---

# Couplings: releasing an agent, and the archive's layout

The published archive's entry names, the release tags, the signing identity and the pinned hashes
are agreed between the packaging scripts, CI and code that never opens either.

**The lock file is half a version bump.** Every cargo invocation in CI passes `--locked`, so a bump
that touches only `Cargo.toml` dies before compiling anything — and because it stops at the test
step, the release job never fires and the tag is silently never cut. Verify with the same invocation
CI uses: `cd clients/<platform>-agent && cargo test --locked`.

- The installer tarball's top-level entry names are load-bearing: `self_update.rs` extracts
  `kintsugi-agent` / `kintsugi-agent.exe` by name out of the same archive a human downloads for a
  fresh install. Both agents publish `.tar.gz` — Windows included — because
  `AgentPackageArchiveRewriter` reads gzip-tar specifically, and `tar.exe` has shipped in Windows
  since 10 1803.
- The macOS archive carries **three** plists, not two, and `packaging/install.sh` is now the only
  thing that reads the third out of it — `self_update` installs the job from
  `remote_shell::LAUNCHD_JOB_PLIST` instead (see "Remote control"). Dropping it from the archive
  would leave a fresh install with no remote-shell job until its first check-in repaired it.
- `/usr/local/bin/kintsugi-mas` is named in four places that nothing checks agree: the macOS agent's
  `config::MAS_BINARY_PATH` (what `self_update` replaces and `self_removal` deletes), its
  `MAS_BINARY_NAME` (the tarball entry `self_update` extracts and `publish-release.sh` writes),
  `install.sh`/`uninstall.sh`'s `MAS_DEST`, and the `MAS=` line of the server's
  `AppStoreUpgradeScript`. Move one and App Store rows fail with "kintsugi-mas is not installed" on a
  host that plainly has it — a signed script's text is not rewritten by a deployment, so the server
  side of that rename only reaches a host after a human takes and re-signs the new script.
- **The Windows bootstrap script's pinned hash is coupled to the GitHub asset's bytes and to
  nothing that checks it.** `WindowsBootstrapScript` renders `AgentPackage.UpstreamSha256`, recorded
  when the import fetched that release; if a release is ever re-cut under the same tag, every host
  the script is pushed to refuses to install and the only correct fix is a re-import and a
  re-render. The script is also kept **ASCII-only**, like every other server-written script here,
  because Windows PowerShell 5.1 decodes a BOM-less `.ps1` with the system ANSI code page and this
  is a file an operator saves themselves — a test asserts it. And it names `install.ps1`,
  `config.toml` and `kintsugi-agent.exe` as top-level archive entries, so it is coupled to
  `publish-release.ps1`'s `tar` invocation exactly as `self_update.rs` is.
- The enrollment token is not baked into published packages — `AgentPackageArchiveRewriter` writes
  the current `AGENT_ENROLLMENT_TOKEN` into `config.toml` on every download, so rotation never
  staleness-breaks a published package. `AgentPackagesController.Download` skips that rewrite for a
  cert-bearing agent, because rewriting would change the bytes and break the publish-time checksum.
- CI's release tags (`<platform>-agent-v<version>`) are parsed by `GitHubAgentPackageSourceClient`
  to work out which platform and version a release is. Renaming a tag on either side silently stops
  that platform ever being found again — a refresh just reports nothing new.
- The agent-package platform namespace (`"macos"`, `"windows"`, `"linux"`) is *not*
  `PlatformBucket`'s namespace (`"macOS"`, `"Windows"`, `"Linux"`, `"pm:..."`). They name different
  things; don't unify them.
- The Wayland backend is **optional in the archive**. `publish-release.sh` packages it only if it was
  built (or passed with `--wayland-binary`) and warns loudly when it was not; `install.sh` installs
  it if present; `self_update` installs it beside the agent if the new archive carries one, so a host
  first installed from an X11-only package gains Wayland support on its next update without a
  reinstall. The name lives in `config::WAYLAND_BACKEND_BINARY` because three places have to agree on
  it.
- **The code-signing identity's name is one string in five places that nothing checks agree**:
  `packaging/create-signing-identity.sh` (which mints it, as both the PKCS#12's friendly name and
  its CN), `.github/workflows/ci.yml` (which looks the certificate up by it after importing, and
  passes it as `--signing-identity`), `packaging/publish-release.sh` and `packaging/install.sh`
  (which resolve an identity by it), and this list. Rename it in one and the release job fails at
  its own assertions — which is the good outcome, and deliberate: the alternative is a package that
  installs perfectly and orphans every host's Screen Recording and Accessibility grant on the next
  self-update. The two secret names (`MACOS_SIGNING_CERTIFICATE_P12`, `_PASSWORD`) are the same kind
  of pair, shared between that script and that workflow.
- The PPPC profile's `CodeRequirement` is tied to the agent's code signature, which is now stable
  across releases — so the profile is fillable, but with a self-signed leaf rather than the
  Developer ID Apple's guidance assumes, and nobody has confirmed MDM honours that. See
  `packaging/kintsugi-remote-control.mobileconfig.example`, which says what to verify before
  deploying it and what breaks if somebody fills it in from an ad-hoc build anyway.
- **The macOS remote-shell handoff is four names that have to agree, and they are checked now.** The
  request suffix (`remote_shell::REQUEST_EXTENSION`), the directory
  (`config::REMOTE_SHELL_QUEUE_DIR`), the job label (`config::REMOTE_SHELL_LAUNCHD_LABEL`) and the
  `WatchPaths` entry plus `ProgramArguments` in
  `packaging/au.com.sharpblue.kintsugiagent-remote-shell.plist` all have to line up, and so does the
  `--remote-shell` arm in `main`. Change one and the per-user process writes a request nothing ever
  reads: the session is reported as never connecting, and neither log says why. Two tests in
  `remote_shell` pin all of it — the suffix, and then the label, the watched directory, the binary
  path and the argument — and they can only do so because the plist is compiled into the binary
  rather than read from the archive. Keep it that way when editing the plist.
