import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:kintsugi_web/core/di/locator.dart';
import 'package:kintsugi_web/core/theme/app_theme.dart';
import 'package:kintsugi_web/core/widgets/buttons.dart';
import 'package:kintsugi_web/domain/entities/enums.dart';
import 'package:kintsugi_web/domain/entities/vulnerability.dart';
import 'package:kintsugi_web/domain/repositories/repositories.dart';
import 'package:kintsugi_web/domain/usecases/vulnerability_usecases.dart';
import 'package:kintsugi_web/presentation/vulnerabilities/cve_reporting_screen.dart';

/// The CVE Reporting table's headers and its page.
///
/// The property under test is almost always *what was asked of the server*, not what the widget
/// did with what it had. Filtering and sorting run on the server because the page is cut after
/// them: order a page in the browser and "the least-installed of the hundred highest-scoring"
/// reads as the fleet's least-installed. So these assert the outgoing [VulnerabilityQuery], and
/// the three things easiest to get wrong about it — that a filter or a sort goes back to page
/// one, that keystrokes collapse into one request, and that the two totals on screen are not
/// interchangeable.
void main() {
  late _FakeVulnerabilityRepository repository;

  setUp(() {
    repository = _FakeVulnerabilityRepository();
    locator.registerSingleton(GetVulnerabilityOverview(repository));
  });

  tearDown(() => locator.reset());

  Future<void> pumpScreen(WidgetTester tester, {double width = 1600}) async {
    tester.view.physicalSize = Size(width, 1400);
    tester.view.devicePixelRatio = 1.0;
    addTearDown(tester.view.reset);

    await tester.pumpWidget(MaterialApp(
      theme: AppTheme.light(),
      home: const Scaffold(body: CveReportingScreen()),
    ));
    await tester.pumpAndSettle();
  }

  testWidgets('draws its header controls at the width it declares as its floor', (tester) async {
    // 1180 is what a narrow display gets; the tests above all pump at 1600. A dropdown or a
    // search box wider than its column does not shrink, it overflows — which throws here and is
    // invisible in a release build.
    repository.findings = [_finding('CVE-2024-0001')];

    await pumpScreen(tester, width: 1340);

    expect(tester.takeException(), isNull);
    expect(find.text('PLATFORM'), findsOneWidget);
    expect(find.text('SCORE'), findsOneWidget);
  });

  group('the Score column', () {
    testWidgets('carries the number and its provenance, leaving the band to Severity',
        (tester) async {
      repository.findings = [_finding('CVE-2024-0001', score: 9.8, derived: true)];

      await pumpScreen(tester);

      expect(find.text('HIGH'), findsOneWidget);
      expect(find.text('9.8'), findsOneWidget);
      expect(find.text('calculated'), findsOneWidget);
    });

    testWidgets('is blank when the finding cannot be ranked, and Severity says why',
        (tester) async {
      // Either half missing means unscored, which is also what the Severity filter's Unscored
      // option matches — the cells and the filter must not come to disagree about which rows
      // those are.
      repository.findings = [_finding('CVE-2024-0001', score: null, severity: null)];

      await pumpScreen(tester);

      expect(find.text('Unscored'), findsOneWidget);
      expect(find.text('0.0'), findsNothing);
    });

    testWidgets('sorts on its own key, not the Severity column\'s', (tester) async {
      // Two columns, two orders: a v2 HIGH and a v3 HIGH do not begin at the same figure.
      repository.findings = [_finding('CVE-2024-0001')];

      await pumpScreen(tester);
      await tester.tap(find.text('SCORE'));
      await tester.pumpAndSettle();

      expect(repository.lastQuery.sortKey, VulnerabilitySortKey.score);

      await tester.tap(find.text('SEVERITY'));
      await tester.pumpAndSettle();

      expect(repository.lastQuery.sortKey, VulnerabilitySortKey.severity);
    });

    testWidgets('filters by a floor rather than by a band', (tester) async {
      repository.findings = [_finding('CVE-2024-0001')];

      await pumpScreen(tester);
      await tester.tap(find.text('Any score'));
      await tester.pumpAndSettle();
      await tester.tap(find.text('7 and above').last);
      await tester.pumpAndSettle();

      expect(repository.lastQuery.minScore, '7');
      expect(repository.lastQuery.page, 0);
      expect(repository.lastQuery.hasFilters, isTrue);
    });
  });

  group('the Platform column', () {
    testWidgets('shows every family a CVE reaches, not just the first', (tester) async {
      // One is the exception, not the rule: an OpenSSL flaw is a Homebrew install on the laptops
      // and a distribution package on the servers, and showing one would send somebody to patch
      // half the estate.
      repository.findings = [_finding('CVE-2024-0001', platforms: const ['Linux', 'macOS'])];

      await pumpScreen(tester);

      expect(find.text('LINUX'), findsOneWidget);
      expect(find.text('MACOS'), findsOneWidget);
    });

    testWidgets('shows Unknown as a platform rather than as a blank', (tester) async {
      // A host whose reported operating system nothing recognises is exposed just the same.
      repository.findings = [_finding('CVE-2024-0001', platforms: const ['Unknown'])];

      await pumpScreen(tester);

      expect(find.text('UNKNOWN'), findsOneWidget);
    });
  });

  group('the headers', () {
    testWidgets('sort on the server, descending first and reversing on a second press',
        (tester) async {
      repository.findings = [_finding('CVE-2024-0001')];

      await pumpScreen(tester);
      await tester.tap(find.text('SEVERITY'));
      await tester.pumpAndSettle();

      expect(repository.lastQuery.sortKey, VulnerabilitySortKey.severity);
      // Worst first is what anybody wants of a severity the first time they ask for it.
      expect(repository.lastQuery.sortAscending, isFalse);

      await tester.tap(find.text('SEVERITY'));
      await tester.pumpAndSettle();

      expect(repository.lastQuery.sortAscending, isTrue);
    });

    testWidgets('go back to page one when the order changes', (tester) async {
      // The row that was on page seven of the old order is not on page seven of the new one.
      repository.total = 250;
      repository.findings = [_finding('CVE-2024-0001')];

      await pumpScreen(tester);
      await tester.tap(find.text('Next'.toUpperCase()));
      await tester.pumpAndSettle();
      expect(repository.lastQuery.page, 1);

      await tester.tap(find.text('INSTALLS'));
      await tester.pumpAndSettle();

      expect(repository.lastQuery.sortKey, VulnerabilitySortKey.installs);
      expect(repository.lastQuery.page, 0);
    });

    testWidgets('filter on the server and go back to page one', (tester) async {
      repository.total = 250;
      repository.findings = [_finding('CVE-2024-0001')];

      await pumpScreen(tester);
      await tester.tap(find.text('Next'.toUpperCase()));
      await tester.pumpAndSettle();

      await tester.tap(find.text('Any platform'));
      await tester.pumpAndSettle();
      await tester.tap(find.text('Windows').last);
      await tester.pumpAndSettle();

      expect(repository.lastQuery.platform, 'Windows');
      expect(repository.lastQuery.page, 0);
    });

    testWidgets('collapse a typed search into one request', (tester) async {
      // Each of these runs on the server, so an un-debounced field is a round trip per keystroke
      // — and the last answer back is not necessarily the last one sent.
      repository.findings = [_finding('CVE-2024-0001')];

      await pumpScreen(tester);
      final before = repository.queries.length;

      await tester.enterText(find.byType(TextField).first, 'CVE');
      await tester.enterText(find.byType(TextField).first, 'CVE-2024');
      await tester.enterText(find.byType(TextField).first, 'CVE-2024-0001');
      await tester.pump(const Duration(milliseconds: 100));

      expect(repository.queries.length, before, reason: 'still inside the debounce window');

      await tester.pumpAndSettle(const Duration(milliseconds: 500));

      expect(repository.queries.length, before + 1);
      expect(repository.lastQuery.cveSearch, 'CVE-2024-0001');
    });

    testWidgets('do not drop one search because the other was typed in next', (tester) async {
      // With a single shared timer, moving between the two boxes inside the debounce window
      // cancelled the first field's pending request, and the second field's event carried no
      // value for it — so the text still visible in the first box was never applied.
      repository.findings = [_finding('CVE-2024-0001')];

      await pumpScreen(tester);
      final fields = find.byType(TextField);

      await tester.enterText(fields.first, 'CVE-2021');
      await tester.pump(const Duration(milliseconds: 100));
      await tester.enterText(fields.last, 'openssl');
      await tester.pumpAndSettle(const Duration(milliseconds: 500));

      expect(repository.lastQuery.cveSearch, 'CVE-2021');
      expect(repository.lastQuery.subjectSearch, 'openssl');
    });
  });

  group('the page', () {
    testWidgets('counts the matching findings, not the fleet total', (tester) async {
      // The two numbers measure different things and will disagree the moment a filter is on, so
      // one of them says "matching" and the other does not.
      repository.total = 5;
      repository.pageSize = 2;
      repository.findings = [_finding('CVE-2024-0001'), _finding('CVE-2024-0002')];

      await pumpScreen(tester);

      expect(find.text('Showing 1–2 of 5 matching'), findsOneWidget);
      expect(find.text('Page 1 of 3'), findsOneWidget);
    });

    testWidgets('is not offered at all when everything fits on one', (tester) async {
      repository.total = 2;
      repository.findings = [_finding('CVE-2024-0001'), _finding('CVE-2024-0002')];

      await pumpScreen(tester);

      expect(find.text('Next'.toUpperCase()), findsNothing);
    });

    testWidgets('takes the page the server actually returned', (tester) async {
      // The server clamps past the end, so a filter that shrinks the set under the reader lands
      // them on the last page rather than on an empty table.
      repository.total = 250;
      repository.clampTo = 0;
      repository.findings = [_finding('CVE-2024-0001')];

      await pumpScreen(tester);
      await tester.tap(find.text('Next'.toUpperCase()));
      await tester.pumpAndSettle();

      expect(find.text('Page 1 of 3'), findsOneWidget);
    });
  });

  group('clearing', () {
    testWidgets('is offered only once something is filtering, and resets all of it',
        (tester) async {
      repository.findings = [_finding('CVE-2024-0001')];

      await pumpScreen(tester);
      expect(find.widgetWithText(SecondaryButton, 'Clear Filters'.toUpperCase()), findsNothing);

      await tester.tap(find.text('Any severity'));
      await tester.pumpAndSettle();
      await tester.tap(find.text('CRITICAL').last);
      await tester.pumpAndSettle();

      expect(find.widgetWithText(SecondaryButton, 'Clear Filters'.toUpperCase()), findsOneWidget);

      await tester.tap(find.widgetWithText(SecondaryButton, 'Clear Filters'.toUpperCase()));
      await tester.pumpAndSettle();

      expect(repository.lastQuery.severity, VulnerabilityQuery.anyValue);
      expect(repository.lastQuery.hasFilters, isFalse);
    });

    testWidgets('is still on screen when the filters have emptied the table', (tester) async {
      // The defect this is here for: the empty state replaced the whole table, and the toolbar
      // holding Clear Filters went with it — so the message saying "clear them from the toolbar
      // above" pointed at a control that was no longer drawn, and reloading the page was the
      // only way back.
      repository.findings = [_finding('CVE-2024-0001')];

      await pumpScreen(tester);
      repository.findings = [];
      repository.total = 0;

      await tester.tap(find.text('Any platform'));
      await tester.pumpAndSettle();
      await tester.tap(find.text('Windows').last);
      await tester.pumpAndSettle();

      expect(find.textContaining('No CVE matches these filters'), findsOneWidget);
      expect(find.widgetWithText(SecondaryButton, 'Clear Filters'.toUpperCase()), findsOneWidget);
      // Under Affects, the wide column — not crammed into the 190px CVE column, which is where a
      // short cell list lands by default.
      expect(
        tester.getRect(find.textContaining('No CVE matches these filters')).left,
        greaterThan(tester.getRect(find.text('AFFECTS')).left - 1),
      );

      repository.findings = [_finding('CVE-2024-0001')];
      repository.total = 1;
      await tester.tap(find.widgetWithText(SecondaryButton, 'Clear Filters'.toUpperCase()));
      await tester.pumpAndSettle();

      expect(find.text('CVE-2024-0001'), findsOneWidget);
    });

    testWidgets('empties the search boxes as well as the query', (tester) async {
      // SearchField seeds its controller once, so clearing the filters used to leave the typed
      // text sitting in the box claiming a filter that was no longer applied.
      repository.findings = [_finding('CVE-2024-0001')];

      await pumpScreen(tester);
      await tester.enterText(find.byType(TextField).first, 'CVE-2021');
      await tester.pumpAndSettle(const Duration(milliseconds: 500));
      expect(repository.lastQuery.cveSearch, 'CVE-2021');

      await tester.tap(find.widgetWithText(SecondaryButton, 'Clear Filters'.toUpperCase()));
      await tester.pumpAndSettle();

      expect(repository.lastQuery.cveSearch, isEmpty);
      expect(find.text('CVE-2021'), findsNothing);
    });
  });
}

VulnerabilityFinding _finding(
  String cveId, {
  List<String> platforms = const ['macOS'],
  double? score = 7.5,
  String? severity = 'HIGH',
  bool derived = false,
}) =>
    VulnerabilityFinding(
      cveId: cveId,
      description: null,
      cvssBaseScore: score,
      cvssSeverity: severity,
      cvssVector: null,
      cvssVersion: '3.1',
      cvssDerivedFromVector: derived,
      publishedUtc: null,
      knownExploited: false,
      kevDateAddedUtc: null,
      kevDueDateUtc: null,
      kevKnownRansomwareUse: false,
      kevShortDescription: null,
      kevRequiredAction: null,
      hostCount: 3,
      affectedSubjects: const [
        AffectedSubject(
          cpeMappingId: '',
          subjectKind: VulnerabilitySubjectKind.application,
          displayName: 'openssl',
          version: '3.0.11',
          hostCount: 3,
        ),
      ],
      platforms: platforms,
    );

/// Answers whatever it is told to, and records what it was asked. It deliberately does **not**
/// filter or sort: the point of every assertion here is the outgoing query, because that is where
/// the behaviour lives now.
class _FakeVulnerabilityRepository implements VulnerabilityRepository {
  List<VulnerabilityFinding> findings = const [];
  int total = 1;
  int pageSize = 100;

  /// A page the server would clamp the request to, as it does past the end of a shrunken set.
  int? clampTo;

  final List<VulnerabilityQuery> queries = [];

  VulnerabilityQuery get lastQuery => queries.last;

  @override
  Future<VulnerabilityOverview> readOverview(VulnerabilityQuery query) async {
    queries.add(query);
    return VulnerabilityOverview(
      summary: const VulnerabilitySummary(
        knownExploitedCount: 0,
        totalCveCount: 847,
        affectedApplicationCount: 1,
        affectedHostCount: 1,
        unmappedSubjectCount: 0,
        notApplicableSubjectCount: 0,
        confirmedSubjectCount: 1,
        unassessableHostCount: 0,
        assessedPackageCount: 0,
        assessmentsPending: 0,
        lastAssessedUtc: null,
        kevCatalogVersion: null,
        kevRefreshedUtc: null,
      ),
      findings: findings,
      page: clampTo ?? query.page,
      pageSize: pageSize,
      filteredCount: total,
    );
  }

  @override
  Future<List<CpeMapping>> readMappings() => throw UnimplementedError();

  @override
  Future<List<CpeCandidate>> searchCpeDictionary(String keyword) => throw UnimplementedError();

  @override
  Future<void> confirmMapping({required String id, required String vendor, required String product}) =>
      throw UnimplementedError();

  @override
  Future<BulkMappingResult> confirmMappings(List<String> ids) => throw UnimplementedError();

  @override
  Future<BulkMappingResult> resetMappings(List<String> ids) => throw UnimplementedError();

  @override
  Future<void> markMappingNotApplicable({required String id, String? notes}) =>
      throw UnimplementedError();

  @override
  Future<void> resetMapping(String id) => throw UnimplementedError();

  @override
  Future<VulnerabilityRunStatus> readRunStatus() => throw UnimplementedError();

  @override
  Future<VulnerabilityRunStatus> startRun() => throw UnimplementedError();

  @override
  Future<VulnerabilityRunStatus> cancelRun() => throw UnimplementedError();
}
