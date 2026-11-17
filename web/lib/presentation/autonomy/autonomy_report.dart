import 'package:intl/intl.dart';

import '../../domain/entities/autonomy.dart';
import 'autonomy_model.dart';

/// What goes into the report. Each section can be left out; the commentary is the one part that is
/// written by a person rather than worked out.
class ReportOptions {
  const ReportOptions({
    this.summary = true,
    this.kpis = true,
    this.causes = true,
    this.health = true,
    this.faster = true,
    this.trends = true,
    this.chart = true,
    this.commentary = '',
  });

  final bool summary;
  final bool kpis;
  final bool causes;
  final bool health;
  final bool faster;
  final bool trends;
  final bool chart;
  final String commentary;

  ReportOptions copyWith({bool? summary, bool? kpis, bool? causes, bool? health, bool? faster, bool? trends, bool? chart, String? commentary}) =>
      ReportOptions(
        summary: summary ?? this.summary,
        kpis: kpis ?? this.kpis,
        causes: causes ?? this.causes,
        health: health ?? this.health,
        faster: faster ?? this.faster,
        trends: trends ?? this.trends,
        chart: chart ?? this.chart,
        commentary: commentary ?? this.commentary,
      );
}

/// One block of a report. The same model is drawn on screen (the preview) and written to the PDF, so
/// what a person checks is what they download.
sealed class ReportBlock {
  const ReportBlock();
}

final class ReportText extends ReportBlock {
  const ReportText(this.text, {this.muted = false, this.bold = false});

  final String text;
  final bool muted;
  final bool bold;
}

final class ReportBullets extends ReportBlock {
  const ReportBullets(this.items);

  final List<String> items;
}

final class ReportStats extends ReportBlock {
  const ReportStats(this.items);

  /// (big value, label).
  final List<(String, String)> items;
}

/// A table. A cell can be given a status colour with [statusColumns]: the column index maps to the
/// per-row status ("R", "A", "G") of the same row, drawn with the word as well as the colour.
final class ReportTable extends ReportBlock {
  const ReportTable(this.headers, this.rows, {this.statuses = const {}, this.weights});

  final List<String> headers;

  /// How wide each column is relative to the others. Equal when null, which squeezes a number column to
  /// the width of a paragraph column.
  final List<double>? weights;
  final List<List<String>> rows;

  /// Column index to the status of each row ("R", "A", "G", "I"), to colour that column's cells.
  final Map<int, List<String>> statuses;
}

/// Stacked columns, one per day or month: own schedule below, started by an administrator above.
final class ReportColumns extends ReportBlock {
  const ReportColumns(
    this.title,
    this.bars, {
    this.ownLabel = 'own schedule',
    this.adminLabel = 'started by an administrator',
  });

  final String title;

  /// What the lower and the upper part of each column mean. The Autonomy defaults unless a report
  /// counts something else (the device report counts shutdowns and hardware errors).
  final String ownLabel;
  final String adminLabel;

  /// (label, own, admin).
  final List<(String, int, int)> bars;
}

/// Horizontal bars, sorted by the caller, each with its value written at the end.
final class ReportBars extends ReportBlock {
  const ReportBars(this.title, this.items, {this.unit = ''});

  final String title;
  final List<(String, double)> items;
  final String unit;
}

/// One series with a dashed target line.
final class ReportLine extends ReportBlock {
  const ReportLine(this.title, this.labels, this.values, {this.target, this.targetLabel = '', this.suffix = ''});

  final String title;
  final List<String> labels;
  final List<double> values;
  final double? target;
  final String targetLabel;
  final String suffix;
}

class ReportSection {
  const ReportSection(this.title, this.blocks);

  final String title;
  final List<ReportBlock> blocks;
}

class ReportDocument {
  const ReportDocument({required this.title, required this.subtitle, required this.sections});

  final String title;
  final String subtitle;
  final List<ReportSection> sections;
}

/// Turns the Autonomy data into the general report: why patching is slow and what to do about it, with
/// no list of devices. Pure, so it is tested without a screen or a browser.
ReportDocument buildAutonomyReport(Autonomy data, ReportOptions options, DateTime now) {
  final period = data.period == AutonomyPeriod.month && data.month != null ? monthLabel(data.month!) : data.period.label;
  final sections = <ReportSection>[];
  final measured = data.causes.where((c) => c.measured && c.devices > 0).toList()
    ..sort((a, b) => b.deviceDays.compareTo(a.deviceDays));

  if (options.summary) {
    final reds = data.kpis.where((k) => k.status == 'R').length;
    final ambers = data.kpis.where((k) => k.status == 'A').length;
    final greens = data.kpis.where((k) => k.status == 'G').length;
    final p = data.patches;
    final delayedPct = data.totalHosts == 0 ? 0 : (data.devicesDelayed * 100 / data.totalHosts);
    final top = measured.isEmpty ? null : measured.first;
    sections.add(ReportSection('Executive summary', [
      ReportStats([
        ('$reds', 'in treatment'),
        ('$ambers', 'on watch'),
        ('$greens', 'on target'),
        ('${data.devicesDelayed}', 'of ${data.totalHosts} devices delayed (${delayedPct.toStringAsFixed(1)}%)'),
      ]),
      ReportText(
        '${NumberFormat.decimalPattern().format(p.total)} patches landed in the period, ${p.percent(p.ownSchedule)}% '
        "on the devices' own schedule. About ${data.totalDeviceDays.round()} device-days of patching were lost to "
        '${measured.length} cause${measured.length == 1 ? '' : 's'}'
        '${top == null ? '.' : ', and the largest is "${top.title}" at ${top.sharePercent}% of that.'}',
      ),
      if (data.historyStartsUtc != null)
        ReportText(
          'History starts ${DateFormat('yyyy-MM-dd').format(data.historyStartsUtc!.toLocal())}. Nothing before that can be rebuilt.',
          muted: true,
        ),
    ]));
  }

  if (options.kpis) {
    sections.add(ReportSection('KPIs and RAG bands', [
      const ReportText(
        'Green is at or inside the target, amber within tolerance, red outside it. The bands are fixed.',
        muted: true,
      ),
      ReportTable(
        const ['KPI', 'Category', 'Green', 'Amber', 'Red', 'Now', 'Prior', 'Change', 'Status'],
        [
          for (final k in data.kpis)
            [
              k.definition,
              k.category,
              bandsOf(k).green,
              bandsOf(k).amber,
              bandsOf(k).red,
              kpiValue(k),
              k.prior == null ? '—' : kpiValue(k, k.prior),
              kpiChange(k),
              statusWord(k.status),
            ],
        ],
        statuses: {5: [for (final k in data.kpis) k.status], 8: [for (final k in data.kpis) k.status]},
        weights: const [4, 1.3, 0.8, 0.8, 0.8, 0.8, 0.8, 0.9, 1],
      ),
    ]));
  }

  if (options.causes) {
    sections.add(ReportSection('Why patching is slow, ranked by what it costs', [
      const ReportText(
        'Cost is device-days of waiting: for each affected device, the days it has been waiting or failing, added '
        'up. It ranks causes by how much patching they slow down, not by how many devices they touch.',
        muted: true,
      ),
      if (measured.isNotEmpty) ReportBars('Device-days of delay by cause', [for (final c in measured) (c.title, c.deviceDays)]),
      if (measured.isNotEmpty)
        ReportTable(
          const ['Cause', 'Devices', 'Share of delay', 'Why it happens', 'How it slows patching', 'How to remediate'],
          [
            for (final c in measured)
              [
                c.title,
                '${c.devices}${data.totalHosts == 0 ? '' : ' (${(c.devices * 100 / data.totalHosts).toStringAsFixed(1)}%)'}',
                '${c.sharePercent}%',
                c.explanation ?? '',
                howItSlows(c),
                fixFor(c),
              ],
          ],
          weights: const [2, 1.2, 1, 4, 4, 4],
        )
      else
        const ReportText('Nothing is holding patching up right now.'),
      const ReportText(
        'Not measured yet: the person delaying a patch (Delay clicks, the delay budget), a host holding a patch '
        'because the screen is locked, and maintenance mode. They are decided on the host or do not exist yet, so '
        'they are not in this ranking.',
        muted: true,
      ),
    ]));
  }

  if (options.health) {
    final h = data.deviceHealth;
    sections.add(ReportSection('Device health', [
      ReportStats([
        ('${h.unstable}', 'unstable'),
        ('${h.watch}', 'to watch'),
        ('${data.totalHosts - h.unstable - h.watch}', 'with nothing to report'),
      ]),
      ReportText(
        h.unstable + h.watch == 0
            ? 'No device has shut down by itself or logged a hardware error in the last 30 days, among the devices whose '
                'agent reports it.'
            : '${h.unstable} device${h.unstable == 1 ? '' : 's'} unstable and ${h.watch} to watch. An unstable device that '
                'shuts down with no blue screen and logs its own hardware errors almost always has a hardware fault: '
                'give the person a loaner, copy off what is only on it, and raise a warranty claim.',
      ),
      const ReportText(deviceHealthRule, muted: true),
    ]));
  }

  if (options.faster) {
    final actions = speedActions(data);
    sections.add(ReportSection('How to be faster', [
      const ReportText(
        "In order of delay recovered, using only what is measured above. These are the causes' own fixes, not "
        'estimates of speed-up.',
        muted: true,
      ),
      if (actions.isEmpty)
        const ReportText('Nothing to do right now.')
      else
        ReportTable(
          const ['#', 'Action', 'Devices cleared', 'Delay recovered', 'Effort'],
          [
            for (var i = 0; i < actions.length; i++)
              [
                '${i + 1}',
                actions[i].title,
                '${actions[i].devices}',
                '${actions[i].deviceDays} device-days (${actions[i].sharePercent}%)',
                actions[i].effort,
              ],
          ],
          weights: const [0.4, 5, 1, 2.2, 2.5],
        ),
    ]));
  }

  if (options.trends) {
    final blocks = <ReportBlock>[];
    for (final id in trendKpis) {
      final kpi = data.kpis.where((k) => k.id == id).firstOrNull;
      final points = data.historyOf(id);
      if (kpi == null || points.length < 2) continue;
      blocks.add(ReportLine(
        kpi.definition,
        [for (final p in points) DateFormat('d MMM').format(p.dateUtc.toLocal())],
        [for (final p in points) p.value],
        target: kpi.green,
        targetLabel: 'target ${bandsOf(kpi).green}',
        suffix: kpi.isPercent ? '%' : '',
      ));
    }
    sections.add(ReportSection('Trends against the target', [
      if (blocks.isEmpty)
        ReportText(
          'History is building. Kintsugi records each figure once a day, so a trend needs at least two days of '
          'snapshots. ${data.kpiHistory.isEmpty ? 'The first one is recorded when the server has been running for a few minutes.' : ''}',
          muted: true,
        )
      else
        const ReportText('The dashed line is the green target.', muted: true),
      ...blocks,
    ]));
  }

  if (options.chart) {
    final daily = data.buckets.length > 1;
    sections.add(ReportSection('Patches over time', [
      ReportColumns(
        'Patches per ${daily ? 'day' : 'period'}',
        [for (final b in data.buckets) (DateFormat(data.buckets.length <= 7 ? 'E' : 'd').format(b.startUtc), b.own, b.admin)],
      ),
      ReportText(
        '${NumberFormat.decimalPattern().format(data.patches.total)} patches in the period, '
        '${NumberFormat.decimalPattern().format(data.patches.adminStarted)} started by an administrator.',
        muted: true,
      ),
      if (data.months.length > 1)
        ReportColumns('Patches per month', [for (final m in data.months) (monthShort(m.month), m.own, m.admin)]),
    ]));
  }

  if (options.commentary.trim().isNotEmpty) {
    sections.add(ReportSection('Commentary', [ReportText(options.commentary.trim())]));
  }

  return ReportDocument(
    title: 'Patching report',
    subtitle: '$period · prepared ${DateFormat('yyyy-MM-dd').format(now)} · general',
    sections: sections,
  );
}
