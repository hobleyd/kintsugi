use std::process::Command;

use anyhow::{Context, Result};

const DELAY_BUTTON: &str = "Delay";
const PATCH_NOW_BUTTON: &str = "Patch Now";

#[derive(Debug, PartialEq, Eq)]
pub enum ConfirmChoice {
    PatchNow,
    Delay,
    /// The dialog was left up long enough that AppleScript's `giving up after` clause dismissed
    /// it on its own. Callers should treat this the same as an explicit `Delay` — the user never
    /// said no to patching, they just weren't there — so ignoring the dialog still counts down
    /// the delay budget rather than leaving it open forever.
    TimedOut,
}

/// Escapes a string for embedding inside a double-quoted AppleScript string literal — this
/// process never controls the application names or notes text it displays (they ultimately come
/// from the backend, which in turn can include whatever a package manager or the AI reported), so
/// this must not be skippable.
fn escape(value: &str) -> String {
    value.replace('\\', "\\\\").replace('"', "\\\"")
}

fn run_osascript(script: &str) -> Result<String> {
    let output = Command::new("osascript")
        .arg("-e")
        .arg(script)
        .output()
        .context("failed to run osascript")?;

    if !output.status.success() {
        anyhow::bail!(
            "osascript exited with {}: {}",
            output.status,
            String::from_utf8_lossy(&output.stderr).trim()
        );
    }

    Ok(String::from_utf8_lossy(&output.stdout).trim().to_string())
}

/// How many application names the dialog lists before summarising the rest. A host that has been
/// offline for a while can legitimately have dozens of pending updates, and an unbounded list
/// would grow the dialog off the bottom of the screen — the count in the opening sentence still
/// states the whole truth. Kept identical in the Windows and Linux agents' dialogs.
const MAX_LISTED_APPS: usize = 10;

/// Composes the dialog body. Split out from the subprocess call so the wording — which is the
/// part that has to stay true to what `patch_cycle` actually found — can be tested directly.
///
/// The application names are listed under the opening sentence rather than only counted: "3
/// application updates are ready" doesn't tell someone deciding whether to delay whether the
/// thing they have open right now is about to be restarted.
fn confirmation_message(delay_label: &str, delays_remaining: u32, app_names: &[String], os_update_available: bool) -> String {
    let what = match (app_names.len(), os_update_available) {
        (0, true) => "A macOS update is".to_string(),
        (n, false) => format!("{n} application update{} {}", if n == 1 { "" } else { "s" }, if n == 1 { "is" } else { "are" }),
        (n, true) => format!("{n} application update{} and a macOS update are", if n == 1 { "" } else { "s" }),
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

/// Shows the confirm-or-delay dialog. When `delays_remaining` is zero, no delay option is
/// offered at all — the caller is expected to show `acknowledge` instead in that case, since
/// there's nothing left to choose between. Only ever called once the caller has already
/// confirmed there's real work — `app_names`/`os_update_available` describe what that is, so the
/// dialog says something concrete rather than a generic "patches are ready".
///
/// `timeout_seconds` bounds how long the dialog stays up before AppleScript dismisses it on its
/// own (`ConfirmChoice::TimedOut`) — otherwise an ignored dialog would sit on screen forever.
/// Callers pass the delay period itself, so leaving it untouched for one delay period behaves
/// exactly like clicking "Delay" once.
pub fn confirm_patch(
    delay_label: &str,
    delays_remaining: u32,
    app_names: &[String],
    os_update_available: bool,
    timeout_seconds: u64,
) -> Result<ConfirmChoice> {
    let delay_button_label = format!("{DELAY_BUTTON} {delay_label} ({delays_remaining} left)");
    let message = confirmation_message(delay_label, delays_remaining, app_names, os_update_available);

    crate::logging::info(&format!("showing patch confirmation dialog ({delays_remaining} delay(s) available)"));

    let script = format!(
        r#"display dialog "{}" with title "Kintsugi Patching" buttons {{"{}", "{}"}} default button "{}" with icon caution giving up after {}"#,
        escape(&message),
        escape(&delay_button_label),
        PATCH_NOW_BUTTON,
        PATCH_NOW_BUTTON,
        timeout_seconds
    );

    let result = run_osascript(&script)?;
    let choice = parse_confirm_result(&result, &delay_button_label);

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

/// Interprets `osascript`'s `display dialog` result text. With `giving up after` present, the
/// result always carries a `gave up:` property; that's checked first, since a timeout still
/// reports `button returned:<default button>` (Patch Now here), which would otherwise be
/// indistinguishable from the user actually clicking it.
fn parse_confirm_result(result: &str, delay_button_label: &str) -> ConfirmChoice {
    if result.contains("gave up:true") {
        ConfirmChoice::TimedOut
    } else if result.contains(&format!("button returned:{delay_button_label}")) {
        ConfirmChoice::Delay
    } else {
        ConfirmChoice::PatchNow
    }
}

/// A single-button dialog for the "no delays left, proceeding regardless" case, and other
/// blocking messages the user must actively dismiss rather than a passive notification banner.
///
/// Takes a `timeout_seconds` cap for the same reason `confirm_patch` does: patching must start
/// once the delay budget is spent regardless of whether anyone is at the keyboard to click "OK".
pub fn acknowledge(message: &str, timeout_seconds: u64) -> Result<()> {
    crate::logging::info(&format!("showing acknowledgement dialog: {message}"));
    let script = format!(
        r#"display dialog "{}" with title "Kintsugi Patching" buttons {{"OK"}} default button "OK" with icon caution giving up after {}"#,
        escape(message),
        timeout_seconds
    );
    run_osascript(&script).map(|_| ())
}

/// Best-effort — a failed notification (e.g. Notification Center is unreachable, or this runs
/// somehow outside a real user session) shouldn't ever be treated as a reason to abort patching.
pub fn notify(title: &str, message: &str) {
    crate::logging::info(&format!("notification: {title} — {message}"));
    let script = format!(
        r#"display notification "{}" with title "{}""#,
        escape(message),
        escape(title)
    );
    if let Err(err) = run_osascript(&script) {
        crate::logging::warn(&format!("could not show notification: {err:#}"));
    }
}

/// A crude but dependency-free progress indicator (AppleScript's `display notification` has no
/// real progress-bar widget) — rendered as a Unicode block bar inside the notification body, so
/// each step's banner at least visually communicates how far through the run it is.
pub fn progress_bar(completed: usize, total: usize) -> String {
    const WIDTH: usize = 20;
    let filled = if total == 0 { 0 } else { (completed * WIDTH) / total };
    let filled = filled.min(WIDTH);
    format!("[{}{}] {completed}/{total}", "█".repeat(filled), "░".repeat(WIDTH - filled))
}

#[cfg(test)]
mod tests {
    use super::*;

    const DELAY_LABEL: &str = "Delay 1 hour(s) (3 left)";

    #[test]
    fn parse_confirm_result_explicit_patch_now() {
        let result = "button returned:Patch Now, gave up:false";
        assert_eq!(parse_confirm_result(result, DELAY_LABEL), ConfirmChoice::PatchNow);
    }

    #[test]
    fn parse_confirm_result_explicit_delay() {
        let result = format!("button returned:{DELAY_LABEL}, gave up:false");
        assert_eq!(parse_confirm_result(&result, DELAY_LABEL), ConfirmChoice::Delay);
    }

    #[test]
    fn parse_confirm_result_timeout_with_default_button_still_selected() {
        // AppleScript reports the default button ("Patch Now") as `button returned` even when
        // the dialog gave up on its own — `gave up:true` must win over that, or an ignored
        // dialog would incorrectly patch immediately instead of counting as a delay.
        let result = "button returned:Patch Now, gave up:true";
        assert_eq!(parse_confirm_result(result, DELAY_LABEL), ConfirmChoice::TimedOut);
    }

    #[test]
    fn parse_confirm_result_with_no_giving_up_clause() {
        let result = "button returned:Patch Now";
        assert_eq!(parse_confirm_result(result, DELAY_LABEL), ConfirmChoice::PatchNow);
    }

    fn names(names: &[&str]) -> Vec<String> {
        names.iter().map(|name| name.to_string()).collect()
    }

    #[test]
    fn confirmation_message_lists_the_affected_applications() {
        let message = confirmation_message("1 hour(s)", 3, &names(&["Firefox", "Slack"]), false);

        assert!(message.contains("2 application updates are ready"), "{message}");
        assert!(message.contains("\n  \u{2022} Firefox\n  \u{2022} Slack\n"), "{message}");
    }

    /// A host that has been offline for a while can have dozens pending; the dialog has to stay
    /// on screen, so past the cap the rest are counted rather than named.
    #[test]
    fn confirmation_message_summarises_the_tail_of_a_long_list() {
        let all: Vec<String> = (1..=14).map(|n| format!("App {n}")).collect();
        let message = confirmation_message("1 hour(s)", 3, &all, false);

        assert!(message.contains("  \u{2022} App 10\n"), "{message}");
        assert!(!message.contains("App 11"), "{message}");
        assert!(message.contains("  \u{2026} and 4 more"), "{message}");
    }

    /// The OS-only case has no applications to list, and must read exactly as it did before the
    /// list existed — no bullet block, and no extra blank line where one would have gone.
    #[test]
    fn confirmation_message_for_an_os_update_alone_carries_no_list() {
        let message = confirmation_message("1 hour(s)", 3, &[], true);

        assert_eq!(
            message,
            "A macOS update is ready to install. This may restart some applications, and could \
             require a reboot.\n\nYou can delay this up to 3 more time(s), 1 hour(s) at a time."
        );
    }

    #[test]
    fn confirmation_message_states_the_remaining_delay_budget() {
        let message = confirmation_message("2 day(s)", 4, &names(&["Firefox"]), false);

        assert!(message.contains("1 application update is ready"), "{message}");
        assert!(message.contains("up to 4 more time(s), 2 day(s) at a time"), "{message}");
    }

    #[test]
    fn confirmation_message_describes_applications_and_an_os_update_together() {
        let message = confirmation_message("1 hour(s)", 3, &names(&["Firefox", "Slack", "Zoom"]), true);

        assert!(message.contains("3 application updates and a macOS update are ready"), "{message}");
        assert!(message.contains("  \u{2022} Zoom"), "{message}");
    }

    #[test]
    fn escape_handles_quotes_and_backslashes() {
        assert_eq!(escape(r#"He said "hi" \ bye"#), r#"He said \"hi\" \\ bye"#);
    }
}
