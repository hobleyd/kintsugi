import 'dart:async';

import 'package:flutter/gestures.dart';
import 'package:flutter/material.dart';
import 'package:flutter/services.dart';
import 'package:go_router/go_router.dart';
import 'package:intl/intl.dart';

import '../../core/di/locator.dart';
import '../../core/diagnostics/diagnostics_category.dart';
import '../../core/diagnostics/diagnostics_export.dart';
import '../../core/diagnostics/diagnostics_log.dart';
import '../../core/platform/file_downloader.dart';
import '../../core/platform/page_navigator.dart';
import '../../core/router/app_router.dart';
import '../../core/theme/app_theme.dart';
import '../../core/theme/kintsugi_palette.dart';
import '../../core/widgets/alert_box.dart';
import '../../core/widgets/buttons.dart';
import '../../core/widgets/form_bits.dart';
import '../../core/widgets/page_scaffold.dart';
import '../../core/widgets/panel.dart';
import '../../core/widgets/status_chip.dart';
import '../../core/widgets/text_bits.dart';

/// Everything that has run, on a page of its own: the shell's Diagnostics panel with room to read,
/// search, copy and export it.
///
/// The panel stays what it is — a strip beside the page a run's notes are about. This is for the
/// other job, working through a long list, and for the second tab: it reads the same log, which is
/// saved in the browser, so it can sit beside the app and stays current as the first tab records.
class DiagnosticsScreen extends StatefulWidget {
  const DiagnosticsScreen({super.key});

  @override
  State<DiagnosticsScreen> createState() => _DiagnosticsScreenState();
}

/// Which runs the list shows. An entry has one kind, so this filters the list of runs; filtering
/// *within* a run by what each row's outcome was needs the server to say which is which — it
/// reports counts, and a note only for the rows that have one.
enum _KindFilter {
  all('All'),
  needsAttention('Needs attention'),
  ok('OK'),
  info('Info');

  const _KindFilter(this.label);

  final String label;

  bool admits(DiagnosticsEntry entry) => switch (this) {
        all => true,
        needsAttention => entry.kind == DiagnosticsKind.error,
        ok => entry.kind == DiagnosticsKind.success,
        info => entry.kind == DiagnosticsKind.info,
      };
}

class _DiagnosticsScreenState extends State<DiagnosticsScreen> {
  final _search = TextEditingController();
  _KindFilter _filter = _KindFilter.all;
  int? _selectedId;

  /// The result tab open on the selected run — a key from `diagnostics_category.dart` — or null for
  /// All. A run that has no such tab shows All, so a stale choice cannot leave the page empty.
  String? _tab;
  String? _notice;
  Timer? _noticeTimer;

  static final _stamp = DateFormat('yyyy-MM-dd HH:mm:ss');

  @override
  void dispose() {
    _search.dispose();
    _noticeTimer?.cancel();
    super.dispose();
  }

  String get _query => _search.text.trim().toLowerCase();

  bool _matches(DiagnosticsEntry entry) {
    final query = _query;
    if (query.isEmpty) return true;
    return entry.title.toLowerCase().contains(query) ||
        (entry.source?.toLowerCase().contains(query) ?? false) ||
        (entry.summary?.toLowerCase().contains(query) ?? false) ||
        entry.lines.any((line) => line.text.toLowerCase().contains(query));
  }

  /// A run is listed when it passes the kind filter and the search matches it anywhere.
  List<DiagnosticsEntry> _visible(DiagnosticsLog log) =>
      log.entries.where((entry) => _filter.admits(entry) && _matches(entry)).toList();

  void _say(String message) {
    _noticeTimer?.cancel();
    setState(() => _notice = message);
    _noticeTimer = Timer(const Duration(seconds: 2), () {
      if (mounted) setState(() => _notice = null);
    });
  }

  Future<void> _copy(String text, String done) async {
    await Clipboard.setData(ClipboardData(text: text));
    if (mounted) _say(done);
  }

  void _export(String name, String mime, String content) {
    locator<FileDownloader>().save(name, mime, content);
    _say('Exported $name.');
  }

  Future<void> _confirmClear(DiagnosticsLog log) async {
    final confirmed = await showDialog<bool>(
      context: context,
      builder: (dialogContext) => AlertDialog(
        backgroundColor: dialogContext.palette.backgroundAlt,
        shape: RoundedRectangleBorder(
          borderRadius: BorderRadius.circular(6),
          side: BorderSide(color: dialogContext.palette.border),
        ),
        title: Text('Clear every saved run?', style: Theme.of(dialogContext).textTheme.titleLarge),
        content: HintText(
          'This removes all ${log.count} runs from this browser, including in other tabs. It cannot '
          'be undone.',
        ),
        actions: [
          SecondaryButton(label: 'Cancel', onPressed: () => Navigator.of(dialogContext).pop(false)),
          PrimaryButton(label: 'Clear', onPressed: () => Navigator.of(dialogContext).pop(true)),
        ],
      ),
    );
    if (confirmed == true && mounted) {
      log.clear();
      setState(() => _selectedId = null);
    }
  }

  @override
  Widget build(BuildContext context) {
    final log = DiagnosticsLogScope.maybeOf(context);
    if (log == null) {
      return const PageScaffold(
        title: 'Diagnostics',
        children: [AlertBox.error('Diagnostics are not available here.')],
      );
    }

    return AnimatedBuilder(
      animation: log,
      builder: (context, _) {
        final visible = _visible(log);
        final selected = visible.where((e) => e.id == _selectedId).firstOrNull ?? visible.firstOrNull;

        return PageScaffold(
          title: 'Diagnostics',
          subtitle: 'Everything that has run, kept in this browser across reloads. Open it in its '
              'own tab to read it beside the page it is about.',
          children: [
            if (_notice != null) AlertBox.success(_notice!),
            _toolbar(log, visible, selected),
            const SizedBox(height: 16),
            if (log.isEmpty)
              const EmptyPanel(
                'Nothing has been recorded yet. The output of a run — what it skipped, what it '
                'could not check — appears here when one finishes.',
              )
            else
              LayoutBuilder(
                builder: (context, constraints) {
                  final runs = _RunList(
                    entries: visible,
                    selected: selected?.id,
                    filter: _filter,
                    onFilter: (value) => setState(() => _filter = value),
                    onSelect: (id) => setState(() {
                      _selectedId = id;
                      _tab = null;
                    }),
                    total: log.count,
                  );
                  final detail = selected == null
                      ? const EmptyPanel('No run matches.')
                      : _RunDetail(
                          entry: selected,
                          query: _query,
                          tab: _tab,
                          onTab: (value) => setState(() => _tab = value),
                          stamp: _stamp,
                          onCopyRun: () => _copy(diagnosticsToText([selected]), 'Copied this run.'),
                          onCopyLine: (text) => _copy(text, 'Copied one line.'),
                        );

                  if (constraints.maxWidth < 900) {
                    return Column(
                      crossAxisAlignment: CrossAxisAlignment.stretch,
                      children: [runs, const SizedBox(height: 16), detail],
                    );
                  }
                  return Row(
                    crossAxisAlignment: CrossAxisAlignment.start,
                    children: [
                      SizedBox(width: 300, child: runs),
                      const SizedBox(width: 18),
                      Expanded(child: detail),
                    ],
                  );
                },
              ),
          ],
        );
      },
    );
  }

  Widget _toolbar(DiagnosticsLog log, List<DiagnosticsEntry> visible, DiagnosticsEntry? selected) {
    final empty = log.isEmpty;
    return Wrap(
      spacing: 10,
      runSpacing: 10,
      crossAxisAlignment: WrapCrossAlignment.center,
      children: [
        SizedBox(
          width: 360,
          child: KintsugiTextField(
            controller: _search,
            hintText: 'Search every run: application, platform, message…',
            onChanged: (_) => setState(() {}),
          ),
        ),
        SecondaryButton(
          label: 'Copy all',
          onPressed: empty ? null : () => _copy(diagnosticsToText(visible), 'Copied ${visible.length} runs.'),
        ),
        _ExportMenu(
          enabled: !empty,
          hasSelection: selected != null,
          onChoose: (choice) {
            final runs = choice.all ? log.entries.reversed.toList() : [selected!];
            _export(
              choice.all ? 'diagnostics-all.${choice.ext}' : 'diagnostics-run.${choice.ext}',
              choice.ext == 'csv' ? 'text/csv' : 'application/json',
              choice.ext == 'csv' ? diagnosticsToCsv(runs) : diagnosticsToJson(runs),
            );
          },
        ),
        SecondaryButton(
          label: 'Open in new tab',
          tooltip: 'Opens this page in its own tab, which shows the same saved runs.',
          onPressed: () => locator<PageNavigator>().openAppRouteInNewTab(Routes.diagnostics),
        ),
        SecondaryButton(label: 'Clear…', onPressed: empty ? null : () => _confirmClear(log)),
      ],
    );
  }
}

class _ExportChoice {
  const _ExportChoice(this.ext, {required this.all});

  final String ext;
  final bool all;
}

/// "Export ▾": this run or every run, as CSV or JSON.
class _ExportMenu extends StatelessWidget {
  const _ExportMenu({required this.enabled, required this.hasSelection, required this.onChoose});

  final bool enabled;
  final bool hasSelection;
  final ValueChanged<_ExportChoice> onChoose;

  @override
  Widget build(BuildContext context) {
    final palette = context.palette;
    final color = enabled ? palette.neonDim : palette.muted;

    return PopupMenuButton<_ExportChoice>(
      enabled: enabled,
      tooltip: 'Export',
      color: palette.backgroundAlt,
      shape: RoundedRectangleBorder(
        borderRadius: BorderRadius.circular(4),
        side: BorderSide(color: palette.border),
      ),
      onSelected: onChoose,
      itemBuilder: (_) => [
        for (final (label, choice) in [
          ('This run as CSV', const _ExportChoice('csv', all: false)),
          ('This run as JSON', const _ExportChoice('json', all: false)),
          ('All runs as CSV', const _ExportChoice('csv', all: true)),
          ('All runs as JSON', const _ExportChoice('json', all: true)),
        ])
          PopupMenuItem(
            value: choice,
            enabled: !(hasSelection == false && !choice.all),
            child: Text(label, style: Theme.of(context).textTheme.bodyMedium),
          ),
      ],
      child: Container(
        padding: const EdgeInsets.symmetric(horizontal: 14, vertical: 14),
        decoration: BoxDecoration(
          border: Border.all(color: palette.border),
          borderRadius: BorderRadius.circular(3),
        ),
        child: Row(
          mainAxisSize: MainAxisSize.min,
          children: [
            Text('EXPORT', style: AppTheme.display(color: color, size: 10.9, letterSpacing: 0.87)),
            const SizedBox(width: 6),
            Icon(Icons.arrow_drop_down, size: 16, color: color),
          ],
        ),
      ),
    );
  }
}

/// The runs, newest first, each labelled with when it happened.
class _RunList extends StatelessWidget {
  const _RunList({
    required this.entries,
    required this.selected,
    required this.filter,
    required this.onFilter,
    required this.onSelect,
    required this.total,
  });

  final List<DiagnosticsEntry> entries;
  final int? selected;
  final _KindFilter filter;
  final ValueChanged<_KindFilter> onFilter;
  final ValueChanged<int> onSelect;
  final int total;

  static final _when = DateFormat('d MMM yyyy, HH:mm:ss');

  @override
  Widget build(BuildContext context) {
    final palette = context.palette;

    return KintsugiPanel(
      padding: const EdgeInsets.all(12),
      clip: false,
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.stretch,
        children: [
          Padding(
            padding: const EdgeInsets.fromLTRB(4, 2, 4, 10),
            child: Text('RUNS', style: Theme.of(context).textTheme.labelLarge),
          ),
          Wrap(
            spacing: 6,
            runSpacing: 6,
            children: [
              for (final option in _KindFilter.values)
                _FilterChip(label: option.label, on: option == filter, onTap: () => onFilter(option)),
            ],
          ),
          const SizedBox(height: 10),
          if (entries.isEmpty)
            const Padding(padding: EdgeInsets.all(8), child: HintText('No run matches.'))
          else
            for (final entry in entries)
              _RunTile(
                key: ValueKey('run-${entry.id}'),
                entry: entry,
                when: _when.format(entry.recordedAt),
                on: entry.id == selected,
                onTap: () => onSelect(entry.id),
              ),
          Padding(
            padding: const EdgeInsets.fromLTRB(4, 8, 4, 2),
            child: Text(
              'Kept in this browser: the last ${DiagnosticsLog.retained} runs ($total saved).',
              style: Theme.of(context).textTheme.bodySmall?.copyWith(color: palette.muted),
            ),
          ),
        ],
      ),
    );
  }
}

class _FilterChip extends StatelessWidget {
  const _FilterChip({required this.label, required this.on, required this.onTap});

  final String label;
  final bool on;
  final VoidCallback onTap;

  @override
  Widget build(BuildContext context) {
    final palette = context.palette;
    return InkWell(
      onTap: onTap,
      borderRadius: BorderRadius.circular(3),
      child: Semantics(
        button: true,
        selected: on,
        child: Container(
          padding: const EdgeInsets.symmetric(horizontal: 10, vertical: 6),
          decoration: BoxDecoration(
            color: on ? palette.neon.withValues(alpha: 0.12) : null,
            border: Border.all(color: on ? palette.neon : palette.border),
            borderRadius: BorderRadius.circular(3),
          ),
          child: Text(
            label.toUpperCase(),
            style: AppTheme.display(color: on ? palette.neon : palette.muted, size: 9.5, letterSpacing: 0.8),
          ),
        ),
      ),
    );
  }
}

/// One run in the list. Lights up under the pointer like the sidebar's links, and stays lit while
/// it is the one being read.
class _RunTile extends StatefulWidget {
  const _RunTile({super.key, required this.entry, required this.when, required this.on, required this.onTap});

  final DiagnosticsEntry entry;
  final String when;
  final bool on;
  final VoidCallback onTap;

  @override
  State<_RunTile> createState() => _RunTileState();
}

class _RunTileState extends State<_RunTile> {
  bool _hovering = false;

  @override
  Widget build(BuildContext context) {
    final palette = context.palette;
    final lit = _hovering || widget.on;
    final entry = widget.entry;
    final tone = switch (entry.kind) {
      DiagnosticsKind.success => palette.green,
      DiagnosticsKind.error => palette.red,
      DiagnosticsKind.info => palette.neonSoft,
    };

    return MouseRegion(
      onEnter: (_) => setState(() => _hovering = true),
      onExit: (_) => setState(() => _hovering = false),
      child: GestureDetector(
        behavior: HitTestBehavior.opaque,
        onTap: widget.onTap,
        child: Semantics(
          button: true,
          selected: widget.on,
          label: '${entry.title}, ${widget.when}',
          child: AnimatedContainer(
            duration: const Duration(milliseconds: 120),
            margin: const EdgeInsets.only(bottom: 6),
            padding: const EdgeInsets.symmetric(horizontal: 12, vertical: 10),
            decoration: BoxDecoration(
              border: Border.all(color: lit ? palette.neon : Colors.transparent),
              borderRadius: BorderRadius.circular(4),
              boxShadow: lit && palette.glowsEnabled
                  ? [BoxShadow(color: palette.neon.withValues(alpha: 0.35), blurRadius: 12)]
                  : const [],
            ),
            child: Column(
              crossAxisAlignment: CrossAxisAlignment.start,
              children: [
                Row(
                  children: [
                    Container(
                      width: 8,
                      height: 8,
                      margin: const EdgeInsets.only(right: 8),
                      decoration: BoxDecoration(color: tone, shape: BoxShape.circle),
                    ),
                    Expanded(
                      child: Text(
                        entry.title,
                        style: Theme.of(context).textTheme.bodyMedium?.copyWith(
                              color: widget.on ? palette.neon : palette.text,
                              fontWeight: FontWeight.w600,
                            ),
                      ),
                    ),
                  ],
                ),
                Padding(
                  padding: const EdgeInsets.only(left: 16),
                  child: Text(widget.when, style: Theme.of(context).textTheme.bodySmall),
                ),
              ],
            ),
          ),
        ),
      ),
    );
  }
}

/// One run: its summary, and every line it produced — the ones the search matches, when there is a
/// search.
class _RunDetail extends StatelessWidget {
  const _RunDetail({
    required this.entry,
    required this.query,
    required this.tab,
    required this.onTab,
    required this.stamp,
    required this.onCopyRun,
    required this.onCopyLine,
  });

  final DiagnosticsEntry entry;
  final String query;
  final String? tab;
  final ValueChanged<String?> onTab;
  final DateFormat stamp;
  final VoidCallback onCopyRun;
  final ValueChanged<String> onCopyLine;

  @override
  Widget build(BuildContext context) {
    final palette = context.palette;
    final tone = switch (entry.kind) {
      DiagnosticsKind.success => palette.green,
      DiagnosticsKind.error => palette.red,
      DiagnosticsKind.info => palette.neonSoft,
    };

    // A search that matched the run's own title or summary shows the whole run; one that matched
    // only some lines shows those lines, which is what somebody searching for one application in
    // two hundred notes is after.
    final headerMatches = query.isEmpty ||
        entry.title.toLowerCase().contains(query) ||
        (entry.source?.toLowerCase().contains(query) ?? false) ||
        (entry.summary?.toLowerCase().contains(query) ?? false);
    final searched = headerMatches
        ? entry.lines
        : entry.lines.where((line) => line.text.toLowerCase().contains(query)).toList();

    // The tabs are the outcomes the rows of this run carry, in a fixed order. Their counts are of
    // the lines the search leaves, so a search says which tab its matches are in.
    final categories = [
      for (final key in diagnosticsCategoryOrder)
        if (entry.lines.any((line) => line.category == key)) key,
    ];
    final openTab = categories.contains(tab) ? tab : null;
    final lines = openTab == null ? searched : searched.where((line) => line.category == openTab).toList();

    return KintsugiPanel(
      padding: const EdgeInsets.all(20),
      clip: false,
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.stretch,
        children: [
          Row(
            crossAxisAlignment: CrossAxisAlignment.start,
            children: [
              Expanded(
                child: Column(
                  crossAxisAlignment: CrossAxisAlignment.start,
                  children: [
                    Text(entry.title.toUpperCase(), style: Theme.of(context).textTheme.titleLarge),
                    const SizedBox(height: 4),
                    Text(
                      '${entry.source == null ? '' : '${entry.source} · '}${stamp.format(entry.recordedAt)}',
                      style: Theme.of(context).textTheme.bodySmall,
                    ),
                  ],
                ),
              ),
              const SizedBox(width: 12),
              StatusChip(
                switch (entry.kind) {
                  DiagnosticsKind.success => 'OK',
                  DiagnosticsKind.error => 'Needs attention',
                  DiagnosticsKind.info => 'Info',
                },
                statusKey: switch (entry.kind) {
                  DiagnosticsKind.success => 'online',
                  DiagnosticsKind.error => 'offline',
                  DiagnosticsKind.info => '_accent',
                },
              ),
              const SizedBox(width: 12),
              SecondaryButton(label: 'Copy this run', onPressed: onCopyRun),
            ],
          ),
          if (entry.summary case final summary?) ...[
            const SizedBox(height: 12),
            Text(summary, style: Theme.of(context).textTheme.bodyMedium?.copyWith(color: tone)),
          ],
          if (categories.isNotEmpty) ...[
            const SizedBox(height: 16),
            Wrap(
              spacing: 6,
              runSpacing: 6,
              children: [
                _ResultTab(key: const ValueKey('tab-all'), label: 'All', count: searched.length, on: openTab == null, onTap: () => onTab(null)),
                for (final key in categories)
                  _ResultTab(
                    key: ValueKey('tab-$key'),
                    label: diagnosticsCategoryLabel(key),
                    count: searched.where((line) => line.category == key).length,
                    on: openTab == key,
                    alert: key == 'failed',
                    onTap: () => onTab(key),
                  ),
              ],
            ),
          ],
          const SizedBox(height: 16),
          if (entry.lines.isEmpty)
            const HintText('This run reported one summary and no list of rows.')
          else if (lines.isEmpty)
            const HintText('No line in this run matches the search.')
          else
            for (var i = 0; i < lines.length; i++)
              _LineRow(
                key: ValueKey('line-${entry.id}-$i'),
                line: lines[i],
                query: query,
                onCopy: () => onCopyLine(lines[i].text),
              ),
        ],
      ),
    );
  }
}

/// One result tab of a run: its name and how many rows are in it. A failure count is drawn in the
/// alert colour, because it is the number somebody opened the page to find.
class _ResultTab extends StatelessWidget {
  const _ResultTab({
    super.key,
    required this.label,
    required this.count,
    required this.on,
    required this.onTap,
    this.alert = false,
  });

  final String label;
  final int count;
  final bool on;
  final bool alert;
  final VoidCallback onTap;

  @override
  Widget build(BuildContext context) {
    final palette = context.palette;
    final countColor = alert && count > 0 ? palette.red : (on ? palette.neonSoft : palette.muted);

    return InkWell(
      onTap: onTap,
      borderRadius: BorderRadius.circular(3),
      child: Semantics(
        button: true,
        selected: on,
        label: '$label, $count',
        child: Container(
          padding: const EdgeInsets.symmetric(horizontal: 12, vertical: 8),
          decoration: BoxDecoration(
            color: on ? palette.neon.withValues(alpha: 0.12) : null,
            border: Border.all(color: on ? palette.neon : palette.border),
            borderRadius: BorderRadius.circular(3),
          ),
          child: Row(
            mainAxisSize: MainAxisSize.min,
            children: [
              Text(
                label.toUpperCase(),
                style: AppTheme.display(color: on ? palette.neon : palette.muted, size: 10, letterSpacing: 0.9),
              ),
              const SizedBox(width: 8),
              Text(
                '$count',
                style: AppTheme.display(color: countColor, size: 10, letterSpacing: 0.5),
              ),
            ],
          ),
        ),
      ),
    );
  }
}

/// One line of a run: tappable when it names a row, with a copy button that appears on hover, and
/// the search's matches marked.
class _LineRow extends StatefulWidget {
  const _LineRow({super.key, required this.line, required this.query, required this.onCopy});

  final DiagnosticsLine line;
  final String query;
  final VoidCallback onCopy;

  @override
  State<_LineRow> createState() => _LineRowState();
}

class _LineRowState extends State<_LineRow> {
  bool _hovering = false;

  /// Held by the state, not made per build: a recognizer that is not disposed leaks, and one made
  /// in `build` would be thrown away on every hover.
  TapGestureRecognizer? _tap;

  /// `Slack (macOS): the script did not report a version.` — the part before the colon is the
  /// application and its platform, and it is all of the line that is about a row. The note after it
  /// is prose. Only the first is the link, so a line reads as text with one place to click rather than
  /// as a wall of underline. A line with a route but not that shape is a link as a whole, which is
  /// what it was.
  static final _subject = RegExp(r'^(.+ \([^()]*\))(?=: |$)');

  @override
  void dispose() {
    _tap?.dispose();
    super.dispose();
  }

  @override
  Widget build(BuildContext context) {
    final palette = context.palette;
    final target = widget.line.target;
    // Null off the app, the way the panel's lines treat it: a link that cannot navigate is not
    // drawn as one.
    final router = GoRouter.maybeOf(context);
    final linked = target != null && router != null;

    if (linked) {
      // One for the life of the row, its target updated each build: replacing it would dispose a
      // recognizer the paragraph painted last frame still points at.
      _tap ??= TapGestureRecognizer();
      _tap!.onTap = () => router.go(target);
    }

    final text = Text.rich(
      TextSpan(children: _spans(palette, linked: linked)),
      style: AppTheme.mono(color: palette.text, size: 12.5),
    );

    return MouseRegion(
      onEnter: (_) => setState(() => _hovering = true),
      onExit: (_) => setState(() => _hovering = false),
      child: Container(
        padding: const EdgeInsets.symmetric(horizontal: 10, vertical: 6),
        decoration: BoxDecoration(
          color: _hovering ? palette.neon.withValues(alpha: 0.05) : null,
          border: Border.all(color: _hovering ? palette.border : Colors.transparent),
          borderRadius: BorderRadius.circular(3),
        ),
        child: Row(
          crossAxisAlignment: CrossAxisAlignment.start,
          children: [
            Expanded(child: text),
            Opacity(
              opacity: _hovering ? 1 : 0,
              child: IconButton(
                icon: const Icon(Icons.copy_outlined, size: 14),
                color: palette.muted,
                visualDensity: VisualDensity.compact,
                constraints: const BoxConstraints.tightFor(width: 28, height: 28),
                padding: EdgeInsets.zero,
                tooltip: 'Copy this line',
                onPressed: widget.onCopy,
              ),
            ),
          ],
        ),
      ),
    );
  }

  List<InlineSpan> _spans(KintsugiPalette palette, {required bool linked}) {
    final text = widget.line.text;
    final subjectEnd = linked ? (_subject.firstMatch(text)?.group(1)?.length ?? text.length) : 0;

    return [
      ..._marked(text.substring(0, subjectEnd), palette, link: true),
      ..._marked(text.substring(subjectEnd), palette, link: false),
    ];
  }

  /// [text] with the search's matches marked, drawn as the link when [link] is set.
  List<InlineSpan> _marked(String text, KintsugiPalette palette, {required bool link}) {
    if (text.isEmpty) return const [];

    TextStyle? style(TextStyle? marks) => link
        ? TextStyle(
            decoration: TextDecoration.underline,
            decorationColor: palette.neonDim,
            color: _hovering ? palette.neon : null,
          ).merge(marks)
        : marks;
    TextSpan span(String piece, {TextStyle? marks}) => TextSpan(
          text: piece,
          style: style(marks),
          recognizer: link ? _tap : null,
          mouseCursor: link ? SystemMouseCursors.click : null,
        );

    final query = widget.query;
    if (query.isEmpty) return [span(text)];

    final lower = text.toLowerCase();
    final spans = <InlineSpan>[];
    var from = 0;
    while (true) {
      final at = lower.indexOf(query, from);
      if (at < 0) break;
      if (at > from) spans.add(span(text.substring(from, at)));
      spans.add(span(
        text.substring(at, at + query.length),
        marks: TextStyle(backgroundColor: palette.amber.withValues(alpha: 0.35)),
      ));
      from = at + query.length;
    }
    if (from < text.length) spans.add(span(text.substring(from)));
    return spans;
  }
}
