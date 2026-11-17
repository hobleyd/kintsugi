import 'dart:async';
import 'dart:js_interop';

import 'package:web/web.dart' as web;

/// Fires when another tab changes the `localStorage` entry [key].
///
/// The browser reports a `storage` event to every tab **except** the one that wrote, which is what
/// makes this the right signal for a second tab showing the same log: it hears about the first
/// tab's runs, and never about its own. A null key — `localStorage.clear()` — counts as a change.
Stream<void> browserStorageChanges(String key) {
  final controller = StreamController<void>.broadcast();
  web.window.addEventListener(
    'storage',
    ((web.StorageEvent event) {
      if (event.key == key || event.key == null) controller.add(null);
    }).toJS,
  );
  return controller.stream;
}
