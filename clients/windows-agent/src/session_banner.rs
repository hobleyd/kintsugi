//! The always-visible indicator that a remote session is running, with a button to end it.
//!
//! # Why this replaced the tray menu item
//!
//! The first cut put "Remote session: …" and "End Remote Session" in the notification area, because
//! that is where the tray process lives and the tray process was doing the capture. Moving capture
//! into a SYSTEM session helper broke that: the helper has no tray icon, and giving it one would
//! mean a channel back to a medium-integrity process — reintroducing exactly the local-attack
//! surface that moving to SYSTEM removed.
//!
//! A banner is the better answer anyway, on two counts. It is **visible without being looked for**:
//! somebody walking up to a machine mid-session sees it immediately, where a tray item has to be
//! clicked to be found. And because it is owned by a SYSTEM process, UIPI stops a
//! medium-integrity process sending input to it — so user-level malware can neither click "End
//! session" nor, more importantly, close the banner to hide that a session is running.
//!
//! # Its own thread, and the desktop it lives on
//!
//! It owns a window, so its thread can never call `SetThreadDesktop` afterwards (see
//! `remote_desktop`) — which is why it cannot share the capture thread. It inherits the process's
//! startup desktop, `winsta0\default`, and stays there: while a UAC prompt is up the banner is
//! hidden, which is the right trade because the prompt is what the user is looking at and the
//! session resumes visibly the moment it is answered.

use std::sync::atomic::AtomicBool;
use std::sync::Arc;

/// A running banner. Dropping or closing it takes the window down.
pub struct Banner {
    #[cfg(windows)]
    window: platform::BannerWindow,
}

impl Banner {
    /// Takes the banner down and waits for its thread to finish.
    pub fn close(self) {
        #[cfg(windows)]
        self.window.close();
    }
}

/// Puts the banner on screen. `ended_by_user` is set when the button is pressed.
///
/// A flag rather than a channel for the same reason the other agents use one: the click has to be
/// meaningful whether or not the session loop happens to be mid-frame, and the loop is polling
/// anyway.
pub fn show(requested_by: String, ended_by_user: Arc<AtomicBool>) -> Banner {
    #[cfg(windows)]
    {
        Banner { window: platform::BannerWindow::show(requested_by, ended_by_user) }
    }

    #[cfg(not(windows))]
    {
        let _ = (requested_by, ended_by_user);
        Banner {}
    }
}

/// The text the banner shows. Split out so the wording can be tested without a window.
///
/// It names the administrator, because "a remote session is running" without saying whose is not
/// something a user can act on.
pub fn banner_text(requested_by: &str) -> String {
    format!("Remote session in progress — {requested_by}")
}

#[cfg(windows)]
mod platform {
    use std::sync::atomic::{AtomicBool, Ordering};
    use std::sync::{Arc, OnceLock};
    use std::thread::JoinHandle;

    use windows_sys::Win32::Foundation::{HWND, LPARAM, WPARAM};
    use windows_sys::Win32::Graphics::Gdi::GetSysColorBrush;
    use windows_sys::Win32::UI::WindowsAndMessaging::{
        CreateWindowExW, DefWindowProcW, DestroyWindow, DispatchMessageW, GetMessageW,
        GetSystemMetrics, PostMessageW, PostQuitMessage, SetWindowPos, ShowWindow,
        TranslateMessage, BS_PUSHBUTTON, HWND_TOPMOST, MSG, SM_CXSCREEN, SWP_NOACTIVATE, SW_SHOWNA,
        WM_CLOSE, WM_COMMAND, WM_DESTROY, WS_CHILD, WS_EX_NOACTIVATE, WS_EX_TOOLWINDOW,
        WS_EX_TOPMOST, WS_POPUP, WS_VISIBLE,
    };

    use crate::logging;
    use crate::win32::{apply_default_font, instance, register_class, wide, WindowClass};

    use super::banner_text;

    const CLASS_NAME: &str = "KintsugiRemoteSessionBanner";
    const END_BUTTON_ID: usize = 1;

    /// `SS_CENTER` — a static control's centred text style, which windows-sys does not surface.
    const SS_CENTER: u32 = 0x0000_0001;

    const WIDTH: i32 = 460;
    const HEIGHT: i32 = 44;

    /// Where the button is pressed. A static rather than window data because the window procedure is
    /// a bare `extern "system"` function, exactly as `tray_menu` keeps its sender in a static.
    static ENDED: OnceLock<Arc<AtomicBool>> = OnceLock::new();

    pub struct BannerWindow {
        thread: Option<JoinHandle<()>>,
        window: usize,
    }

    impl BannerWindow {
        pub fn show(requested_by: String, ended_by_user: Arc<AtomicBool>) -> Self {
            let _ = ENDED.set(ended_by_user);

            let (window_tx, window_rx) = std::sync::mpsc::channel::<usize>();

            let thread = std::thread::spawn(move || {
                // No SetThreadDesktop here: a new thread starts on the process's startup desktop,
                // which the service set to winsta0\default when it launched this helper. Attaching
                // would have to happen before the window exists, and staying on Default is what is
                // wanted anyway — see the module note.
                let window = unsafe { create(&requested_by) };
                let _ = window_tx.send(window as usize);

                if window.is_null() {
                    logging::error("could not create the remote session banner; the session has no visible indicator");
                    return;
                }

                // SAFETY: a message loop over this thread's own windows. GetMessageW returns 0 on
                // WM_QUIT, which `close` posts.
                unsafe {
                    let mut message: MSG = std::mem::zeroed();
                    while GetMessageW(&mut message, std::ptr::null_mut(), 0, 0) > 0 {
                        TranslateMessage(&message);
                        DispatchMessageW(&message);
                    }
                }
            });

            // Waited for so `close` always has a handle to post to, even if the caller ends the
            // session immediately.
            let window = window_rx.recv().unwrap_or(0);

            Self { thread: Some(thread), window }
        }

        pub fn close(mut self) {
            if self.window != 0 {
                // SAFETY: a window owned by the banner thread. WM_CLOSE is posted rather than sent,
                // because sending from another thread would block on that thread's message loop.
                unsafe { PostMessageW(self.window as HWND, WM_CLOSE, 0, 0) };
            }

            if let Some(thread) = self.thread.take() {
                // Joined rather than detached: the window must be gone before the process exits, or
                // a banner can outlive the session it describes by however long teardown takes.
                let _ = thread.join();
            }
        }
    }

    impl Drop for BannerWindow {
        fn drop(&mut self) {
            // Only reached if `close` was not called — a panic on the session thread. The window
            // must still go: a banner claiming a session that has ended is worse than none.
            if self.window != 0 {
                // SAFETY: as `close`.
                unsafe { PostMessageW(self.window as HWND, WM_CLOSE, 0, 0) };
            }
        }
    }

    /// # Safety
    ///
    /// Must be called on the thread that will run the message loop for the returned window.
    unsafe fn create(requested_by: &str) -> HWND {
        let class = banner_class();
        let title = wide("Kintsugi Remote Control");

        // Centred horizontally, at the very top: out of the way of most application chrome while
        // still being the first thing in the visual field.
        let screen_width = GetSystemMetrics(SM_CXSCREEN);
        let x = ((screen_width - WIDTH) / 2).max(0);

        let window = CreateWindowExW(
            // TOPMOST so it is not covered, TOOLWINDOW so it never appears in the taskbar or
            // Alt-Tab, NOACTIVATE so it cannot steal focus from whatever the operator is driving.
            WS_EX_TOPMOST | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE,
            class.name(),
            title.as_ptr(),
            WS_POPUP | WS_VISIBLE,
            x,
            0,
            WIDTH,
            HEIGHT,
            std::ptr::null_mut(),
            std::ptr::null_mut(),
            instance(),
            std::ptr::null_mut(),
        );

        if window.is_null() {
            return window;
        }

        let label_text = wide(&banner_text(requested_by));
        let label = CreateWindowExW(
            0,
            wide("STATIC").as_ptr(),
            label_text.as_ptr(),
            WS_CHILD | WS_VISIBLE | SS_CENTER as u32,
            12,
            12,
            WIDTH - 130,
            20,
            window,
            std::ptr::null_mut(),
            instance(),
            std::ptr::null_mut(),
        );
        apply_default_font(label);

        let button_text = wide("End session");
        let button = CreateWindowExW(
            0,
            wide("BUTTON").as_ptr(),
            button_text.as_ptr(),
            WS_CHILD | WS_VISIBLE | BS_PUSHBUTTON as u32,
            WIDTH - 110,
            8,
            98,
            28,
            window,
            END_BUTTON_ID as _,
            instance(),
            std::ptr::null_mut(),
        );
        apply_default_font(button);

        // Re-asserted after the children exist: creating them can reorder the window relative to
        // other topmost windows.
        SetWindowPos(window, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOACTIVATE | 0x0001 | 0x0002);
        // SHOWNA, not SHOW: showing without activating, so the remote operator's focus is untouched.
        ShowWindow(window, SW_SHOWNA);

        window
    }

    fn banner_class() -> &'static WindowClass {
        static CLASS: OnceLock<WindowClass> = OnceLock::new();
        CLASS.get_or_init(|| {
            // SAFETY: `banner_wnd_proc` is a valid window procedure and lives for the whole process.
            unsafe { register_class(CLASS_NAME, banner_wnd_proc, GetSysColorBrush(15) as _) }
        })
    }

    unsafe extern "system" fn banner_wnd_proc(window: HWND, message: u32, wparam: WPARAM, lparam: LPARAM) -> isize {
        match message {
            WM_COMMAND if (wparam & 0xFFFF) as usize == END_BUTTON_ID => {
                logging::info("\"End session\" pressed on the remote session banner");
                if let Some(flag) = ENDED.get() {
                    flag.store(true, Ordering::SeqCst);
                }
                0
            }
            WM_CLOSE => {
                DestroyWindow(window);
                0
            }
            WM_DESTROY => {
                PostQuitMessage(0);
                0
            }
            _ => DefWindowProcW(window, message, wparam, lparam),
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn the_banner_names_the_administrator() {
        // "A remote session is running" without saying whose is not something a user can act on.
        let text = banner_text("admin@example.com");

        assert!(text.contains("admin@example.com"), "{text}");
        assert!(text.to_lowercase().contains("remote session"), "{text}");
    }

    #[test]
    fn the_banner_says_it_is_in_progress_rather_than_merely_available() {
        // It is only ever on screen while somebody is actually watching, so the wording has to be
        // present tense — a user reading "remote session" alone might take it for a capability.
        assert!(banner_text("someone").contains("in progress"));
    }
}
