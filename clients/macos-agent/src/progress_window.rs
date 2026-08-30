use std::cell::RefCell;

use objc2::rc::Retained;
use objc2::MainThreadMarker;
use objc2_app_kit::{
    NSApplication, NSBackingStoreType, NSProgressIndicator, NSProgressIndicatorStyle, NSTextField, NSView, NSWindow, NSWindowStyleMask,
};
use objc2_foundation::{NSPoint, NSRect, NSSize, NSString};

struct Window {
    window: Retained<NSWindow>,
    status_label: Retained<NSTextField>,
    progress_bar: Retained<NSProgressIndicator>,
}

thread_local! {
    // Only ever touched from the main thread. Created lazily on first use rather than up front,
    // so a Mac that never actually patches never has a window sitting around at all. `NSWindow`
    // et al. aren't `Send` (they're Cocoa objects), so — same reasoning as tray_menu::MENU_STATE
    // — a thread-local sidesteps needing them to cross threads, rather than fighting it.
    static WINDOW: RefCell<Option<Window>> = RefCell::new(None);
}

const WIDTH: f64 = 440.0;
const HEIGHT: f64 = 110.0;

/// Shows the progress window (creating it on first use) with `current` as its headline and a
/// determinate progress bar reflecting `completed`/`total` (an empty bar when `total` is zero —
/// e.g. during the initial warning period, before there's anything concrete to count). Also
/// brings the app to the foreground, since an `Accessory`-policy app doesn't otherwise get focus
/// when a window opens.
///
/// Must be called on the main thread — see `tray_menu::report_status`, the only caller.
pub fn show_and_update(mtm: MainThreadMarker, current: &str, completed: usize, total: usize) {
    WINDOW.with(|cell| {
        let mut borrowed = cell.borrow_mut();
        let window = borrowed.get_or_insert_with(|| create(mtm));

        window.status_label.setStringValue(&NSString::from_str(current));
        window.progress_bar.setMinValue(0.0);
        window.progress_bar.setMaxValue(if total > 0 { total as f64 } else { 1.0 });
        window.progress_bar.setDoubleValue(completed as f64);

        window.window.makeKeyAndOrderFront(None);
    });

    NSApplication::sharedApplication(mtm).activate();
}

/// Hides the progress window — called once a patch cycle finishes (success, failure, or a
/// delay) and the agent goes back to idle. A no-op if the window was never created.
pub fn hide() {
    WINDOW.with(|cell| {
        if let Some(window) = cell.borrow().as_ref() {
            window.window.orderOut(None);
        }
    });
}

fn create(mtm: MainThreadMarker) -> Window {
    let content_rect = NSRect::new(NSPoint::new(0.0, 0.0), NSSize::new(WIDTH, HEIGHT));
    let style = NSWindowStyleMask::Titled | NSWindowStyleMask::Closable;

    // SAFETY: standard AppKit object initialization — `mtm.alloc()` proves this runs on the main
    // thread, which is AppKit's own requirement for creating a window at all.
    let window = unsafe {
        NSWindow::initWithContentRect_styleMask_backing_defer(mtm.alloc(), content_rect, style, NSBackingStoreType::Buffered, false)
    };
    window.setTitle(&NSString::from_str("Kintsugi Patching"));
    window.center();

    let content_view = NSView::initWithFrame(mtm.alloc(), content_rect);
    window.setContentView(Some(&content_view));

    let status_label = NSTextField::labelWithString(&NSString::from_str("Starting\u{2026}"), mtm);
    status_label.setFrame(NSRect::new(NSPoint::new(20.0, 55.0), NSSize::new(WIDTH - 40.0, 34.0)));
    content_view.addSubview(&status_label);

    let progress_bar = NSProgressIndicator::initWithFrame(mtm.alloc(), NSRect::new(NSPoint::new(20.0, 30.0), NSSize::new(WIDTH - 40.0, 20.0)));
    progress_bar.setStyle(NSProgressIndicatorStyle::Bar);
    progress_bar.setIndeterminate(false);
    progress_bar.setMinValue(0.0);
    progress_bar.setMaxValue(1.0);
    content_view.addSubview(&progress_bar);

    Window { window, status_label, progress_bar }
}
