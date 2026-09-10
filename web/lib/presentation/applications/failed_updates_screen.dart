import 'package:flutter/material.dart';
import 'package:flutter_bloc/flutter_bloc.dart';

import '../../core/di/locator.dart';
import '../../core/theme/app_theme.dart';
import '../../core/theme/kintsugi_palette.dart';
import '../../core/widgets/alert_box.dart';
import '../../core/widgets/buttons.dart';
import '../../core/widgets/form_bits.dart';
import '../../core/widgets/kintsugi_table.dart';
import '../../core/widgets/page_scaffold.dart';
import '../../core/widgets/panel.dart';
import '../../core/widgets/status_chip.dart';
import '../../core/widgets/text_bits.dart';
import '../../domain/entities/patch_failure.dart';
import '../../domain/usecases/patch_failure_usecases.dart';
import 'failed_updates_bloc.dart';
import 'widgets/instructions_panel.dart';

/// Every upgrade an agent tried to apply and could not — the other half of the Applications menu.
///
/// The Applications screen answers "what is installed and what is available"; this one answers
/// "what did the fleet try to do and fail at". A row expands into the same [InstructionsPanel] the
/// Applications screen uses, so repairing a script here is the identical flow — edit the
/// instructions or the script, send to the AI, save, sign — with one difference: the instructions
/// arrive already carrying the failure's own output and the current script, composed server-side.
class FailedUpdatesScreen extends StatelessWidget {
  const FailedUpdatesScreen({super.key});

  @override
  Widget build(BuildContext context) => BlocProvider(
        create: (_) => FailedUpdatesBloc(
          getFailures: locator<GetPatchFailures>(),
          dismissFailure: locator<DismissPatchFailure>(),
        )..add(const FailedUpdatesRequested()),
        child: const _FailedUpdatesView(),
      );
}

class _FailedUpdatesView extends StatelessWidget {
  const _FailedUpdatesView();

  @override
  Widget build(BuildContext context) =>
      const BlocBuilder<FailedUpdatesBloc, FailedUpdatesState>(builder: _build);

  static Widget _build(BuildContext context, FailedUpdatesState state) {
    void reload() =>
        context.read<FailedUpdatesBloc>().add(const FailedUpdatesRequested(showSpinner: false));

    return PageScaffold(
      title: 'Failed Updates',
      children: [
        SectionHeader(
          title: 'Upgrades That Failed On A Host',
          hints: const [
            HintText(
              'An agent reports here when an upgrade script or package-manager command actually ran '
              'on a host and failed. Repeated failures of the same application on the same host fold '
              'into one row, with the count and the date it was first seen, so a script that is '
              'broken shows up once rather than once per patch cycle.',
            ),
            HintText(
              'Expand a row to repair it. For an AI-researched script the instructions box opens '
              'carrying the failure\'s own output and the script as it stands now, so "Send to AI" '
              'asks for a fix rather than fresh research. A package-manager script (Homebrew, '
              'winget, Flatpak and the rest) is one fixed text shared across that manager, so no AI '
              'is offered for it — edit it by hand instead. Either way the result is unsigned until '
              'somebody reviews and signs it, so no host runs it before then.',
            ),
            HintText(
              'A row clears itself when that host next reports the application patched successfully. '
              '"Dismiss" is for the ones that cannot recur — a rebuilt host, an application since '
              'uninstalled.',
            ),
          ],
          actions: [
            SecondaryButton(label: 'Reload', onPressed: reload),
          ],
        ),
        Text(
          state.loading && state.failures.isEmpty
              ? 'Loading...'
              : '${state.outstandingCount} outstanding failure(s) across the fleet',
          style: Theme.of(context).textTheme.bodyMedium?.copyWith(color: context.palette.muted),
        ),
        const SizedBox(height: 20),
        if (state.error case final error? when error.isNotEmpty) AlertBox.error(error),
        if (state.notice case final notice? when notice.isNotEmpty) AlertBox.success(notice),
        if (state.loading && state.failures.isEmpty)
          const EmptyPanel('Loading failed updates...')
        else if (state.failures.isEmpty)
          const EmptyPanel(
            'No agent has reported a failed update. Nothing has gone wrong, or nothing has been '
            'patched yet.',
          )
        else
          _FailuresTable(state: state, onServerStateChanged: reload),
      ],
    );
  }
}

class _FailuresTable extends StatelessWidget {
  const _FailuresTable({required this.state, required this.onServerStateChanged});

  final FailedUpdatesState state;
  final VoidCallback onServerStateChanged;

  @override
  Widget build(BuildContext context) {
    final bloc = context.read<FailedUpdatesBloc>();
    final rows = state.visibleRows;

    final columns = [
      TableColumnSpec(
        label: 'Application',
        width: const FlexColumnWidth(1.4),
        filter: SearchField(
          value: state.filters.search,
          hintText: 'Search name, host or output...',
          onChanged: (value) =>
              bloc.add(FailedUpdatesFiltersChanged(state.filters.copyWith(search: value))),
        ),
      ),
      TableColumnSpec(
        label: 'Host',
        width: const FlexColumnWidth(1.2),
        filter: KintsugiDropdown<String>(
          value: state.filters.hostName,
          items: ['all', ...state.allHostNames],
          labelOf: (value) => value == 'all' ? 'All hosts' : value,
          onChanged: (value) =>
              bloc.add(FailedUpdatesFiltersChanged(state.filters.copyWith(hostName: value))),
        ),
      ),
      const TableColumnSpec(label: 'Platform', width: FlexColumnWidth(0.9)),
      const TableColumnSpec(label: 'Versions', width: FlexColumnWidth(1)),
      TableColumnSpec(
        label: 'Status',
        width: const FixedColumnWidth(160),
        filter: KintsugiDropdown<String>(
          value: state.filters.resolutionKey,
          items: failedUpdateResolutionOptions.keys.toList(),
          labelOf: (value) => failedUpdateResolutionOptions[value]!,
          onChanged: (value) =>
              bloc.add(FailedUpdatesFiltersChanged(state.filters.copyWith(resolutionKey: value))),
        ),
      ),
      // "Last Failed" over a timestamp, which is the date the whole screen sorts and reads on —
      // the API carries it as `lastFailedUtc`, rendered in the browser's own timezone.
      const TableColumnSpec(label: 'Last Failed', width: FlexColumnWidth(1.1)),
      const TableColumnSpec(label: 'Actions', width: FixedColumnWidth(120)),
    ];

    return Column(
      crossAxisAlignment: CrossAxisAlignment.stretch,
      children: [
        KintsugiTable(
          // Seven columns. The Versions cell carries two version strings stacked, and the output
          // preview in the Application cell is what wants the rest — below this the timestamp
          // column starts wrapping one value onto two lines.
          minWidth: 1050,
          columns: columns,
          toolbar: Row(
            children: [
              const Expanded(child: HintText('Search and filter from the column headers below.')),
              if (state.filters.isActive)
                SecondaryButton(
                  label: 'Clear Filters',
                  onPressed: () =>
                      bloc.add(const FailedUpdatesFiltersChanged(FailedUpdateFilters())),
                ),
            ],
          ),
          rows: [
            for (final failure in rows)
              KintsugiTableRow(
                cells: _cells(context, failure),
                expanded: state.expandedId == failure.id ? _FixPanel(failure: failure, onServerStateChanged: onServerStateChanged) : null,
              ),
          ],
        ),
        if (rows.isEmpty)
          const Padding(
            padding: EdgeInsets.only(top: 16),
            child: EmptyPanel('No failures match the current filters.'),
          ),
      ],
    );
  }

  List<Widget> _cells(BuildContext context, PatchFailure failure) {
    final bloc = context.read<FailedUpdatesBloc>();
    final expanded = state.expandedId == failure.id;
    final dismissing = state.dismissingIds.contains(failure.id);

    return [
      _ApplicationCell(failure: failure),
      Text(failure.hostname),
      failure.platform == null ? const NoValue() : HintText(failure.platform!),
      _VersionsCell(failure: failure),
      _StatusCell(failure: failure),
      LocalTimestamp(failure.lastFailedUtc),
      Row(
        mainAxisSize: MainAxisSize.min,
        children: [
          IconActionButton(
            icon: expanded ? Icons.expand_less : Icons.expand_more,
            tooltip: failure.canFix
                ? 'Repair this script'
                : 'Show the failure (no stored script to repair)',
            onPressed: () => bloc.add(FailedUpdateRowExpansionToggled(failure.id)),
          ),
          if (failure.isOutstanding)
            IconActionButton(
              icon: Icons.check,
              tooltip: dismissing ? 'Dismissing...' : 'Dismiss this failure',
              onPressed: dismissing ? null : () => bloc.add(FailedUpdateDismissed(failure.id)),
            ),
        ],
      ),
    ];
  }
}

/// The application's name, with the first line of what the run reported under it.
///
/// A preview rather than the whole output: the full text is in the expanded panel, and a cell that
/// grew to a script's entire stderr would make the table unreadable — but a row saying only
/// "Ollama failed" tells a reader nothing they can scan, and scanning is how four hosts failing on
/// the same "Permission denied" gets noticed as one problem.
class _ApplicationCell extends StatelessWidget {
  const _ApplicationCell({required this.failure});

  final PatchFailure failure;

  @override
  Widget build(BuildContext context) => Column(
        crossAxisAlignment: CrossAxisAlignment.start,
        mainAxisSize: MainAxisSize.min,
        children: [
          Text(failure.applicationName),
          const SizedBox(height: 2),
          Text(
            _firstLine(failure.details),
            maxLines: 1,
            overflow: TextOverflow.ellipsis,
            style: AppTheme.mono(color: context.palette.muted, size: 11.5),
          ),
        ],
      );

  static String _firstLine(String details) {
    for (final line in details.split('\n')) {
      final trimmed = line.trim();
      // Skips the truncation notice the agent prepends when it cut the head off — it is about the
      // reporting, not about the failure.
      if (trimmed.isNotEmpty && !trimmed.startsWith('... [truncated')) return trimmed;
    }
    return details.trim();
  }
}

class _VersionsCell extends StatelessWidget {
  const _VersionsCell({required this.failure});

  final PatchFailure failure;

  @override
  Widget build(BuildContext context) {
    final installed = failure.installedVersion;
    final attempted = failure.attemptedVersion;
    if (installed == null && attempted == null) return const NoValue();

    return Text('${installed ?? '?'} → ${attempted ?? '?'}');
  }
}

class _StatusCell extends StatelessWidget {
  const _StatusCell({required this.failure});

  final PatchFailure failure;

  @override
  Widget build(BuildContext context) {
    if (!failure.isOutstanding) {
      return StatusChip(
        failure.resolution.label,
        statusKey: failure.resolution.name == 'patchSucceeded' ? 'patch-succeeded' : 'dismissed',
      );
    }

    // The count, not the word "failed": "failing, 40 times" is what separates a blip from a broken
    // script, and it is the number a reader wants before they open anything.
    return StatusChip(
      failure.isRepeating ? 'Failed ×${failure.failureCount}' : 'Failed',
      statusKey: 'patch-failed',
    );
  }
}

/// What a row expands into: the failure in full, then the same panel the Applications screen uses
/// to research, edit, save and sign a script.
class _FixPanel extends StatelessWidget {
  const _FixPanel({required this.failure, required this.onServerStateChanged});

  final PatchFailure failure;
  final VoidCallback onServerStateChanged;

  @override
  Widget build(BuildContext context) => Column(
        crossAxisAlignment: CrossAxisAlignment.stretch,
        children: [
          _FailureDetails(failure: failure),
          const SizedBox(height: 20),
          if (failure.canFix && failure.isPackageManagerManaged)
            const Padding(
              padding: EdgeInsets.only(bottom: 12),
              child: AlertBox.info(
                'This application is managed by a package manager, so its upgrade script is one '
                'fixed text shared by every application that manager handles — the server writes '
                'it, and no AI is involved. There is no "Send to AI" for it here, because a repair '
                'aimed at one host would change what the whole fleet runs. Edit the script below '
                'and sign it if it needs changing.',
              ),
            ),
          if (failure.canFix)
            InstructionsPanel(
              // Keyed on the failure, so collapsing one row and opening another rebuilds the panel
              // rather than reusing the previous row's blocs and text controllers.
              key: ValueKey(failure.id),
              applicationName: failure.applicationName,
              platform: failure.platform!,
              // What makes this the *repair* flow rather than the research one: the prompt route
              // composes the brief — this failure's output and the current script — server-side and
              // returns it appended to the ordinary instructions.
              patchFailureId: failure.id,
              onServerStateChanged: onServerStateChanged,
            )
          else
            const AlertBox.info(
              'There is no stored upgrade script for this application any more, so there is nothing '
              'to repair here. Research a path for it on the Currently Installed screen first.',
            ),
        ],
      );
}

class _FailureDetails extends StatelessWidget {
  const _FailureDetails({required this.failure});

  final PatchFailure failure;

  @override
  Widget build(BuildContext context) {
    final palette = context.palette;

    return Column(
      crossAxisAlignment: CrossAxisAlignment.start,
      children: [
        const SubHeadingTight('What the run reported'),
        const SizedBox(height: 6),
        Wrap(
          spacing: 18,
          runSpacing: 6,
          children: [
            _Fact(label: 'Host', child: Text(failure.hostname)),
            if (failure.serialNumber case final serial?) _Fact(label: 'Serial', child: CodeText(serial)),
            _Fact(label: 'First failed', child: LocalTimestamp(failure.firstFailedUtc)),
            _Fact(label: 'Last failed', child: LocalTimestamp(failure.lastFailedUtc)),
            _Fact(label: 'Attempts', child: Text('${failure.failureCount}')),
            if (failure.resolvedUtc case final resolved?)
              _Fact(label: failure.resolution.label, child: LocalTimestamp(resolved)),
            if (failure.hasScript)
              _Fact(
                label: 'Stored script',
                child: Text(failure.scriptSigned ? 'Signed' : 'Unsigned — no agent will run it'),
              ),
          ],
        ),
        const SizedBox(height: 12),
        Container(
          width: double.infinity,
          constraints: const BoxConstraints(maxHeight: 260),
          padding: const EdgeInsets.all(14),
          decoration: BoxDecoration(
            color: palette.accentWash(0.04),
            border: Border.all(color: palette.border),
            borderRadius: BorderRadius.circular(3),
          ),
          child: SingleChildScrollView(
            // Text, not SelectableText: the app-wide SelectionArea in main.dart already makes this
            // selectable, and a SelectableText inside one is a selection *island* that a drag
            // starting outside it stops at — which here would exclude the output, the one thing
            // anybody expands this row to copy.
            child: Text(failure.details, style: AppTheme.mono(color: palette.text, size: 12.5)),
          ),
        ),
      ],
    );
  }
}

class _Fact extends StatelessWidget {
  const _Fact({required this.label, required this.child});

  final String label;
  final Widget child;

  @override
  Widget build(BuildContext context) => Row(
        mainAxisSize: MainAxisSize.min,
        children: [
          Text(
            '${label.toUpperCase()}  ',
            style: AppTheme.display(color: context.palette.muted, size: 9.9, letterSpacing: 0.99),
          ),
          child,
        ],
      );
}
