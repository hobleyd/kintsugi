import 'dart:math' as math;

import 'package:flutter/material.dart';

import '../theme/kintsugi_palette.dart';
import 'panel.dart';

/// The horizontal gutter every cell — header and body — is inset by.
///
/// 12 rather than the 18 the stylesheet used, because a column pays it twice and the Applications
/// table has eight of them: those six pixels are 96px of the width budget that decides whether the
/// table fits a 1512-wide display or scrolls sideways inside its panel.
const _cellGutter = 12.0;

/// What a sortable column's header spends on its sort arrow, beside the label.
const _sortIconWidth = 18.0;

/// One column of a [KintsugiTable].
class TableColumnSpec {
  const TableColumnSpec({
    required this.label,
    this.width = const FlexColumnWidth(),
    this.alignRight = false,
    this.sortKey,
    this.filter,
  });

  final String label;
  final TableColumnWidth width;
  final bool alignRight;

  /// Set to make the header label a sort control. The value is passed back to
  /// [KintsugiTable.onSort]; null means the column is not sortable.
  final String? sortKey;

  /// A control rendered under the header label — the search box and the two dropdowns the
  /// Applications table puts in its own header row, rather than in a toolbar above it.
  final Widget? filter;
}

/// Which column a table is sorted by, and which way.
class TableSort {
  const TableSort(this.key, {this.ascending = true});

  final String key;
  final bool ascending;

  TableSort toggled() => TableSort(key, ascending: !ascending);
}

/// The bordered table every list on every screen is drawn as — `.panel.table-panel > table`.
///
/// Built on [Table] rather than [DataTable] because the requirements are the old markup's, not
/// Material's: per-column widths, a right-aligned count column, an expandable full-width panel
/// spliced in beneath a row, and indented child rows. Horizontal overflow scrolls inside the
/// panel, so the page itself never scrolls sideways.
class KintsugiTable extends StatelessWidget {
  const KintsugiTable({
    super.key,
    required this.columns,
    required this.rows,
    this.sort,
    this.onSort,
    this.toolbar,
    this.minWidth = 0,
  });

  final List<TableColumnSpec> columns;
  final List<KintsugiTableRow> rows;
  final TableSort? sort;
  final void Function(String key)? onSort;

  /// A strip above the header — used for "search and filter from the column headers below" and
  /// the Clear Filters button.
  final Widget? toolbar;

  /// A floor below which the table scrolls horizontally rather than compressing further.
  ///
  /// It is a floor and not a width: given more room than this the table takes it, so a column's
  /// flex is a share of whatever the panel is. Set it from what the *cells* need — the widest
  /// status chip, a version string, a timestamp — because below it the only thing between a
  /// reader and the last column is a scrollbar drawn inside the panel.
  final double minWidth;

  @override
  Widget build(BuildContext context) {
    final palette = context.palette;

    // Every column is floored at its own header label, and the table is floored at the sum of
    // those — see [_labelFloor]. Both matter: the per-column floor is what stops a word being
    // broken, and the total is what keeps the table wider than the floors add up to, because a
    // [Table] narrower than its columns' minimums paints them outside itself rather than
    // scrolling.
    final floors = [for (final column in columns) _labelFloor(context, column)];
    final floor = math.max(minWidth, floors.reduce((a, b) => a + b));

    final table = Table(
      columnWidths: {
        for (var i = 0; i < columns.length; i++)
          i: MaxColumnWidth(columns[i].width, FixedColumnWidth(floors[i])),
      },
      defaultVerticalAlignment: TableCellVerticalAlignment.middle,
      children: [
        TableRow(
          decoration: BoxDecoration(
            color: palette.accentWash(0.05),
            border: Border(bottom: BorderSide(color: palette.border)),
          ),
          children: [for (final column in columns) _HeaderCell(column: column, sort: sort, onSort: onSort)],
        ),
        for (final row in rows) ...row.build(context, columns),
      ],
    );

    return KintsugiPanel(
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.stretch,
        children: [
          if (toolbar != null)
            Container(
              padding: const EdgeInsets.symmetric(horizontal: _cellGutter, vertical: 14),
              decoration: BoxDecoration(
                color: palette.accentWash(0.03),
                border: Border(bottom: BorderSide(color: palette.border)),
              ),
              child: toolbar,
            ),
          // The width is measured *outside* the scroll view and handed in, because a horizontal
          // [SingleChildScrollView] gives its child an unbounded width by definition. Measuring
          // it inside — which is what this did — reads `maxWidth` as infinity every time, so the
          // table was laid out at exactly [minWidth] on every display: it scrolled where it
          // should have and stayed narrow where it should have filled the panel, which is the
          // half of the bug that looks like a wide window's worth of empty space beside a table
          // whose last column is off the edge.
          LayoutBuilder(
            builder: (context, constraints) => Scrollbar(
              child: SingleChildScrollView(
                scrollDirection: Axis.horizontal,
                child: SizedBox(
                  width: math.max(floor, constraints.maxWidth),
                  child: table,
                ),
              ),
            ),
          ),
        ],
      ),
    );
  }
}

/// A row, or a row plus an expanded panel spliced in beneath it.
class KintsugiTableRow {
  const KintsugiTableRow({
    required this.cells,
    this.isChild = false,
    this.expanded,
  });

  final List<Widget> cells;

  /// Indents the first cell and prefixes it with a chevron — how a package manager's applications
  /// are shown under the manager.
  final bool isChild;

  /// Content shown across the full width of the table directly under this row.
  final Widget? expanded;

  List<TableRow> build(BuildContext context, List<TableColumnSpec> columns) {
    final palette = context.palette;
    final border = Border(bottom: BorderSide(color: palette.accentWash(0.12)));

    return [
      TableRow(
        decoration: BoxDecoration(border: border),
        children: [
          for (var i = 0; i < columns.length; i++)
            Padding(
              padding: EdgeInsets.only(
                left: i == 0 && isChild ? _cellGutter + 22 : _cellGutter,
                right: _cellGutter,
                top: 14,
                bottom: 14,
              ),
              child: Align(
                alignment: columns[i].alignRight ? Alignment.centerRight : Alignment.centerLeft,
                child: i < cells.length ? cells[i] : const SizedBox.shrink(),
              ),
            ),
        ],
      ),
      if (expanded != null)
        TableRow(
          decoration: BoxDecoration(border: border, color: palette.accentWash(0.03)),
          children: [
            // A Table has no column-span, so the panel goes in the first cell and every other
            // cell is empty. The panel's own width is then the first column's, which is why the
            // expanding column is always the widest one on a table that uses this.
            Padding(
              padding: const EdgeInsets.symmetric(horizontal: _cellGutter, vertical: 18),
              child: expanded!,
            ),
            for (var i = 1; i < columns.length; i++) const SizedBox.shrink(),
          ],
        ),
    ];
  }
}

/// The narrowest a column may be and still render its header label without breaking a word.
///
/// A [Table] hands a column its width and a `Text` cannot make a word narrower, so a column
/// narrower than the longest word in its label force-breaks it: "ACTIONS" over a 90px icon column
/// renders as "ACTION" above "S". The label may still wrap between words — that is what
/// "Hosts Installed On" over a count badge is meant to do — so the floor is the widest *word*,
/// not the whole label.
///
/// It is applied here, once, rather than as a minimum every screen has to remember for every
/// column it declares, because that is precisely the coupling nothing would enforce: the
/// Applications and Hosts tables both shipped an `Actions` column too narrow for the word
/// "Actions". Below the floor the table scrolls sideways inside its panel, which is what this
/// widget already does with a table too wide for its viewport.
double _labelFloor(BuildContext context, TableColumnSpec column) {
  final painter = TextPainter(
    text: TextSpan(
      text: column.label.toUpperCase(),
      style: Theme.of(context).textTheme.labelLarge,
    ),
    textDirection: Directionality.of(context),
  )..layout();
  // minIntrinsicWidth is the width of the longest word — the part that cannot be wrapped away.
  final widestWord = painter.minIntrinsicWidth;
  painter.dispose();

  return widestWord + (column.sortKey != null ? _sortIconWidth : 0) + _cellGutter * 2;
}

class _HeaderCell extends StatelessWidget {
  const _HeaderCell({required this.column, required this.sort, required this.onSort});

  final TableColumnSpec column;
  final TableSort? sort;
  final void Function(String key)? onSort;

  @override
  Widget build(BuildContext context) {
    final palette = context.palette;
    final sortable = column.sortKey != null && onSort != null;
    final active = sort != null && sort!.key == column.sortKey;

    Widget label = Row(
      mainAxisSize: MainAxisSize.min,
      // Top, not centre: the sort arrow belongs beside the label's first line once the label
      // wraps.
      crossAxisAlignment: CrossAxisAlignment.start,
      children: [
        // Flexible, and wrapping. A header label is routinely wider than the column beneath it —
        // "Hosts Installed On" sits over a count badge, "Actions" over a single icon — and a Row
        // that cannot fit its child does not shrink it, it paints it straight over the next
        // column. That is what the Applications header did: the hosts label ran across the
        // platform one. The label is what a reader recognises the column by, so it wraps to a
        // second line rather than being shortened to fit.
        Flexible(
          child: Text(
            column.label.toUpperCase(),
            // The cross-axis alignment below puts the *Row* on the right; this is what puts the
            // wrapped lines inside it there too, so a two-line header still reads as one block
            // over its right-aligned column.
            textAlign: column.alignRight ? TextAlign.right : TextAlign.left,
            // Two lines, then ellipsis. Both halves are load-bearing. Wrapping cannot break a
            // single word, so a one-word label in a column narrower than it would still paint
            // outside its box — and silently, because the Row fits and nothing is reported; the
            // ellipsis makes that read as a truncated label rather than a collision. And the
            // ellipsis needs an explicit maxLines: with none, Skia ellipsizes on the first line
            // instead of wrapping, which turns "Hosts Installed On" into "Hosts Install…" rather
            // than the two lines it should take.
            maxLines: 2,
            overflow: TextOverflow.ellipsis,
            style: Theme.of(context).textTheme.labelLarge,
          ),
        ),
        if (sortable)
          Padding(
            padding: const EdgeInsets.only(left: 4),
            child: Icon(
              active
                  ? (sort!.ascending ? Icons.arrow_drop_up : Icons.arrow_drop_down)
                  : Icons.unfold_more,
              size: 14,
              color: active ? palette.neon : palette.muted.withValues(alpha: 0.5),
            ),
          ),
      ],
    );

    if (sortable) {
      label = MouseRegion(
        cursor: SystemMouseCursors.click,
        // The sort target is the label, not the whole header: these headers also host filter
        // controls, and clicking a dropdown must not reorder the table underneath it.
        child: GestureDetector(onTap: () => onSort!(column.sortKey!), child: label),
      );
    }

    // Top-aligned, and per cell rather than by changing the table's `defaultVerticalAlignment`:
    // only some header cells carry a filter control, so the row's height is set by those and the
    // rest would otherwise float to the middle of it — which is why the Applications header read
    // as three labels at one height and five at another. The body rows and the expanded panel
    // still want the table's middle default, so this must not become a table-wide setting.
    return TableCell(
      verticalAlignment: TableCellVerticalAlignment.top,
      child: Padding(
        padding: const EdgeInsets.symmetric(horizontal: _cellGutter, vertical: 14),
        child: Column(
          crossAxisAlignment: column.alignRight ? CrossAxisAlignment.end : CrossAxisAlignment.start,
          children: [
            label,
            if (column.filter != null) ...[const SizedBox(height: 8), column.filter!],
          ],
        ),
      ),
    );
  }
}
