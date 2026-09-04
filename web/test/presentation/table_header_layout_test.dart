import 'package:flutter/material.dart';
import 'package:flutter/rendering.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:kintsugi_web/core/theme/app_theme.dart';
import 'package:kintsugi_web/core/widgets/kintsugi_table.dart';

/// [KintsugiTable]'s header row, which is the part of it that cannot be checked by looking at a
/// release build.
///
/// Every one of these is a bug the Applications screen shipped. A `Row` that cannot fit its child
/// does not shrink it, it paints it over whatever is beside it — so "Hosts Installed On" ran
/// across the Platform column. A `Table` centres a cell vertically by default, so the labels of
/// the columns carrying a filter control sat 26px above the labels of the ones that do not. And a
/// `Text` cannot narrow a word, so an "Actions" column sized for its 34px icon rendered the word
/// over its neighbour too. None of the three throws in a release build and none of them is
/// visible in a still of a table that happens to have short labels, which is why they are pinned
/// here at a width narrow enough to force all three.
void main() {
  /// The Applications table's shape: long labels, a mix of columns with and without a filter
  /// control under the label, and an icon column whose header word is wider than the icon.
  List<TableColumnSpec> columnsWith(Widget filter) => [
        TableColumnSpec(
          label: 'Application Name',
          width: const FlexColumnWidth(1.6),
          sortKey: 'name',
          filter: filter,
        ),
        const TableColumnSpec(
          label: 'Hosts Installed On',
          width: FixedColumnWidth(150),
          alignRight: true,
          sortKey: 'hosts',
        ),
        const TableColumnSpec(label: 'Platform', width: FlexColumnWidth(1), sortKey: 'platform'),
        const TableColumnSpec(label: 'Actions', width: FixedColumnWidth(60)),
      ];

  Future<void> pumpTable(
    WidgetTester tester, {
    bool withChildRow = false,
    Widget? expanded,
    double viewport = 900,
    // A declared floor, as every screen using this widget declares one. It matters that it is not
    // zero: the horizontal scroll view hands the table an unbounded width, so a table with no
    // floor at all and nothing to fall back on lays out at zero and every assertion below passes
    // against a collapsed header for the wrong reason.
    double minWidth = 900,
  }) async {
    tester.view.physicalSize = Size(viewport, 800);
    tester.view.devicePixelRatio = 1.0;
    addTearDown(tester.view.reset);

    final columns = columnsWith(
      // Any control taller than the label will do; what matters is that only one column has one.
      const SizedBox(height: 44, child: ColoredBox(color: Color(0xFF000000))),
    );

    await tester.pumpWidget(
      MaterialApp(
        theme: AppTheme.light(),
        home: Scaffold(
          body: SingleChildScrollView(
            child: KintsugiTable(
              minWidth: minWidth,
              columns: columns,
              sort: const TableSort('name'),
              onSort: (_) {},
              rows: [
                KintsugiTableRow(
                  expanded: expanded,
                  cells: const [
                    Text('Microsoft Visual Studio Code'),
                    Text('12'),
                    Text('pm:Homebrew'),
                    // Taller than the rest, so a body cell's vertical alignment is observable.
                    SizedBox(height: 60, child: Icon(Icons.expand_more)),
                  ],
                ),
                if (withChildRow)
                  const KintsugiTableRow(
                    isChild: true,
                    cells: [Text('Google Chrome'), Text('1'), Text('pm:Homebrew'), SizedBox()],
                  ),
              ],
            ),
          ),
        ),
      ),
    );
    await tester.pump();
  }

  RenderParagraph labelOf(WidgetTester tester, String label) =>
      find.text(label.toUpperCase()).evaluate().single.findRenderObject()! as RenderParagraph;

  testWidgets('lays every header label out without overflowing its column', (tester) async {
    await pumpTable(tester);
    expect(tester.takeException(), isNull);
  });

  testWidgets('aligns every header label to the top of the row, filter or not', (tester) async {
    await pumpTable(tester);

    // 'Application Name' is the only column carrying a filter control, so it is what sets the
    // header row's height. Every other label has to start level with it rather than floating to
    // the middle of the space that control makes.
    final tops = {
      for (final label in ['Application Name', 'Hosts Installed On', 'Platform', 'Actions'])
        tester.getRect(find.text(label.toUpperCase())).top,
    };
    expect(tops, hasLength(1));
  });

  testWidgets('wraps a label too wide for its column instead of truncating it', (tester) async {
    await pumpTable(tester);

    final label = labelOf(tester, 'Hosts Installed On');
    // Narrower than the whole phrase, so it did have to wrap...
    expect(label.size.width, lessThan(label.getMaxIntrinsicWidth(double.infinity)));
    // ...onto a second line, rather than being ellipsised onto one. `TextOverflow.ellipsis` with
    // no `maxLines` would do the latter, silently, and read as "Hosts Install…".
    expect(label.size.height, greaterThan(label.getMinIntrinsicHeight(double.infinity)));
  });

  testWidgets('never breaks a header label mid-word, however narrow the column asks to be',
      (tester) async {
    await pumpTable(tester);

    // The 'Actions' column asks for 60 — an icon's worth. The word does not fit that and cannot
    // wrap, so without the floor `KintsugiTable` applies it would render as "ACTION" over "S".
    for (final label in ['Application Name', 'Hosts Installed On', 'Platform', 'Actions']) {
      final paragraph = labelOf(tester, label);
      expect(
        paragraph.size.width,
        greaterThanOrEqualTo(paragraph.getMinIntrinsicWidth(double.infinity) - 0.5),
        reason: '$label was broken mid-word',
      );
    }
  });

  testWidgets('widens the table itself to hold those floors, rather than painting past them',
      (tester) async {
    // A viewport far narrower than the labels need, and a declared floor of nothing at all: the
    // table has to find its own, and grow past its scroll viewport, instead of squeezing the
    // columns below their minimums — a Table narrower than those paints them outside itself.
    await pumpTable(tester, viewport: 320, minWidth: 0);
    expect(tester.takeException(), isNull);

    final table = tester.getRect(find.byType(Table));
    expect(table.width, greaterThan(320));
    for (final label in ['Application Name', 'Hosts Installed On', 'Platform', 'Actions']) {
      final paragraph = labelOf(tester, label);
      expect(
        paragraph.size.width,
        greaterThanOrEqualTo(paragraph.getMinIntrinsicWidth(double.infinity) - 0.5),
        reason: '$label was broken mid-word',
      );
    }
  });

  testWidgets('takes the panel\'s full width when there is more of it than the floor',
      (tester) async {
    // A horizontal [SingleChildScrollView] hands its child an unbounded width, so the table's
    // own width has to be measured outside it. Measured inside, `maxWidth` is infinity and the
    // table lays out at exactly its floor however wide the window is — narrow, with the panel
    // empty beside it.
    await pumpTable(tester, viewport: 1400, minWidth: 900);

    // The panel's own 1px border on each side is all that separates the two.
    expect(tester.getRect(find.byType(Table)).width, closeTo(1400, 2));
  });

  testWidgets('still indents a child row past the gutter', (tester) async {
    // How a package manager's applications are shown under the manager, on the same Applications
    // screen. The indent is measured from the cell gutter rather than stated absolutely, so it
    // survives a change to that gutter — but nothing else says so, and a child row that lines up
    // with its parent stops reading as nested at all.
    await pumpTable(tester, withChildRow: true);

    final parent = tester.getRect(find.text('Microsoft Visual Studio Code')).left;
    final child = tester.getRect(find.text('Google Chrome')).left;
    expect(child - parent, greaterThanOrEqualTo(16));
  });

  testWidgets('leaves body cells vertically centred', (tester) async {
    await pumpTable(tester);

    // The top alignment is applied per header cell rather than as the table's
    // `defaultVerticalAlignment`, precisely so this stays true: the body rows and the expanded
    // instructions panel are laid out against the middle default, and switching the table over
    // would shift every one of them.
    final row = tester.getRect(find.text('pm:Homebrew'));
    final tall = tester.getRect(find.byIcon(Icons.expand_more));
    expect(row.center.dy, closeTo(tall.center.dy, 1));
  });

  testWidgets('gives an expanded panel the full width of the table, not the first column\'s',
      (tester) async {
    // A [Table] has no column-span, and the first cut put the panel in the first cell with the
    // rest empty — so the Applications screen's instructions panel, which needs two 200px
    // columns before it can lay out at all, was handed a ~190px column and threw. A build
    // exception is a plain grey box in a release build, which is exactly what was reported.
    await pumpTable(
      tester,
      viewport: 1400,
      expanded: LayoutBuilder(
        key: const ValueKey('panel'),
        builder: (_, constraints) => SizedBox(
          height: 40,
          child: Text('width ${constraints.maxWidth.round()}'),
        ),
      ),
    );

    expect(tester.takeException(), isNull);
    final panel = tester.getRect(find.byKey(const ValueKey('panel')));
    final table = tester.getRect(find.byType(KintsugiTable));
    // Less the cell gutter on each side and the panel's own 1px border.
    expect(panel.width, closeTo(table.width - 24, 4));
    // And beneath the row it belongs to, not beside it.
    expect(panel.top, greaterThan(tester.getRect(find.text('12')).bottom));
  });

  testWidgets('keeps the rows below an expanded panel aligned with the rows above it',
      (tester) async {
    // The panel is spliced in as a sibling between two [Table]s, so the column boundaries of
    // the second have to land where the first's did or every row under an open panel jogs
    // sideways. They do because every column width resolves against the same total; this is
    // what would catch an [IntrinsicColumnWidth] creeping in.
    await pumpTable(
      tester,
      withChildRow: true,
      expanded: const SizedBox(height: 40, child: Text('panel')),
    );

    expect(find.byType(Table), findsNWidgets(2));
    final above = tester.getRect(find.text('pm:Homebrew').first).left;
    final below = tester.getRect(find.text('pm:Homebrew').last).left;
    expect(below, closeTo(above, 0.01));
    // The indent is the child row's own, so compare the parent's cell edge instead.
    final parentName = tester.getRect(find.text('Microsoft Visual Studio Code')).left;
    final childName = tester.getRect(find.text('Google Chrome')).left;
    expect(childName - parentName, closeTo(22, 0.01));
  });
}
