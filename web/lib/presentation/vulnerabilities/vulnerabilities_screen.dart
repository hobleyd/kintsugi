import 'package:flutter/material.dart';
import 'package:flutter_bloc/flutter_bloc.dart';

import '../../core/di/locator.dart';
import '../../core/platform/page_navigator.dart';
import '../../core/theme/kintsugi_palette.dart';
import '../../core/widgets/alert_box.dart';
import '../../core/widgets/buttons.dart';
import '../../core/widgets/kintsugi_table.dart';
import '../../core/widgets/page_scaffold.dart';
import '../../core/widgets/panel.dart';
import '../../core/widgets/form_bits.dart';
import '../../core/widgets/status_chip.dart';
import '../../core/widgets/text_bits.dart';
import '../../domain/entities/vulnerability.dart';
import '../../domain/usecases/vulnerability_usecases.dart';
import 'cpe_mapping_queue.dart';
import 'vulnerabilities_bloc.dart';

/// Which published CVEs affect the versions this fleet has installed.
///
/// The screen leads with the exploited set and shows the total as a number, which is the whole
/// editorial decision here: one out-of-date browser matched 631 CVEs against the live NVD API and
/// exactly one of them was in CISA's exploited catalogue. A list of 631 reads as noise and gets
/// ignored; the one is a thing somebody does something about this week.
///
/// It also states its own coverage. An application with no confirmed CPE has not been assessed at
/// all, and a screen that listed only what it managed to match would read as a clean bill of
/// health — the same failure the Vanta screen refuses by naming the eleven resource types it does
/// not sync.
class VulnerabilitiesScreen extends StatelessWidget {
  const VulnerabilitiesScreen({super.key});

  @override
  Widget build(BuildContext context) => BlocProvider(
        create: (_) => VulnerabilitiesBloc(getOverview: locator<GetVulnerabilityOverview>())
          ..add(const VulnerabilitiesRequested()),
        child: const _VulnerabilitiesView(),
      );
}

class _VulnerabilitiesView extends StatelessWidget {
  const _VulnerabilitiesView();

  @override
  Widget build(BuildContext context) => BlocBuilder<VulnerabilitiesBloc, VulnerabilitiesState>(
        builder: (context, state) {
          final overview = state.overview;

          return PageScaffold(
            title: 'Vulnerabilities',
            subtitle: 'Published CVEs affecting the versions this fleet has installed, matched by '
                'the National Vulnerability Database and flagged where CISA lists them as actively '
                'exploited in the wild.',
            children: [
              if (state.error != null) AlertBox.error(state.error!),
              if (overview != null) ...[
                _SummaryPanel(summary: overview.summary),
                const SizedBox(height: 20),
                _CoverageNotice(summary: overview.summary),
                _FilterBar(
                  knownExploitedOnly: state.knownExploitedOnly,
                  totalCveCount: overview.summary.totalCveCount,
                  knownExploitedCount: overview.summary.knownExploitedCount,
                ),
                const SizedBox(height: 12),
                if (state.loading)
                  const Padding(padding: EdgeInsets.all(24), child: LinearProgressIndicator())
                else if (overview.findings.isEmpty)
                  EmptyPanel(
                    overview.summary.hasAnyCoverage
                        ? (state.knownExploitedOnly
                            ? 'Nothing installed on this fleet is in CISA’s exploited catalogue.'
                            : 'No published CVE matches any version this fleet has installed.')
                        // The distinction that matters: an empty table means one of two very
                        // different things, and only one of them is good news.
                        : 'Nothing has been assessed yet. Confirm what an application is on the '
                            'CPE mapping queue below, and the next run will check it.',
                  )
                else
                  _FindingsTable(findings: overview.findings),
                const SizedBox(height: 32),
                const CpeMappingQueue(),
              ] else if (state.loading)
                const Padding(padding: EdgeInsets.all(24), child: LinearProgressIndicator()),
            ],
          );
        },
      );
}

/// The header numbers. Exploited first and largest, because it is the only one that is a list of
/// work rather than a measurement.
class _SummaryPanel extends StatelessWidget {
  const _SummaryPanel({required this.summary});

  final VulnerabilitySummary summary;

  @override
  Widget build(BuildContext context) {
    final palette = context.palette;

    return KintsugiPanel(
      child: Padding(
        padding: const EdgeInsets.all(16),
        child: Wrap(
          spacing: 40,
          runSpacing: 20,
          crossAxisAlignment: WrapCrossAlignment.start,
          children: [
            _Stat(
              value: '${summary.knownExploitedCount}',
              label: 'Known exploited',
              hint: 'In CISA’s catalogue of vulnerabilities observed being exploited',
              color: summary.knownExploitedCount > 0 ? palette.red : palette.green,
            ),
            _Stat(
              value: '${summary.totalCveCount}',
              label: 'CVEs matched',
              hint: 'Across every assessed product and version',
            ),
            _Stat(
              value: '${summary.affectedHostCount}',
              label: 'Hosts affected',
              hint: 'Distinct machines running at least one affected version',
            ),
            _Stat(
              value: '${summary.confirmedSubjectCount}',
              label: 'Assessed',
              hint: 'Applications and operating systems with a confirmed CPE',
            ),
            _Stat(
              value: '${summary.assessedPackageCount}',
              label: 'OS packages assessed',
              hint: 'Linux distribution packages, matched against their own distribution’s advisories',
            ),
            _Stat(
              value: '${summary.unmappedSubjectCount}',
              label: 'Not assessed',
              hint: 'No CPE confirmed, so NVD has never been asked about these',
              color: summary.unmappedSubjectCount > 0 ? palette.amber : null,
            ),
          ],
        ),
      ),
    );
  }
}

class _Stat extends StatelessWidget {
  const _Stat({required this.value, required this.label, required this.hint, this.color});

  final String value;
  final String label;
  final String hint;
  final Color? color;

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);

    return ConstrainedBox(
      constraints: const BoxConstraints(maxWidth: 220),
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.start,
        mainAxisSize: MainAxisSize.min,
        children: [
          Text(
            value,
            style: theme.textTheme.headlineLarge?.copyWith(color: color ?? context.palette.text),
          ),
          const SizedBox(height: 2),
          Text(label, style: theme.textTheme.bodyMedium),
          const SizedBox(height: 2),
          HintText(hint),
        ],
      ),
    );
  }
}

/// What this screen cannot see. Rendered as its own notice rather than folded into a footnote,
/// because missing coverage is the one thing a vulnerability screen must not be quiet about.
class _CoverageNotice extends StatelessWidget {
  const _CoverageNotice({required this.summary});

  final VulnerabilitySummary summary;

  @override
  Widget build(BuildContext context) {
    final parts = <String>[
      if (summary.unmappedSubjectCount > 0)
        '${summary.unmappedSubjectCount} application(s) or operating system(s) have no confirmed '
            'CPE, so nothing has been assessed for them.',
      if (summary.unassessableHostCount > 0)
        '${summary.unassessableHostCount} host(s) run an agent that does not report enough to '
            'assess their operating system — upgrade the agent on those hosts.',
      if (summary.assessmentsPending > 0)
        '${summary.assessmentsPending} installed version(s) are still queued to be checked.',
    ];

    if (parts.isEmpty) {
      return const SizedBox.shrink();
    }

    return Padding(
      padding: const EdgeInsets.only(bottom: 16),
      // Info rather than error: missing coverage is a gap to close, not a failure, and rendering
      // it in red beside a genuinely exploited CVE would flatten the difference.
      child: AlertBox.info(parts.join(' ')),
    );
  }
}

class _FilterBar extends StatelessWidget {
  const _FilterBar({
    required this.knownExploitedOnly,
    required this.totalCveCount,
    required this.knownExploitedCount,
  });

  final bool knownExploitedOnly;
  final int totalCveCount;
  final int knownExploitedCount;

  @override
  Widget build(BuildContext context) => Wrap(
        spacing: 12,
        runSpacing: 8,
        crossAxisAlignment: WrapCrossAlignment.center,
        children: [
          SegmentedButton<bool>(
            segments: [
              ButtonSegment(value: true, label: Text('Known exploited ($knownExploitedCount)')),
              ButtonSegment(value: false, label: Text('All matched CVEs ($totalCveCount)')),
            ],
            selected: {knownExploitedOnly},
            showSelectedIcon: false,
            onSelectionChanged: (selection) => context
                .read<VulnerabilitiesBloc>()
                .add(VulnerabilitiesFilterChanged(selection.first)),
          ),
          if (!knownExploitedOnly && totalCveCount > 200)
            const HintText('Showing the 200 highest-scoring; the count beside the button is the total.'),
        ],
      );
}

class _FindingsTable extends StatelessWidget {
  const _FindingsTable({required this.findings});

  final List<VulnerabilityFinding> findings;

  @override
  Widget build(BuildContext context) => KintsugiTable(
        minWidth: 960,
        columns: const [
          TableColumnSpec(label: 'CVE', width: FixedColumnWidth(150)),
          TableColumnSpec(label: 'Severity', width: FixedColumnWidth(130)),
          TableColumnSpec(label: 'Exploited', width: FixedColumnWidth(120)),
          TableColumnSpec(label: 'Affects', width: FlexColumnWidth(2)),
          TableColumnSpec(label: 'Installs', width: FixedColumnWidth(90), alignRight: true),
        ],
        rows: [
          for (final finding in findings)
            KintsugiTableRow(
              cells: [
                LinkText(label: finding.cveId, onTap: () => _openExternal(finding.nvdUrl)),
                _SeverityCell(finding: finding),
                if (finding.knownExploited)
                  const StatusChip('Exploited', statusKey: 'exploited')
                else
                  const NoValue(),
                _AffectsCell(subjects: finding.affectedSubjects),
                CountBadge(finding.hostCount, alert: finding.knownExploited),
              ],
              expanded: _FindingDetail(finding: finding),
            ),
        ],
      );

}

/// Opens somebody else's page in a new tab. Through [PageNavigator] rather than `package:web`
/// directly, because the browser implementation is deliberately the only file in this app that
/// imports it.
void _openExternal(String url) => locator<PageNavigator>().openInNewTab(url);

class _SeverityCell extends StatelessWidget {
  const _SeverityCell({required this.finding});

  final VulnerabilityFinding finding;

  @override
  Widget build(BuildContext context) {
    final severity = finding.cvssSeverity;
    if (severity == null || finding.cvssBaseScore == null) {
      // Common for recent CVEs, and said rather than shown as a zero — an unscored critical and a
      // genuinely harmless one would otherwise look identical.
      return const HintText('Unscored');
    }

    return Wrap(
      spacing: 6,
      crossAxisAlignment: WrapCrossAlignment.center,
      children: [
        StatusChip(severity, statusKey: 'cve-${severity.toLowerCase()}'),
        Text(
          finding.cvssBaseScore!.toStringAsFixed(1),
          style: Theme.of(context).textTheme.bodyMedium,
        ),
      ],
    );
  }
}

class _AffectsCell extends StatelessWidget {
  const _AffectsCell({required this.subjects});

  final List<AffectedSubject> subjects;

  @override
  Widget build(BuildContext context) {
    final shown = subjects.take(3).toList();

    return Column(
      crossAxisAlignment: CrossAxisAlignment.start,
      mainAxisSize: MainAxisSize.min,
      children: [
        for (final subject in shown)
          Text(
            '${subject.displayName} ${subject.version}',
            style: Theme.of(context).textTheme.bodyMedium,
          ),
        if (subjects.length > shown.length) HintText('and ${subjects.length - shown.length} more'),
      ],
    );
  }
}

class _FindingDetail extends StatelessWidget {
  const _FindingDetail({required this.finding});

  final VulnerabilityFinding finding;

  @override
  Widget build(BuildContext context) => Padding(
        padding: const EdgeInsets.fromLTRB(12, 12, 12, 16),
        child: Column(
          crossAxisAlignment: CrossAxisAlignment.start,
          children: [
            if (finding.knownExploited) ...[
              AlertBox.error(
                finding.kevShortDescription ?? 'CISA lists this vulnerability as actively exploited.',
              ),
              if (finding.kevRequiredAction != null) ...[
                const SizedBox(height: 6),
                HintText('CISA’s required action: ${finding.kevRequiredAction}'),
              ],
              if (finding.kevDueDateUtc != null) ...[
                const SizedBox(height: 6),
                Row(
                  children: [
                    const HintText('Federal remediation due '),
                    LocalTimestamp(finding.kevDueDateUtc),
                  ],
                ),
              ],
              const SizedBox(height: 12),
            ],
            if (finding.description != null) Text(finding.description!),
            const SizedBox(height: 12),
            Wrap(
              spacing: 24,
              runSpacing: 6,
              children: [
                if (finding.cvssVector != null)
                  // Verbatim, and linked out rather than paraphrased into words: the vector is
                  // the precise claim, and NVD's own page is where the configuration ranges live.
                  CodeText('CVSS v${finding.cvssVersion ?? '?'} ${finding.cvssVector}'),
                LinkText(label: 'View on NVD', onTap: () => _openExternal(finding.nvdUrl)),
              ],
            ),
            const SizedBox(height: 12),
            const SubHeadingTight('Affected'),
            for (final subject in finding.affectedSubjects)
              Padding(
                padding: const EdgeInsets.only(top: 4),
                child: Row(
                  children: [
                    StatusChip.neutral(subject.subjectKind.label),
                    const SizedBox(width: 8),
                    Text('${subject.displayName} ${subject.version}'),
                    const SizedBox(width: 8),
                    HintText('on ${subject.hostCount} host(s)'),
                  ],
                ),
              ),
            // Which database answered, said once per kind rather than per row. Not decoration:
            // a distribution package's answer accounts for that distribution's backported fixes
            // and an application's does not have to, so the two claims mean different things and
            // a reader deciding what to do about one is entitled to know which they are reading.
            for (final kind in finding.affectedSubjects.map((s) => s.subjectKind).toSet()) ...[
              const SizedBox(height: 6),
              HintText('${kind.label}: ${kind.sourceLabel}'),
            ],
          ],
        ),
      );
}
