use std::cell::RefCell;

use windows_sys::Win32::Foundation::{HWND, LPARAM, WPARAM};
use windows_sys::Win32::Graphics::Gdi::{GetSysColorBrush, COLOR_BTNFACE};
use windows_sys::Win32::UI::Controls::{
    InitCommonControlsEx, ICC_PROGRESS_CLASS, INITCOMMONCONTROLSEX, PBM_SETPOS, PBM_SETRANGE32, PROGRESS_CLASSW,
};
use windows_sys::Win32::UI::WindowsAndMessaging::{
    CreateWindowExW, DefWindowProcW, SendMessageW, SetForegroundWindow, SetWindowTextW, ShowWindow, SW_HIDE, SW_SHOW, WM_CLOSE,
    WS_CAPTION, WS_CHILD, WS_SYSMENU, WS_VISIBLE,
};

use crate::win32::{apply_default_font, center_on_screen, instance, register_class, wide, WindowClass};

/// `SS_LEFT` — a static control's default, left-aligned text style. Spelled out here because
/// windows-sys doesn't surface the `SS_*` constants; it is `0x0000_0000` in the Windows SDK, so
/// naming it costs nothing and says what the flag means at the call site.
const SS_LEFT: u32 = 0x0000_0000;

const CLASS_NAME: &str = "KintsugiAgentProgress";
const WIDTH: i32 = 460;
const HEIGHT: i32 = 150;
const MARGIN: i32 = 20;

struct Window {
    window: HWND,
    status_label: HWND,
    progress_bar: HWND,
}

thread_local! {
    // Only ever touched from the UI thread — created lazily on first use rather than up front, so a
    // PC that never actually patches never has a window sitting around at all. A window belongs to
    // the thread that created it, and only that thread may touch it, so a thread-local expresses
    // exactly the right scope; the same reasoning the macOS agent applies to its own non-Send
    // AppKit objects.
    static WINDOW: RefCell<Option<Window>> = const { RefCell::new(None) };
}

/// Shows the progress window (creating it on first use) with `current` as its headline and a
/// determinate progress bar reflecting `completed`/`total` (an empty bar when `total` is zero —
/// e.g. during the initial warning period, before there's anything concrete to count). Also brings
/// it to the foreground, since a process with no visible main window doesn't otherwise get focus
/// when a window opens.
///
/// Must be called on the UI thread — see `tray_menu::apply_status`, the only caller.
pub fn show_and_update(current: &str, completed: usize, total: usize) {
    WINDOW.with(|cell| {
        let mut borrowed = cell.borrow_mut();
        let window = borrowed.get_or_insert_with(create);

        if window.window.is_null() {
            return;
        }

        // SAFETY: every handle here was created by this thread and is still live; the text buffer
        // outlives the call, which copies it.
        unsafe {
            let text = wide(current);
            SetWindowTextW(window.status_label, text.as_ptr());

            // A zero total would make the bar's range empty, which renders as permanently full —
            // the opposite of what "nothing has happened yet" should look like.
            let range_max = if total > 0 { total as i32 } else { 1 };
            SendMessageW(window.progress_bar, PBM_SETRANGE32, 0, range_max as LPARAM);
            SendMessageW(window.progress_bar, PBM_SETPOS, completed, 0);

            ShowWindow(window.window, SW_SHOW);
            SetForegroundWindow(window.window);
        }
    });
}

/// Hides the progress window — called once a patch cycle finishes (success, failure, or a delay)
/// and the agent goes back to idle. A no-op if the window was never created.
///
/// Hidden rather than destroyed: a patch cycle recurs, and keeping the window means the next one
/// doesn't have to rebuild it (and can't fail to).
pub fn hide() {
    WINDOW.with(|cell| {
        if let Some(window) = cell.borrow().as_ref() {
            if !window.window.is_null() {
                // SAFETY: a live window owned by this thread.
                unsafe {
                    ShowWindow(window.window, SW_HIDE);
                }
            }
        }
    });
}

fn create() -> Window {
    let class = progress_class();

    // SAFETY: the progress-bar common control class has to be registered before a window of it can
    // be created; INITCOMMONCONTROLSEX is a two-field struct fully initialized here.
    unsafe {
        let controls = INITCOMMONCONTROLSEX {
            dwSize: std::mem::size_of::<INITCOMMONCONTROLSEX>() as u32,
            dwICC: ICC_PROGRESS_CLASS,
        };
        InitCommonControlsEx(&controls);
    }

    // SAFETY: every pointer below is either null or a live NUL-terminated buffer that outlives its
    // call. A null return is checked by every caller before the handle is used.
    unsafe {
        let title = wide("Kintsugi Patching");
        let window = CreateWindowExW(
            0,
            class.name(),
            title.as_ptr(),
            // No WS_THICKFRAME and no maximize box: a fixed-size status window, not something to
            // resize. WS_SYSMENU gives it a close box, which WM_CLOSE below turns into "hide".
            WS_CAPTION | WS_SYSMENU,
            0,
            0,
            WIDTH,
            HEIGHT,
            std::ptr::null_mut(),
            std::ptr::null_mut(),
            instance(),
            std::ptr::null(),
        );

        if window.is_null() {
            crate::logging::warn("could not create the progress window; patching will continue without it");
            return Window { window, status_label: std::ptr::null_mut(), progress_bar: std::ptr::null_mut() };
        }

        let static_class = wide("STATIC");
        let initial = wide("Starting\u{2026}");
        let status_label = CreateWindowExW(
            0,
            static_class.as_ptr(),
            initial.as_ptr(),
            WS_CHILD | WS_VISIBLE | SS_LEFT,
            MARGIN,
            MARGIN,
            WIDTH - MARGIN * 3,
            40,
            window,
            std::ptr::null_mut(),
            instance(),
            std::ptr::null(),
        );
        apply_default_font(status_label);

        let progress_bar = CreateWindowExW(
            0,
            PROGRESS_CLASSW,
            std::ptr::null(),
            WS_CHILD | WS_VISIBLE,
            MARGIN,
            MARGIN + 46,
            WIDTH - MARGIN * 3,
            22,
            window,
            std::ptr::null_mut(),
            instance(),
            std::ptr::null(),
        );

        center_on_screen(window, WIDTH, HEIGHT);

        Window { window, status_label, progress_bar }
    }
}

fn progress_class() -> &'static WindowClass {
    use std::sync::OnceLock;
    static CLASS: OnceLock<WindowClass> = OnceLock::new();

    CLASS.get_or_init(|| {
        // SAFETY: progress_wnd_proc has the required window-procedure signature, and the class is
        // registered exactly once for the life of the process.
        unsafe { register_class(CLASS_NAME, progress_wnd_proc, GetSysColorBrush(COLOR_BTNFACE) as _) }
    })
}

unsafe extern "system" fn progress_wnd_proc(hwnd: HWND, msg: u32, wparam: WPARAM, lparam: LPARAM) -> isize {
    match msg {
        // The close box hides the window rather than destroying it. Destroying it would leave
        // `WINDOW` holding a dangling handle that the next progress update would write to — and
        // there is nothing for a user to "close" here anyway: the patch cycle carries on either
        // way, and the window reappears at the next step.
        WM_CLOSE => {
            ShowWindow(hwnd, SW_HIDE);
            0
        }
        _ => DefWindowProcW(hwnd, msg, wparam, lparam),
    }
}
