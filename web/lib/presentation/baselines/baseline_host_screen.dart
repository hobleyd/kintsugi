import 'dart:math' as math;

import 'package:flutter/material.dart';
import 'package:flutter_bloc/flutter_bloc.dart';

import '../../core/di/locator.dart';
import '../../core/widgets/alert_box.dart';
import '../../core/widgets/kintsugi_table.dart';
import '../../core/widgets/page_scaffold.dart';
import '../../core/widgets/panel.dart';
import '../../core/widgets/status_chip.dart';
import '../../core/widgets/text_bits.dart';
import '../../domain/entities/baseline.dart';
import '../../domain/entities/enums.dart';
import '../../domain/usecases/baseline_usecases.dart';
import 'baselines_bloc.dart';

/// One device against one baseline: what each setting read as, and the facts it reported.
class BaselineHostScreen extends StatelessWidget {
  const BaselineHostScreen({super.key, required this.baselineId, required this.hostId});

  final String baselineId;
  final String hostId;

  @override
  Widget build(BuildContext context) => BlocProvider(
        create: (_) => BaselineHostBloc(
          baselineId: baselineId,
          hostId: hostId,
          getBaselineHost: locator<GetBaselineHost>(),
        )..add(const BaselinesLoadRequested()),
        child: const _BaselineHostView(),
      );
}

class _BaselineHostView extends StatelessWidget {
  const _BaselineHostView();

  @override
  Widget build(BuildContext context) => BlocBuilder<BaselineHostBloc, BaselinesState<BaselineHostDetail>>(
        builder: (context, state) {
          final detail = state.data;
          return PageScaffold(
            title: detail?.hostname ?? 'Device',
            subtitle: detail?.signedVersion == null
                ? 'No signed version of this baseline yet.'
                : 'Against version ${detail!.signedVersion}, as this device last reported it.',
            children: [
              if (state.error != null) AlertBox.error(state.error!),
              if (detail == null)
                state.loading ? const Center(child: CircularProgressIndicator()) : const EmptyPanel('Not found.')
              else ...[
                if (detail.items.isEmpty)
                  const EmptyPanel('Nothing to show yet.')
                else
                  KintsugiTable(
                    minWidth: 1300,
                    columns: [
                      const TableColumnSpec(label: 'Item', width: FlexColumnWidth(1.3)),
                      const TableColumnSpec(label: 'Registry Value', width: FlexColumnWidth(2.2)),
                      const TableColumnSpec(label: 'Expected', width: FlexColumnWidth(1)),
                      // Measured: "Not applicable" is a chip a Text cannot narrow (see KintsugiTable).
                      TableColumnSpec(
                        label: 'State',
                        width: TableColumnSpec.forContent(
                          BaselineItemState.values.map((s) => StatusChip.widthFor(context, s.label)).reduce(math.max),
                        ),
                      ),
                      const TableColumnSpec(label: 'Observed', width: FlexColumnWidth(1.2)),
                      const TableColumnSpec(label: 'Last Changed', width: FixedColumnWidth(150)),
                    ],
                    rows: [for (final row in detail.items) KintsugiTableRow(cells: _cells(row))],
                  ),
                if (detail.facts.isNotEmpty) ...[
                  const SubHeading('Facts'),
                  KintsugiTable(
                    columns: const [
                      TableColumnSpec(label: 'Fact', width: FlexColumnWidth(1)),
                      TableColumnSpec(label: 'Value', width: FlexColumnWidth(2)),
                      TableColumnSpec(label: 'Last Changed', width: FixedColumnWidth(150)),
                    ],
                    rows: [
                      for (final fact in detail.facts)
                        KintsugiTableRow(
                          cells: [
                            Text(fact.key),
                            fact.value == null ? const NoValue() : CodeText(fact.value!, size: 11.5),
                            LocalTimestamp(fact.lastChangedUtc),
                          ],
                        ),
                    ],
                  ),
                ],
              ],
            ],
          );
        },
      );

  static List<Widget> _cells(HostItemState row) => [
        Tooltip(message: row.item.reason ?? '', child: Text(row.item.key)),
        CodeText(row.item.location, size: 11.5),
        CodeText(row.item.expected, size: 11.5),
        row.state == null
            ? const HintText('Not reported')
            : Tooltip(
                message: row.detail ?? '',
                child: StatusChip(row.state!.label, statusKey: row.state!.statusKey),
              ),
        row.observedValue == null
            ? (row.detail == null ? const NoValue() : HintText(row.detail!))
            : CodeText(row.observedValue!, size: 11.5),
        LocalTimestamp(row.lastChangedUtc),
      ];
}
