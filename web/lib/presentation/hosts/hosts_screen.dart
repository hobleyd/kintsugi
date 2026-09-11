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
import '../../domain/entities/enums.dart';
import '../../domain/entities/host.dart';
import '../../domain/usecases/host_usecases.dart';
import 'hosts_bloc.dart';

/// Every host in the fleet. What `Pages/Hosts.cshtml` was.
class HostsScreen extends StatelessWidget {
  const HostsScreen({super.key});

  @override
  Widget build(BuildContext context) => BlocProvider(
        create: (_) => HostsBloc(
          getHosts: locator<GetHosts>(),
          requestHostRemoval: locator<RequestHostRemoval>(),
        )..add(const HostsRequested()),
        child: const _HostsView(),
      );
}

class _HostsView extends StatelessWidget {
  const _HostsView();

  @override
  Widget build(BuildContext context) =>
      BlocBuilder<HostsBloc, HostsState>(builder: (context, state) => _buildBody(context, state));

  Widget _buildBody(BuildContext context, HostsState state) {
    final bloc = context.read<HostsBloc>();
    final columns = <TableColumnSpec>[
      TableColumnSpec(
        label: 'Hostname',
        width: const FlexColumnWidth(1.4),
        // Under the Hostname header, where the Applications table keeps its own search, though it
        // matches serial numbers and addresses too — see `HostsState.visibleHosts`. A fleet of a
        // few hundred hosts is past scrolling for one, and the hostname is what somebody arrives
        // knowing.
        filter: SearchField(
          value: state.search,
          hintText: 'Search hosts...',
          onChanged: (value) => bloc.add(HostsSearchChanged(value)),
        ),
      ),
      const TableColumnSpec(label: 'Serial Number', width: FlexColumnWidth(1.2)),
      const TableColumnSpec(label: 'Operating System', width: FlexColumnWidth(1.2)),
      const TableColumnSpec(label: 'OS Update', width: FlexColumnWidth(1.2)),
      const TableColumnSpec(label: 'App Updates', width: FixedColumnWidth(130), alignRight: true),
      // CVEs a host is confirmed to have cleared versus everything else — see
      // `HostSummary.unpatchedCveCount`/`patchedCveCount`. "Unpatched" is the wider bucket: an
      // application with no researched/confirmed-current verdict lands here rather than being
      // dropped as a coverage gap. A distribution package has no upgrade path of its own, but
      // apt/dnf patches the OS and every package it shipped in one pull, so a package's verdict
      // is the host's own OS Update column — current there means every package counts as
      // patched too, not just the OS itself. These two need not sum to any other column here.
      const TableColumnSpec(label: 'Unpatched CVEs', width: FixedColumnWidth(130), alignRight: true),
      const TableColumnSpec(label: 'Patched CVEs', width: FixedColumnWidth(130), alignRight: true),
      const TableColumnSpec(label: 'IP Address', width: FlexColumnWidth(1)),
      // Measured rather than guessed, because "Decommissioned" is one fourteen-character word and
      // a chip cannot be made narrower than that: at a hand-set 140 it broke mid-word, the way
      // the CVE Reporting table's Exploited column did. `KintsugiTable` floors a column at its
      // *header* word only, so nothing else would have caught it.
      TableColumnSpec(
        label: 'Status',
        width: TableColumnSpec.forContent(
          HostStatus.values
              .map((status) => StatusChip.widthFor(context, status.label))
              .reduce(math.max),
        ),
      ),
      const TableColumnSpec(label: 'Last Seen', width: FlexColumnWidth(1)),
      // Three icons now (Connect, Terminal and Remove). The arithmetic is the reason this is not
      // still 150: three 40px buttons plus two 24px gaps is 168, which is more than the 126 that
      // width left after the gutters — and a `Wrap` that cannot fit its children does not shrink
      // them, it breaks to a second line and makes every row in the table taller.
      // `KintsugiTable` floors the width at the header's own, which is comfortably below this.
      const TableColumnSpec(label: 'Actions', width: FixedColumnWidth(200)),
    ];

    final visible = state.visibleHosts;

    return PageScaffold(
      title: 'Registered Hosts',
      subtitle: state.search.isEmpty
          ? '${state.hosts.length} host(s) registered'
          : '${visible.length} of ${state.hosts.length} host(s) match the search',
      children: [
        if (state.error != null) AlertBox.error(state.error!),
        if (state.notice != null) AlertBox.success(state.notice!),
        if (state.loading && state.hosts.isEmpty)
          const _Loading()
        else if (state.hosts.isEmpty)
          const EmptyPanel(
            'No hosts have been registered yet. A host appears here once its agent enrolls and '
            'reports in.',
          )
        else ...[
          // The table stays on screen when the search matches nothing, because the search box is
          // in its header — swapping the whole table for an empty panel would take the box away
          // with the text still in it.
          KintsugiTable(
            columns: columns,
            minWidth: 1440,
            rows: [
              for (final host in visible) KintsugiTableRow(cells: _cells(context, host, state)),
            ],
          ),
          if (visible.isEmpty)
            const Padding(
              padding: EdgeInsets.only(top: 16),
              child: EmptyPanel('No hosts match the search.'),
            ),
        ],
      ],
    );
  }

  static List<Widget> _cells(BuildContext context, HostSummary host, HostsState state) => [
        Text(host.hostname),
        CodeText(host.serialNumber),
        host.operatingSystem == null ? const NoValue() : Text(host.operatingSystem!),
        _OsUpdateCell(host: host),
        host.appUpdatesAvailableCount > 0
            ? CountBadge(
                host.appUpdatesAvailableCount,
                alert: true,
                tooltip: 'View applications requiring an update on ${host.hostname}',
                // The same deep link the old badge was an <a> to. It carries both filters,
                // because "update available" on its own is fleet-wide and would list every
                // application anyone is behind on rather than this host's.
                onTap: () => context.go(
                  Uri(
                    path: Routes.applications,
                    queryParameters: {'status': 'update-available', 'host': host.hostname},
                  ).toString(),
                ),
              )
            : CountBadge(host.appUpdatesAvailableCount),
        CountBadge(
          host.unpatchedCveCount,
          alert: host.unpatchedCveCount > 0,
          tooltip: 'CVEs on ${host.hostname} not confirmed fixed: applications with an update '
              'available or never researched, and the OS (and every distribution package it '
              'shipped) when an OS update is pending or has never been checked',
        ),
        CountBadge(
          host.patchedCveCount,
          tooltip: 'CVEs on ${host.hostname} against applications, or against the OS and its '
              'packages when no OS update is pending, confirmed already up to date — patching '
              'would not remove these',
        ),
        host.ipAddress == null ? const NoValue() : Text(host.ipAddress!),
        // Centred on the chip, not on the column. The cell aligns its child left under loose
        // constraints, so this Column is only as wide as its widest child — the chip — and
        // `center` puts the version under the middle of it. Not `IntrinsicWidth`: on web the
        // chip's intrinsic width comes out narrower than it lays out at and its label breaks
        // mid-word ("ONLIN / E").
        Column(
          crossAxisAlignment: CrossAxisAlignment.center,
          children: [
            Wrap(
              spacing: 6,
              runSpacing: 4,
              children: [
                StatusChip(host.status.label, statusKey: host.status.key),
                if (host.removalRequested)
                  const StatusChip('Removing', statusKey: 'update-available'),
              ],
            ),
            // The agent's own version sits under the status rather than in its own column: it is
            // a property of the same check-in the chip summarises, and a column for a string that
            // is null on every host predating `HostDto.AgentVersion` would mostly show dashes.
            // Same arrangement `_OsUpdateCell` uses for the latest OS version.
            if (host.agentVersion != null)
              Padding(
                padding: const EdgeInsets.only(top: 4),
                child: HintText('(${host.agentVersion})'),
              ),
          ],
        ),
        LocalTimestamp(host.lastSeenUtc),
        Wrap(
          // Deliberately wide: Remove is the only one of the three that is not reversible from
          // here, so a slip into it costs a host rather than a mis-click. Three 40px buttons plus
          // two of these gaps is 168, inside the 176 the fixed 200 column leaves after the
          // gutters, so the Wrap never breaks onto a second line.
          spacing: 24,
          children: [
            IconActionButton(
              icon: Icons.desktop_windows_outlined,
              tooltip: 'Connect to this host',
              // Offered whatever the host's status says, deliberately. "Online" here means the host
              // checked in within the last interval — up to an hour ago — whereas remote control
              // additionally needs an agent holding a socket right now, which means somebody logged
              // in. Only the server can answer that, and it answers by returning a session already
              // marked unreachable, which the remote-control screen explains. Disabling the button
              // on a stale status would hide a working host instead.
              onPressed: host.removalRequested
                  ? null
                  : () => context.go(
                        Uri(
                          path: Routes.remoteControl(host.id),
                          queryParameters: {'hostname': host.hostname},
                        ).toString(),
                      ),
            ),
            IconActionButton(
              icon: Icons.terminal_outlined,
              tooltip: 'Open a terminal on this host',
              // Offered on the same terms as Connect, and for the same reason: only the server can
              // say whether an agent is holding a socket right now. It is offered on a host with
              // nobody logged in as well, which is not true of a screen session — a terminal needs
              // no desktop, and on Linux and Windows a host with nobody signed in can still provide
              // one. macOS cannot: its agent's per-user process is the half holding the identity,
              // so a Mac with nobody logged in is unreachable for either kind.
              onPressed: host.removalRequested
                  ? null
                  : () => context.go(
                        Uri(
                          path: Routes.remoteShell(host.id),
                          queryParameters: {'hostname': host.hostname},
                        ).toString(),
                      ),
            ),
            IconActionButton(
              icon: Icons.delete_outline,
              danger: true,
              tooltip: 'Remove host',
              onPressed: state.removingId == host.id ? null : () => _confirmRemoval(context, host),
            ),
          ],
        ),
      ];

  static Future<void> _confirmRemoval(BuildContext context, HostSummary host) async {
    final bloc = context.read<HostsBloc>();
    final confirmed = await showDialog<bool>(
      context: context,
      builder: (dialogContext) => AlertDialog(
        backgroundColor: dialogContext.palette.backgroundAlt,
        shape: RoundedRectangleBorder(
          borderRadius: BorderRadius.circular(6),
          side: BorderSide(color: dialogContext.palette.border),
        ),
        title: Text('Remove ${host.hostname}?', style: Theme.of(dialogContext).textTheme.titleLarge),
        content: const HintText(
          'Its agent will be instructed to uninstall itself completely on its next check-in. The '
          'host record stays until the agent confirms it has done so.',
        ),
        actions: [
          SecondaryButton(label: 'Cancel', onPressed: () => Navigator.of(dialogContext).pop(false)),
          PrimaryButton(label: 'Remove', onPressed: () => Navigator.of(dialogContext).pop(true)),
        ],
      ),
    );

    if (confirmed == true) bloc.add(HostRemovalRequested(host));
  }
}

class _OsUpdateCell extends StatelessWidget {
  const _OsUpdateCell({required this.host});

  final HostSummary host;

  @override
  Widget build(BuildContext context) {
    // Tri-state, and the third state is not a rounding of the other two: null means the host has
    // never reported an OS update check, which is different from being up to date.
    return switch (host.operatingSystemUpdateAvailable) {
      true => Column(
          crossAxisAlignment: CrossAxisAlignment.start,
          children: [
            const StatusChip('Update Available', statusKey: 'update-available'),
            if (host.operatingSystemLatestVersion != null)
              Padding(
                padding: const EdgeInsets.only(top: 4),
                child: HintText(host.operatingSystemLatestVersion!),
              ),
          ],
        ),
      false => const StatusChip('Up To Date', statusKey: 'up-to-date'),
      null => const StatusChip('Not Checked', statusKey: 'unknown'),
    };
  }
}

class _Loading extends StatelessWidget {
  const _Loading();

  @override
  Widget build(BuildContext context) => KintsugiPanel(
        padding: const EdgeInsets.symmetric(vertical: 48),
        child: Center(
          child: SizedBox(
            width: 24,
            height: 24,
            child: CircularProgressIndicator(strokeWidth: 2, color: context.palette.neon),
          ),
        ),
      );
}
