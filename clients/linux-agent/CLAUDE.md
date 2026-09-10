# The Linux agent

Loaded when Claude reads files under `clients/linux-agent/`. What the three agents share is in
`clients/CLAUDE.md`; this is what Linux forced to differ. The Wayland capture backend is its own
crate — see `clients/linux-agent-wayland/CLAUDE.md`.

## Building and testing from a non-Linux machine

**Working on the Linux agent from a non-Linux machine.** Far easier, because there is no
cross-compilation involved at all — a Linux container *is* the target:

```bash
docker run --rm -v "$PWD/clients/linux-agent":/w -w /w rust:1-slim cargo test
```

One thing that container will *not* do on an Apple Silicon Mac: build the
`x86_64-unknown-linux-musl` release CI ships. `rust:1-slim` is arm64 there, so its `musl-gcc` cannot
cross-compile `ring`'s C, and the failure looks alarming — a `cc-rs` error deep in a dependency
rather than anything about the target. Use `aarch64-unknown-linux-musl` to check the static-linking
property (it holds or fails for the same reasons), or `--platform linux/amd64` if the exact artifact
matters. And note `cargo build` for the *host* works on macOS too, which is the quickest syntax check
of all — see the coupling note on keeping it that way.

It links no C library and no GUI toolkit (see the `ksni` note in its `Cargo.toml`), so the stock
image needs nothing added. Every output-parsing function — `flatpak list`, `snap list`, the DMI
serial screening, `apt-get --just-print upgrade` and the four other package managers' listings —
takes a `&str` and has tests against captured real output, for exactly the reason the Windows
`winget list` parser does.


## The state directory, and two root entry points

**The state directory is `0711`, and `0700` silently kills the drop-box.** A queue at `1733` is
unreachable if nothing outside root can *traverse* its parent, so no user can write a request or a
heartbeat — and because the per-user process cannot list the directory either, `is_dir` on the
queue fails exactly as it would if the agent were not installed, which is what 0.5.0's warning
wrongly claimed. `0711` is traverse-only: root is still the only one who can list the directory or
read `identity/` (still `0700`, and deliberately). `install.sh` sets it, and
`config::repair_directory_modes` re-asserts both modes on every root check-in — required, not
belt-and-braces, because `self_update` replaces the binary and never re-runs the installer, so
hosts already in the field have no other repair path.


**Two root entry points mean an explicit lock.** launchd and the Windows SCM both give mutual
exclusion for free (one instance per job; one resident service). systemd guarantees that per *unit*,
and the Linux agent has two — `kintsugi-agent.service` on the timer and `kintsugi-agent-queue.service`
on a `.path` watch — so `lock.rs` takes an advisory `flock` both of them hold. Without it a
queue-triggered patch can land inside an unattended cycle and two `apt-get` runs deadlock on the
dpkg lock with no useful error.


## Remote control: a fourth root unit, and two backends

**Linux takes the same split, and two things about it are specific to the platform.**

The first is that it needed **a fourth root unit**. The other three cannot hold a standing
connection — `kintsugi-agent.service` is a oneshot on a timer, `kintsugi-agent-queue.service` is a
oneshot on a path watch, and the per-user unit holds no identity — so remote control runs as
`kintsugi-agent --remote-control` under `kintsugi-agent-remote.service`, resident and
`Restart=always`. It deliberately does **not** take `lock.rs`'s advisory flock: that lock stops two
`apt-get` runs deadlocking on the dpkg lock, this unit installs nothing, and holding it would mean a
remote session blocked patching for as long as somebody was watching.

The second is **two capture and input backends, chosen at session start**, because X11 and Wayland
share nothing here. `backend.rs` picks one; everything downstream — `FrameEncoder`, the tiles,
`remote_ipc`, the server, the viewer — is identical either way.


**A Wayland host may be watchable and not drivable, and the viewer has to say so.** Capture and input
are one portal session — `NotifyPointerMotionAbsolute` names the stream it is positioning within, and
the portal only accepts a stream from the same session — but `RemoteDesktop` is optional and
**wlroots does not implement it**, so Sway, Hyprland and river hosts get ScreenCast alone. The
negotiation falls back to capture-only and reports `canControlInput: false`, which is what that flag
on the wire exists for. Without it an operator sees a live picture that ignores the mouse and
concludes the session is broken. Do not "fix" this by writing to `/dev/uinput` as root: that bypasses
the portal's consent entirely, which is the thing the portal exists to enforce.

**The Wayland check runs before the X11 one, and that ordering is the whole point.** Most Wayland
sessions also run XWayland and *do* set `DISPLAY`, so an X11 connection succeeds — and then the root
window is not the compositor's output, so `GetImage` returns black or a desktop containing only X11
clients. A plausible-looking wrong picture is far worse than an error, so `backend::is_wayland_session`
is asked first and `screen_capture::unavailable_reason` is now X11's alone.


**The host user is asked twice, deliberately.** Once by the agent (`dialogs::confirm_remote_control`,
which names the administrator) and once by the portal (which names only the application). Neither can
go: the agent's is the only one that can say *who* is asking, and the portal's is the compositor's own
security boundary. The portal's uses `PersistMode::ExplicitlyRevoked` with a stored restore token so
it is asked once per host rather than once per session; the agent's is asked every time and must
never be persisted.


**On Linux a host that cannot be controlled never connects at all.** The per-user process checks
`unavailable_reason` once and, if there is one, never opens the local socket — so the root unit never
opens its control socket and the server reports the host unreachable. That is the same mechanism as
"nobody is logged in", which means there is exactly one way for a host to be unavailable rather than
a session that starts and shows nothing.


**Three Linux-only costs, all named where they are paid.** `GetImage` transfers the whole root window
every frame (~8 MB at 1920x1080) and it is downscaled in software, which is why the frame rate is 8
rather than macOS's 15 — MIT-SHM would avoid the transfer and is deliberately not used, for one code
path rather than two. The consent dialog is zenity or kdialog, and **a host with neither cannot be
remote controlled at all**: no dialog program means consent cannot be asked for, which is the
opposite of what `confirm_patch` does with the same situation, where it proceeds rather than nags.
And kdialog has no way to make No the default button, so its labels are *reversed* — Deny is the Yes
button — which keeps Return and every unexpected exit status on the refusing side.


**A self-update had to learn about the new unit, and the gap it closes would have been invisible.**
`install_binary` replaces only the binary, so a host self-updating from a release that predates
remote control would get the new agent and no unit file to run it under — reporting as unreachable
forever with nothing to explain why, until somebody re-ran `install.sh` on every host.
`self_update::restart_remote_control_unit` therefore installs the unit **if and only if** the path
does not exist, and restarts it otherwise: it is also the one root unit a self-update must restart,
being the only long-running one, or it would go on executing the previous binary until reboot.
Writing units only when absent is what keeps it from ever clobbering a file an administrator edited.


## Couplings nothing enforces

- `x11rb`'s `randr` feature is what `describe_displays` needs for `GetMonitors`. It is pure Rust like
  the rest of x11rb, so it costs the Linux agent's no-C-library invariant nothing — but any *other*
  crate added for display enumeration would break the statically linked musl release for the whole
  fleet, not just remote control.
- `input_injection::evdev_keycode_for_hid` is the base table and `xtest_keycode_for_hid` is that plus
  `EVDEV_KEYCODE_OFFSET`. XTEST wants the offset form; the portal's `NotifyKeyboardKeycode` wants the
  raw kernel code. Getting it backwards types a key eight positions along the physical keyboard —
  wrong letters on Wayland hosts only, which reads as a broken keymap on the host rather than a bug
  in the agent. A test asserts the two agree for every usage.
- **The Linux agent compiles on macOS, and that is worth not breaking.** It is a Linux program, but
  `cargo build` on a Mac is the fastest way to check a change before waiting on a container — and the
  one place remote control needed a Linux-only facility (`libc::ucred`/`SO_PEERCRED`, for the peer
  check on the local socket) is `#[cfg(target_os = "linux")]` with a stub behind it purely for that
  reason. The stub can never run: only the root unit calls it, and there is no root unit off Linux.
- The Linux agent must keep linking no C library. `x11rb` is pure Rust (its whole tree is
  `rustix`/`linux-raw-sys`) and that is why it was chosen over the `x11`/`libxcb` bindings; the
  check that matters is CI's own, that the musl artifact is not dynamically linked. Adding anything
  that pulls a `-sys` crate here breaks the release for the whole fleet, not just remote control.
