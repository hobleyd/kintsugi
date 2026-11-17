//! Whether an application with a patch waiting is open right now — the one question that decides
//! whether patching it can go ahead without asking anybody.
//!
//! An upgrade replaces files a running program holds open, and most installers either refuse or
//! close the program to do it. Done to an application nobody has open, that costs the person at the
//! desk nothing, so the service patches those silently after each hourly check-in
//! (`service::Agent::patch_shut_applications_silently`) and the tray's due cycle patches them before
//! it prompts (`patch_cycle::run`); only the applications this module finds open are left for the
//! confirm/delay prompt.
//!
//! **Not knowing is answered as "open".** No uninstall entry to be found, an entry that names no
//! usable folder, a process table that could not be read: each leaves the row to the prompt, which
//! is exactly where every row went before this module existed. Quitting somebody's open
//! application with no warning is the one thing the feature must never do; a row prompted for
//! unnecessarily costs a dialog.
//!
//! What "open" means here: some process's image lives under a folder the application's uninstall
//! entry names — its `InstallLocation`, and the folder of its `DisplayIcon` executable, which is
//! nearly always the program itself. The entry is found by the row's `application_identifier`
//! (the uninstall key name, for a plain install) and otherwise by `DisplayName` (what a winget or
//! Chocolatey row is named by, since those identifiers are package ids rather than keys), across
//! both HKLM views and every loaded user hive — the service runs as SYSTEM, so `HKEY_CURRENT_USER`
//! is SYSTEM's own and holds nobody's per-user installs.
//!
//! A folder too broad to mean one application (`Program Files` itself, a drive root, anything under
//! `Windows`, a profile's `AppData\Local`) is not used: matched against, it would call nearly every
//! application open. Every process on the machine counts, not only one session's — the service sees
//! them all, and an application left open in a disconnected session is closed by its upgrade too.
//!
//! Kept identical in intent in the other two agents; what each platform matches on differs and is
//! described in each one's copy. [`partition`] is the shared rule, and is the same in all three.

use crate::upgrade::UpgradeStatus;

/// Splits `apps` into those `is_open` says are shut — patchable now, without asking — and the
/// rest, in their original order. `None` from `is_open` ("could not tell") lands with the open
/// ones; see the module docs for why that is the rule the whole feature rests on. Kept identical
/// in the other two agents.
pub fn partition<T>(apps: Vec<T>, is_open: impl Fn(&T) -> Option<bool>) -> (Vec<T>, Vec<T>) {
    apps.into_iter().partition(|app| is_open(app) == Some(false))
}

/// `Some(true)`/`Some(false)` when it can say, `None` when it cannot. Reads the process table
/// afresh each time, because a silent pass can run for many minutes and somebody may open an
/// application between the list being split and its turn coming.
pub fn is_open(app: &UpgradeStatus) -> Option<bool> {
    let folders = application_folders(app.application_identifier.as_deref(), &app.application_name);
    if folders.is_empty() {
        return None;
    }
    match running_images() {
        Ok(images) => Some(images.iter().any(|image| folders.iter().any(|folder| is_under(image, folder)))),
        Err(err) => {
            crate::logging::warn(&format!("could not list running processes, so {} is treated as open: {err:#}", app.application_name));
            None
        }
    }
}

/// `path` lower-cased, with forward slashes turned back and quotes and a trailing separator gone,
/// so two spellings of one folder compare equal.
fn normalize(path: &str) -> String {
    path.trim().trim_matches('"').replace('/', "\\").trim_end_matches('\\').to_lowercase()
}

/// Whether `image` lives somewhere under `folder`; both already [`normalize`]d.
fn is_under(image: &str, folder: &str) -> bool {
    image.strip_prefix(folder).is_some_and(|rest| rest.starts_with('\\'))
}

/// The executable a `DisplayIcon` names, without its `,<index>` suffix or quotes — or `None` when it
/// is not an `.exe` (an `.ico`, a `.dll` resource) and so says nothing about what runs.
fn icon_executable(display_icon: &str) -> Option<String> {
    let trimmed = display_icon.trim().trim_matches('"');
    let without_index = match trimmed.rsplit_once(',') {
        Some((path, index)) if index.trim().trim_start_matches('-').chars().all(|c| c.is_ascii_digit()) => path,
        _ => trimmed,
    };
    let path = normalize(without_index);
    path.ends_with(".exe").then_some(path)
}

/// Whether a normalized folder is narrow enough to stand for one application — see the module docs.
fn is_specific(folder: &str) -> bool {
    let parts: Vec<&str> = folder.split('\\').filter(|part| !part.is_empty()).collect();
    let Some((drive, rest)) = parts.split_first() else { return false };
    if !drive.ends_with(':') || rest.is_empty() {
        return false;
    }
    let broad = |names: &[&str]| rest.len() == names.len() && rest.iter().zip(names).all(|(a, b)| a == b);
    if rest[0] == "windows" {
        return false;
    }
    if broad(&["program files"]) || broad(&["program files (x86)"]) || broad(&["programdata"]) || broad(&["users"]) {
        return false;
    }
    // A profile, and the per-user folders under it that installers put many applications in.
    if rest[0] == "users" {
        let below_profile = &rest[1..];
        let is = |names: &[&str]| below_profile.len() == names.len() + 1 && below_profile[1..].iter().zip(names).all(|(a, b)| a == b);
        if below_profile.len() == 1
            || is(&["appdata"])
            || is(&["appdata", "local"])
            || is(&["appdata", "roaming"])
            || is(&["appdata", "local", "programs"])
        {
            return false;
        }
    }
    true
}

/// The folders one uninstall entry says the application runs from — its `InstallLocation` and its
/// `DisplayIcon` executable's folder — keeping only the specific ones.
fn folders_from_entry(install_location: Option<&str>, display_icon: Option<&str>) -> Vec<String> {
    let mut folders = Vec::new();
    if let Some(location) = install_location.map(normalize).filter(|location| !location.is_empty()) {
        folders.push(location);
    }
    if let Some(folder) = display_icon.and_then(icon_executable).and_then(|exe| exe.rsplit_once('\\').map(|(folder, _)| folder.to_string())) {
        folders.push(folder);
    }
    folders.retain(|folder| is_specific(folder));
    folders.dedup();
    folders
}

fn application_folders(identifier: Option<&str>, name: &str) -> Vec<String> {
    use winreg::enums::{HKEY_LOCAL_MACHINE, HKEY_USERS, KEY_READ, KEY_WOW64_32KEY, KEY_WOW64_64KEY};
    use winreg::RegKey;

    const UNINSTALL_KEY: &str = r"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";

    let mut views: Vec<RegKey> = [KEY_WOW64_64KEY, KEY_WOW64_32KEY]
        .into_iter()
        .filter_map(|view| RegKey::predef(HKEY_LOCAL_MACHINE).open_subkey_with_flags(UNINSTALL_KEY, KEY_READ | view).ok())
        .collect();
    let users = RegKey::predef(HKEY_USERS);
    for sid in users.enum_keys().flatten().filter(|sid| sid.starts_with("S-1-5-21-") && !sid.ends_with("_Classes")) {
        if let Ok(key) = users.open_subkey_with_flags(format!(r"{sid}\{UNINSTALL_KEY}"), KEY_READ) {
            views.push(key);
        }
    }

    let read = |entry: &RegKey| {
        let location: Option<String> = entry.get_value("InstallLocation").ok();
        let icon: Option<String> = entry.get_value("DisplayIcon").ok();
        folders_from_entry(location.as_deref(), icon.as_deref())
    };

    // By key name first: it is exact, where a display name can be shared by two installs.
    if let Some(identifier) = identifier {
        let folders: Vec<String> =
            views.iter().filter_map(|view| view.open_subkey_with_flags(identifier, KEY_READ).ok()).flat_map(|entry| read(&entry)).collect();
        if !folders.is_empty() {
            return folders;
        }
    }

    views
        .iter()
        .flat_map(|view| view.enum_keys().flatten().filter_map(move |key| view.open_subkey_with_flags(&key, KEY_READ).ok()))
        .filter(|entry| entry.get_value::<String, _>("DisplayName").is_ok_and(|display| display.trim().eq_ignore_ascii_case(name.trim())))
        .flat_map(|entry| read(&entry))
        .collect()
}

/// Every running process's image path, [`normalize`]d. A process the service may not open (a
/// protected one) is simply absent; only the listing itself failing is an error.
fn running_images() -> anyhow::Result<Vec<String>> {
    use windows_sys::Win32::Foundation::CloseHandle;
    use windows_sys::Win32::System::ProcessStatus::K32EnumProcesses;
    use windows_sys::Win32::System::Threading::{OpenProcess, QueryFullProcessImageNameW, PROCESS_NAME_WIN32, PROCESS_QUERY_LIMITED_INFORMATION};

    let mut pids = vec![0u32; 4096];
    loop {
        let mut needed = 0u32;
        let bytes = (pids.len() * std::mem::size_of::<u32>()) as u32;
        // SAFETY: the buffer is `bytes` long and lives across the call.
        if unsafe { K32EnumProcesses(pids.as_mut_ptr(), bytes, &mut needed) } == 0 {
            anyhow::bail!("EnumProcesses failed: {}", std::io::Error::last_os_error());
        }
        // A full buffer may have been truncated; try again with room to spare.
        if needed < bytes {
            pids.truncate(needed as usize / std::mem::size_of::<u32>());
            break;
        }
        pids.resize(pids.len() * 2, 0);
    }

    let mut images = Vec::with_capacity(pids.len());
    let mut buffer = vec![0u16; 32_768];
    for pid in pids.into_iter().filter(|pid| *pid != 0) {
        // SAFETY: plain call; a null handle is checked below.
        let process = unsafe { OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, 0, pid) };
        if process.is_null() {
            continue;
        }
        let mut length = buffer.len() as u32;
        // SAFETY: `buffer` holds `length` UTF-16 units and lives across the call.
        let ok = unsafe { QueryFullProcessImageNameW(process, PROCESS_NAME_WIN32, buffer.as_mut_ptr(), &mut length) };
        // SAFETY: `process` is a handle this function opened.
        unsafe { CloseHandle(process) };
        if ok != 0 {
            images.push(normalize(&String::from_utf16_lossy(&buffer[..length as usize])));
        }
    }
    Ok(images)
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn an_image_under_the_install_folder_is_the_application_open() {
        let folder = normalize(r"C:\Program Files\Mozilla Firefox\");
        assert!(is_under(&normalize(r"C:\PROGRAM FILES\Mozilla Firefox\firefox.exe"), &folder));
        assert!(!is_under(&normalize(r"C:\Program Files\Mozilla Firefox Beta\firefox.exe"), &folder), "a sibling sharing a prefix is not inside it");
    }

    #[test]
    fn a_display_icon_names_its_executable_without_the_index_or_quotes() {
        assert_eq!(icon_executable(r#""C:\Program Files\Notepad++\notepad++.exe",0"#).as_deref(), Some(r"c:\program files\notepad++\notepad++.exe"));
        assert_eq!(icon_executable(r"C:\Program Files\7-Zip\7zFM.exe,-1").as_deref(), Some(r"c:\program files\7-zip\7zfm.exe"));
        assert_eq!(icon_executable(r"C:\Program Files\App\app.ico"), None, "an icon file says nothing about what runs");
    }

    #[test]
    fn a_folder_too_broad_to_mean_one_application_is_not_used() {
        for broad in [r"c:", r"c:\program files", r"c:\program files (x86)", r"c:\windows\system32", r"c:\programdata", r"c:\users\alice", r"c:\users\alice\appdata\local", r"c:\users\alice\appdata\local\programs"] {
            assert!(!is_specific(broad), "{broad} should be refused");
        }
        for specific in [r"c:\program files\mozilla firefox", r"c:\python313", r"c:\users\alice\appdata\local\programs\microsoft vs code"] {
            assert!(is_specific(specific), "{specific} should be accepted");
        }
    }

    #[test]
    fn an_entry_contributes_its_location_and_its_icon_folder() {
        assert_eq!(
            folders_from_entry(Some(r"C:\Program Files\VideoLAN\VLC\"), Some(r"C:\Program Files\VideoLAN\VLC\vlc.exe,0")),
            [r"c:\program files\videolan\vlc"]
        );
        // An MSI install commonly has no InstallLocation and a DisplayIcon under Windows\Installer.
        assert!(folders_from_entry(None, Some(r"C:\Windows\Installer\{GUID}\icon.exe")).is_empty());
    }

    #[test]
    fn partition_sends_could_not_tell_to_the_prompt() {
        let (shut, rest) = partition(vec![("Shut", Some(false)), ("Open", Some(true)), ("Unknown", None)], |app| app.1);
        assert_eq!(shut.iter().map(|app| app.0).collect::<Vec<_>>(), ["Shut"]);
        assert_eq!(rest.iter().map(|app| app.0).collect::<Vec<_>>(), ["Open", "Unknown"]);
    }
}
