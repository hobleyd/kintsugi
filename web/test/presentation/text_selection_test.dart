import 'package:flutter/material.dart';
import 'package:flutter/services.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:kintsugi_web/core/theme/app_theme.dart';
import 'package:kintsugi_web/core/widgets/text_bits.dart';

/// The app-wide text selection, which `main.dart` installs as a [SelectionArea] in
/// `MaterialApp.builder`.
///
/// Mirrored here rather than exercised through `KintsugiApp` itself, because that pulls in
/// `core/di/injection.dart` and so `package:web`, which does not compile for the VM these tests
/// run on. So this asserts what the mechanism does — a drag across ordinary [Text] widgets
/// reaching the clipboard — and `main.dart` carries the comment saying it is the only thing
/// switching that mechanism on. Change one and change the other.
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

  testWidgets('text a screen renders as plain Text is selectable and copyable', (tester) async {
    final area = GlobalKey<SelectionAreaState>();
    String? selected;

    await tester.pumpWidget(
      MaterialApp(
        theme: AppTheme.dark(),
        home: SelectionArea(
          key: area,
          onSelectionChanged: (content) => selected = content?.plainText,
          child: const Scaffold(
            body: Column(
              children: [
                // A table cell's two commonest shapes: a hostname, and a serial number — the
                // value most likely to be wanted on the clipboard.
                Text('build-server-04'),
                CodeText('C02XK1YZJGH5'),
              ],
            ),
          ),
        ),
      ),
    );

    // Tap first: the copy shortcut is the selectable region's, so it only fires once the region
    // holds focus — exactly as it does for a visitor, who has clicked in the page before
    // dragging across it.
    await tester.tapAt(tester.getCenter(find.text('build-server-04')));
    await tester.pump();
    area.currentState!.selectableRegion.selectAll();
    await tester.pump();

    // One selection spanning both widgets, rather than each being its own island.
    expect(selected, contains('build-server-04'));
    expect(selected, contains('C02XK1YZJGH5'));

    await tester.sendKeyDownEvent(LogicalKeyboardKey.controlLeft);
    await tester.sendKeyEvent(LogicalKeyboardKey.keyC);
    await tester.sendKeyUpEvent(LogicalKeyboardKey.controlLeft);
    await tester.pump();

    expect(copied, contains('build-server-04'));
    expect(copied, contains('C02XK1YZJGH5'));
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
