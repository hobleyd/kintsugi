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
/// not sync. What it cannot see is fixed next door, on CVE Mapping — so every sentence here that
/// names a gap names that screen rather than pointing at something below it.
class CveReportingScreen extends StatelessWidget {
  const CveReportingScreen({super.key});

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
            title: 'CVE Reporting',
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
                // A fleet with nothing assessed has no table to show. Anything else keeps one,
                // empty or not: the filters and the Clear Filters button live in its toolbar, and
                // replacing the table with a panel that says "clear them from the toolbar" takes
                // away the only control that would.
                else if (!overview.summary.hasAnyCoverage)
                  const EmptyPanel('Nothing has been assessed yet. Confirm what an application is '
                      'on the CVE Mapping screen, and the next run will check it.')
                else ...[
                  _FindingsTable(findings: overview.findings, state: state),
                  _Paginator(overview: overview),
                ],
              ] else if (state.loading)
                const Padding(padding: EdgeInsets.all(24), child: LinearProgressIndicator()),
            ],
          );
        },
      );
}

/// What an empty table means, which is three quite different things.
///
/// Nothing assessed, nothing found, and nothing *matching the filters* are not interchangeable.
/// The first is handled above, because it is the one case with no table at all; these two are
/// rows inside the table, so the toolbar the third one points at is still on screen. Reporting
/// any of them as another is the clean-bill-of-health failure this screen is built to refuse.
String _emptyRowMessage(VulnerabilityQuery query) {
  if (query.hasFilters) {
    return 'No CVE matches these filters. Clear them from the toolbar above to see the rest.';
  }

  return query.knownExploitedOnly
      ? 'Nothing installed on this fleet is in CISA’s exploited catalogue.'
      : 'No published CVE matches any version this fleet has installed.';
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
            'CPE, so nothing has been assessed for them — confirm what they are on the CVE '
            'Mapping screen.',
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
          // These two numbers count every match in the fleet and know nothing about the header
          // filters, which is why the paginator below says "matching" and this does not: with a
          // Platform filter on, this can honestly read 847 above a page of 31.
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
        ],
      );
}

/// Which page of the matching findings is on screen, and how to reach the others.
///
/// It says "matching" deliberately. The count beside it is the filtered total, which is not the
/// segmented button's total above — that one counts every match in the fleet — and two unlabelled
/// numbers disagreeing on one screen is worse than either of them being absent.
class _Paginator extends StatelessWidget {
  const _Paginator({required this.overview});

  final VulnerabilityOverview overview;

  @override
  Widget build(BuildContext context) {
    if (overview.pageCount <= 1) {
      return const SizedBox.shrink();
    }

    final bloc = context.read<VulnerabilitiesBloc>();

    return Padding(
      padding: const EdgeInsets.only(top: 12),
      child: Wrap(
        spacing: 12,
        runSpacing: 8,
        crossAxisAlignment: WrapCrossAlignment.center,
        children: [
          HintText('Showing ${overview.firstRowNumber}–${overview.lastRowNumber} '
              'of ${overview.filteredCount} matching'),
          SecondaryButton(
            label: 'Previous',
            // Null rather than hidden at the ends: a control that vanishes moves the one beside
            // it under the pointer that was about to press it.
            onPressed: overview.page == 0
                ? null
                : () => bloc.add(VulnerabilitiesPageChanged(overview.page - 1)),
          ),
          HintText('Page ${overview.page + 1} of ${overview.pageCount}'),
          SecondaryButton(
            label: 'Next',
            onPressed: overview.page >= overview.pageCount - 1
                ? null
                : () => bloc.add(VulnerabilitiesPageChanged(overview.page + 1)),
          ),
        ],
      ),
    );
  }
}

/// The table. Every header control here sends a request rather than filtering what has already
/// arrived, because the page is cut on the server after the filter and the sort — see
/// `VulnerabilityQuery`. Sorting a page in the browser would order the page and not the set,
/// which is how "the least-installed of the hundred highest-scoring" ends up reading as the
/// fleet's least-installed.
class _FindingsTable extends StatelessWidget {
  const _FindingsTable({required this.findings, required this.state});

  final List<VulnerabilityFinding> findings;
  final VulnerabilitiesState state;

  @override
  Widget build(BuildContext context) {
    final bloc = context.read<VulnerabilitiesBloc>();
    final query = state.query;

    return KintsugiTable(
      minWidth: 1180,
      sort: query.sortKey == null
          ? null
          : TableSort(query.sortKey!, ascending: query.sortAscending),
      onSort: (key) => bloc.add(VulnerabilitiesSortChanged(key)),
      toolbar: Row(
        children: [
          const Expanded(child: HintText('Search, filter and sort from the column headers below.')),
          if (query.hasFilters)
            SecondaryButton(
              label: 'Clear Filters',
              onPressed: () => bloc.add(const VulnerabilitiesFiltersCleared()),
            ),
        ],
      ),
      columns: [
        TableColumnSpec(
          label: 'CVE',
          width: const FixedColumnWidth(190),
          sortKey: VulnerabilitySortKey.cve,
          filter: SearchField(
            value: query.cveSearch,
            hintText: 'CVE-2024-…',
            // Through the bloc's own debounce rather than as an event: each of these runs on the
            // server, so a keystroke is a round trip unless they are collapsed first.
            onChanged: (value) => bloc.searchChanged(cveSearch: value),
          ),
        ),
        TableColumnSpec(
          label: 'Severity',
          width: const FixedColumnWidth(180),
          sortKey: VulnerabilitySortKey.severity,
          filter: KintsugiDropdown<String>(
            value: query.severity,
            items: const [VulnerabilityQuery.anyValue, ...vulnerabilitySeverities],
            labelOf: (value) => value == VulnerabilityQuery.anyValue ? 'Any severity' : value,
            onChanged: (value) => bloc.add(VulnerabilitiesHeaderFilterChanged(severity: value)),
          ),
        ),
        // Measured rather than guessed: the cell is a chip, which cannot be made narrower than
        // its one word, and the table's own floor only covers the header label above it. At a
        // hand-set 120 this was under two pixels short and rendered "EXPLOITE" over "D".
        TableColumnSpec(
          label: 'Exploited',
          width: TableColumnSpec.forContent(StatusChip.widthFor(context, 'Exploited')),
          sortKey: VulnerabilitySortKey.exploited,
        ),
        TableColumnSpec(
          label: 'Platform',
          width: const FixedColumnWidth(190),
          filter: KintsugiDropdown<String>(
            value: query.platform,
            items: const [VulnerabilityQuery.anyValue, ...vulnerabilityPlatforms],
            labelOf: (value) => value == VulnerabilityQuery.anyValue ? 'Any platform' : value,
            onChanged: (value) => bloc.add(VulnerabilitiesHeaderFilterChanged(platform: value)),
          ),
        ),
        TableColumnSpec(
          label: 'Affects',
          width: const FlexColumnWidth(2),
          filter: SearchField(
            value: query.subjectSearch,
            hintText: 'Product or version...',
            onChanged: (value) => bloc.searchChanged(subjectSearch: value),
          ),
        ),
        const TableColumnSpec(
          label: 'Installs',
          width: FixedColumnWidth(110),
          alignRight: true,
          sortKey: VulnerabilitySortKey.installs,
        ),
      ],
      rows: [
        if (findings.isEmpty)
          KintsugiTableRow(
            key: const ValueKey('no-matches'),
            cells: [HintText(_emptyRowMessage(query))],
          ),
        for (final finding in findings)
          KintsugiTableRow(
            key: ValueKey(finding.cveId),
            cells: [
              LinkText(label: finding.cveId, onTap: () => _openExternal(finding.nvdUrl)),
              _SeverityCell(finding: finding),
              if (finding.knownExploited)
                const StatusChip('Exploited', statusKey: 'exploited')
              else
                const NoValue(),
              _PlatformCell(platforms: finding.platforms),
              _AffectsCell(subjects: finding.affectedSubjects),
              CountBadge(finding.hostCount, alert: finding.knownExploited),
            ],
            expanded: _FindingDetail(finding: finding),
          ),
      ],
    );
  }
}

/// Which operating system families this CVE actually reaches in this fleet.
///
/// A list rather than a value, because one is the exception: an OpenSSL flaw arrives as a
/// Homebrew install on the laptops and a distribution package on the servers, and a column
/// showing only the first would send somebody to patch half the estate. "Unknown" is shown like
/// any other platform — a host whose reported operating system nothing recognises is exposed
/// just the same, and quietly omitting it is the failure this screen exists to refuse.
class _PlatformCell extends StatelessWidget {
  const _PlatformCell({required this.platforms});

  final List<String> platforms;

  @override
  Widget build(BuildContext context) {
    if (platforms.isEmpty) {
      return const NoValue();
    }

    return Wrap(
      spacing: 6,
      runSpacing: 4,
      children: [
        for (final platform in platforms)
          StatusChip(
            platform,
            statusKey: platform == 'Unknown' ? 'unknown' : '_accent',
          ),
      ],
    );
  }
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
        // Marked, not hidden. The number is exact for the vector it was computed from, but that
        // vector is the distribution's analysis rather than NVD's, and the two can differ.
        if (finding.cvssDerivedFromVector)
          const Tooltip(
            message: 'Computed from the advisory’s own CVSS vector, which is shown in full when '
                'this row is expanded. NVD has published no score for this CVE.',
            child: HintText('calculated'),
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
                  // It is also what a derived score is checkable against.
                  CodeText('CVSS v${finding.cvssVersion ?? '?'} ${finding.cvssVector}'),
                if (finding.cvssDerivedFromVector)
                  const HintText(
                    'Score calculated from that vector — a CVSS base score is a fixed function of '
                    'its vector — because NVD has published none. The vector is the distribution’s '
                    'own analysis.',
                  ),
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
