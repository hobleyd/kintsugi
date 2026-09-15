use std::process::Command;

use anyhow::{Context, Result};

const DELAY_BUTTON: &str = "Delay";
const PATCH_NOW_BUTTON: &str = "Patch Now";

#[derive(Debug, PartialEq, Eq)]
pub enum ConfirmChoice {
    PatchNow,
    Delay,
    /// The dialog was left up long enough that AppleScript's `giving up after` clause dismissed
    /// it on its own.
    ///
    /// It counts against the delay budget — the user was asked and said nothing — but **not** the
    /// way an explicit `Delay` does: the dialog stands there for a whole delay period, so that
    /// period is already spent by the time it gives up, and postponing by another one on top would
    /// count the budget down half as fast as the policy says. See
    /// `ScheduleState::register_unanswered_prompt`.
    TimedOut,
}

/// Escapes a string for embedding inside a double-quoted AppleScript string literal — this
/// process never controls the application names or notes text it displays (they ultimately come
/// from the backend, which in turn can include whatever a package manager or the AI reported), so
/// this must not be skippable.
fn escape(value: &str) -> String {
    value.replace('\\', "\\\\").replace('"', "\\\"")
}

/// Upper-cases the first character so a phrase built for mid-sentence use ("a macOS 26.7 update")
/// can also open one. ASCII-only by construction — the strings it is given are composed above out
/// of literals and a `softwareupdate` version number.
fn capitalize_first(value: &str) -> String {
    let mut chars = value.chars();
    match chars.next() {
        Some(first) => first.to_uppercase().collect::<String>() + chars.as_str(),
        None => String::new(),
    }
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
fn confirmation_message(
    delay_label: &str,
    delays_remaining: u32,
    app_names: &[String],
    os_update_available: bool,
    os_update_version: Option<&str>,
) -> String {
    // Named rather than counted, when macOS told us the version: `-i -a` installs everything
    // applicable, so "a macOS update" can mean a point release or a whole new major version, and
    // somebody deciding whether to delay is entitled to know which. See
    // `install_password_message`, which names it for the same reason.
    let the_os_update = match os_update_version {
        Some(version) => format!("a macOS {version} update"),
        None => "a macOS update".to_string(),
    };

    let what = match (app_names.len(), os_update_available) {
        (0, true) => format!("{} is", capitalize_first(&the_os_update)),
        (n, false) => format!("{n} application update{} {}", if n == 1 { "" } else { "s" }, if n == 1 { "is" } else { "are" }),
        (n, true) => format!("{n} application update{} and {the_os_update} are", if n == 1 { "" } else { "s" }),
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
/// Callers pass the delay period itself, so an ignored dialog has spent one whole delay period by
/// the time it gives up — which is why that outcome counts the budget down without postponing the
/// cycle any further.
pub fn confirm_patch(
    delay_label: &str,
    delays_remaining: u32,
    app_names: &[String],
    os_update_available: bool,
    os_update_version: Option<&str>,
    timeout_seconds: u64,
) -> Result<ConfirmChoice> {
    let delay_button_label = format!("{DELAY_BUTTON} {delay_label} ({delays_remaining} left)");
    let message = confirmation_message(delay_label, delays_remaining, app_names, os_update_available, os_update_version);

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
            ConfirmChoice::TimedOut => "timed out (counts as a delay, and is asked again at once)",
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

/// What the person at the keyboard said when asked to hand over control of their Mac.
///
/// **A separate type from [`ConfirmChoice`] on purpose, because the polarity of a timeout is the
/// opposite.** There, nobody answering means "they were not at the desk, so count it as a delay and
/// patch later" — the user never said no, and patching is going to happen regardless. Here, nobody
/// answering means **nobody consented**, and the only safe reading of silence is refusal. Reusing
/// that enum would have made the safe default one careless `match` arm away from letting an
/// unattended Mac be taken over.
#[derive(Debug, PartialEq, Eq)]
pub enum RemoteControlChoice {
    Allow,
    Deny,
    /// The dialog was left up until AppleScript dismissed it. Treated exactly as [`Self::Deny`] by
    /// every caller, and kept distinct only so the audit record can tell an empty desk from a
    /// deliberate refusal.
    TimedOut,
}

const ALLOW_BUTTON: &str = "Allow";
const DENY_BUTTON: &str = "Deny";

/// Composes the consent dialog's text. Split out from the subprocess call so the wording — the part
/// somebody has to be able to make a decision from — can be tested directly.
///
/// It names the administrator, says what is being granted in plain terms, and says how to end it.
/// All three matter: a dialog reading "allow remote access?" with no name is one people click
/// through, and one that does not mention the menu bar leaves someone who regrets it with no
/// visible way out.
fn remote_control_message(requested_by: &str, restrictions: &[String]) -> String {
    let mut message = format!(
        "{requested_by} is asking to control this Mac remotely.\n\n\
         If you allow this, they will see your screen and be able to use your keyboard and mouse \
         as though they were sitting here. You can end the session at any time from the Kintsugi \
         icon in the menu bar.\n"
    );

    // Said out loud rather than discovered once the session is running and half of it does not
    // work — see screen_capture and input_injection on why either permission can be missing.
    if !restrictions.is_empty() {
        message.push('\n');
        for restriction in restrictions {
            message.push_str(&format!("  \u{2022} {restriction}\n"));
        }
    }

    message
}

/// Asks the host user to hand over control, and returns what they said.
///
/// `restrictions` is anything the session will not be able to do (see
/// `remote_control::describe_restrictions`), listed in the dialog so the decision is an informed
/// one.
///
/// The default button is **Deny**, deliberately. AppleScript reports `button returned:<default
/// button>` even when it dismissed the dialog itself, so making Allow the default would mean a
/// timeout arriving as an apparent click on Allow — and while `parse_remote_control_result` checks
/// `gave up:` first and would catch that, a safe default is worth having in the one dialog where
/// getting it wrong hands over somebody's desktop. Pressing Return also refuses, which is the right
/// way round for a prompt that appears unannounced.
pub fn confirm_remote_control(requested_by: &str, restrictions: &[String], timeout_seconds: u64) -> Result<RemoteControlChoice> {
    crate::logging::info(&format!("asking the console user to approve remote control for {requested_by}"));

    let script = format!(
        r#"display dialog "{}" with title "Kintsugi Remote Control" buttons {{"{}", "{}"}} default button "{}" with icon caution giving up after {}"#,
        escape(&remote_control_message(requested_by, restrictions)),
        DENY_BUTTON,
        ALLOW_BUTTON,
        DENY_BUTTON,
        timeout_seconds
    );

    let result = run_osascript(&script)?;
    let choice = parse_remote_control_result(&result);

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

/// Interprets the consent dialog's result. `gave up:` is checked first for the same reason
/// `parse_confirm_result` checks it first, and the fallback is `Deny` rather than `Allow` — an
/// unparseable answer must never be read as consent.
fn parse_remote_control_result(result: &str) -> RemoteControlChoice {
    if result.contains("gave up:true") {
        RemoteControlChoice::TimedOut
    } else if result.contains(&format!("button returned:{ALLOW_BUTTON}")) {
        RemoteControlChoice::Allow
    } else {
        RemoteControlChoice::Deny
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

/// What came back from the authorization prompt.
///
/// **A separate type from [`ConfirmChoice`], and the polarity of a timeout is the opposite** — the
/// same reason [`RemoteControlChoice`] is its own enum. There, nobody answering means "they were
/// not at the desk, count it as a delay, patch later": the update is going to happen regardless.
/// Here, nobody answering means there is no password, and there is nothing useful to do with that
/// but **skip the OS update entirely**. Falling through to an unauthorized install instead would
/// download several gigabytes on an unattended Mac and fail at the end of it, every single cycle.
/// Reusing `ConfirmChoice` would have put that behaviour one careless `match` arm away.
#[derive(Debug, PartialEq, Eq)]
pub enum PasswordAnswer {
    /// The user authorized. Never logged, never rendered — see `os_update::InstallAuth`.
    Provided(String),
    /// Cancel, or an empty box. Treated as a refusal.
    Cancelled,
    /// Nobody was there. Treated as a refusal, and kept distinct only so the log can tell an empty
    /// desk from a deliberate "not now".
    TimedOut,
}

const AUTHORIZE_BUTTON: &str = "Authorize";
const CANCEL_BUTTON: &str = "Cancel";

/// Composes the authorization prompt. Split out from the subprocess call for the same reason every
/// other message in this file is — the wording is the part somebody has to make a decision from.
///
/// It names the version, and that is not decoration: `softwareupdate -i -a` installs everything
/// applicable, which on a host offered both a point release and a major upgrade means the major
/// one. "A macOS update is ready" would be a fair description of a 2.9GB point release and a
/// misleading one of an 11.7GB new major version, and the difference is exactly what somebody being
/// asked for their password should get to weigh.
fn install_password_message(username: &str, version: Option<&str>) -> String {
    let what = match version {
        Some(version) => format!("macOS {version}"),
        None => "a macOS update".to_string(),
    };

    format!(
        "Kintsugi is ready to install {what} on this Mac.\n\n\
         macOS requires your password to authorize a system update on Apple silicon. It is used \
         once, to run this installation, and is not stored.\n\n\
         Enter the password for \u{201c}{username}\u{201d}, or Cancel to skip the macOS update this \
         time \u{2014} application updates will still be installed."
    )
}

/// Asks the console user to authorize the macOS install, and returns what they said.
///
/// The password reaches this process on `osascript`'s stdout and goes straight into an
/// `os_update::InstallAuth`; it is never logged here, and `run_osascript`'s `trim()` is
/// deliberately *not* used on it (see [`run_osascript_untrimmed`]).
pub fn request_install_password(username: &str, version: Option<&str>, timeout_seconds: u64) -> Result<PasswordAnswer> {
    crate::logging::info(&format!("asking {username} to authorize the macOS install"));

    let script = format!(
        r#"display dialog "{}" with title "Kintsugi Patching" default answer "" with hidden answer buttons {{"{}", "{}"}} default button "{}" with icon caution giving up after {}"#,
        escape(&install_password_message(username, version)),
        CANCEL_BUTTON,
        AUTHORIZE_BUTTON,
        AUTHORIZE_BUTTON,
        timeout_seconds
    );

    let answer = match run_osascript_untrimmed(&script) {
        Ok(result) => parse_password_result(&result),
        // osascript exits non-zero when the user presses Cancel ("User canceled. (-128)"), which is
        // an answer rather than a failure — anything else really is one.
        Err(err) if format!("{err:#}").contains("User canceled") => PasswordAnswer::Cancelled,
        Err(err) => return Err(err),
    };

    crate::logging::info(&format!(
        "{username} chose: {}",
        match answer {
            PasswordAnswer::Provided(_) => "authorize the macOS install",
            PasswordAnswer::Cancelled => "skip the macOS update",
            PasswordAnswer::TimedOut => "timed out (treated as a refusal, so the macOS update is skipped)",
        }
    ));

    Ok(answer)
}

/// [`run_osascript`] without the `trim()`.
///
/// Every other caller wants the trim; this one must not have it. A password may legitimately begin
/// or end with a space, and trimming the whole of osascript's output would silently hand
/// `softwareupdate` a different password than the one that was typed — which arrives as "Failed to
/// authenticate" after the download, indistinguishable from a wrong password.
fn run_osascript_untrimmed(script: &str) -> Result<String> {
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

    // Only the single trailing newline osascript itself adds.
    let stdout = String::from_utf8_lossy(&output.stdout);
    Ok(stdout.strip_suffix('\n').unwrap_or(&stdout).to_string())
}

/// Interprets the authorization prompt's result: `button returned:Authorize, text returned:<typed>,
/// gave up:false`.
///
/// `gave up:` is checked first for the reason the other two parsers check it first — a timeout
/// reports `button returned:<default button>`, which here is Authorize. The password is taken as
/// everything between `text returned:` and the *final* `, gave up:`, so one containing a comma
/// survives; and an unparseable answer falls through to `Cancelled`, never to a `Provided` holding
/// something that isn't the password.
fn parse_password_result(result: &str) -> PasswordAnswer {
    if result.contains("gave up:true") {
        return PasswordAnswer::TimedOut;
    }

    let Some((_, after)) = result.split_once("text returned:") else {
        return PasswordAnswer::Cancelled;
    };

    let password = match after.rsplit_once(", gave up:") {
        Some((password, _)) => password,
        None => after,
    };

    if password.is_empty() {
        PasswordAnswer::Cancelled
    } else {
        PasswordAnswer::Provided(password.to_string())
    }
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
        let message = confirmation_message("1 hour(s)", 3, &names(&["Firefox", "Slack"]), false, None);

        assert!(message.contains("2 application updates are ready"), "{message}");
        assert!(message.contains("\n  \u{2022} Firefox\n  \u{2022} Slack\n"), "{message}");
    }

    /// A host that has been offline for a while can have dozens pending; the dialog has to stay
    /// on screen, so past the cap the rest are counted rather than named.
    #[test]
    fn confirmation_message_summarises_the_tail_of_a_long_list() {
        let all: Vec<String> = (1..=14).map(|n| format!("App {n}")).collect();
        let message = confirmation_message("1 hour(s)", 3, &all, false, None);

        assert!(message.contains("  \u{2022} App 10\n"), "{message}");
        assert!(!message.contains("App 11"), "{message}");
        assert!(message.contains("  \u{2026} and 4 more"), "{message}");
    }

    /// The OS-only case has no applications to list, and must read exactly as it did before the
    /// list existed — no bullet block, and no extra blank line where one would have gone.
    #[test]
    fn confirmation_message_for_an_os_update_alone_carries_no_list() {
        let message = confirmation_message("1 hour(s)", 3, &[], true, None);

        assert_eq!(
            message,
            "A macOS update is ready to install. This may restart some applications, and could \
             require a reboot.\n\nYou can delay this up to 3 more time(s), 1 hour(s) at a time."
        );
    }

    #[test]
    fn confirmation_message_states_the_remaining_delay_budget() {
        let message = confirmation_message("2 day(s)", 4, &names(&["Firefox"]), false, None);

        assert!(message.contains("1 application update is ready"), "{message}");
        assert!(message.contains("up to 4 more time(s), 2 day(s) at a time"), "{message}");
    }

    #[test]
    fn confirmation_message_describes_applications_and_an_os_update_together() {
        let message = confirmation_message("1 hour(s)", 3, &names(&["Firefox", "Slack", "Zoom"]), true, None);

        assert!(message.contains("3 application updates and a macOS update are ready"), "{message}");
        assert!(message.contains("  \u{2022} Zoom"), "{message}");
    }

    #[test]
    fn remote_control_result_explicit_allow() {
        assert_eq!(parse_remote_control_result("button returned:Allow, gave up:false"), RemoteControlChoice::Allow);
    }

    #[test]
    fn remote_control_result_explicit_deny() {
        assert_eq!(parse_remote_control_result("button returned:Deny, gave up:false"), RemoteControlChoice::Deny);
    }

    #[test]
    fn remote_control_result_timeout_is_not_consent() {
        // The whole reason this has its own enum: unlike a patch dialog, nobody answering must not
        // be read as permission. Deny is the default button, so this is what a timeout looks like.
        assert_eq!(parse_remote_control_result("button returned:Deny, gave up:true"), RemoteControlChoice::TimedOut);
    }

    #[test]
    fn remote_control_result_timeout_wins_over_an_allow_button() {
        // Belt and braces: even if the default button were ever changed to Allow, a dismissed
        // dialog must still not grant anything.
        assert_eq!(parse_remote_control_result("button returned:Allow, gave up:true"), RemoteControlChoice::TimedOut);
    }

    #[test]
    fn remote_control_result_unparseable_is_a_refusal() {
        assert_eq!(parse_remote_control_result(""), RemoteControlChoice::Deny);
        assert_eq!(parse_remote_control_result("something unexpected"), RemoteControlChoice::Deny);
    }

    #[test]
    fn remote_control_message_names_who_is_asking_and_how_to_stop_it() {
        let message = remote_control_message("admin@example.com", &[]);

        assert!(message.contains("admin@example.com is asking to control this Mac"), "{message}");
        assert!(message.contains("keyboard and mouse"), "{message}");
        // Somebody who regrets allowing it needs to know there is a way out.
        assert!(message.contains("menu bar"), "{message}");
    }

    #[test]
    fn remote_control_message_lists_what_the_session_cannot_do() {
        let message = remote_control_message(
            "admin@example.com",
            &["Keyboard and mouse control is unavailable: this agent has not been granted Accessibility.".to_string()],
        );

        assert!(message.contains("\u{2022} Keyboard and mouse control is unavailable"), "{message}");
    }

    #[test]
    fn escape_handles_quotes_and_backslashes() {
        assert_eq!(escape(r#"He said "hi" \ bye"#), r#"He said \"hi\" \\ bye"#);
    }

    #[test]
    fn parse_password_result_returns_what_was_typed() {
        let result = "button returned:Authorize, text returned:hunter2, gave up:false";
        assert_eq!(parse_password_result(result), PasswordAnswer::Provided("hunter2".to_string()));
    }

    /// osascript separates the record's fields with ", " and a password may contain one — taking
    /// everything up to the *last* ", gave up:" is what keeps it whole.
    #[test]
    fn parse_password_result_keeps_a_password_containing_a_comma() {
        let result = "button returned:Authorize, text returned:one, two, three, gave up:false";
        assert_eq!(parse_password_result(result), PasswordAnswer::Provided("one, two, three".to_string()));
    }

    #[test]
    fn parse_password_result_keeps_leading_and_trailing_spaces() {
        // The reason this dialog does not go through `run_osascript`: trimming would hand
        // softwareupdate a different password and the failure would be indistinguishable from a
        // wrong one, after the whole download.
        let result = "button returned:Authorize, text returned: spaced , gave up:false";
        assert_eq!(parse_password_result(result), PasswordAnswer::Provided(" spaced ".to_string()));
    }

    /// A timeout reports `button returned:<default button>`, which here is Authorize — so `gave up:`
    /// has to be read first, exactly as in the other two parsers.
    #[test]
    fn parse_password_result_reads_an_unanswered_prompt_as_a_timeout_not_an_authorization() {
        let result = "button returned:Authorize, text returned:, gave up:true";
        assert_eq!(parse_password_result(result), PasswordAnswer::TimedOut);
    }

    #[test]
    fn parse_password_result_treats_an_empty_or_unreadable_answer_as_cancelled() {
        assert_eq!(parse_password_result("button returned:Authorize, text returned:, gave up:false"), PasswordAnswer::Cancelled);
        assert_eq!(parse_password_result("something unexpected"), PasswordAnswer::Cancelled);
    }

    /// `-i -a` installs everything applicable, so "a macOS update" can mean a 2.9GB point release
    /// or an 11.7GB new major version. Somebody being asked for their password is owed the
    /// difference.
    #[test]
    fn install_password_message_names_the_version_and_the_account() {
        let message = install_password_message("david", Some("26.7"));

        assert!(message.contains("macOS 26.7"), "{message}");
        assert!(message.contains("david"), "{message}");
        assert!(message.contains("Cancel"), "declining has to be presented as an option: {message}");
    }

    #[test]
    fn install_password_message_stays_readable_when_the_version_is_unknown() {
        let message = install_password_message("david", None);

        assert!(message.contains("a macOS update"), "{message}");
        assert!(!message.contains("macOS  "), "no gap where the version would have been: {message}");
    }

    #[test]
    fn confirmation_message_names_the_macos_version_when_it_is_known() {
        let alone = confirmation_message("1 hour(s)", 3, &[], true, Some("26.7"));
        assert!(alone.starts_with("A macOS 26.7 update is ready"), "{alone}");

        let with_apps = confirmation_message("1 hour(s)", 3, &names(&["Firefox"]), true, Some("27"));
        assert!(with_apps.contains("1 application update and a macOS 27 update are ready"), "{with_apps}");
    }
}
