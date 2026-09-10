# The three agents (`clients/`)

Loaded when Claude reads files under `clients/`. Each agent then has its own `CLAUDE.md` for what
its platform forced to differ; this file is what they share.

Read the macOS one first — it's the original — then the others for what each platform forced to
differ.

Two rules from the root `CLAUDE.md` bite hardest here. A new agent-facing route is un-gated until
`nginx/default.conf` is edited too, and nothing in the C# will tell you. And a version bump is half
a `Cargo.toml` edit and half a regenerated `Cargo.lock`: CI passes `--locked` everywhere, so a bump
missing the lock file dies before compiling, the release job never fires, and the tag is silently
never cut. Verify with `cargo test --locked`, which is the invocation CI uses.

## Building and testing

```bash
# macOS agent (inline #[cfg(test)] modules — checkin_schedule, identity, self_update, ...)
# Once, ever: mints the fleet code-signing identity straight into this repository's Actions secrets,
# which is what keeps a host's Screen Recording and Accessibility grants across releases. Nothing
# local holds the key. See "One certificate signs every build" in clients/macos-agent/CLAUDE.md.
clients/macos-agent/packaging/create-signing-identity.sh --set-secrets
cd clients/macos-agent && cargo build --release
cd clients/macos-agent && cargo test
cd clients/macos-agent && cargo test load_or_assign_persists_a_fresh_minute_when_nothing_is_saved_yet


# Windows agent — same shape, but it only builds on Windows (winreg, windows-sys, windows-service)
cd clients/windows-agent && cargo build --release
cd clients/windows-agent && cargo test


# Linux agent — same shape again. Builds and tests natively on Linux; from macOS, run it in a
# container (see clients/linux-agent/CLAUDE.md), a real Linux build rather than a cross-compile.
cd clients/linux-agent && cargo build --release
cd clients/linux-agent && cargo test


# The Linux agent's Wayland backend, which is its own crate because it links libpipewire (see
# "Couplings"). Needs libpipewire-0.3-dev >= 0.3.65 — debian:12 or newer, not ubuntu:22.04.
cd clients/linux-agent-wayland && cargo test
```

**The lock file is half the bump, and forgetting it fails after the merge rather than before it.**
A crate's own version is recorded in its `Cargo.lock` as well as its `Cargo.toml`, and every cargo
invocation in CI passes `--locked` (`ci.yml:122`, `190-191`, `228`, `275`) — which exists precisely
to refuse a lock file that doesn't match. So a bump that touches only `Cargo.toml` dies at
`error: cannot update the lock file ... because --locked was passed`, *before compiling anything*.
The failure reads as "the agent tests failed" when the tests never ran, and because it stops at the
test step the release job never fires, so the tag is silently never cut. A bare `cargo test` will
not reproduce it — the flag is the whole difference — so verify a bump with the same invocation CI
uses:

```bash
cd clients/<platform>-agent && cargo test --locked
```

Running any ordinary `cargo build`/`test`/`check` after editing the version updates the lock file in
passing; the trap is doing that in a scratch copy of the tree and committing only from the original.

Each publish script still works by hand and is still the only place the archive's layout is
defined; CI calls the same script with `--binary`/`--output-dir` (`-Binary`/`-OutputDir` on
Windows) so the `tar` invocation is never duplicated in YAML. Run one directly and it builds and
POSTs to `/api/agent-packages` as before. The Linux one must then be run on the *oldest* glibc in
the fleet (or a container of it): a binary linked against a newer glibc will not start on an older
host, and nothing in the publish path checks that. **CI sidesteps that entirely** by targeting
`x86_64-unknown-linux-musl` — statically linked, so there is no libc floor at all, which is only
possible because the agent links no C library of its own (rustls links nothing; `ksni` speaks
StatusNotifierItem over zbus rather than linking GTK). It asserts the result is not dynamically
linked rather than trusting the target name. macOS gets a `lipo`'d universal binary for the same
reason there is one package per platform: an arm64-only build would silently exclude every Intel
Mac in the fleet.

## The three agents

They are deliberately the same program in different clothes: same modules, same names, same
ordering, same comments where the reasoning carries over. Read the macOS one first — it's the
original — then the others for what each platform forced to differ. The differences that matter:

| | macOS | Windows | Linux |
|---|---|---|---|
| Privileged half | root LaunchDaemon, re-invoked by launchd | resident service (`windows-service`) | systemd oneshot on a `.timer` |
| Per-user half | LaunchAgent | logon-triggered task for `BUILTIN\Users` | systemd user unit, `graphical-session.target` |
| Check-in schedule | rewrites its own plist, reloads launchd via a detached helper | computes its next wake in-process | rewrites its own `.timer`, `daemon-reload` |
| Privilege handoff | queue: OS updates, AI-researched scripts and App Store rows; Homebrew stays per-user | queue, everything | queue, everything |
| Inventory | `/Applications` bundles + Homebrew + App Store (by receipt) | uninstall registry (3 views) + winget + Chocolatey | Flatpak + Snap (not dpkg/rpm — see "Platform buckets" in src/CLAUDE.md) |
| OS updates | `softwareupdate` | Windows Update Agent COM API, via PowerShell | apt / dnf / yum / zypper / pacman / apk |
| Host identity | hardware serial, always present | SMBIOS serial, **often a placeholder** | DMI serial, **often a placeholder** |
| Nobody logged in | nothing patches | nothing patches | root service patches unattended — see below |
| Remote control | per-user process, consent + capture + input | service holds the socket, a **SYSTEM session helper** does the rest, named pipe between | resident root unit holds the socket, per-user process does the rest, unix socket between; X11 via XTEST, Wayland via a separate portal/PipeWire binary |
| Choosing a display | restarts the ScreenCaptureKit stream on another `SCDisplay` | a source rectangle of the whole virtual desktop, plus "All displays" | X11: a crop of the root window, plus "All displays". Wayland: whichever outputs the portal's picker granted, re-linked to a different PipeWire node |
| Remote shell | a third root LaunchDaemon, as **root**, asked by the per-user process through its own queue | the service itself, as **SYSTEM**, over ConPTY — no helper | the resident root unit, as **root** — nothing crosses `remote_ipc` |
| Reachable with nobody logged in | no — the per-user process holds the identity | yes, for a shell; a screen request answers `Unavailable` | yes, for a shell; a screen request answers `Unavailable` |


**Linux borrows its architecture from Windows, not macOS, and for the same forcing reason.** Every
upgrade it can perform (`apt-get`, `dnf`, `flatpak update --system`, `snap refresh`) requires root,
so patching lives in the root service and the per-user process holds no identity and makes **no
network call at all** — it decides *when*, and asks. macOS is the odd one out precisely because
Homebrew *refuses* to run as root and installs into a user-writable prefix. The queue directory is
`root:root 1733` (a drop-box: anyone may write, only root may read or list), which is the Linux
spelling of the macOS queue's `root:admin 0770` and needs no group — "local administrators" is
`sudo` on Debian, `wheel` on Red Hat, and neither elsewhere.


**macOS hands off by row, and `upgrade::runs_as_root` is the one place that decision lives.** The
per-user process runs a Homebrew row itself — `brew` refuses to run as root and its installs are
user-owned — and sends an AI-researched row (`UpgradeStatusDto.PackageManager` null) to the root
daemon through the same queue OS updates use. It has to: the prompt tells those scripts to replace
the bundle under `/Applications` or run `installer -pkg ... -target /`, and a bundle that arrived by
MDM or any installer that asked for a password is `root:wheel`. Run as the logged-in user, the
script's `rm` printed `Permission denied` for every file in `Ollama.app` and left the old version in
place, while the log called it a script failure. The queue keeps its one security property in both
directions — an app-patch request carries a name, the daemon re-fetches the row, verifies the
signature and re-asks `runs_as_root` itself, so a forged request can neither run arbitrary code nor
get `brew` run as root — and a request older than `queue::REQUEST_TIMEOUT` or from before the
current boot is discarded unrun, because the process that would have shown progress for it is gone.
The prompt now describes that context (root, a LaunchDaemon, no GUI session — quit the application
via `launchctl asuser`, never relaunch it), so a script is generated for the process that runs it.
An App Store row (`PackageManager` "App Store") goes to the daemon too, for the reason under
"Platform buckets": the install half needs root and the download half needs the console user's
session, and only root can be both.


**"No network call at all" includes the patching policy, and 0.5.0 got that wrong.**
`/api/patching-policy` sits inside nginx's client-certificate regex, so there is no such thing as
fetching it without an identity — but the Linux per-user process tried, under a comment asserting
the route was ungated. Every Linux host with a graphical session therefore 403'd once a minute
forever while the root service, having deferred to that process, patched nothing; the host went on
reporting healthy check-ins the whole time, and the symptom only appears on day two because a
freshly enrolled host isn't due until then. The fix is the Windows arrangement, which had it right:
the root service fetches the policy on **every** check-in — before registration, and regardless of
whether it then defers — and writes `/var/lib/kintsugi-agent/policy.json` `0644`; the per-user
process only ever `policy::load_cached`es it. Keep `fetch` private to the root side on both
platforms, and don't reintroduce a per-user fetch of anything.


**"Next check-in" is a prediction the per-user process makes from a root-written file, and "Check
In Now" is a queue request.** The menu's check-in line is `checkin_schedule::next_check_in_epoch`:
the next occurrence of the minute in `checkin-schedule.json`, which only the root half writes.
That file therefore has to be readable by the per-user process on all three platforms — root's
`0644` umask into a `0755` directory on macOS, an explicit `0644` in the Linux `persist` (the state
directory is traverse-only, so the mode on the file is all there is, exactly as for `policy.json`),
and `%ProgramData%`'s inherited `Users` read on Windows. It shows "not yet scheduled" until the
first check-in writes it. The scheduler re-reads it every tick and pushes it to the menu only when
it changes, because `format_due` shells out for the local time. "Check In Now" is
`RequestKind::CheckIn`, carrying nothing — the root half re-registers, re-reports the inventory and
runs `self_update`, which is everything it would do at the hour, so the worst a forged request does
is a check-in early. What answers it differs by platform in a way worth knowing before debugging
one: on macOS the file is only a *wake-up* — `WatchPaths` starts the daemon, every daemon
invocation *is* a check-in, and `DaemonRequestHandler::check_in` merely confirms work already done
before the queue was reached — whereas the Windows service (`Agent::check_in`) and the Linux queue
service (`main::check_in`, under the lock it already holds) run the check-in *inside* the request.
If that check-in installs a newer agent, the per-user process asking is restarted before it can
read the answer; the new version in the menu is the confirmation. Both action items are greyed out
while either a patch cycle or a check-in is running, because a cycle owns the schedule state for its
whole length (see below) and the scheduler cannot start a second one while it does.


**A patch cycle runs on its own thread, and the confirmation dialog is why.** All three schedulers
used to call `patch_cycle::run` inline, so the loop was parked inside a dialog that stands there for
a whole delay period — hours. Three things went wrong while it was, and only the first is obvious:
the menu's "Next check-in" line stopped updating; a menu click sat in the channel until the dialog
came down and then ran unasked, which is exactly what the greying above exists to prevent; and on
Linux the per-user heartbeat lapsed after `queue::HEARTBEAT_MAX_AGE` (ten minutes), so the root
service concluded nobody was logged in and **patched unattended under a user who had the prompt
open**. `main::spawn_cycle` hands the cycle its own thread, and hands it the `ScheduleState` by
*ownership* rather than behind a lock — a mutex held for the length of a dialog would have moved the
block rather than removed it, since `is_due` would then be the thing waiting. So `state` being `None`
in the scheduler loop *is* "a cycle is in flight", the join site is where the state comes back, and
`AgentStatus::AwaitingAnswer` is what greys "Patch Now" while a prompt is on screen without opening
the progress window over it. Keep the three copies in step; the shape is identical and only the
arguments a cycle needs differ.


**Only the Linux agent patches with nobody logged in, and it has to.** Both other agents put the
patching schedule in the per-user process, which costs nothing when every managed host is somebody's
desktop. Most of a Linux fleet is servers with no graphical session at all, so the same design would
mean the majority of hosts silently never patched. The per-user process writes a heartbeat into the
queue directory (`queue::record_heartbeat`); when the root service's hourly check-in finds none
recent, it runs the cycle itself with the confirm/delay/warning steps dropped rather than faked —
there is nobody to ask. The per-user process exits immediately when it has no `DISPLAY`, so an SSH
login can't suppress a server's own patching by leaving a heartbeat behind.


**Windows and Linux serial numbers are frequently placeholders.** `HKLM\HARDWARE\DESCRIPTION\System\BIOS`
and `/sys/class/dmi/id/product_serial` read the same SMBIOS field and inherit the same junk from board
vendors: "To Be Filled By O.E.M.", "Default string", "0", "Not Specified" (which is what every guest
of a bare `qemu-system-x86_64` reports). The serial *is* this host's identity — it becomes the
certificate CN, which `[RequireAgentIdentity]` compares against every request body — so two hosts
sharing one would share a host record, a certificate, and each other's data.
`system_info::serial_number` in both agents therefore screens against a placeholder list and
**refuses to enroll** rather than inventing a value. macOS has no equivalent failure mode.


**Every request an agent makes is retried, and for a long time only the POSTs were.**
`post_with_retry` has been there since the beginning, so an intermittently lossy path between a host
and the server is invisible on enrollment, inventory and patch results — and every GET beside it had
exactly one attempt. On one host that cost a whole patch cycle to a bad minute:
`upgrade::fetch_upgrade_statuses` is called once per application, deliberately, so five applications
in a row each spent the client's full 15s timeout and reported "operation timed out". `get_with_retry`
now sits next to `post_with_retry` in the same file, and its budget is deliberately different — a
POST reports something that has already happened and can afford minutes of backoff, a GET is holding
up a patch cycle somebody is watching, so it takes a short per-attempt timeout and a short delay. The
one GET left un-retried is the self-update *download*, because its client's timeout is sized for a
multi-megabyte transfer and a short per-attempt budget would cut a healthy one short; it says so.
This is the same asymmetry `remote_control`'s session socket had, in a third place — if you are
adding a request to an agent, the question to ask is what happens to it when one SYN is dropped.


**Replacing a running binary differs.** macOS and Linux stage next to the target and rename over it
(atomic, and Unix will unlink an open file). Windows locks a running image, so `self_update` renames
the *old* binary aside — which Windows does allow — copies the new one into the freed path, and
deletes the displaced copy at next service start. It restores the old one if the copy fails; leaving
the path empty would break the agent permanently. Linux also has nothing to restart on the root
side: it is a oneshot that is about to exit, and the next timer firing execs whatever is at the path
by then — only the long-running per-user units get restarted.


## Remote control: what the three agents share

The server's half is in `src/CLAUDE.md`, the viewer's in `web/CLAUDE.md`. The timeout orderings
between agent and server, and the media protocol's hand-mirrored wire format, are path-scoped rules
under `.claude/rules/` that load when you open the file at either end.

An administrator can take control of a host's screen, keyboard and mouse from the Hosts screen's
Connect action, or open a **terminal** on it from the Terminal action beside it. Both are the same
session underneath — one request, one relay, one audit row — and `RemoteControlSessionKind` on the
wire is the whole of the difference.

**A shell session asks nobody, and that is the deliberate exception to everything below.** It is
the access an administrator of this fleet already has by other means — it is what they would reach
SSH or a remote PowerShell session for — and the only reason it is routed through this agent is
that SSH would need an inbound port on every managed machine, which is exactly what the relay
design exists to avoid. So it reports `RemoteControlConsent.NotRequired` in place of a decision,
and the compensating control is that the same `remote_control_sessions` row a screen request writes
is written for it, naming who asked and when. Three things follow and none of them is optional.
`NotRequired` is accepted **only** for a shell, checked in `RemoteControlSession.RecordConsent` and
again in `RemoteControlRelaySession.LatchConsent` — the second is not belt-and-braces, it is the
latch the socket gate actually reads, so enforcing it in the entity alone would let an agent open
*capture, keyboard and pointer* on a host shown no dialog simply by claiming no permission was
needed. The shell always runs as the account that already holds this host's fleet identity —
**root** on macOS and Linux, **SYSTEM** on Windows, and **never the logged-in user on any
platform** — which is what makes "nobody is asked" defensible: it is not a session inside somebody
else's desktop, it is the administrative access an SSH key would already give. It is stated on the
wire (`ShellInfo.user`) and shown in the viewer rather than left to be remembered, so a host that
ever answered with a user account would be visible as the different thing it is.


**A terminal needs no desktop, and decoupling that from reachability is what makes the feature
useful.** Both the Linux and Windows agents used to hold their control socket *only* while somebody
was logged in, so "reachable" meant "somebody is sitting at a desktop". That is right for a screen
session and quite wrong for a shell: most of a Linux fleet is servers with no graphical session at
all, and those are precisely the hosts an administrator wants a terminal on. Both now hold the
socket whenever the host has an identity, treat the desktop as a *capability* rather than a
precondition, and answer a screen request that arrives without one with
`ConsentOutcome::Unavailable` — the agent saying "there is nobody here" at once, rather than the
host being invisible and the administrator being told the agent is unreachable. **macOS is still unreachable with nobody logged
in**, for both kinds: its *control* socket is held by the per-user process, so there is nothing to
receive the request in the first place. Only the shell's execution moved to root, not the
negotiation — moving the control socket too would mean porting the Linux split (root holds the
socket, per-user connects back over a local one) to macOS wholesale.

The PTY runs in whichever process already holds the identity and needs no helper: the resident root
unit on Linux (no `remote_ipc` hop — nothing crosses it for a shell) and the **service itself** on
Windows via ConPTY, with no `session_launcher` and no SYSTEM session helper. Everything the Windows
helper exists for is about a desktop, and requiring one would put a logged-in user back in the way of
the host most likely to want a shell.


**Neither socket is new, and no nginx change was needed.** A shell session uses the same standing
control socket and the same per-session media socket a screen session does; the media protocol
gained two message kinds and one text message and nothing else. Keep it that way — a
`/api/admin/remote-shell` location would be a second thing to keep in step with the agent regex for
no gain.

The rest of this section is about **screen** sessions, which are the ones with a consent rule.


**Nothing happens until the person at the keyboard says yes.** The whole feature rests on three
properties, and none of them is optional or configurable: consent is asked for every session and
names the requesting administrator; silence is a refusal; and while a session runs it is visible in
the menu bar with a way to end it. A fourth, durable one sits behind them —
`remote_control_sessions` records every *request*, including the refused ones, the timed-out ones
and the ones against a host that never answered, because "an administrator asked to watch this
laptop and was told no" is precisely the event an auditor is looking for.


**The control socket is standing, and it is the only push channel in the system.** Everything else
an agent does is a request it makes when it has something to say; remote control is the one case
where the server has to reach a host, and an hourly check-in cannot carry "somebody would like to
see your screen now". So the per-user process holds one socket open for its life, with reconnect
backoff. Sessions get their own socket so a frame stream can never queue behind a control message,
and so a session dropping does not cost the host its reachability.


**A session socket gets one chance where the control socket gets unlimited ones, and that asymmetry
hid a flaky link for months.** The control socket's connect sits in a reconnect loop, so a connect
that times out costs a log line and a few seconds of backoff before it succeeds — a host whose SYNs
to the server are intermittently blackholed reports healthy check-ins, holds a live control socket
and looks entirely well. A session socket was a single `connect`, so the *same* packet loss ended
the session with "could not connect", and a screen session tried a minute later would work,
"proving" the network was fine. That is exactly how one Windows host presented: terminal sessions
failing, screen sessions succeeding, `Test-NetConnection` succeeding, and two `connection timed out`
lines against the control socket in the log that nobody had reason to read. So all three agents
retry the session connect (`connect_session_socket`), with a per-attempt timeout deliberately
*shorter* than the control socket's — a blackholed SYN is not answered by waiting longer, it is
answered by a fresh connection — and the whole budget is held inside the server's pairing window by
a test. Log the peer address when changing any of this: `remote control socket open to {url}` named
no address, which is why the diagnosis needed a support call rather than the log.


**Consent timeouts have the opposite polarity to patching, and the code keeps them apart.**
`dialogs::ConfirmChoice::TimedOut` means "nobody was at the desk, so count it as a delay" — the
user never refused and patching happens regardless. `RemoteControlChoice::TimedOut` means **nobody
consented**, and is treated exactly as a refusal. They are separate enums for that reason; reusing
the first would have put the safe default one careless `match` arm away. The dialog's default button
is Deny for the same reason, since AppleScript reports the default button as `button returned:` even
when it dismissed the dialog itself.


**A delay the dialog spent waiting is not a delay to be served again, and getting that wrong made
the countdown twice as long as the policy says.** The confirmation dialog's giveup *is* one delay
period (`patch_cycle` passes `policy.delay_seconds()` as the timeout), so by the time it fires, the
time a delay buys has already been spent. `register_delay` — right for an explicit "Delay" click,
which asks for a fresh period — then postponed by another one on top, so eight one-hour delays
counted down over sixteen hours: dialog for an hour, an hour of nothing, dialog again with the
count decremented. `ScheduleState::register_unanswered_prompt` is the unanswered case instead: it
charges the budget for however many whole delay periods actually elapsed while the dialog stood
there, and leaves the cycle due *now*, so the next poll tick re-asks at once and the count falls
8 → 7 → 6 once per period. Crediting elapsed periods rather than assuming one is what covers sleep
— a machine that slept with the dialog open wakes to a giveup that fires immediately, and spends
the delays that passed rather than starting them again. The budget running out needs no special
case: the next tick finds `can_delay` false and shows the "no delays left" dialog, which is where
the five-minute notice comes from. **That dialog is the warning rather than a preamble to it**: it
states the period, stands there for as much of it as the user leaves it up, and `remaining_warning`
hands `execute` whatever is left, so the notice is five minutes in total — somebody who reads it and
clicks OK still gets the rest of the period, and a host with nobody at it waits five minutes instead
of the ten that showing both in series cost. Nothing here is a running timer — `is_due` compares wall
clock against a persisted absolute epoch, which is why sleep, hibernation and a restart all need no
wake detection.


**A host with several monitors is watched one at a time, and the picker is a media-protocol
addition alone.** `DisplayInfo` carries a `displays` list and an `activeDisplayId`; the viewer sends
`{"type":"select-display","id":…}` back; the agent restarts its capture there and resends
`DisplayInfo`. **Nothing in the server changed for any of it**, which is the property "the server
relays the media protocol without parsing it" exists to buy — and equally means nothing server-side
would catch the two ends drifting, so `web/test/data/remote_control_mapper_test.dart` and the three
agents' `remote_protocol.rs` tests assert the same JSON from both sides.

Five things about it are load-bearing, and the first is the one that fails on the commonest desk.

- **The encoder must be told to send a whole frame on every switch, explicitly.** `FrameEncoder`
  finds changes by diffing against the previous frame, and two identical monitors — an ordinary
  office setup — are the *same size*, so its own geometry check sees nothing changed and it happily
  diffs the new display's pixels against the old display's, sending only the tiles that differed and
  leaving fragments of the previous monitor everywhere the two agreed. `force_full_frame` is what
  each agent calls; a test in all three pins it.

- **Everything held is released before the pointer space moves.** A modifier or a mouse button down
  across a switch would otherwise stay down with nothing on either screen to explain it — the same
  invariant `release_all` already keeps at the end of a session, at the one other moment the
  coordinate space changes underneath somebody's hand.
- **Ids are opaque and agent-defined, deliberately.** A `CGDirectDisplayID` on macOS, a one-based
  index into Windows' monitor enumeration, a RandR monitor (or zero for the whole root window) on
  X11, a PipeWire node id on Wayland. The viewer echoes one back and interprets nothing, so there is
  no shared numbering to keep in step; the *label* is built by the agent too, because only the host
  knows that one of its screens is "Built-in Display" or "HDMI-1" — a label composed in the browser
  from a resolution says nothing about two identical monitors. An empty list means "offer no
  picker", which is what an agent from before this sends and what a one-display host sends, and the
  viewer shows the control only where there is a genuine choice.
- **A failed switch never ends the session.** A monitor can be unplugged between the list being
  drawn and the choice being made; the agent logs it, stays on the display it was on, and says so in
  the next `DisplayInfo`. Ending the session instead would cost the administrator a granted session
  and the host's user another consent dialog over a pulled cable. For the same reason the viewer's
  picker is stateless — it shows what the agent last *reported*, never what was clicked.


**How the four backends reach a second display differs completely, and two of them are a crop rather
than a different source.** macOS restarts the ScreenCaptureKit stream against another `SCDisplay`
and moves `InputInjector`'s origin, which the injector already had because `CGEventPost` works in
global points. Windows and X11 both already had a device context or a root window spanning the
*whole* virtual desktop, so a switch is a source rectangle inside it — which is why both offer "All
displays" as an entry of its own and macOS does not. Wayland is the one that has to ask somebody
else; see clients/linux-agent-wayland/CLAUDE.md.

Two platform consequences worth knowing before debugging one. **Windows needed
`MOUSEEVENTF_VIRTUALDESK` and a virtual-desktop divisor together, and either alone puts the pointer
somewhere else** — `MOUSEEVENTF_ABSOLUTE` normalises over the *primary* monitor without the flag, so
coordinates spanning the desktop get squeezed onto one screen. On a single-monitor host the two
rectangles are the same numbers and the whole conversion is the identity, so a wrong one tests
perfectly clean; `input_injection::PointerSpace` is a separate testable type outside the
`#[cfg(windows)]` module for exactly that reason, and its tests use a desktop with a **negative**
origin, which is what a monitor placed left of or above the primary gives. And **the Windows session
helper has to remember which display was picked**, because Windows switching desktops (a UAC prompt,
the lock screen) rebuilds the capture from scratch — a rebuild that passed `None` would quietly drop
the session back onto the primary monitor with nothing to explain it.


**The default display changed on Linux and Windows multi-monitor hosts, and that is deliberate.**
Both now start on the primary monitor rather than the whole desktop: two monitors sent as one
picture are twice as wide for the same 1600-pixel budget, so both arrived at half the resolution of
the one anybody was looking at. "All displays" is one click away, so nothing is lost. macOS never
had the whole-desktop form to lose.


**`ui.Image` and `CGEventSource` both need releasing by hand.** The viewer keeps decoded tiles as
live `ui.Image`s keyed by position rather than compositing to an offscreen surface, so a repaint is a
few `drawImageRect` calls — but each holds a native texture the garbage collector does not account
for, so every replaced or discarded tile is disposed explicitly. On the agent side
`InputInjector::release_all` runs on **every** path out of a session including a dropped socket: a
session that ends while the remote user happens to be holding Command otherwise leaves the Mac's own
owner with Command stuck down, and nothing on screen explaining it.


**A host is reachable only if somebody is logged in**, which is a stronger statement than the Hosts
screen's own status. "Online" there means a check-in within the last interval, up to an hour ago;
reachable here means a per-user agent process holds a socket right now. The Connect button is
therefore offered regardless of status and the *server* answers — with a session already marked
`AgentUnreachable`, which the remote-control screen explains. Disabling the button on a stale status
would hide working hosts.


## Couplings nothing enforces

- **`pty.rs` is one file twice and a third that only matches its shape.** The macOS and Linux copies
  are byte-identical — both reach a PTY through `openpty`, `setsid` and `TIOCSWINSZ` — and `diff`
  should report nothing. The Windows one is ConPTY and shares no implementation at all; what is kept
  identical is the six-call surface above it (`spawn`, `read_available`, `write_all`, `resize`,
  `try_wait`, `terminate`), because the relay loop that drives it is meant to read the same on all
  three. Its `try_wait` is not redundant with the EOF check beside it: a shell that exits while a
  background process still holds the slave open produces no EOF at all, and without it the session
  sits there attached to nothing.
- All three `screen_capture.rs` share their whole "pure half" — `FrameEncoder`, `tile_differs`,
  `extract_rgb` and the tile constants — verbatim. They all feed the same viewer, so the tile grid
  and the full-frame threshold have to agree; the platform half above it is the only part that
  differs, and it differs completely (ScreenCaptureKit, GDI, X11 `GetImage`).
- The three agents' tray glyphs (`macos-agent/assets/menu-bar-icon.png`,
  `linux-agent/assets/tray-icon.png`, `windows-agent/assets/tray-icon.png`) are one file three times:
  a black shape on transparency, which each platform recolours itself — macOS as a template image,
  Windows by tinting it black or white for the taskbar's `SystemUsesLightTheme` at load and on every
  `ImmersiveColorSet` broadcast. A replacement that bakes in a colour or a background breaks that on
  all three at once; `cmp` them after touching one.
