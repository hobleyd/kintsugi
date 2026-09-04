import 'package:flutter/material.dart';
import 'package:flutter_bloc/flutter_bloc.dart';

import '../../core/di/locator.dart';
import '../../core/widgets/alert_box.dart';
import '../../core/widgets/buttons.dart';
import '../../core/widgets/kintsugi_table.dart';
import '../../core/widgets/page_scaffold.dart';
import '../../core/widgets/panel.dart';
import '../../core/widgets/status_chip.dart';
import '../../core/widgets/text_bits.dart';
import '../../domain/entities/upgrade_script.dart';
import '../../domain/usecases/upgrade_script_usecases.dart';
import 'upgrade_scripts_bloc.dart';

/// Every upgrade script an agent could run, and whether a human has approved it.
/// What `Pages/UpgradeScripts.cshtml` was.
class UpgradeScriptsScreen extends StatelessWidget {
  const UpgradeScriptsScreen({super.key});

  @override
  Widget build(BuildContext context) => BlocProvider(
        create: (_) => UpgradeScriptsBloc(
          getView: locator<GetUpgradeScriptsView>(),
          refreshApprovedScripts: locator<RefreshApprovedScripts>(),
          adoptApprovedScript: locator<AdoptApprovedScript>(),
          takeServerWrittenScript: locator<TakeServerWrittenScript>(),
        )..add(const UpgradeScriptsRequested()),
        child: const _UpgradeScriptsView(),
      );
}

class _UpgradeScriptsView extends StatelessWidget {
  const _UpgradeScriptsView();

  @override
  Widget build(BuildContext context) =>
      const BlocBuilder<UpgradeScriptsBloc, UpgradeScriptsState>(builder: _build);

  static Widget _build(BuildContext context, UpgradeScriptsState state) {
    final view = state.view;
    final overview = view?.overview;

    return PageScaffold(
      title: 'Upgrade Scripts',
      children: [
        SectionHeader(
          title: 'Upgrade Scripts',
          hints: [
            const HintText(
              'Every upgrade script an agent could run, and whether a human has approved it. An '
              'unapproved script is one no agent will execute: each agent verifies the signature '
              'against the key it pinned at enrollment before running anything, so "unsigned" means '
              'inert rather than trusted. Scripts are reviewed and signed on the Applications screen.',
            ),
            if (overview != null)
              HintText(
                'Approvals are shared through ${overview.repository}. Signing a script there opens a '
                'pull request against it; merging that pull request is what makes the approval '
                'available to every other Kintsugi server. "Refresh scripts" reads that branch back '
                'and signs any local script whose bytes are already approved there.',
              ),
          ],
          actions: [
            PrimaryButton(
              label: 'Refresh scripts',
              busy: state.busyLabel == 'refresh',
              onPressed: state.loading || state.busy
                  ? null
                  : () => context.read<UpgradeScriptsBloc>().add(const UpgradeScriptsRefreshRequested()),
            ),
          ],
        ),
        if (state.error != null) AlertBox.error(state.error!),
        if (view != null) ..._notices(view),
        if (state.loading)
          const _LoadingPanel()
        else if (overview == null)
          const EmptyPanel('Nothing could be loaded for this screen.')
        else ...[
          _TrustPanel(overview: overview),
          const SubHeading('Local scripts'),
          ..._localScriptNotices(overview),
          if (overview.localScripts.isEmpty)
            const EmptyPanel(
              'No upgrade scripts have been resolved yet. Use "Find Upgrade Paths" on the '
              'Applications screen.',
            )
          else
            _LocalScriptsTable(overview: overview, state: state),
          const SubHeading('Approved scripts available to adopt'),
          HintText(
            'Scripts approved elsewhere for an application this server has no approved script for. '
            'Adopting one copies its content onto that upgrade path and signs it with this '
            "server's key, because every agent pins one signing key and it is its own server's — a "
            'remote signature would be genuine and still refused. Check the signer before adopting: '
            'merging to ${overview.defaultBranch ?? 'the default branch'} is what puts an entry in '
            'this list, so this button is the last human decision before agents run the script as '
            'root.',
          ),
          const SizedBox(height: 12),
          if (overview.adoptionCandidates.isEmpty)
            const EmptyPanel(
              'Nothing to adopt. Every local upgrade path either has an approved script or has no '
              'approved counterpart upstream.',
            )
          else
            _AdoptionTable(overview: overview, state: state),
          const SubHeading('Imported approvals'),
          if (overview.approved.isEmpty)
            EmptyPanel('Nothing has been imported from ${overview.repository} yet. Use "Refresh scripts".')
          else
            _ApprovedTable(overview: overview),
        ],
      ],
    );
  }

  static List<Widget> _notices(UpgradeScriptsView view) {
    final overview = view.overview;
    final notices = <Widget>[];

    if (!overview.publishingEnabled) {
      // Said out loud for the same reason the Clients screen announces a derived
      // AGENT_API_BASE_URL: the failure is invisible otherwise. Signing keeps working, so nothing
      // looks wrong — an operator would only find out no audit trail exists by going to look for
      // pull requests that were never opened.
      notices.add(AlertBox.info(
        'No script-approval token is configured, so signing a script approves it on this server '
        'only — no pull request is raised, nothing is recorded in ${overview.repository}, and no '
        'other server can pick the approval up. Reading the approved corpus still works; only '
        'publishing needs the token.',
      ));
    }

    if (view.refreshError != null) {
      notices.add(AlertBox.error('Could not refresh from ${overview.repository}: ${view.refreshError}'));
    } else if (overview.unavailableReason != null) {
      // A note beside a working screen, not an error in place of one: already-imported approvals
      // and already-signed local scripts are unaffected by GitHub being unreachable.
      notices.add(AlertBox.info('Could not read ${overview.repository}: ${overview.unavailableReason}'));
    }

    if (view.adoptError != null) {
      notices.add(AlertBox.error('Could not adopt that script: ${view.adoptError}'));
    } else if (view.adopted != null) {
      notices.add(AlertBox.success(
        'Adopted the approved script for ${view.adopted!.applicationName} on '
        "${view.adopted!.platform} and signed it with this server's key.",
      ));
    }

    if (view.takeServerScriptError != null) {
      notices.add(AlertBox.error('Could not take the newer script: ${view.takeServerScriptError}'));
    } else if (view.tookServerScript != null) {
      final took = view.tookServerScript!;
      notices.add(took.changed
          ? AlertBox.success(
              '${took.applicationName} on ${took.platform} now holds the script this server writes, '
              'and is awaiting review — no agent will run it until it is signed. Signing it covers '
              'every other row holding the same content.',
            )
          : AlertBox.success(
              '${took.applicationName} on ${took.platform} already holds the script this server '
              'writes; nothing changed.',
            ));
    }

    final import = view.importResult;
    if (import != null) {
      notices.add(AlertBox.success(
        'Read ${overview.repository} at ${shorten(import.commitSha ?? '?', 12)}: '
        '${import.imported} newly imported, ${import.alreadyKnown} already known.',
      ));

      if (import.blessed.isNotEmpty) {
        // Distinguished from adoption in the wording, because the distinction is the whole safety
        // argument: nothing arrived, an existing local script was recognised as already reviewed.
        notices.add(AlertBox.success(
          'Signed ${import.blessed.length} local script(s) whose content was already approved '
          'upstream — no script text changed: '
          '${import.blessed.map((b) => '${b.applicationName} on ${b.platform}').join(', ')}.',
        ));
      }

      for (final reason in import.rejected) {
        notices.add(AlertBox.error('Skipped: $reason'));
      }
    }

    return notices;
  }

  static List<Widget> _localScriptNotices(UpgradeScriptsOverview overview) {
    final notices = <Widget>[];

    if (overview.awaitingReview > 0) {
      // The only number on this screen representing work outstanding: until a human signs these,
      // no agent will run them, so those applications are not patching at all.
      notices.add(AlertBox.info(
        '${overview.awaitingReview} script(s) are waiting for review. Until one is signed no agent '
        'will run it, so those applications are not patching. Review and sign them on the '
        'Applications screen.',
      ));
    }

    if (overview.newerServerScripts > 0) {
      // Deliberately not phrased as work outstanding: these rows are signed and patching normally.
      // A signed script is never replaced by a deployment, so this is the only thing that says a
      // newer one exists.
      notices.add(AlertBox.info(
        '${overview.newerServerScripts} signed row(s) hold a script this server no longer writes — '
        'one of its package-manager scripts has been changed since they were reviewed. They keep '
        'running the text that was approved, and go on patching, until someone takes the newer one '
        'below. Taking it leaves that row unsigned until it is reviewed and signed.',
      ));
    }

    return notices;
  }
}

/// What verifying an approval does and does not prove.
class _TrustPanel extends StatelessWidget {
  const _TrustPanel({required this.overview});

  final UpgradeScriptsOverview overview;

  @override
  Widget build(BuildContext context) => KintsugiPanel(
        padding: const EdgeInsets.all(18),
        child: HintText(
          'This server signs as ${overview.thisServerFingerprint}. That is the only fingerprint '
          'whose signatures are verified against a key this server holds; every other signer\'s '
          'public key travels in the approval repository alongside the script it vouches for, so a '
          'signature from one shows who claims to have reviewed a script, not that they were '
          "authorized to. Authorization is the repository's branch protection on "
          '${overview.defaultBranch ?? 'the default branch'}.',
        ),
      );
}

class _LocalScriptsTable extends StatelessWidget {
  const _LocalScriptsTable({required this.overview, required this.state});

  final UpgradeScriptsOverview overview;
  final UpgradeScriptsState state;

  @override
  Widget build(BuildContext context) => KintsugiTable(
        minWidth: 900,
        columns: const [
          TableColumnSpec(label: 'Application', width: FlexColumnWidth(1.4)),
          TableColumnSpec(label: 'Platform', width: FlexColumnWidth(1)),
          TableColumnSpec(label: 'Content', width: FlexColumnWidth(0.9)),
          TableColumnSpec(label: 'Approved here', width: FixedColumnWidth(160)),
          TableColumnSpec(label: 'Approved upstream', width: FixedColumnWidth(160)),
          TableColumnSpec(label: 'Server script', width: FixedColumnWidth(210)),
        ],
        rows: [
          for (final script in overview.localScripts)
            KintsugiTableRow(
              cells: [
                Text(script.applicationName),
                Text(script.platform),
                CodeText(shorten(script.sha256, 12), muted: true),
                script.signed
                    ? const StatusChip('Signed', statusKey: 'up-to-date')
                    : const StatusChip('Review and sign', statusKey: 'update-available'),
                script.approvedUpstream
                    ? const StatusChip('Yes', statusKey: 'up-to-date')
                    : const NoValue(),
                if (script.newerServerScriptAvailable)
                  // The row keeps running its reviewed script until this is pressed — nothing does
                  // it by itself, because replacing the content of a signed row is replacing what
                  // the fleet's agents execute. Taking it leaves the row unsigned, so the button
                  // says so rather than implying the new text goes live on click.
                  SecondaryButton(
                    label: 'Take newer, for review',
                    onPressed: state.busy
                        ? null
                        : () => context
                            .read<UpgradeScriptsBloc>()
                            .add(ServerScriptTakeRequested(script)),
                  )
                else
                  const HintText('Current'),
              ],
            ),
        ],
      );
}

class _AdoptionTable extends StatelessWidget {
  const _AdoptionTable({required this.overview, required this.state});

  final UpgradeScriptsOverview overview;
  final UpgradeScriptsState state;

  @override
  Widget build(BuildContext context) => KintsugiTable(
        minWidth: 1000,
        columns: const [
          TableColumnSpec(label: 'Application', width: FlexColumnWidth(1.2)),
          TableColumnSpec(label: 'Platform', width: FlexColumnWidth(0.9)),
          TableColumnSpec(label: 'Content', width: FlexColumnWidth(0.8)),
          TableColumnSpec(label: 'Signer', width: FlexColumnWidth(1.2)),
          TableColumnSpec(label: 'Signed by', width: FlexColumnWidth(1)),
          TableColumnSpec(label: 'Approved', width: FlexColumnWidth(1)),
          TableColumnSpec(label: 'Action', width: FixedColumnWidth(200)),
        ],
        rows: [
          for (final candidate in overview.adoptionCandidates)
            KintsugiTableRow(
              cells: [
                Text(candidate.applicationName),
                Text(candidate.platform),
                CodeText(shorten(candidate.sha256, 12), muted: true),
                candidate.isThisServer
                    ? const StatusChip('This server', statusKey: 'up-to-date')
                    : CodeText(shorten(candidate.signerFingerprint, 19), muted: true),
                candidate.signedBy == null ? const NoValue() : HintText(candidate.signedBy!),
                LocalTimestamp(candidate.approvedAtUtc),
                SecondaryButton(
                  // The label changes when there is unsigned local content to lose. No agent is
                  // running it — the row is unsigned either way — but someone who hand-wrote a
                  // script here and has not signed it yet should not discover that from the result.
                  label: candidate.replacesExistingScript ? 'Replace and adopt' : 'Adopt',
                  onPressed: state.busy
                      ? null
                      : () => context
                          .read<UpgradeScriptsBloc>()
                          .add(ApprovedScriptAdoptRequested(candidate)),
                ),
              ],
            ),
        ],
      );
}

class _ApprovedTable extends StatelessWidget {
  const _ApprovedTable({required this.overview});

  final UpgradeScriptsOverview overview;

  @override
  Widget build(BuildContext context) => KintsugiTable(
        minWidth: 1000,
        columns: const [
          TableColumnSpec(label: 'Application', width: FlexColumnWidth(1.2)),
          TableColumnSpec(label: 'Bucket', width: FlexColumnWidth(0.9)),
          TableColumnSpec(label: 'Content', width: FlexColumnWidth(0.8)),
          TableColumnSpec(label: 'Signer', width: FlexColumnWidth(1.2)),
          TableColumnSpec(label: 'Signed by', width: FlexColumnWidth(1)),
          TableColumnSpec(label: 'In use here', width: FixedColumnWidth(140)),
          TableColumnSpec(label: 'From commit', width: FlexColumnWidth(0.9)),
        ],
        rows: [
          for (final approved in overview.approved)
            KintsugiTableRow(
              cells: [
                Text(approved.applicationName),
                Text(approved.platformBucket),
                CodeText(shorten(approved.sha256, 12), muted: true),
                approved.isThisServer
                    ? const StatusChip('This server', statusKey: 'up-to-date')
                    : CodeText(shorten(approved.signerFingerprint, 19), muted: true),
                approved.signedBy == null ? const NoValue() : HintText(approved.signedBy!),
                approved.heldLocally
                    ? const StatusChip('Yes', statusKey: 'up-to-date')
                    : const NoValue(),
                CodeText(shorten(approved.sourceCommitSha, 12), muted: true),
              ],
            ),
        ],
      );
}

class _LoadingPanel extends StatelessWidget {
  const _LoadingPanel();

  @override
  Widget build(BuildContext context) => const KintsugiPanel(
        padding: EdgeInsets.symmetric(vertical: 48),
        child: Center(child: SizedBox(width: 24, height: 24, child: CircularProgressIndicator(strokeWidth: 2))),
      );
}
