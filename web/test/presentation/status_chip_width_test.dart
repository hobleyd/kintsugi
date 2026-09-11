import 'package:flutter/material.dart';
import 'package:flutter/rendering.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:kintsugi_web/core/theme/app_theme.dart';
import 'package:kintsugi_web/core/widgets/kintsugi_table.dart';
import 'package:kintsugi_web/core/widgets/status_chip.dart';

/// [StatusChip.widthFor], which exists so a table column carrying chips is sized by measurement
/// rather than by eye.
///
/// The bug it was written for: the CVE Reporting table's Exploited column was hand-set to 120px,
/// which is 1.7px less than "EXPLOITED" needs in the display face, and a `Text` cannot narrow a
/// word — so the chip rendered "EXPLOITE" above "D". [KintsugiTable] already floors every column
/// at the longest word of its *header* label for exactly this reason, but nothing measures the
/// cells, and a chip is every bit as unbreakable as a header word.
///
/// Both assertions are independent of the font the test runner resolves: `widthFor` and the chip
/// itself measure with the same style, so what is pinned is that they agree, and that the
/// agreement is tight rather than lucky slack.
void main() {
  Future<double> pumpChipIn(WidgetTester tester, double? width) async {
    await tester.pumpWidget(
      MaterialApp(
        theme: AppTheme.light(),
        home: Scaffold(
          body: Align(
            alignment: Alignment.topLeft,
            child: SizedBox(
              width: width,
              child: const StatusChip('Exploited', statusKey: 'exploited'),
            ),
          ),
        ),
      ),
    );
    await tester.pump();
    return StatusChip.widthFor(tester.element(find.byType(StatusChip)), 'Exploited');
  }

  /// How many lines the chip's label rendered on. Counted from the selection boxes' distinct
  /// tops, because [RenderParagraph] does not expose its line count on this Flutter version.
  int linesOf(WidgetTester tester) {
    // Scoped to the chip: the column's own header label uppercases to the same word.
    final paragraph = tester.renderObject<RenderParagraph>(
      find.descendant(of: find.byType(StatusChip), matching: find.text('EXPLOITED')),
    );
    final boxes = paragraph.getBoxesForSelection(
      const TextSelection(baseOffset: 0, extentOffset: 'EXPLOITED'.length),
    );
    return boxes.map((box) => box.top.roundToDouble()).toSet().length;
  }

  testWidgets('reports the width the chip actually lays out at', (tester) async {
    final measured = await pumpChipIn(tester, null);

    // The padding-and-border arithmetic in widthFor is the part that could silently drift from
    // what build draws; this is what stops it.
    expect(tester.getSize(find.byType(StatusChip)).width, closeTo(measured, 0.01));
    expect(linesOf(tester), 1);
  });

  testWidgets('reports enough width to keep the label on one line, and no more', (tester) async {
    final measured = await pumpChipIn(tester, null);

    await pumpChipIn(tester, measured);
    expect(linesOf(tester), 1);

    // Four pixels narrower and the word breaks — twice the margin the real column was short by.
    // Without this the first assertion would pass just as well against a generous over-estimate,
    // which is not what a column should be sized from.
    await pumpChipIn(tester, measured - 4);
    expect(linesOf(tester), 2);
  });

  testWidgets('sizes a table column wide enough for the chip in it', (tester) async {
    tester.view.physicalSize = const Size(900, 600);
    tester.view.devicePixelRatio = 1.0;
    addTearDown(tester.view.reset);

    late double measured;

    await tester.pumpWidget(
      MaterialApp(
        theme: AppTheme.light(),
        home: Scaffold(
          body: Builder(
            builder: (context) {
              measured = StatusChip.widthFor(context, 'Exploited');
              return SingleChildScrollView(
                child: KintsugiTable(
                  minWidth: 600,
                  columns: [
                    TableColumnSpec(
                      label: 'Exploited',
                      width: TableColumnSpec.forContent(measured),
                    ),
                    const TableColumnSpec(label: 'Affects', width: FlexColumnWidth(2)),
                  ],
                  rows: const [
                    KintsugiTableRow(
                      cells: [StatusChip('Exploited', statusKey: 'exploited'), Text('openssl')],
                    ),
                  ],
                ),
              );
            },
          ),
        ),
      ),
    );
    await tester.pump();

    expect(linesOf(tester), 1);
    expect(tester.getSize(find.byType(StatusChip)).width, closeTo(measured, 0.01));
  });
}
