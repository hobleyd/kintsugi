import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:kintsugi_web/core/di/locator.dart';
import 'package:kintsugi_web/core/theme/app_theme.dart';
import 'package:kintsugi_web/data/models/patch_failure_mapper.dart';
import 'package:kintsugi_web/domain/entities/patch_failure.dart';
import 'package:kintsugi_web/domain/repositories/repositories.dart';
import 'package:kintsugi_web/domain/usecases/patch_failure_usecases.dart';
import 'package:kintsugi_web/presentation/applications/failed_updates_bloc.dart';
import 'package:kintsugi_web/presentation/applications/failed_updates_screen.dart';

class _FakePatchFailureRepository implements PatchFailureRepository {
  _FakePatchFailureRepository(this.failures);

  final List<PatchFailure> failures;

  @override
  Future<List<PatchFailure>> list() async => failures;

  @override
  Future<void> dismiss(String id) async {}
}

Map<String, dynamic> _json({
  required String id,
  required String application,
  required String details,
  Map<String, dynamic> extra = const {},
}) =>
    {
      'id': id,
      'hostId': 'h-$id',
      'hostname': 'host-$id',
      'applicationName': application,
      'details': details,
      'firstFailedUtc': '2026-11-01T02:00:00Z',
      'lastFailedUtc': '2026-11-05T02:00:00Z',
      'failureCount': 3,
      'resolution': 'Outstanding',
      'hasScript': false,
      'canFix': false,
      ...extra,
    };

final _osJson = _json(
  id: 'os1',
  application: 'Windows',
  details: 'the Windows Update install exited with exit code: 1: 0x80070070',
  extra: {
    'isOperatingSystemUpdate': true,
    'displayName': 'Windows Update',
    'causeTitle': 'Not enough disk space',
    'causeExplanation': 'Windows needs free space on the system drive to install the update.',
    'suggestedFix': 'Free up space on C:, then patch again.',
  },
);

final _appJson = _json(id: 'app1', application: 'Ollama', details: 'exit 1: Permission denied');

void main() {
  test('the mapper reads the server\'s reason for an operating system failure', () {
    final failure = patchFailureFromJson(_osJson);

    expect(failure.isOperatingSystemUpdate, isTrue);
    expect(failure.title, 'Windows Update');
    expect(failure.causeTitle, 'Not enough disk space');
    expect(failure.suggestedFix, 'Free up space on C:, then patch again.');
  });

  test('an older server\'s response reads as an ordinary application failure', () {
    final failure = patchFailureFromJson(_appJson);

    expect(failure.isOperatingSystemUpdate, isFalse);
    expect(failure.title, 'Ollama');
    expect(failure.causeTitle, isNull);
  });

  test('the type filter separates operating system updates from applications', () {
    final os = patchFailureFromJson(_osJson);
    final app = patchFailureFromJson(_appJson);

    const onlyOs = FailedUpdateFilters(kindKey: 'os');
    const onlyApps = FailedUpdateFilters(kindKey: 'apps');

    expect(onlyOs.matches(os), isTrue);
    expect(onlyOs.matches(app), isFalse);
    expect(onlyApps.matches(os), isFalse);
    expect(onlyApps.matches(app), isTrue);
    expect(onlyOs.isActive, isTrue, reason: 'so Clear Filters appears');
  });

  test('search finds an operating system failure by its reason', () {
    final os = patchFailureFromJson(_osJson);

    expect(const FailedUpdateFilters(search: 'disk space').matches(os), isTrue);
    expect(const FailedUpdateFilters(search: 'windows update').matches(os), isTrue);
  });

  group('the screen', () {
    late _FakePatchFailureRepository repository;

    setUp(() {
      repository = _FakePatchFailureRepository([
        patchFailureFromJson(_osJson),
        patchFailureFromJson(_appJson),
      ]);
      locator
        ..registerSingleton(GetPatchFailures(repository))
        ..registerSingleton(DismissPatchFailure(repository));
    });

    tearDown(() => locator.reset());

    Future<void> pumpScreen(WidgetTester tester) async {
      tester.view.physicalSize = const Size(1500, 1000);
      tester.view.devicePixelRatio = 1.0;
      addTearDown(tester.view.reset);

      await tester.pumpWidget(
        MaterialApp(theme: AppTheme.light(), home: const Scaffold(body: FailedUpdatesScreen())),
      );
      await tester.pumpAndSettle();
    }

    testWidgets('a row says it is an OS update and gives the reason in plain words', (tester) async {
      await pumpScreen(tester);

      expect(find.text('Windows Update'), findsOneWidget);
      expect(find.text('OS UPDATE'), findsOneWidget);
      expect(find.text('Not enough disk space'), findsOneWidget);
      // The application row keeps showing the first line of its output.
      expect(find.text('exit 1: Permission denied'), findsOneWidget);
    });

    testWidgets('expanding it says why it failed and what to do', (tester) async {
      await pumpScreen(tester);

      await tester.tap(find.byTooltip('Why this failed and what to do'));
      await tester.pumpAndSettle();

      expect(find.textContaining('WHY WINDOWS UPDATE FAILED ON HOST-OS1'), findsOneWidget);
      expect(find.text('Windows needs free space on the system drive to install the update.'), findsOneWidget);
      expect(find.text('Free up space on C:, then patch again.'), findsOneWidget);
      // No "nothing to repair" notice: there is no script to repair, and the panel says why instead.
      expect(find.textContaining('no stored upgrade script'), findsNothing);
    });

    testWidgets('choosing Operating system updates leaves only those', (tester) async {
      await pumpScreen(tester);

      await tester.tap(find.text('All failures'));
      await tester.pumpAndSettle();
      await tester.tap(find.text('Operating system updates').last);
      await tester.pumpAndSettle();

      expect(find.text('Windows Update'), findsOneWidget);
      expect(find.text('Ollama'), findsNothing);
    });
  });
}
