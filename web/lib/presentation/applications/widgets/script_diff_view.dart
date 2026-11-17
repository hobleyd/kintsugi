import 'package:flutter/material.dart';

import '../../../core/text/invisible_characters.dart';
import '../../../core/text/script_diff.dart';
import '../../../core/theme/app_theme.dart';
import '../../../core/theme/kintsugi_palette.dart';
import '../../../core/widgets/text_bits.dart';

/// A script's changes, line by line, in the way a code review shows them: removed lines in red, added
/// lines in green, a bar beside the ones worth a second look, and each change with its own "mark as
/// reviewed".
///
/// Read-only. Editing happens on the other tab; this is for reading what the editing, or the AI,
/// did to the script that was here when the row was opened.
class ScriptDiffView extends StatefulWidget {
  const ScriptDiffView({
    super.key,
    required this.diff,
    required this.reviewed,
    required this.reviewRequired,
    required this.onToggleReviewed,
  });

  final ScriptDiff diff;

  /// The [ScriptChange.key]s the reviewer has marked as read.
  final Set<String> reviewed;

  /// Whether signing waits for every change to be read. When it does not — the script this is
  /// compared with was never signed — the marks are not offered, because they would gate nothing.
  final bool reviewRequired;

  final ValueChanged<String> onToggleReviewed;

  @override
  State<ScriptDiffView> createState() => _ScriptDiffViewState();
}

class _ScriptDiffViewState extends State<ScriptDiffView> {
  /// Unchanged lines kept either side of a change when the rest is folded away.
  static const _context = 2;

  bool _sideBySide = false;
  bool _fullScript = false;
  final _gapsOpened = <int>{};
  final _scroll = ScrollController();
  final _hunkKeys = <int, GlobalKey>{};
  int _nextChange = -1;

  @override
  void dispose() {
    _scroll.dispose();
    super.dispose();
  }

  void _goToNext() {
    final changes = widget.diff.changes;
    if (changes.isEmpty) return;
    _nextChange = (_nextChange + 1) % changes.length;
    final context = _hunkKeys[_nextChange]?.currentContext;
    if (context != null) {
      Scrollable.ensureVisible(context, alignment: 0.1, duration: const Duration(milliseconds: 200));
    }
  }

  @override
  Widget build(BuildContext context) {
    final palette = context.palette;
    final diff = widget.diff;
    final changes = diff.changes;
    final reviewedCount = changes.where((c) => widget.reviewed.contains(c.key)).length;
    final lookTwice = changes.where((c) => c.flags.isNotEmpty).length;

    return Column(
      crossAxisAlignment: CrossAxisAlignment.stretch,
      children: [
        Wrap(
          spacing: 12,
          runSpacing: 8,
          crossAxisAlignment: WrapCrossAlignment.center,
          children: [
            Text.rich(
              TextSpan(children: [
                TextSpan(text: '+${diff.added}', style: TextStyle(color: palette.green)),
                const TextSpan(text: '  '),
                TextSpan(text: '−${diff.removed}', style: TextStyle(color: palette.red)),
              ]),
              style: AppTheme.mono(color: palette.text, size: 13),
            ),
            HintText(
              '${changes.length} change${changes.length == 1 ? '' : 's'}'
              '${lookTwice == 0 ? '' : ', $lookTwice to look at twice'}',
            ),
            if (widget.reviewRequired && changes.isNotEmpty) _Progress(done: reviewedCount, of: changes.length),
            _Segmented(
              options: const ['Changes only', 'Full script'],
              selected: _fullScript ? 1 : 0,
              onSelect: (i) => setState(() {
                _fullScript = i == 1;
                _gapsOpened.clear();
              }),
            ),
            _Segmented(
              options: const ['Unified', 'Side by side'],
              selected: _sideBySide ? 1 : 0,
              onSelect: (i) => setState(() => _sideBySide = i == 1),
            ),
            if (changes.length > 1) _TextButton(label: 'Next change ↓', onTap: _goToNext),
          ],
        ),
        const SizedBox(height: 10),
        Container(
          // As tall as the changes, up to a point, so a two-line change is not a box of empty space.
          constraints: const BoxConstraints(maxHeight: 340),
          decoration: BoxDecoration(
            color: palette.backgroundAlt,
            border: Border.all(color: palette.border),
            borderRadius: BorderRadius.circular(4),
          ),
          child: SingleChildScrollView(
            controller: _scroll,
            child: Column(crossAxisAlignment: CrossAxisAlignment.stretch, children: _rows(context)),
          ),
        ),
        const SizedBox(height: 8),
        const HintText(
          'An amber bar marks a line worth a second look: a download, a deletion, a permission change, '
          'text run as code, or an address the script did not use before. Characters that would '
          'otherwise be invisible — a carriage return, a zero-width or text-direction character — are '
          r'drawn highlighted where they are, as \r or <U+…>.',
        ),
      ],
    );
  }

  List<Widget> _rows(BuildContext context) {
    final rows = <Widget>[];
    final segments = widget.diff.segments;

    for (var i = 0; i < segments.length; i++) {
      switch (segments[i]) {
        case UnchangedSegment(:final lines):
          rows.addAll(_unchanged(context, i, lines, first: i == 0, last: i == segments.length - 1));
        case ChangedSegment(:final change):
          _hunkKeys[change.index] ??= GlobalKey();
          rows.add(_Hunk(
            key: _hunkKeys[change.index],
            change: change,
            total: widget.diff.changes.length,
            done: widget.reviewed.contains(change.key),
            reviewRequired: widget.reviewRequired,
            onToggle: () => widget.onToggleReviewed(change.key),
          ));
          rows.addAll(_sideBySide ? _split(change) : [for (var n = 0; n < change.lines.length; n++) _unifiedLine(change.lines[n], flagged: change.flaggedLines.contains(n))]);
      }
    }
    return rows;
  }

  List<Widget> _unchanged(BuildContext context, int segment, List<DiffLine> lines, {required bool first, required bool last}) {
    List<Widget> show(Iterable<DiffLine> some) =>
        [for (final line in some) _sideBySide ? _splitRow(line, line) : _unifiedLine(line)];

    if (_fullScript || _gapsOpened.contains(segment)) return show(lines);

    final head = first ? 0 : _context;
    final tail = last ? 0 : _context;
    if (lines.length <= head + tail + 1) return show(lines);

    final hidden = lines.length - head - tail;
    return [
      ...show(lines.take(head)),
      _Gap(hidden: hidden, onTap: () => setState(() => _gapsOpened.add(segment))),
      ...show(lines.skip(lines.length - tail)),
    ];
  }

  Widget _unifiedLine(DiffLine line, {bool flagged = false}) => _Line(line: line, flagged: flagged);

  List<Widget> _split(ScriptChange change) {
    final removed = [for (final l in change.lines) if (l.kind == DiffKind.removed) l];
    final added = [for (final l in change.lines) if (l.kind == DiffKind.added) l];
    final flaggedAdded = {
      for (var n = 0; n < change.lines.length; n++)
        if (change.flaggedLines.contains(n)) change.lines[n],
    };

    return [
      for (var i = 0; i < (removed.length > added.length ? removed.length : added.length); i++)
        _splitRow(
          i < removed.length ? removed[i] : null,
          i < added.length ? added[i] : null,
          flagged: i < added.length && flaggedAdded.contains(added[i]),
        ),
    ];
  }

  Widget _splitRow(DiffLine? left, DiffLine? right, {bool flagged = false}) => Row(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          Expanded(child: _Cell(line: left)),
          Expanded(child: _Cell(line: right, flagged: flagged, divider: true)),
        ],
      );
}

TextStyle _mono(BuildContext context, {Color? color}) =>
    AppTheme.mono(color: color ?? context.palette.text, size: 12.5);

Color? _tint(BuildContext context, DiffKind kind) {
  final palette = context.palette;
  return switch (kind) {
    DiffKind.added => palette.green.withValues(alpha: 0.12),
    DiffKind.removed => palette.red.withValues(alpha: 0.13),
    DiffKind.same => null,
  };
}

/// A line's text with every invisible or text-direction character drawn as a highlighted escape in
/// its place (see `invisible_characters.dart`). Drawing them as-is is what makes Trojan Source work:
/// a right-to-left override reorders the line on screen without reordering what the shell runs, and
/// a zero-width character or a trailing `\r` makes two different lines look the same.
class _LineText extends StatelessWidget {
  const _LineText(this.text);

  final String text;

  @override
  Widget build(BuildContext context) {
    if (text.isEmpty) return Text(' ', style: _mono(context));
    final spans = visibleSpans(text);
    if (spans.length == 1 && !spans.single.escaped) return Text(text, style: _mono(context));

    final palette = context.palette;
    return Text.rich(
      TextSpan(children: [
        for (final span in spans)
          span.escaped
              ? TextSpan(
                  text: span.text,
                  style: TextStyle(color: palette.amber, backgroundColor: palette.amber.withValues(alpha: 0.22)),
                )
              : TextSpan(text: span.text),
      ]),
      style: _mono(context),
    );
  }
}

/// One line in the unified view: both line numbers, a + or −, and the text.
class _Line extends StatelessWidget {
  const _Line({required this.line, required this.flagged});

  final DiffLine line;
  final bool flagged;

  @override
  Widget build(BuildContext context) {
    final palette = context.palette;
    final sign = switch (line.kind) {
      DiffKind.added => '+',
      DiffKind.removed => '−',
      DiffKind.same => ' ',
    };
    final signColor = switch (line.kind) {
      DiffKind.added => palette.green,
      DiffKind.removed => palette.red,
      DiffKind.same => palette.muted,
    };

    return Container(
      decoration: BoxDecoration(
        color: _tint(context, line.kind),
        border: Border(left: BorderSide(color: flagged ? palette.amber : Colors.transparent, width: 3)),
      ),
      child: Row(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          _Number(line.oldNumber),
          _Number(line.newNumber),
          SizedBox(width: 18, child: Text(sign, style: _mono(context, color: signColor))),
          Expanded(child: _LineText(line.text)),
        ],
      ),
    );
  }
}

class _Number extends StatelessWidget {
  const _Number(this.value);

  final int? value;

  @override
  Widget build(BuildContext context) => SizedBox(
        width: 38,
        child: Padding(
          padding: const EdgeInsets.only(right: 6),
          child: Text(
            value?.toString() ?? '',
            textAlign: TextAlign.right,
            style: _mono(context, color: context.palette.muted),
          ),
        ),
      );
}

/// One side of a side-by-side row. A null line is the empty space opposite a line the other script
/// does not have.
class _Cell extends StatelessWidget {
  const _Cell({required this.line, this.flagged = false, this.divider = false});

  final DiffLine? line;
  final bool flagged;
  final bool divider;

  @override
  Widget build(BuildContext context) {
    final palette = context.palette;
    final line = this.line;

    return Container(
      constraints: const BoxConstraints(minHeight: 20),
      decoration: BoxDecoration(
        color: line == null ? palette.text.withValues(alpha: 0.02) : _tint(context, line.kind),
        border: Border(
          left: BorderSide(color: flagged ? palette.amber : (divider ? palette.border : Colors.transparent), width: flagged ? 3 : 1),
        ),
      ),
      child: line == null
          ? const SizedBox.shrink()
          : Row(
              crossAxisAlignment: CrossAxisAlignment.start,
              children: [
                _Number(line.kind == DiffKind.added ? line.newNumber : line.oldNumber),
                Expanded(child: _LineText(line.text)),
              ],
            ),
    );
  }
}

/// The header above one change: its place in the list, its size, why it is flagged, and the button
/// that marks it read.
class _Hunk extends StatelessWidget {
  const _Hunk({
    super.key,
    required this.change,
    required this.total,
    required this.done,
    required this.reviewRequired,
    required this.onToggle,
  });

  final ScriptChange change;
  final int total;
  final bool done;
  final bool reviewRequired;
  final VoidCallback onToggle;

  @override
  Widget build(BuildContext context) {
    final palette = context.palette;
    final tone = done ? palette.green : palette.neonSoft;

    return Container(
      padding: const EdgeInsets.symmetric(horizontal: 12, vertical: 7),
      decoration: BoxDecoration(
        color: (done ? palette.green : palette.neon).withValues(alpha: 0.07),
        border: Border.symmetric(horizontal: BorderSide(color: palette.border)),
      ),
      child: Wrap(
        spacing: 10,
        runSpacing: 6,
        crossAxisAlignment: WrapCrossAlignment.center,
        children: [
          Text(
            'CHANGE ${change.index + 1} OF $total',
            style: AppTheme.display(color: tone, size: 10, letterSpacing: 1),
          ),
          Text.rich(
            TextSpan(children: [
              TextSpan(text: '+${change.added}', style: TextStyle(color: palette.green)),
              const TextSpan(text: ' '),
              TextSpan(text: '−${change.removed}', style: TextStyle(color: palette.red)),
            ]),
            style: AppTheme.mono(color: palette.text, size: 12),
          ),
          for (final flag in change.flags)
            Container(
              padding: const EdgeInsets.symmetric(horizontal: 8, vertical: 2),
              decoration: BoxDecoration(
                color: palette.amber.withValues(alpha: 0.08),
                border: Border.all(color: palette.amber),
                borderRadius: BorderRadius.circular(3),
              ),
              child: Text(
                '⚠ $flag',
                style: AppTheme.display(color: palette.amber, size: 9, letterSpacing: 0.7),
              ),
            ),
          if (reviewRequired)
            _TextButton(
              key: ValueKey('review-${change.index}'),
              label: done ? '✓ Reviewed' : 'Mark reviewed',
              onTap: onToggle,
              highlight: done,
            ),
        ],
      ),
    );
  }
}

/// "⋯ N unchanged lines" in place of a run of them, which opens on a click.
class _Gap extends StatelessWidget {
  const _Gap({required this.hidden, required this.onTap});

  final int hidden;
  final VoidCallback onTap;

  @override
  Widget build(BuildContext context) => InkWell(
        onTap: onTap,
        child: Container(
          alignment: Alignment.center,
          padding: const EdgeInsets.symmetric(vertical: 6),
          color: Colors.black.withValues(alpha: 0.2),
          child: Text(
            '⋯ $hidden unchanged line${hidden == 1 ? '' : 's'} — click to show',
            style: AppTheme.display(color: context.palette.muted, size: 10, letterSpacing: 0.9),
          ),
        ),
      );
}

class _Progress extends StatelessWidget {
  const _Progress({required this.done, required this.of});

  final int done;
  final int of;

  @override
  Widget build(BuildContext context) {
    final palette = context.palette;
    return Row(
      mainAxisSize: MainAxisSize.min,
      children: [
        SizedBox(
          width: 90,
          child: ClipRRect(
            borderRadius: BorderRadius.circular(3),
            child: LinearProgressIndicator(
              value: of == 0 ? 0 : done / of,
              minHeight: 6,
              color: palette.green,
              backgroundColor: palette.neon.withValues(alpha: 0.12),
            ),
          ),
        ),
        const SizedBox(width: 8),
        HintText('$done of $of reviewed'),
      ],
    );
  }
}

/// Two or three options joined into one control, one of which is on.
class _Segmented extends StatelessWidget {
  const _Segmented({required this.options, required this.selected, required this.onSelect});

  final List<String> options;
  final int selected;
  final ValueChanged<int> onSelect;

  @override
  Widget build(BuildContext context) {
    final palette = context.palette;
    return Container(
      decoration: BoxDecoration(
        border: Border.all(color: palette.border),
        borderRadius: BorderRadius.circular(3),
      ),
      child: Row(
        mainAxisSize: MainAxisSize.min,
        children: [
          for (var i = 0; i < options.length; i++)
            InkWell(
              onTap: () => onSelect(i),
              child: Semantics(
                button: true,
                selected: i == selected,
                child: Container(
                  padding: const EdgeInsets.symmetric(horizontal: 10, vertical: 6),
                  color: i == selected ? palette.neon.withValues(alpha: 0.16) : null,
                  child: Text(
                    options[i].toUpperCase(),
                    style: AppTheme.display(
                      color: i == selected ? palette.neon : palette.muted,
                      size: 9.5,
                      letterSpacing: 0.8,
                    ),
                  ),
                ),
              ),
            ),
        ],
      ),
    );
  }
}

class _TextButton extends StatelessWidget {
  const _TextButton({super.key, required this.label, required this.onTap, this.highlight = false});

  final String label;
  final VoidCallback onTap;
  final bool highlight;

  @override
  Widget build(BuildContext context) {
    final palette = context.palette;
    final color = highlight ? palette.green : palette.neonDim;
    return InkWell(
      onTap: onTap,
      borderRadius: BorderRadius.circular(3),
      child: Container(
        padding: const EdgeInsets.symmetric(horizontal: 10, vertical: 5),
        decoration: BoxDecoration(
          border: Border.all(color: highlight ? palette.green : palette.border),
          borderRadius: BorderRadius.circular(3),
        ),
        child: Text(label.toUpperCase(), style: AppTheme.display(color: color, size: 9.5, letterSpacing: 0.8)),
      ),
    );
  }
}
