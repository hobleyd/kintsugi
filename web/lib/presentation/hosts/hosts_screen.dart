import 'package:flutter/material.dart';
import 'package:flutter_bloc/flutter_bloc.dart';
import 'package:go_router/go_router.dart';

import '../../core/di/locator.dart';
import '../../core/router/app_router.dart';
import '../../core/theme/kintsugi_palette.dart';
import '../../core/widgets/alert_box.dart';
import '../../core/widgets/buttons.dart';
import '../../core/widgets/kintsugi_table.dart';
import '../../core/widgets/page_scaffold.dart';
import '../../core/widgets/panel.dart';
import '../../core/widgets/status_chip.dart';
import '../../core/widgets/text_bits.dart';
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
    final columns = <TableColumnSpec>[
      const TableColumnSpec(label: 'Hostname', width: FlexColumnWidth(1.4)),
      const TableColumnSpec(label: 'Serial Number', width: FlexColumnWidth(1.2)),
      const TableColumnSpec(label: 'Operating System', width: FlexColumnWidth(1.2)),
      const TableColumnSpec(label: 'OS Update', width: FlexColumnWidth(1.2)),
      const TableColumnSpec(label: 'App Updates', width: FixedColumnWidth(130), alignRight: true),
      const TableColumnSpec(label: 'IP Address', width: FlexColumnWidth(1)),
      const TableColumnSpec(label: 'Status', width: FixedColumnWidth(140)),
      const TableColumnSpec(label: 'Last Seen', width: FlexColumnWidth(1)),
      // Two icons now (Connect and Remove), so wider than the 110 that fitted one plus the header
      // word "ACTIONS". `KintsugiTable` floors the width at the header's own — this is comfortably
      // above it either way.
      const TableColumnSpec(label: 'Actions', width: FixedColumnWidth(150)),
    ];

    return PageScaffold(
      title: 'Registered Hosts',
      subtitle: '${state.hosts.length} host(s) registered',
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
        else
          KintsugiTable(
            columns: columns,
            minWidth: 1180,
            rows: [
              for (final host in state.hosts)
                KintsugiTableRow(cells: _cells(context, host, state)),
            ],
          ),
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
        host.ipAddress == null ? const NoValue() : Text(host.ipAddress!),
        Wrap(
          spacing: 6,
          runSpacing: 4,
          children: [
            StatusChip(host.status.label, statusKey: host.status.key),
            if (host.removalRequested) const StatusChip('Removing', statusKey: 'update-available'),
          ],
        ),
        LocalTimestamp(host.lastSeenUtc),
        Wrap(
          spacing: 4,
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
