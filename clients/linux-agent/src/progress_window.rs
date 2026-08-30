use std::io::Write;
use std::path::{Path, PathBuf};
use std::process::{Child, Command, Stdio};
use std::sync::Mutex;

use crate::logging;

/// The running `zenity --progress` process, if one is currently on screen.
///
/// A `Mutex` rather than the macOS agent's thread-local, because nothing here has to live on any
/// particular thread. That constraint is Cocoa's — an `NSWindow` may only be touched from the main
/// thread, which is the whole reason the macOS agent splits into a UI thread and a scheduler
/// thread at all. Here the "window" is a child process and a pipe, so the scheduler thread that
/// knows what to display can simply write to it.
static WINDOW: Mutex<Option<Child>> = Mutex::new(None);

const TITLE: &str = "Kintsugi Patching";

/// Shows the progress window (starting it on first use) with `current` as its headline and a
/// determinate bar reflecting `completed`/`total` (an empty bar when `total` is zero — e.g.
/// during the initial warning period, before there's anything concrete to count).
///
/// Drives `zenity --progress` over its standard input rather than drawing a window directly: the
/// macOS agent talks to AppKit because it must (a menu bar item already forces a Cocoa event loop
/// into the process), but doing the equivalent here would mean linking GTK — and this binary
/// deliberately links no GUI toolkit at all, so that the very same executable is the one running
/// as the root service on a headless server. See the `ksni` note in Cargo.toml.
///
/// Best-effort throughout: a host with no zenity, or no display to put a window on, simply gets no
/// progress window. It still gets notifications, the notification-area menu, and — most
/// importantly — the patching itself.
pub fn show_and_update(current: &str, completed: usize, total: usize) {
    let mut guard = match WINDOW.lock() {
        Ok(guard) => guard,
        // A poisoned lock means a previous holder panicked mid-update. The child process it was
        // writing to is still perfectly usable, and losing the progress window over it would be a
        // worse outcome than carrying on.
        Err(poisoned) => poisoned.into_inner(),
    };

    if guard.is_none() {
        *guard = spawn();
    }

    let Some(child) = guard.as_mut() else { return };
    let Some(stdin) = child.stdin.as_mut() else { return };

    // zenity's progress protocol, one directive per line: a bare number sets the percentage, and a
    // line starting with '#' replaces the label. A newline in `current` would be read as the start
    // of a new directive, so it is flattened first — the text ultimately comes from the server, by
    // way of an application name.
    let percentage = if total == 0 { 0 } else { (completed * 100 / total).min(100) };
    let label = current.replace(['\n', '\r'], " ");

    if let Err(err) = writeln!(stdin, "#{label}\n{percentage}") {
        logging::warn(&format!("could not update the progress window (it was probably closed): {err}"));
        // Dropped rather than retried: the pipe is gone for good once it breaks, and leaving the
        // dead handle in place would make every later update fail the same way. The next call
        // starts a fresh window.
        let _ = child.kill();
        *guard = None;
    }
}

/// Hides the progress window — called once a patch cycle finishes (success, failure, or a delay)
/// and the agent goes back to idle. A no-op if no window is showing.
pub fn hide() {
    let mut guard = match WINDOW.lock() {
        Ok(guard) => guard,
        Err(poisoned) => poisoned.into_inner(),
    };

    if let Some(mut child) = guard.take() {
        // Killed rather than closed politely: `--auto-close` would end the dialog the moment the
        // bar reached 100%, which is too early (the last step's result still has to be reported),
        // and zenity keeps a completed progress dialog on screen indefinitely otherwise.
        let _ = child.kill();
        let _ = child.wait();
    }
}

fn spawn() -> Option<Child> {
    let zenity = find_zenity()?;

    let result = Command::new(zenity)
        .args([
            "--progress",
            &format!("--title={TITLE}"),
            "--text=Starting\u{2026}",
            "--percentage=0",
            // There is nothing for a cancel button to cancel — the work is being done by the root
            // service on the other side of the queue, and a half-applied patch run is not
            // something this window can undo.
            "--no-cancel",
        ])
        .stdin(Stdio::piped())
        .stdout(Stdio::null())
        .stderr(Stdio::null())
        .spawn();

    match result {
        Ok(child) => Some(child),
        Err(err) => {
            logging::warn(&format!("could not show the progress window: {err}"));
            None
        }
    }
}

/// See `dialogs::find_binary` — same reasoning, kept separate because that one is private to its
/// module (mirroring how the macOS agent keeps `self_update`'s and `self_removal`'s console-user
/// lookups separate rather than sharing them).
fn find_zenity() -> Option<PathBuf> {
    ["/usr/bin", "/bin", "/usr/local/bin"]
        .iter()
        .map(|dir| Path::new(dir).join("zenity"))
        .find(|path| path.is_file())
}
