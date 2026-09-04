import 'package:flutter/gestures.dart';
import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:kintsugi_web/core/di/locator.dart';
import 'package:kintsugi_web/domain/entities/upgrade_path.dart';
import 'package:kintsugi_web/domain/usecases/upgrade_path_usecases.dart';
import 'package:kintsugi_web/presentation/applications/widgets/instructions_panel.dart';

import 'instructions_panel_bloc_test.dart' show FakeUpgradePathRepository, result;

/// The divider between the AI Instructions and Update Script columns.
///
/// Pumps the real [InstructionsPanel] against the bloc test's fake repository, registered in
/// [locator] the way `injection.dart` registers the real ones — which is the only reason
/// `core/di/locator.dart` is a separate file. The gestures are mouse gestures, because that is
/// what drags this divider, and they start *off* the painted line: the divider is a 10px gutter
/// with a 1px line down the middle, and the bug this pins was a drag that worked on that pixel
/// column and nowhere else, under a cursor that promised otherwise across all ten.
void main() {
  setUp(() {
    final repository = FakeUpgradePathRepository(
      promptResult: UpgradePathPrompt(
        available: true,
        platform: 'macOS',
        prompt: 'Research an upgrade path for Nextcloud.',
        reason: null,
        existingResult: result(),
      ),
    );
    locator
      ..registerSingleton(GetUpgradePathPrompt(repository))
      ..registerSingleton(StartUpgradePathRefresh(repository))
      ..registerSingleton(GetUpgradePathRefreshStatus(repository))
      ..registerSingleton(SaveUpgradePath(repository))
      ..registerSingleton(SignUpgradePathScript(repository));
  });

  tearDown(() => locator.reset());

  /// The widget the resize cursor is on — the whole gutter, which is what a reader takes to be
  /// draggable.
  final gutter = find.byWidgetPredicate(
    (widget) => widget is MouseRegion && widget.cursor == SystemMouseCursors.resizeLeftRight,
  );

  Future<void> pumpPanel(WidgetTester tester) async {
    await tester.pumpWidget(
      MaterialApp(
        home: Scaffold(
          body: SizedBox(
            width: 1000,
            child: InstructionsPanel(
              applicationName: 'Nextcloud',
              platform: 'macOS',
              onServerStateChanged: () {},
            ),
          ),
        ),
      ),
    );
    await tester.pumpAndSettle();
    expect(gutter, findsOneWidget);
  }

  /// Drags with a mouse from [from], horizontally by [dx].
  Future<void> dragFrom(WidgetTester tester, Offset from, double dx) async {
    final gesture = await tester.startGesture(from, kind: PointerDeviceKind.mouse);
    await gesture.moveBy(Offset(dx, 0));
    await tester.pump();
    await gesture.up();
    await tester.pump();
  }

  double instructionsWidth(WidgetTester tester) => tester.getSize(find.byType(TextField).first).width;

  testWidgets('a drag that starts anywhere in the gutter moves the divider', (tester) async {
    await pumpPanel(tester);
    final before = instructionsWidth(tester);
    final rect = tester.getRect(gutter);

    // One pixel in from the gutter's left edge: on the cursor, off the line.
    await dragFrom(tester, Offset(rect.left + 1, rect.center.dy), 120);
    expect(instructionsWidth(tester), before + 120);

    // And from its right edge, back the other way.
    final moved = tester.getRect(gutter);
    await dragFrom(tester, Offset(moved.right - 1, moved.center.dy), -50);
    expect(instructionsWidth(tester), before + 70);
  });

  testWidgets('neither column can be dragged narrower than it needs to lay out', (tester) async {
    await pumpPanel(tester);
    final before = instructionsWidth(tester);
    final scriptBefore = tester.getSize(find.byType(TextField).last).width;
    final rect = tester.getRect(gutter);

    await dragFrom(tester, Offset(rect.left + 1, rect.center.dy), -2000);
    final narrowest = instructionsWidth(tester);
    expect(narrowest, lessThan(before));
    expect(narrowest, greaterThan(0));

    await dragFrom(tester, tester.getRect(gutter).center, 4000);
    final scriptNarrowest = tester.getSize(find.byType(TextField).last).width;
    expect(scriptNarrowest, lessThan(scriptBefore));
    // The floor is the same on both sides, so the two columns bottom out at the same width.
    expect(scriptNarrowest, narrowest);
  });
}
