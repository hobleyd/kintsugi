//! Whether a dialog put up right now would be seen by anybody.
//!
//! The scheduler (`main::run_scheduler`) asks this before starting an automatic patch cycle, and
//! holds the cycle while the answer is no. The delay countdown runs on the wall clock from the
//! moment the confirmation dialog appears (see `dialogs::run_osascript_until`), so the dialog has
//! to appear to somebody: a prompt raised to a dark screen would spend the whole delay budget
//! before anyone knew it had been asked, and the person would open the lid to "patching will begin
//! in 5 minutes" without ever having been offered a delay. That is not hypothetical — the
//! per-user process ticks during Power Nap's dark wakes, and on this fleet's own Mac the prompt
//! went up at 08:47 on a Friday to a lid that had been shut for five minutes.
//!
//! Three things make a dialog invisible, and each is read from the window server rather than
//! guessed from the clock: the display is asleep (a dark wake, a closed lid, or the display timer
//! having run out), the screen is locked (the dialog would sit behind the lock screen, where it is
//! not merely unseen but unanswerable), or another user's session owns the console (fast user
//! switching). `CGSSessionScreenIsLocked` is not in a header, but it has been the key every
//! screen-lock checker on macOS reads for well over a decade; if it is ever absent the screen is
//! taken to be unlocked, which is the behaviour this agent had before the check existed.
//!
//! Bound by hand rather than through a CoreGraphics crate, for the reason `input_injection`
//! gives: this binary already refuses to link two copies of anything.
//!
//! Kept identical in intent in the other two agents; what each platform can actually read differs
//! and is described in each one's copy.

use std::ffi::{c_char, c_void, CString};

type CFTypeRef = *const c_void;
type CFDictionaryRef = *const c_void;
type CFStringRef = *const c_void;

/// `kCFStringEncodingUTF8`.
const UTF8: u32 = 0x0800_0100;

#[link(name = "CoreGraphics", kind = "framework")]
extern "C" {
    fn CGMainDisplayID() -> u32;
    /// `boolean_t`, which on macOS is an `unsigned int`.
    fn CGDisplayIsAsleep(display: u32) -> u32;
    /// The window server's description of the session this process is in, or null when it is in
    /// none. The caller owns the dictionary.
    fn CGSessionCopyCurrentDictionary() -> CFDictionaryRef;
}

#[link(name = "CoreFoundation", kind = "framework")]
extern "C" {
    fn CFStringCreateWithCString(alloc: *const c_void, cstr: *const c_char, encoding: u32) -> CFStringRef;
    fn CFDictionaryGetValue(dict: CFDictionaryRef, key: *const c_void) -> CFTypeRef;
    fn CFBooleanGetValue(boolean: CFTypeRef) -> u8;
    /// `*mut` rather than `*const`, to match `input_injection`'s declaration of the same symbol —
    /// rustc warns when one binary declares an import twice with differing signatures.
    fn CFRelease(cf: *mut c_void);
}

/// Why a dialog shown right now would go unseen, or `None` when the display is lit, the screen
/// is unlocked and this session is the one on the console. The reason is worded for the log line
/// the scheduler writes when it holds a cycle.
pub fn obstruction() -> Option<String> {
    // SAFETY: no preconditions; the main display id is always valid to query.
    if unsafe { CGDisplayIsAsleep(CGMainDisplayID()) } != 0 {
        return Some("the display is asleep".to_string());
    }

    // SAFETY: the dictionary is either null or owned by us, and released below on every path.
    let session = unsafe { CGSessionCopyCurrentDictionary() };
    if session.is_null() {
        return Some("this process has no window server session".to_string());
    }

    let reason = if session_flag(session, "CGSSessionScreenIsLocked") == Some(true) {
        Some("the screen is locked".to_string())
    } else if session_flag(session, "kCGSSessionOnConsoleKey") == Some(false) {
        Some("another user's session is on the console".to_string())
    } else {
        None
    };

    // SAFETY: a non-null reference we own.
    unsafe { CFRelease(session.cast_mut()) };
    reason
}

/// Reads a boolean entry out of the session dictionary: `None` when the key is absent, which
/// callers treat as "nothing in the way".
fn session_flag(session: CFDictionaryRef, key: &str) -> Option<bool> {
    let key = CString::new(key).ok()?;
    // SAFETY: `key` is a NUL-terminated buffer that outlives the call; the CFString is released
    // before returning; the value is borrowed from the dictionary, which the caller keeps alive.
    unsafe {
        let cf_key = CFStringCreateWithCString(std::ptr::null(), key.as_ptr(), UTF8);
        if cf_key.is_null() {
            return None;
        }
        let value = CFDictionaryGetValue(session, cf_key);
        CFRelease(cf_key.cast_mut());
        if value.is_null() {
            None
        } else {
            Some(CFBooleanGetValue(value) != 0)
        }
    }
}
