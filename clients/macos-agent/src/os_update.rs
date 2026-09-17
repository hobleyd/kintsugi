use std::fmt;
use std::io::Write;
use std::process::{Command, Stdio};

use anyhow::{Context, Result};
use serde::Serialize;

use crate::config::Config;

/// The result of a standard OS-update check: whether one is pending, and the version it would
/// bring the host to, when the check can determine that.
#[derive(Debug, Clone, Default, PartialEq, Eq)]
pub struct OsUpdateStatus {
    pub available: bool,
    pub latest_version: Option<String>,
}

/// A volume owner's credentials, which is what `softwareupdate -i` demands on Apple silicon before
/// it will install a macOS update — see [`install`] for why root alone is not enough.
///
/// Carried from the per-user half (which asked for it) to the root daemon (which needs it) through
/// `queue`, and deliberately **not** stored anywhere else: there is no on-disk copy that outlives
/// one request, and `queue::take_auth` unlinks the file the moment it has been read.
#[derive(Clone)]
pub struct InstallAuth {
    /// The account's short name, passed to `--user`. Must be a volume owner — see
    /// [`is_volume_owner`], which is checked before anything is downloaded.
    pub user: String,
    /// Fed to `--stdinpass` over a pipe. Never logged, never put in an error, and redacted by this
    /// type's own `Debug` so that an `{:?}` added later can't leak it either.
    pub password: String,
}

impl fmt::Debug for InstallAuth {
    /// Hand-written rather than derived **on purpose**. A derived `Debug` would print the password
    /// in full, and this type travels through `queue`'s request handling where `{kind:?}`-style
    /// logging is the house style — one careless format string is all it would take.
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        f.debug_struct("InstallAuth").field("user", &self.user).field("password", &"<redacted>").finish()
    }
}

/// What a successful [`install`] actually achieved.
///
/// With `-R` the ordinary macOS case never reaches this type at all: the reboot happens *inside*
/// `softwareupdate`, so the process is killed mid-call and nothing after it runs. This describes
/// the case where the install came back — a Safari-only update, an Intel host, or an update that
/// turned out not to need a restart — and something is nonetheless still pending.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct InstallOutcome {
    /// True when a macOS update is *still* listed after the install returned. Ground truth rather
    /// than a phrase matched out of `softwareupdate`'s output, whose wording has changed across
    /// releases. Reporting a host as patched in this state is what made the admin UI flicker: the
    /// flag cleared, and the next check-in's `softwareupdate -l` put it straight back.
    pub restart_required: bool,
}

/// `softwareupdate -l` doesn't require elevation and is safe to run from the (non-root) `--agent`
/// process just to decide whether an OS-update step is even needed — only the actual install
/// (`install`, below) needs root, which the per-user process asks the daemon for through the
/// handoff queue (`queue::RequestKind::OsUpdate`). This is macOS's own standard way of telling
/// whether an OS update is outstanding, the same one Windows (Windows Update) and Linux (the
/// distro's package manager) each have their own equivalent of.
pub fn check() -> Result<OsUpdateStatus> {
    let output = Command::new("softwareupdate")
        .arg("-l")
        .output()
        .context("failed to run softwareupdate -l")?;

    // softwareupdate -l exits non-zero when nothing is available on some macOS versions, so the
    // stdout text (not the exit code) is what actually distinguishes "nothing found" from a real
    // failure — matching either of the two phrasings macOS has used across versions.
    let combined = format!(
        "{}{}",
        String::from_utf8_lossy(&output.stdout),
        String::from_utf8_lossy(&output.stderr)
    );

    let status = parse_check_output(&combined);
    crate::logging::info(&format!(
        "checked for macOS updates: available={} latest_version={:?}",
        status.available, status.latest_version
    ));
    Ok(status)
}

/// The pure text-parsing half of [`check`], split out so it can be exercised directly against
/// sample `softwareupdate -l` output rather than only via a real (macOS-only) subprocess call.
fn parse_check_output(combined: &str) -> OsUpdateStatus {
    if combined.contains("No new software available") {
        return OsUpdateStatus::default();
    }

    let available = combined.contains("Software Update found") || combined.contains("* Label:");
    let latest_version = if available { parse_latest_version(combined) } else { None };
    OsUpdateStatus { available, latest_version }
}

/// The version this host's pending **macOS** update would bring it to, out of a `softwareupdate -l`
/// listing — the number `softwareupdate` itself states, rather than one parsed back out of the
/// free-text title.
///
/// Two things make "the first `Version:` in the output" wrong, and that is how this shipped. A
/// listing carries one `Title:` line per label, and the labels are not all macOS. The listing that
/// exposed it was:
///
/// ```text
/// * Label: Safari27.0TahoeAuto-27.0
///     Title: Safari, Version: 27.0, Size: 249465KiB, Recommended: YES,
/// * Label: macOS Tahoe 26.7-25G229
///     Title: macOS Tahoe 26.7, Version: 26.7, Size: 2960352KiB, Recommended: YES, Action: restart,
/// * Label: macOS 27-26A428
///     Title: macOS 27, Version: 27, Size: 11727573KiB, Recommended: YES, Action: restart,
/// ```
///
/// whose first `Version:` is *Safari's* — which is why the admin UI reported this Mac's pending
/// macOS version as 27.0 while the update actually downloading was 26.7. So only a line whose title
/// names macOS counts here.
///
/// Of those, the **highest** is reported rather than the first: `install` runs `softwareupdate -i
/// -a`, so a host offered both 26.7 and 27 ends up on 27, and the number shown to the administrator
/// has to be the one the host will actually be on.
fn parse_latest_version(text: &str) -> Option<String> {
    text.lines().filter_map(parse_macos_title_version).max_by(|a, b| compare_versions(a, b))
}

/// The `Version:` field of one listing line, but only if that line's `Title:` names macOS. Anything
/// else in the listing — Safari, XProtect, a firmware update — is a real update, just not the one
/// `OsUpdateStatus::latest_version` is describing.
fn parse_macos_title_version(line: &str) -> Option<String> {
    let after_title = line.split_once("Title:")?.1;
    if !after_title.trim_start().starts_with("macOS") {
        return None;
    }

    let after_version = after_title.split_once("Version:")?.1;
    let version = after_version.split(',').next()?.trim();
    (!version.is_empty()).then(|| version.to_string())
}

/// Orders two dotted version strings numerically, so "26.7" sorts below "27" — which a string
/// comparison gets backwards, and which is exactly the pair this host is being offered. A component
/// that isn't a number counts as zero rather than failing the whole comparison: the goal is to pick
/// the larger of two versions macOS printed, not to validate them.
fn compare_versions(left: &str, right: &str) -> std::cmp::Ordering {
    let components = |version: &str| version.split('.').map(|part| part.trim().parse::<u64>().unwrap_or(0)).collect::<Vec<_>>();

    let (left, right) = (components(left), components(right));
    for index in 0..left.len().max(right.len()) {
        let ordering = left.get(index).copied().unwrap_or(0).cmp(&right.get(index).copied().unwrap_or(0));
        if ordering != std::cmp::Ordering::Equal {
            return ordering;
        }
    }
    std::cmp::Ordering::Equal
}

/// The phrase the daemon puts in an `OsUpdate` result when the install is staged but the host is
/// not on the new version yet, and which `patch_cycle` looks for to tell the user to reboot.
///
/// A substring of a human-readable message rather than a field, because `queue::RequestResult` is
/// deliberately just `{success, output}` — a two-field protocol that both agents' queues share.
/// Kept here, as one constant used by both ends, so the message and the test for it cannot drift.
pub const RESTART_REQUIRED_MARKER: &str = "restart required";

/// Whether this Mac is Apple silicon, and therefore whether `softwareupdate -i` will demand a
/// volume owner's authorization at all.
///
/// `hw.optional.arm64` rather than `cfg!(target_arch = ...)`: a Rosetta-translated build of this
/// agent reports `x86_64` for itself while running on hardware that very much does need the
/// password, and the flags are hardware-conditional, not binary-conditional. `--user` and
/// `--stdinpass` do not exist on Intel, where passing them is an error rather than a no-op.
pub fn is_apple_silicon() -> bool {
    let mut value: i32 = 0;
    let mut length = std::mem::size_of::<i32>();
    // SAFETY: `hw.optional.arm64` is an integer sysctl, `length` names the buffer's real size, and
    // the NUL-terminated name outlives the call. Mirrors `queue::boot_epoch`.
    let status = unsafe {
        libc::sysctlbyname(
            b"hw.optional.arm64\0".as_ptr().cast::<libc::c_char>(),
            (&mut value as *mut i32).cast::<libc::c_void>(),
            &mut length,
            std::ptr::null_mut(),
            0,
        )
    };

    // The sysctl is simply absent on Intel, where the call fails rather than returning zero.
    status == 0 && value == 1
}

/// Whether `username` may authorize a macOS install on this Mac — i.e. whether APFS considers it a
/// **volume owner** of the boot volume.
///
/// Checked in the per-user half *before* an OS-update request is ever submitted, and that ordering
/// is the whole point. `softwareupdate --user` takes "an owner user"; naming an account that isn't
/// one fails with `Failed to authenticate` — but only *after* the download, which for this fleet's
/// current listing is between 2.9GB and 11.7GB. That is precisely the failure this module was
/// changed to fix, and it would come straight back, an hour or more at a time, for any host whose
/// console user is a standard account or a second administrator that never got a secure token.
///
/// Volume ownership is not the same thing as being an administrator, which is the trap: an account
/// created by MDM, or one migrated onto an Apple silicon Mac, can be in `admin` and still not be an
/// owner. `dscl` gives the account's `GeneratedUID`, `diskutil apfs listUsers /` gives the UUIDs
/// APFS will accept, and this is their intersection.
pub fn is_volume_owner(username: &str) -> Result<bool> {
    let generated_uid_output = Command::new("dscl")
        .args([".", "-read", &format!("/Users/{username}"), "GeneratedUID"])
        .output()
        .context("failed to run dscl to look up the console user's GeneratedUID")?;

    let generated_uid = parse_generated_uid(&String::from_utf8_lossy(&generated_uid_output.stdout))
        .with_context(|| format!("dscl did not report a GeneratedUID for '{username}'"))?;

    let list_users_output = Command::new("diskutil")
        .args(["apfs", "listUsers", "/"])
        .output()
        .context("failed to run diskutil apfs listUsers /")?;

    let owners = parse_volume_owner_uuids(&String::from_utf8_lossy(&list_users_output.stdout));
    Ok(owners.iter().any(|owner| owner.eq_ignore_ascii_case(&generated_uid)))
}

/// The UUID out of `dscl . -read /Users/<name> GeneratedUID`, whose output is the single line
/// `GeneratedUID: 2DB09770-999B-43FB-99AF-2B878E4FE971`.
fn parse_generated_uid(text: &str) -> Option<String> {
    text.lines().find_map(|line| {
        let value = line.split_once("GeneratedUID:")?.1.trim();
        (!value.is_empty()).then(|| value.to_string())
    })
}

/// Every cryptographic user `diskutil apfs listUsers /` marks as a volume owner. Its output is a
/// little ASCII tree — each user is introduced by a `+-- <uuid>` line and described by the indented
/// lines under it, so ownership is read from the block a UUID opens rather than from its own line:
///
/// ```text
/// Cryptographic users for disk3s1s1 (2 found)
/// |
/// +-- 2DB09770-999B-43FB-99AF-2B878E4FE971
/// |   Type: Local Open Directory User
/// |   Volume Owner: Yes
/// |
/// +-- EBC6C064-0000-11AA-AA11-00306543ECAC
///     Type: Personal Recovery User
///     Volume Owner: Yes
/// ```
///
/// Note the recovery key is an owner too, and is never an account anyone can be logged in as —
/// which is why the caller intersects this with a specific user's UID rather than just counting
/// owners.
fn parse_volume_owner_uuids(text: &str) -> Vec<String> {
    let mut owners = Vec::new();
    let mut current: Option<String> = None;

    for line in text.lines() {
        if let Some((_, uuid)) = line.split_once("+-- ") {
            current = Some(uuid.trim().to_string());
        } else if line.contains("Volume Owner: Yes") {
            if let Some(uuid) = current.take() {
                owners.push(uuid);
            }
        }
    }

    owners
}

/// Collapses `softwareupdate`'s download progress into one line per download.
///
/// It writes `Downloading: 12.30%` with no line ending, hundreds of times, and repeats the same
/// percentage while a large file is in flight — a single 2.9GB download produced **103KB** of it,
/// which went verbatim into `daemon.log`, into the queue result file, and (had the report existed)
/// would have been the only thing left after `upgrade::truncate_for_report` kept the tail. The
/// failure that mattered was the last three lines; everything before them was this.
///
/// Kept as text rather than suppressed at the source because `softwareupdate` has no quiet flag,
/// and the *count* is worth keeping: it distinguishes a download that stalled at 95% from one that
/// never started.
fn condense_progress(text: &str) -> String {
    const MARKER: &str = "Downloading: ";

    let mut out = String::with_capacity(text.len().min(4096));
    let mut rest = text;

    while let Some(index) = rest.find(MARKER) {
        out.push_str(&rest[..index]);
        rest = &rest[index..];

        // Consume the whole run of adjacent progress readings, remembering only the last.
        let mut last: Option<&str> = None;
        let mut count = 0usize;
        while let Some(after_marker) = rest.strip_prefix(MARKER) {
            let Some(percent_end) = after_marker.find('%') else { break };
            let value = &after_marker[..percent_end];
            if value.is_empty() || !value.chars().all(|c| c.is_ascii_digit() || c == '.') {
                break;
            }
            last = Some(value);
            count += 1;
            rest = &after_marker[percent_end + 1..];
        }

        match (count, last) {
            // Not a reading after all ("Downloading: macOS Tahoe 26.7"): emit the marker literally
            // and carry on past it, which also guarantees this loop makes progress.
            (0, _) => {
                out.push_str(MARKER);
                rest = &rest[MARKER.len()..];
            }
            (1, Some(value)) => out.push_str(&format!("{MARKER}{value}%")),
            (count, Some(value)) => out.push_str(&format!("{MARKER}{value}% [{count} readings collapsed]")),
            (_, None) => unreachable!("a non-zero count always recorded a reading"),
        }
    }

    out.push_str(rest);
    out
}

/// Fetches every pending update's bits without installing any of them, one label at a time.
///
/// Root only (`man softwareupdate`: everything but `--list` needs admin). This was written as a
/// single `-d -a` on the belief that `-d` "just downloads" and so, unlike `-i`, needs no volume
/// owner. **That belief was wrong**, and this fleet's own Mac disproved it twice. On Apple silicon
/// `-d` downloads *and prepares*, and the preparation wants the same volume owner the install does,
/// so a run whose asset was already staged ended:
///
/// ```text
/// Downloading macOS Tahoe 26.7
/// Downloaded: macOS Tahoe 26.7
/// Failed to authenticate
/// Password:
/// ```
///
/// in eight seconds — the whole of it preparation, nothing of it download. See
/// `clients/macos-agent/CLAUDE.md`.
///
/// Two consequences, and this function's shape is both of them:
///
/// 1. **That exit 1 is not a failed download.** The bits are on disk and only the preparation is
///    outstanding, which [`install`] performs with the password in hand. Treating it as a failure
///    filed a Failed Updates row every cycle for a step whose work had succeeded, and stopped the
///    cycle before the install it was the preamble to.
/// 2. **`-a` stops at the first update it cannot prepare.** Everything behind it in the listing —
///    on the host above, Safari and the 11.7GB macOS 27 — was never fetched, so `install`'s `-i -a`
///    would have downloaded them *after* the console user typed their password: exactly the
///    hour-late forced reboot this separate step exists to prevent. Hence one `-d --label` per
///    label rather than one `-d -a`. A label that cannot be prepared no longer stops the fetch of
///    the ones behind it.
///
/// The download is still the hour-plus, and still runs with nobody being asked for anything.
/// Re-running it once the assets are present is close to free — the eight seconds above — so a
/// cycle whose password prompt went unanswered brings the next one's prompt up almost at once.
pub fn download() -> Result<()> {
    let labels = list_labels()?;
    if labels.is_empty() {
        crate::logging::info("softwareupdate -l lists nothing to download");
        return Ok(());
    }

    // Collected rather than returned at the first one: a Safari label that will not download is no
    // reason to leave the macOS asset unfetched, and the caller is owed all of it in one message.
    let mut failures = Vec::new();
    for label in &labels {
        match download_one(label) {
            Ok(DownloadOutcome::Downloaded) => crate::logging::info(&format!("downloaded '{label}'")),
            Ok(DownloadOutcome::DownloadedUnprepared) => crate::logging::info(&format!(
                "downloaded '{label}', but preparing it needs a volume owner — left to the authorized install"
            )),
            Err(err) => {
                crate::logging::error(&format!("could not download '{label}': {err:#}"));
                failures.push(format!("{label}: {err:#}"));
            }
        }
    }

    if !failures.is_empty() {
        anyhow::bail!(
            "softwareupdate could not download {} of the {} pending update(s): {}",
            failures.len(),
            labels.len(),
            failures.join("; ")
        );
    }

    Ok(())
}

/// What one label's download achieved. Neither variant installs anything or restarts anything.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
enum DownloadOutcome {
    /// `softwareupdate -d` returned cleanly: fetched, and prepared if it needed preparing.
    Downloaded,
    /// The asset is on disk but unprepared, because preparation asked for a volume owner this
    /// daemon is not — see [`download`]. Not an error: [`install`] prepares and installs it in the
    /// same call, with the password the console user supplies between the two steps.
    DownloadedUnprepared,
}

/// Downloads exactly one label's assets.
///
/// `--label` rather than `-a` so one unpreparable update does not strand the rest — see
/// [`download`]. The label is whatever `softwareupdate -l` printed, passed as a single argument
/// because macOS's labels contain spaces (`macOS Tahoe 26.7-25G229`).
fn download_one(label: &str) -> Result<DownloadOutcome> {
    let output = Command::new("softwareupdate")
        .args(["-d", "--label", label])
        .output()
        .with_context(|| format!("failed to run softwareupdate -d --label '{label}'"))?;

    let combined = condense_progress(&format!(
        "{}{}",
        String::from_utf8_lossy(&output.stdout),
        String::from_utf8_lossy(&output.stderr)
    ));
    crate::logging::info(&format!(
        "softwareupdate -d --label '{label}' finished: success={} output={}",
        output.status.success(),
        combined.trim()
    ));

    if output.status.success() {
        return Ok(DownloadOutcome::Downloaded);
    }
    if is_preparation_authorization_wall(&combined) {
        return Ok(DownloadOutcome::DownloadedUnprepared);
    }

    anyhow::bail!("softwareupdate -d --label '{label}' exited with {}: {}", output.status, combined.trim())
}

/// Whether a non-zero `-d` is the Apple-silicon *preparation* wall rather than a download that did
/// not happen.
///
/// Both halves are required, and that is the point of the check. `Downloaded:` on its own says the
/// asset reached the disk; `Failed to authenticate` on its own could be any refusal, including one
/// raised before a byte moved. Only together do they mean what [`download`] then acts on — fetched,
/// unprepared, and safe to leave to [`install`]. A download that genuinely failed prints no
/// `Downloaded:` line and so still fails, which is what keeps the Failed Updates screen honest.
fn is_preparation_authorization_wall(combined: &str) -> bool {
    combined.contains("Downloaded:") && combined.contains("Failed to authenticate")
}

/// Every label in a `softwareupdate -l` listing, in the order macOS printed them.
///
/// Root is not needed for the listing itself (see [`check`]), but this is deliberately its own call
/// rather than a field grown onto [`OsUpdateStatus`]: that type answers the admin UI's question
/// ("is an OS update pending, and to what version"), and the daemon's download step asks a
/// different one — *which* labels to fetch, macOS and Safari and firmware alike.
fn list_labels() -> Result<Vec<String>> {
    let output = Command::new("softwareupdate").arg("-l").output().context("failed to run softwareupdate -l")?;

    let combined = format!(
        "{}{}",
        String::from_utf8_lossy(&output.stdout),
        String::from_utf8_lossy(&output.stderr)
    );
    parse_label_listing(&combined).with_context(|| format!("softwareupdate -l exited with {}", output.status))
}

/// Reads a `softwareupdate -l` listing as one of three answers, not two: some labels, *none because
/// macOS said so*, or a listing this cannot make sense of.
///
/// The third case has to be an error, and getting that wrong would undo the whole download step.
/// Like [`check`], this cannot use the exit status — `-l` exits non-zero when nothing is available —
/// so an unreadable listing is indistinguishable from an empty one except by its text. Read as
/// empty, a `-l` that failed transiently (a laptop that just woke with no network, a wedged
/// softwareupdate daemon) would make [`download`] report success having fetched nothing at all, and
/// the install that follows would then download every gigabyte of it *after* the console user typed
/// their password. That is exactly the hour-late forced reboot the separate download step exists to
/// prevent, so "I could not tell" fails loudly instead.
fn parse_label_listing(combined: &str) -> Result<Vec<String>> {
    if combined.contains("No new software available") {
        return Ok(Vec::new());
    }

    let labels = parse_labels(combined);
    if labels.is_empty() {
        anyhow::bail!("softwareupdate -l listed no labels and did not say there were none: {}", combined.trim());
    }
    Ok(labels)
}

/// The pure text half of [`parse_label_listing`]. A listing line reads
/// `* Label: macOS Tahoe 26.7-25G229`, and the label runs to the end of the line — it is not
/// comma-delimited the way the `Title:` line's fields are, so nothing may be split off it.
fn parse_labels(text: &str) -> Vec<String> {
    text.lines()
        .filter_map(|line| line.trim_start().strip_prefix("* Label:"))
        .map(|label| label.trim().to_string())
        .filter(|label| !label.is_empty())
        .collect()
}

/// Installs every pending macOS update and restarts the Mac to finish. Root only — this is the
/// daemon's answer to an OS-update request (see `queue`), and it is always this same fixed
/// `-i -a -R` whatever the request said, which is what makes a forged request harmless.
///
/// # `-R` reboots this machine, and usually without asking anyone
///
/// `softwareupdate -i` *without* `-R` only takes the update to `SUMAC_PHASE_PREPARED`: it is staged,
/// `softwareupdate -l` still lists it, and the host stays on the old version until somebody
/// restarts. `-R` is what actually completes it, and it is what this fleet wants — an update that
/// waits indefinitely for a person to reboot is not an unattended patching system.
///
/// The cost is real and worth stating plainly. `man softwareupdate`: "If the user invoking this tool
/// is logged in then macOS will attempt to quit all applications, logout, and restart. If the user
/// is not logged in, macOS will trigger a forced reboot if necessary." The invoking user here is
/// **root, in a LaunchDaemon** — not logged in — so the forced path is the likely one, and unsaved
/// work goes with it. `--force` is deliberately *not* passed on top: it would remove even the chance
/// that macOS treats the `--user` account as logged in and quits applications gracefully first.
///
/// Both dialogs the console user sees say the Mac will restart on its own — see
/// `dialogs::install_password_message`, which is the last thing anyone is asked before this runs.
/// How long they then wait for it is [`download`]'s doing: run first, it leaves this call with the
/// assets already staged, so the restart follows the password by minutes rather than by the hour or
/// more the fetch takes. What it cannot leave staged is the *preparation* — that needs the very
/// volume owner being asked for here — so an update whose download step reported
/// `DownloadedUnprepared` is prepared and installed in this one call.
///
/// **Nothing after the `softwareupdate` call is guaranteed to run.** The reboot happens inside it,
/// so this function does not return, `report_patched` is never sent, and `process_queue` never gets
/// to remove the request. All three are handled where they land: the server re-derives the host's
/// pending state from `softwareupdate -l` at the next check-in, and `queue::is_stale`'s boot check
/// discards the surviving request unrun. The credentials are already gone — `take_auth` unlinks the
/// sidecar before the install starts, precisely so a reboot cannot strand a password on disk.
///
/// **`auth` is not optional in practice on Apple silicon.** `softwareupdate -i` needs a *volume
/// owner* to authorize a macOS install there — `man softwareupdate` calls `--user` "an owner user to
/// authorize installation", and both it and `--stdinpass` are documented "Apple silicon only".
/// Being root in a LaunchDaemon is not enough and never was: root is not an APFS cryptographic
/// user, so the install downloads in full and then dies on
///
/// ```text
/// Downloaded: macOS Tahoe 26.7
/// Failed to authenticate
/// Password:
/// ```
///
/// — an interactive prompt, on a process with no terminal, after 80 minutes of downloading. That is
/// the same masked-password wall root-requiring Homebrew casks hit (see `upgrade.rs`), and it is
/// what the whole password handoff in `queue` exists to get past. `None` is still accepted so an
/// Intel host, where neither flag exists, keeps working unchanged.
///
/// The password reaches `softwareupdate` over a pipe and nowhere else — never argv (visible in
/// `ps`), never a temporary file of this function's making.
pub fn install(auth: Option<&InstallAuth>) -> Result<InstallOutcome> {
    let mut command = Command::new("softwareupdate");
    command.args(["-i", "-a", "-R"]);
    if let Some(auth) = auth {
        command.args(["--user", &auth.user, "--stdinpass"]);
    }

    let mut child = command
        .stdin(Stdio::piped())
        .stdout(Stdio::piped())
        .stderr(Stdio::piped())
        .spawn()
        .context("failed to run softwareupdate -i -a -R")?;

    {
        let mut stdin = child.stdin.take().context("softwareupdate gave us no stdin to authorize through")?;
        if let Some(auth) = auth {
            // Errors here are reported without the password in them, which is why this isn't
            // written as one chained `context` over a closure that formats the request.
            stdin
                .write_all(auth.password.as_bytes())
                .and_then(|()| stdin.write_all(b"\n"))
                .context("could not hand the authorization password to softwareupdate")?;
        }
        // Dropped (and so closed) here rather than at the end of the function: without it
        // `--stdinpass` waits on a read that never ends, and in the no-auth case softwareupdate
        // sees EOF instead of a terminal it could prompt at.
    }

    let output = child.wait_with_output().context("softwareupdate -i -a -R did not finish")?;

    let combined = condense_progress(&format!(
        "{}{}",
        String::from_utf8_lossy(&output.stdout),
        String::from_utf8_lossy(&output.stderr)
    ));
    crate::logging::info(&format!(
        "softwareupdate -i -a -R finished without restarting: success={} authorized_as={:?} output={}",
        output.status.success(),
        auth.map(|auth| auth.user.as_str()),
        combined.trim()
    ));

    if !output.status.success() {
        if combined.contains("Failed to authenticate") {
            anyhow::bail!(
                "softwareupdate refused the authorization{}: macOS needs a volume owner's password to \
                 install an update on Apple silicon, and the one supplied was not accepted. Output: {}",
                auth.map(|auth| format!(" for '{}'", auth.user)).unwrap_or_else(|| " (none was supplied)".to_string()),
                combined.trim()
            );
        }
        anyhow::bail!("softwareupdate -i -a -R exited with {}: {}", output.status, combined.trim());
    }

    // Ground truth rather than a phrase matched out of the output: if macOS still offers a macOS
    // update, this host is not on the new version yet, whatever softwareupdate just printed.
    //
    // `latest_version`, not `available` — `-a` also installs Safari and the like, and one of those
    // still being listed says nothing about whether the *system* update landed. And a check that
    // could not run at all resolves to "restart required", because the only consequence of being
    // wrong that way is a pending flag the next check-in clears by itself, where the other way
    // round is the dashboard flicker this exists to stop.
    let restart_required = check().map(|status| status.latest_version.is_some()).unwrap_or(true);
    Ok(InstallOutcome { restart_required })
}

#[derive(Debug, Serialize)]
#[serde(rename_all = "camelCase")]
struct ReportOsPatchResultRequest<'a> {
    serial_number: &'a str,
}

/// The name an OS-update failure is filed under on the admin UI's Failed Updates screen.
///
/// Reusing the application-failure route rather than adding an OS-specific one is deliberate:
/// `/api/patch-failures` is already in nginx's agent-certificate regex and already carries
/// `[RequireAgentIdentity]`, and `ReportPatchFailureCommandHandler` already copes with a name that
/// resolves to no upgrade path — it files the failure with a null platform, which the screen shows
/// as a failure it cannot offer a repair for. That is exactly right here: no AI-authored script
/// exists to repair, and none should be offered.
pub const OS_FAILURE_APPLICATION_NAME: &str = "macOS";

/// Tells the server this host's pending macOS update was just successfully installed, so its
/// pending-update flag and target version clear immediately rather than waiting on this host's
/// next check-in to re-derive them from a fresh `softwareupdate -l` run. Best-effort, the same as
/// `upgrade::report_patch_result`: the update already succeeded locally by the time this is
/// called, so a failure here is only logged, never treated as undoing the install.
///
/// **Only called when no restart is outstanding** — see [`InstallOutcome::restart_required`]. An
/// update that is merely staged has not changed this host's version, and reporting it as installed
/// made the dashboard clear the flag and then set it again on the next check-in.
pub fn report_patched(client: &reqwest::blocking::Client, config: &Config, serial_number: &str) {
    let request = ReportOsPatchResultRequest { serial_number };

    match client.post(config.os_patch_result_url()).json(&request).send() {
        Ok(response) if response.status().is_success() => {
            crate::logging::info("reported successful macOS update install to the server");
        }
        Ok(response) => {
            crate::logging::warn(&format!("server rejected OS patch-result report (HTTP {})", response.status()));
        }
        Err(err) => {
            crate::logging::warn(&format!("could not report the successful macOS update install to the server: {err:#}"));
        }
    }
}

/// Files a failed macOS install on the Failed Updates screen, under [`OS_FAILURE_APPLICATION_NAME`].
///
/// Before this existed an OS-update failure was logged on the host and nowhere else: `patch_cycle`
/// called `logging::error` and moved on, `os_update_status.available` stayed true, and the admin UI
/// showed a host that simply never updated with no indication why. The authorization wall that
/// prompted all of this sat unreported on a Mac for a day for exactly that reason.
///
/// Called by the root daemon, which is the side that ran the install and holds its output — the
/// same rule `upgrade::report_patch_failure` follows, and for the same reason: reporting from the
/// per-user side as well would record every failure twice.
pub fn report_failed(
    client: &reqwest::blocking::Client,
    config: &Config,
    serial_number: &str,
    attempted_version: Option<&str>,
    error: &anyhow::Error,
) {
    let installed_version = crate::system_info::operating_system().unwrap_or_else(|_| "unknown".to_string());

    crate::upgrade::report_failure(
        client,
        config,
        serial_number,
        OS_FAILURE_APPLICATION_NAME,
        &installed_version,
        attempted_version,
        error,
    );
}

#[cfg(test)]
mod tests {
    use super::*;

    /// A real `softwareupdate -l` listing from the Mac this module's authorization handling was
    /// written for — three labels, only two of them macOS, and the non-macOS one listed first.
    const REAL_LISTING: &str = concat!(
        "Software Update Tool\n\n",
        "Finding available software\n",
        "Software Update found the following new or updated software:\n",
        "* Label: Safari27.0TahoeAuto-27.0\n",
        "\tTitle: Safari, Version: 27.0, Size: 249465KiB, Recommended: YES, \n",
        "* Label: macOS Tahoe 26.7-25G229\n",
        "\tTitle: macOS Tahoe 26.7, Version: 26.7, Size: 2960352KiB, Recommended: YES, Action: restart, \n",
        "* Label: macOS 27-26A428\n",
        "\tTitle: macOS 27, Version: 27, Size: 11727573KiB, Recommended: YES, Action: restart, \n",
    );

    #[test]
    fn parse_check_output_no_updates_available() {
        let combined = "Software Update Tool\n\nNo new software available.\n";

        let status = parse_check_output(combined);

        assert_eq!(status, OsUpdateStatus::default());
        assert!(!status.available);
        assert_eq!(status.latest_version, None);
    }

    #[test]
    fn parse_check_output_one_update_available_modern_phrasing() {
        // The multi-line listing format seen on recent macOS versions.
        let combined = concat!(
            "Software Update Tool\n\n",
            "Finding available software\n",
            "Software Update found the following new or updated software:\n",
            "* Label: macOS Sequoia 15.1-24B83\n",
            "\tTitle: macOS Sequoia 15.1, Version: 15.1, Size: 3319833KiB, Recommended: YES,\n",
        );

        let status = parse_check_output(combined);

        assert!(status.available);
        assert_eq!(status.latest_version.as_deref(), Some("15.1"));
    }

    #[test]
    fn parse_check_output_one_update_available_legacy_phrasing() {
        // Older macOS versions omit the "Software Update found ..." sentence and only ever print
        // the "* Label:" listing lines directly.
        let combined = "* Label: macOS Ventura 13.6-22G120\n\tTitle: macOS Ventura 13.6, Version: 13.6, Size: 1234567KiB, Recommended: YES,\n";

        let status = parse_check_output(combined);

        assert!(status.available);
        assert_eq!(status.latest_version.as_deref(), Some("13.6"));
    }

    #[test]
    fn parse_check_output_available_but_no_parseable_version() {
        // A listing that trips the "available" detection but has no "Version:" field at all —
        // should still report available, just without a version.
        let combined = "Software Update found the following new or updated software:\n* Label: SomeUpdate\n";

        let status = parse_check_output(combined);

        assert!(status.available);
        assert_eq!(status.latest_version, None);
    }

    /// The regression this module's version parsing was changed for: Safari's 27.0 is the first
    /// `Version:` in the output, and reporting it made the admin UI show 27.0 as this host's
    /// pending macOS version while the update downloading was 26.7.
    #[test]
    fn parse_latest_version_ignores_a_non_macos_label_listed_first() {
        let status = parse_check_output(REAL_LISTING);

        assert!(status.available);
        assert_ne!(status.latest_version.as_deref(), Some("27.0"), "that is Safari's version, not macOS's");
        assert_eq!(status.latest_version.as_deref(), Some("27"));
    }

    /// `-i -a` installs every applicable update, so a host offered both 26.7 and 27 ends up on 27 —
    /// and a plain string comparison would have picked "26.7" as the larger.
    #[test]
    fn parse_latest_version_reports_the_highest_macos_offered() {
        let text = "\tTitle: macOS 27, Version: 27,\n\tTitle: macOS Tahoe 26.7, Version: 26.7,\n";

        assert_eq!(parse_latest_version(text).as_deref(), Some("27"));
    }

    #[test]
    fn parse_latest_version_returns_none_when_nothing_macos_is_listed() {
        let text = "\tTitle: Safari, Version: 27.0, Size: 1\n\tTitle: XProtectPlistConfigData, Version: 5300, Size: 2\n";

        assert_eq!(parse_latest_version(text), None);
    }

    #[test]
    fn parse_latest_version_trims_whitespace_around_the_value() {
        let text = "Title: macOS Tahoe, Version:   15.1  , Size: 1\n";

        assert_eq!(parse_latest_version(text).as_deref(), Some("15.1"));
    }

    #[test]
    fn parse_latest_version_returns_none_when_the_field_is_empty() {
        let text = "Title: macOS Tahoe, Version: , Size: 1\n";

        assert_eq!(parse_latest_version(text), None);
    }

    #[test]
    fn parse_latest_version_returns_none_when_no_version_field_exists() {
        let text = "Title: macOS Tahoe, Size: 1\n";

        assert_eq!(parse_latest_version(text), None);
    }

    #[test]
    fn compare_versions_orders_numerically_not_lexicographically() {
        assert_eq!(compare_versions("26.7", "27"), std::cmp::Ordering::Less);
        assert_eq!(compare_versions("26.10", "26.7"), std::cmp::Ordering::Greater);
        assert_eq!(compare_versions("15.1", "15.1.0"), std::cmp::Ordering::Equal);
        assert_eq!(compare_versions("26.7", "26.7"), std::cmp::Ordering::Equal);
    }

    #[test]
    fn compare_versions_treats_an_unparseable_component_as_zero() {
        assert_eq!(compare_versions("26.beta", "26.0"), std::cmp::Ordering::Equal);
        assert_eq!(compare_versions("26.beta", "26.1"), std::cmp::Ordering::Less);
    }

    /// The shape that produced 103KB of queue result for one download.
    #[test]
    fn condense_progress_collapses_a_run_of_readings_to_the_last() {
        let text = "Downloading macOS Tahoe 26.7\nDownloading: 0.10%Downloading: 1.00%Downloading: 100.00%\nDownloaded: macOS Tahoe 26.7\n";

        let condensed = condense_progress(text);

        assert_eq!(
            condensed,
            "Downloading macOS Tahoe 26.7\nDownloading: 100.00% [3 readings collapsed]\nDownloaded: macOS Tahoe 26.7\n"
        );
    }

    #[test]
    fn condense_progress_leaves_a_single_reading_alone() {
        assert_eq!(condense_progress("Downloading: 50.00%\ndone\n"), "Downloading: 50.00%\ndone\n");
    }

    /// "Downloading macOS Tahoe 26.7" has no colon and is not a reading; "Downloading: macOS" has a
    /// colon but no percentage. Neither may be eaten, and neither may spin the loop.
    #[test]
    fn condense_progress_leaves_text_that_only_looks_like_a_reading() {
        let text = "Downloading: macOS Tahoe 26.7\nDownloading macOS 27\n";

        assert_eq!(condense_progress(text), text);
    }

    #[test]
    fn condense_progress_keeps_the_failure_that_follows_the_download() {
        let spam: String = (0..500).map(|_| "Downloading: 95.60%").collect();
        let text = format!("{spam}\nDownloaded: macOS Tahoe 26.7\nFailed to authenticate\nPassword:\n");

        let condensed = condense_progress(&text);

        assert!(condensed.len() < 200, "the whole thing should now fit in a log line, was {}", condensed.len());
        assert!(condensed.contains("[500 readings collapsed]"));
        assert!(condensed.contains("Failed to authenticate"));
    }

    /// The listing that is on the host this was written for, labels and all.
    const SAMPLE_LISTING: &str = "Software Update Tool\n\nFinding available software\nSoftware Update found the following new or updated software:\n* Label: Safari27.0TahoeAuto-27.0\n\tTitle: Safari, Version: 27.0, Size: 249465KiB, Recommended: YES, \n* Label: macOS Tahoe 26.7-25G229\n\tTitle: macOS Tahoe 26.7, Version: 26.7, Size: 2960352KiB, Recommended: YES, Action: restart, \n* Label: macOS 27-26A428\n\tTitle: macOS 27, Version: 27, Size: 11727573KiB, Recommended: YES, Action: restart, \n";

    #[test]
    fn parse_labels_reads_every_label_in_order() {
        assert_eq!(
            parse_labels(SAMPLE_LISTING),
            vec!["Safari27.0TahoeAuto-27.0", "macOS Tahoe 26.7-25G229", "macOS 27-26A428"]
        );
    }

    /// A label is the rest of its line — spaces, dots and the build suffix included. Splitting one
    /// on whitespace or on a comma the way the `Title:` line's fields are split would hand
    /// `softwareupdate --label` a name no update has.
    #[test]
    fn parse_labels_keeps_a_label_that_contains_spaces_whole() {
        assert_eq!(parse_labels("* Label: macOS Tahoe 26.7-25G229\n"), vec!["macOS Tahoe 26.7-25G229"]);
    }

    #[test]
    fn parse_label_listing_reads_macoss_own_word_for_an_empty_listing() {
        let listing = "Software Update Tool\n\nFinding available software\nNo new software available.\n";
        assert_eq!(parse_label_listing(listing).unwrap(), Vec::<String>::new());
    }

    #[test]
    fn parse_label_listing_returns_the_labels_it_found() {
        assert_eq!(parse_label_listing(SAMPLE_LISTING).unwrap().len(), 3);
    }

    /// The case that must not be read as "nothing to download": a `-l` that failed rather than one
    /// that found nothing. Reported as empty it would make the download step succeed having fetched
    /// nothing, leaving every gigabyte to the install that runs after the password is collected.
    #[test]
    fn parse_label_listing_refuses_a_listing_it_cannot_read() {
        let err = parse_label_listing("Software Update Tool\n\nFinding available software\n").unwrap_err();
        assert!(err.to_string().contains("did not say there were none"), "{err}");
    }

    #[test]
    fn parse_labels_finds_nothing_in_a_listing_with_no_updates() {
        assert!(parse_labels("Software Update Tool\n\nFinding available software\nNo new software available.\n").is_empty());
    }

    /// The exact output of the run this whole per-label shape exists for: the asset was already
    /// staged, so the download finished in seconds and everything after it was the preparation
    /// asking for a volume owner.
    #[test]
    fn is_preparation_authorization_wall_recognises_a_fetch_that_only_failed_to_prepare() {
        let combined = "Software Update Tool\n\nFinding available software\nDownloading macOS Tahoe 26.7\n\nDownloaded: macOS Tahoe 26.7\nFailed to authenticate\nPassword:";
        assert!(is_preparation_authorization_wall(combined));
    }

    /// Half the signature is not the signature. A refusal raised before anything was fetched is a
    /// real failure of the download step and has to stay one, or the Failed Updates screen goes
    /// quiet about a host that never gets its bits.
    #[test]
    fn is_preparation_authorization_wall_rejects_an_authorization_failure_with_no_download() {
        assert!(!is_preparation_authorization_wall("Failed to authenticate\nPassword:"));
    }

    #[test]
    fn is_preparation_authorization_wall_rejects_a_download_that_failed_some_other_way() {
        assert!(!is_preparation_authorization_wall("Downloaded: Safari\nThe operation couldn\u{2019}t be completed. (NSURLErrorDomain error -1009.)"));
    }

    #[test]
    fn parse_generated_uid_reads_dscls_single_line() {
        let text = "GeneratedUID: 2DB09770-999B-43FB-99AF-2B878E4FE971\n";

        assert_eq!(parse_generated_uid(text).as_deref(), Some("2DB09770-999B-43FB-99AF-2B878E4FE971"));
    }

    #[test]
    fn parse_generated_uid_returns_none_when_dscl_found_no_such_user() {
        let text = "<dscl_cmd> DS Error: -14136 (eDSRecordNotFound)\n";

        assert_eq!(parse_generated_uid(text), None);
    }

    /// The real `diskutil apfs listUsers /` tree, including its `|` gutter and the recovery user
    /// whose block is indented differently because it is last.
    #[test]
    fn parse_volume_owner_uuids_reads_the_diskutil_tree() {
        let text = concat!(
            "Cryptographic users for disk3s1s1 (2 found)\n",
            "|\n",
            "+-- 2DB09770-999B-43FB-99AF-2B878E4FE971\n",
            "|   Type: Local Open Directory User\n",
            "|   Volume Owner: Yes\n",
            "|\n",
            "+-- EBC6C064-0000-11AA-AA11-00306543ECAC\n",
            "    Type: Personal Recovery User\n",
            "    Volume Owner: Yes\n",
        );

        let owners = parse_volume_owner_uuids(text);

        assert_eq!(
            owners,
            vec!["2DB09770-999B-43FB-99AF-2B878E4FE971".to_string(), "EBC6C064-0000-11AA-AA11-00306543ECAC".to_string()]
        );
    }

    /// The case the whole check exists for: an admin account that is not a volume owner. Naming it
    /// to `--user` costs a multi-gigabyte download and then fails to authenticate.
    #[test]
    fn parse_volume_owner_uuids_omits_a_user_that_is_not_an_owner() {
        let text = concat!(
            "Cryptographic users for disk1s1 (2 found)\n",
            "|\n",
            "+-- 11111111-1111-1111-1111-111111111111\n",
            "|   Type: Local Open Directory User\n",
            "|   Volume Owner: No\n",
            "|\n",
            "+-- 22222222-2222-2222-2222-222222222222\n",
            "    Type: Local Open Directory User\n",
            "    Volume Owner: Yes\n",
        );

        let owners = parse_volume_owner_uuids(text);

        assert_eq!(owners, vec!["22222222-2222-2222-2222-222222222222".to_string()]);
    }

    /// The property `InstallAuth`'s hand-written `Debug` exists for — a derived one would print
    /// the password, and `{:?}` is this codebase's habit in log lines.
    #[test]
    fn install_auth_debug_does_not_print_the_password() {
        let auth = InstallAuth { user: "david".to_string(), password: "correct horse battery".to_string() };

        let rendered = format!("{auth:?}");

        assert!(!rendered.contains("correct horse battery"), "the password leaked: {rendered}");
        assert!(rendered.contains("david"), "the account name is not a secret and is worth logging");
    }
}
