import 'dart:async';
import 'dart:convert';

import 'package:flutter_test/flutter_test.dart';
import 'package:kintsugi_web/core/diagnostics/diagnostics_log.dart';
import 'package:kintsugi_web/core/diagnostics/diagnostics_store.dart';
import 'package:shared_preferences/shared_preferences.dart';

class _FakeStore implements DiagnosticsStore {
  _FakeStore([List<DiagnosticsEntry> saved = const []]) : saved = List.of(saved);

  List<DiagnosticsEntry> saved;
  final saves = <List<DiagnosticsEntry>>[];
  final elsewhere = StreamController<void>.broadcast();
  bool failSaves = false;

  @override
  List<DiagnosticsEntry> load() => List.of(saved);

  @override
  Future<List<DiagnosticsEntry>> reload() async => List.of(saved);

  @override
  Future<void> save(List<DiagnosticsEntry> entries) async {
    if (failSaves) throw StateError('quota exceeded');
    saves.add(List.of(entries));
    saved = List.of(entries);
  }

  @override
  Stream<void> get changedElsewhere => elsewhere.stream;
}

DiagnosticsEntry _entry(int id, {String title = 'Check for Updates', List<String> lines = const []}) =>
    DiagnosticsEntry(
      id: id,
      title: title,
      kind: DiagnosticsKind.error,
      recordedAt: DateTime.utc(2026, 11, 4, 10, 12, 41).toLocal(),
      summary: '1 failed.',
      source: 'Applications',
      lines: [for (final line in lines) DiagnosticsLine(line, target: '/applications?name=$line')],
    );

extension on DiagnosticsEntry {
  DiagnosticsEntry copyWithLines(List<DiagnosticsLine> lines) => DiagnosticsEntry(
        id: id,
        title: title,
        kind: kind,
        recordedAt: recordedAt,
        summary: summary,
        source: source,
        lines: lines,
      );
}

/// What the log keeps across a reload, and what the codec refuses to trust.
void main() {
  group('the saved form', () {
    test('round-trips an entry, including the route a line is about', () {
      final decoded = decodeDiagnosticsEntries(
        encodeDiagnosticsEntries([_entry(3, lines: ['Slack (macOS): no version.'])]),
      );

      final entry = decoded.single;
      expect(entry.id, 3);
      expect(entry.title, 'Check for Updates');
      expect(entry.kind, DiagnosticsKind.error);
      expect(entry.summary, '1 failed.');
      expect(entry.source, 'Applications');
      expect(entry.recordedAt.toUtc(), DateTime.utc(2026, 11, 4, 10, 12, 41));
      expect(entry.lines.single.text, 'Slack (macOS): no version.');
      expect(entry.lines.single.target, '/applications?name=Slack (macOS): no version.');
    });

    test('keeps a line with no route as plain text', () {
      final decoded = decodeDiagnosticsEntries(encodeDiagnosticsEntries([
        DiagnosticsEntry(
          id: 0,
          title: 'x',
          kind: DiagnosticsKind.info,
          recordedAt: DateTime.now(),
          lines: const [DiagnosticsLine('...and 3 more not listed.')],
        ),
      ]));

      expect(decoded.single.lines.single.target, isNull);
    });

    test('saves at most maxLinesPerEntry lines and says how many it left out', () {
      const max = SharedPreferencesDiagnosticsStore.maxLinesPerEntry;
      final lines = [for (var i = 0; i < max + 7; i++) 'row $i'];

      final decoded = decodeDiagnosticsEntries(encodeDiagnosticsEntries([_entry(0, lines: lines)])).single;

      expect(decoded.lines, hasLength(max + 1));
      expect(decoded.lines.last.text, '...and 7 more lines not saved.');
      expect(decoded.lines.first.text, 'row 0');
    });

    test('round-trips what a row concluded and whether it was routine', () {
      final decoded = decodeDiagnosticsEntries(encodeDiagnosticsEntries([
        DiagnosticsEntry(
          id: 0,
          title: 'Find Upgrade Paths',
          kind: DiagnosticsKind.error,
          recordedAt: DateTime.now(),
          lines: const [
            DiagnosticsLine('Firefox (macOS)', category: 'resolved', quiet: true),
            DiagnosticsLine('Slack (macOS): no version.', category: 'failed'),
            DiagnosticsLine('...and 3 more rows not listed.'),
          ],
        ),
      ])).single;

      expect(decoded.lines.map((l) => l.category), ['resolved', 'failed', null]);
      expect(decoded.lines.map((l) => l.quiet), [true, false, false]);
    });

    test('reads a run saved before outcomes were kept: no category, not quiet', () {
      final saved = jsonEncode({
        'v': 1,
        'entries': [
          {
            'id': 1,
            'title': 'Check for Updates',
            'kind': 'error',
            'at': '2026-11-04T10:00:00Z',
            'lines': [
              {'t': 'Slack (macOS): no version.', 'go': '/applications?search=Slack'},
            ],
          },
        ],
      });

      final line = decodeDiagnosticsEntries(saved).single.lines.single;

      expect(line.category, isNull);
      expect(line.quiet, isFalse);
    });

    test('an outcome this build does not know reads as no category, not as a failure', () {
      final saved = jsonEncode({
        'v': 1,
        'entries': [
          {
            'id': 1,
            'title': 'x',
            'kind': 'info',
            'at': '2026-11-04T10:00:00Z',
            'lines': [
              {'t': 'a', 'c': 'someFutureOutcome'},
            ],
          },
        ],
      });

      expect(decodeDiagnosticsEntries(saved).single.lines.single.category, isNull);
    });

    test('when it must cut a long run it drops the routine rows first and keeps the order', () {
      const max = SharedPreferencesDiagnosticsStore.maxLinesPerEntry;
      // Quiet rows from 0 to max+49, with a loud one in the middle and one at the very end.
      final lines = [
        for (var i = 0; i < max + 50; i++)
          DiagnosticsLine('row $i', category: 'resolved', quiet: true),
      ];
      lines[200] = const DiagnosticsLine('loud middle', category: 'failed');
      lines[max + 49] = const DiagnosticsLine('loud last', category: 'failed');

      final decoded = decodeDiagnosticsEntries(
        encodeDiagnosticsEntries([_entry(0).copyWithLines(lines)]),
      ).single;

      final texts = decoded.lines.map((l) => l.text).toList();
      expect(texts, contains('loud middle'));
      expect(texts, contains('loud last'), reason: 'a failed row at the end of a long run is the one that must survive');
      expect(texts.last, '...and 50 more lines not saved.');
      expect(texts.indexOf('loud middle'), lessThan(texts.indexOf('loud last')));
      expect(decoded.lines, hasLength(max + 1));
    });

    test('nothing saved, empty text, and text that is not JSON all read as no entries', () {
      expect(decodeDiagnosticsEntries(null), isEmpty);
      expect(decodeDiagnosticsEntries(''), isEmpty);
      expect(decodeDiagnosticsEntries('{not json'), isEmpty);
    });

    test('a different version or shape is not trusted', () {
      expect(decodeDiagnosticsEntries(jsonEncode({'v': 99, 'entries': []})), isEmpty);
      expect(decodeDiagnosticsEntries(jsonEncode({'v': 1, 'entries': 'x'})), isEmpty);
      expect(decodeDiagnosticsEntries(jsonEncode([1, 2])), isEmpty);
    });

    test('skips one broken entry and keeps the rest', () {
      final saved = jsonEncode({
        'v': 1,
        'entries': [
          {'id': 1, 'title': 'good', 'kind': 'success', 'at': '2026-11-04T10:00:00Z', 'lines': []},
          {'id': 'nope', 'title': 'bad id', 'kind': 'success', 'at': '2026-11-04T10:00:00Z'},
          {'id': 3, 'title': 'bad kind', 'kind': 'purple', 'at': '2026-11-04T10:00:00Z'},
          {'id': 4, 'title': 'bad time', 'kind': 'info', 'at': 'yesterday'},
        ],
      });

      expect(decodeDiagnosticsEntries(saved).map((e) => e.title), ['good']);
    });
  });

  group('SharedPreferencesDiagnosticsStore', () {
    test('saves to preferences and reads it back after a reload', () async {
      SharedPreferences.setMockInitialValues({});
      final preferences = await SharedPreferences.getInstance();
      final store = SharedPreferencesDiagnosticsStore(preferences);

      await store.save([_entry(2, lines: ['a'])]);

      expect((await store.reload()).single.id, 2);
      expect(preferences.getString(SharedPreferencesDiagnosticsStore.storageKey), isNotNull);
    });
  });

  group('DiagnosticsLog with a store', () {
    test('starts with what was saved, and numbers new entries after the highest saved id', () {
      final log = DiagnosticsLog(store: _FakeStore([_entry(4), _entry(9)]));

      expect(log.count, 2);

      log.record(title: 'next', kind: DiagnosticsKind.info);

      expect(log.entries.first.id, 10);
    });

    test('starts closed even though there are saved entries', () {
      expect(DiagnosticsLog(store: _FakeStore([_entry(1)])).isOpen, isFalse);
    });

    test('keeps only the newest retained entries when more were saved', () {
      final saved = [for (var i = 0; i < DiagnosticsLog.retained + 5; i++) _entry(i)];

      final log = DiagnosticsLog(store: _FakeStore(saved));

      expect(log.count, DiagnosticsLog.retained);
      expect(log.entries.first.id, DiagnosticsLog.retained + 4);
    });

    test('saves after a record, a dismiss and a clear', () async {
      final store = _FakeStore();
      final log = DiagnosticsLog(store: store);

      log.record(title: 'one', kind: DiagnosticsKind.error);
      log.record(title: 'two', kind: DiagnosticsKind.error);
      log.dismiss(log.entries.first.id);
      log.clear();
      await Future<void>.delayed(Duration.zero);

      expect(store.saves.map((s) => s.length), [1, 2, 1, 0]);
    });

    test('a store that cannot save costs persistence and nothing else', () async {
      final store = _FakeStore()..failSaves = true;
      final log = DiagnosticsLog(store: store);

      log.record(title: 'still shown', kind: DiagnosticsKind.error, summary: 'x');
      await Future<void>.delayed(Duration.zero);

      expect(log.entries.single.title, 'still shown');
    });

    test('takes what another tab saved, and tells its listeners', () async {
      final store = _FakeStore();
      final log = DiagnosticsLog(store: store);
      var notified = 0;
      log.addListener(() => notified++);

      store.saved = [_entry(7, title: 'from the other tab')];
      store.elsewhere.add(null);
      await Future<void>.delayed(Duration.zero);

      expect(log.entries.single.title, 'from the other tab');
      expect(notified, 1);
    });

    test('closes the panel when the other tab cleared everything', () async {
      final store = _FakeStore([_entry(1)]);
      final log = DiagnosticsLog(store: store)..open();

      store.saved = [];
      store.elsewhere.add(null);
      await Future<void>.delayed(Duration.zero);

      expect(log.isEmpty, isTrue);
      expect(log.isOpen, isFalse);
    });

    test('without a store it is the in-memory log it always was', () {
      final log = DiagnosticsLog();

      log.record(title: 'x', kind: DiagnosticsKind.info);

      expect(log.count, 1);
    });
  });
}
