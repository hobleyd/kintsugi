use std::cell::RefCell;
use std::sync::mpsc::Sender;

use anyhow::{anyhow, Context, Result};
use objc2::MainThreadMarker;
use objc2_app_kit::{NSApplication, NSApplicationActivationPolicy};
use tray_icon::menu::{Menu, MenuEvent, MenuItem, PredefinedMenuItem};
use tray_icon::{Icon, TrayIcon, TrayIconBuilder};

use crate::logging;
use crate::status::AgentStatus;

const MENU_BAR_ICON_BYTES: &[u8] = include_bytes!("../assets/menu-bar-icon.png");
const PATCH_NOW_ID: &str = "patch-now";
const TOOLTIP: &str = "Kintsugi Patching";

struct MenuState {
    // Kept alive for as long as the app runs — dropping it removes the icon from the menu bar.
    _tray_icon: TrayIcon,
    status_item: MenuItem,
    progress_item: MenuItem,
    patch_now_item: MenuItem,
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
pub fn run(patch_now_tx: Sender<()>) -> Result<()> {
    let mtm = MainThreadMarker::new().context("the menu bar can only be set up on the main thread")?;

    let app = NSApplication::sharedApplication(mtm);
    // A pure menu-bar utility: no Dock icon, no Cmd-Tab entry, never steals focus on launch.
    app.setActivationPolicy(NSApplicationActivationPolicy::Accessory);

    let (tray_icon, status_item, progress_item, patch_now_item) = build_tray_icon()?;
    MENU_STATE.with(|cell| {
        *cell.borrow_mut() = Some(MenuState {
            _tray_icon: tray_icon,
            status_item,
            progress_item,
            patch_now_item,
        });
    });
    logging::info("menu bar icon created");

    MenuEvent::set_event_handler(Some(move |event: MenuEvent| {
        if event.id.as_ref() == PATCH_NOW_ID {
            logging::info("\"Patch Now\" clicked in the menu bar");
            if let Err(err) = patch_now_tx.send(()) {
                logging::error(&format!("could not signal the scheduler thread: {err}"));
            }
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

fn build_tray_icon() -> Result<(TrayIcon, MenuItem, MenuItem, MenuItem)> {
    let icon = load_icon()?;

    let status_item = MenuItem::with_id("status", "Loading patching status\u{2026}", false, None);
    let progress_item = MenuItem::with_id("progress", "", false, None);
    let patch_now_item = MenuItem::with_id(PATCH_NOW_ID, "Patch Now", true, None);
    // Static for the life of the process — this binary's own version never changes underneath it
    // (a self-update replaces the binary on disk and restarts this process; it doesn't rewrite a
    // running one) — so, unlike status_item/progress_item, there's nothing to keep a handle to
    // for later updates.
    let version_item = MenuItem::new(format!("Version {}", env!("CARGO_PKG_VERSION")), false, None);

    let menu = Menu::new();
    menu.append(&status_item).map_err(|e| anyhow!("{e}"))?;
    menu.append(&progress_item).map_err(|e| anyhow!("{e}"))?;
    menu.append(&PredefinedMenuItem::separator()).map_err(|e| anyhow!("{e}"))?;
    menu.append(&patch_now_item).map_err(|e| anyhow!("{e}"))?;
    menu.append(&PredefinedMenuItem::separator()).map_err(|e| anyhow!("{e}"))?;
    menu.append(&version_item).map_err(|e| anyhow!("{e}"))?;

    let tray_icon = TrayIconBuilder::new()
        .with_menu(Box::new(menu))
        .with_icon(icon)
        .with_icon_as_template(true)
        .with_tooltip(TOOLTIP)
        .build()
        .map_err(|e| anyhow!("{e}"))?;

    Ok((tray_icon, status_item, progress_item, patch_now_item))
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
