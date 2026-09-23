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

- **The prompt cannot be dismissed, so it is only ever put up for somebody who is there.**
  `dialogs::request_install_password` has no Cancel button and no timeout: Escape does nothing to an
  alert with no button named Cancel, an empty box is re-shown, and it returns only with a password
  (or an error, when there is no window server to draw on). It used to offer both, and both read as
  "skip the macOS update this time" — so a person who walked away for ten minutes, or reached for
  Cancel out of habit, left a Mac that had already downloaded everything sitting unpatched until the
  next cycle asked again. It is a Cocoa `NSAlert` run through `osascript -l JavaScript` rather than a
  `display dialog` like every other prompt in `dialogs.rs`, for two reasons that matter: it sits at
  `NSModalPanelWindowLevel` and joins every space, so it stays in front of whatever the person
  switches to rather than being buried the first time another window takes focus; and it is this
  process's *own* window. The AppleScript way to a front-most dialog — `tell application "System
  Events"` then `activate` — sends Apple events to another process, which TCC gates behind an
  Automation prompt per Mac (`Not authorised to send Apple events to System Events`, -1743, measured
  here). Text reaches the script as `argv`, never interpolated, so nothing in the message is escaped
  for JavaScript, and the password is the script's return value on stdout with only osascript's own
  trailing newline stripped — a password ending in a space has to reach `softwareupdate` intact.
- **Nobody there to ask in the first place.** A cycle reaches the patching step unattended whenever
  the delay budget ran out with nobody at the desk — that is what spending the budget is *for* — and
  a prompt that never times out would hold the cycle, and the menu bar's "Patch Now" with it, until
  whoever comes back. So `execute` carries a `user_present` flag: true when the menu bar's "Patch Now"
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

**The download happens before the prompt, and that ordering is what makes `-R` humane.** Everything
is fetched first with nobody being asked for anything. Only then does the per-user half prompt for
the password, and the `RequestKind::OsUpdate` that follows finds the assets on disk and finishes in
minutes — so the forced restart lands minutes after the person agreed to it.

**The fetch is the daemon's own work, not a step of the patch cycle** — `main::prefetch_os_updates`,
run at the end of each check-in. It was `RequestKind::OsDownload`, submitted by the cycle and waited
on, and that blocked the wrong thing: `MenuState::refresh_actions` disables "Check In Now" and
"Patch Now" for as long as a cycle runs, so fetching this host's 15GB left the menu bar dead for
**nine hours** showing "Downloading the macOS update", with no way to check in and no sign it was
not simply hung. Nothing about the fetch needs a user, so it does not belong behind a dialog.

Three things keep the pre-fetch from becoming its own nuisance, and each is load-bearing:

- **It runs last**, after registration, the inventory, the queue drain and the self-update.
- **It stands aside whenever the queue is non-empty** (`queue::has_pending_request`). launchd will
  not run two copies of the check-in job, so a request arriving mid-fetch waits for it — and nobody
  who just clicked "Patch Now" should be behind an hour of downloading.
- **It re-confirms what it believes it already has, every check-in** (`os_update::needs_prefetch`).
  This used to run only when macOS offered a label the record did not mention, with a seven-day
  backstop, on the reasoning that a pre-fetch costs gigabytes. It does not, when the asset is still
  there: `softwareupdate -d` on a staged label comes back `Downloaded:` in four to nine seconds,
  measured on this host for 26.7 and for Safari. And macOS discards staged assets on a schedule of
  its own — 26.7 was confirmed on disk on 19 September and gone by the 22nd, when the authorized
  install spent **two hours** downloading it again *after* the password was typed, which is the
  hour-late reboot this whole ordering exists to prevent. So the gigabytes are only spent when the
  asset is gone, and then spending them is the job. `StagedDownloads::refreshed` folds each attempt
  into the record: a label that was confirmed before and that `-d` printed nothing for this time (the
  `softwareupdate` blip below) stays believed, so one blip does not stop the cycle prompting for an
  update that is sitting ready; a label macOS no longer offers is dropped.
- **A failed attempt is recorded too, and backed off.** This is the same runaway from the other
  side: writing a record only on success left a failure with nothing behind it, and "nothing behind
  it" reads exactly like "nothing staged" — so the next hourly invocation retried all 15GB, and the
  one after that, for as long as the failure lasted. The record now carries `failed`,
  `failure_count` and `attempted_epoch`, and a failed label is left alone for an hour, then two,
  doubling to a day. The first retry is soon because the likeliest cause is momentary — the 19:46
  failure on this host was a `softwareupdate -l` blip — and a host that gave up for good on one of
  those would simply never update.

**The pre-fetch draws a progress bar in the menu bar, and does not grey it.** The download happens
in the *root* process and the menu bar lives in the per-user one, so the daemon publishes
`os_update::DownloadProgress` to `os-download-progress.json` (0644, root writes and the user reads,
like the staged record) and the scheduler tick turns it into `AgentStatus::PreFetching`. Three
things about that state are deliberate:

- **It does not set `patching`**, so "Check In Now" and "Patch Now" stay live. That is the whole
  difference between it and `Patching`, and the reason the download moved off the cycle at all.
- **It leaves the progress window closed.** The window is for holding somebody's attention through a
  patch run they agreed to; a background download they never asked about has no claim on the screen.
- **The bar spans the whole pre-fetch**, not the current label — `overall_percent`, so halfway
  through the second of two updates reads 75%. A bar that moved three times in an hour would say
  almost nothing.

Getting the percentages out means **streaming both of `softwareupdate`'s pipes** rather than
`Command::output`, which only returns at exit — an hour too late to draw anything. Both pipes,
because it splits its output across them and reading one while the other's buffer fills deadlocks
the child. `pump` parses the last *complete* reading out of everything received so far rather than
out of the latest chunk: a read landing mid-reading would otherwise turn `Downloading: 4` +
`1.00%` into a bar showing 1%. It reports whole percentages only — one download emitted 7,992
readings, and the menu bar wants at most 101 of them. A stale record (nothing written for two
minutes) reads as no download at all, because a daemon killed mid-fetch cannot tidy up after itself
and a bar frozen at 41% forever is worse than none.

**macOS will not tell you what is staged, so the agent remembers** — `os_update::StagedDownloads`,
written to `os-download-state.json` by root at 0644 and read by the per-user process.
`softwareupdate -l` lists an update until it is *installed*, staged or not, and
`/var/db/softwareupdate/journal.plist` records only what already installed. The record is a belief,
not a fact: being wrong costs an install that downloads what it thought was staged, which is where
this started rather than anywhere worse. `run_os_update` **declines to prompt at all** until the
record covers every macOS label offered — prompting first and downloading afterwards is the thing
this ordering exists to prevent. The labels it compares against ride on `OsUpdateStatus`, from the
same `softwareupdate -l` the cycle already ran: a second scan would be one more thing that can
momentarily come back empty, and an empty listing would have the cycle decline to prompt for an
update sitting ready on disk. That blip is not hypothetical — it is what failed the 19:46 download
in this host's log.

`RequestKind::OsDownload` still exists and the daemon still answers it, though nothing submits one.
It is the self-update window: the restart replaces both jobs, and a per-user process from before
0.14.3 can already have a request queued. A daemon that did not recognise it would leave that
process blocked for the full six-hour bound with a dialog on screen. Delete both once no fleet runs
an agent older than 0.14.3.

**An agent self-update is held back while a patch cycle is mid-flight.** Applying one restarts both
launchd jobs, and the per-user one is the half running the cycle. On `htw-m5pro-hobleyd` that
restart killed the password prompt one second after it appeared, having cost nine hours of
downloading to earn it:

```text
19:29:26  OsDownload request finished: success=true
19:29:26  self-update available: 0.14.1 -> 0.14.2
19:29:27  asking david.hobley to authorize the macOS install
19:29:28  restarting gui/501/au.com.sharpblue.kintsugiagent-ui to pick up the new binary
```

`queue::process_queue` returns the kinds it served, and serving anything but a `CheckIn` defers that
invocation's self-update — a `CheckIn` alone is not a cycle, and deferring for one would mean never
applying an agent update at all.

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

**Only a macOS label's failure stops the cycle.** A Safari label that will not download costs the
install a few minutes fetching 250MB; a macOS label that will not download costs it the 11.7GB this
whole ordering exists to move out from behind the password prompt. `is_macos_label` is the `macOS`
prefix of the label, which the listing's `Title:` line agrees with.

**`softwareupdate` separates its progress readings with a carriage return**, because it is
overwriting one line on a terminal rather than writing many. `condense_progress` looked for the next
`Downloading: ` immediately after a reading, met the `\r`, ended the run at one, and so emitted every
reading individually — it never condensed anything in production while its tests passed, because
every transcript in them was hand-written without a `\r`. macOS 27's download put **160KB and 7,992
readings** into `daemon.log` through the one function whose purpose is to stop that. Its tests now
carry the bytes `od -c` prints from that log line.

**The label is positional and the exit status is worthless**, both measured on that host after
being guessed wrong:

```text
softwareupdate -d --label "macOS Tahoe 26.7-25G229"  ->  unrecognized option `--label'   exit 0
softwareupdate -d "macOS Tahoe 26.7-25G229"          ->  Downloaded: ... Failed to auth   exit 1
softwareupdate -d "definitely-not-an-update-9.9"     ->  No such update                   exit 0
```

There is no `--label` flag (`softwareupdate`'s own usage: `<label> ...  specific updates`), and the
run that did nothing exits **zero** while the run that fetched everything exits **one**.

Confirmed under the daemon's own conditions — root, **no controlling tty** — rather than only from a
shell, because the two differ: given a terminal `softwareupdate` blocks on a real `Password:` prompt
*before* printing `Downloaded:`, and given none it fails straight past to the classifiable output
above. Reproduce it with the redirect **inside** the sudo'd shell, since sudo allocates a pty by
default and one placed outside would hand `softwareupdate` a terminal again:

```bash
sudo sh -c 'softwareupdate -d "macOS Tahoe 26.7-25G229" </dev/null >/tmp/su-probe.log 2>&1'
```
 So
`classify_download` ignores the status and reads the text, the same rule `check` follows for `-l`:
a line beginning `Downloaded` is the whole of the positive evidence, because it is the one line that
appears only when an asset reached the disk. **A line, not the string `Downloaded:`** — macOS prints
`Downloaded: macOS Tahoe 26.7` for a system update and `Downloaded Safari` for everything else, and
looking for the colon logged a completed 255MB Safari download as "fetched nothing" on the first
fleet run of this code. Had `--label` shipped, every label would have returned a usage
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
it. The server re-derives the host's pending state from `softwareupdate -l` at the next check-in,
so that much is self-correcting. `process_queue` never removes the request — `is_stale`'s boot check
discards it unrun at the next boot, which is the case that check was written for. The credentials
are already gone, because `take_auth` unlinks the sidecar *before* the install starts rather than
after it returns. `InstallOutcome::restart_required` now covers only the case where the install came
back without rebooting (Intel, a Safari-only update, or a restart that turned out not to be needed);
the daemon still reports patched only when nothing is pending.

**The success report survives the reboot by being written down first.** Re-deriving the pending
flag is not the same as hearing that an install *succeeded*, and only the latter closes a Failed
Updates row: `ReportOperatingSystemPatchedCommandHandler` resolves the host's `macOS` rows on a
success report, and nothing else ever does. With `report_patched` never sent for an install that
rebooted, a download failure filed from this host on 17 September was still on the screen after the
successful install on the 22nd. So `main::install_os_updates` writes an `os_update::PendingInstall`
(`os-install-pending.json`: the version before, and when) *before* calling `softwareupdate`, and
`main::settle_pending_install` reads it on the next invocation — the `RunAtLoad` one after the
reboot. A host on a different version finished its install and reports it; a host that has booted
(`queue::boot_epoch`) and is on the same version did not, and the note is dropped silently; the same
version on the same boot is still pending and left alone. It runs **before** the registration POST,
because the server's `RecordOperatingSystemPatched` clears the pending flag and the registration
then sets it from this boot's own `-l` — which, on a host that was offered 26.7 and 27 together,
correctly says 27 is still pending. Reported after, it would wipe that answer for an hour.

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

**The third repair is the quarantine flag, and it is the one with a deadline.** A release
downloaded through a browser carries `com.apple.quarantine` on every file in the archive, and BSD
`install` copies the attribute with the bytes. install.sh stripped it from the two binaries —
Gatekeeper refuses to run a quarantined executable — and from nothing else, on the reasoning that
nothing executes a plist. macOS 26 agreed and merely linted it; **macOS 27's launchd refuses to load
a quarantined plist** (`Could not import service ... error = 155: Refusing to execute/trust
quarantined program/file`, visible only in the unified log), so the first Mac to take that upgrade
came back with no check-in daemon and no menu bar agent, and nothing to report either absence. The
LaunchDaemon plist is the exposed file: install.sh keeps it across reinstalls to preserve the
check-in minute, and `checkin_schedule`'s rewrite is an in-place `fs::write`, which keeps existing
attributes — so a flag from the first install survived every later one. install.sh now clears all
four files, and `self_update::repair_quarantine_flags` clears them on every check-in; but the
repair runs inside the daemon that the flag stops from loading, so it only reaches a host that
takes this agent release *before* the OS upgrade. After it, the fix is by hand, as root:
`xattr -d com.apple.quarantine` on the plists, then `launchctl bootstrap` for `system/` and for
`gui/<uid>/`. `sfltool dumpbtm` is a false lead here — it shows the developer group as disabled,
which is the Login Items toggle and not what launchd is refusing on.


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

