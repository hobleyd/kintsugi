import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:kintsugi_web/core/di/locator.dart';
import 'package:kintsugi_web/core/theme/app_theme.dart';
import 'package:kintsugi_web/core/widgets/buttons.dart';
import 'package:kintsugi_web/domain/entities/enums.dart';
import 'package:kintsugi_web/domain/entities/vulnerability.dart';
import 'package:kintsugi_web/domain/repositories/repositories.dart';
import 'package:kintsugi_web/domain/usecases/vulnerability_usecases.dart';
import 'package:kintsugi_web/presentation/vulnerabilities/cpe_mapping_queue.dart';

/// What a confirmation does to the rows underneath it.
///
/// The queue re-sorts on every refetch — `GetCpeMappingsQueryHandler` puts the suggested rows
/// first, so confirming one moves it down past every row still awaiting review. Each row's detail
/// panel holds three text editors, and those live in a [State]: unkeyed, Flutter matches the
/// panels by position, so after the re-sort the row that moved was drawn with the *other* row's
/// search, vendor and product text still in its fields. The detail moved and the fields did not.
void main() {
  late _FakeVulnerabilityRepository repository;

  setUp(() {
    repository = _FakeVulnerabilityRepository([
      _mapping(id: 'zoom', displayName: 'Zoom', status: CpeMappingStatus.suggested, hostCount: 5),
      _mapping(id: 'slack', displayName: 'Slack', status: CpeMappingStatus.unmapped, hostCount: 9),
    ]);
    locator
      ..registerSingleton(GetCpeMappings(repository))
      ..registerSingleton(SearchCpeDictionary(repository))
      ..registerSingleton(ConfirmCpeMapping(repository))
      ..registerSingleton(MarkCpeMappingNotApplicable(repository))
      ..registerSingleton(ResetCpeMapping(repository));
  });

  tearDown(() => locator.reset());

  Future<void> pumpQueue(WidgetTester tester) async {
    tester.view.physicalSize = const Size(1400, 2400);
    tester.view.devicePixelRatio = 1.0;
    addTearDown(tester.view.reset);

    await tester.pumpWidget(
      MaterialApp(
        theme: AppTheme.light(),
        home: const Scaffold(
          body: SingleChildScrollView(child: CpeMappingQueue()),
        ),
      ),
    );
    await tester.pumpAndSettle();
  }

  Finder vendorFieldOf(String id) => find.descendant(
        of: find.byKey(ValueKey(id)),
        matching: find.widgetWithText(TextField, 'Vendor, e.g. mozilla').hitTestable(),
      );

  Finder productFieldOf(String id) => find.descendant(
        of: find.byKey(ValueKey(id)),
        matching: find.widgetWithText(TextField, 'Product, e.g. firefox').hitTestable(),
      );

  Finder searchFieldOf(String id) => find.descendant(
        of: find.byKey(ValueKey(id)),
        matching: find.widgetWithText(TextField, 'Product name').hitTestable(),
      );

  String textOf(WidgetTester tester, Finder field) =>
      tester.widget<TextField>(field).controller!.text;

  testWidgets('the fields follow their own row when confirming re-sorts the queue', (tester) async {
    await pumpQueue(tester);

    // Suggested first, so Zoom opens above Slack.
    expect(
      tester.getTopLeft(find.byKey(const ValueKey('zoom'))).dy,
      lessThan(tester.getTopLeft(find.byKey(const ValueKey('slack'))).dy),
    );

    await tester.enterText(vendorFieldOf('zoom'), 'zoom');
    await tester.enterText(productFieldOf('zoom'), 'meetings');
    // The search box is a third editor with a life of its own — it keeps whatever was last looked
    // up, which is not the confirmed product.
    await tester.enterText(searchFieldOf('zoom'), 'zoom video');
    await tester.pump();

    await tester.tap(find.descendant(
      of: find.byKey(const ValueKey('zoom')),
      matching: find.widgetWithText(PrimaryButton, 'CONFIRM'),
    ));
    await tester.pumpAndSettle();

    expect(repository.confirmed, ['zoom:zoom:meetings']);

    // Confirming drops Zoom out of the suggested bucket, so Slack is now the top row.
    expect(
      tester.getTopLeft(find.byKey(const ValueKey('slack'))).dy,
      lessThan(tester.getTopLeft(find.byKey(const ValueKey('zoom'))).dy),
    );

    // All three editors went with the row, not with the position it used to occupy.
    expect(textOf(tester, vendorFieldOf('zoom')), 'zoom');
    expect(textOf(tester, productFieldOf('zoom')), 'meetings');
    expect(textOf(tester, searchFieldOf('zoom')), 'zoom video');
    expect(textOf(tester, vendorFieldOf('slack')), '');
    expect(textOf(tester, productFieldOf('slack')), '');
    expect(textOf(tester, searchFieldOf('slack')), 'Slack');
  });
}

CpeMapping _mapping({
  required String id,
  required String displayName,
  required CpeMappingStatus status,
  required int hostCount,
  String? vendor,
  String? product,
}) =>
    CpeMapping(
      id: id,
      subjectKind: CpeSubjectKind.application,
      subjectKey: displayName.toLowerCase(),
      displayName: displayName,
      vendor: vendor,
      product: product,
      status: status,
      suggestionSource: CpeSuggestionSource.none,
      suggestionNotes: null,
      confirmedAtUtc: null,
      hostCount: hostCount,
      versions: const [],
      matchCount: 0,
      knownExploitedCount: 0,
      lastAssessedUtc: null,
      lastError: null,
      unassessableReason: null,
    );

/// Sorts the way `GetCpeMappingsQueryHandler` does — suggested first, then by host count — because
/// that re-sort is the whole of what this test is about.
class _FakeVulnerabilityRepository implements VulnerabilityRepository {
  _FakeVulnerabilityRepository(this._mappings);

  List<CpeMapping> _mappings;
  final List<String> confirmed = [];

  @override
  Future<List<CpeMapping>> readMappings() async {
    final sorted = [..._mappings]..sort((a, b) {
        final suggested = (b.status == CpeMappingStatus.suggested ? 1 : 0) -
            (a.status == CpeMappingStatus.suggested ? 1 : 0);
        return suggested != 0 ? suggested : b.hostCount.compareTo(a.hostCount);
      });
    return sorted;
  }

  @override
  Future<void> confirmMapping({
    required String id,
    required String vendor,
    required String product,
  }) async {
    confirmed.add('$id:$vendor:$product');
    _mappings = [
      for (final mapping in _mappings)
        if (mapping.id == id)
          _mapping(
            id: mapping.id,
            displayName: mapping.displayName,
            status: CpeMappingStatus.confirmed,
            hostCount: mapping.hostCount,
            vendor: vendor,
            product: product,
          )
        else
          mapping,
    ];
  }

  @override
  Future<List<CpeCandidate>> searchCpeDictionary(String keyword) async => const [];

  @override
  Future<void> markMappingNotApplicable({required String id, String? notes}) async {}

  @override
  Future<void> resetMapping(String id) async {}

  @override
  Future<VulnerabilityOverview> readOverview({bool knownExploitedOnly = true}) =>
      throw UnimplementedError();

  @override
  Future<VulnerabilityRunStatus> readRunStatus() => throw UnimplementedError();

  @override
  Future<VulnerabilityRunStatus> startRun() => throw UnimplementedError();
}
