import 'package:flutter/material.dart';
import 'package:flutter_bloc/flutter_bloc.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:kintsugi_web/core/diagnostics/diagnostics_log.dart';
import 'package:kintsugi_web/core/theme/app_theme.dart';
import 'package:kintsugi_web/domain/entities/upgrade_path.dart';
import 'package:kintsugi_web/presentation/applications/background_run_bloc.dart';
import 'package:kintsugi_web/presentation/applications/run_progress_mappers.dart';
import 'package:kintsugi_web/presentation/applications/widgets/run_progress_view.dart';
import 'package:kintsugi_web/presentation/shell/diagnostics_panel.dart';

class _RunBloc extends BackgroundRunBloc {
  _RunBloc({required super.start, required super.status});
}

UpgradePathScanStatus _scan({String? faultReason, List<String> notes = const []}) => UpgradePathScanStatus(
      isRunning: false,
      total: 5,
      completed: 5,
      resolved: 2,
      notFound: 1,
      failed: 1,
      skipped: 1,
      startedUtc: DateTime.utc(2026, 11, 4, 10),
      completedUtc: DateTime.utc(2026, 11, 4, 11),
      faultReason: faultReason,
      notes: notes,
    );

RunRow _row(String app, String? outcome, [String? note]) =>
    RunRow(applicationName: app, platform: 'macOS', outcome: outcome, note: note);

/// What a finished run keeps now that the server says which row is which: a line per row, carrying
/// its outcome, so the Diagnostics page can split them into tabs — while the side panel still shows
/// only the rows that have something to say.
void main() {
  group('runRowLine', () {
    test('reads as the note always did when the row has one, and is not quiet', () {
      final line = runRowLine(_row('Slack', 'failed', 'the script did not report a version.'));

      expect(line.text, 'Slack (macOS): the script did not report a version.');
      expect(line.category, 'failed');
      expect(line.quiet, isFalse);
      expect(line.target, '/applications?search=Slack');
    });

    test('is just the name and platform, and quiet, when the row has no note', () {
      final line = runRowLine(_row('Firefox', 'resolved'));

      expect(line.text, 'Firefox (macOS)');
      expect(line.quiet, isTrue);
    });

    test('treats a blank note as none', () {
      expect(runRowLine(_row('Firefox', 'resolved', '   ')).quiet, isTrue);
    });

    test('links an application whose name has brackets and spaces to its own row', () {
      final line = runRowLine(_row('Microsoft Visual C++ 2015-2022 Redistributable (x64)', 'noKnownPath', 'no known path.'));

      expect(line.target, contains('search=Microsoft+Visual+C%2B%2B'));
    });
  });

  group('a finished run with rows', () {
    late DiagnosticsLog log;

    Future<void> pumpRun(
      WidgetTester tester, {
      required Future<RunResults> Function()? loadRows,
      UpgradePathScanStatus? status,
    }) async {
      final finished = status ?? _scan(notes: const ['Slack (macOS): the AI agent returned no script.']);
      await tester.pumpWidget(
        MaterialApp(
          theme: AppTheme.light(),
          home: Scaffold(
            body: DiagnosticsLogScope(
              log: log,
              child: BlocProvider(
                create: (_) => _RunBloc(
                  start: () async => startedProgress(RunStarted(started: true, status: finished), scanProgress),
                  status: () async => scanProgress(finished),
                )..add(const RunStartRequested()),
                child: RunProgressView<_RunBloc>(
                  title: 'Find Upgrade Paths',
                  source: 'Installed Applications',
                  onFinished: () {},
                  loadRows: loadRows,
                ),
              ),
            ),
          ),
        ),
      );
      await tester.pumpAndSettle();
    }

    setUp(() => log = DiagnosticsLog());

    testWidgets('keeps a line per row, each with its outcome', (tester) async {
      await pumpRun(
        tester,
        loadRows: () async => RunResults(
          rows: [
            _row('Firefox', 'resolved'),
            _row('ncstreamer', 'noKnownPath', 'no upstream source found.'),
            _row('Slack', 'failed', 'the AI agent returned no script.'),
          ],
          unlisted: 0,
        ),
      );

      final lines = log.entries.single.lines;
      expect(lines.map((l) => l.category), ['resolved', 'noKnownPath', 'failed']);
      expect(lines.map((l) => l.quiet), [true, false, false]);
      expect(log.entries.single.summary, contains('2 resolved'));

      await tester.pumpWidget(const SizedBox());
    });

    testWidgets('says how many rows the server counted without keeping', (tester) async {
      await pumpRun(tester, loadRows: () async => RunResults(rows: [_row('Slack', 'failed', 'x')], unlisted: 12));

      expect(log.entries.single.lines.last.text, contains('12 more rows not listed'));
      expect(log.entries.single.lines.last.quiet, isFalse);

      await tester.pumpWidget(const SizedBox());
    });

    testWidgets('falls back to the notes when the rows cannot be read', (tester) async {
      await pumpRun(tester, loadRows: () async => throw StateError('older server'));

      final lines = log.entries.single.lines;
      expect(lines.single.text, 'Slack (macOS): the AI agent returned no script.');
      expect(lines.single.category, isNull, reason: 'without rows there is nothing to split into tabs');

      await tester.pumpWidget(const SizedBox());
    });

    testWidgets('does not ask for rows of a run that faulted', (tester) async {
      var asked = false;
      await pumpRun(
        tester,
        status: _scan(faultReason: 'The run died.'),
        loadRows: () async {
          asked = true;
          return const RunResults(rows: [], unlisted: 0);
        },
      );

      expect(asked, isFalse, reason: "a faulted run's rows are of work it never finished");
      expect(log.entries.single.lines, isEmpty);

      await tester.pumpWidget(const SizedBox());
    });

    testWidgets('a run whose rows are all routine does not open the panel', (tester) async {
      await pumpRun(
        tester,
        status: _scan(),
        loadRows: () async => RunResults(rows: [_row('Firefox', 'resolved'), _row('Chrome', 'resolved')], unlisted: 0),
      );

      expect(log.count, 1);
      expect(log.isOpen, isFalse, reason: 'two hundred resolved rows are not a reason to slide a panel over the page');

      await tester.pumpWidget(const SizedBox());
    });

    testWidgets('a run with one row that needs reading does', (tester) async {
      await pumpRun(
        tester,
        status: _scan(),
        loadRows: () async => RunResults(rows: [_row('Firefox', 'resolved'), _row('Slack', 'failed', 'x')], unlisted: 0),
      );

      expect(log.isOpen, isTrue);

      await tester.pumpWidget(const SizedBox());
    });
  });

  group('the side panel', () {
    Future<void> pumpPanel(WidgetTester tester, DiagnosticsLog log) async {
      tester.view.physicalSize = const Size(1200, 900);
      tester.view.devicePixelRatio = 1.0;
      addTearDown(tester.view.reset);

      await tester.pumpWidget(
        MaterialApp(
          theme: AppTheme.light(),
          home: Scaffold(
            body: Row(children: [const Expanded(child: SizedBox()), DiagnosticsPanel(log: log)]),
          ),
        ),
      );
      await tester.pumpAndSettle();
    }

    DiagnosticsLog twoQuietOneLoud() => DiagnosticsLog()
      ..record(
        title: 'Find Upgrade Paths',
        kind: DiagnosticsKind.error,
        summary: 'x',
        lines: const [
          DiagnosticsLine('Firefox (macOS)', category: 'resolved', quiet: true),
          DiagnosticsLine('Chrome (macOS)', category: 'resolved', quiet: true),
          DiagnosticsLine('Slack (macOS): no version.', category: 'failed'),
        ],
      );

    testWidgets('lists only the rows that have something to say, and counts the rest', (tester) async {
      await pumpPanel(tester, twoQuietOneLoud());

      expect(find.text('Slack (macOS): no version.'), findsOneWidget);
      expect(find.text('Firefox (macOS)'), findsNothing);
      expect(find.text('Chrome (macOS)'), findsNothing);
      expect(find.text('+ 2 more rows without a note, on the Diagnostics page'), findsOneWidget);
    });

    testWidgets('says "row" for one', (tester) async {
      final log = DiagnosticsLog()
        ..record(
          title: 'x',
          kind: DiagnosticsKind.error,
          lines: const [DiagnosticsLine('Firefox (macOS)', quiet: true)],
        );
      await pumpPanel(tester, log);

      expect(find.text('+ 1 more row without a note, on the Diagnostics page'), findsOneWidget);
    });

    testWidgets('a run with no quiet rows shows no such line', (tester) async {
      final log = DiagnosticsLog()
        ..record(
          title: 'x',
          kind: DiagnosticsKind.error,
          lines: const [DiagnosticsLine('Slack (macOS): no version.')],
        );
      await pumpPanel(tester, log);

      expect(find.textContaining('more row'), findsNothing);
    });
  });
}
