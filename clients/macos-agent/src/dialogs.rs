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

    // The reboot sentence is conditional, and states a certainty rather than a possibility when
    // the OS update is in the plan: `os_update::install` passes `-R`, so this Mac restarts on its
    // own to finish. "Could require a reboot" was true when the update merely staged itself; saying
    // it now would understate what clicking this button starts.
    let consequence = match (os_update_available, app_names.is_empty()) {
        (true, false) => {
            "Some applications will be restarted, and this Mac will restart itself to finish the \
             macOS update \u{2014} so save your work before continuing."
        }
        (true, true) => {
            "This Mac will restart itself to finish the macOS update \u{2014} so save your work before \
             continuing."
        }
        (false, _) => "This may restart some applications, and could require a reboot.",
    };
    let mut message = format!("{what} ready to install. {consequence}\n");

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

const AUTHORIZE_BUTTON: &str = "Authorize";

/// The headline of the authorization alert — the bold line, where the other dialogs' `with title`
/// text goes, since a Cocoa alert has no title bar.
const INSTALL_PASSWORD_HEADLINE: &str = "Kintsugi Patching";

/// The authorization prompt itself: a Cocoa `NSAlert` with a secure text field, driven through
/// `osascript`'s JavaScript-for-Automation bridge. Every other dialog in this file is an AppleScript
/// `display dialog`; this one is not, because it has to do three things `display dialog` cannot:
///
/// - **It cannot be dismissed.** There is no Cancel button and no `giving up after`. Escape and
///   Cmd-. do nothing to an alert with no button named Cancel, an empty box is re-shown rather than
///   returned, and the script only exits when it holds a non-empty password. The person clicked
///   "Patch Now" (or spent their delay budget with somebody at the desk — see
///   `patch_cycle::os_update_eligibility`), the applications are already patched, and the macOS
///   update is downloaded and waiting: leaving the prompt up until it is answered is what makes the
///   cycle finish. Before this, a Cancel and a ten-minute timeout both read as "skip the macOS
///   update", and a Mac whose owner walked away at the wrong moment simply never updated.
/// - **It stays in front.** `activateIgnoringOtherApps` brings it up over whatever is running, and
///   the window sits at `NSModalPanelWindowLevel` (8) with `CanJoinAllSpaces |
///   FullScreenAuxiliary` (1 | 256), so it is drawn above other applications' windows and follows
///   the person across spaces and full-screen apps rather than being buried behind them the moment
///   they click elsewhere. A `display dialog` from a background process appears wherever the window
///   server puts it and is lost the first time another window takes focus.
/// - **It asks nothing of any other application.** The obvious AppleScript route to a front-most
///   dialog — `tell application "System Events"` + `activate` — sends Apple events to another
///   process, which TCC gates behind an Automation prompt per Mac ("Not authorised to send Apple
///   events to System Events", -1743, measured on this fleet's own Mac). The alert below is this
///   process's own window, so no permission is involved.
///
/// Its arguments arrive as `argv` rather than being interpolated into the source, so nothing in the
/// message has to be escaped for JavaScript: `argv[0]` is the headline, `argv[1]` the body,
/// `argv[2]` the button. The password is the script's return value, which `osascript` prints to
/// stdout exactly as typed plus one trailing newline — see [`request_install_password`] for why
/// only that newline is stripped.
const INSTALL_PASSWORD_ALERT_SCRIPT: &str = r#"
ObjC.import('Cocoa');
function run(argv) {
  var app = $.NSApplication.sharedApplication;
  app.setActivationPolicy($.NSApplicationActivationPolicyAccessory);
  var alert = $.NSAlert.alloc.init;
  alert.messageText = argv[0];
  alert.informativeText = argv[1];
  alert.icon = $.NSImage.imageNamed('NSCaution');
  alert.addButtonWithTitle(argv[2]);
  var field = $.NSSecureTextField.alloc.initWithFrame($.NSMakeRect(0, 0, 320, 24));
  alert.accessoryView = field;
  alert.layout;
  var win = alert.window;
  win.level = 8;
  win.collectionBehavior = 257;
  win.initialFirstResponder = field;
  var answer = '';
  while (answer === '') {
    app.activateIgnoringOtherApps(true);
    alert.runModal;
    answer = field.stringValue.js;
  }
  return answer;
}
"#;

/// Composes the authorization prompt. Split out from the subprocess call for the same reason every
/// other message in this file is — the wording is the part somebody has to make a decision from.
///
/// It names the version, and that is not decoration: `softwareupdate -i -a` installs everything
/// applicable, which on a host offered both a point release and a major upgrade means the major
/// one. "A macOS update is ready" would be a fair description of a 2.9GB point release and a
/// misleading one of an 11.7GB new major version, and the difference is exactly what somebody being
/// asked for their password should get to weigh.
///
/// It offers no way out, because there is none: see [`INSTALL_PASSWORD_ALERT_SCRIPT`]. Saying so
/// is kinder than leaving somebody to look for the Cancel button.
fn install_password_message(username: &str, version: Option<&str>) -> String {
    let what = match version {
        Some(version) => format!("macOS {version}"),
        None => "a macOS update".to_string(),
    };

    format!(
        "Kintsugi is ready to install {what} on this Mac.\n\n\
         \u{26a0}\u{fe0f} The update has already been downloaded, so installing it will begin as soon as \
         you authorize it and this Mac will restart by itself a few minutes later. It may not be \
         able to close your applications first \u{2014} save your work now.\n\n\
         macOS requires your password to authorize a system update on Apple silicon. It is used \
         once, to run this installation, and is not stored.\n\n\
         Enter the password for \u{201c}{username}\u{201d} and press {AUTHORIZE_BUTTON}. This prompt \
         stays open until you do \u{2014} the application updates have already been installed, and \
         the macOS update is the one thing left."
    )
}

/// Asks the console user to authorize the macOS install, and returns their password.
///
/// It returns only when it has one: the prompt cannot be cancelled or left to time out (see
/// [`INSTALL_PASSWORD_ALERT_SCRIPT`]), so the only other way out is an `Err` — `osascript` could
/// not be run, or the alert could not be shown at all (no window server, say). There is no
/// "declined" and no "nobody answered" any more, which is why this returns a `String` rather than
/// the three-way `PasswordAnswer` it used to.
///
/// The password reaches this process on `osascript`'s stdout and goes straight into an
/// `os_update::InstallAuth`; it is never logged here, and `run_osascript`'s `trim()` is deliberately
/// *not* used on it. A password may legitimately begin or end with a space, and trimming the whole
/// of osascript's output would silently hand `softwareupdate` a different password than the one
/// that was typed — which arrives as "Failed to authenticate", indistinguishable from a wrong
/// password. Only the single trailing newline osascript itself adds comes off.
pub fn request_install_password(username: &str, version: Option<&str>) -> Result<String> {
    crate::logging::info(&format!("asking {username} to authorize the macOS install"));

    let output = Command::new("osascript")
        .args(["-l", "JavaScript", "-e", INSTALL_PASSWORD_ALERT_SCRIPT])
        .arg(INSTALL_PASSWORD_HEADLINE)
        .arg(install_password_message(username, version))
        .arg(AUTHORIZE_BUTTON)
        .output()
        .context("failed to run osascript")?;

    if !output.status.success() {
        anyhow::bail!(
            "osascript exited with {}: {}",
            output.status,
            String::from_utf8_lossy(&output.stderr).trim()
        );
    }

    let stdout = String::from_utf8_lossy(&output.stdout);
    let password = strip_result_newline(&stdout);
    if password.is_empty() {
        // The script does not return until it holds a non-empty answer, so this is osascript
        // printing nothing at all — never something to hand softwareupdate as a password.
        anyhow::bail!("the authorization prompt closed without a password");
    }

    crate::logging::info(&format!("{username} authorized the macOS install"));
    Ok(password.to_string())
}

/// Removes the one trailing newline `osascript` prints after a script's result, and nothing else —
/// a password that ends in a space, or that is *only* spaces, has to reach `softwareupdate` intact.
fn strip_result_newline(stdout: &str) -> &str {
    stdout.strip_suffix('\n').unwrap_or(stdout)
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
            "A macOS update is ready to install. This Mac will restart itself to finish the macOS \
             update \u{2014} so save your work before continuing.\n\nYou can delay this up to 3 more \
             time(s), 1 hour(s) at a time."
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

    /// The password is the script's return value, which osascript prints followed by one newline.
    /// That newline is the only thing that may come off it.
    #[test]
    fn strip_result_newline_removes_only_the_newline_osascript_adds() {
        assert_eq!(strip_result_newline("hunter2\n"), "hunter2");
        assert_eq!(strip_result_newline("one, two, three\n"), "one, two, three");
    }

    /// The reason this dialog does not go through `run_osascript`: trimming would hand
    /// softwareupdate a different password and the failure would be indistinguishable from a wrong
    /// one.
    #[test]
    fn strip_result_newline_keeps_leading_and_trailing_spaces() {
        assert_eq!(strip_result_newline(" spaced \n"), " spaced ");
        assert_eq!(strip_result_newline("   \n"), "   ", "a password that is only spaces is still a password");
    }

    #[test]
    fn strip_result_newline_leaves_output_without_one_alone() {
        assert_eq!(strip_result_newline("hunter2"), "hunter2");
        assert_eq!(strip_result_newline(""), "");
    }

    /// The alert has no Cancel button and no timeout, so the script has nothing to escape and the
    /// message has to be safe to hand over as a plain argument. Nothing in the source is
    /// interpolated: the three `argv` reads are the only way text gets in.
    #[test]
    fn install_password_alert_script_takes_its_text_as_arguments_and_cannot_be_dismissed() {
        assert!(INSTALL_PASSWORD_ALERT_SCRIPT.contains("argv[0]"), "headline");
        assert!(INSTALL_PASSWORD_ALERT_SCRIPT.contains("argv[1]"), "body");
        assert!(INSTALL_PASSWORD_ALERT_SCRIPT.contains("argv[2]"), "button");
        assert_eq!(INSTALL_PASSWORD_ALERT_SCRIPT.matches("addButtonWithTitle").count(), 1, "one button, so Escape has nothing to press");
        assert!(!INSTALL_PASSWORD_ALERT_SCRIPT.contains("Cancel"), "{INSTALL_PASSWORD_ALERT_SCRIPT}");
        assert!(INSTALL_PASSWORD_ALERT_SCRIPT.contains("while (answer === '')"), "an empty box is re-shown, not returned");
        assert!(INSTALL_PASSWORD_ALERT_SCRIPT.contains("activateIgnoringOtherApps(true)"), "it comes to the front");
        assert!(INSTALL_PASSWORD_ALERT_SCRIPT.contains("win.level = 8"), "and stays above other windows");
    }

    /// `-i -a` installs everything applicable, so "a macOS update" can mean a 2.9GB point release
    /// or an 11.7GB new major version. Somebody being asked for their password is owed the
    /// difference.
    /// The one thing somebody must not be able to miss before authorizing: `-R` means this Mac
    /// reboots on its own, possibly without closing anything first.
    #[test]
    fn install_password_message_warns_that_the_mac_restarts_itself() {
        let message = install_password_message("david", Some("26.7"));

        assert!(message.contains("restart by itself"), "{message}");
        assert!(message.contains("save your work"), "{message}");
    }

    /// This prompt is only ever shown *after* the daemon's pre-fetch has staged the update (see
    /// `main::prefetch_os_updates`, and `patch_cycle::os_update_is_staged` for the check), which is what
    /// makes "a few minutes later" true. It said the opposite when the download still followed the
    /// authorization, and a stale promise here is the difference between an expected restart and an
    /// ambush.
    #[test]
    fn install_password_message_says_the_restart_is_imminent_not_distant() {
        let message = install_password_message("david", Some("26.7"));

        assert!(message.contains("already been downloaded"), "{message}");
        assert!(message.contains("a few minutes later"), "{message}");
        assert!(!message.contains("some time after"), "the pre-split wording promised a long wait: {message}");
    }

    #[test]
    fn confirmation_message_promises_a_reboot_only_when_the_os_update_is_in_the_plan() {
        let with_os = confirmation_message("1 hour(s)", 3, &names(&["Firefox"]), true, Some("26.7"));
        assert!(with_os.contains("will restart itself"), "{with_os}");

        let apps_only = confirmation_message("1 hour(s)", 3, &names(&["Firefox"]), false, None);
        assert!(!apps_only.contains("will restart itself"), "no OS update, no promise of a reboot: {apps_only}");
        assert!(apps_only.contains("could require a reboot"), "{apps_only}");
    }

    #[test]
    fn install_password_message_names_the_version_and_the_account() {
        let message = install_password_message("david", Some("26.7"));

        assert!(message.contains("macOS 26.7"), "{message}");
        assert!(message.contains("david"), "{message}");
        assert!(!message.contains("Cancel"), "there is no Cancel button, so the text must not promise one: {message}");
        assert!(message.contains("stays open until you do"), "and it says the prompt will wait: {message}");
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
