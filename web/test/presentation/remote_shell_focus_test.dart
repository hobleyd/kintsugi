import 'dart:typed_data';

import 'package:flutter/gestures.dart';
import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:kintsugi_web/core/theme/app_theme.dart';
import 'package:kintsugi_web/presentation/remote_control/remote_shell_view.dart';
import 'package:xterm/xterm.dart';

/// Clicking back into the terminal has to take the keyboard on the *first* click.
///
/// Two things used to stop it, and together they made the number of clicks needed look random.
/// xterm focuses from `onTapDown`, so a press that drifts a pixel or two — most real clicks — is a
/// drag rather than a tap and focuses nothing; and a focused text field elsewhere on the page
/// unfocuses on any pointer down outside itself, which lands focus on the route's modal scope after
/// any handler that ran inline. The fix is a [Listener] (outside the gesture arena, so it sees every
/// press) that requests focus in a microtask (so it runs last). This pins both halves: the gesture
/// deliberately moves, and a real [TextField] holds focus first.
void main() {
  testWidgets('a click that drifts still takes the keyboard away from a focused field',
      (tester) async {
    final field = FocusNode(debugLabel: 'a field elsewhere on the page');
    addTearDown(field.dispose);

    await tester.pumpWidget(
      MaterialApp(
        theme: AppTheme.dark(),
        home: Scaffold(
          body: Column(
            children: [
              TextField(focusNode: field),
              Expanded(
                child: RemoteShellView(
                  output: const Stream<Uint8List>.empty(),
                  onInput: (_) {},
                ),
              ),
            ],
          ),
        ),
      ),
    );

    field.requestFocus();
    await tester.pump();
    expect(field.hasPrimaryFocus, isTrue, reason: 'the field should hold focus to start with');

    // Not `tester.tap`: a tap is the one gesture that already worked. This is the ordinary click
    // that misses being a tap by a couple of pixels — and the pointer has to be a *mouse* for that
    // to be true. A touch pointer tolerates 18 logical pixels of slop before a press stops being a
    // tap, a mouse one; the whole bug lives inside that difference, so a default (touch) gesture
    // here would sail through against the broken code.
    final gesture = await tester.startGesture(
      tester.getCenter(find.byType(TerminalView)),
      kind: PointerDeviceKind.mouse,
    );
    await gesture.moveBy(const Offset(4, 2));
    await gesture.up();
    await tester.pump();

    expect(
      FocusManager.instance.primaryFocus?.debugLabel,
      'remote terminal',
      reason: 'the terminal should hold the keyboard after one click, however much the mouse moved',
    );

    // xterm arms a double-tap timer on every press; left pending it fails the test after the
    // assertion has already passed, which reads as a focus failure when it is nothing of the kind.
    await tester.pumpAndSettle();
  });
}
