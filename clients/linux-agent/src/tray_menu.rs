use std::sync::atomic::{AtomicBool, Ordering};
use std::sync::mpsc::Sender;
use std::sync::{Mutex, OnceLock};

use anyhow::{Context, Result};
use ksni::blocking::{Handle, TrayMethods};
use ksni::menu::StandardItem;
use ksni::{Icon, MenuItem, ToolTip};

use crate::logging;
use crate::status::AgentStatus;

const TRAY_ICON_BYTES: &[u8] = include_bytes!("../assets/tray-icon.png");
const TOOLTIP: &str = "Kintsugi Patching";

/// Sends the scheduler a "Patch Now" signal. Stored in a static rather than captured by the menu
/// closure because `ksni` rebuilds the menu from `&self` on every redraw, and threading a channel
/// through the tray's own state would mean `AgentStatus` updates carrying it around too — the
/// same reasoning that puts the Windows agent's sender in a static beside its window procedure.
static PATCH_NOW_TX: OnceLock<Sender<()>> = OnceLock::new();

/// The live tray, once `run` has managed to register one. `None` on a host with no notification
/// area (see `run`), which every update below then silently skips.
static TRAY: Mutex<Option<Handle<KintsugiTray>>> = Mutex::new(None);

/// Who is currently controlling this host, if anybody.
///
/// **The only thing on screen that says a session is happening, which makes it part of the security
/// model rather than a nicety.** Somebody who allowed a session — or who walks up to a machine where
/// one is running — has to be able to see whose it is and end it without finding an administrator.
static REMOTE_SESSION: Mutex<Option<String>> = Mutex::new(None);

/// Set by "End Remote Session" and cleared by whoever acts on it. A flag rather than a channel for
/// the same reason both other agents use one: the click has to be meaningful whether or not a
/// session is running at that instant, and ksni rebuilds the menu from `&self` so a closure cannot
/// hold one.
static END_REMOTE_SESSION: AtomicBool = AtomicBool::new(false);

/// The two lines of menu text the icon currently shows, plus whether "Patch Now" is selectable —
/// greyed out mid-cycle, the same way both other agents' menu items are, so a second cycle can't
/// be started on top of a running one.
struct KintsugiTray {
    status_line: String,
    progress_line: String,
    patch_now_enabled: bool,
}

impl ksni::Tray for KintsugiTray {
    fn id(&self) -> String {
        "kintsugi-agent".into()
    }

    fn title(&self) -> String {
        TOOLTIP.into()
    }

    fn tool_tip(&self) -> ToolTip {
        ToolTip {
            title: TOOLTIP.into(),
            description: self.status_line.clone(),
            ..Default::default()
        }
    }

    fn icon_pixmap(&self) -> Vec<Icon> {
        icon().into_iter().collect()
    }

    fn menu(&self) -> Vec<MenuItem<Self>> {
        let mut items: Vec<MenuItem<Self>> = vec![
            StandardItem {
                label: self.status_line.clone(),
                enabled: false,
                ..Default::default()
            }
            .into(),
            StandardItem {
                label: self.progress_line.clone(),
                enabled: false,
                ..Default::default()
            }
            .into(),
            MenuItem::Separator,
        ];

        // Directly under the status lines and above "Patch Now": while a session is running it is
        // the most important thing in this menu. Absent entirely otherwise, rather than greyed out —
        // the overwhelming majority of hosts never have one.
        if let Some(requested_by) = REMOTE_SESSION.lock().ok().and_then(|held| held.clone()) {
            items.push(
                StandardItem {
                    label: format!("Remote session: {requested_by}"),
                    enabled: false,
                    ..Default::default()
                }
                .into(),
            );
            items.push(
                StandardItem {
                    label: "End Remote Session".into(),
                    activate: Box::new(|_: &mut Self| {
                        logging::info("\"End Remote Session\" clicked in the notification area");
                        END_REMOTE_SESSION.store(true, Ordering::SeqCst);
                    }),
                    ..Default::default()
                }
                .into(),
            );
            items.push(MenuItem::Separator);
        }

        items.extend([
            StandardItem {
                label: "Patch Now".into(),
                enabled: self.patch_now_enabled,
                activate: Box::new(|_: &mut Self| {
                    logging::info("\"Patch Now\" clicked in the notification area");
                    match PATCH_NOW_TX.get() {
                        Some(sender) => {
                            if let Err(err) = sender.send(()) {
                                logging::error(&format!("could not signal the scheduler thread: {err}"));
                            }
                        }
                        None => logging::error("\"Patch Now\" clicked before the scheduler was wired up"),
                    }
                }),
                ..Default::default()
            }
            .into(),
            MenuItem::Separator,
            // Static for the life of the process — this binary's own version never changes
            // underneath it (a self-update replaces the binary on disk and restarts this process;
            // it doesn't rewrite a running one).
            StandardItem {
                label: format!("Version {}", env!("CARGO_PKG_VERSION")),
                enabled: false,
                ..Default::default()
            }
            .into(),
        ]);

        items
    }
}

/// Registers the notification-area icon and then blocks for the rest of the process's life.
/// `patch_now_tx` is how a click on "Patch Now" reaches the scheduler thread.
///
/// **A failure to register is not a failure of this function.** The macOS and Windows agents can
/// both take a menu bar / notification area for granted; Linux cannot. There may be no session
/// bus, no StatusNotifierItem host (a bare window manager, or GNOME without the AppIndicator
/// extension), or no graphical session at all. In every one of those cases the scheduler thread
/// this function was called alongside is still perfectly able to fetch the policy, notice a cycle
/// is due, ask the user, and patch — so this logs what happened and parks instead of returning an
/// error that would end the process and take the scheduler with it.
///
/// It never returns normally, matching `tray_menu::run` on both other agents: `main` treats this
/// call as the end of the line.
pub fn run(patch_now_tx: Sender<()>) -> Result<()> {
    let _ = PATCH_NOW_TX.set(patch_now_tx);

    let tray = KintsugiTray {
        status_line: "Loading patching status\u{2026}".to_string(),
        progress_line: String::new(),
        patch_now_enabled: true,
    };

    match tray.spawn() {
        Ok(handle) => {
            *TRAY.lock().expect("the tray handle mutex is never held across a panic") = Some(handle);
            logging::info("notification-area icon created");
        }
        Err(err) => {
            logging::warn(&format!(
                "could not show a notification-area icon ({err}) — patching continues normally; \
                 only the icon and its menu are unavailable"
            ));
        }
    }

    // Nothing to pump: `ksni` runs its own D-Bus connection on a thread of its own, and there is
    // no Cocoa/Win32 event loop that has to own this one. Parking keeps the process (and with it
    // the scheduler thread) alive, which is this function's real job.
    loop {
        std::thread::park();
    }
}

/// Pushes a status update to the notification area. Safe to call from any thread — the scheduler
/// thread is the only real caller.
/// Shows or hides the remote-session block in the menu. Safe to call from any thread.
pub fn report_remote_session(requested_by: Option<String>) {
    if let Ok(mut held) = REMOTE_SESSION.lock() {
        *held = requested_by;
    }

    // ksni rebuilds the menu from `&self`, so it has to be told the model changed — unlike the
    // Windows agent, where the menu is constructed fresh on every click and setting the value is
    // the whole of it.
    if let Ok(handle) = TRAY.lock() {
        if let Some(handle) = handle.as_ref() {
            handle.update(|_: &mut KintsugiTray| {});
        }
    }
}

/// Whether the person at the keyboard has asked for the session to end, clearing the request.
pub fn take_end_remote_session_request() -> bool {
    END_REMOTE_SESSION.swap(false, Ordering::SeqCst)
}

pub fn report_status(status: AgentStatus) {
    let (status_line, progress_line, patch_now_enabled) = match &status {
        AgentStatus::Idle { next_due_epoch } => (
            format!("Next patch due: {}", format_due(*next_due_epoch)),
            "Status: idle".to_string(),
            true,
        ),
        AgentStatus::Patching { current, completed, total } => (
            format!("Patching: {current}"),
            if *total > 0 {
                format!("Progress: {}", crate::dialogs::progress_bar(*completed, *total))
            } else {
                "Progress: starting\u{2026}".to_string()
            },
            false,
        ),
    };

    if let Ok(guard) = TRAY.lock() {
        if let Some(handle) = guard.as_ref() {
            handle.update(move |tray: &mut KintsugiTray| {
                tray.status_line = status_line;
                tray.progress_line = progress_line;
                tray.patch_now_enabled = patch_now_enabled;
            });
        }
    }

    // A window, unlike the menu, is visible without the user having to think to go looking for it
    // — opened the moment there's something to show, closed again once idle. Deliberately outside
    // the tray check: a host with no notification area may still have a display, and this is the
    // only progress the user would otherwise see.
    match &status {
        AgentStatus::Idle { .. } => crate::progress_window::hide(),
        AgentStatus::Patching { current, completed, total } => crate::progress_window::show_and_update(current, *completed, *total),
    }
}

/// Decodes the embedded PNG into the ARGB32 layout the StatusNotifierItem specification wants.
/// Decoded once and cached: `ksni` asks for the icon again on every redraw.
fn icon() -> Option<Icon> {
    static ICON: OnceLock<Option<Icon>> = OnceLock::new();

    ICON.get_or_init(|| match load_icon() {
        Ok(icon) => Some(icon),
        Err(err) => {
            // Not fatal: an icon-less item still shows the desktop's own fallback and its menu
            // still works.
            logging::warn(&format!("could not decode the embedded tray icon: {err:#}"));
            None
        }
    })
    .clone()
}

fn load_icon() -> Result<Icon> {
    let decoded = image::load_from_memory_with_format(TRAY_ICON_BYTES, image::ImageFormat::Png)
        .context("failed to decode the embedded tray icon")?
        .into_rgba8();
    let (width, height) = decoded.dimensions();

    let mut data = decoded.into_vec();
    // The `image` crate hands back RGBA; StatusNotifierItem wants ARGB in network byte order, so
    // each pixel's alpha moves from last to first.
    for pixel in data.chunks_exact_mut(4) {
        pixel.rotate_right(1);
    }

    Ok(Icon { width: width as i32, height: height as i32, data })
}

/// Renders `epoch` in the host's own local time (not UTC) — what the person looking at the menu
/// actually keeps their clock in. Shells out to `date` rather than using the `time` crate's own
/// local-offset support, for exactly the reason the macOS agent does: that requires opting out of
/// a soundness guard the crate enables by default in multithreaded programs (which this is),
/// since determining the local UTC offset on Unix isn't safely reentrant.
///
/// The invocation differs from the macOS one — GNU coreutils spells it `date -d @<epoch>` where
/// BSD `date` spells it `date -r <epoch>`.
fn format_due(epoch: u64) -> String {
    std::process::Command::new("date")
        .arg(format!("-d@{epoch}"))
        .arg("+%Y-%m-%d %H:%M %Z")
        .output()
        .ok()
        .filter(|output| output.status.success())
        .map(|output| String::from_utf8_lossy(&output.stdout).trim().to_string())
        .filter(|text| !text.is_empty())
        .unwrap_or_else(|| epoch.to_string())
}

#[cfg(test)]
mod tests {
    use super::*;

    /// The icon ships in the binary, so a corrupt or wrongly-formatted asset is a build-time
    /// mistake this catches rather than something a user discovers.
    #[test]
    fn the_embedded_icon_decodes_to_argb32() {
        let icon = load_icon().expect("the embedded tray icon should decode");

        assert!(icon.width > 0 && icon.height > 0);
        assert_eq!(icon.data.len(), (icon.width * icon.height * 4) as usize);
    }

    #[test]
    fn format_due_falls_back_to_the_raw_epoch_rather_than_showing_nothing() {
        // Whatever `date` does with it, the result is never empty — the menu always says
        // something.
        assert!(!format_due(1_756_512_000).is_empty());
    }
}
