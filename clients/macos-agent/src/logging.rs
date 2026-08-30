use std::fs::OpenOptions;
use std::io::Write;
use std::path::Path;
use std::sync::{Mutex, OnceLock};

use time::OffsetDateTime;

static LOG_FILE: OnceLock<Mutex<std::fs::File>> = OnceLock::new();

/// Opens (creating if needed) `log_path` in append mode and directs every subsequent
/// `info`/`warn`/`error` call to it, in addition to stdout/stderr — so every action this process
/// takes (a patch attempt, a dialog shown, a delay chosen, a queue request processed) has a
/// durable record on disk regardless of how the process was launched, rather than depending on
/// launchd's stdout/stderr capture (which the per-user agent, running under a plist shared by
/// every logged-in user, can't rely on at all — see the LaunchAgent plist).
pub fn init(log_path: &Path) {
    if let Some(parent) = log_path.parent() {
        let _ = std::fs::create_dir_all(parent);
    }

    match OpenOptions::new().create(true).append(true).open(log_path) {
        Ok(file) => {
            let _ = LOG_FILE.set(Mutex::new(file));
        }
        Err(err) => {
            eprintln!("warning: could not open log file {}: {err}", log_path.display());
        }
    }
}

fn timestamp() -> String {
    let format = time::macros::format_description!("[year]-[month]-[day] [hour]:[minute]:[second] UTC");
    OffsetDateTime::now_utc().format(format).unwrap_or_else(|_| "0000-00-00 00:00:00 UTC".to_string())
}

fn write_line(level: &str, message: &str) {
    let line = format!("{} [{level}] {message}", timestamp());

    if level == "ERROR" || level == "WARN" {
        eprintln!("{line}");
    } else {
        println!("{line}");
    }

    if let Some(mutex) = LOG_FILE.get() {
        if let Ok(mut file) = mutex.lock() {
            let _ = writeln!(file, "{line}");
            let _ = file.flush();
        }
    }
}

pub fn info(message: &str) {
    write_line("INFO", message);
}

pub fn warn(message: &str) {
    write_line("WARN", message);
}

pub fn error(message: &str) {
    write_line("ERROR", message);
}
