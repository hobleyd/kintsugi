import 'dart:convert';

import 'package:shared_preferences/shared_preferences.dart';

import 'diagnostics_category.dart';
import 'diagnostics_log.dart';

/// Where [DiagnosticsLog] keeps what it has recorded, so a reload — or a second tab — still has it.
///
/// Behind an interface for the reason `PageNavigator` is: the log is plain Dart that tests build
/// without a browser, and the thing that knows about `localStorage` and its `storage` event is one
/// small implementation.
abstract interface class DiagnosticsStore {
  /// What was last saved, oldest first. Empty — never an error — when nothing was, or when what is
  /// there cannot be read: a corrupt value must not stop the app starting.
  List<DiagnosticsEntry> load();

  /// Reads again from the underlying storage, for a change another tab made. [load] alone would
  /// answer from a cache taken when this page started.
  Future<List<DiagnosticsEntry>> reload();

  Future<void> save(List<DiagnosticsEntry> entries);

  /// Fires when **another** tab saved. Never for this tab's own [save], which the browser does not
  /// report to the tab that made it — and which is why the log can simply re-read on every event.
  Stream<void> get changedElsewhere;
}

/// A [DiagnosticsStore] over `SharedPreferences`, which on the web is `localStorage`.
///
/// **Last writer wins.** Every save writes the whole list, so two tabs that both record within
/// milliseconds of each other lose one entry. A tab that records tells the others at once (the
/// `storage` event), so tabs converge; the case that is lost is two runs finishing in the same
/// instant in two tabs, which is not worth a merge protocol for an administrator's scratch log.
class SharedPreferencesDiagnosticsStore implements DiagnosticsStore {
  SharedPreferencesDiagnosticsStore(this._preferences, {Stream<void>? changedElsewhere})
      : changedElsewhere = changedElsewhere ?? const Stream.empty();

  /// `localStorage` holds this as `flutter.kintsugi.diagnostics`; the page-level watcher is given
  /// that prefixed name.
  static const storageKey = 'kintsugi.diagnostics';

  /// The most lines of one entry that are saved. A Find Upgrade Paths run keeps a note for every
  /// row that has one, with no bound server-side, and `localStorage` gives a page about 5 MB for
  /// everything. The entry says how many it left out, so the cut is visible rather than silent.
  static const maxLinesPerEntry = 500;

  final SharedPreferences _preferences;

  @override
  final Stream<void> changedElsewhere;

  @override
  List<DiagnosticsEntry> load() => decodeDiagnosticsEntries(_preferences.getString(storageKey));

  @override
  Future<List<DiagnosticsEntry>> reload() async {
    await _preferences.reload();
    return load();
  }

  @override
  Future<void> save(List<DiagnosticsEntry> entries) async {
    await _preferences.setString(storageKey, encodeDiagnosticsEntries(entries));
  }
}

const _version = 1;

/// The saved form. Versioned so a later change to what an entry holds can tell an old save from a
/// new one instead of guessing from its shape.
String encodeDiagnosticsEntries(List<DiagnosticsEntry> entries) => jsonEncode({
      'v': _version,
      'entries': [
        for (final entry in entries)
          {
            'id': entry.id,
            'title': entry.title,
            'kind': entry.kind.name,
            'at': entry.recordedAt.toUtc().toIso8601String(),
            'summary': entry.summary,
            'source': entry.source,
            'lines': _savedLines(entry.lines),
          },
      ],
    });

List<Map<String, Object?>> _savedLines(List<DiagnosticsLine> lines) {
  const max = SharedPreferencesDiagnosticsStore.maxLinesPerEntry;
  final kept = lines.length <= max ? lines : _keepMost(lines, max);
  return [
    for (final line in kept)
      {
        't': line.text,
        'go': line.target,
        if (line.category != null) 'c': line.category,
        if (line.quiet) 'q': true,
      },
    if (lines.length > max) {'t': '...and ${lines.length - max} more lines not saved.', 'go': null},
  ];
}

/// The [max] lines worth keeping, in their original order: every line with something to say first,
/// then as many of the quiet ones — the rows that simply succeeded — as there is room for. A run of
/// two thousand rows loses its routine ones, not the few that failed.
List<DiagnosticsLine> _keepMost(List<DiagnosticsLine> lines, int max) {
  final keep = <int>{
    for (var i = 0; i < lines.length; i++)
      if (!lines[i].quiet) i,
  }.take(max).toSet();
  for (var i = 0; i < lines.length && keep.length < max; i++) {
    keep.add(i);
  }
  return [for (var i = 0; i < lines.length; i++) if (keep.contains(i)) lines[i]];
}

/// Reads what [encodeDiagnosticsEntries] wrote. Anything that is not that — nothing saved, an
/// older or newer version, text that is not JSON, one entry missing a field — gives what could be
/// read, which may be nothing. This runs at startup, and a bad value in `localStorage` must cost
/// the saved runs, not the app.
List<DiagnosticsEntry> decodeDiagnosticsEntries(String? saved) {
  if (saved == null || saved.isEmpty) return const [];
  try {
    final root = jsonDecode(saved);
    if (root is! Map || root['v'] != _version || root['entries'] is! List) return const [];

    final entries = <DiagnosticsEntry>[];
    for (final raw in root['entries'] as List) {
      final entry = _decodeEntry(raw);
      if (entry != null) entries.add(entry);
    }
    return entries;
  } on FormatException {
    return const [];
  }
}

DiagnosticsEntry? _decodeEntry(Object? raw) {
  if (raw is! Map) return null;
  final id = raw['id'];
  final title = raw['title'];
  final at = DateTime.tryParse('${raw['at']}');
  final kind = DiagnosticsKind.values.where((k) => k.name == raw['kind']).firstOrNull;
  if (id is! int || title is! String || at == null || kind == null) return null;

  return DiagnosticsEntry(
    id: id,
    title: title,
    kind: kind,
    recordedAt: at.toLocal(),
    summary: raw['summary'] as String?,
    source: raw['source'] as String?,
    lines: [
      for (final line in (raw['lines'] as List<dynamic>? ?? const []))
        if (line is Map && line['t'] is String)
          DiagnosticsLine(
            line['t'] as String,
            target: line['go'] as String?,
            category: diagnosticsCategoryKey(line['c'] as String?),
            quiet: line['q'] == true,
          ),
    ],
  );
}
