import 'dart:typed_data';

import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:kintsugi_web/core/theme/app_theme.dart';
import 'package:kintsugi_web/presentation/remote_control/remote_shell_view.dart';
import 'package:xterm/xterm.dart';

/// A terminal has to be monospaced, and on web nothing enforces that but the family string this
/// widget asks for.
///
/// The face is fetched at runtime by `google_fonts`, which registers it under a family of its own
/// making rather than under the human name — so naming `'Share Tech Mono'` matches no registered
/// font, and Flutter's canvas renderer has no system monospace to fall back to. The text lands in
/// the default *proportional* face, which is what shipped: aligned output, box drawing and progress
/// bars all wrong, and nothing in a build or an analyze pass to say so.
void main() {
  testWidgets('the terminal asks for the family the app actually registers its monospace face under',
      (tester) async {
    await tester.pumpWidget(
      MaterialApp(
        theme: AppTheme.dark(),
        home: Scaffold(
          body: RemoteShellView(
            output: const Stream<Uint8List>.empty(),
            onInput: (_) {},
          ),
        ),
      ),
    );

    final terminal = tester.widget<TerminalView>(find.byType(TerminalView));

    expect(terminal.textStyle.fontFamily, AppTheme.monoFamily);
    // The failure this pins, spelled out: the human name is what a reader reaches for, and it is
    // the one string that cannot work.
    expect(terminal.textStyle.fontFamily, isNot('Share Tech Mono'));
  });
}
