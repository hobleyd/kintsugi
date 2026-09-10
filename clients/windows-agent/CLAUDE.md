# The Windows agent

Loaded when Claude reads files under `clients/windows-agent/`. What the three agents share is in
`clients/CLAUDE.md`; this is what Windows forced to differ.

## Building and testing from a non-Windows machine

**Working on the Windows agent from a non-Windows machine.** It can't be built natively, but it can
be fully type-checked *and its unit tests actually run*, via Docker + mingw + Wine:

```bash
docker run --rm --platform linux/amd64 -v "$PWD/clients/windows-agent":/w -w /w <image> \
    cargo test --target x86_64-pc-windows-gnu
```

where `<image>` is `rust:1-slim` plus `gcc-mingw-w64-x86-64` and `wine`, with
`CARGO_TARGET_X86_64_PC_WINDOWS_GNU_LINKER=x86_64-w64-mingw32-gcc` and
`CARGO_TARGET_X86_64_PC_WINDOWS_GNU_RUNNER=wine`. The `-gnu` target rather than `-msvc` because
`ring`'s build script compiles C and the MSVC headers can't be shipped; both exercise the same
`#[cfg(windows)]` code and the same `windows-sys` bindings. This is worth the setup — it is what
caught the `winget list` parser silently reporting zero applications.

**On an Apple Silicon Mac, drop the `--platform linux/amd64` and use `--no-run`.** That flag is what
makes the above unusable there: under qemu, `gcc`'s own `collect2` takes a SIGSEGV part-way through
`ring`'s C or a build script's link step, and the failure reads as a compiler bug rather than as
emulation. It is also unnecessary for the half that matters. Debian's `gcc-mingw-w64-x86-64` is
packaged **for arm64 as well**, so the cross-compiler runs natively and emits x86_64 Windows objects
— the whole agent compiles and *links* with no emulation at all:

```bash
docker run --rm --platform linux/arm64 -v "$PWD/clients/windows-agent":/w -w /w \
    -e CARGO_TARGET_X86_64_PC_WINDOWS_GNU_LINKER=x86_64-w64-mingw32-gcc \
    rust:1-slim bash -c 'apt-get update -qq && apt-get install -y -qq gcc-mingw-w64-x86-64 \
        && rustup target add x86_64-pc-windows-gnu \
        && cargo test --locked --no-run --target x86_64-pc-windows-gnu'
```

`--no-run` is the whole difference: it type-checks every module *and* the `#[cfg(test)]` ones, which
is what catches a signature or an FFI declaration that does not exist, while stopping short of
executing an x86_64 PE — the one step that genuinely needs Wine and therefore emulation. Running the
tests still wants an amd64 host (or Rosetta rather than qemu: `colima start --vm-type=vz
--vz-rosetta`, or Docker Desktop's "Use Rosetta for x86/64 emulation"). Note the Windows agent also
**cannot be checked on the macOS host at all**, not even partially: `winreg` is an unconditional
dependency and its `compile_error!` fires before anything else is compiled.


## Identity, the serial chain, and the tray

**The Windows tray process holds no identity and makes no network call.** On macOS the per-user
process talks to the server directly and runs patches itself (Homebrew refuses to run as root). On
Windows every upgrade needs elevation, so patches move to the service anyway — and once they have,
the tray process has no reason to hold the client private key either. So it goes through
`queue.rs` for all three privileged things: *what's pending*, *patch this application*, *install
Windows updates*. The security property is the macOS queue's, strengthened: **a request never
carries anything executable.** An app-patch request names an application; the service independently
re-fetches that application's upgrade path from the server and verifies its signature before running
anything. The worst a forged request can do is start an already-approved upgrade early.


**One blank field is not one missing serial, and the Windows agent walks a chain.** The registry key
above is the value an administrator sees on the sticker, but it is routinely *absent* on hardware
whose `Win32_BIOS.SerialNumber` carries that same serial — so reading only the registry reported no
serial on a machine that plainly has one. `choose_serial_number` therefore tries, in order: the
registry serial, `Win32_BIOS.SerialNumber`, `Win32_ComputerSystemProduct.IdentifyingNumber`,
`Win32_BaseBoard.SerialNumber` (SMBIOS type 2, reachable *only* via CIM — the registry key exposes
the baseboard's manufacturer, product and version and no serial at all),
`Win32_SystemEnclosure.SerialNumber`, `Win32_ComputerSystemProduct.UUID`, and only then the
`MachineGuid`. All five CIM fields come from one PowerShell pass (`FIRMWARE_IDENTITY_SCRIPT`), read
lazily — a host whose registry value is populated never spawns it. `SMBIOSAssetTag` sits beside the
chassis serial and is deliberately *not* read: an asset tag is administrator-assigned and frequently
identical across a purchase batch.

The order is by how well each field identifies *this physical machine*, and the two rungs at the
bottom are where the reasoning is easy to get backwards. `MachineGuid` is **last**, not second: it
identifies a Windows *installation*, sysprep regenerates it, and an image deployed *without* sysprep
gives every clone the same one — which are exactly the machines whose SMBIOS serial is a placeholder
too. The SMBIOS system UUID outranks it because it is per-machine and set per-VM by every hypervisor,
but it needs its own screening (`PLACEHOLDER_SYSTEM_UUIDS`): the all-`F` form means "field omitted",
and `03000200-0400-0500-0006-000700080009` is a constant shipped by some VMware and Dell firmware, so
accepting it would enroll a whole fleet as one host.


**Widening that chain re-identifies hosts, and `identity::load` will not notice.** It reads the
certificate off disk and compares nothing, so a host already enrolled under a fallback identity —
its `MachineGuid`, or a placeholder that used to pass screening — starts sending the newly-found
serial in request bodies while presenting a certificate whose CN is the old value.
`[RequireAgentIdentity]` then 403s every authenticated route, permanently, while the host still looks
enrolled. The remedy is the documented one (delete `identity/` and let it re-enroll), but nothing
prompts for it and `self_update` delivers the change unattended. So before shipping any change to
what `serial_number` returns: check the Hosts screen for GUID-shaped or placeholder-shaped serials,
because those are precisely the hosts that will need re-enrolling.


**The Windows identity directory is SYSTEM and Administrators only, and a service running as
anything else cannot read its own identity.** `identity::restrict_identity_permissions` strips
inheritance and grants exactly `S-1-5-18` and `S-1-5-32-544`, once, inside `enroll` — there is no
repair pass re-asserting it, unlike Linux's `config::repair_directory_modes`. The failure mode is
quiet in a specific way: `icacls` reads as perfectly correct, because it *is* correct for the two
SIDs it names. Check `sc.exe qc KintsugiAgent` before believing an ACL. `install.ps1` pins
`obj= LocalSystem` on both branches now, but nothing stops a hardening baseline changing it later.

`identity::load` used to answer that with `None` — the same answer it gives a host that has never
enrolled — so the agent reported "this agent has not enrolled an identity yet", re-enrolled every
check-in forever, spent a certificate issuance on the server each time, and died on the *write*,
naming `agent.crt` (the first file written) rather than whichever read actually failed. It now
separates `NotFound` from every other error and refuses to start an enrollment it knows will be
refused. The remedy is to delete the identity directory **outright** rather than its contents:
`create_dir_all` then recreates it inheriting from the parent, which is what clears stale per-file
permissions.


## Self-update and restarting the service

**The Windows service cannot restart itself, and the hand-off that does it has to be watched.** A
process that stops its own service is killed by that stop before it reaches the start, so
`self_update` spawns the *newly installed* binary as `kintsugi-agent.exe --restart-service`
(`self_update::run_restart_helper`), which stops the service through the SCM, waits for `Stopped`
for as long as a check-in can take, starts it, and logs every step to `service.log`. It used to be a
detached PowerShell running `Restart-Service -Force -ErrorAction SilentlyContinue`, and that left a
host on 0.5.2 for days with a 0.7.0 binary beside it, its tray (restarted separately, by `schtasks`)
on the new build and every "Check In Now" timing out because a 0.5.2 service does not know that
request kind. Two independent faults, neither of which logged anything: `DETACHED_PROCESS` gives
PowerShell no console and null standard handles, which its console host does not reliably start
under; and `Restart-Service` waits exactly two seconds for `Stopped`, then errors out — never
calling `Start` — unless the service is reporting `StopPending`, which this service's control handler
did not do while its loop polled the shutdown flag every two seconds. Three things now hold. The
control handler in `main.rs` reports `StopPending` (`service::STOP_WAIT_HINT`), which is also what
makes `install.ps1`'s own `Stop-Service` reliable; `self_removal`'s PowerShell helper, which cannot
be the native binary because it deletes it, runs with `CREATE_NO_WINDOW` and `NUL` handles instead.
And the install writes `config::self_update_restart_marker_path()` before handing off, which the
service deletes on every start — so a check-in that finds it is, by construction, the displaced
build still running out of `kintsugi-agent.exe.old`, and re-issues the restart instead of trying to
move a file it is executing. The tell for a host in that state is the hourly
`could not move the running binary aside to ...kintsugi-agent.exe.old: Access is denied` line; hosts
wedged by a release before this one need `Restart-Service KintsugiAgent` by hand.


## Remote control: the service, the SYSTEM session helper, and the pipe

**The other two agents need a different design, and Windows now has it.** On macOS the per-user
process holds the fleet identity, which is exactly what remote control needs — the screen and
keyboard belong to a GUI session the root daemon does not have. On Windows the per-user half holds no
identity and makes no network call at all, so it cannot open a socket; and the service, which does
hold the identity, cannot reach a desktop at all because of session 0 isolation. Neither half can do
this alone.

So Windows splits it, and `remote_ipc` is the boundary: the **service** holds both WebSockets and
relays bytes, a **session helper** does the asking, the capturing and the input, and a named pipe
joins them. The rejected alternative was handing the certificate and key to a per-user process so it
could behave like macOS — that would put the fleet private key in the address space of a process
running as whoever is logged in, which is precisely what the identity directory's ACL exists to
prevent.


**The helper runs as SYSTEM inside the user's session, and both halves of that are load-bearing.**
The first cut put this work in the tray process, and it could not answer a UAC prompt — the single
most common thing a support session needs. Two separate Windows rules stood in the way. `SendInput`
cannot reach a window at higher integrity than the sender, so an elevated installer could be watched
and not clicked. And a UAC prompt is drawn on a *different desktop object* (`Winlogon`), which a
thread attached to `Default` can neither read nor write — so the screen froze on the last frame until
somebody answered at the machine.

`session_launcher` therefore duplicates the **service's own** token, moves the copy into the console
session with `SetTokenInformation(TokenSessionId)` — which needs `SE_TCB_NAME`, so only SYSTEM can do
it — and launches the helper with it. `WTSQueryUserToken` is deliberately *not* used to build that
token: it would give the user's own token and a medium-integrity process, back where the tray was.
`remote_desktop` then keeps the capture thread attached to whichever desktop has input, following
Windows onto the secure desktop and back.


**Reading "a SYSTEM process doing input injection" as a step backwards gets it exactly wrong.** It is
a net improvement on three counts. It exists only while a session runs, spawned by the one process
holding this host's identity and only after the server asked. The pipe now has no interactive user on
either end, so its ACL grants Local System and Administrators and **nothing else** — which deletes,
rather than mitigates, the attack the tray design had to reason about, where a local process races to
answer a consent request and feeds the operator a fabricated screen. And the consent dialog is now a
SYSTEM-owned window, so UIPI works in our favour: user-level malware can neither click it nor
suppress it.


**`install.ps1` pinning `obj= LocalSystem` is now load-bearing twice over.** It was already needed so
the service could read the identity directory; it is now also what makes `SE_TCB_NAME` available, and
without it the helper cannot be launched into the session at all. A hardening baseline that moves the
service to a virtual account breaks remote control specifically, and the error names the privilege.


**Thread layout in the helper is dictated by one rule**: `SetThreadDesktop` refuses a thread that
owns any window. So the capture-and-input thread creates none and can follow desktops; the consent
dialog and the session banner each get their own thread and attach once. The capture must also be
**rebuilt** on a desktop switch — its device contexts belong to the desktop they were made on, and a
stale one yields frames of the desktop the user is no longer looking at.


**The indicator moved from the tray to a banner, and that is an upgrade.** A `SYSTEM`-owned bar at
the top of the screen with an "End session" button is visible without being looked for, where a tray
item has to be clicked to be found — and UIPI stops a medium-integrity process clicking it *or
closing it to hide that a session is running*. The tray's remote-session entries were removed rather
than kept alongside, so there is one source of truth about whether a session exists.


**Windows reachability now follows the console session, not a connected process.** With capture in a
helper that exists only during a session, "a tray is connected" is gone as a signal, so the service
asks Windows: `console_session_with_user` is `WTSGetActiveConsoleSessionId` plus a
`WTSQueryUserToken` probe, and the control socket is held open exactly while the answer is yes. A
locked screen still counts, which is correct — the consent dialog appears on the lock screen, and if
nobody is there it times out and is recorded as a timeout.


**One restriction is left on Windows and no process can fix it.** While the secure desktop has input,
only that prompt is usable — that is what a secure desktop is *for*. The consent dialog says so, and
the elevated-window wording it used to carry is gone, because that gap actually closed.


**Windows captures with GDI, not Desktop Duplication, and Remote Desktop is why.** DXGI Desktop
Duplication is the modern API and gives dirty rectangles for free, but `DuplicateOutput` returns
`DXGI_ERROR_UNSUPPORTED` in an RDP session — and a fleet agent is very often reached over RDP, so the
modern API fails precisely on the hosts an administrator is already logged into remotely. `StretchBlt`
from the desktop DC works in both. The costs are named in `screen_capture`: no hardware video
overlays, some layered windows missed, and the cursor has to be drawn in by hand because `BitBlt`
does not include it.


## Couplings nothing enforces

- Windows PowerShell 5.1 decodes a BOM-less `.ps1` using the system ANSI code page, not UTF-8. The
  Windows agent writes every script with a UTF-8 BOM for exactly that reason, and the
  server-written ones are kept ASCII-only as well.
- `session_launcher::HELPER_ARGUMENT` and the `--remote-session-helper` arm in `main` are the same
  string in two places. Rename one and the helper starts, fails to recognise its own mode, runs an
  ordinary check-in instead, and the session times out having produced no frame — with nothing in
  either log saying why.
- The Windows ConPTY path is **compile-checked but never executed** by anything here — the
  docker + mingw + wine arrangement has no console host, so `pty.rs`'s tests there cover the pure
  half only (command-line quoting, the environment block). The Unix agents' `pty.rs` does have live
  tests against a real shell for the behaviour the three share.
- **The Windows service's pipe has one instance, so a connection handed out twice is a session
  lost.** `ConnectNamedPipe` on an instance whose client is still connected does not wait for a new
  one — it returns `ERROR_PIPE_CONNECTED` for the client already there, which reads as success — so
  an accept loop that runs ahead of its consumer hands out the same client repeatedly, and dropping
  any copy runs `DisconnectNamedPipe` on the instance the others are using. Combined with a helper
  leaked by a `relay()` that returned `Err` (which is why `Relay` has a `Drop`), that cost the
  *next* screen session: the stale helper's connection was discarded and took the freshly launched
  helper's pipe with it, failing the first write with error 233 and losing the session before the
  request was even sent. `PipeListener::accept` now waits for the outstanding connection to be
  dropped, and `start_session_helper` drains the channel before launching. Keep both — either alone
  leaves a window.
