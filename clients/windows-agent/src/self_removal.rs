use std::fs;
use std::path::{Path, PathBuf};
use std::process::Command;
use std::time::Duration;

use serde::Serialize;

use crate::config::{self, Config};
use crate::logging;
use crate::self_update::detached_powershell;

const REPORT_ATTEMPTS: u32 = 3;
const REPORT_BACKOFF: Duration = Duration::from_secs(5);

#[derive(Debug, Serialize)]
#[serde(rename_all = "camelCase")]
struct ConfirmRemovalRequest<'a> {
    serial_number: &'a str,
}

/// Runs when a check-in response marks this host for removal (see `service`'s `RegisterHostResponse`
/// and `Kintsugi.Domain.Entities.Host.RemovalRequested` on the backend): tears this agent down
/// completely from the machine, reports that back to the server, and only then removes itself.
///
/// Order matters throughout — every step that doesn't kill this process runs first. The confirmation
/// report is made with `client`'s mTLS identity already loaded in memory (built once at service
/// startup — see `identity::build_client`), so it still succeeds even though the on-disk certificate
/// files it originally came from are already gone by the time it's sent. Self-termination is
/// strictly the last thing this does; nothing after it is guaranteed to run.
pub fn run(client: &reqwest::blocking::Client, config: &Config, serial_number: &str) {
    logging::info("server marked this host for removal — uninstalling completely from this machine");

    remove_ui_task();
    remove_files();
    report_removed(client, config, serial_number);

    logging::info("uninstall complete — removing this service");
    remove_service_and_binary();
}

/// Everything on disk this agent ever wrote, across both the service and the per-user tray process
/// — a "complete" removal per the server's request, not just the conservative subset
/// `packaging/uninstall.ps1` leaves behind for a human to clean up manually.
///
/// The binary itself is deliberately not removed here: it is the file currently executing, and
/// Windows holds a running image locked. `remove_service_and_binary` deals with it last, from
/// outside this process.
fn remove_files() {
    // Config, identity, queue, service log, check-in schedule, and the shared policy cache all live
    // under this one directory — see `config::config_dir`.
    remove_dir(&config::config_dir());

    for home in user_profile_dirs() {
        // The one piece of per-user state that lives outside config_dir() — see
        // `config::user_state_dir`. Removed for every profile on the machine, not just the console
        // user's: unlike macOS, where a managed Mac has one admin user in practice, a shared PC
        // routinely has several profiles that have each run the tray process.
        remove_dir(&home.join(r"AppData\Local\Kintsugi"));
    }
}

fn remove_dir(path: &Path) {
    match fs::remove_dir_all(path) {
        Ok(()) => logging::info(&format!("removed {}", path.display())),
        Err(err) if err.kind() == std::io::ErrorKind::NotFound => {}
        Err(err) => logging::warn(&format!("could not remove {}: {err}", path.display())),
    }
}

/// Every local user profile directory, read from the filesystem rather than from the registry's
/// ProfileList: what's wanted here is just "which folders under C:\Users hold per-user state", and
/// the directory listing answers that without needing to resolve SIDs.
fn user_profile_dirs() -> Vec<PathBuf> {
    let root = std::env::var("SystemDrive").unwrap_or_else(|_| "C:".to_string()) + r"\Users";

    let Ok(entries) = fs::read_dir(&root) else {
        return Vec::new();
    };

    entries
        .filter_map(|entry| entry.ok())
        .map(|entry| entry.path())
        .filter(|path| path.is_dir())
        .collect()
}

/// Stops and deletes the logon-triggered task that hosts the per-user tray process — the only other
/// running process this binary has, besides this service itself.
///
/// Best-effort: if the task is already gone (or was never registered), deleting it fails harmlessly
/// and the tray process, having no task to relaunch it, simply doesn't come back.
fn remove_ui_task() {
    logging::info("stopping and removing the per-user agent task");
    run_command("schtasks", &["/End", "/TN", config::UI_TASK_NAME]);
    run_command("schtasks", &["/Delete", "/TN", config::UI_TASK_NAME, "/F"]);
}

/// The last thing `run` does: unregisters this service and deletes the binary behind it.
///
/// Both have to happen from *outside* this process, and for the same reason as
/// `self_update::restart_service`: stopping the service kills the process executing the stop, and
/// Windows won't delete a running image at all. So this hands the whole teardown to a detached
/// PowerShell process that sleeps first, letting this one finish and exit, and then removes what's
/// left. Nothing after this call is guaranteed to run.
fn remove_service_and_binary() {
    let service = config::SERVICE_NAME;
    let binary = config::installed_binary_path();
    let install_dir = binary.parent().map(Path::to_path_buf).unwrap_or_default();

    // Deleting the directory only if it's empty, so a machine that keeps other Kintsugi tooling
    // under %ProgramFiles%\Kintsugi doesn't lose it to this agent's uninstall.
    let script = format!(
        "Start-Sleep -Seconds 5; \
         Stop-Service -Name '{service}' -Force -ErrorAction SilentlyContinue; \
         & sc.exe delete '{service}' | Out-Null; \
         Start-Sleep -Seconds 2; \
         Remove-Item -LiteralPath '{binary}' -Force -ErrorAction SilentlyContinue; \
         Remove-Item -LiteralPath '{binary}.old' -Force -ErrorAction SilentlyContinue; \
         if ((Get-ChildItem -LiteralPath '{install_dir}' -Force -ErrorAction SilentlyContinue | Measure-Object).Count -eq 0) {{ \
             Remove-Item -LiteralPath '{install_dir}' -Force -ErrorAction SilentlyContinue }}",
        binary = binary.display(),
        install_dir = install_dir.display(),
    );

    match detached_powershell(&script).spawn() {
        Ok(_) => logging::info("handed off the final service and binary removal to a detached helper"),
        Err(err) => {
            // Not silent: the machine is already fully uninstalled apart from the service
            // registration and one file, and an administrator needs to know those are left.
            logging::error(&format!(
                "could not spawn the removal helper ({err}) — the '{service}' service and {} are still present \
                 and must be removed by hand",
                binary.display()
            ));
        }
    }
}

fn run_command(program: &str, args: &[&str]) {
    match Command::new(program).args(args).output() {
        Ok(output) if output.status.success() => {}
        Ok(output) => logging::warn(&format!(
            "{program} {} exited with {}: {}",
            args.join(" "),
            output.status,
            String::from_utf8_lossy(&output.stderr).trim()
        )),
        Err(err) => logging::warn(&format!("failed to run {program} {}: {err}", args.join(" "))),
    }
}

/// Confirms this host's removal to the server — a bounded retry rather than best-effort, since this
/// is the one call that actually makes the record disappear server-side (see
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
                logging::warn(&format!("attempt {attempt}/{REPORT_ATTEMPTS} to confirm removal failed: {err:#}"));
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
    fn remove_dir_is_silent_about_something_that_was_never_there() {
        // A removal runs against a fixed list of paths, most of which won't exist on a given
        // machine; treating "already gone" as a warning would fill the log with noise on every
        // uninstall and bury anything that actually failed.
        remove_dir(Path::new(r"C:\this\does\not\exist"));
    }

    #[test]
    fn remove_dir_deletes_a_whole_tree() {
        let dir = std::env::temp_dir().join(format!("kintsugi-removal-test-{}", std::process::id()));
        fs::create_dir_all(dir.join("nested")).unwrap();
        fs::write(dir.join("nested").join("state.json"), "{}").unwrap();

        remove_dir(&dir);

        assert!(!dir.exists());
    }
}
