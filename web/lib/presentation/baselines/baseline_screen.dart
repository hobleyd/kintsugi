import 'dart:convert';

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
import 'baselines_screen.dart';

/// One baseline: review and sign its versions, choose who it applies to, and see per-item and
/// per-device compliance for the signed version.
class BaselineScreen extends StatelessWidget {
  const BaselineScreen({super.key, required this.baselineId});

  final String baselineId;

  @override
  Widget build(BuildContext context) => BlocProvider(
        create: (_) => BaselineBloc(
          baselineId: baselineId,
          getBaseline: locator<GetBaseline>(),
          importBaseline: locator<ImportBaseline>(),
          signVersion: locator<SignBaselineVersion>(),
          assignBaseline: locator<AssignBaseline>(),
        )..add(const BaselinesLoadRequested()),
        child: const _BaselineView(),
      );
}

class _BaselineView extends StatelessWidget {
  const _BaselineView();

  @override
  Widget build(BuildContext context) => BlocBuilder<BaselineBloc, BaselinesState<BaselineDetail>>(
        builder: (context, state) {
          final detail = state.data;
          return PageScaffold(
            title: detail?.name ?? 'Baseline',
            subtitle: detail?.signedVersion == null
                ? 'Nothing signed yet, so no device has this baseline.'
                : 'Agents read version ${detail!.signedVersion!.number}. Enforce items are set back when they drift; '
                    'everything else is only reported.',
            children: [
              if (state.error != null) AlertBox.error(state.error!),
              if (state.notice != null) AlertBox.success(state.notice!),
              if (detail == null)
                state.loading ? const Center(child: CircularProgressIndicator()) : const EmptyPanel('Baseline not found.')
              else ...[
                _Versions(detail: detail, busy: state.busy),
                const SubHeading('Applies to'),
                _AssignmentForm(detail: detail, busy: state.busy),
                const SubHeading('Settings'),
                _Items(detail: detail),
                const SubHeading('Devices'),
                _Hosts(detail: detail),
              ],
            ],
          );
        },
      );
}

class _Versions extends StatelessWidget {
  const _Versions({required this.detail, required this.busy});

  final BaselineDetail detail;
  final bool busy;

  @override
  Widget build(BuildContext context) {
    final bloc = context.read<BaselineBloc>();
    return Column(
      crossAxisAlignment: CrossAxisAlignment.start,
      children: [
        SectionHeader(
          title: 'Versions',
          hints: const [
            HintText(
              'A version is signed once, after review, and never edited. Agents run the newest signed '
              'version; a newer unsigned one changes nothing until it is signed.',
            ),
          ],
          actions: [
            SecondaryButton(
              label: 'Import New Version',
              busy: busy,
              onPressed: () => showImportDialog(
                context,
                title: 'New version of ${detail.name}',
                onImport: (json) => bloc.add(BaselineImportRequested(json)),
              ),
            ),
          ],
        ),
        KintsugiTable(
          minWidth: 800,
          columns: [
            const TableColumnSpec(label: 'Version', width: FixedColumnWidth(100)),
            TableColumnSpec(label: 'Status', width: TableColumnSpec.forContent(StatusChip.widthFor(context, 'Unsigned'))),
            const TableColumnSpec(label: 'Created', width: FlexColumnWidth(1.4)),
            const TableColumnSpec(label: 'Signed', width: FlexColumnWidth(1.4)),
            const TableColumnSpec(label: 'Actions', width: FixedColumnWidth(190)),
          ],
          rows: [
            for (final version in detail.versions)
              KintsugiTableRow(
                cells: [
                  Text('v${version.number}'),
                  version.signed
                      ? const StatusChip('Signed', statusKey: 'up-to-date')
                      : const StatusChip('Unsigned', statusKey: 'review-sign'),
                  Text('${version.createdBy ?? 'unknown'} · ${_date(version.createdUtc)}'),
                  version.signed ? Text('${version.signedBy ?? 'unknown'} · ${_date(version.signedUtc)}') : const NoValue(),
                  version.signed
                      ? const NoValue()
                      : SecondaryButton(
                          label: 'Review and Sign',
                          busy: busy,
                          onPressed: () => _reviewAndSign(context, version, bloc),
                        ),
                ],
              ),
          ],
        ),
      ],
    );
  }

  static String _date(DateTime? value) => value == null ? '—' : value.toString().substring(0, 16);

  /// Shows the document that will be signed and its SHA-256, and signs only on an explicit press.
  /// The hash sent is computed from the very string displayed here (see
  /// `SignBaselineVersion.documentSha256`), so the server refuses with a 409 if what it holds has changed.
  static Future<void> _reviewAndSign(BuildContext context, BaselineVersion version, BaselineBloc bloc) async {
    // Indented for reading only; the hash is over the exact string the server sent.
    final readable = const JsonEncoder.withIndent('  ').convert(jsonDecode(version.document));
    final confirmed = await showDialog<bool>(
      context: context,
      builder: (dialogContext) => AlertDialog(
        backgroundColor: dialogContext.palette.backgroundAlt,
        shape: RoundedRectangleBorder(
          borderRadius: BorderRadius.circular(6),
          side: BorderSide(color: dialogContext.palette.border),
        ),
        title: Text('Sign version ${version.number}?', style: Theme.of(dialogContext).textTheme.titleLarge),
        content: SizedBox(
          width: 820,
          child: Column(
            mainAxisSize: MainAxisSize.min,
            crossAxisAlignment: CrossAxisAlignment.start,
            children: [
              const HintText(
                'Signing makes this document what every assigned device reads within the hour. Items in '
                'audit mode are only reported; an item in enforce mode is also set on the device when it '
                'has drifted.',
              ),
              const SizedBox(height: 8),
              CodeText('SHA-256 ${SignBaselineVersion.documentSha256(version.document)}'),
              const SizedBox(height: 12),
              SizedBox(height: 420, child: SingleChildScrollView(child: CodeText(readable))),
            ],
          ),
        ),
        actions: [
          SecondaryButton(label: 'Cancel', onPressed: () => Navigator.of(dialogContext).pop(false)),
          PrimaryButton(label: 'Sign', onPressed: () => Navigator.of(dialogContext).pop(true)),
        ],
      ),
    );

    if (confirmed == true) bloc.add(BaselineSignRequested(version));
  }
}

/// Every Windows host, a list of host names, or both. Its own state because it holds two inputs the
/// administrator is editing while the screen polls underneath.
class _AssignmentForm extends StatefulWidget {
  const _AssignmentForm({required this.detail, required this.busy});

  final BaselineDetail detail;
  final bool busy;

  @override
  State<_AssignmentForm> createState() => _AssignmentFormState();
}

class _AssignmentFormState extends State<_AssignmentForm> {
  late bool _allWindowsHosts = widget.detail.assignedToAllWindowsHosts;
  late final TextEditingController _hostNames =
      TextEditingController(text: widget.detail.assignedHostNames.join('\n'));

  @override
  void dispose() {
    _hostNames.dispose();
    super.dispose();
  }

  @override
  Widget build(BuildContext context) => KintsugiPanel(
        padding: const EdgeInsets.all(18),
        child: Column(
          crossAxisAlignment: CrossAxisAlignment.start,
          children: [
            KintsugiCheckbox(
              label: 'Every Windows host',
              value: _allWindowsHosts,
              onChanged: (value) => setState(() => _allWindowsHosts = value),
            ),
            const SizedBox(height: 12),
            LabelledField(
              label: 'And these hosts',
              hints: const [HintText('One host name per line. Use test devices only while this is a pilot.')],
              child: KintsugiTextField(controller: _hostNames, minLines: 3, maxLines: 8),
            ),
            const SizedBox(height: 12),
            PrimaryButton(
              label: 'Save Assignment',
              busy: widget.busy,
              onPressed: () => context.read<BaselineBloc>().add(
                    BaselineAssignRequested(
                      allWindowsHosts: _allWindowsHosts,
                      hostNames: [
                        for (final line in _hostNames.text.split('\n'))
                          if (line.trim().isNotEmpty) line.trim(),
                      ],
                    ),
                  ),
            ),
          ],
        ),
      );
}

class _Items extends StatelessWidget {
  const _Items({required this.detail});

  final BaselineDetail detail;

  @override
  Widget build(BuildContext context) {
    final settings = detail.items.where((i) => i.item.isSetting).toList();
    if (settings.isEmpty) return const EmptyPanel('Nothing is signed yet, so nothing has been checked.');

    return KintsugiTable(
      minWidth: 1300,
      columns: complianceColumns(const [
        TableColumnSpec(label: 'Item', width: FlexColumnWidth(1.4)),
        TableColumnSpec(label: 'Registry Value', width: FlexColumnWidth(2.4)),
        TableColumnSpec(label: 'Expected', width: FlexColumnWidth(1)),
      ]),
      rows: [
        for (final row in settings)
          KintsugiTableRow(
            cells: [
              Tooltip(message: row.item.reason ?? '', child: Text(row.item.key)),
              CodeText(row.item.location, size: 11.5),
              CodeText(row.item.expected, size: 11.5),
              ...complianceCells(row.counts),
            ],
          ),
      ],
    );
  }
}

class _Hosts extends StatelessWidget {
  const _Hosts({required this.detail});

  final BaselineDetail detail;

  @override
  Widget build(BuildContext context) {
    if (detail.hosts.isEmpty) return const EmptyPanel('This baseline applies to no host yet.');

    return KintsugiTable(
      minWidth: 1000,
      columns: [
        ...complianceColumns(const [TableColumnSpec(label: 'Host', width: FlexColumnWidth(1.6))]),
        const TableColumnSpec(label: 'Last Checked', width: FixedColumnWidth(150)),
      ],
      rows: [
        for (final host in detail.hosts)
          KintsugiTableRow(
            cells: [
              LinkText(label: host.hostname, onTap: () => context.go(Routes.baselineHost(detail.id, host.hostId))),
              ...complianceCells(host.counts),
              LocalTimestamp(host.lastCheckedUtc),
            ],
          ),
      ],
    );
  }
}
