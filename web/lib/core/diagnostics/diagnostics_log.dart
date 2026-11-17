import 'dart:async';

import 'package:flutter/widgets.dart';

import 'diagnostics_store.dart';

/// What a recorded entry is: the same three tones the screens' own `AlertBox`es use, so an entry
/// reads the same colour in the panel as its summary did on the page that produced it.
enum DiagnosticsKind { success, error, info }

/// One line of a recorded entry's output, and — where the producer could work one out — the place
/// in the app that line is about.
///
/// A run's notes name rows: "Slack (macOS): the script did not report a version." Reading that and
/// then finding Slack among two thousand installed applications is the work the note creates, and
/// [target] is how the panel hands it back. It is a route rather than a callback so the entry stays
/// plain data that outlives the screen that recorded it — the panel navigates, nothing else has to
/// know how.
@immutable
class DiagnosticsLine {
  const DiagnosticsLine(this.text, {this.target, this.category, this.quiet = false});

  final String text;

  /// What the run concluded about this row — a key from `diagnostics_category.dart`, which is what
  /// the Diagnostics page makes its tabs of. Null for a line that is about no particular row, and
  /// for every line a build before the tabs recorded.
  final String? category;

  /// A row with nothing to say for itself: it succeeded, or was skipped, and has no note. The page
  /// lists these; the side panel leaves them out, which is exactly what it showed before rows were
  /// kept — a line per row that has a note — so a run of two hundred resolved rows does not bury the
  /// three that need reading.
  final bool quiet;

  /// A route to go to when this line is tapped, or null for a line that is about nothing in
  /// particular. Null lines are drawn as plain text — a line that looks tappable and does nothing
  /// is worse than one that never offered.
  final String? target;
}

/// One thing a run — or anything else with output worth keeping — had to say.
///
/// [lines] is the part that earns the panel. A summary fits above a table; a line per row that
/// was skipped or failed does not, and that is precisely the output somebody needs to read in
/// full and, usually, to copy somewhere else.
@immutable
class DiagnosticsEntry {
  const DiagnosticsEntry({
    required this.id,
    required this.title,
    required this.kind,
    required this.recordedAt,
    this.summary,
    this.source,
    this.lines = const [],
  });

  /// Monotonic within one page load. The panel keys its widgets on it, and dismissal names it —
  /// two runs of the same thing are otherwise indistinguishable.
  final int id;

  /// What produced this — "Check for Updates". Not a sentence.
  final String title;

  final DiagnosticsKind kind;

  /// Local time, not the server's: this is when *this browser* was told, which is what somebody
  /// reading the panel is trying to line up against what they were doing.
  final DateTime recordedAt;

  /// The one line the screen itself showed, repeated here so an entry stands alone once the
  /// screen that produced it has been navigated away from.
  final String? summary;

  /// The screen the entry came from, shown because the panel outlives that screen. Null when the
  /// title already says it.
  final String? source;

  /// The output proper, one line per item, in the order the producer gave them. A line may carry
  /// a route it is about — see [DiagnosticsLine].
  final List<DiagnosticsLine> lines;
}

/// The application-wide record of error and log output, and whether its panel is showing.
///
/// **App-level rather than screen-level, because reading the output and acting on it happen on
/// different screens.** "Check for Updates" answers with a line per row it could not check; the
/// next thing to do with that is open Upgrade Scripts, or Failed Updates, or one host under
/// Hosts — every one of which, before this existed, destroyed the only copy of what had just been
/// read. So the log lives above the router, the panel lives in the shell beside the sidebar, and
/// navigating is not a way of losing it. Nothing closes it except the person who opened it.
///
/// A [ChangeNotifier] rather than a bloc, for the same reason `UnauthorizedNotifier` and
/// `FullScreenController` are plain classes in `core/`: this is not one screen's state machine,
/// it is a cross-cutting thing many screens write to and exactly one widget draws.
///
/// **Optionally backed by a [DiagnosticsStore]**, which is how the entries outlive a reload and
/// show in a second tab. With none — every widget test, and anything pumped off the shell — it is
/// the in-memory log it always was. A store that fails to save costs persistence and nothing else:
/// the entry is still in memory and still shown.
class DiagnosticsLog extends ChangeNotifier {
  DiagnosticsLog({DiagnosticsStore? store}) : _store = store {
    if (store == null) return;
    _adopt(store.load());
    _changes = store.changedElsewhere.listen((_) => _reload());
  }

  final DiagnosticsStore? _store;
  StreamSubscription<void>? _changes;
  final List<DiagnosticsEntry> _entries = [];
  bool _isOpen = false;
  int _nextId = 0;

  /// Kept entries, oldest dropped first. In memory for as long as the tab lives, so this is a
  /// bound on that rather than on any one run: fifty is already more than anybody scrolls, and
  /// the server bounds the note list inside a single entry separately — see
  /// `UpdateCheckCoordinator`, which caps at fifty and says how many rows it left out.
  static const retained = 50;

  /// Newest first, which is the order the panel draws and the order a reader wants: the run that
  /// just finished is the one they pressed the button for.
  List<DiagnosticsEntry> get entries => List.unmodifiable(_entries.reversed);

  int get count => _entries.length;

  bool get isEmpty => _entries.isEmpty;

  bool get isOpen => _isOpen;

  /// Appends an entry, and shows the panel unless the entry is a plain success with nothing to
  /// read — no line that is not [DiagnosticsLine.quiet]. That exception is the whole of the auto-open policy and is worth stating: a run that
  /// worked and had no notes has already said so on the page, and sliding a panel over the table
  /// to repeat it would make the feature something to be endured.
  void record({
    required String title,
    required DiagnosticsKind kind,
    String? summary,
    String? source,
    List<DiagnosticsLine> lines = const [],
  }) {
    _entries.add(DiagnosticsEntry(
      id: _nextId++,
      title: title,
      kind: kind,
      recordedAt: DateTime.now(),
      summary: summary,
      source: source,
      lines: List.unmodifiable(lines),
    ));
    if (_entries.length > retained) _entries.removeRange(0, _entries.length - retained);

    if (kind != DiagnosticsKind.success || lines.any((line) => !line.quiet)) _isOpen = true;
    _persist();
    notifyListeners();
  }

  /// Replaces what is held with [entries] and carries on numbering after the highest of them, so an
  /// entry recorded next never reuses an id a saved one already has.
  void _adopt(List<DiagnosticsEntry> entries) {
    _entries
      ..clear()
      ..addAll(entries.length > retained ? entries.sublist(entries.length - retained) : entries);
    for (final entry in _entries) {
      if (entry.id >= _nextId) _nextId = entry.id + 1;
    }
  }

  /// Another tab saved: take what it saved. Closes the panel only when that left nothing to show,
  /// for the reason [dismiss] does.
  Future<void> _reload() async {
    final store = _store;
    if (store == null) return;
    try {
      _adopt(await store.reload());
    } on Object {
      return;
    }
    if (_entries.isEmpty) _isOpen = false;
    notifyListeners();
  }

  void _persist() {
    final store = _store;
    if (store == null) return;
    // Not awaited, and failures dropped: the entry is already in memory and on screen, and a full
    // or disabled `localStorage` — a private window, a quota — must not turn into an error on the
    // run that just finished.
    unawaited(store.save(_entries.toList()).catchError((Object _) {}));
  }

  @override
  void dispose() {
    _changes?.cancel();
    super.dispose();
  }

  void open() {
    if (_isOpen) return;
    _isOpen = true;
    notifyListeners();
  }

  void close() {
    if (!_isOpen) return;
    _isOpen = false;
    notifyListeners();
  }

  void toggle() => _isOpen ? close() : open();

  /// Drops one entry. Closing the panel on the last one is deliberate: an open panel saying
  /// nothing is a strip of empty page taken from the screen beside it.
  void dismiss(int id) {
    _entries.removeWhere((entry) => entry.id == id);
    if (_entries.isEmpty) _isOpen = false;
    _persist();
    notifyListeners();
  }

  void clear() {
    if (_entries.isEmpty && !_isOpen) return;
    _entries.clear();
    _isOpen = false;
    _persist();
    notifyListeners();
  }
}

/// Hands the [DiagnosticsLog] to the subtree the shell wraps, so a screen can record without
/// being handed one through every widget between.
///
/// **[maybeOf] returns null rather than throwing, and that is the point of it.** A widget test
/// pumps one screen with no shell around it — that is how nearly every test in `test/presentation`
/// is written — and a screen that demanded this would make every one of them register something
/// first. Null means "nobody is collecting"; the screen's own inline alert is unaffected either
/// way, because the panel adds to what a screen says and never replaces it.
class DiagnosticsLogScope extends InheritedWidget {
  const DiagnosticsLogScope({super.key, required this.log, required super.child});

  final DiagnosticsLog log;

  /// Deliberately does not register a dependency: a caller of this is a *writer*, and rebuilding
  /// every screen that can record whenever anything is recorded would be a rebuild of the whole
  /// page for a panel that draws itself. The shell listens to the notifier directly instead.
  static DiagnosticsLog? maybeOf(BuildContext context) =>
      context.getInheritedWidgetOfExactType<DiagnosticsLogScope>()?.log;

  @override
  bool updateShouldNotify(DiagnosticsLogScope oldWidget) => oldWidget.log != log;
}
