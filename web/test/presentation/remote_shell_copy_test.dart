import 'dart:async';
import 'dart:convert';

import 'package:flutter/foundation.dart';
import 'package:flutter/gestures.dart';
import 'package:flutter/material.dart';
import 'package:flutter/services.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:kintsugi_web/core/theme/app_theme.dart';
import 'package:kintsugi_web/presentation/remote_control/remote_shell_view.dart';
import 'package:xterm/xterm.dart';

/// Selecting several lines in the remote shell and pressing Cmd-C has to put them on the clipboard.
///
/// It did not, and only for a *multi-line* selection: double-clicking one word worked, which is
/// what made it read as a copy bug rather than a focus one. The cause is the app-wide
/// `SelectionArea` in `main.dart` — see `_takeKeyboard` in the widget for the whole chain. These
/// two tests pin the outcome and the mechanism separately, so a regression says which half broke.
void main() {
  /// The arrangement `main.dart` builds: every screen sits inside one `SelectionArea`, and it is
  /// that region's gesture recognizer, not anything the terminal owns, that causes this.
  Widget wrapped(Widget child, FocusNode selection) => MaterialApp(
        theme: AppTheme.dark(),
        home: Overlay.wrap(
          child: SelectionArea(
            focusNode: selection,
            child: Scaffold(body: child),
          ),
        ),
      );

  testWidgets('a multi-line drag selection is on the clipboard after Cmd-C', (tester) async {
    // Read before the first pump: TerminalView picks its shortcut map (Cmd-C rather than
    // Ctrl-Shift-C) once, in initState, from defaultTargetPlatform.
    debugDefaultTargetPlatformOverride = TargetPlatform.macOS;

    final copied = <String>[];
    tester.binding.defaultBinaryMessenger.setMockMethodCallHandler(
      SystemChannels.platform,
      (call) async {
        if (call.method == 'Clipboard.setData') {
          copied.add((call.arguments as Map<Object?, Object?>)['text']! as String);
        }
        return null;
      },
    );
    addTearDown(
      () => tester.binding.defaultBinaryMessenger
          .setMockMethodCallHandler(SystemChannels.platform, null),
    );

    final selection = FocusNode(skipTraversal: true, debugLabel: 'selection');
    addTearDown(selection.dispose);
    final output = StreamController<Uint8List>();
    addTearDown(output.close);

    await tester.pumpWidget(
      wrapped(
        RemoteShellView(output: output.stream, onInput: (_) {}),
        selection,
      ),
    );

    output.add(Uint8List.fromList(utf8.encode('alpha bravo\r\ncharlie delta\r\n')));
    await tester.pump();

    final origin = tester.getTopLeft(find.byType(TerminalView));
    final gesture = await tester.startGesture(
      origin + const Offset(2, 2),
      kind: PointerDeviceKind.mouse,
    );
    // Held past kPressTimeout before moving, which is what dragging across lines always looks
    // like — and is the difference between this and the double-click that worked.
    await tester.pump(const Duration(milliseconds: 150));
    await gesture.moveBy(const Offset(60, 30));
    await tester.pump();
    await gesture.up();
    await tester.pump();

    await tester.sendKeyDownEvent(LogicalKeyboardKey.metaLeft);
    await tester.sendKeyDownEvent(LogicalKeyboardKey.keyC);
    await tester.sendKeyUpEvent(LogicalKeyboardKey.keyC);
    await tester.sendKeyUpEvent(LogicalKeyboardKey.metaLeft);
    await tester.pumpAndSettle();

    expect(copied, isNotEmpty, reason: 'Cmd-C should have reached xterm and written the clipboard');
    expect(
      copied.single,
      contains('\n'),
      reason: 'a selection spanning two rows should copy as two lines',
    );

    // Reset here rather than in a tear-down: the framework checks this variable between the body
    // and the tear-downs, and fails the test for leaving it set however the assertions went.
    debugDefaultTargetPlatformOverride = null;
  });

  testWidgets('a drag longer than the press timeout leaves the keyboard on the terminal',
      (tester) async {
    final selection = FocusNode(skipTraversal: true, debugLabel: 'selection');
    addTearDown(selection.dispose);

    await tester.pumpWidget(
      wrapped(
        RemoteShellView(output: const Stream<Uint8List>.empty(), onInput: (_) {}),
        selection,
      ),
    );

    final gesture = await tester.startGesture(
      tester.getTopLeft(find.byType(TerminalView)) + const Offset(2, 2),
      kind: PointerDeviceKind.mouse,
    );
    await tester.pump(const Duration(milliseconds: 150));
    await gesture.moveBy(const Offset(60, 30));
    await tester.pump();

    // The steal itself, asserted rather than assumed: if this ever stops being true the widget's
    // `onPointerUp` has become dead weight rather than a fix, and the comment on `_takeKeyboard`
    // is describing a Flutter that no longer exists.
    expect(
      FocusManager.instance.primaryFocus?.debugLabel,
      'selection',
      reason: "SelectableRegion's press deadline should have taken the keyboard mid-drag",
    );

    await gesture.up();
    await tester.pump();

    expect(
      FocusManager.instance.primaryFocus?.debugLabel,
      'remote terminal',
      reason: 'releasing should hand the keyboard back, so the copy shortcut resolves to xterm',
    );

    // xterm arms a double-tap timer on every press; left pending it fails the test after the
    // assertions have passed, which reads as a focus failure when it is nothing of the kind.
    await tester.pumpAndSettle();
  });
}
