use std::fs;
use std::path::{Path, PathBuf};
use std::process::Command;
use std::time::Duration;

use serde::Serialize;

use crate::config::{self, Config};
use crate::logging;
use crate::self_update;

const REPORT_ATTEMPTS: u32 = 3;
const REPORT_BACKOFF: Duration = Duration::from_secs(5);

#[derive(Debug, Serialize)]
#[serde(rename_all = "camelCase")]
struct ConfirmRemovalRequest<'a> {
    serial_number: &'a str,
}

/// Runs when a check-in response marks this host for removal (see `main::RegisterHostResponse`
/// and `Kintsugi.Domain.Entities.Host.RemovalRequested` on the backend): tears this agent down
/// completely from the host machine, then reports that back to the server. The confirmation
/// report is made with `client`'s mTLS identity already loaded in memory (built once at startup —
/// see `identity::build_client`), so it still succeeds even though the on-disk certificate files
/// it originally came from are already gone by the time it's sent.
///
/// Order matters throughout, but for a gentler reason than on macOS. There, the last step
/// (`launchctl bootout` on the daemon's own job) kills the process running it, so everything else
/// has to happen first. Here the root half is a systemd oneshot that is about to exit anyway —
/// there is nothing to kill, and nothing after this returns. What the order still has to get right
/// is *disabling before deleting*: `systemctl disable` reads the unit file's `[Install]` section,
/// so removing the files first would leave the enablement symlinks behind, pointing at units that
/// no longer exist.
pub fn run(client: &reqwest::blocking::Client, config: &Config, serial_number: &str) {
    logging::info("server marked this host for removal — uninstalling completely from this machine");

    stop_user_agents();
    disable_units();
    remove_files();
    reload_systemd();
    report_removed(client, config, serial_number);

    logging::info("uninstall complete");
}

/// Stops the per-user agent for every user currently logged in, and un-enables it for every future
/// login. Best-effort: a user who isn't logged in right now has nothing running to stop, and the
/// global disable below is what stops it coming back at their next login.
fn stop_user_agents() {
    for (uid, username) in self_update::logged_in_users() {
        logging::info(&format!("stopping {} for {username}", config::UI_UNIT));
        self_update::run_user_systemctl(uid, &username, &["stop", config::UI_UNIT]);
    }

    // `--global` operates on the enablement symlinks under /etc/systemd/user that apply to every
    // user, which is how packaging/install.sh turned this on in the first place.
    run_systemctl(&["--global", "disable", config::UI_UNIT]);
}

/// Stops and un-enables the two units that would otherwise start this agent again: the check-in
/// timer and the queue watch. The queue *service* needs neither — it has no `[Install]` section
/// and only ever runs because the path unit triggered it.
///
/// This deliberately doesn't stop `kintsugi-agent.service`: that is the unit running this code,
/// and it is a oneshot that exits on its own the moment `run` returns.
fn disable_units() {
    run_systemctl(&["disable", "--now", config::TIMER_UNIT]);
    run_systemctl(&["disable", "--now", config::QUEUE_PATH_UNIT]);
}

/// Everything on disk this agent ever wrote, across both the root service and the per-user
/// process — a "complete" removal per the server's request, not just the conservative subset
/// `packaging/uninstall.sh` leaves behind for a human to clean up manually.
fn remove_files() {
    remove_path(&config::timer_unit_path());
    remove_path(&config::service_unit_path());
    remove_path(&config::queue_service_unit_path());
    remove_path(&config::queue_path_unit_path());
    remove_path(&config::ui_unit_path());
    remove_path(&config::installed_binary_path());
    // Config lives under /etc and mutable state under /var/lib — see `config` for why they're
    // split. Identity, queue, daemon log, check-in schedule and staged scripts are all under the
    // latter.
    remove_dir(&config::config_dir());
    remove_dir(&config::state_dir());

    for (_, username) in self_update::logged_in_users() {
        // The one piece of state that lives outside the two directories above — see
        // `config::user_state_dir`. Only reachable for users who are logged in right now, the same
        // limitation the macOS agent has; `packaging/uninstall.sh` says as much for the rest.
        if let Some(home) = home_directory(&username) {
            remove_dir(&home.join(".local/state/kintsugi-agent"));
        }
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

/// Tells systemd the unit files are gone, so it stops reporting them as loaded-but-missing.
/// `--no-block` for the same reason `checkin_schedule::reload_timer` uses it — there is nothing
/// left for this process to do that depends on the reload having finished.
fn reload_systemd() {
    run_systemctl(&["daemon-reload"]);
}

fn run_systemctl(args: &[&str]) {
    match Command::new("systemctl").args(args).output() {
        Ok(output) if output.status.success() => {}
        Ok(output) => logging::warn(&format!(
            "systemctl {} exited with {}: {}",
            args.join(" "),
            output.status,
            String::from_utf8_lossy(&output.stderr).trim()
        )),
        Err(err) => logging::warn(&format!("failed to run systemctl {}: {err}", args.join(" "))),
    }
}

/// A user's home directory, via `getent` rather than assuming `/home/<name>` — the same care the
/// macOS agent takes in reading it from Directory Services rather than assuming `/Users/<name>`,
/// and it matters more here, where a fleet may well have LDAP- or SSSD-backed accounts whose homes
/// are somewhere else entirely.
fn home_directory(username: &str) -> Option<PathBuf> {
    let output = Command::new("getent").args(["passwd", username]).output().ok()?;
    if !output.status.success() {
        return None;
    }
    parse_passwd_home(&String::from_utf8_lossy(&output.stdout)).map(PathBuf::from)
}

/// Pulls the home directory (the sixth colon-separated field) out of a passwd entry:
/// `alice:x:1000:1000:Alice:/home/alice:/bin/bash`.
fn parse_passwd_home(entry: &str) -> Option<String> {
    let home = entry.trim().split(':').nth(5)?.trim();
    (!home.is_empty() && home.starts_with('/')).then(|| home.to_string())
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
    fn parse_passwd_home_extracts_the_sixth_field() {
        assert_eq!(
            parse_passwd_home("alice:x:1000:1000:Alice Smith:/home/alice:/bin/bash\n").as_deref(),
            Some("/home/alice")
        );
    }

    #[test]
    fn parse_passwd_home_handles_a_home_that_is_not_under_slash_home() {
        assert_eq!(
            parse_passwd_home("svc:x:900:900::/var/lib/svc:/usr/sbin/nologin").as_deref(),
            Some("/var/lib/svc")
        );
    }

    /// An account with no home directory recorded must not turn into a `remove_dir_all` of
    /// something relative to the working directory.
    #[test]
    fn parse_passwd_home_returns_none_for_an_empty_or_relative_home() {
        assert_eq!(parse_passwd_home("nobody:x:65534:65534::/:/usr/sbin/nologin").as_deref(), Some("/"));
        assert_eq!(parse_passwd_home("broken:x:1:1:::"), None);
        assert_eq!(parse_passwd_home("broken:x:1:1::relative/path:"), None);
    }

    #[test]
    fn parse_passwd_home_returns_none_for_unexpected_output() {
        assert_eq!(parse_passwd_home("no such user\n"), None);
    }
}
