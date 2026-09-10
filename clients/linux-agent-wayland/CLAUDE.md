# The Linux agent's Wayland backend

Loaded when Claude reads files under `clients/linux-agent-wayland/`. This is a separate crate
because it links `libpipewire`, which is the one C library anything in this fleet links — see the
libc-floor coupling at the bottom. The agent that starts it is `clients/linux-agent/`.

## Verifying it actually captures

**Verifying the Wayland backend actually captures.** `cargo test` checks the framing, the pod and
the slot semantics; none of that says whether PipeWire will hand over a frame, and every way of
getting that wrong fails as a stream that connects and delivers nothing. The negotiation needs no
compositor — any PipeWire producer exercises the same code — so the cheap decisive test is:

```bash
# in a container with pipewire, wireplumber, gstreamer1.0-pipewire and libpipewire-0.3-dev
pipewire & wireplumber & sleep 2
# mode=provide is load-bearing: in its default mode pipewiresink looks for somewhere to render and
# publishes nothing, so there is no node to target.
gst-launch-1.0 -q videotestsrc pattern=smpte is-live=true \
    ! video/x-raw,format=BGRx,width=640,height=480,framerate=30/1 ! pipewiresink mode=provide &
sleep 3
NODE=$(pw-dump | ... Stream/Output/Video ...)
cargo run --example capture-node -- "$NODE" > frames.bin   # then decode the framing
```

A real compositor is only needed to exercise the *portal*, which is a separate question and worth
standing up once: `debian:trixie-slim` with `sway pipewire wireplumber xdg-desktop-portal
xdg-desktop-portal-wlr`, `WLR_BACKENDS=headless WLR_RENDERER=pixman`, `XDG_CURRENT_DESKTOP=sway`
(without which the frontend matches no backend and every request fails with no detail), and
`~/.config/xdg-desktop-portal-wlr/config` naming an `output_name` before the portal starts — it
otherwise looks for slurp or wofi to ask a human. That environment confirms the ScreenCast/no-
RemoteDesktop split wlroots really has, which is the view-only path. It does *not* deliver frames:
sway's headless output has no DRM device, so the GLES2 renderer will not initialise and pixman's
screencopy never offers the portal a format. `grim` working there while the portal does not is how to
tell that apart from a bug in this code.


## The design, and why it is a separate binary

**Wayland needs a fourth binary, and that is the whole design.** Capture goes through
`xdg-desktop-portal`'s ScreenCast interface, which hands back a PipeWire node — and `libpipewire` is
a C library. The agent links none, which is the only reason CI ships a statically linked musl binary
with no libc floor at all; linking PipeWire would reintroduce that floor for the whole fleet in order
to add remote control on part of it. So the PipeWire half lives in `clients/linux-agent-wayland`,
a separate binary shipped in the same archive and started only for the duration of a session. It
holds no identity, makes no network call and knows nothing about consent — it captures pixels and
injects input, and every security decision stays in the process holding the fleet private key.

**It is started by the per-user process, not the root service, and that is forced.** The portal is
per-user in every respect that matters: `WAYLAND_DISPLAY`, `XDG_RUNTIME_DIR` and
`DBUS_SESSION_BUS_ADDRESS` all name the logged-in user's session, and the portal keys its permission
store by uid. A helper launched by the root service would be asking *root's* compositor for *root's*
grant, and there is not one. `kintsugi-agent-ui.service` already has all three variables from
systemd, so it inherits them by not clearing them — and capture already lived there, so nothing about
the architecture moves.

Raw frames are large (8 MB at 1920x1080) and cross exactly one boundary, the helper's stdout;
encoding stays in the per-user process, so what goes over `remote_ipc` is the same few tens of
kilobytes of JPEG tiles the X11 path sends.


**Wayland's display list is the outputs the host's user agreed to share, not the monitors
attached** — a different thing from the other three, and it cannot be widened from this side. The
helper asks `SelectSources` for `multiple(true)`, the portal's own picker decides what comes back,
and `describe_streams` reports whatever it granted. Three consequences. A two-monitor Wayland host
that shared one output honestly offers one entry, and the viewer shows no picker. **A host that
granted a session before this change keeps returning one output**, because the stored restore token
remembers that single-output grant until somebody revokes the permission in their desktop settings —
that is the portal remembering an answer, not the call being ignored, and it will read exactly like
the compositor limitation the persist-mode bug above produced. And the switch is **asynchronous**:
the helper tears down its PipeWire stream and negotiates another, so frames keep arriving from the
previous display for a moment. `FormatMessage` therefore carries the `node_id`, the agent's
`active_display_id` follows the *frames* rather than the request, and `remote_session` announces the
geometry by comparing it against what it last sent — which is also, incidentally, the first thing
that handles a monitor mode change or a hotplug mid-session on that backend.

**The switch in the Wayland helper is a fresh stream, and the shape it takes is forced.** A PipeWire
stream may only be touched from the thread driving its loop, and that thread is inside
`mainloop.run()` for the whole session — so the request arrives over PipeWire's own channel (whose
callback runs *on* that thread), records the wanted node and quits the loop, and `capture::run` loops
round to connect a new stream. Two things then fall out for free that would otherwise have needed
deliberate work: `KIND_FORMAT` is re-sent before the first frame of the new display, because each
pass has its own writer thread and so its own "have I sent a format yet", and the old stream is fully
torn down before the new one links. `examples/capture-node.rs` is still how to exercise any of this
without a compositor — two `pipewiresink` producers and a switch between their node ids.


## The portal: persistence, and three PipeWire mistakes that fail silently

**That persistence goes on whichever portal created the session, and nowhere else — and getting it
wrong reads as a compositor limitation, not a bug.** On a `RemoteDesktop` session the persist mode
and restore token belong on `SelectDevices`; the `ScreenCast.SelectSources` call made against that
same session must carry neither, because xdg-desktop-portal refuses it outright
(`desktop-portal/screen-cast.c`: `IS_REMOTE_DESKTOP_SESSION` → "Remote desktop sessions cannot
persist"). The helper's first cut sent both on every `SelectSources`, and nothing errored where anyone
looked: the refusal was caught by the view-only fallback, logged as "this compositor's portal does
not offer usable RemoteDesktop", and reported to the viewer as `canControlInput: false` — so every
Wayland host, GNOME and KDE included, showed the "watched but not controlled" notice that is meant
for wlroots. Two things keep it from coming back. `portal::select_sources` takes the persistence
explicitly and the `RemoteDesktop` path passes `None`; and the fallback's log line now states which
call failed and how, rather than asserting a cause. The tokens are two files
(`portal-restore-token-remote-desktop`, `portal-restore-token-screen-cast`) because the portal keeps
them in separate permission tables — one file would lose the remote-desktop grant whenever a session
fell back for a transient reason. When a GNOME or KDE host reports view-only, the journal line above
the agent's "started" entry is the diagnosis; do not start with the compositor.


**Three PipeWire mistakes that each fail silently, all found by running it rather than reading it.**
`clients/linux-agent-wayland/examples/capture-node.rs` streams any PipeWire node with the real
capture module, so the negotiation can be exercised against `gst-launch-1.0 videotestsrc ...
pipewiresink mode=provide` with no compositor involved. It is an example rather than a flag on the
binary because a `--node-id` switch would be a way to point the shipped helper at a stream the portal
never granted. What it caught:

- **Without `StreamFlags::AUTOCONNECT` no link is ever created.** With a target node id it means
  "link to *that* node", not "pick something"; without it the stream sits in `Paused` forever and
  nothing errors. It is the *session manager* that acts on it, so a host with no wireplumber cannot
  capture either.
- **Capping the framerate in the format request breaks negotiation outright.** Asking for a range of
  0/1 to 8/1 reads as the tidy way to want fewer frames, and has no intersection with a producer
  publishing at a fixed 30/1 — the result is `Error("no more input formats")` and a session with no
  picture. Advertise the widest rate that could arrive and drop frames in the process callback, which
  is what `MAX_FRAMES_PER_SECOND` now does. Note there are then **two** rate gates in series and only
  one is authoritative: `DEFAULT_MAX_FPS` in the agent decides the session's rate on both backends,
  and the helper's cap is a bandwidth ceiling deliberately set *above* it. Setting the two equal is
  worse than either — two free-running 8 Hz gates beat against each other and the session runs below
  8 with jitter.
- **`PW_KEY_TARGET_OBJECT` is not where a portal node id goes.** It matches an object *name* or an
  `object.serial`; the portal hands out a global node id, and setting the property to one gives
  `Error("no target node available")`. The deprecated `target_id` argument to `pw_stream_connect` is
  the only thing that takes it.

Two more that are quieter still: the format pod deliberately advertises **no**
`SPA_FORMAT_VIDEO_modifier`, because advertising one lets the compositor hand back DMA-BUF, which
`MAP_BUFFERS` does not map and whose frames would all be silently dropped. And the helper normalises
RGBx/RGBA to BGRA itself rather than putting the pixel format on the wire, because the wire promises
BGRA and a consumer getting that decision wrong produces a sharp picture with the reds and blues
exchanged — which reads as a display-profile problem rather than a byte-order one.


## Couplings nothing enforces

- **`kintsugi-agent-wayland` is the one binary in this fleet with a libc floor**, and it is the
  price of Wayland support. It links `libpipewire`, so it cannot be the static musl build the agent
  is. CI builds it in a **debian:12** container and asserts the result needs no symbol newer than
  `GLIBC_2.34` (Ubuntu 22.04, RHEL 9, Debian 12, Fedora 35 and up). Going older does not work:
  Ubuntu 22.04's libpipewire is 0.3.48 and `libspa` 0.10 does not compile against those headers at
  all, so **libpipewire 0.3.65 is the floor the crate imposes** and Debian 12 is the oldest
  widely-deployed distribution that has it. A host below either floor is not a broken agent — the
  backend fails to start, the agent reports Wayland capture unavailable with the reason in the
  journal, and X11 hosts, patching and inventory are untouched. Keep that degradation graceful; the
  failure it replaces is a session that connects and never paints.
- The helper's stdio protocol (`clients/linux-agent-wayland/src/wire.rs` and the agent's
  `wayland_backend.rs`) is another hand-mirrored pair, like the Rust structs against the C# DTOs. It
  needs no version negotiation, and only because the two are shipped in one archive and replaced
  together by `self_update` — do not give the helper a separate release cadence without adding one.
