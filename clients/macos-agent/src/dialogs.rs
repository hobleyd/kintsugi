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

/// Shows the confirm-or-delay dialog. When `delays_remaining` is zero, no delay option is
/// offered at all — the caller is expected to show `acknowledge` instead in that case, since
/// there's nothing left to choose between. Only ever called once the caller has already
/// confirmed there's real work — `app_count`/`os_update_available` describe what that is, so the
/// dialog says something concrete rather than a generic "patches are ready".
///
/// `timeout_seconds` bounds how long the dialog stays up before AppleScript dismisses it on its
/// own (`ConfirmChoice::TimedOut`) — otherwise an ignored dialog would sit on screen forever.
/// Callers pass the delay period itself, so leaving it untouched for one delay period behaves
/// exactly like clicking "Delay" once.
pub fn confirm_patch(
    delay_label: &str,
    delays_remaining: u32,
    app_count: usize,
    os_update_available: bool,
    timeout_seconds: u64,
) -> Result<ConfirmChoice> {
    let delay_button_label = format!("{DELAY_BUTTON} {delay_label} ({delays_remaining} left)");

    let what = match (app_count, os_update_available) {
        (0, true) => "A macOS update is".to_string(),
        (n, false) => format!("{n} application update{} {}", if n == 1 { "" } else { "s" }, if n == 1 { "is" } else { "are" }),
        (n, true) => format!("{n} application update{} and a macOS update are", if n == 1 { "" } else { "s" }),
    };
    let message = format!(
        "{what} ready to install. This may restart some applications, and could require a \
         reboot.\n\nYou can delay this up to {delays_remaining} more time(s), {delay_label} at a time."
    );

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

    #[test]
    fn escape_handles_quotes_and_backslashes() {
        assert_eq!(escape(r#"He said "hi" \ bye"#), r#"He said \"hi\" \\ bye"#);
    }
}
