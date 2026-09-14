import 'package:flutter/material.dart';
import 'package:flutter_bloc/flutter_bloc.dart';

import '../../core/di/locator.dart';
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
              checkUpdate: locator<CheckApplicationUpdate>(),
              forcePatchRuns: locator<RequestForcedPatchRuns>(),
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
        // The titles are the buttons' own labels, because the panel these are recorded into is
        // read from other screens and "the run that failed" has to be identifiable there.
        RunProgressView<UpgradePathScanBloc>(
          title: 'Find Upgrade Paths',
          source: 'Installed Applications',
          onFinished: reload,
        ),
        RunProgressView<UpdateCheckBloc>(
          title: 'Check for Updates',
          source: 'Installed Applications',
          onFinished: reload,
        ),
        Text(
          '${state.overview.totalApplicationCount} distinct application(s) reported across all hosts',
          style: Theme.of(context).textTheme.bodyMedium?.copyWith(color: context.palette.muted),
        ),
        const SizedBox(height: 20),
        if (state.error != null) AlertBox.error(state.error!),
        if (state.checkNotice case final notice?)
          switch (notice) {
            UpdateCheckNotice(success: true) => AlertBox.success(notice.message),
            // A row with no script to run is not a row that failed: red here said the check broke
            // when nothing was ever attempted.
            UpdateCheckNotice(skipped: true) => AlertBox.info(notice.message),
            _ => AlertBox.error(notice.message),
          },
        if (state.forceNotice case final notice?)
          notice.success ? AlertBox.success(notice.message) : AlertBox.error(notice.message),
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
        filter: SearchField(
          value: state.filters.search,
          onChanged: (value) =>
              bloc.add(ApplicationsFiltersChanged(state.filters.copyWith(search: value))),
        ),
      ),
      TableColumnSpec(
        label: 'Hosts',
        width: const FixedColumnWidth(150),
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
      TableColumnSpec(
        label: 'Platform',
        width: const FlexColumnWidth(1),
        sortKey: 'platform',
        filter: KintsugiDropdown<String>(
          value: state.filters.platform,
          items: ['all', ...state.platformOptions],
          labelOf: (value) => value == 'all' ? 'All platforms' : value,
          onChanged: (value) =>
              bloc.add(ApplicationsFiltersChanged(state.filters.copyWith(platform: value))),
        ),
      ),
      TableColumnSpec(
        label: 'Status',
        width: const FixedColumnWidth(160),
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
      // 110 rather than the 90 this started as, which is what one 34px icon needs and not what
      // the word "ACTIONS" over it does. `KintsugiTable` floors a column at its own label either
      // way; the number here says so out loud rather than being quietly overridden.
      const TableColumnSpec(label: 'Actions', width: FixedColumnWidth(110)),
    ];

    return Column(
      crossAxisAlignment: CrossAxisAlignment.stretch,
      children: [
        KintsugiTable(
          // Eight columns, and the widest table in the product. 1180 rather than the 1400 this
          // started as, which was 226px wider than the panel on a 1512-point display — so the
          // last two columns were reachable only by finding the panel's own horizontal scrollbar,
          // which on web is not drawn until something scrolls. Two things paid for the
          // difference: the 12px cell gutter costs 96px less across eight columns, and the table
          // now takes the panel's full width rather than laying out at exactly this figure, so
          // every column is wider than this arithmetic whenever the window allows. What is left
          // is a real floor — below this the version and timestamp columns start wrapping their
          // one value onto two lines. The expanded instructions panel does not bear on it: it is
          // spliced in at the table's full width, not laid out in a column.
          //
          // It was 1100 until the Upgrade column gained a third icon ("Patch now", see
          // [_ForcePatchNowButton]). That column is FlexColumnWidth(1.2) of 5.7, so a 34px icon
          // costs about 160px of table, and at 1100 the three icons overflowed their cell by 16px
          // — `test/presentation/diagnostics_panel_test.dart` is what says so, since it lays this
          // table out beside the 320px diagnostics panel, which is the narrowest this table is
          // ever asked to be. Adding a fourth icon here means doing this arithmetic again.
          minWidth: 1180,
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
                expanded: row.offersInstructions && state.expandedRowKey == row.key
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
      _NameCell(row: row, childrenShown: state.expandedManagerNames.contains(row.application.name)),
      CountBadge(row.application.hostCount),
      path == null ? const NoValue() : HintText(path.platform),
      _StatusCell(row: row),
      HintText(path?.latestVersion ?? '—'),
      _UpgradeCell(row: row, checking: state.checkingRowKeys.contains(row.key)),
      path == null ? const NoValue() : LocalTimestamp(path.checkedUtc),
      if (row.offersInstructions)
        IconActionButton(
          icon: expanded ? Icons.expand_less : Icons.expand_more,
          tooltip: 'AI instructions',
          onPressed: () =>
              context.read<ApplicationsBloc>().add(ApplicationRowExpansionToggled(row.key)),
        )
      else
        const NoValue(),
    ];
  }
}

/// The Application Name column: an expander for a package manager with applications under it, and
/// the name.
///
/// Every row spends [_expanderSlotWidth] before its name, whether or not there is an expander in
/// it, so the names line up down the column: a chevron beside "Homebrew" and none beside
/// "Firefox" would otherwise stagger the two by an icon's width. Child rows are indented a further
/// step by [KintsugiTableRow.isChild], which is what puts them visibly under the manager rather
/// than beside it.
class _NameCell extends StatelessWidget {
  const _NameCell({required this.row, required this.childrenShown});

  final ApplicationTableRow row;

  /// Whether the manager's applications are on screen — drives the chevron's direction.
  final bool childrenShown;

  /// One [IconActionButton] (34px) and the gap to the name.
  static const _expanderSlotWidth = 38.0;

  @override
  Widget build(BuildContext context) {
    final count = row.matchingChildCount;
    final plural = count == 1 ? 'application' : 'applications';

    return Row(
      mainAxisSize: MainAxisSize.min,
      children: [
        SizedBox(
          width: _expanderSlotWidth,
          child: count == 0
              ? null
              : Align(
                  alignment: Alignment.centerLeft,
                  child: IconActionButton(
                    icon: childrenShown ? Icons.expand_more : Icons.chevron_right,
                    tooltip: childrenShown ? 'Hide $plural' : 'Show $count $plural',
                    onPressed: () => context
                        .read<ApplicationsBloc>()
                        .add(ApplicationChildrenToggled(row.application.name)),
                  ),
                ),
        ),
        Flexible(
          child: Text(
            row.application.name,
            style: row.isChild
                ? Theme.of(context).textTheme.bodyMedium?.copyWith(color: context.palette.muted)
                : null,
          ),
        ),
      ],
    );
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
  const _UpgradeCell({required this.row, required this.checking});

  final ApplicationTableRow row;

  /// Whether this row's version check is in flight, which swaps the Refresh icon for a spinner.
  final bool checking;

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
      UpgradeMethod.script when path.script != null => Row(
          mainAxisSize: MainAxisSize.min,
          children: [
            // The emergency action, first in the row because it is the one anybody comes to this
            // column in a hurry for. See [_ForcePatchNowButton] for what it does and what it
            // deliberately does not claim.
            _ForcePatchNowButton(row: row),
            // The manager's shared script is shown on the manager's row alone; an application
            // under it keeps the version check, which is its own. See
            // [ApplicationTableRow.usesManagerScript].
            if (!row.usesManagerScript)
              IconActionButton(
                icon: Icons.description_outlined,
                tooltip: 'View script',
                onPressed: () => showScriptDialog(
                  context,
                  applicationName: row.application.name,
                  platform: path.platform,
                  script: path.script!,
                ),
              ),
            // The per-row form of "Check for Updates": runs this one script's --update-version on
            // the server, synchronously, and no AI is involved. The Latest and Checked columns
            // move when the overview is re-read; the notice above the table says what happened
            // when they do not.
            IconActionButton(
              icon: Icons.refresh,
              tooltip: checking ? 'Checking for a new version' : 'Check for a new version',
              busy: checking,
              onPressed: () =>
                  context.read<ApplicationsBloc>().add(ApplicationUpdateCheckRequested(row)),
            ),
          ],
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

/// "Patch now": tells every host this row is currently filtered to to run this application's
/// upgrade script at its next opportunity, instead of waiting for its own patching cycle.
///
/// Four things about it are deliberate.
///
/// **It is confirmed, and the dialog names the hosts.** This is the only control on this screen
/// that reaches out and changes managed machines, and the set it changes is decided by filters set
/// elsewhere on the page — so the confirmation states how many hosts and which ones, because
/// "currently filtered to" is not something an operator can check by looking at the icon.
///
/// **It is disabled when there is nothing to force**, with the tooltip saying which of the two
/// reasons applies. An agent only patches a row its work list reports an update available for
/// (`upgrade::is_patchable` in all three), so an enabled button that quietly instructs hosts to do
/// nothing would be worse than one that says why it cannot.
///
/// **It promises an instruction, not an outcome.** Nothing is pushed to a host — every agent polls
/// — so the dialog says when each kind of host will act and that the five-minute warning it then
/// shows cannot be delayed. That last clause is the feature: an ordinary cycle offers "Delay", and
/// this one does not.
///
/// **It cannot run an unsigned script.** The instruction carries a name and nothing else; the agent
/// re-fetches its work list and re-verifies the signature before running anything. Forcing is an
/// urgency override, never a trust override.
class _ForcePatchNowButton extends StatelessWidget {
  const _ForcePatchNowButton({required this.row});

  final ApplicationTableRow row;

  @override
  Widget build(BuildContext context) {
    final state = context.watch<ApplicationsBloc>().state;
    final hostNames = state.forcedRunHostNamesFor(row);
    final signed = row.upgradePath?.isSigned ?? false;

    final reason = switch ((signed, hostNames.isEmpty)) {
      (false, _) => 'This script is not signed yet, so no agent will run it.',
      (_, true) => 'No host matching the current filters is behind on this application.',
      _ => null,
    };

    return IconActionButton(
      icon: Icons.bolt,
      busy: state.forcingRowKeys.contains(row.key),
      tooltip: reason ?? 'Patch now on ${_hostSummary(hostNames)}',
      onPressed: reason != null ? null : () => _confirm(context, hostNames),
    );
  }

  /// "3 host(s)" when the filter names none in particular, and the host's own name when it does —
  /// which is the whole point of the tooltip on a screen where the target set is decided elsewhere.
  static String _hostSummary(List<String> hostNames) =>
      hostNames.length == 1 ? hostNames.single : '${hostNames.length} host(s)';

  Future<void> _confirm(BuildContext context, List<String> hostNames) async {
    final bloc = context.read<ApplicationsBloc>();

    // Named in full up to a point, then counted. A dialog listing four hundred hostnames is one
    // nobody reads, and the number is the part that matters once the list stops being checkable.
    const listed = 12;
    final names = hostNames.length <= listed
        ? hostNames.join(', ')
        : '${hostNames.take(listed).join(', ')} and ${hostNames.length - listed} more';

    final confirmed = await showDialog<bool>(
      context: context,
      builder: (dialogContext) => AlertDialog(
        backgroundColor: dialogContext.palette.backgroundAlt,
        shape: RoundedRectangleBorder(
          borderRadius: BorderRadius.circular(6),
          side: BorderSide(color: dialogContext.palette.border),
        ),
        title: Text(
          'Patch ${row.application.name} now on ${_hostSummary(hostNames)}?',
          style: Theme.of(dialogContext).textTheme.titleLarge,
        ),
        content: HintText(
          'This is the emergency action. $names will run this upgrade at their next check — within '
          'a minute on a host somebody is logged in to, at the next hourly check-in on a server '
          'with nobody on it — rather than waiting for their own patching cycle.\n\n'
          'Each host shows a five-minute warning first. Unlike a scheduled cycle, the person at the '
          'keyboard cannot delay it.',
        ),
        actions: [
          SecondaryButton(label: 'Cancel', onPressed: () => Navigator.of(dialogContext).pop(false)),
          PrimaryButton(label: 'Patch Now', onPressed: () => Navigator.of(dialogContext).pop(true)),
        ],
      ),
    );

    if (confirmed == true) bloc.add(ApplicationForcedPatchRunRequested(row));
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
          // Text rather than SelectableText — see the note in script_dialog.dart; the app-wide
          // SelectionArea covers this, and a nested one would break a drag across the panel.
          children: [Text(instructions, style: Theme.of(context).textTheme.bodySmall)],
        ),
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
