use std::cell::RefCell;

use anyhow::Result;
use windows_sys::Win32::Foundation::{HWND, LPARAM, WPARAM};
use windows_sys::Win32::Graphics::Gdi::{GetSysColorBrush, COLOR_BTNFACE};
use windows_sys::Win32::UI::WindowsAndMessaging::{
    CreateWindowExW, DefWindowProcW, DestroyWindow, DispatchMessageW, GetMessageW, KillTimer, PostQuitMessage, SetForegroundWindow, SetTimer,
    ShowWindow, TranslateMessage, BS_DEFPUSHBUTTON, BS_PUSHBUTTON, MSG, SW_SHOW, WM_COMMAND, WM_DESTROY, WM_TIMER, WS_CAPTION,
    WS_CHILD, WS_EX_TOPMOST, WS_SYSMENU, WS_TABSTOP, WS_VISIBLE,
};

use crate::win32::{apply_default_font, center_on_screen, instance, register_class, wide, WindowClass};

/// `SS_LEFT` — a static control's default, left-aligned text style. Spelled out here because
/// windows-sys doesn't surface the `SS_*` constants; it is `0x0000_0000` in the Windows SDK, so
/// naming it costs nothing and says what the flag means at the call site.
const SS_LEFT: u32 = 0x0000_0000;

const DELAY_BUTTON: &str = "Delay";
const PATCH_NOW_BUTTON: &str = "Patch Now";

#[derive(Debug, PartialEq, Eq)]
pub enum ConfirmChoice {
    PatchNow,
    Delay,
    /// The dialog was left up long enough that its own timer dismissed it. Callers should treat
    /// this the same as an explicit `Delay` — the user never said no to patching, they just
    /// weren't there — so ignoring the dialog still counts down the delay budget rather than
    /// leaving it open forever.
    TimedOut,
}

/// Shows the confirm-or-delay dialog. When `delays_remaining` is zero, no delay option is offered
/// at all — the caller is expected to show `acknowledge` instead in that case, since there's
/// nothing left to choose between. Only ever called once the caller has already confirmed there's
/// real work — `app_count`/`os_update_available` describe what that is, so the dialog says
/// something concrete rather than a generic "patches are ready".
///
/// `timeout_seconds` bounds how long the dialog stays up before dismissing itself
/// (`ConfirmChoice::TimedOut`) — otherwise an ignored dialog would sit on screen forever. Callers
/// pass the delay period itself, so leaving it untouched for one delay period behaves exactly like
/// clicking "Delay" once.
pub fn confirm_patch(
    delay_label: &str,
    delays_remaining: u32,
    app_count: usize,
    os_update_available: bool,
    timeout_seconds: u64,
) -> Result<ConfirmChoice> {
    let delay_button_label = format!("{DELAY_BUTTON} {delay_label} ({delays_remaining} left)");
    let message = confirm_message(delay_label, delays_remaining, app_count, os_update_available);

    crate::logging::info(&format!("showing patch confirmation dialog ({delays_remaining} delay(s) available)"));

    // "Patch Now" first so it's the default (index 0 below) — the same default the macOS agent's
    // AppleScript dialog uses, for the same reason: pressing Enter should proceed, not postpone.
    let choice = match show_dialog(&message, &[PATCH_NOW_BUTTON, &delay_button_label], timeout_seconds)? {
        Some(0) => ConfirmChoice::PatchNow,
        Some(_) => ConfirmChoice::Delay,
        None => ConfirmChoice::TimedOut,
    };

    crate::logging::info(&format!(
        "user chose: {}",
        match choice {
            ConfirmChoice::Delay => "delay",
            ConfirmChoice::PatchNow => "patch now",
            ConfirmChoice::TimedOut => "timed out (treated as delay)",
        }
    ));

    Ok(choice)
}

/// The dialog's body text. Pure, so the phrasing across the four singular/plural/OS combinations is
/// testable without putting a window on screen.
fn confirm_message(delay_label: &str, delays_remaining: u32, app_count: usize, os_update_available: bool) -> String {
    let what = match (app_count, os_update_available) {
        (0, true) => "A Windows update is".to_string(),
        (n, false) => format!("{n} application update{} {}", if n == 1 { "" } else { "s" }, if n == 1 { "is" } else { "are" }),
        (n, true) => format!("{n} application update{} and a Windows update are", if n == 1 { "" } else { "s" }),
    };

    format!(
        "{what} ready to install. This may restart some applications, and could require a \
         reboot.\n\nYou can delay this up to {delays_remaining} more time(s), {delay_label} at a time."
    )
}

/// A single-button dialog for the "no delays left, proceeding regardless" case, and other blocking
/// messages the user must actively dismiss rather than a passive notification.
///
/// Takes a `timeout_seconds` cap for the same reason `confirm_patch` does: patching must start once
/// the delay budget is spent regardless of whether anyone is at the keyboard to click "OK".
pub fn acknowledge(message: &str, timeout_seconds: u64) -> Result<()> {
    crate::logging::info(&format!("showing acknowledgement dialog: {message}"));
    show_dialog(message, &["OK"], timeout_seconds).map(|_| ())
}

/// Best-effort — a failed notification shouldn't ever be treated as a reason to abort patching.
///
/// Delivered as a balloon on this agent's own notification-area icon (see `tray_menu::notify`),
/// which is both the platform's equivalent of macOS's `display notification` and the only route
/// available: a toast raised any other way needs a registered AppUserModelID and a Start Menu
/// shortcut to appear at all.
pub fn notify(title: &str, message: &str) {
    crate::logging::info(&format!("notification: {title} — {message}"));
    crate::tray_menu::notify(title, message);
}

/// A crude but dependency-free progress indicator, rendered as a Unicode block bar inside the
/// notification body — so each step's balloon at least visually communicates how far through the
/// run it is. The window (see `progress_window`) shows a real progress bar; this is for the
/// notification text, which is plain text only.
pub fn progress_bar(completed: usize, total: usize) -> String {
    const WIDTH: usize = 20;
    let filled = if total == 0 { 0 } else { (completed * WIDTH) / total };
    let filled = filled.min(WIDTH);
    format!("[{}{}] {completed}/{total}", "█".repeat(filled), "░".repeat(WIDTH - filled))
}

// ---------------------------------------------------------------------------------------------
// The Win32 dialog itself.
//
// Hand-built rather than `MessageBoxW`, for one reason: a message box has no timeout, and this
// dialog *must* dismiss itself. Its whole contract is that leaving it alone for one delay period
// counts as one delay (see ConfirmChoice::TimedOut) — a dialog that waits forever would stall the
// patch cycle on any machine whose user walked away, indefinitely. (`MessageBoxTimeoutW` exists but
// is undocumented and unexported from any import library, so it isn't a real option.)
// ---------------------------------------------------------------------------------------------

const CLASS_NAME: &str = "KintsugiAgentDialog";
const TIMER_ID: usize = 1;
const WIDTH: i32 = 460;
const HEIGHT: i32 = 210;
const BUTTON_HEIGHT: i32 = 28;
const BUTTON_GAP: i32 = 10;
const MARGIN: i32 = 16;

// The outcome of the dialog currently being shown on *this* thread.
//
// Thread-local rather than a global: `confirm_patch` is called from the scheduler thread while the
// notification-area icon's own message loop runs on the main thread, so two windows belonging to
// two threads exist at once. A shared global would let one clobber the other's result.
thread_local! {
    static RESULT: RefCell<Option<usize>> = const { RefCell::new(None) };
}

/// Shows a modal dialog with `buttons` (index 0 is the default) and blocks until one is clicked or
/// `timeout_seconds` elapses. Returns the clicked index, or `None` on timeout.
fn show_dialog(message: &str, buttons: &[&str], timeout_seconds: u64) -> Result<Option<usize>> {
    let class = dialog_class();

    RESULT.with(|result| *result.borrow_mut() = None);

    // SAFETY: every pointer below is either null or a live, NUL-terminated buffer that outlives the
    // call. The window and its children are destroyed before this function returns, and the message
    // loop that drives them runs on this same thread.
    unsafe {
        let title = wide("Kintsugi Patching");
        let hwnd = CreateWindowExW(
            WS_EX_TOPMOST,
            class.name(),
            title.as_ptr(),
            // Deliberately no WS_MAXIMIZEBOX/WS_THICKFRAME: a fixed-size dialog, and no close box
            // either (no WS_SYSMENU close is offered beyond the frame) so the only ways out are a
            // button or the timeout — dismissing it with no answer would be ambiguous.
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

        if hwnd.is_null() {
            anyhow::bail!("could not create the patching dialog window");
        }

        let text = wide(message);
        let static_class = wide("STATIC");
        let label = CreateWindowExW(
            0,
            static_class.as_ptr(),
            text.as_ptr(),
            WS_CHILD | WS_VISIBLE | SS_LEFT,
            MARGIN,
            MARGIN,
            WIDTH - MARGIN * 3,
            HEIGHT - MARGIN * 3 - BUTTON_HEIGHT,
            hwnd,
            std::ptr::null_mut(),
            instance(),
            std::ptr::null(),
        );
        apply_default_font(label);

        // Laid out right to left from the bottom-right corner, which puts the default (index 0)
        // rightmost — the position Windows puts a primary action in.
        let button_class = wide("BUTTON");
        let mut right_edge = WIDTH - MARGIN * 2;
        for (index, caption) in buttons.iter().enumerate() {
            // Roughly 8px per character plus padding: enough for "Delay 2 hour(s) (3 left)" without
            // measuring text, which would mean a device context and a font just for this.
            let button_width = (caption.chars().count() as i32 * 8 + 32).max(90);
            let caption_wide = wide(caption);
            let style = if index == 0 { BS_DEFPUSHBUTTON } else { BS_PUSHBUTTON };
            let button = CreateWindowExW(
                0,
                button_class.as_ptr(),
                caption_wide.as_ptr(),
                WS_CHILD | WS_VISIBLE | WS_TABSTOP | style as u32,
                right_edge - button_width,
                HEIGHT - MARGIN * 2 - BUTTON_HEIGHT,
                button_width,
                BUTTON_HEIGHT,
                hwnd,
                // The control id is the button's index, which is what WM_COMMAND reports back and
                // so what identifies the answer.
                index as isize as _,
                instance(),
                std::ptr::null(),
            );
            apply_default_font(button);
            right_edge -= button_width + BUTTON_GAP;
        }

        center_on_screen(hwnd, WIDTH, HEIGHT);
        ShowWindow(hwnd, SW_SHOW);
        // Without this the dialog can open behind whatever the user is working in — for a message
        // that is about to interrupt them, silently appearing behind their work is the same as not
        // appearing.
        SetForegroundWindow(hwnd);

        // Clamped to what SetTimer can express (a u32 of milliseconds, ~49 days) — a policy could
        // legitimately set a multi-day delay period, and an overflow would fire the timer
        // immediately, turning every dialog into an instant timeout.
        let timeout_ms = timeout_seconds.saturating_mul(1000).min(u64::from(u32::MAX - 1)) as u32;
        SetTimer(hwnd, TIMER_ID, timeout_ms, None);

        run_modal_loop();

        KillTimer(hwnd, TIMER_ID);
        // Destroying the parent destroys its children with it.
        DestroyWindow(hwnd);
    }

    Ok(RESULT.with(|result| *result.borrow()))
}

/// Runs this thread's message loop until the dialog's window procedure posts a quit message.
///
/// # Safety
///
/// Must be called on the thread that created the dialog window — a message loop only ever pumps
/// messages for windows owned by its own thread.
unsafe fn run_modal_loop() {
    let mut msg: MSG = std::mem::zeroed();
    // GetMessageW returns 0 for WM_QUIT and -1 on error; anything else is a message to dispatch.
    while GetMessageW(&mut msg, std::ptr::null_mut(), 0, 0) > 0 {
        TranslateMessage(&msg);
        DispatchMessageW(&msg);
    }
}

fn dialog_class() -> &'static WindowClass {
    use std::sync::OnceLock;
    static CLASS: OnceLock<WindowClass> = OnceLock::new();

    CLASS.get_or_init(|| {
        // SAFETY: dialog_wnd_proc is a valid window procedure with the required signature, and the
        // class is registered exactly once for the life of the process.
        unsafe { register_class(CLASS_NAME, dialog_wnd_proc, GetSysColorBrush(COLOR_BTNFACE) as _) }
    })
}


unsafe extern "system" fn dialog_wnd_proc(hwnd: HWND, msg: u32, wparam: WPARAM, lparam: LPARAM) -> isize {
    match msg {
        WM_COMMAND => {
            // The low word of wparam is the control id, which is the button's index (see the
            // creation loop above). The high word is the notification code; a click is BN_CLICKED
            // (0), and nothing else here sends WM_COMMAND, so it doesn't need distinguishing.
            let control_id = (wparam & 0xFFFF) as usize;
            RESULT.with(|result| *result.borrow_mut() = Some(control_id));
            PostQuitMessage(0);
            0
        }
        WM_TIMER if wparam == TIMER_ID => {
            // Left as None — the caller reads that as "nobody answered", which counts as a delay.
            PostQuitMessage(0);
            0
        }
        WM_DESTROY => {
            PostQuitMessage(0);
            0
        }
        _ => DefWindowProcW(hwnd, msg, wparam, lparam),
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn progress_bar_is_empty_at_the_start_and_full_at_the_end() {
        assert!(progress_bar(0, 4).starts_with("[░"));
        assert_eq!(progress_bar(4, 4), format!("[{}] 4/4", "█".repeat(20)));
    }

    #[test]
    fn progress_bar_handles_a_zero_total_without_dividing_by_zero() {
        // Reached during the 5-minute warning, before there's anything concrete to count.
        assert_eq!(progress_bar(0, 0), format!("[{}] 0/0", "░".repeat(20)));
    }

    #[test]
    fn progress_bar_never_overflows_its_width() {
        // A completed count above the total shouldn't be able to produce a bar wider than the
        // fixed width (which would wrap the notification text).
        let bar = progress_bar(9, 4);

        assert!(bar.starts_with(&format!("[{}]", "█".repeat(20))));
    }

    #[test]
    fn confirm_message_reads_correctly_for_a_single_application() {
        let message = confirm_message("2 hour(s)", 3, 1, false);

        assert!(message.contains("1 application update is ready"), "{message}");
    }

    #[test]
    fn confirm_message_reads_correctly_for_several_applications() {
        let message = confirm_message("2 hour(s)", 3, 4, false);

        assert!(message.contains("4 application updates are ready"), "{message}");
    }

    #[test]
    fn confirm_message_mentions_windows_updates_rather_than_macos_ones() {
        let message = confirm_message("2 hour(s)", 3, 0, true);

        assert!(message.contains("A Windows update is ready"), "{message}");
    }

    #[test]
    fn confirm_message_covers_applications_and_the_os_together() {
        let message = confirm_message("1 day(s)", 2, 3, true);

        assert!(message.contains("3 application updates and a Windows update are ready"), "{message}");
        assert!(message.contains("up to 2 more time(s), 1 day(s) at a time"), "{message}");
    }
}
