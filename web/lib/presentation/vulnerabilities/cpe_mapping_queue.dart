import 'package:flutter/material.dart';
import 'package:flutter_bloc/flutter_bloc.dart';

import '../../core/di/locator.dart';
import '../../core/theme/kintsugi_palette.dart';
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
///
/// One line per subject, and everything a decision needs on that line: what NVD matched, how far
/// the evidence backs it, and the three actions. The detail a reviewer only needs for the row they
/// are working on — the dictionary search, the installed versions, the last error — opens under
/// the row and closes again. It was a stack of permanently-open panels first, which meant six
/// subjects filled a display and a backlog of three hundred could not be read at all.
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
          confirmMappings: locator<ConfirmCpeMappings>(),
          resetMappings: locator<ResetCpeMappings>(),
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
            if (state.bulkResult != null) _BulkResultAlert(result: state.bulkResult!),
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

/// What the last bulk action did — and, more to the point, what it did not.
class _BulkResultAlert extends StatelessWidget {
  const _BulkResultAlert({required this.result});

  final BulkMappingResult result;

  @override
  Widget build(BuildContext context) {
    final message = result.skipped.isEmpty
        ? '${result.applied} ${_subjects(result.applied)} updated.'
        : '${result.applied} ${_subjects(result.applied)} updated, '
            '${result.skipped.length} left unchanged.';

    return AlertBox(
      message,
      kind: result.skipped.isEmpty ? AlertKind.success : AlertKind.info,
      child: Padding(
        padding: const EdgeInsets.only(top: 8),
        child: Column(
          crossAxisAlignment: CrossAxisAlignment.start,
          children: [
            // Every one of them, by name. A bulk action that reports a number and not the rows
            // behind it reads as a success, and the subjects it passed over are exactly the ones
            // that still need somebody.
            for (final skipped in result.skipped)
              HintText('${skipped.displayName} — ${skipped.reason}'),
            if (result.skipped.isNotEmpty) const SizedBox(height: 8),
            // Dismissed by hand rather than on a timer: the skipped list is a work list, and a
            // reviewer reading twenty-eight names must not have it vanish underneath them. It also
            // goes when the next action supersedes it — see CpeMappingsBloc.
            LinkText(
              label: 'Dismiss',
              muted: true,
              onTap: () =>
                  context.read<CpeMappingsBloc>().add(const CpeMappingsBulkResultDismissed()),
            ),
          ],
        ),
      ),
    );
  }

  static String _subjects(int count) => count == 1 ? 'subject' : 'subjects';
}

class _MappingsTable extends StatelessWidget {
  const _MappingsTable({required this.state});

  final CpeMappingsState state;

  @override
  Widget build(BuildContext context) {
    final bloc = context.read<CpeMappingsBloc>();
    final filters = state.filters;
    final visible = state.visibleMappings;
    final selectedVisible = state.actionableIds.length;

    void filter(CpeMappingFilters next) => bloc.add(CpeMappingFiltersChanged(next));

    return KintsugiTable(
      // Ten columns, four of them counts or chips that cannot shrink. Below this the table scrolls
      // sideways inside its panel rather than compressing the CPE column to the point where
      // `cpe:2.3:a:mozilla:firefox` reads as `cpe:2.3:a:moz…`, which is the one thing this screen
      // is for.
      minWidth: 1500,
      toolbar: selectedVisible == 0 ? null : _SelectionToolbar(selected: selectedVisible, state: state),
      columns: [
        TableColumnSpec(
          label: 'All',
          width: const FixedColumnWidth(58),
          headerContent: _SelectAllCheckbox(state: state, visible: visible),
        ),
        TableColumnSpec(
          label: 'Application',
          width: const FlexColumnWidth(1.5),
          filter: SearchField(
            value: filters.application,
            hintText: 'Name…',
            onChanged: (value) => filter(filters.copyWith(application: value)),
          ),
        ),
        TableColumnSpec(
          label: 'CPE',
          width: const FlexColumnWidth(1.7),
          filter: SearchField(
            value: filters.cpe,
            hintText: 'cpe:2.3…',
            onChanged: (value) => filter(filters.copyWith(cpe: value)),
          ),
        ),
        TableColumnSpec(
          label: 'Vendor',
          width: const FlexColumnWidth(1),
          filter: SearchField(
            value: filters.vendor,
            hintText: 'Vendor…',
            onChanged: (value) => filter(filters.copyWith(vendor: value)),
          ),
        ),
        TableColumnSpec(
          label: 'Product',
          width: const FlexColumnWidth(1.1),
          filter: SearchField(
            value: filters.product,
            hintText: 'Product…',
            onChanged: (value) => filter(filters.copyWith(product: value)),
          ),
        ),
        TableColumnSpec(
          label: 'Confidence',
          width: const FixedColumnWidth(150),
          filter: KintsugiDropdown<String>(
            value: filters.confidence,
            items: ['all', ...CpeConfidence.values.map((c) => c.name)],
            labelOf: (value) => value == 'all'
                ? 'Any confidence'
                : CpeConfidence.values.byName(value).filterLabel,
            onChanged: (value) => filter(filters.copyWith(confidence: value)),
          ),
        ),
        TableColumnSpec(
          label: 'Status',
          width: const FixedColumnWidth(165),
          filter: KintsugiDropdown<String>(
            value: filters.status,
            items: ['all', ...CpeMappingStatus.values.map((s) => s.name)],
            labelOf: (value) =>
                value == 'all' ? 'Any status' : CpeMappingStatus.values.byName(value).label,
            onChanged: (value) => filter(filters.copyWith(status: value)),
          ),
        ),
        TableColumnSpec(
          label: 'Hosts',
          width: const FixedColumnWidth(110),
          alignRight: true,
          filter: SearchField(
            value: filters.hosts,
            hintText: 'Min',
            onChanged: (value) => filter(filters.copyWith(hosts: value)),
          ),
        ),
        TableColumnSpec(
          label: 'CVEs',
          width: const FixedColumnWidth(155),
          alignRight: true,
          filter: KintsugiDropdown<String>(
            value: filters.cves,
            items: const ['all', 'with', 'exploited', 'none'],
            labelOf: (value) => switch (value) {
              'with' => 'With CVEs',
              'exploited' => 'Known exploited',
              'none' => 'None found',
              _ => 'Any CVEs',
            },
            onChanged: (value) => filter(filters.copyWith(cves: value)),
          ),
        ),
        // Four 40px icon buttons — Material 3 lays an IconButton out at 40 whatever constraints
        // it is given — plus this table's two 12px gutters. Narrower and the last one is painted
        // outside its own cell.
        const TableColumnSpec(label: 'Actions', width: FixedColumnWidth(184)),
      ],
      rows: [
        if (visible.isEmpty)
          const KintsugiTableRow(
            key: ValueKey('no-matches'),
            cells: [
              SizedBox.shrink(),
              HintText('No subject matches these filters.'),
            ],
          ),
        for (final mapping in visible)
          KintsugiTableRow(
            // Keyed by the mapping, because this list re-sorts under the reader:
            // GetCpeMappingsQueryHandler puts suggested rows first, so confirming one moves it
            // down the queue. Unkeyed, the open detail panel's [State] — and with it the search,
            // vendor and product fields — would stay at the position rather than follow the row,
            // leaving another mapping's text under the row that had just moved.
            key: ValueKey(mapping.id),
            cells: _cells(context, mapping),
            expanded: state.expandedId == mapping.id
                ? _MappingDetail(mapping: mapping, state: state)
                : null,
          ),
      ],
    );
  }

  List<Widget> _cells(BuildContext context, CpeMapping mapping) {
    final bloc = context.read<CpeMappingsBloc>();
    final busy = state.busyId == mapping.id;
    final selected = state.selectedIds.contains(mapping.id);
    final expanded = state.expandedId == mapping.id;

    return [
      Checkbox(
        value: selected,
        onChanged: (_) => bloc.add(CpeMappingSelectionToggled(mapping.id)),
      ),
      Row(
        children: [
          if (mapping.subjectKind == CpeSubjectKind.operatingSystem) ...[
            // An icon rather than the second line this row used to carry: the kind changes what a
            // wrong mapping costs — an OS mapping covers every host at once — and it has to stay
            // visible without spending a line on it.
            Tooltip(
              message: 'Operating system',
              child: Icon(Icons.dns_outlined, size: 14, color: context.palette.muted),
            ),
            const SizedBox(width: 6),
          ],
          Expanded(
            child: Text(
              mapping.displayName,
              maxLines: 1,
              overflow: TextOverflow.ellipsis,
              style: Theme.of(context).textTheme.bodyMedium,
            ),
          ),
        ],
      ),
      mapping.cpeName != null ? _Ellipsised(mapping.cpeName!) : const NoValue(),
      mapping.vendor != null ? _Ellipsised(mapping.vendor!) : const NoValue(),
      mapping.product != null ? _Ellipsised(mapping.product!) : const NoValue(),
      mapping.confidence == CpeConfidence.none
          ? const NoValue()
          : Tooltip(
              message: mapping.confidenceReason ?? '',
              child: StatusChip(mapping.confidence.label, statusKey: mapping.confidence.statusKey),
            ),
      StatusChip(mapping.status.label, statusKey: _statusKey(mapping.status)),
      CountBadge(mapping.hostCount),
      CountBadge(
        mapping.matchCount,
        alert: mapping.knownExploitedCount > 0,
        tooltip: mapping.knownExploitedCount > 0
            ? '${mapping.knownExploitedCount} known exploited'
            : null,
      ),
      Row(
        mainAxisSize: MainAxisSize.min,
        children: [
          IconActionButton(
            icon: Icons.check,
            tooltip: mapping.vendor == null
                ? 'Nothing is proposed for this yet — open the row and search NVD’s dictionary.'
                : mapping.status == CpeMappingStatus.confirmed
                    ? 'Already confirmed as ${mapping.vendor}:${mapping.product}.'
                    : 'Confirm ${mapping.vendor}:${mapping.product}.',
            busy: busy,
            // Confirms what the row already carries. Anything else — a correction, a pair from the
            // dictionary — is typed in the detail below, which has its own Confirm.
            onPressed: mapping.vendor == null || mapping.status == CpeMappingStatus.confirmed
                ? null
                : () => bloc.add(CpeMappingConfirmed(
                      id: mapping.id,
                      vendor: mapping.vendor!,
                      product: mapping.product!,
                    )),
          ),
          IconActionButton(
            icon: Icons.block_outlined,
            tooltip: 'Not applicable: for in-house software, or anything NVD does not track. Takes '
                'it out of the not-assessed count, because that is a decision rather than a gap.',
            onPressed: busy || mapping.status == CpeMappingStatus.notApplicable
                ? null
                : () => bloc.add(CpeMappingDismissed(mapping.id)),
          ),
          IconActionButton(
            icon: Icons.undo,
            tooltip: 'Clear: returns this to the queue and discards what it was matched against.',
            onPressed: busy || mapping.status == CpeMappingStatus.unmapped
                ? null
                : () => bloc.add(CpeMappingReset(mapping.id)),
          ),
          IconActionButton(
            icon: expanded ? Icons.expand_less : Icons.expand_more,
            tooltip: expanded ? 'Hide the dictionary search' : 'Search NVD’s dictionary for this',
            onPressed: () => bloc.add(CpeMappingExpansionToggled(mapping.id)),
          ),
        ],
      ),
    ];
  }

  static String _statusKey(CpeMappingStatus status) => switch (status) {
        CpeMappingStatus.confirmed => 'mapping-confirmed',
        CpeMappingStatus.suggested => 'mapping-suggested',
        CpeMappingStatus.unmapped => 'mapping-unmapped',
        CpeMappingStatus.notApplicable => 'mapping-not-applicable',
      };
}

/// A one-line cell that shortens rather than wraps — every row is one line high, and a long CPE
/// must not be what decides the height of the table.
class _Ellipsised extends StatelessWidget {
  const _Ellipsised(this.text);

  final String text;

  @override
  Widget build(BuildContext context) => Tooltip(
        message: text,
        child: Text(
          text,
          maxLines: 1,
          overflow: TextOverflow.ellipsis,
          style: Theme.of(context).textTheme.bodyMedium?.copyWith(
                fontFamily: 'monospace',
                color: context.palette.neonSoft,
                fontSize: 12.8,
              ),
        ),
      );
}

/// Ticks or clears every row the filters leave visible.
///
/// Deliberately not "every row in the queue": a filtered table is what the reviewer is looking at,
/// and a header checkbox that also ticked the four hundred rows scrolled past a filter would
/// confirm mappings nobody had read.
class _SelectAllCheckbox extends StatelessWidget {
  const _SelectAllCheckbox({required this.state, required this.visible});

  final CpeMappingsState state;
  final List<CpeMapping> visible;

  @override
  Widget build(BuildContext context) {
    final selected = state.actionableIds.length;
    final all = visible.isNotEmpty && selected == visible.length;

    return Tooltip(
      message: all
          ? 'Deselect the ${visible.length} rows these filters leave'
          : 'Select the ${visible.length} rows these filters leave',
      child: Checkbox(
        // Tristate for display only — a partial selection shows a dash, and pressing it still
        // means "select everything visible" rather than cycling through a third state nobody
        // asked for.
        tristate: true,
        value: all ? true : (selected == 0 ? false : null),
        onChanged: visible.isEmpty
            ? null
            : (_) => context.read<CpeMappingsBloc>().add(CpeMappingVisibleSelectionToggled(!all)),
      ),
    );
  }
}

/// The strip above the header once anything is ticked: what is selected, and the two things that
/// can be done to all of it at once.
class _SelectionToolbar extends StatelessWidget {
  const _SelectionToolbar({required this.selected, required this.state});

  final int selected;
  final CpeMappingsState state;

  @override
  Widget build(BuildContext context) {
    final bloc = context.read<CpeMappingsBloc>();

    return Wrap(
      spacing: 12,
      runSpacing: 8,
      crossAxisAlignment: WrapCrossAlignment.center,
      children: [
        Text(
          '$selected selected',
          style: Theme.of(context).textTheme.bodyMedium,
        ),
        PrimaryButton(
          label: 'Confirm selected',
          busy: state.bulkBusy,
          onPressed: () => bloc.add(const CpeMappingsBulkConfirmed()),
        ),
        SecondaryButton(
          label: 'Clear selected',
          tooltip: 'Returns every selected subject to the queue, discarding what each was matched '
              'against.',
          onPressed: state.bulkBusy ? null : () => bloc.add(const CpeMappingsBulkReset()),
        ),
        SecondaryButton(
          label: 'Deselect all',
          onPressed: state.bulkBusy ? null : () => bloc.add(const CpeMappingSelectionCleared()),
        ),
      ],
    );
  }
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
          if (mapping.confidenceReason != null) ...[
            const SizedBox(height: 6),
            HintText(mapping.confidenceReason!),
          ],
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
                const SizedBox(width: 12),
                // The row's own tick confirms what the row already carries; this one confirms what
                // has just been typed here, which is the only path that reaches NVD's dictionary
                // check on the way in.
                PrimaryButton(
                  label: mapping.status == CpeMappingStatus.confirmed ? 'Re-map' : 'Confirm this pair',
                  busy: busy,
                  onPressed: () => context.read<CpeMappingsBloc>().add(CpeMappingConfirmed(
                        id: mapping.id,
                        vendor: _vendor.text.trim(),
                        product: _product.text.trim(),
                      )),
                ),
              ],
            ),
          ],
        ],
      ),
    );
  }

  void _runSearch(BuildContext context) => context.read<CpeMappingsBloc>().add(
        CpeDictionarySearched(mappingId: widget.mapping.id, keyword: _search.text.trim()),
      );
}
