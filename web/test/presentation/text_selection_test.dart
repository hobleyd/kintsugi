import 'package:flutter/material.dart';
import 'package:flutter/services.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:go_router/go_router.dart';
import 'package:kintsugi_web/core/theme/app_theme.dart';
import 'package:kintsugi_web/core/widgets/text_bits.dart';

/// The app-wide text selection, which `main.dart` installs as a [SelectionArea] in
/// `MaterialApp.builder`.
///
/// `KintsugiApp` itself cannot be pumped here — it pulls in `core/di/injection.dart` and so
/// `package:web`, which does not compile for the VM these tests run on. So the wiring is repeated
/// rather than imported, but through the same `MaterialApp.router` and the same `builder`
/// argument, because that placement is the part that can break silently: [SelectableRegion]
/// asserts an [Overlay] ancestor and `builder` runs above the Navigator that would provide one,
/// which is a first-frame crash in debug and nothing at all in the release bundle `flutter build
/// web` produces. Assert and arrangement both go through here. Change one and change the other.
void main() {
  TestWidgetsFlutterBinding.ensureInitialized();

  /// Whatever the widget under test last put on the clipboard.
  String? copied;

  setUp(() {
    copied = null;
    TestDefaultBinaryMessengerBinding.instance.defaultBinaryMessenger.setMockMethodCallHandler(
      SystemChannels.platform,
      (call) async {
        if (call.method == 'Clipboard.setData') {
          copied = (call.arguments as Map)['text'] as String?;
        }
        return null;
      },
    );
  });

  tearDown(() {
    TestDefaultBinaryMessengerBinding.instance.defaultBinaryMessenger
        .setMockMethodCallHandler(SystemChannels.platform, null);
  });

  final area = GlobalKey<SelectionAreaState>();

  // main.dart's own focus node, mirrored so that the copy shortcut is exercised under the
  // arrangement that ships: `skipTraversal` keeps the region out of the Tab order and out of the
  // first-frame traversal sort, but it must not stop a tap focusing the region, or Ctrl+C reaches
  // nothing.
  final focusNode = FocusNode(skipTraversal: true, debugLabel: 'selection');

  /// Pumps [screen] as the one route of an app wired exactly as `main.dart` wires it, and
  /// reports what is selected within it.
  Future<ValueGetter<String?>> pumpApp(WidgetTester tester, Widget screen) async {
    String? selected;

    await tester.pumpWidget(
      MaterialApp.router(
        theme: AppTheme.dark(),
        routerConfig: GoRouter(routes: [GoRoute(path: '/', builder: (_, _) => screen)]),
        // main.dart's line, with the two hooks this test reads it through.
        builder: (context, child) => Overlay.wrap(
          child: SelectionArea(
            key: area,
            focusNode: focusNode,
            onSelectionChanged: (content) => selected = content?.plainText,
            child: child!,
          ),
        ),
      ),
    );

    return () => selected;
  }

  /// Selects everything on screen and copies it, the way a visitor does. The tap comes first
  /// because the copy shortcut is the selectable region's, so it only fires once that region
  /// holds focus — as it does for someone who has clicked in the page before dragging across it.
  Future<void> selectAllAndCopy(WidgetTester tester, Finder tapTarget) async {
    await tester.tapAt(tester.getCenter(tapTarget));
    await tester.pump();
    area.currentState!.selectableRegion.selectAll();
    await tester.pump();

    await tester.sendKeyDownEvent(LogicalKeyboardKey.controlLeft);
    await tester.sendKeyEvent(LogicalKeyboardKey.keyC);
    await tester.sendKeyUpEvent(LogicalKeyboardKey.controlLeft);
    await tester.pump();
  }

  testWidgets('text a screen renders as plain Text is selectable and copyable', (tester) async {
    final selected = await pumpApp(
      tester,
      const Scaffold(
        body: Column(
          children: [
            // A table cell's two commonest shapes: a hostname, and a serial number — the value
            // most likely to be wanted on the clipboard.
            Text('build-server-04'),
            CodeText('C02XK1YZJGH5'),
          ],
        ),
      ),
    );

    await selectAllAndCopy(tester, find.text('build-server-04'));

    // One selection spanning both widgets, rather than each being its own island — which is what
    // a SelectableText among them would be, and why there are none left in lib/.
    expect(selected(), contains('build-server-04'));
    expect(selected(), contains('C02XK1YZJGH5'));

    expect(copied, contains('build-server-04'));
    expect(copied, contains('C02XK1YZJGH5'));
  });

  testWidgets('a dialog is covered too, which is where the script is read', (tester) async {
    final selected = await pumpApp(
      tester,
      Scaffold(
        body: Builder(
          builder: (context) => TextButton(
            onPressed: () => showDialog<void>(
              context: context,
              builder: (_) => const Dialog(child: Text('#!/bin/bash\nbrew upgrade')),
            ),
            child: const Text('View script'),
          ),
        ),
      ),
    );

    await tester.tap(find.text('View script'));
    await tester.pumpAndSettle();

    // showDialog pushes onto the root Navigator, so a SelectionArea inside a screen would not
    // reach it — the app-root one in main.dart is what makes a script copyable by selection as
    // well as by the dialog's own Copy button.
    await selectAllAndCopy(tester, find.textContaining('brew upgrade'));

    expect(selected(), contains('brew upgrade'));
    expect(copied, contains('brew upgrade'));
  });

  test('both themes state a selection colour rather than taking the framework grey', () {
    // Flutter falls back to DefaultSelectionStyle.defaultColor, a flat 50% grey, which on the
    // dark theme's near-black background is close to invisible — a selection that copies fine
    // and looks broken.
    for (final theme in [AppTheme.dark(), AppTheme.light()]) {
      expect(theme.textSelectionTheme.selectionColor, isNotNull);
      expect(theme.textSelectionTheme.selectionColor, isNot(DefaultSelectionStyle.defaultColor));
    }
  });
}
