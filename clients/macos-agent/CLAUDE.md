# The macOS agent

Loaded when Claude reads files under `clients/macos-agent/`. What the three agents share is in
`clients/CLAUDE.md`; this is what macOS forced to differ.

## Identity and file modes

**Only the macOS per-user process holds the agent identity, and that constrains its file mode.**
It reads the same `identity/` directory the root daemon writes, so the directory is `root:admin
0770` and the key `0640` — `admin` because that is the logged-in administrator's group, and the
per-user process is not root (Homebrew refusing to run as root is the whole reason macOS differs;
the Windows and Linux per-user halves hold no identity and go through their queue instead).
`install.sh` sets that ownership, and `identity.rs`'s `enroll` now sets it again on every
enrollment, because macOS gives a new file its *directory's* group rather than the creating
process's. Without that second call, deleting `identity/` to recover from a regenerated CA — the
documented remedy — recreates it under root's own `wheel`, and the per-user process can never read
its own key again. It fails half-visibly: the root daemon is fine, the host keeps registering, and
only the per-user half stops, presenting no certificate at all and drawing a 403.


## Remote control and the remote shell

**macOS needs a handoff for that, and it is a third root job.** Remote control lives in its per-user
process, because the screen belongs to a GUI session the root daemon has not got — so the per-user
process answers the request on the control socket it already holds, flushes that answer, and drops a
request naming the session id into `remote-shell/`. launchd's `WatchPaths` starts
`kintsugi-agent --remote-shell`, which opens the **media socket itself** and runs a root PTY on it.
That needs no server change at all, and the reason is worth holding onto: the two sockets of a
session are independent, and the server pairs a media socket by `(serialNumber, sessionId)` and
authenticates it by the certificate nginx verified — which the root daemon presents because it reads
the same `identity/` directory. Nothing server-side can tell, or needs to.

Three things about that follow. It is **its own launchd job**, not a fourth `queue::RequestKind`:
the main queue is drained by the check-in daemon, launchd never runs two instances of one job, and a
session held open for a support call would otherwise stall this host's check-ins, patches and
self-update for its whole length. A **forged request buys nothing** — the server refuses a media
socket for a session it did not create for this serial and has not seen answered, so the only id
that works is one an administrator has already opened a shell for; this is the main queue's "the
worst it can do is start an already-approved upgrade early" in its narrowest form, and it is why the
request carries a session id and nothing else. And **the root check-in installs that plist, not
`self_update`** — `remote_shell::install_job_if_absent`, called from `run_daemon` before anything
that can fail over the network, creating the drop-box `root:admin 0770` and `launchctl
bootstrap`ping the job when either is missing. Putting it in `self_update` is the obvious place and
it does not work, for a reason worth stating once: **a self-update is performed by the binary that
is already installed**, so an update path can only install a job the *previous* release knew about.
0.9.5 shipped exactly that mistake — its own `install_binary` installed the plist, 0.9.4's did not,
and every Mac that reached 0.9.5 by self-updating got a binary understanding `--remote-shell` with
no job to run it under. What that looks like is a terminal that never opens: the per-user process
cannot create the drop-box (its parent is root's), the server reports "the other end never
connected", and only the per-user log names a path. So this is the macOS spelling of Linux's
`config::repair_directory_modes`, and it is required for the same documented reason — `self_update`
never re-runs the installer, so a host in the field has no other repair path. Two consequences. The
job description is compiled into the binary (`remote_shell::LAUNCHD_JOB_PLIST`, an `include_str!` of
the packaged file), because a check-in has no archive to read from; and its *contents* are still
written **only when absent**, so an administrator's edits survive and a change to the packaged plist
reaches no host that already has one.

**Installing when absent was not enough, and what it missed is an ownership defect the whole
self-update shares.** `tar -xzf` run as root restores the uid and gid recorded *in the archive* —
whichever account built the release — and `fs::copy` on APFS is `fclonefileat`, which clones the
owner and the timestamps with the bytes. So every macOS self-update quietly replaced three
root-owned files with user-owned ones, and each failed in its own unrelated-looking way: **launchd
refuses a LaunchDaemon it does not find root-owned** (`Bootstrap failed: 5: Input/output error`,
which says nothing about ownership), so terminal sessions were requested and never served;
`AppStoreUpgradeScript` refuses a `kintsugi-mas` not owned by root, so App Store rows stopped
patching; and `/usr/local/bin/kintsugi-agent` — the binary launchd executes as root — became
writable by a local account, which is a root escalation for whoever owns it. packaging/install.sh
had this right with `install -o root -g wheel` all along; every self-update since undid it.
`extract_and_install` now passes `--no-same-owner` and `install_over` asserts `root:wheel`, but a
flag only helps future updates — so `self_update::repair_installed_ownership` and
`remote_shell::ensure_job_installed` re-assert both on every check-in, and the latter also
bootstraps the job whenever launchd has not got it. That is what makes a host that has *already*
self-updated into the broken state heal itself rather than needing a reinstall.


**Nothing is shown on the Mac while a shell session runs**, deliberately. The menu bar reports a
*screen* session, because somebody's screen is being watched; a root shell is not a session inside
anyone's desktop, the other two agents announce nothing either, and a notice the per-user process
raised it could not reliably clear — the session runs in a different process, on a socket that one
cannot see.


**Both TCC permissions fail silently, which is why they are checked before consent is asked.**
`CGEventPost` without Accessibility is dropped with no error and no return code — the session shows
the screen perfectly and ignores the mouse. ScreenCaptureKit without Screen Recording produces
either nothing or a desktop with every window missing. So `describe_restrictions` checks both up
front and the consent dialog lists whatever will not work, rather than leaving it to be discovered
mid-call.


## Code signing, TCC, and why a grant outlives a release

**The signature in `publish-release.sh` is load-bearing, and `cargo build` alone is not enough.**
The linker signs an arm64 slice as "linker-signed" — no designated requirement — and signs an
x86_64 slice not at all, and TCC accepts neither as an identity. What that looks like is not an
error: System Settings shows the binary added and switched on under both Screen Recording and
Accessibility, and the process still fails both checks, however many times the rows are removed and
re-added, restarted or not. 0.6.0 shipped that way; the tell was the system `TCC.db` holding a
`csreq` naming two cdhashes that matched neither slice of the installed file.


**One certificate signs every build, and that is what makes a grant outlive a release.** TCC
records a binary's designated requirement when somebody grants Screen Recording or Accessibility
and re-checks the running process against it on every access. An *ad-hoc* signature's requirement is
`cdhash H"..."` per slice — a hash of that exact build — so it satisfied the grant it was given and
nothing afterwards: `self_update` replaces the binary unattended, so **every release used to orphan
both permissions across the whole fleet**, with System Settings still showing the agent switched on.
So `packaging/create-signing-identity.sh` mints one long-lived self-signed code-signing certificate
(`Kintsugi Agent Signing`) and `release-macos` signs every published build with it — so the
requirement is `identifier "kintsugi-agent" and certificate leaf = H"..."`, which every future
build satisfies. Four things follow.

- **The key lives in the repository's Actions secrets and nowhere else.** The release job is the
  only thing that builds the binary a host installs and self-updates to, so it is the only thing
  that needs to sign; a copy on somebody's laptop would be a second copy of a fleet credential for
  no gain, and a *second identity* there would be worse than none — a host hand-installed from a
  locally signed build and then self-updating from a differently-signed release loses its grants
  anyway, for a reason neither log explains. So the setup script pushes the PKCS#12 straight into
  `MACOS_SIGNING_CERTIFICATE_P12` / `_PASSWORD` and keeps nothing, `release-macos` **fails** when
  those are missing rather than quietly signing ad hoc, and `publish-release.sh` run by hand signs
  ad hoc and says so — what that produces is a package for one server, not the fleet's release, and
  a human is reading the warning. There is no backup of the key: losing it costs the fleet another
  re-grant, which is cheaper than a copy of it existing somewhere.
- **Signing asserts what it produced.** `publish-release.sh` refuses to publish a package whose
  requirement carries a `cdhash` clause, in the same spirit as the `lipo -archs` and
  not-dynamically-linked assertions elsewhere — a signature that quietly came out cdhash-only is a
  fleet-wide grant wipe that no log names. It asserts the *absence* of `cdhash` rather than any
  particular wording, because a self-signed leaf reads `certificate leaf = H"..."` and an
  Apple-anchored one says more, and betting on a spelling would fail a release over a good
  signature. The release job checks its own end too: an imported certificate with no trust behind
  it is *quiet* — `security import` reports "1 identity imported" and `find-identity -v` then finds
  0 valid ones — so it asserts the listing and then signs a throwaway file, because an identity
  `find-identity` lists can still be one `codesign` refuses. `install.sh` says the same thing about
  a packaged binary it is handed, as a diagnostic: there is nothing an installer can do about it.
- **Nothing on a managed Mac needs the certificate.** Validating a signature is not trusting its
  signer, and the requirement only compares the leaf's hash; only the thing that *signs* needs the
  private key and the `add-trusted-cert` line (without which `codesign` refuses the identity with
  `CSSMERR_TP_NOT_TRUSTED`) — which is the release job, for the length of one run, in a keychain it
  creates and throws away. A locally built agent is therefore ad-hoc signed and its grants last
  until the next `cargo build`; `install.sh` says so rather than leaving it to be discovered.
- **Moving to it costs one final re-grant per Mac**, since the requirement changed — both rows
  removed and re-added, and the per-user process relaunched
  (`launchctl kickstart -k gui/$(id -u)/au.com.sharpblue.kintsugiagent-ui`), because a process that
  was denied stays denied until it reconnects to WindowServer. After that a release should cost
  nothing. TCC keys its row on the path as well as the requirement, so it is the installed
  `/usr/local/bin/kintsugi-agent` that keeps its grants; a binary run out of `target/release` is a
  different row and asks again however it was signed.


**MDM pre-approval is now fillable and still unverified.**
`packaging/kintsugi-remote-control.mobileconfig.example` needs a `CodeRequirement` matched against
the binary's signature, and the stable requirement above is exactly what it was waiting on. What is
not established is that a PPPC profile honours a requirement naming a *self-signed* leaf — Apple's
guidance assumes a Developer ID, and nobody has tried this through MDM here, so verify it on one
enrolled Mac rather than deploying it fleet-wide (the file says so at length, including how far back
`kTCCServiceScreenCapture` is grantable at all). Until then both permissions need a human at each
Mac — once, rather than once per release — and Accessibility specifically **cannot be granted from
its prompt at all**: macOS only offers to open System Settings, where someone then has to find the
binary in a list (`/usr` is hidden in the file picker; ⌘⇧G and type `/usr/local/bin`).

