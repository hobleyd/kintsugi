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


## macOS updates: root is not enough, and the download comes first

**On Apple silicon `softwareupdate -i` will not install a macOS update for root.** It wants a
*volume owner* — an APFS cryptographic user — to authorize it, via `--user <name> --stdinpass`, both
of which `man softwareupdate` marks "Apple silicon only". The root LaunchDaemon is not one, so
`os_update::install` used to download the whole update and then die on an interactive `Password:`
prompt, on a process with no terminal. It is the same masked-password wall root-requiring Homebrew
casks hit, and it is invisible in every way that matters: the exit status is a plain 1, and the
"Failed to authenticate" line is the last of a hundred thousand `Downloading: 95.60%` fragments.

**Everything about this failure is expensive because the authorization comes last.** A real run
spent **80 minutes** fetching 2.9GB before finding out it could not install it, and `-i -a` means
everything applicable — the same host was also offered macOS 27 at 11.7GB. So both ways of getting
the credentials wrong are checked in `patch_cycle::authorize_os_update`, in the per-user half,
*before* the request is submitted:

- **Nobody to ask, or nobody answering.** No password means the request is not submitted at all.
  Note the polarity is the opposite of `confirm_patch`, where an unanswered dialog counts as a delay
  and patching proceeds — that is why `dialogs::PasswordAnswer` is its own enum rather than a reuse
  of `ConfirmChoice`, the same reasoning `RemoteControlChoice` exists for.
- **Nobody there to ask in the first place.** A cycle reaches the patching step unattended whenever
  the delay budget ran out with nobody at the desk — that is what spending the budget is *for* — and
  a password dialog there would stall the cycle for the whole prompt timeout, every cycle, to arrive
  at the same skip. So `execute` carries a `user_present` flag: true when the menu bar's "Patch Now"
  was clicked, and otherwise whatever `acknowledged_by_a_person` made of how long the "no delays
  left" dialog stood there. `acknowledge` returns `Ok(())` for a click and a timeout alike, so its
  duration is the only signal there is — which is why that predicate is split out and tested rather
  than left inline.
- **An account that is not a volume owner.** Being in `admin` is *not* the same thing: an account
  created by MDM, or migrated onto Apple silicon, can be an administrator with no secure token.
  `os_update::is_volume_owner` intersects the user's `GeneratedUID` (from `dscl`) with the UUIDs
  `diskutil apfs listUsers /` marks `Volume Owner: Yes`. A check that could not run is treated as a
  refusal, not as permission.

**The password is the one thing in the queue protocol that is not just a name**, and it travels in a
`<request>.auth` sidecar rather than the request body so that the sentence "a request never carries
anything executable" stays literally true of requests. `queue.rs`'s module docs hold the full
argument; the load-bearing parts are mode `0600` set atomically at creation (the queue directory is
`root:admin 0770`, so any other administrator could otherwise read it), `take_auth` unlinking it as
it reads, and `remove_request` — not a bare unlink — on every path that discards a request.

**`-R`, because a prepared update is not a patched one.** These updates carry `Action: restart`, and
`softwareupdate -i` without `-R` only reaches `SUMAC_PHASE_PREPARED` — staged, still listed by
`softwareupdate -l`, host still on the old version until somebody reboots. That was measured on a
real run, not inferred. An update that waits indefinitely for a person is not unattended patching,
so the daemon passes `-R`.

**What `-R` costs, stated plainly.** `man softwareupdate`: "If the user invoking this tool is logged
in then macOS will attempt to quit all applications, logout, and restart. If the user is not logged
in, macOS will trigger a forced reboot if necessary." The invoking user here is **root in a
LaunchDaemon**, which is not logged in — so the forced path is the likely one and unsaved work goes
with it. `--force` is deliberately not passed on top: it would remove even the chance that macOS
treats the `--user` account as logged in and closes applications gracefully. Both dialogs say the
Mac will restart itself, and `install_password_message` says it immediately above the password box,
because that is the last moment anyone can decline.

**The download is its own queued step, and that ordering is what makes `-R` humane.**
`RequestKind::OsDownload` fetches everything first with nobody being asked for anything. Only then
does the per-user half prompt for the password, and the `RequestKind::OsUpdate` that follows finds
the assets on disk and finishes in minutes — so the forced restart lands minutes after the person
agreed to it.

**`softwareupdate -d` is not the authorization-free step this section used to claim.** On Apple
silicon it downloads *and prepares*, and preparing wants the same volume owner installing does, so
the download step ends:

```text
Downloading macOS Tahoe 26.7
Downloaded: macOS Tahoe 26.7
Failed to authenticate
Password:
```

— exit 1, with everything it was asked to fetch already on disk. Measured on
`htw-m5pro-hobleyd`, twice: **eight seconds end to end**, the whole of it preparation, because the
asset had been staged by an earlier run. Two things follow, and `os_update::download` is both:

- **That exit 1 is reported as success**, naming in the log what was staged unprepared. It is the
  preparation that is outstanding, and `os_update::install` does it with the password in hand. As a
  failure it filed a Failed Updates row every cycle for work that had succeeded — and worse, ended
  the cycle before the install it exists to precede, so the Mac never updated at all.
- **The fetch runs one `-d <label>` per label, not one `-d -a`.** `-a` stops at the first update it
  cannot prepare, so everything behind it in the listing went unfetched — on that host, Safari and
  the 11.7GB macOS 27 — and `-i -a` would have downloaded them *after* the password was typed,
  which is the hour-late reboot this split exists to prevent. Labels come from `softwareupdate -l`
  and run to the end of their line, spaces and build suffix included (`macOS Tahoe 26.7-25G229`).

**The label is positional and the exit status is worthless**, both measured on that host after
being guessed wrong:

```text
softwareupdate -d --label "macOS Tahoe 26.7-25G229"  ->  unrecognized option `--label'   exit 0
softwareupdate -d "macOS Tahoe 26.7-25G229"          ->  Downloaded: ... Failed to auth   exit 1
softwareupdate -d "definitely-not-an-update-9.9"     ->  No such update                   exit 0
```

There is no `--label` flag (`softwareupdate`'s own usage: `<label> ...  specific updates`), and the
run that did nothing exits **zero** while the run that fetched everything exits **one**. So
`classify_download` ignores the status and reads the text, the same rule `check` follows for `-l`:
`Downloaded:` is the whole of the positive evidence, because it is the one line that appears only
when an asset reached the disk. Had `--label` shipped, every label would have returned a usage
error that exits zero and the download step would have reported a fully fetched host having moved
no bytes at all.

Combined into one request, which is how this started, it was the other way round: authorize, wait
out a download that took 80 minutes on a real run, then get rebooted long after the dialog was
forgotten and unsaved work had accumulated since. `patch_cycle::run_os_update` submits the two in
order and is the only place that ordering is expressed; `dialogs::install_password_message` says
"already been downloaded … a few minutes later" on the strength of it, so the two have to move
together. A prompt that goes unanswered after the download costs nothing extra — the assets stay
staged, and the next cycle's download step returns in seconds.

The split also keeps the password's life on disk to the length of an install rather than an install
plus a download, and `OsDownload` carries no sidecar at all.

**Nothing after the `softwareupdate` call is guaranteed to run**, because the reboot happens inside
it. `report_patched` is never sent — the server re-derives the host's pending state from
`softwareupdate -l` at the next check-in, so it is self-correcting. `process_queue` never removes the
request — `is_stale`'s boot check discards it unrun at the next boot, which is the case that check
was written for. The credentials are already gone, because `take_auth` unlinks the sidecar *before*
the install starts rather than after it returns. `InstallOutcome::restart_required` now covers only
the case where the install came back without rebooting (Intel, a Safari-only update, or a restart
that turned out not to be needed); the daemon still reports patched only when nothing is pending.

**Read `softwareupdate -l` by label, not by position.** `OsUpdateStatus::latest_version` took the
first `Version:` in the output, which on a host offered Safari, macOS 26.7 and macOS 27 is *Safari's*
— the admin UI showed 27.0 as the pending macOS version of a Mac downloading 26.7. Only a line whose
`Title:` names macOS counts, and of those the highest, because `-a` installs all of them.


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

