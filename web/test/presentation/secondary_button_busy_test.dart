import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:kintsugi_web/core/theme/app_theme.dart';
import 'package:kintsugi_web/core/widgets/buttons.dart';
import 'package:kintsugi_web/core/widgets/gradient_spinner.dart';

/// [SecondaryButton.busy], which the Applications panel's Sign Script button turns on for the
/// duration of the request. Before it existed the only change on click was the button going dim,
/// which is what a button that may not be pressed looks like, not one that is working.
///
/// The spinner animates forever, so these pump single frames rather than settling.
void main() {
  Future<void> pumpButton(WidgetTester tester, {required bool busy, VoidCallback? onPressed}) async {
    await tester.pumpWidget(
      MaterialApp(
        theme: AppTheme.dark(),
        home: Scaffold(
          body: Center(
            child: SecondaryButton(label: 'Sign Script', busy: busy, onPressed: onPressed),
          ),
        ),
      ),
    );
    await tester.pump();
  }

  testWidgets('busy replaces the label with a spinner and blocks presses', (tester) async {
    var presses = 0;
    await pumpButton(tester, busy: true, onPressed: () => presses++);

    expect(find.byType(GradientSpinner), findsOneWidget);
    // The label is still laid out — see the width test below — but not painted.
    final hiddenLabel = tester.widget<Opacity>(
      find.ancestor(of: find.text('SIGN SCRIPT'), matching: find.byType(Opacity)),
    );
    expect(hiddenLabel.opacity, 0);

    await tester.tap(find.byType(SecondaryButton));
    await tester.pump();
    expect(presses, 0, reason: 'a second press mid-request would sign twice');
  });

  testWidgets('busy keeps the width the label had, so neighbours do not shift', (tester) async {
    await pumpButton(tester, busy: false, onPressed: () {});
    final idleSize = tester.getSize(find.byType(SecondaryButton));

    await pumpButton(tester, busy: true, onPressed: () {});
    expect(tester.getSize(find.byType(SecondaryButton)), idleSize);
  });

  testWidgets('idle shows the label and no spinner', (tester) async {
    await pumpButton(tester, busy: false, onPressed: () {});

    expect(find.byType(GradientSpinner), findsNothing);
    expect(find.text('SIGN SCRIPT'), findsOneWidget);
    expect(find.byType(Opacity), findsNothing);
  });
}
