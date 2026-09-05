use std::sync::atomic::{AtomicBool, AtomicIsize, Ordering};
use std::sync::mpsc::Sender;
use std::sync::{Mutex, OnceLock};

use anyhow::{Context, Result};
use windows_sys::Win32::Foundation::{HWND, LPARAM, WPARAM};
use windows_sys::Win32::UI::Shell::{
    Shell_NotifyIconW, NIF_ICON, NIF_INFO, NIF_MESSAGE, NIF_TIP, NIIF_INFO, NIM_ADD, NIM_DELETE, NIM_MODIFY, NOTIFYICONDATAW,
};
use windows_sys::Win32::UI::WindowsAndMessaging::{
    AppendMenuW, CreatePopupMenu, CreateWindowExW, DefWindowProcW, DestroyMenu, DispatchMessageW, GetCursorPos, GetMessageW, LoadIconW,
    PostMessageW, PostQuitMessage, SetForegroundWindow, TrackPopupMenu, TranslateMessage, IDI_APPLICATION, MF_GRAYED, MF_SEPARATOR, MF_STRING,
    MSG, TPM_BOTTOMALIGN, TPM_RIGHTALIGN, WM_APP, WM_DESTROY, WM_RBUTTONUP, WS_OVERLAPPED,
};
use windows_sys::Win32::Foundation::POINT;

use crate::logging;
use crate::status::{AgentStatus, CheckInStatus, MenuAction};
use crate::win32::{instance, register_class, wide, WindowClass};

const CLASS_NAME: &str = "KintsugiAgentTray";
const TOOLTIP: &str = "Kintsugi Patching";

/// The message the notification-area icon sends this window for every mouse event on it. Must be in
/// the `WM_APP` range: `WM_USER` is reserved for the *window class's* own use, and a control could
/// legitimately send one.
const WM_TRAY_ICON: u32 = WM_APP + 1;
/// Posted (from any thread) to tell the UI thread there's a new status waiting in `PENDING_STATUS`.
const WM_STATUS_CHANGED: u32 = WM_APP + 2;
/// Posted (from any thread) to tell the UI thread there's a balloon waiting in `PENDING_NOTIFICATION`.
const WM_NOTIFY_REQUESTED: u32 = WM_APP + 3;

const ICON_ID: u32 = 1;

const MENU_ID_CHECK_IN: usize = 100;
const MENU_ID_STATUS: usize = 101;
const MENU_ID_PROGRESS: usize = 102;
const MENU_ID_CHECK_IN_NOW: usize = 103;
const MENU_ID_PATCH_NOW: usize = 104;
const MENU_ID_VERSION: usize = 105;

/// The tray window's handle, published for the *other* threads that need to poke the UI.
///
/// `PostMessageW` is explicitly safe to call from any thread — it queues a message and returns
/// rather than calling into the window procedure — which is exactly why the cross-thread contract
/// here is "post a message, let the UI thread do the work". Everything that actually touches a
/// window happens on the thread that owns it.
static TRAY_HWND: AtomicIsize = AtomicIsize::new(0);

/// Set by the scheduler thread, drained by the UI thread when it handles `WM_STATUS_CHANGED`. Only
/// the latest matters — an intermediate progress step that was superseded before the UI got to it
/// has nothing to add.
static PENDING_STATUS: Mutex<Option<AgentStatus>> = Mutex::new(None);

/// Balloons waiting to be shown, oldest first. A queue rather than a single slot, unlike the status
/// above: a patch cycle emits one notification per application, and each is a distinct thing the
/// user is meant to see, so dropping all but the last would silently swallow most of them.
static PENDING_NOTIFICATIONS: Mutex<Vec<(String, String)>> = Mutex::new(Vec::new());

/// Sends the scheduler a `MenuAction`. Stored rather than passed because the window procedure is a
/// plain function pointer with no room for state.
static MENU_TX: OnceLock<Sender<MenuAction>> = OnceLock::new();

/// Whether a patch cycle is running, and whether a "Check In Now" is in flight. Both action items
/// are selectable only while neither is — greyed out mid-cycle, the same way the macOS agent's menu
/// items are, so a second cycle can't be started on top of a running one, and greyed out during a
/// check-in because the scheduler thread serves both actions: a click then would sit in the channel
/// and run the moment the check-in finished, which from the menu looks like an item that did nothing
/// and then, minutes later, did something unasked.
static PATCHING: AtomicBool = AtomicBool::new(false);
static CHECKING_IN: AtomicBool = AtomicBool::new(false);

/// The three lines of menu text the icon currently shows: the check-in line, the status line and
/// the progress line. Owned by the UI thread; only ever read and written while handling a message,
/// which is single-threaded by construction.
static MENU_TEXT: Mutex<MenuText> = Mutex::new(MenuText::EMPTY);

#[derive(Clone)]
struct MenuText {
    check_in: String,
    status: String,
    progress: String,
}

impl MenuText {
    const EMPTY: MenuText = MenuText { check_in: String::new(), status: String::new(), progress: String::new() };
}

/// Sets up the notification-area icon and runs this thread's message loop for the rest of the
/// process's life — must be called on the main thread and never returns normally.
///
/// The macOS agent needs its UI on the main thread because Cocoa demands it. Windows has no such
/// rule — a window is owned by whichever thread created it — but the same shape is kept anyway: the
/// scheduler blocks on HTTP calls, five-minute warnings, and modal dialogs, and a message loop that
/// stops being pumped for that long makes the icon stop responding to clicks. So the scheduler runs
/// on a background thread and the UI keeps this one, exactly as on macOS.

pub fn run(menu_tx: Sender<MenuAction>) -> Result<()> {
    let _ = MENU_TX.set(menu_tx);

    let class = tray_class();

    // SAFETY: a message-only-style hidden window (never shown) that exists solely to receive the
    // icon's notifications. Every pointer is null or a live NUL-terminated buffer.
    let hwnd = unsafe {
        let title = wide(TOOLTIP);
        CreateWindowExW(
            0,
            class.name(),
            title.as_ptr(),
            WS_OVERLAPPED,
            0,
            0,
            0,
            0,
            std::ptr::null_mut(),
            std::ptr::null_mut(),
            instance(),
            std::ptr::null(),
        )
    };

    if hwnd.is_null() {
        anyhow::bail!("could not create the notification-area window");
    }

    TRAY_HWND.store(hwnd as isize, Ordering::SeqCst);

    add_icon(hwnd).context("could not add the notification-area icon")?;
    logging::info("notification-area icon created");

    // SAFETY: runs on the thread that owns the window, which is what a message loop requires.
    unsafe {
        let mut msg: MSG = std::mem::zeroed();
        while GetMessageW(&mut msg, std::ptr::null_mut(), 0, 0) > 0 {
            TranslateMessage(&msg);
            DispatchMessageW(&msg);
        }
    }

    remove_icon(hwnd);
    Ok(())
}

/// Pushes a status update to the notification-area menu. Safe to call from any thread — the
/// scheduler thread is the only real caller — since the actual UI update is marshaled onto the UI
/// thread by posting a message, and only that thread ever touches the icon.
pub fn report_status(status: AgentStatus) {
    if let Ok(mut pending) = PENDING_STATUS.lock() {
        *pending = Some(status);
    }
    post_to_ui_thread(WM_STATUS_CHANGED);
}

/// Pushes the service's check-in schedule to the menu's "Next check-in" line, and greys both
/// actions while a "Check In Now" is in flight. Safe to call from any thread, like `report_status`.
///
/// Separate from `report_status` because the two describe different processes: the patch cycle is
/// this one's, the check-in is the service's, and either can be busy while the other is idle. No
/// message is posted: the menu is built fresh from these statics on every click (see `show_menu`),
/// so setting them is the whole of it — the same reason `report_remote_session` on the Linux agent
/// has to poke ksni and this file's equivalent never did.
pub fn report_check_in(status: CheckInStatus) {
    let (line, checking_in) = match status {
        CheckInStatus::Scheduled { next_epoch: Some(epoch) } => (format!("Next check-in: {}", format_due(epoch)), false),
        CheckInStatus::Scheduled { next_epoch: None } => ("Next check-in: not yet scheduled".to_string(), false),
        CheckInStatus::InProgress => ("Checking in with the server\u{2026}".to_string(), true),
    };

    if let Ok(mut text) = MENU_TEXT.lock() {
        text.check_in = line;
    }
    CHECKING_IN.store(checking_in, Ordering::SeqCst);
}

/// Queues a balloon notification on the icon. Safe to call from any thread, for the same reason as
/// `report_status`. A no-op before `run` has created the icon (or after the process has started
/// shutting down) — a notification is never worth failing a patch over.
pub fn notify(title: &str, message: &str) {
    if let Ok(mut pending) = PENDING_NOTIFICATIONS.lock() {
        pending.push((title.to_string(), message.to_string()));
    }
    post_to_ui_thread(WM_NOTIFY_REQUESTED);
}

fn post_to_ui_thread(message: u32) {
    let hwnd = TRAY_HWND.load(Ordering::SeqCst);
    if hwnd == 0 {
        return;
    }
    // SAFETY: PostMessageW is documented as callable from any thread; it queues the message for the
    // owning thread rather than invoking the window procedure here.
    unsafe {
        PostMessageW(hwnd as HWND, message, 0, 0);
    }
}

/// Builds the fixed part of a NOTIFYICONDATAW for this agent's single icon. Everything variable
/// (which fields are being set, and the balloon text) is filled in by the caller.
fn icon_data(hwnd: HWND) -> NOTIFYICONDATAW {
    // SAFETY: NOTIFYICONDATAW is a plain C struct of integers, handles, and fixed-size arrays; an
    // all-zero value is the documented starting point, with cbSize and the used fields set below.
    let mut data: NOTIFYICONDATAW = unsafe { std::mem::zeroed() };
    data.cbSize = std::mem::size_of::<NOTIFYICONDATAW>() as u32;
    data.hWnd = hwnd;
    data.uID = ICON_ID;
    data
}

fn copy_into(buffer: &mut [u16], value: &str) {
    let encoded = wide(value);
    // Truncated to fit, always leaving room for the terminator — Win32 reads these fixed-size
    // arrays until a NUL, so an unterminated one would run off the end of the struct.
    let limit = buffer.len().saturating_sub(1).min(encoded.len().saturating_sub(1));
    buffer[..limit].copy_from_slice(&encoded[..limit]);
    buffer[limit] = 0;
}

fn add_icon(hwnd: HWND) -> Result<()> {
    let mut data = icon_data(hwnd);
    data.uFlags = NIF_ICON | NIF_MESSAGE | NIF_TIP;
    data.uCallbackMessage = WM_TRAY_ICON;
    // SAFETY: IDI_APPLICATION is a system-provided icon resource; loading it with a null module
    // handle is the documented way to get it, and the handle is owned by the system.
    data.hIcon = unsafe { LoadIconW(std::ptr::null_mut(), IDI_APPLICATION) };
    copy_into(&mut data.szTip, TOOLTIP);

    // SAFETY: data is fully initialized above and outlives the call, which copies what it needs.
    let added = unsafe { Shell_NotifyIconW(NIM_ADD, &data) };
    if added == 0 {
        anyhow::bail!("Shell_NotifyIconW(NIM_ADD) failed");
    }
    Ok(())
}

fn remove_icon(hwnd: HWND) {
    let data = icon_data(hwnd);
    // SAFETY: as above. Leaving the icon behind would strand a dead icon in the notification area
    // until the user hovered over it, so this runs on the way out of the message loop.
    unsafe {
        Shell_NotifyIconW(NIM_DELETE, &data);
    }
}

fn show_balloon(hwnd: HWND, title: &str, message: &str) {
    let mut data = icon_data(hwnd);
    data.uFlags = NIF_INFO;
    data.dwInfoFlags = NIIF_INFO;
    copy_into(&mut data.szInfoTitle, title);
    copy_into(&mut data.szInfo, message);

    // SAFETY: as in add_icon.
    unsafe {
        Shell_NotifyIconW(NIM_MODIFY, &data);
    }
}

/// Applies a status to the menu text and the progress window — the UI thread's half of
/// `report_status`.
fn apply_status(status: AgentStatus) {
    let (status_line, progress_line, patching) = match &status {
        AgentStatus::Idle { next_due_epoch } => (
            format!("Next patch due: {}", format_due(*next_due_epoch)),
            "Status: idle".to_string(),
            false,
        ),
        AgentStatus::Patching { current, completed, total } => (
            format!("Patching: {current}"),
            if *total > 0 {
                format!("Progress: {}", crate::dialogs::progress_bar(*completed, *total))
            } else {
                "Progress: starting\u{2026}".to_string()
            },
            true,
        ),
    };

    if let Ok(mut text) = MENU_TEXT.lock() {
        text.status = status_line;
        text.progress = progress_line;
    }
    PATCHING.store(patching, Ordering::SeqCst);

    // A window, unlike the menu, is visible without the user having to think to go looking for it —
    // opened the moment there's something to show, closed again once idle.
    match &status {
        AgentStatus::Idle { .. } => crate::progress_window::hide(),
        AgentStatus::Patching { current, completed, total } => crate::progress_window::show_and_update(current, *completed, *total),
    }
}

/// Builds and shows the popup menu at the cursor, and returns once the user has picked something or
/// dismissed it.
///
/// Rebuilt on each click rather than kept and mutated: the menu only exists for the moment it's on
/// screen, and building it fresh from the current text means there's no second copy of the state to
/// keep in step.
fn show_menu(hwnd: HWND) {
    let text = MENU_TEXT.lock().map(|text| text.clone()).unwrap_or(MenuText::EMPTY);
    let actions_enabled = !PATCHING.load(Ordering::SeqCst) && !CHECKING_IN.load(Ordering::SeqCst);
    let action_flags = if actions_enabled { MF_STRING } else { MF_STRING | MF_GRAYED };

    // SAFETY: every handle below is created and destroyed within this function, on the UI thread
    // that owns the window; every string outlives the call that reads it.
    unsafe {
        let menu = CreatePopupMenu();
        if menu.is_null() {
            return;
        }

        let check_in_text = wide(if text.check_in.is_empty() { "Next check-in: not yet scheduled" } else { &text.check_in });
        AppendMenuW(menu, MF_STRING | MF_GRAYED, MENU_ID_CHECK_IN, check_in_text.as_ptr());
        let status_text = wide(if text.status.is_empty() { "Loading patching status\u{2026}" } else { &text.status });
        AppendMenuW(menu, MF_STRING | MF_GRAYED, MENU_ID_STATUS, status_text.as_ptr());
        let progress_text = wide(&text.progress);
        AppendMenuW(menu, MF_STRING | MF_GRAYED, MENU_ID_PROGRESS, progress_text.as_ptr());
        AppendMenuW(menu, MF_SEPARATOR, 0, std::ptr::null());

        // No remote-session entry here, deliberately. The session helper shows its own banner (see
        // session_banner), which is both more visible and — being a SYSTEM-owned window — something
        // user-level malware cannot click or close. Two indicators would be two sources of truth
        // that could disagree about whether a session is running.
        let check_in_now_text = wide("Check In Now");
        AppendMenuW(menu, action_flags, MENU_ID_CHECK_IN_NOW, check_in_now_text.as_ptr());
        let patch_now_text = wide("Patch Now");
        AppendMenuW(menu, action_flags, MENU_ID_PATCH_NOW, patch_now_text.as_ptr());
        AppendMenuW(menu, MF_SEPARATOR, 0, std::ptr::null());

        // Static for the life of the process — this binary's own version never changes underneath
        // it (a self-update replaces the binary on disk and restarts this process; it doesn't
        // rewrite a running one).
        let version_text = wide(&format!("Version {}", env!("CARGO_PKG_VERSION")));
        AppendMenuW(menu, MF_STRING | MF_GRAYED, MENU_ID_VERSION, version_text.as_ptr());

        let mut cursor = POINT { x: 0, y: 0 };
        GetCursorPos(&mut cursor);

        // Required before TrackPopupMenu, and not optional: without it the menu doesn't dismiss
        // when the user clicks elsewhere, and just hangs on screen. This is the documented
        // workaround and has been since Windows 95.
        SetForegroundWindow(hwnd);

        // TPM_RETURNCMD isn't used: letting the menu post WM_COMMAND back to this window keeps the
        // click handling in one place (the window procedure) rather than splitting it.
        TrackPopupMenu(menu, TPM_RIGHTALIGN | TPM_BOTTOMALIGN, cursor.x, cursor.y, 0, hwnd, std::ptr::null());

        DestroyMenu(menu);
    }
}

fn tray_class() -> &'static WindowClass {
    static CLASS: OnceLock<WindowClass> = OnceLock::new();
    CLASS.get_or_init(|| {
        // SAFETY: tray_wnd_proc has the required window-procedure signature, and the class is
        // registered exactly once for the life of the process.
        unsafe { register_class(CLASS_NAME, tray_wnd_proc, std::ptr::null_mut()) }
    })
}

unsafe extern "system" fn tray_wnd_proc(hwnd: HWND, msg: u32, wparam: WPARAM, lparam: LPARAM) -> isize {
    use windows_sys::Win32::UI::WindowsAndMessaging::{WM_COMMAND, WM_LBUTTONUP};

    match msg {
        WM_TRAY_ICON => {
            // The icon reports the mouse event in lparam, not wparam. Both buttons open the menu:
            // a notification-area icon with no primary window has nothing else a left click could
            // usefully do, and users reach for either.
            let event = (lparam as u32) & 0xFFFF;
            if event == WM_LBUTTONUP || event == WM_RBUTTONUP {
                show_menu(hwnd);
            }
            0
        }
        WM_COMMAND => {
            let action = match (wparam & 0xFFFF) as usize {
                MENU_ID_CHECK_IN_NOW => {
                    logging::info("\"Check In Now\" clicked in the notification area");
                    Some(MenuAction::CheckInNow)
                }
                MENU_ID_PATCH_NOW => {
                    logging::info("\"Patch Now\" clicked in the notification area");
                    Some(MenuAction::PatchNow)
                }
                _ => None,
            };
            if let Some(action) = action {
                match MENU_TX.get().map(|tx| tx.send(action)) {
                    Some(Ok(())) => {}
                    Some(Err(err)) => logging::error(&format!("could not signal the scheduler thread: {err}")),
                    None => logging::error(&format!("{action:?} clicked before the scheduler was wired up")),
                }
            }
            0
        }
        WM_STATUS_CHANGED => {
            let status = PENDING_STATUS.lock().ok().and_then(|mut pending| pending.take());
            if let Some(status) = status {
                apply_status(status);
            }
            0
        }
        WM_NOTIFY_REQUESTED => {
            let queued = PENDING_NOTIFICATIONS
                .lock()
                .map(|mut pending| std::mem::take(&mut *pending))
                .unwrap_or_default();
            for (title, message) in queued {
                show_balloon(hwnd, &title, &message);
            }
            0
        }
        WM_DESTROY => {
            PostQuitMessage(0);
            0
        }
        _ => DefWindowProcW(hwnd, msg, wparam, lparam),
    }
}

/// Renders `epoch` in the PC's own local time (not UTC) — what the person looking at the menu
/// actually keeps their clock in.
///
/// Shells out to PowerShell rather than using the `time` crate's own local-offset support, for the
/// same reason the macOS agent shells out to `date`: determining the local UTC offset isn't safely
/// reentrant in a multithreaded program (which this is), and that crate refuses to do it by default
/// rather than be unsound. Falls back to the raw epoch if that fails, which is ugly but never wrong.
fn format_due(epoch: u64) -> String {
    let script = format!(
        "[DateTimeOffset]::FromUnixTimeSeconds({epoch}).ToLocalTime().ToString('yyyy-MM-dd HH:mm')"
    );

    std::process::Command::new(crate::os_update::POWERSHELL)
        .args(["-NoProfile", "-NonInteractive", "-Command", &script])
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
    use windows_sys::Win32::UI::WindowsAndMessaging::WM_USER;

    #[test]
    fn copy_into_truncates_to_the_buffer_and_always_terminates() {
        // szInfo and friends are fixed-size arrays that Win32 reads until a NUL — an unterminated
        // one reads off the end of the struct.
        let mut buffer = [0xFFFFu16; 8];

        copy_into(&mut buffer, "a much longer string than fits");

        assert_eq!(buffer[7], 0);
        assert!(buffer[..7].iter().all(|&c| c != 0));
    }

    #[test]
    fn copy_into_writes_a_short_value_with_its_terminator() {
        let mut buffer = [0xFFFFu16; 8];

        copy_into(&mut buffer, "hi");

        assert_eq!(buffer[0], b'h' as u16);
        assert_eq!(buffer[1], b'i' as u16);
        assert_eq!(buffer[2], 0);
    }

    #[test]
    fn copy_into_handles_an_empty_value() {
        let mut buffer = [0xFFFFu16; 4];

        copy_into(&mut buffer, "");

        assert_eq!(buffer[0], 0);
    }

    #[test]
    fn the_tray_callback_message_is_in_the_wm_app_range() {
        // WM_USER is reserved for a window class's own messages and a control could legitimately
        // send one; only WM_APP and above is safe for an application to define.
        assert!(WM_TRAY_ICON >= WM_APP);
        assert!(WM_STATUS_CHANGED >= WM_APP);
        assert!(WM_NOTIFY_REQUESTED >= WM_APP);
        assert!(WM_APP > WM_USER);
    }

    #[test]
    fn the_three_posted_messages_are_distinct() {
        assert_ne!(WM_TRAY_ICON, WM_STATUS_CHANGED);
        assert_ne!(WM_STATUS_CHANGED, WM_NOTIFY_REQUESTED);
        assert_ne!(WM_TRAY_ICON, WM_NOTIFY_REQUESTED);
    }

    #[test]
    fn notify_before_the_icon_exists_queues_rather_than_dropping() {
        // The scheduler can emit a notification before the UI thread has finished setting up; those
        // should be shown once it has, not silently lost.
        if let Ok(mut pending) = PENDING_NOTIFICATIONS.lock() {
            pending.clear();
        }

        notify("Kintsugi Patching", "queued before the icon existed");

        let queued = PENDING_NOTIFICATIONS.lock().unwrap();
        assert_eq!(queued.len(), 1);
        assert_eq!(queued[0].1, "queued before the icon existed");
    }
}
