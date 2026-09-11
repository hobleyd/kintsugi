import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:kintsugi_web/core/di/locator.dart';
import 'package:kintsugi_web/core/theme/app_theme.dart';
import 'package:kintsugi_web/core/widgets/buttons.dart';
import 'package:kintsugi_web/core/widgets/form_bits.dart';
import 'package:kintsugi_web/domain/entities/enums.dart';
import 'package:kintsugi_web/domain/entities/vulnerability.dart';
import 'package:kintsugi_web/domain/repositories/repositories.dart';
import 'package:kintsugi_web/domain/usecases/vulnerability_usecases.dart';
import 'package:kintsugi_web/presentation/vulnerabilities/cpe_mapping_queue.dart';

/// The mapping queue: one line per subject, filtered from its own column headers, and acted on a
/// selection at a time.
///
/// Three things here have each been a bug worth a test. The queue **re-sorts under the reader** —
/// `GetCpeMappingsQueryHandler` puts suggested rows first, so confirming one moves it — and the
/// open detail panel holds three text editors in a [State]: unkeyed, Flutter matches those panels
/// by position, so the row that moved was drawn with another row's text still in its fields. The
/// **select-all checkbox** must never reach past the filters, or a reviewer confirms mappings that
/// were never on screen. And a **bulk action must name what it skipped**, because "12 of 40" with
/// no detail reads as a success.
void main() {
  late _FakeVulnerabilityRepository repository;

  setUp(() {
    repository = _FakeVulnerabilityRepository([
      _mapping(
        id: 'zoom',
        displayName: 'Zoom',
        status: CpeMappingStatus.suggested,
        hostCount: 5,
        vendor: 'zoom',
        product: 'zoom_workplace_desktop',
        confidence: CpeConfidence.medium,
      ),
      _mapping(id: 'slack', displayName: 'Slack', status: CpeMappingStatus.unmapped, hostCount: 9),
      _mapping(
        id: 'firefox',
        displayName: 'Firefox',
        status: CpeMappingStatus.suggested,
        hostCount: 2,
        vendor: 'mozilla',
        product: 'firefox',
        confidence: CpeConfidence.high,
      ),
    ]);
    locator
      ..registerSingleton(GetCpeMappings(repository))
      ..registerSingleton(SearchCpeDictionary(repository))
      ..registerSingleton(ConfirmCpeMapping(repository))
      ..registerSingleton(MarkCpeMappingNotApplicable(repository))
      ..registerSingleton(ResetCpeMapping(repository))
      ..registerSingleton(ConfirmCpeMappings(repository))
      ..registerSingleton(ResetCpeMappings(repository));
  });

  tearDown(() => locator.reset());

  Future<void> pumpQueue(WidgetTester tester, {double width = 2200}) async {
    tester.view.physicalSize = Size(width, 2400);
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

  Finder detailOf(String id) => find.byKey(ValueKey(id));

  Finder fieldIn(String id, String hint) => find.descendant(
        of: detailOf(id),
        matching: find.widgetWithText(TextField, hint),
      );

  String textOf(WidgetTester tester, Finder field) =>
      tester.widget<TextField>(field).controller!.text;

  /// Opens one row's detail. There is one chevron per row and they are in layout order, so the
  /// row's own position is what picks it out.
  Future<void> expand(WidgetTester tester, String name) async {
    await tester.tap(
      find.byTooltip('Search NVD’s dictionary for this').at(_rowIndexOf(tester, name)),
    );
    await tester.pumpAndSettle();
  }

  testWidgets('every subject is one row until its detail is asked for', (tester) async {
    await pumpQueue(tester);

    // Nothing is expanded, so none of the three dictionary search boxes exists yet.
    expect(find.widgetWithText(TextField, 'Product name'), findsNothing);
    expect(find.text('Zoom'), findsOneWidget);
    expect(find.text('Slack'), findsOneWidget);
    expect(find.text('Firefox'), findsOneWidget);

    await expand(tester, 'Zoom');

    expect(fieldIn('zoom', 'Product name'), findsOneWidget);
    // Still only one panel open — the queue is a table, not a stack of panels.
    expect(find.widgetWithText(TextField, 'Product name'), findsOneWidget);
  });

  testWidgets('ten columns scroll inside the panel rather than overflowing a real display',
      (tester) async {
    // A 1512-point laptop, which is narrower than this table's floor. Overflow is an error the
    // renderer only reports in debug — in a release build it is content painted over the next
    // column — so the assertion is that pumping at this width throws nothing.
    await pumpQueue(tester, width: 1512);
    await expand(tester, 'Zoom');

    expect(tester.takeException(), isNull);
  });

  testWidgets('the open detail follows its own row when confirming re-sorts the queue',
      (tester) async {
    await pumpQueue(tester);
    await expand(tester, 'Zoom');

    await tester.enterText(fieldIn('zoom', 'Vendor, e.g. mozilla'), 'zoom');
    await tester.enterText(fieldIn('zoom', 'Product, e.g. firefox'), 'meetings');
    await tester.enterText(fieldIn('zoom', 'Product name'), 'zoom video');
    await tester.pump();

    await tester.tap(find.descendant(
      of: detailOf('zoom'),
      matching: find.widgetWithText(PrimaryButton, 'CONFIRM THIS PAIR'),
    ));
    await tester.pumpAndSettle();

    expect(repository.confirmed, ['zoom:zoom:meetings']);

    // Confirming drops Zoom out of the suggested bucket, so it is no longer the top row.
    expect(_rowIndexOf(tester, 'Zoom'), greaterThan(_rowIndexOf(tester, 'Firefox')));

    // All three editors went with the row, not with the position it used to occupy.
    expect(textOf(tester, fieldIn('zoom', 'Vendor, e.g. mozilla')), 'zoom');
    expect(textOf(tester, fieldIn('zoom', 'Product, e.g. firefox')), 'meetings');
    expect(textOf(tester, fieldIn('zoom', 'Product name')), 'zoom video');
  });

  testWidgets('the row’s own tick confirms what it already carries', (tester) async {
    await pumpQueue(tester);

    // Firefox proposes mozilla:firefox already; accepting it needs no typing and no dictionary
    // search, which is the whole point of the tick being on the row.
    await tester.tap(find.byTooltip('Confirm mozilla:firefox.'));
    await tester.pumpAndSettle();

    expect(repository.confirmed, ['firefox:mozilla:firefox']);
  });

  testWidgets('each column filters the rows under it', (tester) async {
    await pumpQueue(tester);

    await tester.enterText(find.widgetWithText(TextField, 'Name…'), 'fire');
    await tester.pumpAndSettle();

    expect(find.text('Firefox'), findsOneWidget);
    expect(find.text('Zoom'), findsNothing);
    expect(find.text('Slack'), findsNothing);

    await tester.enterText(find.widgetWithText(TextField, 'Name…'), '');
    await tester.enterText(find.widgetWithText(TextField, 'Min'), '6');
    await tester.pumpAndSettle();

    // Only Slack is on more than five hosts.
    expect(find.text('Slack'), findsOneWidget);
    expect(find.text('Firefox'), findsNothing);

    await tester.enterText(find.widgetWithText(TextField, 'Min'), '');
    await tester.pumpAndSettle();
    await _chooseDropdown(tester, 'Any confidence', 'High confidence');

    expect(find.text('Firefox'), findsOneWidget);
    expect(find.text('Zoom'), findsNothing);
  });

  testWidgets('a filter that matches nothing says so rather than emptying the screen',
      (tester) async {
    await pumpQueue(tester);

    await tester.enterText(find.widgetWithText(TextField, 'Name…'), 'nothing-like-this');
    await tester.pumpAndSettle();

    expect(find.text('No subject matches these filters.'), findsOneWidget);
  });

  testWidgets('select-all reaches only the rows the filters leave', (tester) async {
    await pumpQueue(tester);

    await tester.enterText(find.widgetWithText(TextField, 'Name…'), 'o');
    await tester.pumpAndSettle();

    // Zoom and Firefox contain an "o"; Slack does not.
    await tester.tap(find.byTooltip('Select the 2 rows these filters leave'));
    await tester.pumpAndSettle();

    expect(find.text('2 selected'), findsOneWidget);

    await tester.tap(find.widgetWithText(PrimaryButton, 'CONFIRM SELECTED'));
    await tester.pumpAndSettle();

    // Slack was never ticked, so it is untouched — and nothing went through the per-row confirm,
    // which is the route that spends an NVD request each.
    expect(repository.bulkConfirmed, [
      ['zoom', 'firefox'],
    ]);
    expect(repository.confirmed, isEmpty);
  });

  testWidgets('a bulk action names every subject it left alone', (tester) async {
    await pumpQueue(tester);

    await tester.tap(find.byTooltip('Select the 3 rows these filters leave'));
    await tester.pumpAndSettle();
    await tester.tap(find.widgetWithText(PrimaryButton, 'CONFIRM SELECTED'));
    await tester.pumpAndSettle();

    // Slack has no proposal, so the server skipped it. Saying "2 subjects updated" and nothing
    // else is what leaves it sitting unassessed.
    expect(find.text('2 subjects updated, 1 left unchanged.'), findsOneWidget);
    expect(
      find.text('Slack — Nothing is proposed for it yet — open the row and search NVD’s dictionary.'),
      findsOneWidget,
    );

    // The selection is spent, so the button cannot be pressed a second time by accident.
    expect(find.text('3 selected'), findsNothing);

    // The list of skipped subjects is a work list; it goes when the reviewer says so.
    await tester.tap(find.text('Dismiss'));
    await tester.pumpAndSettle();
    expect(find.text('2 subjects updated, 1 left unchanged.'), findsNothing);
  });

  testWidgets('a row action supersedes the banner describing the last bulk one', (tester) async {
    await pumpQueue(tester);

    await tester.tap(find.byTooltip('Select the 3 rows these filters leave'));
    await tester.pumpAndSettle();
    await tester.tap(find.widgetWithText(PrimaryButton, 'CONFIRM SELECTED'));
    await tester.pumpAndSettle();
    expect(find.text('2 subjects updated, 1 left unchanged.'), findsOneWidget);

    // Marking one row not-applicable changes the table under the banner, which would otherwise
    // read as a report on what just happened. Slack is the row with something left to decide.
    await tester.tap(find.byTooltip(
      'Not applicable: for in-house software, or anything NVD does not track. Takes it out of the '
      'not-assessed count, because that is a decision rather than a gap.',
    ).first);
    await tester.pumpAndSettle();

    expect(find.text('2 subjects updated, 1 left unchanged.'), findsNothing);
  });

  /// Alphabetical by the label a reader sees, not by the declaration order of
  /// [CpeMappingStatus] — which is an ordinal on the wire and must not be reordered to get this.
  /// Pinned here because a list sorted at the widget rather than at the enum is the kind of thing
  /// the next edit to this column quietly drops.
  testWidgets('the status filter offers its options in alphabetical order', (tester) async {
    await pumpQueue(tester);

    final dropdown = tester.widget<KintsugiDropdown<String>>(
      find.byWidgetPredicate(
        (w) => w is KintsugiDropdown<String> && w.items.contains('confirmed'),
      ),
    );
    final labels = [for (final item in dropdown.items) dropdown.labelOf!(item)];

    expect(labels, [
      'Any status',
      'Awaiting review',
      'Mapped',
      'Not applicable',
      'Not mapped',
    ]);
  });

  testWidgets('clear selected returns every ticked subject to the queue', (tester) async {
    await pumpQueue(tester);

    await tester.tap(find.byTooltip('Select the 3 rows these filters leave'));
    await tester.pumpAndSettle();
    await tester.tap(find.widgetWithText(SecondaryButton, 'CLEAR SELECTED'));
    await tester.pumpAndSettle();

    // In the order the queue shows them: suggested first, then by host count.
    expect(repository.bulkReset, [
      ['zoom', 'firefox', 'slack'],
    ]);
  });
}

/// Which row a name is in, by the order the names appear in the tree — which is the order the
/// table lays them out.
int _rowIndexOf(WidgetTester tester, String name) {
  final names = ['Zoom', 'Slack', 'Firefox']
      .where((n) => tester.any(find.text(n)))
      .map((n) => (n, tester.getTopLeft(find.text(n)).dy))
      .toList()
    ..sort((a, b) => a.$2.compareTo(b.$2));
  return names.indexWhere((entry) => entry.$1 == name);
}

Future<void> _chooseDropdown(WidgetTester tester, String from, String to) async {
  await tester.tap(find.text(from).last);
  await tester.pumpAndSettle();
  await tester.tap(find.text(to).last);
  await tester.pumpAndSettle();
}

CpeMapping _mapping({
  required String id,
  required String displayName,
  required CpeMappingStatus status,
  required int hostCount,
  String? vendor,
  String? product,
  CpeConfidence confidence = CpeConfidence.none,
}) =>
    CpeMapping(
      id: id,
      subjectKind: CpeSubjectKind.application,
      subjectKey: displayName.toLowerCase(),
      displayName: displayName,
      vendor: vendor,
      product: product,
      status: status,
      suggestionSource: vendor == null ? CpeSuggestionSource.none : CpeSuggestionSource.ai,
      suggestionNotes: null,
      confirmedAtUtc: null,
      hostCount: hostCount,
      versions: const [],
      matchCount: 0,
      knownExploitedCount: 0,
      lastAssessedUtc: null,
      lastError: null,
      unassessableReason: null,
      confidence: confidence,
      confidenceReason: confidence == CpeConfidence.none ? null : 'Because the names line up.',
    );

/// Sorts the way `GetCpeMappingsQueryHandler` does — suggested first, then by host count — because
/// that re-sort is what the keyed rows are for. Its bulk confirm skips a subject with nothing
/// proposed, exactly as `ConfirmCpeMappingsCommandHandler` does.
class _FakeVulnerabilityRepository implements VulnerabilityRepository {
  _FakeVulnerabilityRepository(this._mappings);

  List<CpeMapping> _mappings;
  final List<String> confirmed = [];
  final List<List<String>> bulkConfirmed = [];
  final List<List<String>> bulkReset = [];

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
    _confirm(id, vendor, product);
  }

  @override
  Future<BulkMappingResult> confirmMappings(List<String> ids) async {
    bulkConfirmed.add(ids);
    final skipped = <SkippedMapping>[];
    var applied = 0;

    for (final id in ids) {
      final mapping = _mappings.firstWhere((m) => m.id == id);
      if (mapping.vendor == null) {
        skipped.add(SkippedMapping(
          id: id,
          displayName: mapping.displayName,
          reason: 'Nothing is proposed for it yet — open the row and search NVD’s dictionary.',
        ));
        continue;
      }
      _confirm(id, mapping.vendor!, mapping.product!);
      applied++;
    }

    return BulkMappingResult(applied: applied, skipped: skipped);
  }

  @override
  Future<BulkMappingResult> resetMappings(List<String> ids) async {
    bulkReset.add(ids);
    return BulkMappingResult(applied: ids.length, skipped: const []);
  }

  void _confirm(String id, String vendor, String product) {
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
            confidence: CpeConfidence.high,
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
