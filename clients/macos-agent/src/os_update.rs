use std::fmt;
use std::io::Write;
use std::path::Path;
use std::process::{Command, Stdio};

use anyhow::{Context, Result};
use serde::{Deserialize, Serialize};

use crate::config::Config;

/// The result of a standard OS-update check: whether one is pending, and the version it would
/// bring the host to, when the check can determine that.
#[derive(Debug, Clone, Default, PartialEq, Eq)]
pub struct OsUpdateStatus {
    pub available: bool,
    pub latest_version: Option<String>,
    /// Every label the listing offered, macOS and otherwise, in the order it printed them.
    ///
    /// Carried on the same `softwareupdate -l` that answered the other two rather than fetched by a
    /// second run of it: `patch_cycle` needs the labels to ask whether the pre-fetch has staged
    /// them (see [`StagedDownloads::covers_the_macos_updates`]), and a second scan is both slower
    /// and one more thing that can blip — a listing that momentarily comes back empty would have
    /// the cycle decline to prompt for an update that is sitting ready on disk. The 19:46 run in
    /// this host's log is exactly that blip, on the download side.
    pub labels: Vec<String>,
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
    OsUpdateStatus { available, latest_version, labels: parse_labels(combined) }
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
        //
        // `trim_start_matches` is the whole of why this works on real output: `softwareupdate`
        // separates its readings with a **carriage return**, because it is overwriting one line on
        // a terminal rather than writing many. Without it the run ended at the first `\r`, every
        // reading was emitted as a run of one, and nothing was ever condensed — 157KB of
        // `Downloading: nn%` went into `daemon.log` verbatim on macOS 27's download while this
        // function sat in the path doing nothing. The test that was supposed to cover it used a
        // hand-written transcript with no `\r` in it.
        let mut last: Option<&str> = None;
        let mut count = 0usize;
        while let Some(after_marker) = rest.trim_start_matches(['\r', '\n']).strip_prefix(MARKER) {
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
///    hour-late forced reboot this separate step exists to prevent. Hence one `-d <label>` per
///    label rather than one `-d -a`. A label that cannot be prepared no longer stops the fetch of
///    the ones behind it.
///
/// The download is still the hour-plus, and still runs with nobody being asked for anything.
/// Re-running it once the assets are present is close to free — the eight seconds above — so a
/// cycle whose password prompt went unanswered brings the next one's prompt up almost at once.
pub fn download(on_progress: &(dyn Fn(DownloadProgress) + Sync)) -> Result<PrefetchOutcome> {
    let labels = list_labels()?;
    if labels.is_empty() {
        crate::logging::info("softwareupdate -l lists nothing to download");
        return Ok(PrefetchOutcome::default());
    }

    let mut staged = Vec::new();

    // Collected rather than returned at the first one: a Safari label that will not download is no
    // reason to leave the macOS asset unfetched, and the caller is owed all of it in one message.
    let mut failures = Vec::new();
    for (index, label) in labels.iter().enumerate() {
        let report = |percent| {
            on_progress(DownloadProgress {
                label: label.clone(),
                index,
                total: labels.len(),
                percent,
                epoch: now_epoch(),
            })
        };
        match download_one(label, &report) {
            Ok(DownloadOutcome::Downloaded) => {
                crate::logging::info(&format!("downloaded '{label}'"));
                staged.push(label.clone());
            }
            Ok(DownloadOutcome::DownloadedUnprepared) => {
                crate::logging::info(&format!(
                    "downloaded '{label}', but preparing it needs a volume owner — left to the authorized install"
                ));
                staged.push(label.clone());
            }
            Err(err) => {
                crate::logging::error(&format!("could not download '{label}': {err:#}"));
                failures.push((label.clone(), format!("{err:#}")));
            }
        }
    }

    // Both halves come back, and the caller writes both down. Returning an `Err` here — which is
    // what this did — threw away the record of everything that *had* been fetched, so the next
    // invocation saw no record, could not tell that from nothing-staged, and re-fetched the lot. An
    // hourly job doing that with 15GB is the failure this shape exists to prevent.
    //
    // Only a *macOS* label's failure is worth the caller's attention, and the asymmetry is the
    // point: a Safari label that would not download costs the install a few minutes fetching
    // 250MB, while a macOS label that would not download costs it the 11.7GB the whole ordering
    // exists to move out from behind the password prompt.
    Ok(PrefetchOutcome {
        staged,
        failed: failures.into_iter().map(|(label, _)| label).filter(|label| is_macos_label(label)).collect(),
    })
}

/// What one pre-fetch managed, both halves of it.
#[derive(Debug, Clone, Default, PartialEq, Eq)]
pub struct PrefetchOutcome {
    /// Labels now believed to be on disk.
    pub staged: Vec<String>,
    /// **macOS** labels this attempt could not fetch — the ones that will hold the authorization
    /// prompt back, and the ones [`needs_prefetch`] backs off on.
    pub failed: Vec<String>,
}

/// Whether a label names a macOS system update rather than one of the other things
/// `softwareupdate` offers. macOS prints them as `macOS Tahoe 26.7-25G229` and `macOS 27-26A428`
/// beside `Safari27.0TahoeAuto-27.0`, so the prefix is what separates them — and the title line of
/// the same listing agrees, which is what `parse_macos_title_version` reads.
fn is_macos_label(label: &str) -> bool {
    label.starts_with("macOS")
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

/// Downloads exactly one label's assets, reporting how far along it is as it goes.
///
/// **The label is positional, and the exit status is not to be trusted.** Both were got wrong first
/// time, and each was measured on `htw-m5pro-hobleyd` rather than reasoned about:
///
/// ```text
/// softwareupdate -d --label "macOS Tahoe 26.7-25G229"  ->  unrecognized option `--label'   exit 0
/// softwareupdate -d "macOS Tahoe 26.7-25G229"          ->  Downloaded: ... Failed to auth   exit 1
/// softwareupdate -d "definitely-not-an-update-9.9"     ->  No such update                   exit 0
/// ```
///
/// There is no `--label` flag — `softwareupdate -d` takes labels as bare arguments (its own usage
/// text: `<label> ...  specific updates`) — and a run that did nothing at all exits **zero** while
/// the run that fetched everything asked of it exits **one**. So the status is ignored entirely and
/// the text decides, the same rule [`check`] follows for `-l` and for the same reason.
///
/// The label goes in as a single argument because macOS's labels contain spaces
/// (`macOS Tahoe 26.7-25G229`), and it is whatever `softwareupdate -l` printed.
///
/// # Why this streams instead of using `Command::output`
///
/// `on_percent` is what puts a progress bar in the menu bar while the daemon pre-fetches, and
/// `output()` cannot feed one: it returns when the child exits, which for macOS 27 is an hour after
/// anybody wanted to know. So both pipes are read as they fill. Both, not just one — `softwareupdate`
/// splits its output across them, and reading one while the other's pipe buffer fills is the
/// classic way to deadlock a child that is only trying to talk to you.
fn download_one(label: &str, on_percent: &(dyn Fn(u8) + Sync)) -> Result<DownloadOutcome> {
    let mut child = Command::new("softwareupdate")
        .args(["-d", label])
        .stdout(Stdio::piped())
        .stderr(Stdio::piped())
        .spawn()
        .with_context(|| format!("failed to run softwareupdate -d '{label}'"))?;

    let stdout = child.stdout.take().context("softwareupdate gave us no stdout to follow")?;
    let stderr = child.stderr.take().context("softwareupdate gave us no stderr to follow")?;
    let (out_text, err_text) = std::thread::scope(|scope| {
        let out = scope.spawn(|| pump(stdout, on_percent));
        let err = scope.spawn(|| pump(stderr, on_percent));
        (out.join().unwrap_or_default(), err.join().unwrap_or_default())
    });

    // Waited on only once both pipes have hit EOF, which they do when the child exits — so this
    // does not block on a child that is blocked writing to us.
    let status = child.wait().with_context(|| format!("softwareupdate -d '{label}' did not finish"))?;

    let combined = condense_progress(&format!("{out_text}{err_text}"));
    crate::logging::info(&format!(
        "softwareupdate -d '{label}' finished: success={} output={}",
        status.success(),
        combined.trim()
    ));

    classify_download(&combined)
        .with_context(|| format!("softwareupdate -d '{label}' fetched nothing: {}", combined.trim()))
}

/// Drains one of the child's pipes to EOF, returning everything it said and calling `on_percent`
/// each time the whole-number percentage changes.
///
/// Whole numbers, not every reading: a single download emitted **7,992** of them (see
/// `condense_progress`), and a menu-bar line redrawn that many times is all cost and no
/// information. At most 101 calls per label reach the caller.
fn pump<R: std::io::Read>(mut reader: R, on_percent: &(dyn Fn(u8) + Sync)) -> String {
    let mut collected = String::new();
    let mut buf = [0u8; 8192];
    let mut last_reported = None;

    loop {
        let read = match reader.read(&mut buf) {
            Ok(0) | Err(_) => break,
            Ok(read) => read,
        };
        collected.push_str(&String::from_utf8_lossy(&buf[..read]));

        // Parsed out of everything so far rather than out of this chunk, because a reading can be
        // split across two reads — and `Downloading: 4` followed by `1.00%` would otherwise report
        // 1% for what is really 41%.
        if let Some(percent) = last_percent(&collected) {
            if last_reported != Some(percent) {
                last_reported = Some(percent);
                on_percent(percent);
            }
        }
    }

    collected
}

/// The most recent complete progress reading in `text`, as a whole number of percent.
///
/// A trailing *incomplete* reading — the marker with no `%` yet, which is exactly what a read that
/// lands mid-reading leaves behind — reports nothing rather than guessing, and the next read
/// carries a complete one along.
fn last_percent(text: &str) -> Option<u8> {
    const MARKER: &str = "Downloading: ";
    let after_marker = &text[text.rfind(MARKER)? + MARKER.len()..];
    let percent_end = after_marker.find('%')?;
    let value: f32 = after_marker[..percent_end].parse().ok()?;
    (value.is_finite()).then(|| value.clamp(0.0, 100.0) as u8)
}

/// What a `-d` run's output says it achieved, or `None` if it does not say it downloaded anything.
///
/// A line beginning `Downloaded` is the whole of the positive evidence, deliberately: it is the one
/// line that appears only when an asset reached the disk. Everything that goes wrong here — a label
/// no update answers to, a flag that does not exist, a network that is not there — is a run that
/// prints no such line, and several of those exit zero, so an exit status cannot stand in for it.
///
/// **A line, not the string `Downloaded:`.** macOS prints two shapes in one listing, which cost a
/// completed 255MB Safari download being logged as "fetched nothing":
///
/// ```text
/// Downloaded: macOS Tahoe 26.7     <- a system update, with a colon
/// Downloaded Safari                <- everything else, without one
/// Done.
/// ```
///
/// Matching at the start of a line keeps `Downloading` — which condensing leaves behind as
/// `Downloading: 100.00% [n readings collapsed]` — from counting as a download that finished.
///
/// `Failed to authenticate` after it is then the Apple-silicon *preparation* wall rather than a
/// failed download: the bits are staged and only the preparation is outstanding, which [`install`]
/// does with a volume owner's password in hand. See [`download`].
fn classify_download(combined: &str) -> Option<DownloadOutcome> {
    if !combined.lines().any(|line| line.trim_start().starts_with("Downloaded")) {
        return None;
    }
    Some(if combined.contains("Failed to authenticate") {
        DownloadOutcome::DownloadedUnprepared
    } else {
        DownloadOutcome::Downloaded
    })
}

/// Every label in a `softwareupdate -l` listing, in the order macOS printed them.
///
/// Root is not needed for the listing itself (see [`check`]), but this is deliberately its own call
/// rather than a field grown onto [`OsUpdateStatus`]: that type answers the admin UI's question
/// ("is an OS update pending, and to what version"), and the daemon's download step asks a
/// different one — *which* labels to fetch, macOS and Safari and firmware alike.
pub fn list_labels() -> Result<Vec<String>> {
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

/// How far the daemon's pre-fetch has got, for the menu bar to draw a bar from.
///
/// Written by root as the download runs and read by the per-user process on its scheduler tick —
/// the two are separate processes, so a file is the only channel between them, the same shape the
/// staged record below uses. Deliberately *not* the queue: nothing is being requested and nobody is
/// waiting on an answer, which is the whole difference between this and the old `OsDownload`.
///
/// Stale-checked on the reading side rather than cleaned up perfectly on the writing side. A daemon
/// killed mid-download cannot tidy up after itself, and a menu bar left claiming 41% forever would
/// be worse than one that goes quiet — see `PROGRESS_FRESH_FOR_SECS`.
#[derive(Debug, Clone, Serialize, Deserialize, PartialEq, Eq)]
#[serde(rename_all = "camelCase")]
pub struct DownloadProgress {
    /// The label being fetched, as `softwareupdate -l` printed it.
    pub label: String,
    /// Its position in this pre-fetch, counting from zero.
    pub index: usize,
    /// How many labels the pre-fetch is working through.
    pub total: usize,
    /// How far through this label, 0-100.
    pub percent: u8,
    /// When this was written. The reader treats anything older than
    /// [`PROGRESS_FRESH_FOR_SECS`] as nothing at all.
    pub epoch: u64,
}

impl DownloadProgress {
    /// What the menu bar's status line says — the label's own title, and which of how many it is
    /// when there is more than one.
    pub fn describe(&self) -> String {
        let title = self.label.rsplit_once('-').map_or(self.label.as_str(), |(title, _build)| title);
        if self.total > 1 {
            format!("{title} ({} of {})", self.index + 1, self.total)
        } else {
            title.to_string()
        }
    }

    /// How far through the *whole* pre-fetch, so the bar crosses the menu once rather than
    /// restarting at each label. A bar that only moved three times in an hour would tell the person
    /// watching it almost nothing, which is the complaint this whole feature answers.
    pub fn overall_percent(&self) -> u8 {
        if self.total == 0 {
            return 0;
        }
        let done = self.index as f32 + f32::from(self.percent) / 100.0;
        ((done / self.total as f32) * 100.0).clamp(0.0, 100.0) as u8
    }
}

/// How long a [`DownloadProgress`] record is believed.
///
/// `softwareupdate` reports often enough that a record this old means the writer is gone rather
/// than slow — the 2.9GB fetch emitted 7,992 readings — so the menu bar stops showing a bar rather
/// than showing one frozen at whatever it last said.
pub const PROGRESS_FRESH_FOR_SECS: u64 = 120;

/// Reads the daemon's progress record, or `None` when there is none or it has gone stale.
pub fn read_progress(path: &Path, now_epoch: u64) -> Option<DownloadProgress> {
    let progress: DownloadProgress = serde_json::from_str(&std::fs::read_to_string(path).ok()?).ok()?;
    (now_epoch.saturating_sub(progress.epoch) <= PROGRESS_FRESH_FOR_SECS).then_some(progress)
}

/// Publishes where the pre-fetch has got to. Best-effort in both directions: a write that fails
/// costs a progress bar, and the download carries on regardless.
pub fn write_progress(path: &Path, progress: &DownloadProgress) {
    if let Ok(json) = serde_json::to_string(progress) {
        let _ = std::fs::write(path, json);
    }
}

/// Takes the progress record away once there is no download to describe. The reader's staleness
/// check is what covers the case where this never runs.
pub fn clear_progress(path: &Path) {
    let _ = std::fs::remove_file(path);
}

fn now_epoch() -> u64 {
    std::time::SystemTime::now()
        .duration_since(std::time::UNIX_EPOCH)
        .map(|since| since.as_secs())
        .unwrap_or_default()
}

/// What the daemon's pre-fetch believes is on disk, as the agent's own record of it.
///
/// **macOS will not answer this question.** `softwareupdate -l` lists an update until it is
/// *installed*, staged or not — the run that proved it is in this host's log, where 26.7 appears as
/// offered immediately after being downloaded — and `/var/db/softwareupdate/journal.plist` records
/// only what has already installed. So there is no way to ask "are the bits here?", and the agent
/// remembers instead.
///
/// It is a belief, not a fact, and the code treats it as one: being wrong costs an install that
/// downloads what it thought was staged, which is exactly what used to happen every time, never
/// something worse. **And it goes stale by itself**, which is why [`needs_prefetch`] re-confirms it
/// every check-in rather than trusting it until the offered set changes: macOS discards staged
/// assets on a schedule of its own. On this fleet's Mac, 26.7 was confirmed on disk on 19 September
/// (`softwareupdate -d` came back `Downloaded:` in four seconds) and was gone by the 22nd, when the
/// authorized install spent two hours fetching it again *after* the password had been typed —
/// the hour-late reboot the whole pre-fetch exists to prevent. See [`StagedDownloads::refreshed`]
/// for how a re-confirmation folds into the record.
#[derive(Debug, Clone, Default, Serialize, Deserialize, PartialEq, Eq)]
#[serde(rename_all = "camelCase")]
pub struct StagedDownloads {
    /// The labels believed to be on disk: what the last attempt confirmed, plus anything an earlier
    /// one confirmed that macOS still offers and the last attempt could not re-check (see
    /// [`StagedDownloads::refreshed`]).
    pub labels: Vec<String>,
    /// When something was last confirmed on disk, as a Unix epoch. Informational: the retry
    /// spacing reads `attempted_epoch`, and the per-user process reads `labels`.
    pub staged_epoch: u64,
    /// The labels that attempt could *not* fetch.
    ///
    /// Recorded rather than thrown away, and this is what stops a failing pre-fetch running away
    /// with the network. A failed attempt used to write no record at all, and "no record" is
    /// indistinguishable from "nothing staged" — so the next hourly invocation retried the whole
    /// set, and the one after that, for as long as the failure lasted. On this fleet's own Mac
    /// that is 15GB an attempt, on a host whose only symptom is a log line.
    #[serde(default)]
    pub failed: Vec<String>,
    /// How many attempts in a row have failed, which sets how long before the next one — see
    /// [`retry_delay_secs`]. Zeroed by an attempt that fetched everything.
    #[serde(default)]
    pub failure_count: u32,
    /// When the last attempt finished, successful or not. Distinct from `staged_epoch`, which does
    /// not move when nothing new was staged.
    #[serde(default)]
    pub attempted_epoch: u64,
}

impl StagedDownloads {
    /// Whether every **macOS** label currently offered is one this record says was staged.
    ///
    /// The macOS ones only: `patch_cycle` asks this to decide whether the authorization prompt can
    /// go up, and a Safari label that has not been fetched costs that install a couple of minutes,
    /// where an unfetched system update costs it the hour the whole ordering exists to move out
    /// from behind the prompt.
    pub fn covers_the_macos_updates(&self, offered: &[String]) -> bool {
        offered
            .iter()
            .filter(|label| is_macos_label(label))
            .all(|label| self.labels.iter().any(|staged| staged == label))
    }

    /// The record to write after an attempt: what it confirmed, folded into what was believed
    /// before.
    ///
    /// **A label an earlier attempt staged stays believed staged when this attempt could not
    /// re-check it.** The failure [`download_one`] reports is "`softwareupdate -d` printed no
    /// `Downloaded` line", and the one cause of that seen on this fleet is `softwareupdate` coming
    /// back with nothing at all for a moment — a blip that says nothing about the disk. Dropping
    /// the label on a blip would have the per-user process decline to prompt for an update that is
    /// sitting ready, every hour until the retry lands. So `labels` is the union, and `failed`
    /// still records the blip so [`needs_prefetch`] spaces the retries. A label macOS no longer
    /// offers is dropped: it has been installed, and there is nothing left to believe about it.
    ///
    /// The cost of believing wrongly is bounded by the retry spacing — an install that downloads —
    /// and is only paid when an asset was evicted *and* `-d` blipped in the same hour.
    pub fn refreshed(previous: Option<&Self>, offered: &[String], outcome: &PrefetchOutcome, now_epoch: u64) -> Self {
        let mut labels = outcome.staged.clone();
        if let Some(previous) = previous {
            for label in &previous.labels {
                if offered.contains(label) && !labels.contains(label) {
                    labels.push(label.clone());
                }
            }
        }

        // Zeroed by an attempt that fetched everything, which is what makes the spacing a
        // *consecutive* count.
        let failure_count = if outcome.failed.is_empty() {
            0
        } else {
            previous.map_or(0, |previous| previous.failure_count).saturating_add(1)
        };

        Self {
            labels,
            staged_epoch: if outcome.staged.is_empty() {
                previous.map_or(0, |previous| previous.staged_epoch)
            } else {
                now_epoch
            },
            failed: outcome.failed.clone(),
            failure_count,
            attempted_epoch: now_epoch,
        }
    }
}

/// Whether the daemon should run the pre-fetch on this invocation.
///
/// **Yes, whenever anything is offered and nothing is being backed off** — every hourly check-in.
/// This used to be "only when the offered set changes, with a seven-day backstop", reasoned from
/// the cost of a pre-fetch being measured in gigabytes. It is not, when the assets are present:
/// `softwareupdate -d` on a staged label comes back `Downloaded:` in four to nine seconds, measured
/// on this fleet's Mac for both 26.7 and Safari, because macOS answers from what it already holds.
/// The gigabytes are only spent when the asset is *gone*, and then spending them is exactly the
/// job: macOS discarded 26.7 within three days of it being confirmed on disk, and the authorized
/// install that followed downloaded for two hours after the password was typed. A record that was
/// believed for a week could not have noticed.
///
/// What still holds an attempt back is a *failed* label — the one case where re-running costs
/// something (an hour of a failing download, or a blip that repeats) — and that is spaced by
/// [`retry_delay_secs`] rather than run hourly. A label macOS has only just started offering
/// overrides even that: it is new work, not failed work.
pub fn needs_prefetch(offered: &[String], staged: Option<&StagedDownloads>, now_epoch: u64) -> bool {
    if offered.is_empty() {
        return false;
    }
    let Some(staged) = staged else {
        return true;
    };

    let known = |label: &String| staged.labels.contains(label) || staged.failed.contains(label);
    if offered.iter().any(|label| !known(label)) {
        return true;
    }

    // Something the last attempt could not fetch. Worth another go — the 19:46 failure on this
    // fleet's Mac was a momentary `softwareupdate -l` blip, and a host that gave up for good on one
    // of those would simply never update — but not every hour, because a failure that is *not*
    // momentary would otherwise repeat for as long as it lasted.
    if offered.iter().any(|label| staged.failed.contains(label)) {
        return now_epoch.saturating_sub(staged.attempted_epoch) >= retry_delay_secs(staged.failure_count);
    }

    true
}

/// How long to leave a failing pre-fetch alone, doubling per consecutive failure from an hour up to
/// a day.
///
/// The first retry is soon because the likeliest cause is momentary — a laptop that just woke, a
/// listing that came back empty — and those clear by themselves. The ceiling is what bounds a
/// failure that does not clear: without it, an hourly invocation re-fetching 15GB is 360GB a day on
/// a host whose only symptom is a log line nobody is reading.
pub fn retry_delay_secs_for(failure_count: u32) -> u64 {
    retry_delay_secs(failure_count)
}

fn retry_delay_secs(failure_count: u32) -> u64 {
    const HOUR: u64 = 60 * 60;
    HOUR.saturating_mul(1 << failure_count.saturating_sub(1).min(5)).min(24 * HOUR)
}

/// Reads the daemon's record of what it last staged. A missing or unreadable file is `None` — the
/// caller then pre-fetches, which is the safe direction: the worst case is one download that macOS
/// answers from its own cache in seconds.
pub fn read_staged(path: &Path) -> Option<StagedDownloads> {
    serde_json::from_str(&std::fs::read_to_string(path).ok()?).ok()
}

/// Records what the pre-fetch just staged, for `patch_cycle` to read before it puts up a password
/// prompt. Best-effort: a record that cannot be written means the next cycle declines to prompt and
/// the next check-in pre-fetches again, which is a wasted download rather than a wrong install.
pub fn write_staged(path: &Path, staged: &StagedDownloads) {
    let Ok(json) = serde_json::to_string(staged) else { return };
    if let Err(err) = std::fs::write(path, json) {
        crate::logging::warn(&format!("could not record what was staged: {err}"));
    }
}

/// The daemon's note to itself that an install is under way whose end it will not see.
///
/// `softwareupdate -i -a -R` reboots the Mac from *inside* the call, so nothing after it runs:
/// [`install`] never returns, `report_patched` is never sent, and the server learns that the host
/// moved on only by inference from the next check-in's `softwareupdate -l`. That left one thing
/// nobody ever told it — that the install *succeeded* — and so nothing ever closed the Failed
/// Updates row an earlier attempt had opened. On this fleet's Mac a download failure filed on
/// 17 September stayed on the screen through a successful install on the 22nd.
///
/// So `main::install_os_updates` writes this **before** running the install, and the next daemon
/// invocation — the `RunAtLoad` one straight after the reboot, ordinarily — reads it back and asks
/// [`judge_pending_install`] what happened. The version it records is the one to compare against:
/// a host that has moved off it finished the install, whatever else `softwareupdate -l` may now be
/// offering.
#[derive(Debug, Clone, Serialize, Deserialize, PartialEq, Eq)]
#[serde(rename_all = "camelCase")]
pub struct PendingInstall {
    /// `system_info::operating_system()` as it read before the install began.
    pub from_version: String,
    /// When the install began, as a Unix epoch.
    pub epoch: u64,
}

/// What became of a [`PendingInstall`], read on a later daemon invocation.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum PendingInstallVerdict {
    /// The host is on a different version than it was: the install finished. Report it.
    Installed,
    /// The Mac has booted since the install began and is on the same version: the update did not
    /// take (or the record was written for a Safari-only install that changed no version). Forget
    /// it, and say nothing — a success report for an install that did not happen would clear the
    /// pending flag the next check-in then has to set again.
    DidNotTake,
    /// Same version, same boot: `softwareupdate` has not rebooted yet, or somebody is still waiting
    /// on the restart it asked for. Keep the record and look again next time.
    StillPending,
}

/// Decides what a [`PendingInstall`] record means now. Pure, for the sake of the tests: the callers
/// supply the current version and the kernel's boot time.
///
/// The version comparison comes first and settles it on its own: a version that moved is an install
/// that finished, whether or not a boot is on record. A kernel that will not say when it booted
/// (`None`) then leaves a same-version record pending rather than discarding it — the record costs
/// nothing to keep and is dropped the moment the version moves.
pub fn judge_pending_install(pending: &PendingInstall, current_version: &str, boot_epoch: Option<u64>) -> PendingInstallVerdict {
    if current_version != pending.from_version {
        return PendingInstallVerdict::Installed;
    }
    match boot_epoch {
        Some(boot) if boot > pending.epoch => PendingInstallVerdict::DidNotTake,
        _ => PendingInstallVerdict::StillPending,
    }
}

/// Reads the daemon's note of an install under way, or `None` when there is none or it is
/// unreadable — in which case nothing is reported, which is the failure mode that already existed.
pub fn read_pending_install(path: &Path) -> Option<PendingInstall> {
    serde_json::from_str(&std::fs::read_to_string(path).ok()?).ok()
}

/// Writes the note. Best-effort: failing to write it costs a success report, never the install.
pub fn write_pending_install(path: &Path, pending: &PendingInstall) {
    let Ok(json) = serde_json::to_string(pending) else { return };
    if let Err(err) = std::fs::write(path, json) {
        crate::logging::warn(&format!("could not record that a macOS install is under way: {err}"));
    }
}

pub fn clear_pending_install(path: &Path) {
    let _ = std::fs::remove_file(path);
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
/// so this function does not return, `report_patched` is not sent from here, and `process_queue`
/// never gets to remove the request. All three are handled where they land: the server re-derives
/// the host's pending state from `softwareupdate -l` at the next check-in, `queue::is_stale`'s boot
/// check discards the surviving request unrun, and the success report is sent by the *next*
/// invocation, off the [`PendingInstall`] note `main::install_os_updates` writes before calling
/// this. The credentials are already gone — `take_auth` unlinks the sidecar before the install
/// starts, precisely so a reboot cannot strand a password on disk.
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
/// **Only called when no restart is outstanding** — see [`InstallOutcome::restart_required`] — or
/// once the restart has happened, which `main::settle_pending_install` establishes from a
/// [`PendingInstall`] on the invocation after the reboot. An update that is merely staged has not
/// changed this host's version, and reporting it as installed made the dashboard clear the flag and
/// then set it again on the next check-in.
///
/// The server end, `ReportOperatingSystemPatchedCommandHandler`, also closes any Failed Updates row
/// filed under [`OS_FAILURE_APPLICATION_NAME`] for this host — so a success has to be *sent* for
/// an earlier failure to stop showing, which is what the post-reboot call is for.
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
    /// The labels ride on the same listing that answered `available` and `latest_version`, so that
    /// `patch_cycle` can ask whether they are staged without a second `softwareupdate -l` — one
    /// more scan being one more thing that can momentarily come back empty and have the cycle
    /// decline to prompt for an update sitting ready on disk.
    #[test]
    fn parse_check_output_carries_the_labels_the_listing_offered() {
        let status = parse_check_output(SAMPLE_LISTING);

        assert_eq!(status.labels, vec!["Safari27.0TahoeAuto-27.0", "macOS Tahoe 26.7-25G229", "macOS 27-26A428"]);
        assert_eq!(status.latest_version.as_deref(), Some("27"));
    }

    #[test]
    fn parse_check_output_carries_no_labels_when_nothing_is_offered() {
        assert!(parse_check_output("Software Update Tool\n\nNo new software available.\n").labels.is_empty());
    }

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

    /// The separator real `softwareupdate` uses, which is a **carriage return** and not nothing:
    /// it overwrites one line on a terminal rather than writing many. Every other transcript in
    /// this module was written by hand without one, so condensing was dead in production while its
    /// tests passed — macOS 27's download put 157KB of readings in `daemon.log` through a function
    /// whose entire purpose is to stop exactly that.
    ///
    /// The bytes are `od -c`'d from that log line.
    #[test]
    fn condense_progress_collapses_readings_separated_by_carriage_returns() {
        let text = "Downloading macOS 27\n\rDownloading: 0.10%\rDownloading: 0.90%\rDownloading: 100.00%\nDownloaded: macOS 27\n";

        let condensed = condense_progress(text);

        assert_eq!(
            condensed,
            "Downloading macOS 27\n\rDownloading: 100.00% [3 readings collapsed]\nDownloaded: macOS 27\n"
        );
    }

    /// The size of the thing, on the real shape: a download's worth of readings has to come out as
    /// one line, not as one line each.
    #[test]
    fn condense_progress_turns_a_real_downloads_worth_of_readings_into_one_line() {
        let mut text = String::from("Downloading macOS 27\n");
        for reading in 0..4000 {
            text.push_str(&format!("\rDownloading: {}.00%", reading % 101));
        }
        text.push_str("\nDownloaded: macOS 27\nFailed to authenticate\n");

        let condensed = condense_progress(&text);

        assert!(condensed.len() < 200, "should be one line, was {} bytes: {condensed}", condensed.len());
        assert!(condensed.contains("[4000 readings collapsed]"), "{condensed}");
        assert_eq!(classify_download(&condensed), Some(DownloadOutcome::DownloadedUnprepared));
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
    /// `softwareupdate -d` a name no update answers to — which it reports as `No such update` and
    /// an exit status of zero.
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
    /// asking for a volume owner. Note that this run exits **1**.
    #[test]
    fn classify_download_reads_a_fetch_that_only_failed_to_prepare() {
        let combined = "Software Update Tool\n\nFinding available software\nDownloading macOS Tahoe 26.7\n\nDownloaded: macOS Tahoe 26.7\nFailed to authenticate\nPassword:";
        assert_eq!(classify_download(combined), Some(DownloadOutcome::DownloadedUnprepared));
    }

    #[test]
    fn classify_download_reads_a_clean_fetch() {
        let combined = "Software Update Tool\n\nFinding available software\nDownloading Safari\n\nDownloaded: Safari\n";
        assert_eq!(classify_download(combined), Some(DownloadOutcome::Downloaded));
    }

    /// A refusal raised before anything was fetched is a real failure of the download step and has
    /// to stay one, or the Failed Updates screen goes quiet about a host that never gets its bits.
    /// The two halves in the order the daemon actually runs them, against a *cold* download rather
    /// than the eight-second cached one every other transcript here is.
    ///
    /// `classify_download` reads text that `condense_progress` has already been through, and
    /// condensing exists precisely to swallow runs of `Downloading: nn%`. `Downloaded:` is a
    /// different line and survives — but nothing except this test says so, and if condensing ever
    /// took it, a host that had fetched every byte would report a hard failure and the cycle would
    /// stop before the install. That is the bug this whole change removed, arriving from the other
    /// side.
    #[test]
    fn a_condensed_cold_download_is_still_read_as_fetched_but_unprepared() {
        // No line endings between the readings: that is how softwareupdate writes them, and being
        // adjacent is what makes them one collapsible run.
        let mut raw = String::from("Software Update Tool\n\nFinding available software\nDownloading macOS Tahoe 26.7\n");
        for percent in 0..=100 {
            raw.push_str(&format!("Downloading: {percent}.00%"));
        }
        raw.push_str("\nDownloaded: macOS Tahoe 26.7\nFailed to authenticate\nPassword:");

        let condensed = condense_progress(&raw);
        assert!(condensed.len() < raw.len(), "the readings should have been collapsed: {condensed}");
        assert_eq!(classify_download(&condensed), Some(DownloadOutcome::DownloadedUnprepared));
    }

    /// The shape everything that is not a system update prints — measured on a 255MB Safari
    /// download that this classifier's first version logged as "fetched nothing", because it looked
    /// for the colon the macOS line has and this one does not.
    #[test]
    fn classify_download_reads_the_colonless_line_other_updates_print() {
        let combined = "Software Update Tool\n\nFinding available software\nDownloading Safari\nDownloaded Safari\nDone.\n";
        assert_eq!(classify_download(combined), Some(DownloadOutcome::Downloaded));
    }

    /// `Downloading` must not be mistaken for `Downloaded`, including the form condensing leaves
    /// behind — which is what makes this a line-start match rather than a substring one.
    #[test]
    fn classify_download_rejects_a_download_that_only_started() {
        let combined = "Downloading Safari\nDownloading: 100.00% [412 readings collapsed]\n";
        assert_eq!(classify_download(combined), None);
    }

    /// The listing's own two labels, which is all this distinction has to carry.
    #[test]
    fn is_macos_label_tells_a_system_update_from_the_rest_of_the_listing() {
        assert!(is_macos_label("macOS Tahoe 26.7-25G229"));
        assert!(is_macos_label("macOS 27-26A428"));
        assert!(!is_macos_label("Safari27.0TahoeAuto-27.0"));
    }

    #[test]
    fn classify_download_rejects_an_authorization_failure_with_no_download() {
        assert_eq!(classify_download("Failed to authenticate\nPassword:"), None);
    }

    /// Both of the ways `softwareupdate` does nothing and **exits zero** while doing it, measured
    /// rather than assumed. If either were read as success the download step would report a host
    /// fully fetched having moved no bytes at all, and the install would then download every
    /// gigabyte after the console user had typed their password.
    #[test]
    fn classify_download_rejects_a_label_no_update_answers_to() {
        assert_eq!(classify_download("definitely-not-an-update-9.9: No such update\nNo updates are available."), None);
    }

    #[test]
    fn classify_download_rejects_a_usage_error() {
        assert_eq!(classify_download("softwareupdate: unrecognized option `--label'\nusage: softwareupdate <cmd> [<args> ...]"), None);
    }

    #[test]
    fn classify_download_rejects_a_download_that_failed_some_other_way() {
        assert_eq!(classify_download("Downloading Safari\nThe operation couldn\u{2019}t be completed. (NSURLErrorDomain error -1009.)"), None);
    }

    /// Exercises the real pipe-reading path against a child that writes the way `softwareupdate`
    /// does — carriage returns, no newlines, readings running together — because the whole point of
    /// streaming rather than `Command::output` is feeding a bar while the child is still alive, and
    /// a version of this that only reported at exit would pass every other test here.
    ///
    /// What it asserts is deliberately not "every reading arrived". How many reach the callback
    /// depends on how the kernel happens to split the pipe, and a chunk holding three readings
    /// rightly reports only the newest — a progress bar wants the current state, not the history.
    /// An earlier version of this test asserted the exact sequence and failed the moment the whole
    /// output arrived in one read. What must hold regardless is that nothing bogus is ever reported
    /// (the split-reading bug would show up here as a 1 or a 4), that the same value is never
    /// pushed twice in a row, and that the last word is 100.
    #[test]
    fn pump_reports_whole_percentages_as_the_child_writes_them() {
        use std::sync::Mutex;

        let mut child = Command::new("sh")
            .arg("-c")
            .arg(
                r"printf 'Downloading macOS 27\n'; printf '\rDownloading: 0.10%%'; sleep 0.1;                   printf '\rDownloading: 0.90%%'; sleep 0.1; printf '\rDownloading: 41.25%%'; sleep 0.1;                   printf '\rDownloading: 100.00%%\nDownloaded: macOS 27\n'",
            )
            .stdout(Stdio::piped())
            .spawn()
            .expect("sh is on every macOS host");
        let stdout = child.stdout.take().expect("just asked for a pipe");

        let seen = Mutex::new(Vec::new());
        let text = pump(stdout, &|percent| seen.lock().expect("no panic holds this").push(percent));
        child.wait().expect("sh exits");

        let seen = seen.into_inner().expect("no panic holds this");
        assert!(!seen.is_empty(), "the callback was never called at all");
        assert!(
            seen.iter().all(|percent| [0, 41, 100].contains(percent)),
            "a percentage nothing printed means a reading was parsed across a chunk boundary: {seen:?}"
        );
        assert!(seen.windows(2).all(|pair| pair[0] != pair[1]), "the same value twice running: {seen:?}");
        assert_eq!(seen.last(), Some(&100), "{seen:?}");
        assert!(text.contains("Downloaded: macOS 27"), "the full text still comes back for classification");
    }

    fn progress(label: &str, index: usize, total: usize, percent: u8) -> DownloadProgress {
        DownloadProgress { label: label.to_string(), index, total, percent, epoch: 1_000 }
    }

    /// The reading shapes `softwareupdate` actually emits, `\r`-separated and run together.
    #[test]
    fn last_percent_reads_the_most_recent_complete_reading() {
        assert_eq!(last_percent("\rDownloading: 0.10%\rDownloading: 41.25%"), Some(41));
    }

    /// A read that lands mid-reading leaves the marker with no `%` yet. Guessing here is what
    /// would turn a split `Downloading: 4` + `1.00%` into a menu bar that says 1%.
    #[test]
    fn last_percent_reports_nothing_for_a_reading_that_is_still_arriving() {
        assert_eq!(last_percent("\rDownloading: 41.25%\rDownloading: 4"), None);
    }

    #[test]
    fn last_percent_reports_nothing_when_no_reading_has_arrived() {
        assert_eq!(last_percent("Software Update Tool\n\nFinding available software\n"), None);
    }

    /// `Downloading macOS 27` is a title, not a reading — it has no colon and no percentage.
    #[test]
    fn last_percent_is_not_fooled_by_the_title_line() {
        assert_eq!(last_percent("Downloading macOS 27\n"), None);
    }

    /// The bar crosses the menu once across the whole pre-fetch rather than restarting per label:
    /// halfway through the second of two updates is 75%, not 50%.
    #[test]
    fn overall_percent_spans_every_label_rather_than_the_current_one() {
        assert_eq!(progress("macOS 27-26A428", 1, 2, 50).overall_percent(), 75);
        assert_eq!(progress("macOS Tahoe 26.7-25G229", 0, 2, 0).overall_percent(), 0);
        assert_eq!(progress("macOS 27-26A428", 2, 3, 100).overall_percent(), 100);
    }

    #[test]
    fn overall_percent_of_an_empty_prefetch_is_zero_rather_than_a_division_by_zero() {
        assert_eq!(progress("macOS 27-26A428", 0, 0, 50).overall_percent(), 0);
    }

    /// The build suffix is noise in a menu bar; the title is what the person recognises.
    #[test]
    fn describe_names_the_update_and_its_place_in_the_queue() {
        assert_eq!(progress("macOS 27-26A428", 1, 3, 40).describe(), "macOS 27 (2 of 3)");
    }

    #[test]
    fn describe_leaves_the_count_off_when_there_is_only_one() {
        assert_eq!(progress("macOS Tahoe 26.7-25G229", 0, 1, 40).describe(), "macOS Tahoe 26.7");
    }

    /// A daemon killed mid-download cannot tidy up, and a menu bar frozen at 41% forever would be
    /// worse than one that goes quiet.
    #[test]
    fn read_progress_ignores_a_record_nothing_has_touched_in_a_while() {
        let dir = std::env::temp_dir().join(format!("kintsugi-progress-{}", std::process::id()));
        std::fs::create_dir_all(&dir).unwrap();
        let path = dir.join("os-download-progress.json");
        let record = progress("macOS 27-26A428", 1, 3, 40);

        write_progress(&path, &record);

        assert_eq!(read_progress(&path, 1_000 + PROGRESS_FRESH_FOR_SECS), Some(record));
        assert_eq!(read_progress(&path, 1_000 + PROGRESS_FRESH_FOR_SECS + 1), None, "stale");

        clear_progress(&path);
        assert_eq!(read_progress(&path, 1_000), None, "and gone once cleared");
        let _ = std::fs::remove_dir_all(&dir);
    }

    fn staged(labels: &[&str], epoch: u64) -> StagedDownloads {
        StagedDownloads {
            labels: labels.iter().map(|label| label.to_string()).collect(),
            staged_epoch: epoch,
            ..Default::default()
        }
    }

    fn staged_with_failure(labels: &[&str], failed: &[&str], failure_count: u32, attempted_epoch: u64) -> StagedDownloads {
        StagedDownloads {
            labels: labels.iter().map(|label| label.to_string()).collect(),
            staged_epoch: attempted_epoch,
            failed: failed.iter().map(|label| label.to_string()).collect(),
            failure_count,
            attempted_epoch,
        }
    }

    const OFFERED: [&str; 3] = ["Safari27.0TahoeAuto-27.0", "macOS Tahoe 26.7-25G229", "macOS 27-26A428"];

    #[test]
    fn needs_prefetch_with_no_record_at_all() {
        let offered = OFFERED.map(String::from).to_vec();
        assert!(needs_prefetch(&offered, None, 1_000));
    }

    /// Nothing offered is nothing to fetch, however absent the record is — a host with no pending
    /// updates must not spend an invocation on `softwareupdate -d`.
    #[test]
    fn needs_prefetch_is_false_when_nothing_is_offered() {
        assert!(!needs_prefetch(&[], None, 1_000));
    }

    #[test]
    fn needs_prefetch_when_macos_starts_offering_something_new() {
        let offered = OFFERED.map(String::from).to_vec();
        let record = staged(&["Safari27.0TahoeAuto-27.0", "macOS Tahoe 26.7-25G229"], 1_000);
        assert!(needs_prefetch(&offered, Some(&record), 1_100), "macOS 27 appeared since");
    }

    /// The record is re-confirmed every check-in, however complete it looks: macOS discarded a
    /// staged 26.7 within three days on this fleet's Mac, and re-running `-d` on an asset that is
    /// still there costs seconds, not gigabytes.
    #[test]
    fn needs_prefetch_re_confirms_a_record_that_covers_everything_offered() {
        let offered = OFFERED.map(String::from).to_vec();
        let record = staged(&OFFERED, 1_000);
        assert!(needs_prefetch(&offered, Some(&record), 1_000 + 60 * 60));
        assert!(needs_prefetch(&offered, Some(&record), 1_000), "even straight away");
    }

    fn outcome(staged: &[&str], failed: &[&str]) -> PrefetchOutcome {
        PrefetchOutcome {
            staged: staged.iter().map(|label| label.to_string()).collect(),
            failed: failed.iter().map(|label| label.to_string()).collect(),
        }
    }

    #[test]
    fn refreshed_with_no_previous_record_is_just_the_outcome() {
        let offered = OFFERED.map(String::from).to_vec();
        let record = StagedDownloads::refreshed(None, &offered, &outcome(&OFFERED, &[]), 2_000);

        assert_eq!(record.labels, offered);
        assert_eq!(record.staged_epoch, 2_000);
        assert_eq!(record.attempted_epoch, 2_000);
        assert!(record.failed.is_empty());
        assert_eq!(record.failure_count, 0);
    }

    /// The blip case: 26.7 was confirmed last hour, `-d` printed nothing this hour. The per-user
    /// process must still be allowed to prompt for it, and the retry must still be spaced.
    #[test]
    fn refreshed_keeps_believing_a_label_the_attempt_could_not_re_check() {
        let offered = OFFERED.map(String::from).to_vec();
        let previous = staged(&OFFERED, 1_000);
        let record = StagedDownloads::refreshed(
            Some(&previous),
            &offered,
            &outcome(&["Safari27.0TahoeAuto-27.0", "macOS 27-26A428"], &["macOS Tahoe 26.7-25G229"]),
            2_000,
        );

        assert!(record.covers_the_macos_updates(&offered), "{record:?}");
        assert_eq!(record.failed, vec!["macOS Tahoe 26.7-25G229".to_string()]);
        assert_eq!(record.failure_count, 1);
        assert_eq!(record.attempted_epoch, 2_000);
    }

    /// Installed is gone: a label macOS no longer offers is not carried forward, so the record
    /// does not grow a history of every update this host ever staged.
    #[test]
    fn refreshed_drops_a_label_that_is_no_longer_offered() {
        let previous = staged(&OFFERED, 1_000);
        let offered = vec!["macOS 27-26A428".to_string()];
        let record = StagedDownloads::refreshed(Some(&previous), &offered, &outcome(&["macOS 27-26A428"], &[]), 2_000);

        assert_eq!(record.labels, offered);
    }

    #[test]
    fn refreshed_counts_consecutive_failures_and_zeroes_them_on_a_clean_attempt() {
        let offered = OFFERED.map(String::from).to_vec();
        let previous = staged_with_failure(&["Safari27.0TahoeAuto-27.0"], &["macOS 27-26A428"], 3, 1_000);

        let failed_again = StagedDownloads::refreshed(Some(&previous), &offered, &outcome(&[], &["macOS 27-26A428"]), 2_000);
        assert_eq!(failed_again.failure_count, 4);
        assert_eq!(failed_again.staged_epoch, 1_000, "nothing new was staged, so that date does not move");

        let clean = StagedDownloads::refreshed(Some(&previous), &offered, &outcome(&OFFERED, &[]), 3_000);
        assert_eq!(clean.failure_count, 0);
        assert!(clean.failed.is_empty());
        assert_eq!(clean.staged_epoch, 3_000);
    }

    fn pending(from_version: &str, epoch: u64) -> PendingInstall {
        PendingInstall {
            from_version: from_version.to_string(),
            epoch,
        }
    }

    /// The case this record exists for: the Mac rebooted inside `softwareupdate`, came back on the
    /// new version, and nobody had told the server.
    #[test]
    fn judge_pending_install_reports_a_host_that_moved_version() {
        let verdict = judge_pending_install(&pending("macOS 26.6.2", 1_000), "macOS 26.7", Some(2_000));
        assert_eq!(verdict, PendingInstallVerdict::Installed);
    }

    /// The version settles it on its own — a kernel that will not say when it booted does not
    /// stop a finished install being reported.
    #[test]
    fn judge_pending_install_needs_no_boot_time_to_see_a_version_change() {
        let verdict = judge_pending_install(&pending("macOS 26.6.2", 1_000), "macOS 26.7", None);
        assert_eq!(verdict, PendingInstallVerdict::Installed);
    }

    #[test]
    fn judge_pending_install_forgets_an_install_the_reboot_did_not_apply() {
        let verdict = judge_pending_install(&pending("macOS 26.7", 1_000), "macOS 26.7", Some(2_000));
        assert_eq!(verdict, PendingInstallVerdict::DidNotTake);
    }

    /// Same version, same boot: the restart has not happened yet. Nothing to say, nothing to drop.
    #[test]
    fn judge_pending_install_waits_while_the_restart_is_still_to_come() {
        assert_eq!(
            judge_pending_install(&pending("macOS 26.7", 1_000), "macOS 26.7", Some(500)),
            PendingInstallVerdict::StillPending
        );
        assert_eq!(
            judge_pending_install(&pending("macOS 26.7", 1_000), "macOS 26.7", None),
            PendingInstallVerdict::StillPending,
            "and an unknown boot time keeps waiting rather than discarding"
        );
    }

    #[test]
    fn pending_install_round_trips_through_its_file() {
        let dir = std::env::temp_dir().join(format!("kintsugi-pending-install-{}", std::process::id()));
        std::fs::create_dir_all(&dir).unwrap();
        let path = dir.join("os-install-pending.json");

        assert_eq!(read_pending_install(&path), None);
        write_pending_install(&path, &pending("macOS 26.6.2", 1_000));
        assert_eq!(read_pending_install(&path), Some(pending("macOS 26.6.2", 1_000)));
        clear_pending_install(&path);
        assert_eq!(read_pending_install(&path), None);

        let _ = std::fs::remove_dir_all(&dir);
    }

    /// The runaway this backoff exists to stop: a failed attempt used to write no record, "no
    /// record" reads the same as "nothing staged", and the next hourly invocation re-fetched all
    /// 15GB — for as long as the failure lasted.
    #[test]
    fn needs_prefetch_holds_off_a_label_that_just_failed() {
        let offered = OFFERED.map(String::from).to_vec();
        let record = staged_with_failure(
            &["Safari27.0TahoeAuto-27.0", "macOS Tahoe 26.7-25G229"],
            &["macOS 27-26A428"],
            1,
            1_000,
        );

        assert!(!needs_prefetch(&offered, Some(&record), 1_000 + 60 * 60 - 1), "an hour has not passed");
        assert!(needs_prefetch(&offered, Some(&record), 1_000 + 60 * 60), "and now it has");
    }

    /// A failure that does not clear must not retry at the same rate forever. Four consecutive ones
    /// put the next attempt eight hours out rather than one.
    #[test]
    fn needs_prefetch_backs_further_off_the_longer_a_failure_lasts() {
        let offered = OFFERED.map(String::from).to_vec();
        let record = staged_with_failure(
            &["Safari27.0TahoeAuto-27.0", "macOS Tahoe 26.7-25G229"],
            &["macOS 27-26A428"],
            4,
            1_000,
        );

        assert!(!needs_prefetch(&offered, Some(&record), 1_000 + 8 * 60 * 60 - 1));
        assert!(needs_prefetch(&offered, Some(&record), 1_000 + 8 * 60 * 60));
    }

    #[test]
    fn retry_delay_doubles_per_failure_up_to_a_day() {
        assert_eq!(retry_delay_secs(1), 60 * 60);
        assert_eq!(retry_delay_secs(2), 2 * 60 * 60);
        assert_eq!(retry_delay_secs(4), 8 * 60 * 60);
        assert_eq!(retry_delay_secs(9), 24 * 60 * 60, "and never longer than a day");
        assert_eq!(retry_delay_secs(0), 60 * 60, "a count of zero is still one attempt's worth");
    }

    /// New work is not failed work: a label macOS has only just started offering is fetched at
    /// once, whatever happened to a different label last time.
    #[test]
    fn needs_prefetch_does_not_hold_off_a_label_it_has_never_tried() {
        let offered = OFFERED.map(String::from).to_vec();
        let record = staged_with_failure(&["Safari27.0TahoeAuto-27.0"], &["macOS Tahoe 26.7-25G229"], 3, 1_000);

        assert!(needs_prefetch(&offered, Some(&record), 1_001), "macOS 27 has never been attempted");
    }

    /// What `patch_cycle` asks before it puts a password prompt on screen.
    #[test]
    fn covers_the_macos_updates_wants_every_system_update_staged() {
        let offered = OFFERED.map(String::from).to_vec();
        assert!(staged(&OFFERED, 0).covers_the_macos_updates(&offered));
        assert!(
            !staged(&["Safari27.0TahoeAuto-27.0", "macOS Tahoe 26.7-25G229"], 0).covers_the_macos_updates(&offered),
            "macOS 27 is offered and not staged, and -i -a would install it"
        );
    }

    /// Safari is not a system update: an unfetched one costs the install a couple of minutes, where
    /// an unfetched macOS update costs it the hour this whole ordering exists to move out from
    /// behind the prompt. So it must not hold the prompt back.
    #[test]
    fn covers_the_macos_updates_ignores_a_label_that_is_not_macos() {
        let offered = OFFERED.map(String::from).to_vec();
        assert!(staged(&["macOS Tahoe 26.7-25G229", "macOS 27-26A428"], 0).covers_the_macos_updates(&offered));
    }

    #[test]
    fn staged_downloads_round_trip_through_the_file_the_daemon_writes() {
        let dir = std::env::temp_dir().join(format!("kintsugi-staged-{}", std::process::id()));
        std::fs::create_dir_all(&dir).unwrap();
        let path = dir.join("os-download-state.json");
        let record = staged(&OFFERED, 1_759_000_000);

        write_staged(&path, &record);

        assert_eq!(read_staged(&path), Some(record));
        let _ = std::fs::remove_dir_all(&dir);
    }

    /// A missing record reads as "nothing staged" rather than failing, because the caller's safe
    /// direction is to pre-fetch (one cheap download) or to decline to prompt (one skipped cycle).
    #[test]
    fn read_staged_answers_none_for_a_file_that_is_not_there() {
        assert_eq!(read_staged(Path::new("/nonexistent/kintsugi/os-download-state.json")), None);
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
