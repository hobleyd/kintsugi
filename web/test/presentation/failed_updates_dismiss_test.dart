import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:kintsugi_web/core/di/locator.dart';
import 'package:kintsugi_web/core/theme/app_theme.dart';
import 'package:kintsugi_web/data/models/patch_failure_mapper.dart';
import 'package:kintsugi_web/domain/entities/patch_failure.dart';
import 'package:kintsugi_web/domain/repositories/repositories.dart';
import 'package:kintsugi_web/domain/usecases/patch_failure_usecases.dart';
import 'package:kintsugi_web/presentation/applications/failed_updates_screen.dart';

class _FakePatchFailureRepository implements PatchFailureRepository {
  _FakePatchFailureRepository(this.failures);

  final List<PatchFailure> failures;
  final List<String> dismissed = [];

  @override
  Future<List<PatchFailure>> list() async => failures;

  @override
  Future<void> dismiss(String id) async => dismissed.add(id);
}

/// Dismiss is inside the expanded row, under the output, and nowhere else.
///
/// It is irreversible from this screen — the row leaves the default view and only the status
/// filter brings it back — and it used to sit in the Actions cell one icon away from the expand
/// chevron, which is a mis-aimed click away from burying a live failure nobody had read. What
/// pins it here is the *collapsed* half of each assertion: a check icon drawn in the row again
/// would pass every test about the panel.
///
/// The row is `canFix: false` deliberately, so the panel renders its "nothing to repair" notice
/// rather than an `InstructionsPanel` that would want half of `injection.dart` registered. That
/// is also the case worth covering: a failure with no stored script is exactly the kind somebody
/// dismisses, and it only has a button at all because the button lives in `_FailureDetails`
/// rather than behind the `canFix` branch beside it.
const _expandTooltip = 'Show the failure (no stored script to repair)';
const _dismissTooltip = 'Dismiss this failure';

void main() {
  late _FakePatchFailureRepository repository;

  setUp(() {
    repository = _FakePatchFailureRepository([
      patchFailureFromJson({
        'id': 'f1',
        'hostId': 'h1',
        'hostname': 'alpha',
        'applicationName': 'Ollama',
        'platform': 'macOS',
        'details': 'exit 1: Permission denied\nbrew: this cask needs sudo',
        'firstFailedUtc': '2026-09-01T02:00:00Z',
        'lastFailedUtc': '2026-09-10T02:00:00Z',
        'failureCount': 4,
        'resolution': 'Outstanding',
        'hasScript': false,
        'canFix': false,
      }),
    ]);

    locator
      ..registerSingleton(GetPatchFailures(repository))
      ..registerSingleton(DismissPatchFailure(repository));
  });

  tearDown(() => locator.reset());

  Future<void> pumpScreen(WidgetTester tester) async {
    tester.view.physicalSize = const Size(1400, 900);
    tester.view.devicePixelRatio = 1.0;
    addTearDown(tester.view.reset);

    await tester.pumpWidget(
      MaterialApp(
        theme: AppTheme.light(),
        home: const Scaffold(body: FailedUpdatesScreen()),
      ),
    );
    await tester.pumpAndSettle();
  }

  testWidgets('a collapsed row offers no way to dismiss', (tester) async {
    await pumpScreen(tester);

    expect(find.text('Ollama'), findsOneWidget);
    // By tooltip rather than by icon: `Icons.expand_more` is also every filter dropdown's chevron.
    expect(find.byTooltip(_expandTooltip), findsOneWidget);
    expect(find.byTooltip(_dismissTooltip), findsNothing);
  });

  testWidgets('expanding reveals it under the output, right-aligned', (tester) async {
    await pumpScreen(tester);
    await tester.tap(find.byTooltip(_expandTooltip));
    await tester.pumpAndSettle();

    final dismiss = find.byTooltip(_dismissTooltip);
    expect(dismiss, findsOneWidget);

    // Below the output box, and at the far end of it rather than beside it. The panel's copy of
    // the output is matched by its second line, which the row's one-line preview does not carry.
    final output = tester.getRect(find.textContaining('brew: this cask needs sudo'));
    final button = tester.getRect(dismiss);
    expect(button.top, greaterThan(output.bottom));
    expect(button.right, closeTo(tester.getRect(find.byType(FailedUpdatesScreen)).right, 60));

    await tester.tap(dismiss);
    await tester.pumpAndSettle();
    expect(repository.dismissed, ['f1']);
  });
}
