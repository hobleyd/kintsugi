use std::path::{Path, PathBuf};
use std::process::{Child, Command, Stdio};
use std::time::Duration;

use anyhow::{Context, Result};

use crate::schedule::now_epoch;

const DELAY_BUTTON: &str = "Delay";
const PATCH_NOW_BUTTON: &str = "Patch Now";
const TITLE: &str = "Kintsugi Patching";

#[derive(Debug, PartialEq, Eq)]
pub enum ConfirmChoice {
    PatchNow,
    Delay,
    /// The dialog stood for a whole delay period of wall-clock time without an answer — either
    /// zenity's own `--timeout` dismissed it, or this process closed it because the deadline had
    /// passed (see `run_until` for why both exist).
    ///
    /// It counts against the delay budget — the user was asked and said nothing — but **not** the
    /// way an explicit `Delay` does: the dialog stands there for a whole delay period, so that
    /// period is already spent by the time it gives up, and postponing by another one on top would
    /// count the budget down half as fast as the policy says. See
    /// `ScheduleState::register_unanswered_prompt`.
    TimedOut,
}

/// How often a dialog's subprocess is checked against its wall-clock deadline. Small enough that a
/// laptop resumed past its deadline closes the stale prompt before the person has finished reading
/// it; each check is one `try_wait`, so there is nothing to save by making it larger.
const DEADLINE_POLL: Duration = Duration::from_secs(5);

/// How a dialog subprocess ended.
enum DialogRun {
    /// The process exited on its own — a button was clicked, or zenity's own timeout fired — with
    /// this exit status (`None` for a signal death, which the callers read as a decline).
    Exited(Option<i32>),
    /// The wall-clock deadline arrived first and the process was killed, which takes its dialog
    /// down with it. Every caller reads this as "nobody answered".
    DeadlinePassed,
}

/// Runs a dialog program and holds it to a deadline on the **wall clock**, which neither zenity's
/// `--timeout` nor coreutils' `timeout` reliably does.
///
/// zenity's timeout is a GLib timeout on `CLOCK_MONOTONIC`, which on Linux does not advance while
/// the machine is suspended, so a laptop closed with the prompt up resumes with the timer having
/// lost the whole suspend. The macOS agent measured the same class of defect on its own dialog
/// program — a one-hour prompt that stood for seventy-one hours across a weekend of sleep — and the
/// whole delay budget the policy describes is meant to run from the moment the person is asked,
/// lid open or shut. The only party that can see a wall clock across a suspend is this process, so
/// it polls the child against `deadline_epoch` and kills it when the deadline has passed. zenity's
/// own `--timeout` is kept all the same: while the machine stays awake it closes the dialog cleanly
/// at exactly the period, with its own exit status, and costs nothing. kdialog has no timeout of
/// its own and used to borrow coreutils' `timeout`, which is dropped here — this process's kill is
/// the timeout now, and it means one fewer program a desktop has to have installed.
///
/// `now_epoch` rather than `Instant` for the deadline, for the reason `schedule` gives: `Instant`
/// stops during suspend as well, and the suspend is the case this exists for.
fn run_until(path: &Path, args: &[&str], deadline_epoch: u64) -> Result<DialogRun> {
    let child = dialog_command(path)
        .args(args)
        .stdin(Stdio::null())
        .stdout(Stdio::null())
        .stderr(Stdio::null())
        .spawn()
        .with_context(|| format!("failed to run {}", path.display()))?;

    wait_until(child, deadline_epoch, DEADLINE_POLL)
}

/// Waits for `child` to exit, or kills it once the wall clock reaches `deadline_epoch`, whichever
/// comes first. Split from `run_until` so the deadline behaviour can be tested with an ordinary
/// command rather than a dialog. The deadline is checked before each sleep rather than after, so a
/// deadline that has already passed — the resume-from-suspend case — is acted on at once.
fn wait_until(mut child: Child, deadline_epoch: u64, poll: Duration) -> Result<DialogRun> {
    loop {
        if let Some(status) = child.try_wait().context("could not poll the dialog process")? {
            return Ok(DialogRun::Exited(status.code()));
        }
        if now_epoch() >= deadline_epoch {
            // A kill that fails because the child exited in between is not a problem: the wait
            // reaps it either way, and the deadline had passed, so "nobody answered" stands.
            let _ = child.kill();
            let _ = child.wait();
            return Ok(DialogRun::DeadlinePassed);
        }
        std::thread::sleep(poll);
    }
}

/// Which dialog program this desktop has. Both are checked because neither is universal: zenity
/// is the GTK world's (GNOME, Xfce, Cinnamon, MATE), kdialog is KDE's, and a host may reasonably
/// have only one.
///
/// This is the piece the macOS agent gets for free — `osascript` is part of the OS, always
/// present, and its `display dialog` speaks a single documented result format. On Linux there is
/// no such guarantee, so `confirm_patch` has to deal with a missing dialog program as an ordinary
/// outcome rather than an error condition; see `patch_cycle::confirm_or_delay`, which treats a
/// dialog failure as "proceed rather than nag", and `tray_menu::run`, which does the same for a
/// missing notification area.
#[derive(Debug, Clone, PartialEq, Eq)]
enum DialogTool {
    Zenity(PathBuf),
    KDialog(PathBuf),
}

/// Where a desktop's dialog programs actually live. `PATH` is deliberately not consulted, for the
/// same reason `system_info::find_binary` doesn't: a systemd user service inherits systemd's own
/// minimal environment, not a login shell's.
const SEARCH_DIRS: &[&str] = &["/usr/bin", "/bin", "/usr/local/bin"];

fn find_binary(name: &str) -> Option<PathBuf> {
    SEARCH_DIRS
        .iter()
        .map(|dir| Path::new(dir).join(name))
        .find(|path| path.is_file())
}

fn detect_dialog_tool() -> Option<DialogTool> {
    find_binary("zenity")
        .map(DialogTool::Zenity)
        .or_else(|| find_binary("kdialog").map(DialogTool::KDialog))
}

/// How many application names the dialog lists before summarising the rest. A host that has been
/// offline for a while can legitimately have dozens of pending updates, and an unbounded list
/// would grow the dialog off the bottom of the screen — the count in the opening sentence still
/// states the whole truth. Kept identical in the macOS and Windows agents' dialogs.
const MAX_LISTED_APPS: usize = 10;

/// Composes the dialog body. Split out from the subprocess call so the wording — which is the
/// part that has to stay true to what `patch_cycle` actually found — can be tested directly.
///
/// The application names are listed under the opening sentence rather than only counted: "3
/// application updates are ready" doesn't tell someone deciding whether to delay whether the
/// thing they have open right now is about to be restarted.
fn confirmation_message(delay_label: &str, delays_remaining: u32, app_names: &[String], os_update_available: bool) -> String {
    let what = match (app_names.len(), os_update_available) {
        (0, true) => "A system update is".to_string(),
        (n, false) => format!("{n} application update{} {}", if n == 1 { "" } else { "s" }, if n == 1 { "is" } else { "are" }),
        (n, true) => format!("{n} application update{} and a system update are", if n == 1 { "" } else { "s" }),
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
/// `timeout_seconds` bounds how long the dialog stays up, on the wall clock, before it is taken
/// down unanswered (`ConfirmChoice::TimedOut`) — otherwise an ignored dialog would sit on screen
/// forever. Callers pass the delay period itself, so an ignored dialog has spent one whole delay
/// period by the time it gives up — which is why that outcome counts the budget down without
/// postponing the cycle any further. Wall clock, not run time: see `run_until`.
pub fn confirm_patch(
    delay_label: &str,
    delays_remaining: u32,
    app_names: &[String],
    os_update_available: bool,
    timeout_seconds: u64,
) -> Result<ConfirmChoice> {
    let tool = detect_dialog_tool().context("no dialog program (zenity or kdialog) is installed")?;
    let delay_button_label = format!("{DELAY_BUTTON} {delay_label} ({delays_remaining} left)");
    let message = confirmation_message(delay_label, delays_remaining, app_names, os_update_available);

    crate::logging::info(&format!("showing patch confirmation dialog ({delays_remaining} delay(s) available)"));

    let deadline = now_epoch() + timeout_seconds;
    let run = match &tool {
        // zenity answers entirely through its exit status — 0 for the OK button, 1 for cancel (or
        // the window being closed), 5 for `--timeout` expiring. That is a far cleaner contract than
        // the macOS agent's, which has to string-parse `osascript`'s result *and* check `gave up:`
        // first, because AppleScript reports the default button as pressed even on a timeout.
        DialogTool::Zenity(path) => run_until(
            path,
            &[
                "--question",
                // The message now carries application names, which come from the backend and so
                // can contain anything a package manager reported. zenity parses `--text` as
                // Pango markup by default, and a name with a bare `&` in it makes that parse
                // fail — zenity then exits non-zero, which `interpret_exit_status` reads as a
                // decline, so the host would silently delay forever and never show a dialog at
                // all. kdialog's path needs no equivalent: Qt only treats text as rich when it
                // looks like markup, and these names don't.
                "--no-markup",
                &format!("--title={TITLE}"),
                &format!("--text={message}"),
                &format!("--ok-label={PATCH_NOW_BUTTON}"),
                &format!("--cancel-label={delay_button_label}"),
                &format!("--timeout={timeout_seconds}"),
            ],
            deadline,
        )?,
        // kdialog has no timeout of its own; the deadline `run_until` enforces is the only one.
        DialogTool::KDialog(path) => run_until(
            path,
            &[
                "--title",
                TITLE,
                "--yes-label",
                PATCH_NOW_BUTTON,
                "--no-label",
                &delay_button_label,
                "--yesno",
                &message,
            ],
            deadline,
        )?,
    };

    let choice = match run {
        DialogRun::Exited(status) => interpret_exit_status(status),
        DialogRun::DeadlinePassed => ConfirmChoice::TimedOut,
    };

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

/// What the person at the keyboard said when asked to hand over control of this host.
///
/// **A separate type from [`ConfirmChoice`] on purpose, because a timeout means the opposite
/// thing.** There, nobody answering means "they were not at the desk, so count it as a delay and
/// patch later" — the user never refused, and patching happens regardless. Here, nobody answering
/// means **nobody consented**, and the only safe reading of silence is refusal. Reusing that enum
/// would have left the safe default one careless `match` arm away from handing an unattended desktop
/// to whoever asked. Kept identical to the other two agents' `RemoteControlChoice`.
#[derive(Debug, PartialEq, Eq)]
pub enum RemoteControlChoice {
    Allow,
    Deny,
    /// The dialog dismissed itself. Treated exactly as [`Self::Deny`] by every caller, and kept
    /// distinct only so the audit record can tell an empty desk from a deliberate refusal.
    TimedOut,
}

const ALLOW_BUTTON: &str = "Allow";
const DENY_BUTTON: &str = "Deny";
const REMOTE_CONTROL_TITLE: &str = "Kintsugi Remote Control";

/// Composes the consent dialog's text. Split out so the wording — the part somebody has to make a
/// decision from — can be tested without a desktop.
fn remote_control_message(requested_by: &str, restrictions: &[String]) -> String {
    let mut message = format!(
        "{requested_by} is asking to control this computer remotely.\n\n\
         If you allow this, they will see your screen and be able to use your keyboard and mouse as \
         though they were sitting here. You can end the session at any time from the Kintsugi icon \
         in the notification area.\n"
    );

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
/// **A missing dialog program is a refusal here, and that is the opposite of what `confirm_patch`
/// does with one.** There, no zenity and no kdialog means "proceed rather than nag" — patching has
/// to happen and there is nobody to ask. Here there is nothing to proceed with: consent that cannot
/// be asked for has not been given, so a host with no dialog program simply cannot be remote
/// controlled. That is the correct outcome and the administrator is told so.
pub fn confirm_remote_control(
    requested_by: &str,
    restrictions: &[String],
    timeout_seconds: u64,
) -> Result<RemoteControlChoice> {
    let tool = detect_dialog_tool()
        .context("no dialog program (zenity or kdialog) is installed, so nobody can be asked for consent")?;
    let message = remote_control_message(requested_by, restrictions);

    crate::logging::info(&format!("asking the console user to approve remote control for {requested_by}"));

    // The two tools are handled separately rather than through a shared exit-status mapper, because
    // their safe defaults are reached in opposite ways and hiding that in one function is how it
    // would eventually get "simplified" into a hole.
    let deadline = now_epoch() + timeout_seconds;
    let choice = match &tool {
        // zenity can be told to make Cancel the default (`--default-cancel`), so the labels sit the
        // natural way round: OK is Allow, Cancel is Deny. Exit 0 is Allow, 5 is its own timeout,
        // and everything else — including the window being closed and a markup parse failure — is a
        // refusal.
        DialogTool::Zenity(path) => {
            let run = run_until(
                path,
                &[
                    "--question",
                    // As confirm_patch: the requester's name comes from the server and zenity parses
                    // --text as Pango markup by default, so a name containing `&` would fail the
                    // parse and exit non-zero — which here would read as a refusal nobody made.
                    "--no-markup",
                    "--default-cancel",
                    &format!("--title={REMOTE_CONTROL_TITLE}"),
                    &format!("--text={message}"),
                    &format!("--ok-label={ALLOW_BUTTON}"),
                    &format!("--cancel-label={DENY_BUTTON}"),
                    &format!("--timeout={timeout_seconds}"),
                ],
                deadline,
            )?;

            match run {
                DialogRun::Exited(Some(0)) => RemoteControlChoice::Allow,
                DialogRun::Exited(Some(5)) | DialogRun::DeadlinePassed => RemoteControlChoice::TimedOut,
                DialogRun::Exited(_) => RemoteControlChoice::Deny,
            }
        }

        // kdialog has no way to make No the default button for `--yesno`, so the labels are
        // **reversed**: Deny is the Yes button. That makes the default action, and therefore
        // Return, a refusal — and it keeps the fallback safe as well, since exit 0 (Yes) is Deny and
        // every unexpected status falls through to Deny too. Labelling them the natural way round
        // would mean Return granted a session to whoever asked.
        DialogTool::KDialog(path) => {
            let run = run_until(
                path,
                &[
                    "--title",
                    REMOTE_CONTROL_TITLE,
                    "--yes-label",
                    DENY_BUTTON,
                    "--no-label",
                    ALLOW_BUTTON,
                    "--yesno",
                    &message,
                ],
                deadline,
            )?;

            match run {
                // 1 is the No button, which this dialog labels Allow.
                DialogRun::Exited(Some(1)) => RemoteControlChoice::Allow,
                DialogRun::DeadlinePassed => RemoteControlChoice::TimedOut,
                DialogRun::Exited(_) => RemoteControlChoice::Deny,
            }
        }
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

/// Maps a confirm dialog's exit status onto a choice. zenity uses 5 for its own `--timeout` (a
/// deadline this process enforced never reaches here — see `run_until`). Anything else that isn't
/// a clean 0 is a decline, which includes the user closing the window — the conservative reading,
/// since a closed window is not consent to start patching.
fn interpret_exit_status(status: Option<i32>) -> ConfirmChoice {
    match status {
        Some(0) => ConfirmChoice::PatchNow,
        Some(5) => ConfirmChoice::TimedOut,
        _ => ConfirmChoice::Delay,
    }
}

/// Whether a child needs `LC_ALL` forced to a UTF-8 locale, given what this process inherited.
///
/// GLib decodes a program's arguments using the locale's charset, and under the C locale zenity
/// rejects *any* non-ASCII argument outright — it exits 255, which `interpret_exit_status` reads
/// as a decline, so the host would silently delay forever and never show a dialog again. That is
/// not hypothetical: the message now lists application names, which come from whatever Flatpak
/// and Snap reported, and it draws them with a bullet. This is the same class of environment gap
/// as `find_binary` not consulting `PATH` — a systemd user unit inherits systemd's environment,
/// where nothing has set `LANG` unless the session manager did.
///
/// A locale that is already UTF-8 is left alone, so a user's own `de_DE.UTF-8` survives; only a
/// locale that cannot represent the text is replaced. `LC_ALL` is what gets set, because it is
/// what overrides an inherited non-UTF-8 `LANG`.
fn needs_utf8_locale_override(lc_all: Option<&str>, lang: Option<&str>) -> bool {
    let is_utf8 = |value: &str| {
        let value = value.to_ascii_lowercase().replace('-', "");
        value.contains("utf8")
    };

    match (lc_all, lang) {
        (Some(lc_all), _) if !lc_all.is_empty() => !is_utf8(lc_all),
        (_, Some(lang)) if !lang.is_empty() => !is_utf8(lang),
        _ => true,
    }
}

/// Builds a command for a dialog program, with the locale sorted out — see
/// `needs_utf8_locale_override`, which is the whole reason this exists rather than
/// `Command::new`.
fn dialog_command(program: &Path) -> Command {
    let mut command = Command::new(program);
    if needs_utf8_locale_override(std::env::var("LC_ALL").ok().as_deref(), std::env::var("LANG").ok().as_deref()) {
        command.env("LC_ALL", "C.UTF-8");
    }
    command
}

/// A single-button dialog for the "no delays left, proceeding regardless" case, and other
/// blocking messages the user must actively dismiss rather than a passive notification banner.
///
/// Takes a `timeout_seconds` cap for the same reason `confirm_patch` does: patching must start
/// once the delay budget is spent regardless of whether anyone is at the keyboard to click "OK".
/// Held to the wall clock like every other dialog here (`run_until`), so the five-minute notice
/// before an automatic start is five minutes of real time even across a suspend.
pub fn acknowledge(message: &str, timeout_seconds: u64) -> Result<()> {
    let tool = detect_dialog_tool().context("no dialog program (zenity or kdialog) is installed")?;
    crate::logging::info(&format!("showing acknowledgement dialog: {message}"));

    let deadline = now_epoch() + timeout_seconds;
    match &tool {
        DialogTool::Zenity(path) => run_until(
            path,
            &[
                "--warning",
                &format!("--title={TITLE}"),
                &format!("--text={message}"),
                &format!("--timeout={timeout_seconds}"),
            ],
            deadline,
        )?,
        DialogTool::KDialog(path) => run_until(path, &["--title", TITLE, "--sorry", message], deadline)?,
    };

    Ok(())
}

/// Best-effort — a failed notification (no notification daemon running, no session bus, this
/// running somehow outside a real user session) shouldn't ever be treated as a reason to abort
/// patching.
///
/// `notify-send` is the freedesktop standard and what every desktop environment implements;
/// zenity's own `--notification` is the fallback for a host that has zenity but not libnotify's
/// command-line tool.
pub fn notify(title: &str, message: &str) {
    crate::logging::info(&format!("notification: {title} — {message}"));

    if let Some(notify_send) = find_binary("notify-send") {
        // `dialog_command` for the locale, not for a dialog: notify-send is GLib-based too, and
        // `progress_bar`'s block characters are non-ASCII on every single notification.
        match dialog_command(&notify_send).args(["--app-name", TITLE, title, message]).output() {
            Ok(output) if output.status.success() => return,
            Ok(output) => crate::logging::warn(&format!(
                "notify-send exited with {}: {}",
                output.status,
                String::from_utf8_lossy(&output.stderr).trim()
            )),
            Err(err) => crate::logging::warn(&format!("could not run notify-send: {err}")),
        }
    }

    if let Some(DialogTool::Zenity(zenity)) = detect_dialog_tool() {
        let text = format!("{title}\n{message}");
        if let Err(err) = dialog_command(&zenity).args(["--notification", &format!("--text={text}")]).output() {
            crate::logging::warn(&format!("could not show notification: {err}"));
        }
    }
}

/// A crude but dependency-free progress indicator, rendered as a Unicode block bar inside a
/// notification body, so each step's banner at least visually communicates how far through the
/// run it is. Shared with `tray_menu`'s menu line, exactly as on macOS.
pub fn progress_bar(completed: usize, total: usize) -> String {
    const WIDTH: usize = 20;
    let filled = if total == 0 { 0 } else { (completed * WIDTH) / total };
    let filled = filled.min(WIDTH);
    format!("[{}{}] {completed}/{total}", "█".repeat(filled), "░".repeat(WIDTH - filled))
}

#[cfg(test)]
mod tests {
    #[test]
    fn remote_control_message_names_who_is_asking_and_how_to_stop_it() {
        let message = remote_control_message("admin@example.com", &[]);

        assert!(message.contains("admin@example.com is asking to control this computer"), "{message}");
        assert!(message.contains("keyboard and mouse"), "{message}");
        // Somebody who regrets allowing it needs to know there is a way out.
        assert!(message.contains("notification area"), "{message}");
    }

    #[test]
    fn remote_control_message_lists_what_the_session_cannot_do() {
        let message = remote_control_message(
            "admin@example.com",
            &["This host is running Wayland.".to_string()],
        );

        assert!(message.contains("\u{2022} This host is running Wayland."), "{message}");
    }

    use super::*;

    #[test]
    fn interpret_exit_status_maps_a_clean_exit_to_patch_now() {
        assert_eq!(interpret_exit_status(Some(0)), ConfirmChoice::PatchNow);
    }

    #[test]
    fn interpret_exit_status_maps_cancel_to_delay() {
        assert_eq!(interpret_exit_status(Some(1)), ConfirmChoice::Delay);
    }

    /// zenity's own `--timeout` must count as "nobody was there", not as a decision.
    #[test]
    fn interpret_exit_status_maps_zenitys_timeout_to_timed_out() {
        assert_eq!(interpret_exit_status(Some(5)), ConfirmChoice::TimedOut);
    }

    /// The resume-from-suspend case: a deadline that has already passed is acted on at the first
    /// check, before any sleep, so a stale prompt comes down within a poll of the machine waking.
    #[test]
    fn wait_until_kills_a_child_whose_deadline_has_already_passed() {
        let child = Command::new("sleep").arg("30").spawn().unwrap();
        let started = std::time::Instant::now();

        let run = wait_until(child, now_epoch().saturating_sub(1), Duration::from_secs(60)).unwrap();

        assert!(matches!(run, DialogRun::DeadlinePassed));
        assert!(started.elapsed() < Duration::from_secs(5), "must not have waited out the poll interval");
    }

    #[test]
    fn wait_until_reports_the_exit_status_of_a_child_that_exits_before_its_deadline() {
        let child = Command::new("sh").args(["-c", "exit 5"]).spawn().unwrap();

        let run = wait_until(child, now_epoch() + 3600, Duration::from_millis(50)).unwrap();

        assert!(matches!(run, DialogRun::Exited(Some(5))), "zenity's own timeout status has to reach the caller");
    }

    /// A dialog killed by a signal has no exit code at all; treating that as consent to start
    /// patching would be the wrong way to be wrong.
    #[test]
    fn interpret_exit_status_treats_a_signal_death_as_a_decline() {
        assert_eq!(interpret_exit_status(None), ConfirmChoice::Delay);
    }

    /// The C locale is what a systemd user unit inherits when nothing has set `LANG`, and it is
    /// the case that breaks zenity on any non-ASCII character.
    #[test]
    fn needs_utf8_locale_override_when_nothing_sets_a_locale_at_all() {
        assert!(needs_utf8_locale_override(None, None));
        assert!(needs_utf8_locale_override(Some(""), Some("")));
        assert!(needs_utf8_locale_override(None, Some("C")));
        assert!(needs_utf8_locale_override(None, Some("en_AU.ISO-8859-1")));
    }

    /// A desktop session that already has a UTF-8 locale keeps it — overriding would change the
    /// language a user deliberately chose, and there is nothing to fix.
    #[test]
    fn needs_no_override_when_the_inherited_locale_is_already_utf8() {
        assert!(!needs_utf8_locale_override(None, Some("de_DE.UTF-8")));
        assert!(!needs_utf8_locale_override(None, Some("en_AU.utf8")));
        assert!(!needs_utf8_locale_override(Some("C.UTF-8"), Some("C")));
    }

    /// `LC_ALL` overrides `LANG` for the child, so a non-UTF-8 `LC_ALL` has to be replaced even
    /// when `LANG` looks fine.
    #[test]
    fn needs_override_when_lc_all_is_not_utf8_despite_a_utf8_lang() {
        assert!(needs_utf8_locale_override(Some("C"), Some("de_DE.UTF-8")));
    }

    fn names(names: &[&str]) -> Vec<String> {
        names.iter().map(|name| name.to_string()).collect()
    }

    #[test]
    fn confirmation_message_describes_applications_only() {
        let message = confirmation_message("1 hour(s)", 3, &names(&["Firefox", "Slack"]), false);

        assert!(message.contains("2 application updates are ready"), "{message}");
        assert!(!message.contains("system update"), "{message}");
    }

    #[test]
    fn confirmation_message_uses_the_singular_for_one_application() {
        assert!(confirmation_message("1 hour(s)", 3, &names(&["Firefox"]), false).contains("1 application update is ready"));
    }

    #[test]
    fn confirmation_message_describes_an_os_update_on_its_own() {
        assert!(confirmation_message("1 hour(s)", 3, &[], true).contains("A system update is ready"));
    }

    #[test]
    fn confirmation_message_describes_both_together() {
        let message = confirmation_message("1 hour(s)", 3, &names(&["Firefox", "Slack", "Zoom"]), true);

        assert!(message.contains("3 application updates and a system update are ready"), "{message}");
    }

    #[test]
    fn confirmation_message_states_the_remaining_delay_budget() {
        assert!(confirmation_message("2 day(s)", 4, &names(&["Firefox"]), false).contains("up to 4 more time(s), 2 day(s) at a time"));
    }

    #[test]
    fn confirmation_message_lists_the_affected_applications() {
        let message = confirmation_message("1 hour(s)", 3, &names(&["Firefox", "Slack"]), false);

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
            "A system update is ready to install. This may restart some applications, and could \
             require a reboot.\n\nYou can delay this up to 3 more time(s), 1 hour(s) at a time."
        );
    }

    #[test]
    fn progress_bar_is_empty_at_the_start_and_full_at_the_end() {
        assert_eq!(progress_bar(0, 4), format!("[{}] 0/4", "░".repeat(20)));
        assert_eq!(progress_bar(4, 4), format!("[{}] 4/4", "█".repeat(20)));
    }

    /// The warning-period case, where there's nothing concrete to count yet — a zero total must
    /// not divide by zero.
    #[test]
    fn progress_bar_handles_a_zero_total() {
        assert_eq!(progress_bar(0, 0), format!("[{}] 0/0", "░".repeat(20)));
    }
}
