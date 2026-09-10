import 'package:flutter/material.dart';
import 'package:flutter_bloc/flutter_bloc.dart';

import '../../core/di/locator.dart';
import '../../core/widgets/alert_box.dart';
import '../../core/widgets/buttons.dart';
import '../../core/widgets/form_bits.dart';
import '../../core/widgets/kintsugi_table.dart';
import '../../core/widgets/panel.dart';
import '../../core/widgets/status_chip.dart';
import '../../core/widgets/text_bits.dart';
import '../../domain/entities/enums.dart';
import '../../domain/entities/vulnerability.dart';
import '../../domain/usecases/vulnerability_usecases.dart';
import 'vulnerabilities_bloc.dart';

/// The CPE mapping queue: everything this fleet has installed, and what NVD is being asked about
/// it.
///
/// This is the gate the whole feature runs through, and the reason it is a human decision rather
/// than an automatic one is on the screen: NVD's own dictionary ranks Slackware Linux first for
/// "slack" and ZoomText for "zoom", so an accepted wrong mapping quietly attributes another
/// product's vulnerabilities to yours — which is worse than no answer, because it is the answer
/// somebody acts on.
class CpeMappingQueue extends StatelessWidget {
  const CpeMappingQueue({super.key});

  @override
  Widget build(BuildContext context) => BlocProvider(
        create: (_) => CpeMappingsBloc(
          getMappings: locator<GetCpeMappings>(),
          searchDictionary: locator<SearchCpeDictionary>(),
          confirmMapping: locator<ConfirmCpeMapping>(),
          markNotApplicable: locator<MarkCpeMappingNotApplicable>(),
          resetMapping: locator<ResetCpeMapping>(),
        )..add(const CpeMappingsRequested()),
        child: const _MappingQueueView(),
      );
}

class _MappingQueueView extends StatelessWidget {
  const _MappingQueueView();

  @override
  Widget build(BuildContext context) => BlocBuilder<CpeMappingsBloc, CpeMappingsState>(
        builder: (context, state) => Column(
          crossAxisAlignment: CrossAxisAlignment.start,
          children: [
            const SubHeadingTight('CPE mapping queue'),
            const HintText(
              'NVD indexes vulnerabilities by CPE — a vendor and product name that is often not '
              'what the software calls itself. Nothing is assessed for an application until '
              'somebody confirms which one it is.',
            ),
            const SizedBox(height: 12),
            if (state.error != null) AlertBox.error(state.error!),
            if (state.loading)
              const Padding(padding: EdgeInsets.all(24), child: LinearProgressIndicator())
            else if (state.mappings.isEmpty)
              const EmptyPanel(
                'Nothing has been discovered yet. Subjects appear here after the first assessment '
                'run, which reads what the fleet has reported installed.',
              )
            else
              _MappingsTable(state: state),
          ],
        ),
      );
}

class _MappingsTable extends StatelessWidget {
  const _MappingsTable({required this.state});

  final CpeMappingsState state;

  @override
  Widget build(BuildContext context) => KintsugiTable(
        minWidth: 940,
        columns: const [
          TableColumnSpec(label: 'Application', width: FlexColumnWidth(2)),
          TableColumnSpec(label: 'CPE', width: FlexColumnWidth(2)),
          TableColumnSpec(label: 'Status', width: FixedColumnWidth(150)),
          TableColumnSpec(label: 'Hosts', width: FixedColumnWidth(80), alignRight: true),
          TableColumnSpec(label: 'CVEs', width: FixedColumnWidth(80), alignRight: true),
        ],
        rows: [
          for (final mapping in state.mappings)
            KintsugiTableRow(
              cells: [
                Column(
                  crossAxisAlignment: CrossAxisAlignment.start,
                  mainAxisSize: MainAxisSize.min,
                  children: [
                    Text(mapping.displayName, style: Theme.of(context).textTheme.bodyMedium),
                    if (mapping.subjectKind == CpeSubjectKind.operatingSystem)
                      const HintText('Operating system'),
                  ],
                ),
                mapping.cpeName != null ? CodeText(mapping.cpeName!) : const NoValue(),
                StatusChip(mapping.status.label, statusKey: _statusKey(mapping.status)),
                CountBadge(mapping.hostCount),
                CountBadge(
                  mapping.matchCount,
                  alert: mapping.knownExploitedCount > 0,
                  tooltip: mapping.knownExploitedCount > 0
                      ? '${mapping.knownExploitedCount} known exploited'
                      : null,
                ),
              ],
              expanded: _MappingDetail(mapping: mapping, state: state),
            ),
        ],
      );

  static String _statusKey(CpeMappingStatus status) => switch (status) {
        CpeMappingStatus.confirmed => 'mapping-confirmed',
        CpeMappingStatus.suggested => 'mapping-suggested',
        CpeMappingStatus.unmapped => 'mapping-unmapped',
        CpeMappingStatus.notApplicable => 'mapping-not-applicable',
      };
}

class _MappingDetail extends StatefulWidget {
  const _MappingDetail({required this.mapping, required this.state});

  final CpeMapping mapping;
  final CpeMappingsState state;

  @override
  State<_MappingDetail> createState() => _MappingDetailState();
}

class _MappingDetailState extends State<_MappingDetail> {
  late final TextEditingController _search =
      TextEditingController(text: widget.mapping.displayName);
  late final TextEditingController _vendor = TextEditingController(text: widget.mapping.vendor ?? '');
  late final TextEditingController _product =
      TextEditingController(text: widget.mapping.product ?? '');

  @override
  void dispose() {
    _search.dispose();
    _vendor.dispose();
    _product.dispose();
    super.dispose();
  }

  void _pick(CpeCandidate candidate) {
    setState(() {
      _vendor.text = candidate.vendor;
      _product.text = candidate.product;
    });
  }

  @override
  Widget build(BuildContext context) {
    final mapping = widget.mapping;
    final state = widget.state;
    final busy = state.busyId == mapping.id;
    final candidates = state.candidates[mapping.id] ?? const <CpeCandidate>[];

    return Padding(
      padding: const EdgeInsets.fromLTRB(12, 12, 12, 16),
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          HintText(mapping.status.description),
          if (mapping.unassessableReason != null) ...[
            const SizedBox(height: 8),
            // The one case a mapping cannot fix: the agent is not sending enough. Said here rather
            // than left as an empty CVE count, which would read as "nothing wrong with this host".
            AlertBox.info(mapping.unassessableReason!),
          ],
          if (mapping.lastError != null) ...[
            const SizedBox(height: 8),
            AlertBox.error('The last check failed: ${mapping.lastError}'),
          ],
          if (mapping.suggestionNotes != null && mapping.status == CpeMappingStatus.suggested) ...[
            const SizedBox(height: 8),
            Row(
              children: [
                StatusChip.neutral(mapping.suggestionSource.label),
                const SizedBox(width: 8),
                Flexible(child: HintText(mapping.suggestionNotes!)),
              ],
            ),
          ],
          const SizedBox(height: 12),
          if (mapping.versions.isNotEmpty) ...[
            Wrap(
              spacing: 6,
              runSpacing: 4,
              crossAxisAlignment: WrapCrossAlignment.center,
              children: [
                const HintText('Installed versions: '),
                for (final version in mapping.versions.take(12)) CodeText(version),
                if (mapping.versions.length > 12)
                  HintText('and ${mapping.versions.length - 12} more'),
              ],
            ),
            const SizedBox(height: 12),
          ],
          if (mapping.status != CpeMappingStatus.notApplicable) ...[
            const SubHeadingTight('Find this product in NVD’s dictionary'),
            const HintText(
              'Search returns the vendor and product names NVD actually indexes. It is a keyword '
              'search, so read the results rather than taking the first: “slack” ranks Slackware '
              'Linux above Slack.',
            ),
            const SizedBox(height: 8),
            Row(
              crossAxisAlignment: CrossAxisAlignment.start,
              children: [
                SizedBox(
                  width: 280,
                  child: KintsugiTextField(
                    controller: _search,
                    hintText: 'Product name',
                    onEditingComplete: () => _runSearch(context),
                  ),
                ),
                const SizedBox(width: 8),
                SecondaryButton(
                  label: state.searchingFor == mapping.id ? 'Searching…' : 'Search',
                  onPressed: state.searchingFor == mapping.id ? null : () => _runSearch(context),
                ),
              ],
            ),
            if (candidates.isNotEmpty) ...[
              const SizedBox(height: 10),
              ConstrainedBox(
                constraints: const BoxConstraints(maxHeight: 220),
                child: SingleChildScrollView(
                  child: Column(
                    crossAxisAlignment: CrossAxisAlignment.start,
                    children: [
                      for (final candidate in candidates.take(25))
                        Padding(
                          padding: const EdgeInsets.only(bottom: 4),
                          child: Row(
                            children: [
                              SecondaryButton(label: 'Use', onPressed: () => _pick(candidate)),
                              const SizedBox(width: 8),
                              CodeText('${candidate.vendor}:${candidate.product}'),
                              const SizedBox(width: 8),
                              Flexible(
                                child: HintText(
                                  candidate.titleHint ?? '${candidate.entryCount} dictionary entries',
                                ),
                              ),
                            ],
                          ),
                        ),
                    ],
                  ),
                ),
              ),
            ] else if (state.searchingFor != mapping.id && state.candidates.containsKey(mapping.id))
              const HintText('NVD’s dictionary has nothing matching that name.'),
            const SizedBox(height: 14),
            Row(
              crossAxisAlignment: CrossAxisAlignment.start,
              children: [
                SizedBox(
                  width: 200,
                  child: KintsugiTextField(controller: _vendor, hintText: 'Vendor, e.g. mozilla'),
                ),
                const SizedBox(width: 8),
                SizedBox(
                  width: 200,
                  child: KintsugiTextField(controller: _product, hintText: 'Product, e.g. firefox'),
                ),
              ],
            ),
            const SizedBox(height: 12),
          ],
          Wrap(
            spacing: 8,
            runSpacing: 8,
            children: [
              if (mapping.status != CpeMappingStatus.notApplicable)
                PrimaryButton(
                  label: mapping.status == CpeMappingStatus.confirmed ? 'Re-map' : 'Confirm',
                  busy: busy,
                  onPressed: () => context.read<CpeMappingsBloc>().add(CpeMappingConfirmed(
                        id: mapping.id,
                        vendor: _vendor.text.trim(),
                        product: _product.text.trim(),
                      )),
                ),
              if (mapping.status != CpeMappingStatus.notApplicable)
                SecondaryButton(
                  label: 'Not applicable',
                  tooltip: 'For in-house software, or anything NVD does not track. Takes it out of '
                      'the not-assessed count, because that is a decision rather than a gap.',
                  onPressed: busy
                      ? null
                      : () => context.read<CpeMappingsBloc>().add(CpeMappingDismissed(mapping.id)),
                ),
              if (mapping.status != CpeMappingStatus.unmapped)
                SecondaryButton(
                  label: 'Clear',
                  tooltip: 'Returns this to the queue and discards what it was matched against.',
                  onPressed: busy
                      ? null
                      : () => context.read<CpeMappingsBloc>().add(CpeMappingReset(mapping.id)),
                ),
            ],
          ),
        ],
      ),
    );
  }

  void _runSearch(BuildContext context) => context.read<CpeMappingsBloc>().add(
        CpeDictionarySearched(mappingId: widget.mapping.id, keyword: _search.text.trim()),
      );
}
