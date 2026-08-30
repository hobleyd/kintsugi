use std::fs;
use std::path::{Path, PathBuf};
use std::process::Command;
use std::time::Duration;

use serde::Serialize;

use crate::config::{self, Config};
use crate::logging;

const REPORT_ATTEMPTS: u32 = 3;
const REPORT_BACKOFF: Duration = Duration::from_secs(5);

#[derive(Debug, Serialize)]
#[serde(rename_all = "camelCase")]
struct ConfirmRemovalRequest<'a> {
    serial_number: &'a str,
}

/// Runs when a check-in response marks this host for removal (see `main::RegisterHostResponse`
/// and `Kintsugi.Domain.Entities.Host.RemovalRequested` on the backend): tears this agent down
/// completely from the host machine, reports that back to the server, and only then unloads its
/// own LaunchDaemon. Order matters throughout — every step that doesn't kill this process runs
/// first. The confirmation report is made with `client`'s mTLS identity already loaded in memory
/// (built once at daemon startup — see `identity::build_client`), so it still succeeds even though
/// the on-disk certificate files it originally came from are already gone by the time it's sent.
/// Self-termination (`bootout_daemon`) is strictly the last thing this does; nothing after it is
/// guaranteed to run.
pub fn run(client: &reqwest::blocking::Client, config: &Config, serial_number: &str) {
    logging::info("server marked this host for removal — uninstalling completely from this machine");

    bootout_ui_agent();
    remove_files();
    report_removed(client, config, serial_number);

    logging::info("uninstall complete — stopping this daemon");
    bootout_daemon();
}

/// Everything on disk this agent ever wrote, across both the root daemon and the per-user
/// `--agent` process — a "complete" removal per the server's request, not just the conservative
/// subset `packaging/uninstall.sh` leaves behind for a human to clean up manually.
fn remove_files() {
    remove_path(&config::ui_plist_path());
    remove_path(&config::daemon_plist_path());
    remove_path(&config::installed_binary_path());
    // Config, identity, queue, daemon log, and check-in schedule all live under this one
    // directory — see `config::config_dir`.
    remove_dir(&config::config_dir());
    remove_path(Path::new("/var/log/kintsugi-agent.log"));
    remove_path(Path::new("/var/log/kintsugi-agent.err.log"));
    remove_path(Path::new("/tmp/kintsugi-agent-ui.out.log"));
    remove_path(Path::new("/tmp/kintsugi-agent-ui.err.log"));

    if let Some(home) = console_user_home() {
        // The one piece of per-user state that lives outside config::config_dir() — see
        // config::user_state_dir().
        remove_dir(&home.join("Library/Application Support/kintsugi-agent"));
    }
}

fn remove_path(path: &Path) {
    match fs::remove_file(path) {
        Ok(()) => logging::info(&format!("removed {}", path.display())),
        Err(err) if err.kind() == std::io::ErrorKind::NotFound => {}
        Err(err) => logging::warn(&format!("could not remove {}: {err}", path.display())),
    }
}

fn remove_dir(path: &Path) {
    match fs::remove_dir_all(path) {
        Ok(()) => logging::info(&format!("removed {}", path.display())),
        Err(err) if err.kind() == std::io::ErrorKind::NotFound => {}
        Err(err) => logging::warn(&format!("could not remove {}: {err}", path.display())),
    }
}

/// Unloads the per-user menu bar LaunchAgent for whichever user is at the console right now — the
/// only other running process this binary has, besides this root daemon itself. Best-effort: if no
/// one is logged in (or the lookup fails), its plist is still deleted by `remove_files`, so it
/// simply won't relaunch at next login rather than being actively stopped now.
fn bootout_ui_agent() {
    let Some(uid) = console_user_uid() else {
        return;
    };
    let target = format!("gui/{uid}/{}", config::UI_LAUNCHD_LABEL);
    logging::info(&format!("stopping {target}"));
    run_launchctl(&["bootout", &target]);
}

/// The last thing `run` does — unloads this very LaunchDaemon, which ends this process.
fn bootout_daemon() {
    run_launchctl(&["bootout", &format!("system/{}", config::DAEMON_LAUNCHD_LABEL)]);
}

fn run_launchctl(args: &[&str]) {
    match Command::new("launchctl").args(args).output() {
        Ok(output) if output.status.success() => {}
        Ok(output) => logging::warn(&format!(
            "launchctl {} exited with {}: {}",
            args.join(" "),
            output.status,
            String::from_utf8_lossy(&output.stderr).trim()
        )),
        Err(err) => logging::warn(&format!("failed to run launchctl {}: {err}", args.join(" "))),
    }
}

/// Mirrors `self_update`'s own console-user lookup (kept separate rather than shared, since
/// that one's private to its module) — the currently logged-in console user, if any.
fn console_username() -> Option<String> {
    let output = Command::new("stat").arg("-f").arg("%Su").arg("/dev/console").output().ok()?;
    if !output.status.success() {
        return None;
    }
    let username = String::from_utf8_lossy(&output.stdout).trim().to_string();
    (!username.is_empty() && username != "root").then_some(username)
}

fn console_user_uid() -> Option<u32> {
    let username = console_username()?;
    let uid_output = Command::new("id").arg("-u").arg(&username).output().ok()?;
    if !uid_output.status.success() {
        return None;
    }
    String::from_utf8_lossy(&uid_output.stdout).trim().parse().ok()
}

/// The console user's home directory, via Directory Services rather than assuming `/Users/<name>`
/// — needed to clean up the one piece of per-user state (`schedule.json`, `policy.json`) this
/// agent leaves outside `config::config_dir()`.
fn console_user_home() -> Option<PathBuf> {
    let username = console_username()?;
    let output = Command::new("dscl").args([".", "-read", &format!("/Users/{username}"), "NFSHomeDirectory"]).output().ok()?;
    if !output.status.success() {
        return None;
    }
    parse_dscl_home_directory(&String::from_utf8_lossy(&output.stdout)).map(PathBuf::from)
}

fn parse_dscl_home_directory(output: &str) -> Option<String> {
    let value = output.trim().strip_prefix("NFSHomeDirectory:")?.trim();
    (!value.is_empty()).then(|| value.to_string())
}

/// Confirms this host's removal to the server — a bounded retry rather than best-effort, since
/// this is the one call that actually makes the record disappear server-side (see
/// ConfirmHostRemovalCommandHandler). Even if every attempt fails, this machine is already fully
/// uninstalled by the time this runs (`remove_files` already happened), so there's nothing left to
/// roll back to; a permanent failure here just leaves the host soft-deleted until someone notices.
fn report_removed(client: &reqwest::blocking::Client, config: &Config, serial_number: &str) {
    let request = ConfirmRemovalRequest { serial_number };
    let mut backoff = REPORT_BACKOFF;

    for attempt in 1..=REPORT_ATTEMPTS {
        match client.post(config.host_removed_url()).json(&request).send() {
            Ok(response) if response.status().is_success() => {
                logging::info("confirmed this host's removal to the server");
                return;
            }
            Ok(response) => {
                logging::warn(&format!(
                    "attempt {attempt}/{REPORT_ATTEMPTS}: server rejected the removal confirmation (HTTP {})",
                    response.status()
                ));
            }
            Err(err) => {
                logging::warn(&format!("attempt {attempt}/{REPORT_ATTEMPTS} to confirm removal failed: {err}"));
            }
        }

        if attempt < REPORT_ATTEMPTS {
            std::thread::sleep(backoff);
            backoff *= 2;
        }
    }

    logging::error(
        "could not confirm this host's removal to the server after several attempts — \
         this machine is already fully uninstalled regardless",
    );
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn parse_dscl_home_directory_extracts_the_path() {
        assert_eq!(parse_dscl_home_directory("NFSHomeDirectory: /Users/jsmith\n"), Some("/Users/jsmith".to_string()));
    }

    #[test]
    fn parse_dscl_home_directory_returns_none_for_unexpected_output() {
        assert_eq!(parse_dscl_home_directory("no such key\n"), None);
    }

    #[test]
    fn parse_dscl_home_directory_returns_none_for_an_empty_value() {
        assert_eq!(parse_dscl_home_directory("NFSHomeDirectory: \n"), None);
    }

    #[test]
    fn parse_dscl_home_directory_trims_surrounding_whitespace() {
        assert_eq!(parse_dscl_home_directory("  NFSHomeDirectory:   /Users/jsmith  \n"), Some("/Users/jsmith".to_string()));
    }
}
