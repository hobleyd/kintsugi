use std::cell::RefCell;
use std::sync::atomic::{AtomicBool, Ordering};
use std::sync::mpsc::Sender;
use std::sync::Arc;

use anyhow::{anyhow, Context, Result};
use objc2::MainThreadMarker;
use objc2_app_kit::{NSApplication, NSApplicationActivationPolicy};
use tray_icon::menu::{Menu, MenuEvent, MenuItem, PredefinedMenuItem};
use tray_icon::{Icon, TrayIcon, TrayIconBuilder};

use crate::logging;
use crate::status::AgentStatus;

const MENU_BAR_ICON_BYTES: &[u8] = include_bytes!("../assets/menu-bar-icon.png");
const PATCH_NOW_ID: &str = "patch-now";
const END_REMOTE_SESSION_ID: &str = "end-remote-session";
const TOOLTIP: &str = "Kintsugi Patching";

struct MenuState {
    // Kept alive for as long as the app runs — dropping it removes the icon from the menu bar.
    _tray_icon: TrayIcon,
    status_item: MenuItem,
    progress_item: MenuItem,
    patch_now_item: MenuItem,

    // The remote control block. Held rather than rebuilt because these three are inserted into and
    // removed from the menu as sessions come and go — a permanent "Remote session: none" line would
    // be clutter on the overwhelming majority of hosts, which never have one.
    menu: Menu,
    remote_session_item: MenuItem,
    end_remote_session_item: MenuItem,
    remote_separator: PredefinedMenuItem,
    remote_block_shown: bool,
}

thread_local! {
    // Only ever touched from the main thread: populated once by `run` before the app loop
    // starts, and read inside a block that's already running on the main thread by the time it
    // executes (dispatched there via `report_status`, even though `report_status` itself is
    // normally *called* from the background scheduler thread — see its doc comment). `MenuItem`
    // and `TrayIcon` are `Rc`-based and so aren't `Send`; a thread-local sidesteps that rather
    // than fighting it, since nothing here actually needs to cross threads.
    static MENU_STATE: RefCell<Option<MenuState>> = RefCell::new(None);
}

/// Sets up the menu bar icon and runs AppKit's main event loop for the rest of the process's
/// life — must be called on the main thread (a hard Cocoa requirement for any UI, including a
/// status item) and never returns normally. `patch_now_tx` is how a click on "Patch Now" reaches
/// the scheduler thread.
pub fn run(patch_now_tx: Sender<()>, end_remote_session: Arc<AtomicBool>) -> Result<()> {
    let mtm = MainThreadMarker::new().context("the menu bar can only be set up on the main thread")?;

    let app = NSApplication::sharedApplication(mtm);
    // A pure menu-bar utility: no Dock icon, no Cmd-Tab entry, never steals focus on launch.
    app.setActivationPolicy(NSApplicationActivationPolicy::Accessory);

    let built = build_tray_icon()?;
    MENU_STATE.with(|cell| {
        *cell.borrow_mut() = Some(built);
    });
    logging::info("menu bar icon created");

    MenuEvent::set_event_handler(Some(move |event: MenuEvent| {
        match event.id.as_ref() {
            PATCH_NOW_ID => {
                logging::info("\"Patch Now\" clicked in the menu bar");
                if let Err(err) = patch_now_tx.send(()) {
                    logging::error(&format!("could not signal the scheduler thread: {err}"));
                }
            }
            END_REMOTE_SESSION_ID => {
                // A flag rather than a channel, because the session loop is polling anyway (it has
                // to, to interleave reading input with sending frames) and because this must work
                // whether or not a session is currently running — a click that arrives as one is
                // already ending has nowhere to be delivered.
                logging::info("\"End Remote Session\" clicked in the menu bar");
                end_remote_session.store(true, Ordering::SeqCst);
            }
            _ => {}
        }
    }));

    // Blocks for the rest of the process's life, running the standard Cocoa event loop — this is
    // what actually makes the status item clickable/interactive, not just visible.
    app.run();

    Ok(())
}

/// Pushes a status update to the menu bar. Safe to call from any thread — the scheduler thread is
/// the only real caller — since the actual UI update is marshaled onto the main thread via GCD
/// (`dispatch_async` to the main queue), because AppKit objects like these menu items may only be
/// touched from there.
pub fn report_status(status: AgentStatus) {
    dispatch::Queue::main().exec_async(move || {
        MENU_STATE.with(|cell| {
            let borrowed = cell.borrow();
            let Some(state) = borrowed.as_ref() else { return };

            match &status {
                AgentStatus::Idle { next_due_epoch } => {
                    state.status_item.set_text(format!("Next patch due: {}", format_due(*next_due_epoch)));
                    state.progress_item.set_text("Status: idle");
                    state.patch_now_item.set_enabled(true);
                }
                AgentStatus::Patching { current, completed, total } => {
                    state.status_item.set_text(format!("Patching: {current}"));
                    state.progress_item.set_text(if *total > 0 {
                        format!("Progress: {}", crate::dialogs::progress_bar(*completed, *total))
                    } else {
                        "Progress: starting\u{2026}".to_string()
                    });
                    state.patch_now_item.set_enabled(false);
                }
            }
        });

        // A window, unlike the menu, is visible without the user having to think to go looking
        // for it — opened the moment there's something to show, closed again once idle.
        let Some(mtm) = MainThreadMarker::new() else { return };
        match &status {
            AgentStatus::Idle { .. } => crate::progress_window::hide(),
            AgentStatus::Patching { current, completed, total } => {
                crate::progress_window::show_and_update(mtm, current, *completed, *total)
            }
        }
    });
}

/// Shows or hides the remote control block in the menu.
///
/// **This is the only thing on screen that says a session is happening, and that makes it part of
/// the security model rather than a nicety.** macOS shows its own screen-recording indicator in the
/// menu bar, but that says something is being captured, not who by and not how to stop it. Somebody
/// who allowed a session — or who walked up to a Mac where one is running — needs to be able to see
/// whose it is and end it without finding an administrator.
///
/// Safe to call from any thread, like `report_status`, and for the same reason: the actual menu
/// mutation is marshaled onto the main thread, because AppKit menu objects may only be touched
/// there.
pub fn report_remote_session(requested_by: Option<String>) {
    dispatch::Queue::main().exec_async(move || {
        MENU_STATE.with(|cell| {
            let mut borrowed = cell.borrow_mut();
            let Some(state) = borrowed.as_mut() else { return };

            match &requested_by {
                Some(requested_by) => {
                    state.remote_session_item.set_text(format!("Remote session: {requested_by}"));

                    if !state.remote_block_shown {
                        // Directly under the status lines, above "Patch Now": the most important
                        // thing in this menu while it is there.
                        let inserted = state
                            .menu
                            .insert(&state.remote_separator, 2)
                            .and_then(|()| state.menu.insert(&state.remote_session_item, 3))
                            .and_then(|()| state.menu.insert(&state.end_remote_session_item, 4));

                        match inserted {
                            Ok(()) => state.remote_block_shown = true,
                            // Reported rather than ignored: if this fails, a session is running with
                            // nothing in the menu bar to end it, which is exactly the state this
                            // block exists to prevent.
                            Err(err) => logging::error(&format!(
                                "could not show the remote session controls in the menu bar: {err}"
                            )),
                        }
                    }
                }

                None => {
                    if state.remote_block_shown {
                        let _ = state.menu.remove(&state.end_remote_session_item);
                        let _ = state.menu.remove(&state.remote_session_item);
                        let _ = state.menu.remove(&state.remote_separator);
                        state.remote_block_shown = false;
                    }
                }
            }
        });
    });
}

fn build_tray_icon() -> Result<MenuState> {
    let icon = load_icon()?;

    let status_item = MenuItem::with_id("status", "Loading patching status\u{2026}", false, None);
    let progress_item = MenuItem::with_id("progress", "", false, None);
    let patch_now_item = MenuItem::with_id(PATCH_NOW_ID, "Patch Now", true, None);
    // Static for the life of the process — this binary's own version never changes underneath it
    // (a self-update replaces the binary on disk and restarts this process; it doesn't rewrite a
    // running one) — so, unlike status_item/progress_item, there's nothing to keep a handle to
    // for later updates.
    let version_item = MenuItem::new(format!("Version {}", env!("CARGO_PKG_VERSION")), false, None);

    // Built now but not appended: `report_remote_session` inserts them only while a session is
    // running. Held here so the same objects are reused rather than recreated per session, which is
    // what lets `Menu::remove` find them again.
    let remote_session_item = MenuItem::with_id("remote-session", "Remote session", false, None);
    let end_remote_session_item = MenuItem::with_id(END_REMOTE_SESSION_ID, "End Remote Session", true, None);
    let remote_separator = PredefinedMenuItem::separator();

    let menu = Menu::new();
    menu.append(&status_item).map_err(|e| anyhow!("{e}"))?;
    menu.append(&progress_item).map_err(|e| anyhow!("{e}"))?;
    menu.append(&PredefinedMenuItem::separator()).map_err(|e| anyhow!("{e}"))?;
    menu.append(&patch_now_item).map_err(|e| anyhow!("{e}"))?;
    menu.append(&PredefinedMenuItem::separator()).map_err(|e| anyhow!("{e}"))?;
    menu.append(&version_item).map_err(|e| anyhow!("{e}"))?;

    let tray_icon = TrayIconBuilder::new()
        .with_menu(Box::new(menu.clone()))
        .with_icon(icon)
        .with_icon_as_template(true)
        .with_tooltip(TOOLTIP)
        .build()
        .map_err(|e| anyhow!("{e}"))?;

    Ok(MenuState {
        _tray_icon: tray_icon,
        status_item,
        progress_item,
        patch_now_item,
        menu,
        remote_session_item,
        end_remote_session_item,
        remote_separator,
        remote_block_shown: false,
    })
}

fn load_icon() -> Result<Icon> {
    let decoded = image::load_from_memory(MENU_BAR_ICON_BYTES)
        .context("failed to decode the embedded menu bar icon")?
        .into_rgba8();
    let (width, height) = decoded.dimensions();
    Icon::from_rgba(decoded.into_raw(), width, height).map_err(|e| anyhow!("{e}"))
}

/// Renders `epoch` in the Mac's own local time (not UTC) — what the person looking at the menu
/// actually keeps their clock in. Shells out to `date` rather than using the `time` crate's own
/// local-offset support: that requires opting out of a soundness guard it enables by default in
/// multithreaded programs (which this is), since determining the local UTC offset on Unix isn't
/// safely reentrant. `date` doesn't have that problem — it's what the menu bar clock itself uses.
fn format_due(epoch: u64) -> String {
    std::process::Command::new("date")
        .arg("-r")
        .arg(epoch.to_string())
        .arg("+%Y-%m-%d %H:%M %Z")
        .output()
        .ok()
        .filter(|output| output.status.success())
        .map(|output| String::from_utf8_lossy(&output.stdout).trim().to_string())
        .filter(|text| !text.is_empty())
        .unwrap_or_else(|| epoch.to_string())
}
