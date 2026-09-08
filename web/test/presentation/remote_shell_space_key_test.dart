import 'dart:async';

import 'package:flutter/material.dart';
import 'package:flutter/services.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:kintsugi_web/presentation/remote_control/remote_shell_view.dart';

/// Stands in for the shortcut `WidgetsApp` itself installs above every screen.
class _AncestorSpaceIntent extends Intent {
  const _AncestorSpaceIntent();
}

/// A space typed into the terminal must reach the browser, not an ancestor shortcut.
///
/// On web `WidgetsApp` maps a bare space to `PrioritizedIntents([ActivateIntent, ScrollIntent(page
/// down)])`, and the scroll half is enabled whenever there is a `Scrollable` above — which there
/// always is here. The framework then reports the key handled, Flutter's web embedding calls
/// `preventDefault()`, and the hidden input element xterm actually reads never receives the
/// character: a terminal that types everything except spaces.
///
/// **This cannot be tested through the character itself.** `kIsWeb` is false under `flutter test`,
/// so there is no input element and no `preventDefault` to observe — and the platform's own default
/// shortcuts differ, so the failing mapping is not even installed. What *is* testable is the
/// property the fix rests on and the one the app depends on: a space press must not be visible to
/// any ancestor shortcut. That holds on every platform, and it is exactly what
/// `DoNothingAndStopPropagationIntent` is for.
void main() {
  testWidgets('a space press never reaches a shortcut above the terminal', (tester) async {
    var claimedAbove = false;

    await tester.pumpWidget(
      MaterialApp(
        home: Scaffold(
          body: Actions(
            actions: <Type, Action<Intent>>{
              _AncestorSpaceIntent: CallbackAction<_AncestorSpaceIntent>(
                onInvoke: (_) {
                  claimedAbove = true;
                  return null;
                },
              ),
            },
            child: Shortcuts(
              shortcuts: const <ShortcutActivator, Intent>{
                SingleActivator(LogicalKeyboardKey.space): _AncestorSpaceIntent(),
              },
              child: RemoteShellView(
                output: const Stream<Uint8List>.empty(),
                onInput: (_) {},
              ),
            ),
          ),
        ),
      ),
    );
    await tester.pumpAndSettle();

    await tester.sendKeyEvent(LogicalKeyboardKey.space);
    await tester.pump();

    expect(claimedAbove, isFalse);
  });

  testWidgets('every other printable key is left to the framework as before', (tester) async {
    // The guard is deliberately one key wide. A letter is answered by nothing here, so it still
    // climbs to whatever is above — which on web is what lets the browser insert it, and is the
    // behaviour the terminal has always relied on.
    var claimedAbove = false;

    await tester.pumpWidget(
      MaterialApp(
        home: Scaffold(
          body: Actions(
            actions: <Type, Action<Intent>>{
              _AncestorSpaceIntent: CallbackAction<_AncestorSpaceIntent>(
                onInvoke: (_) {
                  claimedAbove = true;
                  return null;
                },
              ),
            },
            child: Shortcuts(
              shortcuts: const <ShortcutActivator, Intent>{
                SingleActivator(LogicalKeyboardKey.keyA): _AncestorSpaceIntent(),
              },
              child: RemoteShellView(
                output: const Stream<Uint8List>.empty(),
                onInput: (_) {},
              ),
            ),
          ),
        ),
      ),
    );
    await tester.pumpAndSettle();

    await tester.sendKeyEvent(LogicalKeyboardKey.keyA);
    await tester.pump();

    expect(claimedAbove, isTrue);
  });
}
