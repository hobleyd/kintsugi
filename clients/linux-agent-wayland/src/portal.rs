//! Negotiating an xdg-desktop-portal session, and injecting input through it.
//!
//! # One session, not two, and the order is forced
//!
//! Capture and input are two portal interfaces — `ScreenCast` and `RemoteDesktop` — but they must
//! share **one** session, because `NotifyPointerMotionAbsolute` names the stream it is positioning
//! within: a coordinate is meaningless without knowing which monitor it is on, and the portal will
//! only accept a stream belonging to the same session. So the session is created on `RemoteDesktop`
//! and `ScreenCast::select_sources` is then called against it. `ashpd` models exactly that with
//! `IsScreencastSession`, which `RemoteDesktop` implements.
//!
//! The call order is the portal's, not a preference: `SelectDevices` and `SelectSources` both have
//! to happen before `Start`, and `Start` is what raises the permission dialog. `OpenPipeWireRemote`
//! only works afterwards.
//!
//! # And the fallback, which is the case that made this whole flag necessary
//!
//! `RemoteDesktop` is optional and **wlroots does not implement it** — `xdg-desktop-portal-wlr`
//! offers `ScreenCast` alone, so Sway, Hyprland and river hosts can be watched and not driven.
//! That is not a bug to work around: there is no other route to input on those compositors that
//! does not mean writing to `/dev/uinput` as root, which would bypass the portal's consent entirely
//! and is exactly the kind of thing the portal exists to prevent. So a missing `RemoteDesktop` is
//! reported as a view-only session and the viewer says so — see `RemoteDisplayGeometry`'s
//! `canControlInput` in `web/lib/domain/entities/remote_control_session.dart`.
//!
//! Detection is by trying, not by asking. The interface can be present on the bus and still refuse
//! `SelectDevices`, and a portal backend can be installed but not running; attempting the whole
//! negotiation and falling back on failure is the only test that reflects what will actually happen.
//!
//! # Two consent dialogs, and why that is right
//!
//! The host user is asked twice for one session: once by the agent (`dialogs::confirm_remote_control`,
//! which names the administrator) and once by the portal (which names nothing but the application).
//! Suppressing either was considered and rejected. The agent's dialog is the only one that can say
//! *who* is asking, which is the thing a user needs in order to answer; the portal's is the
//! compositor's own security boundary and is not ours to remove. The portal's is asked second and
//! only after the user has already agreed, so it reads as a confirmation rather than a surprise.
//!
//! `PersistMode::ExplicitlyRevoked` plus a restore token means the *portal's* half is asked once per
//! host rather than once per session, which is what keeps that from being a nuisance. The agent's is
//! asked every time, always, and must never be persisted — see the comment on `restore_token_path`.
//!
//! **Persistence belongs to whichever portal created the session, and only that one.** On a
//! `RemoteDesktop` session the persist mode and restore token go on `SelectDevices`; the
//! `SelectSources` call made against that same session must carry *neither*, because
//! xdg-desktop-portal refuses a `ScreenCast.SelectSources` that names either on a remote-desktop
//! session (`desktop-portal/screen-cast.c`, "Remote desktop sessions cannot persist"). The first cut
//! sent both on every `SelectSources`, and the result was not an error anyone saw: the refusal was
//! caught by the view-only fallback below, logged as the compositor lacking `RemoteDesktop`, and every
//! Wayland host — GNOME and KDE included — reported itself as watchable and not drivable. The two
//! paths therefore keep separate tokens as well (`RestoreTokenFor`): the portal files them in
//! separate permission tables, so a token from one presented to the other is silently dropped, and
//! one file holding whichever path ran last would lose the remote-desktop grant the moment a session
//! fell back for any transient reason.

use std::os::fd::OwnedFd;
use std::path::PathBuf;

use anyhow::{anyhow, Context, Result};
use ashpd::desktop::remote_desktop::{Axis, DeviceType, KeyState, RemoteDesktop, SelectDevicesOptions};
use ashpd::desktop::screencast::{
    CursorMode, IsScreencastSession, Screencast, SelectSourcesOptions, SourceType,
};
use ashpd::desktop::{PersistMode, Session, SessionPortal};
use ashpd::enumflags2::BitFlags;

use crate::wire::{DisplayEntry, DisplaysMessage, InputMessage, PointerAction};

/// A live portal session: the compositor's grant, held open.
///
/// Dropping this closes the session and the PipeWire stream with it, so the caller keeps it alive
/// for as long as the capture runs. That is also why the two shapes below are an enum rather than an
/// `Option` beside a single session field — the session is *typed* by the portal that created it,
/// and that type is what stops the input calls being attempted on a capture-only session. There is
/// no cast between them that would be anything but a lie.
enum Grant {
    /// `RemoteDesktop` created the session, so both capture and input are available.
    Controllable { remote_desktop: RemoteDesktop, session: Session<RemoteDesktop> },

    /// `ScreenCast` created it. Watchable and not drivable — the wlroots case.
    ViewOnly {
        #[allow(dead_code, reason = "held only to keep the portal session open; see the type note")]
        session: Session<Screencast>,
    },
}

/// What the negotiation produced: enough for the caller to open the PipeWire stream, plus the live
/// session that must stay alive for as long as the capture does.
pub struct PortalSession {
    grant: Grant,

    /// The PipeWire node the compositor is publishing frames on, and the one input is positioned
    /// within. Moves when the administrator picks a different display — see [`Self::select_node`].
    pub node_id: u32,

    /// Every output the compositor published for this session, in the order it did.
    ///
    /// **This is the outputs the host's user agreed to share, not the monitors attached.** The
    /// portal's own picker decides; this asks for more than one and reports whatever came back.
    displays: Vec<DisplayEntry>,

    /// The file descriptor for the PipeWire remote, taken by the capture side.
    ///
    /// An `Option` because it is moved out exactly once: `pipewire::Context::connect_fd` consumes
    /// it, and the fd is the one thing here that cannot be duplicated cheaply.
    pipewire_fd: Option<OwnedFd>,
}

impl PortalSession {
    /// Runs the whole negotiation: session, sources, devices, `Start`, then the PipeWire fd.
    ///
    /// Tries with input first and retries capture-only if that fails, which is the wlroots path.
    pub async fn negotiate() -> Result<Self> {
        match Self::negotiate_with_input().await {
            Ok(session) => Ok(session),
            Err(error) => {
                // Logged at info rather than warn: on a wlroots host this is the normal, expected
                // outcome and a warning every session would train people to ignore the log. It
                // states which call failed and how, not a diagnosis — the first cut said "this
                // compositor does not offer RemoteDesktop" here, and that is exactly the wording
                // that let a refused `SelectSources` pass as a compositor limitation for a release.
                eprintln!(
                    "remote control: the RemoteDesktop negotiation failed ({error:#}); continuing \
                     with a view-only session, which is the expected outcome only on a compositor \
                     without RemoteDesktop (wlroots)"
                );
                Self::negotiate_capture_only().await
            }
        }
    }

    /// Whether the portal granted keyboard and pointer as well as capture.
    pub fn can_control_input(&self) -> bool {
        matches!(self.grant, Grant::Controllable { .. })
    }

    /// The outputs this session may show, for the agent to offer as a picker.
    pub fn displays(&self) -> DisplaysMessage {
        DisplaysMessage { displays: self.displays.clone() }
    }

    /// Moves input onto a different granted output. `false` for a node this session was not granted.
    ///
    /// **Checked against the granted list rather than accepted, and that is a security property
    /// rather than tidiness.** The node id arrives from the agent, which got it from a browser; a
    /// node this session was never granted is either a stale id or an attempt to position a pointer
    /// inside somebody else's stream, and the portal would refuse it anyway — refusing here means
    /// the refusal is stated in this process's own log instead of appearing as input that silently
    /// stopped working.
    pub fn select_node(&mut self, node_id: u32) -> bool {
        if !self.displays.iter().any(|display| display.node_id == node_id) {
            return false;
        }

        self.node_id = node_id;
        true
    }

    /// Hands the PipeWire remote's file descriptor to the capture side. Only available once.
    pub fn take_pipewire_fd(&mut self) -> Result<OwnedFd> {
        self.pipewire_fd
            .take()
            .ok_or_else(|| anyhow!("the PipeWire remote descriptor has already been taken"))
    }

    async fn negotiate_with_input() -> Result<Self> {
        let remote_desktop =
            RemoteDesktop::new().await.context("connecting to the RemoteDesktop portal")?;
        let screencast = Screencast::new().await.context("connecting to the ScreenCast portal")?;

        let session =
            remote_desktop.create_session(Default::default()).await.context("creating a portal session")?;

        // Devices before sources, and both before Start — the portal's own ordering. Persistence is
        // requested here and here only; see the module note on why `select_sources` must not.
        let devices = remote_desktop
            .select_devices(
                &session,
                SelectDevicesOptions::default()
                    .set_devices(DeviceType::Keyboard | DeviceType::Pointer)
                    .set_persist_mode(PERSIST_MODE)
                    .set_restore_token(stored_restore_token(RestoreTokenFor::RemoteDesktop).as_deref()),
            )
            .await
            .context("asking the portal for keyboard and pointer")?;
        devices.response().context("the portal refused keyboard and pointer")?;

        select_sources(&screencast, &session, None).await?;

        let started = remote_desktop
            .start(&session, None, Default::default())
            .await
            .context("starting the portal session")?
            .response()
            .context("the user or the compositor refused the portal's own permission request")?;

        // An empty stream list means capture was not granted even though the call succeeded, which
        // a backend granting input alone would produce. There is nothing to show, so it is an error
        // rather than a view-only session.
        let displays = describe_streams(started.streams());
        let node_id = displays
            .first()
            .ok_or_else(|| anyhow!("the portal granted input but published no screen to capture"))?
            .node_id;

        // Only claim input if the portal actually granted *both* devices. A backend handing back
        // pointer alone would otherwise present a keyboard that silently does nothing — better to
        // fall through to the view-only path, which the viewer explains.
        let granted = started.devices();
        if !(granted.contains(DeviceType::Keyboard) && granted.contains(DeviceType::Pointer)) {
            return Err(anyhow!(
                "the portal granted {granted:?} rather than both keyboard and pointer"
            ));
        }

        if let Some(token) = started.restore_token() {
            store_restore_token(RestoreTokenFor::RemoteDesktop, token);
        }

        let pipewire_fd = screencast
            .open_pipe_wire_remote(&session, Default::default())
            .await
            .context("opening the PipeWire remote")?;

        Ok(Self {
            grant: Grant::Controllable { remote_desktop, session },
            node_id,
            displays,
            pipewire_fd: Some(pipewire_fd),
        })
    }

    async fn negotiate_capture_only() -> Result<Self> {
        let screencast = Screencast::new().await.context("connecting to the ScreenCast portal")?;

        let session = screencast
            .create_session(Default::default())
            .await
            .context("creating a screen-cast session")?;

        select_sources(&screencast, &session, Some(RestoreTokenFor::ScreenCast)).await?;

        let started = screencast
            .start(&session, None, Default::default())
            .await
            .context("starting the screen-cast session")?
            .response()
            .context("the user or the compositor refused the portal's own permission request")?;

        let displays = describe_streams(started.streams());
        let node_id = displays
            .first()
            .ok_or_else(|| anyhow!("the portal published no screen to capture"))?
            .node_id;

        if let Some(token) = started.restore_token() {
            store_restore_token(RestoreTokenFor::ScreenCast, token);
        }

        let pipewire_fd = screencast
            .open_pipe_wire_remote(&session, Default::default())
            .await
            .context("opening the PipeWire remote")?;

        Ok(Self { grant: Grant::ViewOnly { session }, node_id, displays, pipewire_fd: Some(pipewire_fd) })
    }

    /// Injects one input event. A no-op on a view-only session.
    pub async fn inject(&self, input: &InputMessage) -> Result<()> {
        let Grant::Controllable { remote_desktop, session } = &self.grant else {
            // A view-only session. Dropped silently rather than reported: the viewer already knows
            // it cannot drive this host and sends nothing, so anything arriving here is a race with
            // a session that has just been reported view-only, not a fault worth logging per event.
            return Ok(());
        };

        match input {
            // Not an input event: it is handled in `main`'s portal loop, which calls `select_node`
            // before this is ever reached. Matched explicitly rather than with a wildcard so that a
            // *new* message added to the wire cannot be silently swallowed here — the compiler is
            // the only thing that would notice, and this is the arm that would hide it.
            InputMessage::SelectDisplay { .. } => {}
            InputMessage::Pointer { x, y, action, button } => match action {
                // Absolute rather than relative, because the viewer knows where the pointer should
                // be and the agent has no way to read where it currently is — a relative stream
                // accumulates error and drifts out of alignment with the picture.
                PointerAction::Move => {
                    remote_desktop
                        .notify_pointer_motion_absolute(
                            session,
                            self.node_id,
                            *x,
                            *y,
                            Default::default(),
                        )
                        .await?;
                }
                PointerAction::Down | PointerAction::Up => {
                    // Moved first, then pressed. The portal has no "click at" call, and a press at
                    // wherever the pointer happened to be is the classic remote-control misclick.
                    remote_desktop
                        .notify_pointer_motion_absolute(
                            session,
                            self.node_id,
                            *x,
                            *y,
                            Default::default(),
                        )
                        .await?;
                    remote_desktop
                        .notify_pointer_button(
                            session,
                            button.evdev_code(),
                            if matches!(action, PointerAction::Down) {
                                KeyState::Pressed
                            } else {
                                KeyState::Released
                            },
                            Default::default(),
                        )
                        .await?;
                }
            },
            InputMessage::Key { evdev, down } => {
                remote_desktop
                    .notify_keyboard_keycode(
                        session,
                        *evdev,
                        if *down { KeyState::Pressed } else { KeyState::Released },
                        Default::default(),
                    )
                    .await?;
            }
            InputMessage::Scroll { steps_x, steps_y } => {
                // Discrete steps rather than the continuous axis, because that is what a wheel is
                // and what applications expect from one; the continuous form is for touchpads and
                // scrolls at a completely different rate.
                if *steps_y != 0 {
                    remote_desktop
                        .notify_pointer_axis_discrete(
                            session,
                            Axis::Vertical,
                            *steps_y,
                            Default::default(),
                        )
                        .await?;
                }
                if *steps_x != 0 {
                    remote_desktop
                        .notify_pointer_axis_discrete(
                            session,
                            Axis::Horizontal,
                            *steps_x,
                            Default::default(),
                        )
                        .await?;
                }
            }
        }

        Ok(())
    }
}

/// Turns the portal's granted streams into the picker the administrator sees.
///
/// Labels come from the portal where it offers one. `Stream::id()` is the output's own identifier
/// — "HDMI-1", "eDP-1" on a portal that publishes it — which is what a person sitting at the host
/// would call their monitors; the fallback numbers them, because two entries that both read
/// "Display" and differ only in a resolution are indistinguishable on the very ordinary desk of a
/// laptop beside an external monitor of the same size.
fn describe_streams(streams: &[ashpd::desktop::screencast::Stream]) -> Vec<DisplayEntry> {
    // Nothing in the ScreenCast interface says which output is the primary one, so position stands
    // in: a compositor puts the output containing the origin first in its own layout. Falling back
    // to the first stream matters — with no primary at all the agent's picker would have no default
    // and `select_display` would have nothing to fall back to.
    let at_origin = streams
        .iter()
        .position(|stream| stream.position() == Some((0, 0)))
        .unwrap_or(0);

    streams
        .iter()
        .enumerate()
        .map(|(index, stream)| {
            let (width, height) = stream.size().unwrap_or((0, 0));
            let width = width.max(0) as u32;
            let height = height.max(0) as u32;

            let name = stream
                .id()
                .map(str::to_string)
                .filter(|id| !id.is_empty())
                .unwrap_or_else(|| format!("Display {}", index + 1));

            DisplayEntry {
                // The node id, not the index: it is what `NotifyPointerMotionAbsolute` takes and
                // what the stream is linked by, so using anything else here would mean a mapping
                // table between this list and the two places that consume it.
                node_id: stream.pipe_wire_node_id(),
                label: if width > 0 && height > 0 {
                    format!("{name} ({width} x {height})")
                } else {
                    name
                },
                width,
                height,
                is_primary: index == at_origin,
            }
        })
        .collect()
}

/// `persistence` is `Some` only when `ScreenCast` itself owns the session. On a `RemoteDesktop`
/// session the persist mode and restore token have already gone on `SelectDevices`, and repeating
/// them here is the call the portal rejects outright — see the module note.
async fn select_sources<T>(
    screencast: &Screencast,
    session: &Session<T>,
    persistence: Option<RestoreTokenFor>,
) -> Result<()>
where
    T: IsScreencastSession + SessionPortal,
{
    let options = SelectSourcesOptions::default()
        // Monitor only. A window stream would be a smaller attack surface but is not what
        // remote *control* means — an administrator fixing a machine needs the desktop,
        // including whatever dialog is currently covering it.
        .set_sources(BitFlags::from(SourceType::Monitor))
        // Embedded, so the cursor is drawn into the frames. The agent's X11 path composites
        // it by hand from XFIXES because X11 has no equivalent; here the compositor does it,
        // which is both cheaper and correct for scaled outputs.
        .set_cursor_mode(CursorMode::Embedded)
        // More than one monitor is asked for, and only one is ever *streamed* at a time — the
        // helper links to whichever node the administrator picked and re-links on a switch, so
        // there is never a coordinate space spanning two outputs. Asking is what puts the choice
        // in front of the host's user, who is the only one entitled to make it.
        //
        // Two things follow and neither is a bug to be fixed here. The portal's picker decides what
        // comes back, so a two-monitor host that shared one output offers one entry — see
        // `DisplayEntry`. And a host that already granted a session before this asked for multiple
        // has a *stored restore token* for that single-output grant, so it keeps returning one
        // output until somebody revokes the permission in their desktop's settings; that is the
        // portal remembering an answer, not this call being ignored.
        .set_multiple(true);

    let stored = persistence.and_then(stored_restore_token);
    let options = match persistence {
        Some(_) => options.set_persist_mode(PERSIST_MODE).set_restore_token(stored.as_deref()),
        None => options,
    };

    screencast
        .select_sources(session, options)
        .await
        .context("asking the portal for the screen")?
        .response()
        .context("the portal refused the screen")?;

    Ok(())
}

/// `ExplicitlyRevoked` — the portal remembers until the user withdraws it in their settings.
///
/// The alternative, `Transient`, lasts until the portal restarts and would raise the compositor's
/// own dialog on most sessions. That is a real trade and it is worth being explicit about which way
/// it went: persisting the *portal's* permission means an administrator's session begins without
/// waiting on a second dialog, and consent has not been weakened, because the agent's own dialog is
/// asked every single time and cannot be persisted at all.
const PERSIST_MODE: PersistMode = PersistMode::ExplicitlyRevoked;

/// Which portal a restore token was issued by. The portal keeps the two in separate permission
/// tables (`remote-desktop` and `screencast`), so they are separate files here too — see the module
/// note on why one file would lose the remote-desktop grant.
#[derive(Clone, Copy)]
enum RestoreTokenFor {
    RemoteDesktop,
    ScreenCast,
}

impl RestoreTokenFor {
    fn file_name(self) -> &'static str {
        match self {
            Self::RemoteDesktop => "portal-restore-token-remote-desktop",
            Self::ScreenCast => "portal-restore-token-screen-cast",
        }
    }
}

/// Where the portal's restore token is kept.
///
/// In the per-user state directory rather than the agent's, because it is the *user's* grant to
/// their own compositor and is meaningless to any other account on the host. Note what this file is
/// and is not: it lets the helper reopen a stream the user has already permitted, and it grants
/// nothing on its own — the agent's consent dialog stands in front of every session regardless, so
/// a copied token buys an attacker nothing they could use.
///
/// Hosts from before the split may still hold a `portal-restore-token` beside these. It is never
/// read: it only ever held a screen-cast token (every session then fell back), and presenting a
/// token from the wrong table is a no-op in the portal, so nothing is gained by migrating it.
fn restore_token_path(portal: RestoreTokenFor) -> Option<PathBuf> {
    let base = std::env::var_os("XDG_STATE_HOME")
        .map(PathBuf::from)
        .or_else(|| std::env::var_os("HOME").map(|home| PathBuf::from(home).join(".local/state")))?;

    Some(base.join("kintsugi-agent").join(portal.file_name()))
}

fn stored_restore_token(portal: RestoreTokenFor) -> Option<String> {
    let path = restore_token_path(portal)?;
    let token = std::fs::read_to_string(path).ok()?;
    let token = token.trim().to_string();

    // An empty file would be sent as an empty token, which some backends treat as invalid input and
    // reject the whole call over rather than ignoring.
    (!token.is_empty()).then_some(token)
}

fn store_restore_token(portal: RestoreTokenFor, token: &str) {
    let Some(path) = restore_token_path(portal) else {
        return;
    };

    if let Some(parent) = path.parent() {
        let _ = std::fs::create_dir_all(parent);
    }

    // Failure is not worth reporting: the only consequence is the portal's own dialog appearing next
    // session, and the session currently starting is unaffected.
    let _ = std::fs::write(&path, token);
    let _ = restrict_to_owner(&path);
}

#[cfg(unix)]
fn restrict_to_owner(path: &std::path::Path) -> std::io::Result<()> {
    use std::os::unix::fs::PermissionsExt;

    // 0600. Not a secret in the sense the agent's private key is (see `restore_token_path`), but a
    // token naming this user's grant has no business being world-readable on a shared host.
    std::fs::set_permissions(path, std::fs::Permissions::from_mode(0o600))
}
