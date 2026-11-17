import 'package:intl/intl.dart';

import '../../domain/entities/device_report.dart';
import 'autonomy_report.dart';
import 'device_report_model.dart';

/// What goes into the device report PDF. The whole collected log is deliberately not an option: a zip
/// cannot be attached to a PDF made in the browser, and the Logs panel downloads it on its own.
class DeviceReportOptions {
  const DeviceReportOptions({
    this.summary = true,
    this.timeline = true,
    this.involvement = true,
    this.hypotheses = true,
    this.installOutput = false,
    this.logLines = true,
  });

  final bool summary;
  final bool timeline;
  final bool involvement;
  final bool hypotheses;
  final bool installOutput;
  final bool logLines;

  DeviceReportOptions copyWith({
    bool? summary,
    bool? timeline,
    bool? involvement,
    bool? hypotheses,
    bool? installOutput,
    bool? logLines,
  }) =>
      DeviceReportOptions(
        summary: summary ?? this.summary,
        timeline: timeline ?? this.timeline,
        involvement: involvement ?? this.involvement,
        hypotheses: hypotheses ?? this.hypotheses,
        installOutput: installOutput ?? this.installOutput,
        logLines: logLines ?? this.logLines,
      );
}

/// The error lines the PDF prints for one moment: around a selected event, or the newest errors when
/// nothing is selected ([around] is then null).
class LogLinesGroup {
  const LogLinesGroup({required this.around, required this.lines});

  final DeviceReportEvent? around;
  final List<LogEntry> lines;
}

/// The most log lines the PDF carries in all, so a collection of thousands of errors stays a report.
const maxPdfLogLines = 200;

/// Turns one device's report into the specific report document: what happened, and why. Pure, so it is
/// tested without a screen or a browser. The log lines are read by the caller (they come from the entries
/// endpoint) and handed in.
ReportDocument buildDeviceReport(
  DeviceReport report,
  DeviceReportOptions options,
  DateTime now, {
  List<LogLinesGroup> logGroups = const [],
}) {
  final host = report.host;
  final sections = <ReportSection>[];
  final stamp = DateFormat('MMM d, HH:mm');
  String when(DateTime t) => stamp.format(t.toLocal());

  if (options.summary) {
    final likely = mostLikelyText(report);
    sections.add(ReportSection('What happened', [
      ReportText(summaryText(report)),
      if (likely != null) ReportText(likely),
      const ReportText('These are hints read from the logs, not a diagnosis.', muted: true),
    ]));
  }

  if (options.timeline) {
    final sorted = [...report.events]..sort((a, b) => a.occurredUtc.compareTo(b.occurredUtc));
    sections.add(ReportSection('Timeline', [
      ReportColumns(
        'Per day, last ${report.days} days',
        perDayCounts(report.events, report.days, now),
        ownLabel: 'unexpected shutdowns',
        adminLabel: 'hardware errors (fatal and corrected)',
      ),
      if (sorted.isEmpty)
        const ReportText('Nothing in this period.', muted: true)
      else
        ReportTable(
          const ['When', 'What', 'Source', 'What it said'],
          [for (final e in sorted) [when(e.occurredUtc), e.kind.label, e.source, e.detail ?? '']],
          weights: const [1.4, 1.6, 2, 4],
        ),
    ]));
  }

  if (options.involvement) {
    final shutdowns = report.shutdowns;
    sections.add(ReportSection('Was Kintsugi involved?', [
      const ReportText(
        'Kintsugi counts as possibly involved only if it installed an update, or a system update that can restart '
        'the device, in the 30 minutes before the shutdown.',
        muted: true,
      ),
      ReportText('${report.shutdownsNearKintsugi} of ${shutdowns.length} possibly involved.', bold: true),
      if (shutdowns.isNotEmpty)
        ReportTable(
          const ['Shutdown', 'Kintsugi', 'Detail'],
          [
            for (final s in shutdowns)
              [when(s.occurredUtc), involvement(s).possibly ? 'Possibly' : 'Not involved', involvement(s).text],
          ],
          statuses: {1: [for (final s in shutdowns) involvement(s).possibly ? 'A' : 'G']},
          weights: const [1.4, 1.2, 5],
        ),
    ]));
  }

  if (options.hypotheses) {
    sections.add(ReportSection('What it could be', [
      const ReportText('Ranked from the evidence. A hint to start the checking, not a diagnosis.', muted: true),
      if (report.hypotheses.isEmpty) const ReportText('Nothing to report.'),
      for (final h in report.hypotheses) ...[
        ReportText('${h.title} (${h.confidence.label.toLowerCase()})', bold: true),
        if (h.evidence.isNotEmpty) ...[
          const ReportText('Evidence', muted: true),
          ReportBullets(h.evidence),
        ],
        if (h.checkNext.isNotEmpty) ...[
          const ReportText('Check next', muted: true),
          ReportBullets(h.checkNext),
        ],
      ],
    ]));
  }

  if (options.installOutput) {
    final failed = report.installRuns.where((r) => !r.succeeded && (r.output ?? '').isNotEmpty);
    sections.add(ReportSection('Install output', [
      if (report.installRuns.isEmpty)
        const ReportText('Kintsugi installed nothing on this device in the period.', muted: true)
      else
        ReportTable(
          const ['When', 'What', 'Result'],
          [for (final r in report.installRuns) [when(r.atUtc), r.subject, r.succeeded ? 'Installed' : 'Failed']],
          statuses: {2: [for (final r in report.installRuns) r.succeeded ? 'G' : 'R']},
          weights: const [1.4, 4, 1.2],
        ),
      for (final r in failed) ReportText('${r.subject}: ${_clip(r.output!, 600)}', muted: true),
    ]));
  }

  if (options.logLines) {
    final lines = logGroups.fold<int>(0, (n, g) => n + g.lines.length);
    sections.add(ReportSection('Log lines', [
      ReportText(
        lines == 0
            ? 'No error lines in the collected log around ${logGroups.any((g) => g.around != null) ? 'the selected events' : 'this period'}.'
            : 'Errors only, at most $maxPdfLogLines lines, as the operating system wrote them. They can name people and files.',
        muted: true,
      ),
      for (final g in logGroups)
        if (g.lines.isNotEmpty) ...[
          ReportText(
            g.around == null
                ? 'Newest errors in the collected log'
                : '10 minutes around ${when(g.around!.occurredUtc)} (${g.around!.kind.label.toLowerCase()})',
            bold: true,
          ),
          ReportTable(
            const ['Time', 'Severity', 'Source', 'Message'],
            [
              for (final l in g.lines)
                [DateFormat('MMM d HH:mm:ss').format(l.time.toLocal()), l.severity.name.toUpperCase(), l.source, _clip(l.message, 300)],
            ],
            weights: const [1.5, 1, 2, 6],
          ),
        ],
    ]));
  }

  return ReportDocument(
    title: '${host.hostname} · device report',
    subtitle: '${[
      if (host.operatingSystem != null) host.operatingSystem!,
      if (host.serialNumber.isNotEmpty) host.serialNumber,
      switch (report.health.status) {
        DeviceHealthStatus.healthy => 'Healthy',
        DeviceHealthStatus.watch => 'Watch',
        DeviceHealthStatus.unstable => 'Unstable',
      },
    ].join(' · ')} · last ${report.days} days · prepared ${DateFormat('yyyy-MM-dd').format(now)}',
    sections: sections,
  );
}

String _clip(String s, int n) => s.length <= n ? s : '${s.substring(0, n)}...';
