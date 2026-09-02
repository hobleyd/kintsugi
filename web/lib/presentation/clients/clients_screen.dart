import 'package:flutter/material.dart';
import 'package:flutter_bloc/flutter_bloc.dart';

import '../../core/di/injection.dart';
import '../../core/widgets/alert_box.dart';
import '../../core/widgets/buttons.dart';
import '../../core/widgets/kintsugi_table.dart';
import '../../core/widgets/page_scaffold.dart';
import '../../core/widgets/panel.dart';
import '../../core/widgets/status_chip.dart';
import '../../core/widgets/text_bits.dart';
import '../../core/platform/page_navigator.dart';
import '../../data/repositories/agent_package_repository_impl.dart';
import '../../domain/entities/agent_package.dart';
import '../../domain/entities/enums.dart';
import '../../domain/usecases/client_usecases.dart';
import 'clients_bloc.dart';

/// Installable agent packages, and the refresh that pulls newer builds in.
/// What `Pages/Clients.cshtml` was.
class ClientsScreen extends StatelessWidget {
  const ClientsScreen({super.key});

  @override
  Widget build(BuildContext context) => BlocProvider(
        create: (_) => ClientsBloc(
          getClientsView: locator<GetClientsView>(),
          refreshClients: locator<RefreshClients>(),
        )..add(const ClientsRequested()),
        child: const _ClientsView(),
      );
}

class _ClientsView extends StatelessWidget {
  const _ClientsView();

  @override
  Widget build(BuildContext context) =>
      const BlocBuilder<ClientsBloc, ClientsState>(builder: _build);

  static Widget _build(BuildContext context, ClientsState state) {
    final view = state.view;

    return PageScaffold(
      title: 'Clients',
      children: [
        SectionHeader(
          title: 'Agent Packages',
          hints: [
            const HintText(
              'Installable kintsugi-agent packages, one per supported platform. An already-installed '
              'agent checks the latest version here itself and applies it automatically at every '
              'check-in, so these downloads are mainly for a brand-new install.',
            ),
            if (view != null)
              HintText(
                'Builds come from ${view.sourceStatus.sourceDescription}, where CI publishes one '
                'release per agent per version. "Refresh clients" downloads what is newer there and '
                'republishes it here with api_base_url set to ${view.agentApiBaseUrl}, so a '
                'downloaded package needs no editing before it is installed.',
              ),
          ],
          actions: [
            PrimaryButton(
              label: 'Refresh clients',
              busy: state.refreshing,
              onPressed: state.loading
                  ? null
                  : () => context.read<ClientsBloc>().add(const ClientsRefreshRequested()),
            ),
          ],
        ),
        if (state.error != null) AlertBox.error(state.error!),
        if (view != null) ..._notices(view),
        if (state.loading)
          const _LoadingPanel()
        else if (view == null)
          const EmptyPanel('Nothing could be loaded for this screen.')
        else if (view.packages.isEmpty)
          const EmptyPanel('No client packages have been published yet.')
        else
          _PackagesTable(view: view),
      ],
    );
  }

  /// The alerts above the table, in the order the page showed them.
  static List<Widget> _notices(ClientsView view) {
    final notices = <Widget>[];

    if (view.agentApiBaseUrlIsDerived) {
      // Loud rather than quiet, because getting this wrong fails in the worst way the system has:
      // /api/host/enroll sits outside nginx's client-certificate regex, so the agent enrolls,
      // looks installed, and then 403s on every authenticated route forever. Whatever verifies the
      // certificate is nginx itself, so any TLS-terminating hop in front of it — a gateway, a load
      // balancer, a CDN — ends the handshake at itself and the admin UI's own address is the wrong
      // answer.
      notices.add(AlertBox.info(
        'AGENT_API_BASE_URL is not set, so imported packages will be pointed at '
        '${view.requestBaseUrl} — the address this screen was reached on. That is only right if '
        'your agents reach nginx itself there. If anything terminates TLS in front of it, the '
        "agent's client certificate never arrives, and every agent-only route answers 403 after an "
        'enrollment that appeared to succeed. Set AGENT_API_BASE_URL in .env to nginx\'s own '
        'address and port, then refresh.',
      ));
    }

    if (view.refreshError != null) {
      notices.add(AlertBox.error(
        'Could not refresh from ${view.sourceStatus.sourceDescription}: ${view.refreshError}',
      ));
    }

    if (view.importResults.isNotEmpty) {
      final imported =
          view.importResults.where((r) => r.outcome == AgentPackageImportOutcome.imported).toList();
      final failed =
          view.importResults.where((r) => r.outcome == AgentPackageImportOutcome.failed).toList();

      if (imported.isNotEmpty) {
        notices.add(AlertBox.success(
          'Imported ${imported.map((r) => '${r.platform} v${r.version}').join(', ')}.',
        ));
      }

      // Reported separately rather than folded into one message: a refresh imports what it can, so
      // "two of three worked" has to read as two successes and one failure, not as a single
      // ambiguous outcome.
      for (final failure in failed) {
        notices.add(AlertBox.error(
          'Could not import ${failure.platform} v${failure.version}: ${failure.message ?? 'unknown error'}',
        ));
      }

      if (imported.isEmpty && failed.isEmpty) {
        notices.add(AlertBox.info(
          'Every platform is already up to date with ${view.sourceStatus.sourceDescription}.',
        ));
      }
    } else if (view.sourceStatus.unavailableReason != null) {
      // A note beside a working screen rather than an error in place of one: the packages already
      // published here are installable whether or not GitHub is reachable.
      notices.add(AlertBox.info(
        'Could not check ${view.sourceStatus.sourceDescription} for new versions: '
        '${view.sourceStatus.unavailableReason}',
      ));
    } else if (view.sourceStatus.hasNewVersions) {
      final rows = view.sourceStatus.platforms.where((p) => p.isNewer).map(
            (p) => '${p.platform} v${p.availableVersion}'
                '${p.publishedVersion == null ? ' (nothing published yet)' : ' (published: v${p.publishedVersion})'}',
          );
      notices.add(AlertBox.info(
        'Newer builds are available: ${rows.join(', ')}. Use "Refresh clients" to publish them here.',
      ));
    }

    return notices;
  }
}

class _PackagesTable extends StatelessWidget {
  const _PackagesTable({required this.view});

  final ClientsView view;

  @override
  Widget build(BuildContext context) => KintsugiTable(
        minWidth: 940,
        columns: const [
          TableColumnSpec(label: 'Platform', width: FlexColumnWidth(0.8)),
          TableColumnSpec(label: 'Version', width: FlexColumnWidth(0.8)),
          TableColumnSpec(label: 'Available', width: FlexColumnWidth(1)),
          TableColumnSpec(label: 'Size', width: FlexColumnWidth(0.7)),
          TableColumnSpec(label: 'Published', width: FlexColumnWidth(1)),
          TableColumnSpec(label: 'Notes', width: FlexColumnWidth(1.4)),
          TableColumnSpec(label: 'Download', width: FixedColumnWidth(150)),
        ],
        rows: [
          for (final package in view.packages)
            KintsugiTableRow(
              cells: [
                Text(package.platform),
                Text(package.version),
                _AvailableCell(row: view.sourceStatus.rowFor(package.platform)),
                HintText(formatFileSize(package.fileSizeBytes)),
                LocalTimestamp(package.publishedUtc),
                package.releaseNotes == null ? const NoValue() : HintText(package.releaseNotes!),
                SecondaryButton(
                  label: 'Download',
                  // A link the browser follows, not a request: the response is a file, and the
                  // route is anonymous by design so an enrolled agent's own self-update can reach
                  // it before it has proven anything.
                  onPressed: () => _download(AgentPackageRepositoryImpl.downloadUrl(package.platform)),
                ),
              ],
            ),
        ],
      );

  /// Navigating to the URL rather than fetching it, because the response is a file with a
  /// Content-Disposition header and the browser is the right thing to handle that. The page stays
  /// where it is — the navigation resolves into a download, not a document.
  static void _download(String url) => locator<PageNavigator>().go(url);
}

class _AvailableCell extends StatelessWidget {
  const _AvailableCell({required this.row});

  final AgentPackageSourceRow? row;

  @override
  Widget build(BuildContext context) {
    final source = row;
    if (source == null) return const NoValue();
    return source.isNewer
        ? StatusChip('v${source.availableVersion}', statusKey: 'update-available')
        : const StatusChip('Up to date', statusKey: 'up-to-date');
  }
}

class _LoadingPanel extends StatelessWidget {
  const _LoadingPanel();

  @override
  Widget build(BuildContext context) => const KintsugiPanel(
        padding: EdgeInsets.symmetric(vertical: 48),
        child: Center(child: SizedBox(width: 24, height: 24, child: CircularProgressIndicator(strokeWidth: 2))),
      );
}
