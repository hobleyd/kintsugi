import 'package:flutter/material.dart';
import 'package:flutter_bloc/flutter_bloc.dart';

import '../../core/di/injection.dart';
import '../../core/platform/page_navigator.dart';
import '../../core/theme/kintsugi_palette.dart';
import '../../core/widgets/alert_box.dart';
import '../../core/widgets/buttons.dart';
import '../../core/widgets/form_bits.dart';
import '../../core/widgets/kintsugi_table.dart';
import '../../core/widgets/page_scaffold.dart';
import '../../core/widgets/panel.dart';
import '../../core/widgets/status_chip.dart';
import '../../core/widgets/text_bits.dart';
import '../../domain/entities/enums.dart';
import '../../domain/usecases/application_usecases.dart';
import '../../domain/usecases/upgrade_path_usecases.dart';
import 'applications_bloc.dart';
import 'background_run_bloc.dart';
import 'upgrade_run_blocs.dart';
import 'widgets/instructions_panel.dart';
import 'widgets/run_progress_view.dart';
import 'widgets/script_dialog.dart';

/// Every application reported across the fleet, with its upgrade status inline.
/// What `Pages/Applications.cshtml` was, and the busiest screen in the product.
class ApplicationsScreen extends StatelessWidget {
  const ApplicationsScreen({super.key, this.initialStatusKey, this.initialHostName});

  /// Deep-link filters, read from the query string exactly as the page read them off
  /// `window.location.search`. The Hosts screen's "N app updates" badge links here with both set.
  final String? initialStatusKey;
  final String? initialHostName;

  /// The status filter's options, keyed on the same `statusKey` the server computes.
  static const statusOptions = <String, String>{
    'all': 'All statuses',
    'update-available': 'Update Available',
    'up-to-date': 'Up To Date',
    'review-sign': 'Review And Sign',
    'not-found': 'No Known Path',
    'check-failed': 'Check Failed',
    'not-checked': 'Not Checked Yet',
  };

  @override
  Widget build(BuildContext context) => MultiBlocProvider(
        providers: [
          BlocProvider(
            create: (_) => ApplicationsBloc(
              getOverview: locator<GetApplicationOverview>(),
              initialFilters: ApplicationFilters(
                statusKey: statusOptions.containsKey(initialStatusKey) ? initialStatusKey! : 'all',
                // Held as given and matched case-insensitively when filtering: a query parameter
                // whose case does not match the stored hostname would otherwise silently select
                // nothing.
                hostName: initialHostName ?? 'all',
              ),
            )..add(const ApplicationsRequested()),
          ),
          BlocProvider(
            create: (_) => UpgradePathScanBloc(
              startScan: locator<StartUpgradePathScan>(),
              scanStatus: locator<GetUpgradePathScanStatus>(),
            )..add(const RunStatusRequested(adopt: true)),
          ),
          BlocProvider(
            create: (_) => UpdateCheckBloc(
              startUpdateCheck: locator<StartUpdateCheck>(),
              updateCheckStatus: locator<GetUpdateCheckStatus>(),
            )..add(const RunStatusRequested(adopt: true)),
          ),
        ],
        child: const _ApplicationsView(),
      );
}

class _ApplicationsView extends StatelessWidget {
  const _ApplicationsView();

  @override
  Widget build(BuildContext context) =>
      const BlocBuilder<ApplicationsBloc, ApplicationsState>(builder: _build);

  static Widget _build(BuildContext context, ApplicationsState state) {
    void reload() =>
        context.read<ApplicationsBloc>().add(const ApplicationsRequested(showSpinner: false));

    return PageScaffold(
      title: 'Installed Applications',
      children: [
        const SectionHeader(
          title: 'Upgrade Paths',
          hints: [
            HintText(
              '"Find Upgrade Paths" resolves an update method for each installed application that '
              'does not have one yet, one at a time, in series. Package-manager-managed applications '
              'get a fixed, deterministic script inserted directly, with no AI call involved; '
              'everything else uses the configured AI agent to generate one. Either way, a freshly '
              'generated script still needs a human to review and sign it before an agent will run '
              'it.',
            ),
            HintText(
              '"Check for Updates" re-runs each existing script\'s own version check instead, with '
              'no AI call involved, to see whether a newer version has been released. Both run in '
              'the background, and this screen follows their progress.',
            ),
          ],
          actions: [
            _ScanButton(),
            _UpdateCheckButton(),
          ],
        ),
        RunProgressView<UpgradePathScanBloc>(onFinished: reload),
        RunProgressView<UpdateCheckBloc>(onFinished: reload),
        Text(
          '${state.overview.totalApplicationCount} distinct application(s) reported across all hosts',
          style: Theme.of(context).textTheme.bodyMedium?.copyWith(color: context.palette.muted),
        ),
        const SizedBox(height: 20),
        if (state.error != null) AlertBox.error(state.error!),
        if (state.loading && state.overview.applications.isEmpty)
          const _LoadingPanel()
        else if (state.overview.applications.isEmpty)
          const EmptyPanel(
            'No applications have been reported yet. They appear here once an agent reports its '
            'inventory.',
          )
        else
          _ApplicationsTable(state: state, onServerStateChanged: reload),
      ],
    );
  }
}

class _ScanButton extends StatelessWidget {
  const _ScanButton();

  @override
  Widget build(BuildContext context) => BlocBuilder<UpgradePathScanBloc, BackgroundRunState>(
        builder: (context, state) => PrimaryButton(
          label: state.progress.isRunning ? 'Scan Running...' : 'Find Upgrade Paths',
          busy: state.progress.isRunning,
          onPressed: () => context.read<UpgradePathScanBloc>().add(const RunStartRequested()),
        ),
      );
}

class _UpdateCheckButton extends StatelessWidget {
  const _UpdateCheckButton();

  @override
  Widget build(BuildContext context) => BlocBuilder<UpdateCheckBloc, BackgroundRunState>(
        builder: (context, state) => SecondaryButton(
          label: state.progress.isRunning ? 'Checking...' : 'Check for Updates',
          onPressed: state.progress.isRunning
              ? null
              : () => context.read<UpdateCheckBloc>().add(const RunStartRequested()),
        ),
      );
}

class _ApplicationsTable extends StatelessWidget {
  const _ApplicationsTable({required this.state, required this.onServerStateChanged});

  final ApplicationsState state;
  final VoidCallback onServerStateChanged;

  @override
  Widget build(BuildContext context) {
    final bloc = context.read<ApplicationsBloc>();
    final rows = state.visibleRows;

    final columns = [
      TableColumnSpec(
        label: 'Application Name',
        width: const FlexColumnWidth(1.6),
        sortKey: 'name',
        filter: _SearchField(
          value: state.filters.search,
          onChanged: (value) =>
              bloc.add(ApplicationsFiltersChanged(state.filters.copyWith(search: value))),
        ),
      ),
      TableColumnSpec(
        label: 'Hosts Installed On',
        width: const FixedColumnWidth(170),
        alignRight: true,
        sortKey: 'hosts',
        filter: KintsugiDropdown<String>(
          value: state.filters.hostName,
          items: ['all', ...state.overview.allHostNames],
          labelOf: (value) => value == 'all' ? 'All hosts' : value,
          onChanged: (value) =>
              bloc.add(ApplicationsFiltersChanged(state.filters.copyWith(hostName: value))),
        ),
      ),
      const TableColumnSpec(label: 'Platform', width: FlexColumnWidth(1), sortKey: 'platform'),
      TableColumnSpec(
        label: 'Status',
        width: const FixedColumnWidth(180),
        sortKey: 'status',
        filter: KintsugiDropdown<String>(
          value: state.filters.statusKey,
          items: ApplicationsScreen.statusOptions.keys.toList(),
          labelOf: (value) => ApplicationsScreen.statusOptions[value]!,
          onChanged: (value) =>
              bloc.add(ApplicationsFiltersChanged(state.filters.copyWith(statusKey: value))),
        ),
      ),
      const TableColumnSpec(label: 'Latest', width: FlexColumnWidth(0.9), sortKey: 'latest'),
      const TableColumnSpec(label: 'Upgrade', width: FlexColumnWidth(1.2)),
      const TableColumnSpec(label: 'Checked', width: FlexColumnWidth(1), sortKey: 'checked'),
      const TableColumnSpec(label: 'Actions', width: FixedColumnWidth(90)),
    ];

    return Column(
      crossAxisAlignment: CrossAxisAlignment.stretch,
      children: [
        KintsugiTable(
          minWidth: 1400,
          columns: columns,
          sort: state.sort == null
              ? null
              : TableSort(state.sort!.key, ascending: state.sort!.ascending),
          onSort: (key) => bloc.add(ApplicationsSortChanged(key)),
          toolbar: Row(
            children: [
              const Expanded(child: HintText('Search and filter from the column headers below.')),
              if (state.filters.isActive)
                SecondaryButton(
                  label: 'Clear Filters',
                  onPressed: () => bloc.add(const ApplicationsFiltersChanged(ApplicationFilters())),
                ),
            ],
          ),
          rows: [
            for (final row in rows)
              KintsugiTableRow(
                isChild: row.isChild,
                cells: _cells(context, row),
                expanded: state.expandedRowKey == row.key
                    ? InstructionsPanel(
                        // Keyed so switching rows rebuilds the panel rather than reusing the
                        // previous row's blocs and controllers.
                        key: ValueKey(row.key),
                        applicationName: row.application.name,
                        platform: row.platform,
                        onServerStateChanged: onServerStateChanged,
                      )
                    : null,
              ),
          ],
        ),
        if (rows.isEmpty)
          const Padding(
            padding: EdgeInsets.only(top: 16),
            child: EmptyPanel('No applications match the current filters.'),
          ),
      ],
    );
  }

  List<Widget> _cells(BuildContext context, ApplicationTableRow row) {
    final path = row.upgradePath;
    final expanded = state.expandedRowKey == row.key;

    return [
      Text(
        row.isChild ? '> ${row.application.name}' : row.application.name,
        style: row.isChild
            ? Theme.of(context).textTheme.bodyMedium?.copyWith(color: context.palette.muted)
            : null,
      ),
      CountBadge(row.application.hostCount),
      path == null ? const NoValue() : HintText(path.platform),
      _StatusCell(row: row),
      HintText(path?.latestVersion ?? '—'),
      _UpgradeCell(row: row),
      path == null ? const NoValue() : LocalTimestamp(path.checkedUtc),
      IconActionButton(
        icon: expanded ? Icons.expand_less : Icons.expand_more,
        tooltip: 'AI instructions',
        onPressed: () =>
            context.read<ApplicationsBloc>().add(ApplicationRowExpansionToggled(row.key)),
      ),
    ];
  }
}

class _StatusCell extends StatelessWidget {
  const _StatusCell({required this.row});

  final ApplicationTableRow row;

  @override
  Widget build(BuildContext context) {
    if (row.upgradePath == null) return const HintText('Not checked yet');

    // Labelled from the server's own statusKey rather than re-derived, so the chip, the filter and
    // the server all agree on what this row is.
    final label = switch (row.statusKey) {
      'check-failed' => 'Check Failed',
      'not-found' => 'No Known Path',
      'review-sign' => 'Review And Sign',
      'update-available' => 'Update Available',
      'up-to-date' => 'Up To Date',
      _ => 'Not Checked Yet',
    };

    return StatusChip(label, statusKey: row.statusKey);
  }
}

/// The Upgrade column, which shows a different thing per upgrade method.
class _UpgradeCell extends StatelessWidget {
  const _UpgradeCell({required this.row});

  final ApplicationTableRow row;

  @override
  Widget build(BuildContext context) {
    final path = row.upgradePath;
    if (path == null) return const NoValue();

    final primary = switch (path.method) {
      UpgradeMethod.directDownload when path.downloadUrl != null => LinkText(
          label: 'Download',
          onTap: () => locator<PageNavigator>().go(path.downloadUrl!),
        ),
      UpgradeMethod.packageManagerCommand when path.command != null => CodeText(path.command!),
      UpgradeMethod.manualSteps when path.instructions != null =>
        _ManualSteps(instructions: path.instructions!),
      UpgradeMethod.script when path.script != null => LinkText(
          label: 'View script',
          onTap: () => showScriptDialog(
            context,
            applicationName: row.application.name,
            platform: path.platform,
            script: path.script!,
          ),
        ),
      _ => HintText(path.notes ?? 'No reliable information found.'),
    };

    if (path.sourceUrl == null) return primary;

    return Column(
      crossAxisAlignment: CrossAxisAlignment.start,
      children: [
        primary,
        const SizedBox(height: 2),
        LinkText(
          label: 'source',
          muted: true,
          onTap: () => locator<PageNavigator>().go(path.sourceUrl!),
        ),
      ],
    );
  }
}

class _ManualSteps extends StatelessWidget {
  const _ManualSteps({required this.instructions});

  final String instructions;

  @override
  Widget build(BuildContext context) => Theme(
        data: Theme.of(context).copyWith(dividerColor: Colors.transparent),
        child: ExpansionTile(
          tilePadding: EdgeInsets.zero,
          childrenPadding: EdgeInsets.zero,
          title: Text('View steps', style: Theme.of(context).textTheme.bodyMedium),
          children: [SelectableText(instructions, style: Theme.of(context).textTheme.bodySmall)],
        ),
      );
}

class _SearchField extends StatefulWidget {
  const _SearchField({required this.value, required this.onChanged});

  final String value;
  final ValueChanged<String> onChanged;

  @override
  State<_SearchField> createState() => _SearchFieldState();
}

class _SearchFieldState extends State<_SearchField> {
  late final _controller = TextEditingController(text: widget.value);

  @override
  void dispose() {
    _controller.dispose();
    super.dispose();
  }

  @override
  Widget build(BuildContext context) => KintsugiTextField(
        controller: _controller,
        hintText: 'Search...',
        onChanged: widget.onChanged,
      );
}

class _LoadingPanel extends StatelessWidget {
  const _LoadingPanel();

  @override
  Widget build(BuildContext context) => const KintsugiPanel(
        padding: EdgeInsets.symmetric(vertical: 48),
        child: Center(
          child: SizedBox(width: 24, height: 24, child: CircularProgressIndicator(strokeWidth: 2)),
        ),
      );
}
