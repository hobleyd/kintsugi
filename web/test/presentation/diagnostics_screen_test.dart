import 'package:flutter/gestures.dart';
import 'package:flutter/material.dart';
import 'package:go_router/go_router.dart';
import 'package:flutter/services.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:kintsugi_web/core/di/locator.dart';
import 'package:kintsugi_web/core/diagnostics/diagnostics_log.dart';
import 'package:kintsugi_web/core/platform/file_downloader.dart';
import 'package:kintsugi_web/core/platform/page_navigator.dart';
import 'package:kintsugi_web/core/theme/app_theme.dart';
import 'package:kintsugi_web/presentation/diagnostics/diagnostics_screen.dart';

class _FakeDownloader implements FileDownloader {
  final saved = <({String name, String mime, String content})>[];

  @override
  void save(String filename, String mimeType, String content) =>
      saved.add((name: filename, mime: mimeType, content: content));
}

class _FakeNavigator implements PageNavigator {
  final openedInNewTab = <String>[];
  final openedRoutes = <String>[];

  @override
  void go(String url) {}

  @override
  void openAppRouteInNewTab(String route) => openedRoutes.add(route);

  @override
  void post(String url, {Map<String, String> fields = const {}}) {}

  @override
  void openInNewTab(String url) => openedInNewTab.add(url);
}

/// The Diagnostics page: finding one line in a long run, getting it out of the app, and not losing
/// the runs when nothing is open.
void main() {
  late DiagnosticsLog log;
  late _FakeDownloader downloader;
  late _FakeNavigator navigator;
  late List<String> clipboard;

  setUp(() {
    log = DiagnosticsLog();
    downloader = _FakeDownloader();
    navigator = _FakeNavigator();
    clipboard = [];
    locator
      ..registerSingleton<FileDownloader>(downloader)
      ..registerSingleton<PageNavigator>(navigator);
    TestDefaultBinaryMessengerBinding.instance.defaultBinaryMessenger.setMockMethodCallHandler(
      SystemChannels.platform,
      (call) async {
        if (call.method == 'Clipboard.setData') {
          clipboard.add((call.arguments as Map)['text'] as String);
        }
        return null;
      },
    );
  });

  tearDown(() {
    locator.reset();
    TestDefaultBinaryMessengerBinding.instance.defaultBinaryMessenger
        .setMockMethodCallHandler(SystemChannels.platform, null);
  });

  /// Two runs, the older one first: a Check for Updates that failed on two rows, then a clean
  /// vulnerability assessment.
  void recordTwoRuns() {
    log
      ..record(
        title: 'Check for Updates',
        kind: DiagnosticsKind.error,
        summary: '1 updated, 2 failed.',
        source: 'Applications',
        lines: const [
          DiagnosticsLine('Slack (macOS): the script did not report a version.'),
          DiagnosticsLine('Zoom (macOS): the AI agent returned no script.'),
        ],
      )
      ..record(
        title: 'Vulnerability assessment',
        kind: DiagnosticsKind.success,
        summary: 'Checked 412 installed version(s).',
        source: 'Settings > Vulnerabilities',
      );
  }

  Future<void> pumpScreen(WidgetTester tester) async {
    tester.view.physicalSize = const Size(1500, 1600);
    tester.view.devicePixelRatio = 1.0;
    addTearDown(tester.view.reset);

    await tester.pumpWidget(
      MaterialApp(
        theme: AppTheme.light(),
        home: Scaffold(
          body: DiagnosticsLogScope(log: log, child: const DiagnosticsScreen()),
        ),
      ),
    );
    await tester.pumpAndSettle();
  }

  /// A Find Upgrade Paths run as the server now reports it: a line per row, each with its outcome.
  void recordCategorized() => log.record(
        title: 'Find Upgrade Paths',
        kind: DiagnosticsKind.error,
        summary: '2 resolved, 1 with no known path, 1 failed.',
        source: 'Installed Applications',
        lines: const [
          DiagnosticsLine('Firefox (macOS)', category: 'resolved', quiet: true),
          DiagnosticsLine('Chrome (macOS)', category: 'resolved', quiet: true),
          DiagnosticsLine('ncstreamer (macOS): no upstream source found.', category: 'noKnownPath'),
          DiagnosticsLine('Slack (macOS): the script did not report a version.', category: 'failed'),
        ],
      );

  testWidgets('says so, and offers no export, when nothing has been recorded', (tester) async {
    await pumpScreen(tester);

    expect(find.textContaining('Nothing has been recorded yet'), findsOneWidget);
    expect(tester.widget<OutlinedButton>(find.widgetWithText(OutlinedButton, 'COPY ALL')).onPressed, isNull);
    expect(tester.widget<OutlinedButton>(find.widgetWithText(OutlinedButton, 'CLEAR…')).onPressed, isNull);
  });

  testWidgets('lists the runs newest first, and opens the newest', (tester) async {
    recordTwoRuns();
    await pumpScreen(tester);

    final newest = tester.getTopLeft(find.byKey(ValueKey('run-${log.entries.first.id}')));
    final oldest = tester.getTopLeft(find.byKey(ValueKey('run-${log.entries.last.id}')));
    expect(newest.dy, lessThan(oldest.dy));
    expect(find.text('VULNERABILITY ASSESSMENT'), findsOneWidget);
  });

  testWidgets('shows a run\'s lines when its entry is chosen', (tester) async {
    recordTwoRuns();
    await pumpScreen(tester);

    await tester.tap(find.byKey(ValueKey('run-${log.entries.last.id}')));
    await tester.pumpAndSettle();

    expect(find.text('CHECK FOR UPDATES'), findsOneWidget);
    expect(find.textContaining('Slack (macOS)', findRichText: true), findsOneWidget);
    expect(find.textContaining('Zoom (macOS)', findRichText: true), findsOneWidget);
  });

  testWidgets('the kind filter narrows the list of runs', (tester) async {
    recordTwoRuns();
    await pumpScreen(tester);

    await tester.tap(find.text('NEEDS ATTENTION'));
    await tester.pumpAndSettle();

    expect(find.byKey(ValueKey('run-${log.entries.last.id}')), findsOneWidget);
    expect(find.byKey(ValueKey('run-${log.entries.first.id}')), findsNothing);
  });

  group('search', () {
    testWidgets('narrows the runs to those that match, and the lines to the matching ones', (tester) async {
      recordTwoRuns();
      await pumpScreen(tester);

      await tester.enterText(find.byType(TextField), 'zoom');
      await tester.pumpAndSettle();

      expect(find.byKey(ValueKey('run-${log.entries.first.id}')), findsNothing);
      expect(find.textContaining('Zoom (macOS)', findRichText: true), findsOneWidget);
      expect(find.textContaining('Slack (macOS)', findRichText: true), findsNothing);
    });

    testWidgets('shows the whole run when the match is in its title', (tester) async {
      recordTwoRuns();
      await pumpScreen(tester);

      await tester.enterText(find.byType(TextField), 'check for');
      await tester.pumpAndSettle();

      expect(find.textContaining('Slack (macOS)', findRichText: true), findsOneWidget);
      expect(find.textContaining('Zoom (macOS)', findRichText: true), findsOneWidget);
    });

    testWidgets('says when nothing matches', (tester) async {
      recordTwoRuns();
      await pumpScreen(tester);

      await tester.enterText(find.byType(TextField), 'no such thing');
      await tester.pumpAndSettle();

      expect(find.text('No run matches.'), findsWidgets);
    });
  });

  group('copying', () {
    testWidgets('copies one run as text', (tester) async {
      recordTwoRuns();
      await pumpScreen(tester);
      await tester.tap(find.byKey(ValueKey('run-${log.entries.last.id}')));
      await tester.pumpAndSettle();

      await tester.tap(find.text('COPY THIS RUN'));
      await tester.pumpAndSettle();

      expect(clipboard, hasLength(1));
      expect(clipboard.single, contains('Check for Updates'));
      expect(clipboard.single, contains('Slack (macOS): the script did not report a version.'));
      expect(clipboard.single, isNot(contains('Vulnerability assessment')));
      expect(find.text('Copied this run.'), findsOneWidget);
    });

    testWidgets('copies every listed run with Copy all', (tester) async {
      recordTwoRuns();
      await pumpScreen(tester);

      await tester.tap(find.text('COPY ALL'));
      await tester.pumpAndSettle();

      expect(clipboard.single, contains('Check for Updates'));
      expect(clipboard.single, contains('Vulnerability assessment'));
    });

    testWidgets('Copy all copies only what the filter and search leave listed', (tester) async {
      recordTwoRuns();
      await pumpScreen(tester);
      // "OK" is also the status chip of the run that opens by default; the filter chip is the first.
      await tester.tap(find.text('OK').first);
      await tester.pumpAndSettle();

      await tester.tap(find.text('COPY ALL'));
      await tester.pumpAndSettle();

      expect(clipboard.single, contains('Vulnerability assessment'));
      expect(clipboard.single, isNot(contains('Check for Updates')));
    });

    testWidgets('copies one line from its own button', (tester) async {
      recordTwoRuns();
      await pumpScreen(tester);
      await tester.tap(find.byKey(ValueKey('run-${log.entries.last.id}')));
      await tester.pumpAndSettle();

      final line = find.byKey(ValueKey('line-${log.entries.last.id}-1'));
      final pointer = await tester.createGesture(kind: PointerDeviceKind.mouse);
      await pointer.addPointer(location: Offset.zero);
      addTearDown(pointer.removePointer);
      await pointer.moveTo(tester.getCenter(line));
      await tester.pumpAndSettle();
      await tester.tap(find.descendant(of: line, matching: find.byIcon(Icons.copy_outlined)));
      await tester.pumpAndSettle();

      expect(clipboard.single, 'Zoom (macOS): the AI agent returned no script.');
    });
  });

  group('exporting', () {
    Future<void> choose(WidgetTester tester, String item) async {
      await tester.tap(find.text('EXPORT'));
      await tester.pumpAndSettle();
      await tester.tap(find.text(item));
      await tester.pumpAndSettle();
    }

    testWidgets('exports the open run as CSV', (tester) async {
      recordTwoRuns();
      await pumpScreen(tester);
      await tester.tap(find.byKey(ValueKey('run-${log.entries.last.id}')));
      await tester.pumpAndSettle();

      await choose(tester, 'This run as CSV');

      final file = downloader.saved.single;
      expect(file.name, 'diagnostics-run.csv');
      expect(file.mime, 'text/csv');
      expect(file.content, contains('Slack (macOS): the script did not report a version.'));
      expect(file.content, isNot(contains('Vulnerability assessment')));
      expect(find.text('Exported diagnostics-run.csv.'), findsOneWidget);
    });

    testWidgets('exports every saved run as JSON', (tester) async {
      recordTwoRuns();
      await pumpScreen(tester);

      await choose(tester, 'All runs as JSON');

      final file = downloader.saved.single;
      expect(file.name, 'diagnostics-all.json');
      expect(file.mime, 'application/json');
      expect(file.content, contains('Check for Updates'));
      expect(file.content, contains('Vulnerability assessment'));
    });

    testWidgets('an export of all runs is not narrowed by the search', (tester) async {
      recordTwoRuns();
      await pumpScreen(tester);
      await tester.enterText(find.byType(TextField), 'zoom');
      await tester.pumpAndSettle();

      await choose(tester, 'All runs as CSV');

      expect(downloader.saved.single.content, contains('Vulnerability assessment'));
    });
  });

  testWidgets('Open in new tab asks the browser for this page', (tester) async {
    recordTwoRuns();
    await pumpScreen(tester);

    await tester.tap(find.text('OPEN IN NEW TAB'));

    expect(navigator.openedRoutes, ['/diagnostics']);
    expect(navigator.openedInNewTab, isEmpty, reason: 'a route of this app, not a finished address');
  });

  group('clearing', () {
    testWidgets('asks first, and removes everything once confirmed', (tester) async {
      recordTwoRuns();
      await pumpScreen(tester);

      await tester.tap(find.text('CLEAR…'));
      await tester.pumpAndSettle();
      expect(find.text('Clear every saved run?'), findsOneWidget);
      expect(log.count, 2);

      await tester.tap(find.text('CLEAR'));
      await tester.pumpAndSettle();

      expect(log.isEmpty, isTrue);
      expect(find.textContaining('Nothing has been recorded yet'), findsOneWidget);
    });

    testWidgets('keeps everything when cancelled', (tester) async {
      recordTwoRuns();
      await pumpScreen(tester);

      await tester.tap(find.text('CLEAR…'));
      await tester.pumpAndSettle();
      await tester.tap(find.text('CANCEL'));
      await tester.pumpAndSettle();

      expect(log.count, 2);
    });
  });

  testWidgets('shows a run another tab records while it is open', (tester) async {
    await pumpScreen(tester);
    expect(find.textContaining('Nothing has been recorded yet'), findsOneWidget);

    log.record(title: 'Check for Updates', kind: DiagnosticsKind.error, summary: 'x', lines: const [DiagnosticsLine('a')]);
    await tester.pumpAndSettle();

    expect(find.text('CHECK FOR UPDATES'), findsOneWidget);
  });

  group('a line that names a row', () {
    Future<void> pumpWithRouter(WidgetTester tester) async {
      tester.view.physicalSize = const Size(1500, 1600);
      tester.view.devicePixelRatio = 1.0;
      addTearDown(tester.view.reset);

      final router = GoRouter(routes: [
        GoRoute(
          path: '/',
          builder: (_, _) => Scaffold(body: DiagnosticsLogScope(log: log, child: const DiagnosticsScreen())),
        ),
        GoRoute(path: '/applications', builder: (_, _) => const Scaffold(body: Text('THE APPLICATIONS PAGE'))),
      ]);
      await tester.pumpWidget(MaterialApp.router(theme: AppTheme.light(), routerConfig: router));
      await tester.pumpAndSettle();
    }

    void recordLinked() => log.record(
          title: 'Check for Updates',
          kind: DiagnosticsKind.error,
          summary: 'x',
          lines: const [
            DiagnosticsLine(
              'Slack (macOS): the script did not report a version.',
              target: '/applications?search=Slack',
            ),
          ],
        );

    Finder lineText() => find.descendant(
          of: find.byKey(ValueKey('line-${log.entries.single.id}-0')),
          matching: find.byType(RichText),
        );

    /// The pieces of text a line is drawn from, however deep the framework nests them.
    List<TextSpan> pieces(WidgetTester tester) {
      final out = <TextSpan>[];
      tester.widget<RichText>(lineText().first).text.visitChildren((span) {
        if (span is TextSpan && span.text != null) out.add(span);
        return true;
      });
      return out;
    }


    testWidgets('is a link on the application and platform only', (tester) async {
      recordLinked();
      await pumpWithRouter(tester);

      // The name is at the left edge of the line; the note is further along it.
      await tester.tapAt(tester.getTopLeft(lineText().first) + const Offset(6, 8));
      await tester.pumpAndSettle();

      expect(find.text('THE APPLICATIONS PAGE'), findsOneWidget, reason: 'the name is the link');
    });

    testWidgets('does not navigate when the note after it is clicked', (tester) async {
      recordLinked();
      await pumpWithRouter(tester);

      final box = tester.getRect(lineText().first);
      // The line's text is about 50 characters of 12.5pt monospace, so well inside the box and past the
      // name, which is the first 13 of them.
      await tester.tapAt(Offset(box.left + 12.5 * 0.6 * 40, box.top + 8));
      await tester.pumpAndSettle();

      expect(find.text('THE APPLICATIONS PAGE'), findsNothing, reason: 'the note is prose, not part of the link');
    });

    testWidgets('only the name is underlined', (tester) async {
      recordLinked();
      await pumpWithRouter(tester);

      final spans = pieces(tester);

      final underlined = spans.where((s) => s.style?.decoration == TextDecoration.underline).map((s) => s.text).join();
      final plain = spans.where((s) => s.style?.decoration != TextDecoration.underline).map((s) => s.text).join();
      expect(underlined, 'Slack (macOS)');
      expect(plain, ': the script did not report a version.');
    });

    testWidgets('a name with brackets in it is still the whole link', (tester) async {
      log.record(
        title: 'Find Upgrade Paths',
        kind: DiagnosticsKind.error,
        summary: 'x',
        lines: const [
          DiagnosticsLine(
            'Microsoft Visual C++ 2015-2022 Redistributable (x64) (Windows): no known path.',
            target: '/applications?search=x',
          ),
        ],
      );
      await pumpWithRouter(tester);

      final underlined = pieces(tester)
          .where((s) => s.style?.decoration == TextDecoration.underline)
          .map((s) => s.text)
          .join();
      expect(underlined, 'Microsoft Visual C++ 2015-2022 Redistributable (x64) (Windows)');
    });

    testWidgets('search marks matches in the name and in the note', (tester) async {
      recordLinked();
      await pumpWithRouter(tester);

      await tester.enterText(find.byType(TextField), 's');
      await tester.pumpAndSettle();

      final marked = pieces(tester).where((s) => s.style?.backgroundColor != null);
      expect(marked, isNotEmpty);
      expect(marked.any((s) => s.style?.decoration == TextDecoration.underline), isTrue, reason: 'a match in the name is still underlined');
    });
  });

  group('result tabs', () {
    Finder tab(String key) => find.byKey(ValueKey('tab-$key'));

    /// The number drawn beside a tab's name.
    String countOn(WidgetTester tester, String key) =>
        tester.widgetList<Text>(find.descendant(of: tab(key), matching: find.byType(Text))).last.data!;

    testWidgets('are the outcomes the run\'s rows carry, with how many are in each', (tester) async {
      recordCategorized();
      await pumpScreen(tester);

      expect(countOn(tester, 'all'), '4');
      expect(countOn(tester, 'resolved'), '2');
      expect(countOn(tester, 'noKnownPath'), '1');
      expect(countOn(tester, 'failed'), '1');
      expect(tab('updated'), findsNothing, reason: 'a tab this run has no rows for is not offered');
    });

    testWidgets('open on All to start with, listing every row', (tester) async {
      recordCategorized();
      await pumpScreen(tester);

      expect(find.textContaining('Firefox (macOS)', findRichText: true), findsOneWidget);
      expect(find.textContaining('Slack (macOS)', findRichText: true), findsOneWidget);
    });

    testWidgets('narrow the list to one outcome, and All brings the rest back', (tester) async {
      recordCategorized();
      await pumpScreen(tester);

      await tester.tap(tab('failed'));
      await tester.pumpAndSettle();

      expect(find.textContaining('Slack (macOS)', findRichText: true), findsOneWidget);
      expect(find.textContaining('Firefox (macOS)', findRichText: true), findsNothing);
      expect(find.textContaining('ncstreamer', findRichText: true), findsNothing);

      await tester.tap(tab('all'));
      await tester.pumpAndSettle();

      expect(find.textContaining('Firefox (macOS)', findRichText: true), findsOneWidget);
    });

    testWidgets('the resolved tab lists the rows that have no note, which the panel never shows', (tester) async {
      recordCategorized();
      await pumpScreen(tester);

      await tester.tap(tab('resolved'));
      await tester.pumpAndSettle();

      expect(find.textContaining('Firefox (macOS)', findRichText: true), findsOneWidget);
      expect(find.textContaining('Chrome (macOS)', findRichText: true), findsOneWidget);
    });

    testWidgets('a search counts its matches per tab', (tester) async {
      recordCategorized();
      await pumpScreen(tester);

      await tester.enterText(find.byType(TextField), 'slack');
      await tester.pumpAndSettle();

      expect(countOn(tester, 'all'), '1');
      expect(countOn(tester, 'failed'), '1');
      expect(countOn(tester, 'resolved'), '0');
    });

    testWidgets('a run saved before outcomes were kept has no tabs', (tester) async {
      recordTwoRuns();
      await pumpScreen(tester);
      await tester.tap(find.byKey(ValueKey('run-${log.entries.last.id}')));
      await tester.pumpAndSettle();

      expect(tab('all'), findsNothing);
      expect(tab('failed'), findsNothing);
    });

    testWidgets('go back to All when another run is chosen', (tester) async {
      recordCategorized();
      recordTwoRuns();
      await pumpScreen(tester);
      final categorized = find.byKey(ValueKey('run-${log.entries.last.id}'));
      final other = find.byKey(ValueKey('run-${log.entries.first.id}'));

      await tester.tap(categorized);
      await tester.pumpAndSettle();
      await tester.tap(tab('failed'));
      await tester.pumpAndSettle();
      expect(find.textContaining('Firefox (macOS)', findRichText: true), findsNothing);

      await tester.tap(other);
      await tester.pumpAndSettle();
      await tester.tap(categorized);
      await tester.pumpAndSettle();

      expect(find.textContaining('Firefox (macOS)', findRichText: true), findsOneWidget);
    });

    testWidgets('are carried into an export as an outcome column', (tester) async {
      recordCategorized();
      await pumpScreen(tester);

      await tester.tap(find.text('EXPORT'));
      await tester.pumpAndSettle();
      await tester.tap(find.text('This run as CSV'));
      await tester.pumpAndSettle();

      final csv = downloader.saved.single.content;
      expect(csv, contains('"Resolved","Firefox (macOS)"'));
      expect(csv, contains('"No known path","ncstreamer (macOS): no upstream source found."'));
      expect(csv, contains('"Failed","Slack (macOS): the script did not report a version."'));
    });
  });
}
