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

/// What the person at the keyboard said when asked to hand over control of this host.
///
/// **A separate type from [`ConfirmChoice`] on purpose, because a timeout means the opposite
/// thing.** There, nobody answering means "they were not at the desk, so count it as a delay and
/// patch later" — the user never refused, and patching happens regardless. Here, nobody answering
/// means **nobody consented**, and the only safe reading of silence is refusal. Reusing that enum
/// would have left the safe default one careless `match` arm away from handing an unattended
/// desktop to whoever asked. Kept identical to the macOS agent's `RemoteControlChoice`.
#[derive(Debug, PartialEq, Eq)]
pub enum RemoteControlChoice {
    Allow,
    Deny,
    /// The dialog was left up until its own timer dismissed it. Treated exactly as [`Self::Deny`]
    /// by every caller, and kept distinct only so the audit record can tell an empty desk from a
    /// deliberate refusal.
    TimedOut,
}

const ALLOW_BUTTON: &str = "Allow";
const DENY_BUTTON: &str = "Deny";

/// Composes the consent dialog's text. Split out so the wording — the part somebody has to make a
/// decision from — can be tested without a window.
///
/// It names the administrator, says plainly what is being granted, and says how to end it. All
/// three matter: a dialog reading "allow remote access?" with no name is one people click through,
/// and one that does not mention the notification area leaves someone who regrets it with no
/// visible way out.
fn remote_control_message(requested_by: &str, restrictions: &[String]) -> String {
    let mut message = format!(
        "{requested_by} is asking to control this computer remotely.\r\n\r\n\
         If you allow this, they will see your screen and be able to use your keyboard and mouse as \
         though they were sitting here. A bar will appear at the top of the screen for as long as \
         the session lasts, with an \"End session\" button you can press at any time.\r\n"
    );

    if !restrictions.is_empty() {
        message.push_str("\r\n");
        for restriction in restrictions {
            message.push_str(&format!("  \u{2022} {restriction}\r\n"));
        }
    }

    message
}

/// Asks the host user to hand over control, and returns what they said.
///
/// **Deny is listed first, which makes it the default button** — `show_dialog` gives index 0
/// `BS_DEFPUSHBUTTON`. That is deliberate in the one dialog where getting it wrong hands over
/// somebody's desktop: pressing Return or Space on a prompt that appeared unannounced refuses.
///
/// It also puts Deny in the rightmost position, because that implementation lays buttons out
/// right-to-left from index 0, and rightmost is where Windows conventionally puts the *primary*
/// action. That is a real cost and it is accepted rather than overlooked: the two properties are
/// coupled in `show_dialog`, and of the pair, "the button under a habitual click refuses" is worth
/// more here than "the button in the usual place is the affirmative one". Decoupling them would
/// mean a `default_index` parameter on a dialog helper shared with the patching flow, for one
/// caller.
pub fn confirm_remote_control(
    requested_by: &str,
    restrictions: &[String],
    timeout_seconds: u64,
) -> Result<RemoteControlChoice> {
    crate::logging::info(&format!("asking the console user to approve remote control for {requested_by}"));

    let message = remote_control_message(requested_by, restrictions);
    let choice = match show_dialog(&message, &[DENY_BUTTON, ALLOW_BUTTON], timeout_seconds)? {
        Some(0) => RemoteControlChoice::Deny,
        Some(_) => RemoteControlChoice::Allow,
        None => RemoteControlChoice::TimedOut,
    };

    crate::logging::info(&format!(
        "console user chose: {}",
        match choice {
            RemoteControlChoice::Allow => "allow remote control",
            RemoteControlChoice::Deny => "deny remote control",
            RemoteControlChoice::TimedOut => "timed out (treated as a refusal)",
        }
    ));

    Ok(choice)
}

/// Shows the confirm-or-delay dialog. When `delays_remaining` is zero, no delay option is offered
/// at all — the caller is expected to show `acknowledge` instead in that case, since there's
/// nothing left to choose between. Only ever called once the caller has already confirmed there's
/// real work — `app_names`/`os_update_available` describe what that is, so the dialog says
/// something concrete rather than a generic "patches are ready".
///
/// `timeout_seconds` bounds how long the dialog stays up before dismissing itself
/// (`ConfirmChoice::TimedOut`) — otherwise an ignored dialog would sit on screen forever. Callers
/// pass the delay period itself, so leaving it untouched for one delay period behaves exactly like
/// clicking "Delay" once.
pub fn confirm_patch(
    delay_label: &str,
    delays_remaining: u32,
    app_names: &[String],
    os_update_available: bool,
    timeout_seconds: u64,
) -> Result<ConfirmChoice> {
    let delay_button_label = format!("{DELAY_BUTTON} {delay_label} ({delays_remaining} left)");
    let message = confirm_message(delay_label, delays_remaining, app_names, os_update_available);

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

/// How many application names the dialog lists before summarising the rest. A host that has been
/// offline for a while can legitimately have dozens of pending updates, and an unbounded list would
/// grow the dialog off the bottom of the screen — the count in the opening sentence still states the
/// whole truth. Kept identical in the macOS and Linux agents' dialogs.
const MAX_LISTED_APPS: usize = 10;

/// The dialog's body text. Pure, so the phrasing across the four singular/plural/OS combinations is
/// testable without putting a window on screen.
///
/// The application names are listed under the opening sentence rather than only counted: "3
/// application updates are ready" doesn't tell someone deciding whether to delay whether the thing
/// they have open right now is about to be restarted. The list is also what makes the window's
/// height depend on its content — see `window_height`.
fn confirm_message(delay_label: &str, delays_remaining: u32, app_names: &[String], os_update_available: bool) -> String {
    let what = match (app_names.len(), os_update_available) {
        (0, true) => "A Windows update is".to_string(),
        (n, false) => format!("{n} application update{} {}", if n == 1 { "" } else { "s" }, if n == 1 { "is" } else { "are" }),
        (n, true) => format!("{n} application update{} and a Windows update are", if n == 1 { "" } else { "s" }),
    };

    let mut message = format!(
        "{what} ready to install. This may restart some applications, and could require a \
         reboot.\n"
    );

    if !app_names.is_empty() {
        message.push('\n');
        for name in app_names.iter().take(MAX_LISTED_APPS) {
            message.push_str(&format!("  \u{2022} {name}\n"));
        }
        if app_names.len() > MAX_LISTED_APPS {
            message.push_str(&format!("  \u{2026} and {} more\n", app_names.len() - MAX_LISTED_APPS));
        }
    }

    message.push_str(&format!(
        "\nYou can delay this up to {delays_remaining} more time(s), {delay_label} at a time."
    ));
    message
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
/// The floor, not the size: `window_height` grows the window when the message lists enough
/// applications to need the room, so a long list scrolls off nothing.
const MIN_HEIGHT: i32 = 210;
const BUTTON_HEIGHT: i32 = 28;
/// One line of the default UI font plus its leading. Used only to size the window, never to
/// position anything — the static control lays the text out itself.
const LINE_HEIGHT: i32 = 18;
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
    let height = window_height(message);

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
            height,
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
            height - MARGIN * 3 - BUTTON_HEIGHT,
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
                height - MARGIN * 2 - BUTTON_HEIGHT,
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

        center_on_screen(hwnd, WIDTH, height);
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

/// How tall the window has to be for `message` to fit. The macOS and Linux agents get this for
/// free — `osascript` and zenity both size a dialog to its text — but a Win32 window is created at
/// an explicit size, so listing the applications means computing one.
///
/// The line count is estimated rather than measured: measuring would mean a device context and the
/// dialog font before the window exists, to decide the size of the window. `CHARS_PER_LINE` is the
/// label's width divided by a conservative average character width, so an over-long line is counted
/// as the two or three the static control will wrap it into. Erring high costs blank space at the
/// bottom; erring low would clip the text, so the estimate is deliberately pessimistic.
fn window_height(message: &str) -> i32 {
    const CHARS_PER_LINE: usize = 52;

    let lines: usize = message
        .lines()
        .map(|line| line.chars().count().max(1).div_ceil(CHARS_PER_LINE))
        .sum();

    (lines as i32 * LINE_HEIGHT + MARGIN * 3 + BUTTON_HEIGHT).max(MIN_HEIGHT)
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
    #[test]
    fn remote_control_message_names_who_is_asking_and_how_to_stop_it() {
        let message = remote_control_message("admin@example.com", &[]);

        assert!(message.contains("admin@example.com is asking to control this computer"), "{message}");
        assert!(message.contains("keyboard and mouse"), "{message}");
        // Somebody who regrets allowing it needs to know there is a way out, and it has to name the
        // thing that actually exists — the tray item this used to point at is gone, replaced by the
        // session helper's own banner.
        assert!(message.contains("End session"), "{message}");
        assert!(message.contains("top of the screen"), "{message}");
    }

    #[test]
    fn remote_control_message_lists_what_the_session_cannot_do() {
        let message = remote_control_message(
            "admin@example.com",
            &["Elevated windows cannot be clicked.".to_string()],
        );

        assert!(message.contains("\u{2022} Elevated windows cannot be clicked."), "{message}");
    }

    #[test]
    fn remote_control_uses_windows_line_endings_like_every_other_dialog_here() {
        // The dialog is a Win32 static control, which renders a bare \n as a box rather than a
        // break — the existing confirm_message has the same requirement.
        let message = remote_control_message("admin@example.com", &[]);

        assert!(message.contains("\r\n"), "{message}");
        assert!(!message.replace("\r\n", "").contains('\n'), "{message}");
    }

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

    fn names(names: &[&str]) -> Vec<String> {
        names.iter().map(|name| name.to_string()).collect()
    }

    #[test]
    fn confirm_message_reads_correctly_for_a_single_application() {
        let message = confirm_message("2 hour(s)", 3, &names(&["Firefox"]), false);

        assert!(message.contains("1 application update is ready"), "{message}");
    }

    #[test]
    fn confirm_message_reads_correctly_for_several_applications() {
        let message = confirm_message("2 hour(s)", 3, &names(&["Firefox", "Slack", "Zoom", "7-Zip"]), false);

        assert!(message.contains("4 application updates are ready"), "{message}");
    }

    #[test]
    fn confirm_message_mentions_windows_updates_rather_than_macos_ones() {
        let message = confirm_message("2 hour(s)", 3, &[], true);

        assert!(message.contains("A Windows update is ready"), "{message}");
    }

    #[test]
    fn confirm_message_covers_applications_and_the_os_together() {
        let message = confirm_message("1 day(s)", 2, &names(&["Firefox", "Slack", "Zoom"]), true);

        assert!(message.contains("3 application updates and a Windows update are ready"), "{message}");
        assert!(message.contains("up to 2 more time(s), 1 day(s) at a time"), "{message}");
    }

    #[test]
    fn confirm_message_lists_the_affected_applications() {
        let message = confirm_message("2 hour(s)", 3, &names(&["Firefox", "Slack"]), false);

        assert!(message.contains("\n  \u{2022} Firefox\n  \u{2022} Slack\n"), "{message}");
    }

    /// A host that has been offline for a while can have dozens pending; the dialog has to stay on
    /// screen, so past the cap the rest are counted rather than named.
    #[test]
    fn confirm_message_summarises_the_tail_of_a_long_list() {
        let all: Vec<String> = (1..=14).map(|n| format!("App {n}")).collect();
        let message = confirm_message("2 hour(s)", 3, &all, false);

        assert!(message.contains("  \u{2022} App 10\n"), "{message}");
        assert!(!message.contains("App 11"), "{message}");
        assert!(message.contains("  \u{2026} and 4 more"), "{message}");
    }

    /// The OS-only case has no applications to list, and must read exactly as it did before the
    /// list existed — no bullet block, and no extra blank line where one would have gone.
    #[test]
    fn confirm_message_for_an_os_update_alone_carries_no_list() {
        let message = confirm_message("2 hour(s)", 3, &[], true);

        assert_eq!(
            message,
            "A Windows update is ready to install. This may restart some applications, and could \
             require a reboot.\n\nYou can delay this up to 3 more time(s), 2 hour(s) at a time."
        );
    }

    /// The window was a fixed 210px before it listed anything; a message that fits in that must
    /// still get exactly that, or every existing dialog changes size for no reason.
    #[test]
    fn window_height_stays_at_the_minimum_for_a_short_message() {
        assert_eq!(window_height(&confirm_message("2 hour(s)", 3, &[], true)), MIN_HEIGHT);
    }

    /// Each listed application has to buy its own line, or the last few would be drawn outside the
    /// window and the user would approve a patch run they can't fully see.
    #[test]
    fn window_height_grows_with_the_listed_applications() {
        let short = window_height(&confirm_message("2 hour(s)", 3, &names(&["Firefox"]), false));
        let long = window_height(&confirm_message("2 hour(s)", 3, &(1..=10).map(|n| format!("App {n}")).collect::<Vec<_>>(), false));

        assert!(long >= short + 9 * LINE_HEIGHT, "{short} -> {long}");
    }

    /// A single long name wraps in the static control, so it has to be counted as the several
    /// lines it will actually occupy rather than as one.
    #[test]
    fn window_height_counts_a_wrapped_line_more_than_once() {
        let wide = window_height(&confirm_message("2 hour(s)", 3, &names(&["x".repeat(300).as_str()]), false));
        let narrow = window_height(&confirm_message("2 hour(s)", 3, &names(&["x"]), false));

        assert!(wide >= narrow + 5 * LINE_HEIGHT, "{narrow} -> {wide}");
    }
}
