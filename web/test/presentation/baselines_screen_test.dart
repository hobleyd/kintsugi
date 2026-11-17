import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:kintsugi_web/core/di/locator.dart';
import 'package:kintsugi_web/core/theme/app_theme.dart';
import 'package:kintsugi_web/data/models/baseline_mapper.dart';
import 'package:kintsugi_web/domain/entities/baseline.dart';
import 'package:kintsugi_web/domain/repositories/repositories.dart';
import 'package:kintsugi_web/domain/usecases/baseline_usecases.dart';
import 'package:kintsugi_web/presentation/baselines/baselines_screen.dart';

class _FakeBaselineRepository implements BaselineRepository {
  _FakeBaselineRepository(this.baselines);

  final List<BaselineSummary> baselines;
  Map<String, dynamic>? created;

  @override
  Future<List<BaselineSummary>> list() async => baselines;

  @override
  Future<void> create(Map<String, dynamic> body) async {
    created = body;
  }

  @override
  Future<BaselineDetail> get(String baselineId) => throw UnimplementedError();

  @override
  Future<BaselineHostDetail> host(String baselineId, String hostId) => throw UnimplementedError();

  @override
  Future<void> addVersion(String baselineId, Map<String, dynamic> body) => throw UnimplementedError();

  @override
  Future<void> sign(String versionId, String documentSha256) => throw UnimplementedError();

  @override
  Future<void> assign(String baselineId, {required bool allWindowsHosts, required List<String> hostNames}) =>
      throw UnimplementedError();
}

/// The list: one row per baseline, its signed version, and its compliance counts.
void main() {
  late _FakeBaselineRepository repository;

  setUp(() {
    repository = _FakeBaselineRepository([
      baselineSummaryFromJson({
        'id': 'b1',
        'name': 'Windows workstation (Master Daily)',
        'latestVersion': 2,
        'signedVersion': 1,
        'assignedToAllWindowsHosts': false,
        'applicableHostCount': 3,
        'settingCount': 33,
        'totals': {'compliant': 80, 'drifted': 7, 'notApplicable': 3, 'error': 1, 'notReported': 8},
      }),
    ]);
    locator
      ..registerSingleton(GetBaselines(repository))
      ..registerSingleton(ImportBaseline(repository));
  });

  tearDown(() => locator.reset());

  testWidgets('lists each baseline with its signed version and counts', (tester) async {
    tester.view.physicalSize = const Size(1600, 900);
    tester.view.devicePixelRatio = 1.0;
    addTearDown(tester.view.reset);

    await tester.pumpWidget(MaterialApp(theme: AppTheme.light(), home: const Scaffold(body: BaselinesScreen())));
    await tester.pump();

    expect(find.text('Windows workstation (Master Daily)'), findsOneWidget);
    expect(find.text('v1 (v2 unsigned)'), findsOneWidget);
    expect(find.text('33'), findsOneWidget);
    expect(find.text('7'), findsOneWidget);

    // Dispose so the polling bloc closes before the fake clock is checked for pending timers.
    await tester.pumpWidget(const SizedBox());
  });

  test('importing refuses text that is not a JSON object before anything is sent', () async {
    await expectLater(ImportBaseline(repository).call('[1, 2]'), throwsFormatException);
    expect(repository.created, isNull);

    await ImportBaseline(repository).call('{"name": "b", "items": []}');
    expect(repository.created?['name'], 'b');
  });
}
