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
use crate::status::{AgentStatus, CheckInStatus, MenuAction};

const MENU_BAR_ICON_BYTES: &[u8] = include_bytes!("../assets/menu-bar-icon.png");
const CHECK_IN_NOW_ID: &str = "check-in-now";
const PATCH_NOW_ID: &str = "patch-now";
const END_REMOTE_SESSION_ID: &str = "end-remote-session";
const TOOLTIP: &str = "Kintsugi Patching";

struct MenuState {
    // Kept alive for as long as the app runs — dropping it removes the icon from the menu bar.
    _tray_icon: TrayIcon,
    check_in_item: MenuItem,
    status_item: MenuItem,
    progress_item: MenuItem,
    check_in_now_item: MenuItem,
    patch_now_item: MenuItem,

    // Whether each half of the process is busy. The two action items are enabled only while
    // neither is: the scheduler thread serves both actions, so a click during either would sit in
    // the channel and run the moment the current one finished — which from the menu looks like a
    // button that did nothing and then, minutes later, did something unasked.
    patching: bool,
    checking_in: bool,

    // The remote control block. Held rather than rebuilt because these three are inserted into and
    // removed from the menu as sessions come and go — a permanent "Remote session: none" line would
    // be clutter on the overwhelming majority of hosts, which never have one.
    menu: Menu,
    remote_session_item: MenuItem,
    end_remote_session_item: MenuItem,
    remote_separator: PredefinedMenuItem,
    remote_block_shown: bool,
}

impl MenuState {
    fn refresh_actions(&self) {
        let enabled = !self.patching && !self.checking_in;
        self.check_in_now_item.set_enabled(enabled);
        self.patch_now_item.set_enabled(enabled);
    }
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
/// status item) and never returns normally. `menu_tx` is how a click on "Check In Now" or "Patch
/// Now" reaches the scheduler thread.
pub fn run(menu_tx: Sender<MenuAction>, end_remote_session: Arc<AtomicBool>) -> Result<()> {
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
        let action = match event.id.as_ref() {
            CHECK_IN_NOW_ID => {
                logging::info("\"Check In Now\" clicked in the menu bar");
                MenuAction::CheckInNow
            }
            PATCH_NOW_ID => {
                logging::info("\"Patch Now\" clicked in the menu bar");
                MenuAction::PatchNow
            }
            END_REMOTE_SESSION_ID => {
                // A flag rather than a channel, because the session loop is polling anyway (it has
                // to, to interleave reading input with sending frames) and because this must work
                // whether or not a session is currently running — a click that arrives as one is
                // already ending has nowhere to be delivered.
                logging::info("\"End Remote Session\" clicked in the menu bar");
                end_remote_session.store(true, Ordering::SeqCst);
                return;
            }
            _ => return,
        };
        if let Err(err) = menu_tx.send(action) {
            logging::error(&format!("could not signal the scheduler thread: {err}"));
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
            let mut borrowed = cell.borrow_mut();
            let Some(state) = borrowed.as_mut() else { return };

            match &status {
                AgentStatus::Idle { next_due_epoch } => {
                    state.status_item.set_text(format!("Next patch due: {}", format_due(*next_due_epoch)));
                    state.progress_item.set_text("Status: idle");
                    state.patching = false;
                }
                AgentStatus::Patching { current, completed, total } => {
                    state.status_item.set_text(format!("Patching: {current}"));
                    state.progress_item.set_text(if *total > 0 {
                        format!("Progress: {}", crate::dialogs::progress_bar(*completed, *total))
                    } else {
                        "Progress: starting\u{2026}".to_string()
                    });
                    state.patching = true;
                }
            }
            state.refresh_actions();
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

/// Pushes the daemon's check-in schedule to the menu bar's "Next check-in" line, and greys both
/// actions while a "Check In Now" is in flight. Safe to call from any thread, like `report_status`,
/// and for the same reason.
///
/// Separate from `report_status` because the two describe different processes: the patch cycle is
/// this one's, the check-in is the root daemon's, and either can be busy while the other is idle.
pub fn report_check_in(status: CheckInStatus) {
    dispatch::Queue::main().exec_async(move || {
        MENU_STATE.with(|cell| {
            let mut borrowed = cell.borrow_mut();
            let Some(state) = borrowed.as_mut() else { return };

            match status {
                CheckInStatus::Scheduled { next_epoch: Some(epoch) } => {
                    state.check_in_item.set_text(format!("Next check-in: {}", format_due(epoch)));
                    state.checking_in = false;
                }
                CheckInStatus::Scheduled { next_epoch: None } => {
                    state.check_in_item.set_text("Next check-in: not yet scheduled");
                    state.checking_in = false;
                }
                CheckInStatus::InProgress => {
                    state.check_in_item.set_text("Checking in with the server\u{2026}");
                    state.checking_in = true;
                }
            }
            state.refresh_actions();
        });
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
                        // Directly under the status lines, above "Check In Now" and "Patch Now": the
                        // most important thing in this menu while it is there.
                        let inserted = state
                            .menu
                            .insert(&state.remote_separator, 3)
                            .and_then(|()| state.menu.insert(&state.remote_session_item, 4))
                            .and_then(|()| state.menu.insert(&state.end_remote_session_item, 5));

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

    let check_in_item = MenuItem::with_id("check-in", "Next check-in: not yet scheduled", false, None);
    let status_item = MenuItem::with_id("status", "Loading patching status\u{2026}", false, None);
    let progress_item = MenuItem::with_id("progress", "", false, None);
    let check_in_now_item = MenuItem::with_id(CHECK_IN_NOW_ID, "Check In Now", true, None);
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
    menu.append(&check_in_item).map_err(|e| anyhow!("{e}"))?;
    menu.append(&status_item).map_err(|e| anyhow!("{e}"))?;
    menu.append(&progress_item).map_err(|e| anyhow!("{e}"))?;
    menu.append(&PredefinedMenuItem::separator()).map_err(|e| anyhow!("{e}"))?;
    menu.append(&check_in_now_item).map_err(|e| anyhow!("{e}"))?;
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
        check_in_item,
        status_item,
        progress_item,
        check_in_now_item,
        patch_now_item,
        patching: false,
        checking_in: false,
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
