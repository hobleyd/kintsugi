import 'dart:math' as math;

import 'package:flutter/material.dart';
import 'package:flutter_bloc/flutter_bloc.dart';
import 'package:go_router/go_router.dart';

import '../../core/di/locator.dart';
import '../../core/router/app_router.dart';
import '../../core/theme/kintsugi_palette.dart';
import '../../core/widgets/alert_box.dart';
import '../../core/widgets/buttons.dart';
import '../../core/widgets/form_bits.dart';
import '../../core/widgets/kintsugi_table.dart';
import '../../core/widgets/page_scaffold.dart';
import '../../core/widgets/panel.dart';
import '../../core/widgets/status_chip.dart';
import '../../core/widgets/text_bits.dart';
import '../../domain/entities/baseline.dart';
import '../../domain/usecases/baseline_usecases.dart';
import 'baselines_bloc.dart';

/// Every device baseline, with how many hosts comply. The Windows agent reads and reports each item,
/// and sets back only an `enforce` item on the server's write allowlist.
class BaselinesScreen extends StatelessWidget {
  const BaselinesScreen({super.key});

  @override
  Widget build(BuildContext context) => BlocProvider(
        create: (_) => BaselinesBloc(
          getBaselines: locator<GetBaselines>(),
          importBaseline: locator<ImportBaseline>(),
        )..add(const BaselinesLoadRequested()),
        child: const _BaselinesView(),
      );
}

class _BaselinesView extends StatelessWidget {
  const _BaselinesView();

  @override
  Widget build(BuildContext context) => BlocBuilder<BaselinesBloc, BaselinesState<List<BaselineSummary>>>(
        builder: (context, state) {
          final baselines = state.data ?? const <BaselineSummary>[];
          return PageScaffold(
            title: 'Baselines',
            subtitle: 'What should be true on a device, read by its agent every hour. Items are audited; '
                'only an item marked enforce, on the write allowlist, is set back when it drifts.',
            children: [
              if (state.error != null) AlertBox.error(state.error!),
              if (state.notice != null) AlertBox.success(state.notice!),
              SectionHeader(
                title: 'Baselines',
                hints: const [
                  HintText(
                    'A baseline reaches a device only once a version is signed and the baseline is '
                    'assigned. Paste a baseline as JSON (the shape of baselines/master-daily.json) to '
                    'create one.',
                  ),
                ],
                actions: [
                  SecondaryButton(
                    label: 'Import Baseline',
                    busy: state.busy,
                    onPressed: () {
                      final bloc = context.read<BaselinesBloc>();
                      showImportDialog(
                        context,
                        title: 'Import a baseline',
                        onImport: (json) => bloc.add(BaselineImportRequested(json)),
                      );
                    },
                  ),
                ],
              ),
              if (state.loading && state.data == null)
                const Center(child: CircularProgressIndicator())
              else if (baselines.isEmpty)
                const EmptyPanel('No baselines yet.')
              else
                KintsugiTable(
                  minWidth: 1100,
                  columns: complianceColumns([
                    const TableColumnSpec(label: 'Baseline', width: FlexColumnWidth(2)),
                    // At least as wide as the "Unsigned" chip, which a Text cannot narrow.
                    TableColumnSpec(
                      label: 'Signed Version',
                      width: TableColumnSpec.forContent(math.max(130.0, StatusChip.widthFor(context, 'Unsigned'))),
                    ),
                    const TableColumnSpec(label: 'Applies To', width: FlexColumnWidth(1.2)),
                    const TableColumnSpec(label: 'Settings', width: FixedColumnWidth(100), alignRight: true),
                  ]),
                  rows: [
                    for (final baseline in baselines)
                      KintsugiTableRow(
                        cells: [
                          LinkText(label: baseline.name, onTap: () => context.go(Routes.baseline(baseline.id))),
                          baseline.signedVersion == null
                              ? const StatusChip('Unsigned', statusKey: 'review-sign')
                              : Text('v${baseline.signedVersion}'
                                  '${baseline.latestVersion > baseline.signedVersion! ? ' (v${baseline.latestVersion} unsigned)' : ''}'),
                          Text(
                            '${baseline.assignedToAllWindowsHosts ? 'Every Windows host · ' : ''}'
                            '${baseline.applicableHostCount} host(s)',
                          ),
                          Text('${baseline.settingCount}'),
                          ...complianceCells(baseline.totals),
                        ],
                      ),
                  ],
                ),
            ],
          );
        },
      );
}

/// [leading] followed by one column per compliance state — shared by every Baselines table.
List<TableColumnSpec> complianceColumns(List<TableColumnSpec> leading) => [
      ...leading,
      const TableColumnSpec(label: 'Compliant', width: FixedColumnWidth(110), alignRight: true),
      const TableColumnSpec(label: 'Drifted', width: FixedColumnWidth(100), alignRight: true),
      const TableColumnSpec(label: 'Not Applicable', width: FixedColumnWidth(120), alignRight: true),
      const TableColumnSpec(label: 'Error', width: FixedColumnWidth(90), alignRight: true),
      const TableColumnSpec(label: 'Not Reported', width: FixedColumnWidth(110), alignRight: true),
    ];

/// The cells under [complianceColumns]. Drifted and Error are highlighted when not zero; those are
/// the numbers somebody acts on.
List<Widget> complianceCells(ComplianceCounts counts) => [
      CountBadge(counts.compliant),
      CountBadge(counts.drifted, alert: counts.drifted > 0),
      CountBadge(counts.notApplicable),
      CountBadge(counts.error, alert: counts.error > 0),
      CountBadge(counts.notReported),
    ];

/// Asks for pasted JSON and hands it to [onImport]. The server validates it; a body it refuses comes
/// back as the screen's error.
Future<void> showImportDialog(
  BuildContext context, {
  required String title,
  required ValueChanged<String> onImport,
}) async {
  final controller = TextEditingController();
  final json = await showDialog<String>(
    context: context,
    builder: (dialogContext) => AlertDialog(
      backgroundColor: dialogContext.palette.backgroundAlt,
      shape: RoundedRectangleBorder(
        borderRadius: BorderRadius.circular(6),
        side: BorderSide(color: dialogContext.palette.border),
      ),
      title: Text(title, style: Theme.of(dialogContext).textTheme.titleLarge),
      content: SizedBox(
        width: 720,
        child: Column(
          mainAxisSize: MainAxisSize.min,
          crossAxisAlignment: CrossAxisAlignment.start,
          children: [
            const HintText(
              'A JSON object with "name" and "items". Every version starts unsigned; nothing reaches a '
              'device until somebody reviews and signs it.',
            ),
            const SizedBox(height: 12),
            KintsugiTextField(controller: controller, minLines: 12, maxLines: 20, hintText: '{"name": ..., "items": [...]}'),
          ],
        ),
      ),
      actions: [
        SecondaryButton(label: 'Cancel', onPressed: () => Navigator.of(dialogContext).pop()),
        PrimaryButton(label: 'Import', onPressed: () => Navigator.of(dialogContext).pop(controller.text)),
      ],
    ),
  );
  controller.dispose();

  if (json != null && json.trim().isNotEmpty) onImport(json);
}
