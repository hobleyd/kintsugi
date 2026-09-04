import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:kintsugi_web/core/theme/app_theme.dart';
import 'package:kintsugi_web/core/widgets/buttons.dart';
import 'package:kintsugi_web/core/widgets/form_bits.dart';
import 'package:kintsugi_web/core/widgets/kintsugi_table.dart';
import 'package:kintsugi_web/core/widgets/status_chip.dart';
import 'package:kintsugi_web/core/widgets/text_bits.dart';

void main() {
  testWidgets('diag', (tester) async {
    tester.view.physicalSize = const Size(1224, 900);
    tester.view.devicePixelRatio = 1.0;
    addTearDown(tester.view.reset);

    final controller = TextEditingController();
    final columns = [
      TableColumnSpec(
        label: 'Application Name',
        width: const FlexColumnWidth(1.6),
        sortKey: 'name',
        filter: KintsugiTextField(controller: controller, hintText: 'Search...'),
      ),
      TableColumnSpec(
        label: 'Hosts Installed On',
        width: const FixedColumnWidth(170),
        alignRight: true,
        sortKey: 'hosts',
        filter: KintsugiDropdown<String>(
          value: 'all',
          items: const ['all', 'htw-m5pro-david'],
          labelOf: (v) => v == 'all' ? 'All hosts' : v,
          onChanged: (_) {},
        ),
      ),
      const TableColumnSpec(label: 'Platform', width: FlexColumnWidth(1), sortKey: 'platform'),
      TableColumnSpec(
        label: 'Status',
        width: const FixedColumnWidth(180),
        sortKey: 'status',
        filter: KintsugiDropdown<String>(
          value: 'all',
          items: const ['all', 'update-available'],
          labelOf: (v) => v == 'all' ? 'All statuses' : 'Update Available',
          onChanged: (_) {},
        ),
      ),
      const TableColumnSpec(label: 'Latest', width: FlexColumnWidth(0.9), sortKey: 'latest'),
      const TableColumnSpec(label: 'Upgrade', width: FlexColumnWidth(1.2)),
      const TableColumnSpec(label: 'Checked', width: FlexColumnWidth(1), sortKey: 'checked'),
      const TableColumnSpec(label: 'Actions', width: FixedColumnWidth(90)),
    ];

    await tester.pumpWidget(MaterialApp(
      theme: AppTheme.light(),
      home: Scaffold(
        body: Padding(
          padding: const EdgeInsets.all(24),
          child: SingleChildScrollView(
            child: KintsugiTable(
              minWidth: 1400,
              columns: columns,
              sort: const TableSort('latest'),
              onSort: (_) {},
              toolbar: const Row(children: [Expanded(child: HintText('Search and filter from the column headers below.'))]),
              rows: [
                const KintsugiTableRow(cells: [
                  Text('Calibre Agent'),
                  CountBadge(1),
                  HintText('macOS'),
                  HintText('Not checked yet'),
                  HintText('—'),
                  HintText('No reliable information found.'),
                  NoValue(),
                  Icon(Icons.expand_more),
                ]),
                KintsugiTableRow(cells: [
                  const Text('Google Chrome'),
                  const CountBadge(12),
                  const HintText('pm:Homebrew'),
                  const StatusChip('Update Available', statusKey: 'update-available'),
                  const HintText('141.0.7390.55'),
                  LinkText(label: 'View script', onTap: () {}),
                  const HintText('3 Sep 2026, 5:12 pm'),
                  const Icon(Icons.expand_more),
                ]),
              ],
            ),
          ),
        ),
      ),
    ));
    await tester.pump();

    for (final label in ['APPLICATION NAME', 'HOSTS INSTALLED ON', 'PLATFORM', 'STATUS', 'LATEST', 'UPGRADE', 'CHECKED', 'ACTIONS']) {
      final f = find.text(label);
      if (f.evaluate().isEmpty) { debugPrint('$label: NOT FOUND'); continue; }
      final r = tester.getRect(f);
      debugPrint('$label: left=${r.left.toStringAsFixed(1)} top=${r.top.toStringAsFixed(1)} w=${r.width.toStringAsFixed(1)} h=${r.height.toStringAsFixed(1)}');
    }
    final tbl = tester.getRect(find.byType(Table));
    debugPrint('TABLE: left=${tbl.left} w=${tbl.width} h=${tbl.height}');
    debugPrint('EXCEPTIONS: ${tester.takeException()}');
  });
}
