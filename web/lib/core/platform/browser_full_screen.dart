import 'dart:async';
import 'dart:js_interop';

import 'package:web/web.dart' as web;

import 'full_screen.dart';

/// The real [FullScreenController]. One of the two files in this app that touches the DOM.
class BrowserFullScreenController implements FullScreenController {
  BrowserFullScreenController();

  final StreamController<bool> _changes = StreamController<bool>.broadcast();
  bool _listening = false;

  @override
  bool get isFullScreen => web.document.fullscreenElement != null;

  @override
  Future<bool> enter() async {
    if (isFullScreen) return true;

    try {
      // The whole document element, not the Flutter canvas: Flutter web sizes itself to its host
      // element, so making a subtree full-screen would leave the app laid out for the old viewport
      // inside a black rectangle.
      await web.document.documentElement!.requestFullscreen().toDart;
      return true;
    } on Object {
      // Every refusal arrives as a rejected promise — most often "not initiated by a user gesture",
      // which is not an error in the sense worth reporting: the caller offers a button instead. Kept
      // deliberately broad because the rejection type differs between engines.
      return false;
    }
  }

  @override
  Future<void> exit() async {
    if (!isFullScreen) return;

    try {
      await web.document.exitFullscreen().toDart;
    } on Object {
      // Nothing useful to do. The commonest cause is the document having already left full screen
      // between the check above and this call — the user pressed Escape.
    }
  }

  @override
  Stream<bool> get onChanged {
    if (!_listening) {
      _listening = true;
      // `fullscreenchange` rather than polling, because Escape and F11 leave full screen without
      // going anywhere near this class — a viewer that believed its own last request would keep
      // hiding the sidebar on a page that was no longer full-screen.
      web.document.addEventListener(
        'fullscreenchange',
        ((web.Event _) => _changes.add(isFullScreen)).toJS,
      );
    }

    return _changes.stream;
  }
}
