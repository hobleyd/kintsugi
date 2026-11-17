import 'package:flutter/material.dart';
import 'package:go_router/go_router.dart';
import 'package:intl/intl.dart';

import '../../core/di/locator.dart';
import '../../core/network/api_exception.dart';
import '../../core/platform/file_saver.dart';
import '../../core/router/app_router.dart';
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
import '../../domain/entities/autonomy.dart';
import '../../domain/entities/device_report.dart';
import '../../domain/entities/host_activity.dart';
import '../../domain/usecases/autonomy_usecases.dart';
import '../../domain/usecases/host_activity_usecases.dart';
import 'autonomy_model.dart';
import 'autonomy_pdf.dart';
import 'autonomy_report.dart';
import 'device_health_row.dart';
import 'device_search.dart';
import 'report_preview.dart';

/// Why patching is slow, what it costs, who started each patch, and a report of it that can be saved
/// as a PDF.
///
/// Built only from what the server records, so it reads the same for macOS, Linux and Windows hosts.
/// It shows causes with counts and cost, never a list of every device: a fleet of any size reads the
/// same, and the devices in a cause are read a page at a time. What a host decides for itself (holding
/// a patch because the screen is locked, a person pressing Delay) is not on it: the agents do not
/// report that, and the page says so rather than showing a gap that reads as "nothing happened".
class AutonomyScreen extends StatefulWidget {
  const AutonomyScreen({super.key});

  @override
  State<AutonomyScreen> createState() => _AutonomyScreenState();
}

enum _Tab { overview, report }

class _AutonomyScreenState extends State<AutonomyScreen> {
  AutonomyPeriod _period = AutonomyPeriod.days30;
  String? _month;
  Autonomy? _data;
  String? _error;
  bool _loading = false;
  bool _saving = false;
  String? _notice;
  _Tab _tab = _Tab.overview;
  String? _openCause;
  ReportOptions _options = const ReportOptions();
  final _commentary = TextEditingController();

  @override
  void initState() {
    super.initState();
    _load();
  }

  @override
  void dispose() {
    _commentary.dispose();
    super.dispose();
  }

  Future<void> _load() async {
    setState(() {
      _loading = true;
      _error = null;
    });
    try {
      final month = _period == AutonomyPeriod.month ? (_month ?? currentMonth(DateTime.now())) : null;
      final data = await locator<GetAutonomy>()(_period, month: month);
      if (mounted) {
        setState(() {
          _data = data;
          if (_period == AutonomyPeriod.month) _month = data.month;
          // Open the cause that costs the most, so the page starts on the answer to "what slows us down".
          final costly = data.causes.where((c) => c.measured && c.devices > 0).toList()
            ..sort((x, y) => y.deviceDays.compareTo(x.deviceDays));
          _openCause ??= costly.firstOrNull?.key;
        });
      }
    } on ApiException catch (error) {
      if (mounted) setState(() => _error = error.message);
    } finally {
      if (mounted) setState(() => _loading = false);
    }
  }

  void _choosePeriod(AutonomyPeriod period) {
    if (_period == period) return;
    setState(() => _period = period);
    _load();
  }

  void _chooseMonth(String month) {
    setState(() => _month = month);
    _load();
  }

  Future<void> _patchNow(AutonomyCause cause) async {
    final result = await showDialog<AutonomyPatchNowResult>(
      context: context,
      builder: (_) => _PatchNowDialog(cause: cause),
    );
    if (result == null || !mounted) return;
    setState(() => _notice = 'Patch now sent to ${result.started} connected device${result.started == 1 ? '' : 's'}'
        '${result.queuedOffline > 0 ? ' and queued for ${result.queuedOffline} offline' : ''}. '
        'Each one decides for itself what to do about the person at it, and it is on each device\'s timeline '
        'with your name.${result.notRequested.isEmpty ? '' : ' Not sent to ${result.notRequested.length}: no longer registered.'}');
    await _load();
  }

  Future<void> _download(Autonomy data) async {
    setState(() {
      _saving = true;
      _notice = null;
    });
    try {
      final now = DateTime.now();
      final bytes = await reportToPdf(buildAutonomyReport(data, _options.copyWith(commentary: _commentary.text), now));
      await locator<FileSaver>().save(
        bytes,
        'kintsugi-patching-report-${DateFormat('yyyy-MM-dd').format(now)}.pdf',
        mimeType: 'application/pdf',
      );
      if (mounted) setState(() => _notice = 'The report was saved as a PDF. It was made in this browser; nothing was sent anywhere.');
    } on Object catch (error) {
      if (mounted) setState(() => _error = 'Could not make the PDF: $error');
    } finally {
      if (mounted) setState(() => _saving = false);
    }
  }

  @override
  Widget build(BuildContext context) {
    final data = _data;

    return PageScaffold(
      title: 'Autonomy',
      children: [
        SectionHeader(
          title: 'Why Patching Is Slow',
          hints: const [
            HintText(
              'What is holding patching up and how much it costs, who started each patch, and a report you '
              'can save as a PDF. Built from what the server records, so it covers macOS, Linux and Windows '
              'hosts alike.',
            ),
          ],
          actions: [SecondaryButton(label: 'Reload', onPressed: _loading ? null : _load)],
        ),
        if (_error != null) ...[AlertBox.error(_error!), const SizedBox(height: 12)],
        if (_notice != null) ...[AlertBox.success(_notice!), const SizedBox(height: 12)],
        Wrap(
          spacing: 8,
          runSpacing: 8,
          children: [
            _TabButton(label: 'Overview', selected: _tab == _Tab.overview, onTap: () => setState(() => _tab = _Tab.overview)),
            _TabButton(label: 'Report', selected: _tab == _Tab.report, onTap: () => setState(() => _tab = _Tab.report)),
          ],
        ),
        const SizedBox(height: 12),
        _PeriodBar(period: _period, data: data, onPeriod: _choosePeriod),
        const SizedBox(height: 16),
        if (data == null)
          EmptyPanel(_loading ? 'Loading…' : 'Could not load Autonomy.')
        else ...[
          if (_period == AutonomyPeriod.month) _MonthsChart(months: data.months, selected: _month, onSelect: _chooseMonth),
          if (_tab == _Tab.overview) ..._overview(data) else ..._report(data),
        ],
      ],
    );
  }

  List<Widget> _overview(Autonomy data) => [
        _Tiles(data: data),
        const SizedBox(height: 16),
        _Causes(
          data: data,
          open: _openCause,
          onToggle: (key) => setState(() => _openCause = _openCause == key ? null : key),
          onPatchNow: _patchNow,
        ),
        const SizedBox(height: 16),
        _DeviceHealthPanel(health: data.deviceHealth, totalHosts: data.totalHosts),
        const SizedBox(height: 16),
        _Kpis(data: data),
        const SizedBox(height: 16),
        _Trends(data: data),
        const SizedBox(height: 16),
        _DailyChart(data: data),
        const SizedBox(height: 16),
        const _LadderAndGaps(),
        const SizedBox(height: 16),
        _Events(events: data.events),
      ];

  List<Widget> _report(Autonomy data) {
    final document = buildAutonomyReport(data, _options.copyWith(commentary: _commentary.text), DateTime.now());

    Widget option(String label, bool value, void Function(bool) onChanged) => InkWell(
          onTap: () => setState(() => onChanged(!value)),
          child: Row(mainAxisSize: MainAxisSize.min, children: [
            Checkbox(value: value, onChanged: (v) => setState(() => onChanged(v ?? false))),
            Text(label),
            const SizedBox(width: 12),
          ]),
        );

    return [
      KintsugiPanel(
        child: Padding(
          padding: const EdgeInsets.all(14),
          child: Column(crossAxisAlignment: CrossAxisAlignment.start, children: [
            Wrap(
              spacing: 4,
              runSpacing: 4,
              crossAxisAlignment: WrapCrossAlignment.center,
              children: [
                Text('INCLUDE', style: AppTheme.display(color: context.palette.neonDim, size: 10.5, letterSpacing: 1)),
                const SizedBox(width: 8),
                option('Executive summary', _options.summary, (v) => _options = _options.copyWith(summary: v)),
                option('KPIs and RAG bands', _options.kpis, (v) => _options = _options.copyWith(kpis: v)),
                option('Why patching is slow', _options.causes, (v) => _options = _options.copyWith(causes: v)),
                option('Device health', _options.health, (v) => _options = _options.copyWith(health: v)),
                option('How to be faster', _options.faster, (v) => _options = _options.copyWith(faster: v)),
                option('Trends', _options.trends, (v) => _options = _options.copyWith(trends: v)),
                option('Patches over time', _options.chart, (v) => _options = _options.copyWith(chart: v)),
                PrimaryButton(label: 'Download PDF', busy: _saving, onPressed: () => _download(data)),
              ],
            ),
            const SizedBox(height: 12),
            LabelledField(
              label: 'Commentary',
              hints: const [HintText('Written here and printed in the report. It is the only part that is not worked out.')],
              child: KintsugiTextField(
                controller: _commentary,
                maxLines: 3,
                onChanged: (_) => setState(() {}),
              ),
            ),
          ]),
        ),
      ),
      const SizedBox(height: 6),
      const HintText('Made in your browser. Nothing is sent anywhere. The report names no devices: it says why patching is slow.'),
      const SizedBox(height: 12),
      ReportPreview(document: document),
    ];
  }
}

class _TabButton extends StatelessWidget {
  const _TabButton({required this.label, required this.selected, required this.onTap});

  final String label;
  final bool selected;
  final VoidCallback onTap;

  @override
  Widget build(BuildContext context) {
    final palette = context.palette;

    return InkWell(
      onTap: onTap,
      child: Container(
        padding: const EdgeInsets.symmetric(horizontal: 12, vertical: 8),
        decoration: BoxDecoration(
          border: Border.all(color: selected ? palette.neon : Colors.transparent),
          borderRadius: BorderRadius.circular(3),
        ),
        child: Text(
          label.toUpperCase(),
          style: AppTheme.display(color: selected ? palette.neon : palette.muted, size: 10.5, letterSpacing: 1),
        ),
      ),
    );
  }
}

class _PeriodBar extends StatelessWidget {
  const _PeriodBar({required this.period, required this.data, required this.onPeriod});

  final AutonomyPeriod period;
  final Autonomy? data;
  final void Function(AutonomyPeriod period) onPeriod;

  @override
  Widget build(BuildContext context) {
    final d = data;

    return KintsugiPanel(
      child: Padding(
        padding: const EdgeInsets.all(12),
        child: Wrap(
          spacing: 8,
          runSpacing: 8,
          crossAxisAlignment: WrapCrossAlignment.center,
          children: [
            const HintText('Period'),
            for (final p in AutonomyPeriod.values)
              _TabButton(label: p.label, selected: period == p, onTap: () => onPeriod(p)),
            if (d != null) ...[
              const SizedBox(width: 12),
              HintText(
                '${d.historyStartsUtc == null ? 'No history yet' : 'History starts ${DateFormat('yyyy-MM-dd').format(d.historyStartsUtc!.toLocal())}'}'
                ' · ${d.hostsWithEvents} of ${d.totalHosts} hosts have events',
              ),
            ],
          ],
        ),
      ),
    );
  }
}

/// A row of bars, one per day or month: the part of each that ran on the host's own schedule below,
/// the part an administrator started above. Tapping a bar, or its label, is optional.
class _Bars extends StatelessWidget {
  const _Bars({required this.bars, this.selected, this.onTap});

  /// (label, own, admin).
  final List<(String, int, int)> bars;
  final int? selected;
  final void Function(int index)? onTap;

  @override
  Widget build(BuildContext context) {
    final palette = context.palette;
    final max = bars.fold<int>(0, (m, b) => b.$2 + b.$3 > m ? b.$2 + b.$3 : m);
    const area = 120.0;

    return SizedBox(
      height: area + 48,
      child: Row(
        crossAxisAlignment: CrossAxisAlignment.stretch,
        children: [
          for (var i = 0; i < bars.length; i++)
            Expanded(
              // The label is inside the tappable column, so a month is chosen by its name as well as
              // by its bar: a bar of height zero would otherwise have nothing to click.
              child: InkWell(
                onTap: onTap == null ? null : () => onTap!(i),
                child: Container(
                  margin: const EdgeInsets.symmetric(horizontal: 2),
                  decoration: selected == i
                      ? BoxDecoration(border: Border.all(color: palette.neon), color: palette.neon.withValues(alpha: 0.08))
                      : null,
                  child: Column(
                    children: [
                      Expanded(
                        child: Column(
                          mainAxisAlignment: MainAxisAlignment.end,
                          children: [
                            if (bars.length <= 14) Text('${bars[i].$2 + bars[i].$3}', style: const TextStyle(fontSize: 11)),
                            if (max > 0 && bars[i].$3 > 0) Container(height: bars[i].$3 / max * area, color: palette.amber),
                            if (max > 0 && bars[i].$2 > 0) Container(height: bars[i].$2 / max * area, color: palette.neon),
                            if (max == 0 || bars[i].$2 + bars[i].$3 == 0) Container(height: 1, color: palette.border),
                          ],
                        ),
                      ),
                      const SizedBox(height: 4),
                      Text(
                        bars.length > 14 && i % 5 != 0 ? '' : bars[i].$1,
                        overflow: TextOverflow.clip,
                        maxLines: 1,
                        style: TextStyle(fontSize: 11, color: selected == i ? palette.neon : palette.muted),
                      ),
                      const SizedBox(height: 4),
                    ],
                  ),
                ),
              ),
            ),
        ],
      ),
    );
  }
}

class _Legend extends StatelessWidget {
  const _Legend();

  @override
  Widget build(BuildContext context) {
    final palette = context.palette;
    Widget swatch(Color c, String t) => Row(mainAxisSize: MainAxisSize.min, children: [
          Container(width: 11, height: 11, color: c),
          const SizedBox(width: 5),
          HintText(t),
          const SizedBox(width: 14),
        ]);

    return Wrap(children: [
      swatch(palette.neon, "ran on the host's own schedule"),
      swatch(palette.amber, 'started by an administrator'),
    ]);
  }
}

class _MonthsChart extends StatelessWidget {
  const _MonthsChart({required this.months, required this.selected, required this.onSelect});

  final List<AutonomyMonth> months;
  final String? selected;
  final void Function(String month) onSelect;

  @override
  Widget build(BuildContext context) {
    if (months.isEmpty) return const SizedBox.shrink();

    return Padding(
      padding: const EdgeInsets.only(bottom: 16),
      child: KintsugiPanel(
        child: Padding(
          padding: const EdgeInsets.all(16),
          child: Column(
            crossAxisAlignment: CrossAxisAlignment.start,
            children: [
              Text('PATCHES PER MONTH', style: Theme.of(context).textTheme.titleMedium),
              const SizedBox(height: 4),
              const HintText('Click a month to see what happened in it.'),
              const SizedBox(height: 6),
              const _Legend(),
              const SizedBox(height: 8),
              _Bars(
                bars: [for (final m in months) (monthShort(m.month), m.own, m.admin)],
                selected: months.indexWhere((m) => m.month == selected),
                onTap: (i) => onSelect(months[i].month),
              ),
              if (selected != null) ...[
                const SizedBox(height: 8),
                HintText('Showing ${monthLabel(selected!)} below.'),
              ],
            ],
          ),
        ),
      ),
    );
  }
}

class _DailyChart extends StatelessWidget {
  const _DailyChart({required this.data});

  final Autonomy data;

  @override
  Widget build(BuildContext context) {
    final fmt = data.buckets.length <= 7 ? DateFormat('E') : DateFormat('d');

    return KintsugiPanel(
      child: Padding(
        padding: const EdgeInsets.all(16),
        child: Column(
          crossAxisAlignment: CrossAxisAlignment.start,
          children: [
            Text('PATCHES OVER TIME', style: Theme.of(context).textTheme.titleMedium),
            const SizedBox(height: 6),
            const _Legend(),
            const SizedBox(height: 8),
            _Bars(bars: [for (final b in data.buckets) (fmt.format(b.startUtc), b.own, b.admin)]),
          ],
        ),
      ),
    );
  }
}


class _Tiles extends StatelessWidget {
  const _Tiles({required this.data});

  final Autonomy data;

  @override
  Widget build(BuildContext context) {
    final palette = context.palette;
    final p = data.patches;
    final pct = data.totalHosts == 0 ? 0 : (data.devicesDelayed * 100 / data.totalHosts);
    final tiles = <(String, String, String, Color)>[
      ('Patches', NumberFormat.decimalPattern().format(p.total), 'successful, apps and operating system', palette.neon),
      ("Ran on the host's own schedule", '${p.percent(p.ownSchedule)}%', '${p.ownSchedule} of ${p.total} patches', palette.neon),
      ('Started by an administrator', '${p.percent(p.adminStarted)}%', '${p.adminStarted} of ${p.total} patches (Patch now)', palette.amber),
      (
        'Devices delayed',
        '${data.devicesDelayed}',
        'of ${data.totalHosts} (${pct.toStringAsFixed(1)}%) · ${data.totalDeviceDays.round()} device-days',
        data.devicesDelayed > 0 ? palette.red : palette.neon,
      ),
    ];

    return Wrap(
      spacing: 12,
      runSpacing: 12,
      children: [
        for (final (label, value, sub, color) in tiles)
          SizedBox(
            width: 270,
            child: KintsugiPanel(
              child: Padding(
                padding: const EdgeInsets.all(14),
                child: Column(
                  crossAxisAlignment: CrossAxisAlignment.start,
                  children: [
                    Text(value, style: AppTheme.display(color: color, size: 28)),
                    const SizedBox(height: 4),
                    Text(label.toUpperCase(), style: AppTheme.display(color: palette.muted, size: 9.9, letterSpacing: 0.99)),
                    const SizedBox(height: 2),
                    HintText(sub),
                  ],
                ),
              ),
            ),
          ),
      ],
    );
  }
}

/// The causes, ranked by what they cost. A cause is a row with its counts, not a list of devices.
class _Causes extends StatelessWidget {
  const _Causes({required this.data, required this.open, required this.onToggle, required this.onPatchNow});

  final Autonomy data;
  final String? open;
  final void Function(String key) onToggle;
  final void Function(AutonomyCause cause) onPatchNow;

  @override
  Widget build(BuildContext context) {
    final ranked = [
      ...data.causes.where((c) => c.measured).toList()..sort((a, b) => b.deviceDays.compareTo(a.deviceDays)),
      ...data.causes.where((c) => !c.measured),
    ];

    return KintsugiPanel(
      child: Padding(
        padding: const EdgeInsets.all(16),
        child: Column(
          crossAxisAlignment: CrossAxisAlignment.start,
          children: [
            Text('WHY PATCHING IS SLOW, RANKED BY WHAT IT COSTS', style: Theme.of(context).textTheme.titleMedium),
            const SizedBox(height: 4),
            const HintText(
              'Cost is device-days of waiting: for each affected device, the days it has been waiting or failing, '
              'added up. It ranks causes by how much patching they slow down, not by how many devices they touch.',
            ),
            if (ranked.every((c) => c.devices == 0 && c.measured))
              const Padding(padding: EdgeInsets.only(top: 12), child: HintText('Nothing is holding patching up right now.')),
            for (final cause in ranked)
              Padding(
                padding: const EdgeInsets.only(top: 12),
                child: _CauseCard(cause: cause, open: open == cause.key, onToggle: () => onToggle(cause.key), onPatchNow: () => onPatchNow(cause)),
              ),
          ],
        ),
      ),
    );
  }
}

class _CauseCard extends StatelessWidget {
  const _CauseCard({required this.cause, required this.open, required this.onToggle, required this.onPatchNow});

  final AutonomyCause cause;
  final bool open;
  final VoidCallback onToggle;
  final VoidCallback onPatchNow;

  @override
  Widget build(BuildContext context) {
    final palette = context.palette;
    final color = !cause.measured
        ? palette.muted
        : cause.category == 'Restart'
            ? palette.amber
            : cause.devices > 0
                ? palette.red
                : palette.neon;

    return Container(
      decoration: BoxDecoration(border: Border.all(color: palette.border), borderRadius: BorderRadius.circular(4)),
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.stretch,
        children: [
          InkWell(
            onTap: onToggle,
            child: Padding(
              padding: const EdgeInsets.all(12),
              child: Row(
                children: [
                  SizedBox(
                    width: 56,
                    child: Text(cause.measured ? '${cause.devices}' : '–', textAlign: TextAlign.center, style: AppTheme.display(color: color, size: 22)),
                  ),
                  const SizedBox(width: 10),
                  Expanded(
                    child: Column(crossAxisAlignment: CrossAxisAlignment.start, children: [
                      Text(cause.title, style: const TextStyle(fontWeight: FontWeight.w600)),
                      HintText(cause.measured
                          ? '${cause.devices} device${cause.devices == 1 ? '' : 's'} · ${cause.deviceDays} device-days · ${cause.sharePercent}% of the delay'
                          : 'Not measured yet'),
                    ]),
                  ),
                  StatusChip(cause.measured ? 'Measured now' : 'Not built yet', statusKey: cause.measured ? 'up-to-date' : 'unknown'),
                ],
              ),
            ),
          ),
          if (open)
            Padding(
              padding: const EdgeInsets.fromLTRB(16, 0, 16, 16),
              child: Column(
                crossAxisAlignment: CrossAxisAlignment.start,
                children: [
                  if (cause.explanation != null) Text(cause.explanation!),
                  const SizedBox(height: 8),
                  HintText('How it slows patching: ${howItSlows(cause)}'),
                  const SizedBox(height: 10),
                  Container(
                    padding: const EdgeInsets.all(12),
                    decoration: BoxDecoration(border: Border.all(color: palette.green), borderRadius: BorderRadius.circular(3), color: palette.green.withValues(alpha: 0.06)),
                    child: Column(crossAxisAlignment: CrossAxisAlignment.start, children: [
                      Text('HOW TO REMEDIATE', style: AppTheme.display(color: palette.green, size: 10.5, letterSpacing: 1)),
                      const SizedBox(height: 6),
                      Text(fixFor(cause)),
                    ]),
                  ),
                  if (cause.canPatchNow) ...[
                    const SizedBox(height: 12),
                    Wrap(spacing: 12, runSpacing: 6, crossAxisAlignment: WrapCrossAlignment.center, children: [
                      PrimaryButton(
                        label: cause.patchNowConnected > 0
                            ? 'Patch now · ${cause.patchNowConnected} connected${cause.patchNowOffline > 0 ? ' (+${cause.patchNowOffline} offline)' : ''}'
                            : 'Queue Patch now · ${cause.patchNowOffline} offline',
                        onPressed: onPatchNow,
                      ),
                      HintText(
                        cause.patchNowConnected == 0
                            ? 'None is connected right now. A request waits up to 24 hours for them to check in. Windows devices only.'
                            : 'Windows devices only. Devices on macOS and Linux are not patched from here.',
                      ),
                    ]),
                  ],
                  if (cause.devices > 0) _CauseDevices(causeKey: cause.key, total: cause.devices),
                ],
              ),
            ),
        ],
      ),
    );
  }
}

/// The devices in one cause, a page at a time, with a search. Closed until asked for: a cause of a
/// thousand devices costs nothing to show until somebody wants the names.
class _CauseDevices extends StatefulWidget {
  const _CauseDevices({required this.causeKey, required this.total});

  final String causeKey;
  final int total;

  @override
  State<_CauseDevices> createState() => _CauseDevicesState();
}

class _CauseDevicesState extends State<_CauseDevices> {
  static const _page = 25;

  bool _shown = false;
  bool _loading = false;
  final _searchController = TextEditingController();
  String _search = '';
  int _count = _page;
  AutonomyDevicesPage? _result;
  String? _error;

  @override
  void dispose() {
    _searchController.dispose();
    super.dispose();
  }

  Future<void> _load() async {
    setState(() {
      _loading = true;
      _error = null;
    });
    try {
      final page = await locator<GetAutonomyCauseDevices>()(widget.causeKey, take: _count, search: _search);
      if (mounted) setState(() => _result = page);
    } on ApiException catch (error) {
      if (mounted) setState(() => _error = error.message);
    } finally {
      if (mounted) setState(() => _loading = false);
    }
  }

  @override
  Widget build(BuildContext context) {
    final result = _result;

    return Padding(
      padding: const EdgeInsets.only(top: 12),
      child: Column(crossAxisAlignment: CrossAxisAlignment.start, children: [
        LinkText(
          label: _shown ? 'Hide devices' : 'Show devices (${widget.total})',
          onTap: () {
            setState(() => _shown = !_shown);
            if (_shown && result == null) _load();
          },
        ),
        if (_shown) ...[
          const SizedBox(height: 8),
          SizedBox(
            width: 280,
            child: KintsugiTextField(
              controller: _searchController,
              hintText: 'Search these devices',
              onChanged: (v) {
                _search = v;
                _count = _page;
                _load();
              },
            ),
          ),
          if (_error != null) Padding(padding: const EdgeInsets.only(top: 8), child: AlertBox.error(_error!)),
          if (result != null)
            Padding(
              padding: const EdgeInsets.only(top: 8),
              child: Column(children: [
                for (final d in result.items)
                  Padding(
                    padding: const EdgeInsets.symmetric(vertical: 3),
                    child: Row(children: [
                      Expanded(flex: 3, child: Text(d.hostname)),
                      Expanded(flex: 6, child: HintText(d.detail)),
                      LinkText(label: 'Open', onTap: () => context.go(Routes.hostActivity(d.hostId))),
                    ]),
                  ),
                const SizedBox(height: 6),
                Align(
                  alignment: Alignment.centerLeft,
                  child: Row(mainAxisSize: MainAxisSize.min, children: [
                    HintText('Showing ${result.items.length} of ${result.total}'),
                    if (result.items.length < result.total) ...[
                      const SizedBox(width: 12),
                      LinkText(
                        label: 'Show 25 more',
                        onTap: () {
                          _count += _page;
                          _load();
                        },
                      ),
                    ],
                    if (_loading) const Padding(padding: EdgeInsets.only(left: 10), child: HintText('Loading…')),
                  ]),
                ),
              ]),
            ),
        ],
      ]),
    );
  }
}

/// Confirms Patch now for a whole cause. It says how many are connected and start now, offers the
/// offline ones as a separate choice, and above [bigFleet] asks for the number to be typed, because a
/// slip at that size reaches a lot of people.
class _PatchNowDialog extends StatefulWidget {
  const _PatchNowDialog({required this.cause});

  final AutonomyCause cause;

  /// Above this many devices, the number has to be typed to confirm.
  static const bigFleet = 50;

  @override
  State<_PatchNowDialog> createState() => _PatchNowDialogState();
}

class _PatchNowDialogState extends State<_PatchNowDialog> {
  bool _includeOffline = false;
  bool _sending = false;
  String? _error;
  final _typed = TextEditingController();

  AutonomyCause get _cause => widget.cause;
  int get _connected => _cause.patchNowConnected;
  int get _targets => _connected + (_includeOffline || _connected == 0 ? _cause.patchNowOffline : 0);
  bool get _needsTyping => _targets > _PatchNowDialog.bigFleet;
  bool get _typedOk => !_needsTyping || _typed.text.trim() == '$_targets';

  @override
  void dispose() {
    _typed.dispose();
    super.dispose();
  }

  Future<void> _send() async {
    setState(() {
      _sending = true;
      _error = null;
    });
    try {
      final result = await locator<PatchAutonomyCause>()(
        _cause.key,
        expectedConnected: _connected,
        includeOffline: _includeOffline || _connected == 0,
      );
      if (mounted) Navigator.of(context).pop(result);
    } on ApiException catch (error) {
      if (mounted) {
        setState(() {
          _sending = false;
          _error = error.message;
        });
      }
    }
  }

  @override
  Widget build(BuildContext context) {
    final palette = context.palette;

    return AlertDialog(
      backgroundColor: palette.backgroundAlt,
      shape: RoundedRectangleBorder(borderRadius: BorderRadius.circular(6), side: BorderSide(color: palette.border)),
      title: Text(
        _connected > 0 ? 'Patch $_connected Windows device${_connected == 1 ? '' : 's'} now' : 'Queue Patch now for ${_cause.patchNowOffline} offline Windows devices',
        style: Theme.of(context).textTheme.titleLarge,
      ),
      content: SizedBox(
        width: 540,
        child: SingleChildScrollView(
          child: Column(mainAxisSize: MainAxisSize.min, crossAxisAlignment: CrossAxisAlignment.start, children: [
            HintText(_cause.title),
            const SizedBox(height: 10),
            if (_connected > 0)
              AlertBox.info(
                '$_connected connected device${_connected == 1 ? '' : 's'} will start now. Each one decides for itself what to do '
                'about the person at it: somebody there gets a 5-minute "save your work" warning that cannot be '
                'delayed; behind a locked screen it starts right away. A restart happens only if Windows says it '
                'needs one, with Windows\' own 5-minute countdown, and not while anybody signed in is locked or '
                'disconnected. A device nobody is signed in to starts when someone signs in.',
              )
            else
              const AlertBox.info(
                'None of these is connected right now. The request is kept for 24 hours and starts the moment each '
                'one checks in. Somebody working at it then gets the 5-minute warning first.',
              ),
            if (_connected > 0 && _cause.patchNowOffline > 0) ...[
              const SizedBox(height: 12),
              InkWell(
                onTap: () => setState(() => _includeOffline = !_includeOffline),
                child: Row(children: [
                  Checkbox(value: _includeOffline, onChanged: (v) => setState(() => _includeOffline = v ?? false)),
                  Expanded(child: Text('Also queue the ${_cause.patchNowOffline} offline device${_cause.patchNowOffline == 1 ? '' : 's'}')),
                ]),
              ),
              const HintText('They keep the request for 24 hours and start when they check in. Off by default.'),
            ],
            const SizedBox(height: 12),
            const HintText(
              'Only the fixed Windows Update install runs. Nothing typed here is sent to a device. Every press is '
              'recorded on each device\'s timeline with your name.',
            ),
            if (_needsTyping) ...[
              const SizedBox(height: 12),
              LabelledField(
                label: 'Type $_targets to confirm',
                hints: const [HintText('This reaches many devices at once.')],
                child: KintsugiTextField(controller: _typed, onChanged: (_) => setState(() {})),
              ),
            ],
            if (_error != null) ...[const SizedBox(height: 10), AlertBox.error(_error!)],
          ]),
        ),
      ),
      actions: [
        SecondaryButton(label: 'Cancel', onPressed: _sending ? null : () => Navigator.of(context).pop()),
        PrimaryButton(
          label: _connected > 0 ? 'Patch $_connected now' : 'Queue for ${_cause.patchNowOffline}',
          busy: _sending,
          onPressed: _typedOk ? _send : null,
        ),
      ],
    );
  }
}

/// Devices that shut down by themselves or logged hardware errors, read by each agent from its own
/// operating system's log. A device per row with its reason, and the worst few: the counts are the whole
/// fleet's. Opening a row reads that device's own events, each with whether Kintsugi did anything near it.
class _DeviceHealthPanel extends StatelessWidget {
  const _DeviceHealthPanel({required this.health, required this.totalHosts});

  final AutonomyDeviceHealth health;
  final int totalHosts;

  @override
  Widget build(BuildContext context) {
    final palette = context.palette;

    return KintsugiPanel(
      child: Padding(
        padding: const EdgeInsets.all(16),
        child: Column(
          crossAxisAlignment: CrossAxisAlignment.start,
          children: [
            Text('DEVICE HEALTH', style: Theme.of(context).textTheme.titleMedium),
            const SizedBox(height: 4),
            const HintText(deviceHealthRule),
            const SizedBox(height: 10),
            Wrap(spacing: 18, runSpacing: 6, children: [
              Text('${health.unstable} unstable', style: TextStyle(color: health.unstable > 0 ? palette.red : palette.text, fontWeight: FontWeight.w700)),
              Text('${health.watch} to watch', style: TextStyle(color: health.watch > 0 ? palette.amber : palette.text, fontWeight: FontWeight.w700)),
              HintText('of $totalHosts devices'),
            ]),
            if (health.items.isEmpty)
              const Padding(
                padding: EdgeInsets.only(top: 10),
                child: HintText(
                  'No device has reported a shutdown it did not ask for or a hardware error. A device appears here when its '
                  'agent reports one: Kintsugi reads each operating system\'s own log, read-only, and never sends the whole log.',
                ),
              ),
            const SizedBox(height: 12),
            const DeviceSearch(),
            for (final device in health.items) _HealthRow(device: device),
            if (health.unstable + health.watch > health.items.length)
              Padding(
                padding: const EdgeInsets.only(top: 8),
                child: HintText('Showing the worst ${health.items.length} of ${health.unstable + health.watch}.'),
              ),
          ],
        ),
      ),
    );
  }
}

class _HealthRow extends StatefulWidget {
  const _HealthRow({required this.device});

  final AutonomyHealthDevice device;

  @override
  State<_HealthRow> createState() => _HealthRowState();
}

class _HealthRowState extends State<_HealthRow> {
  bool _open = false;
  HostActivity? _activity;
  String? _error;

  Future<void> _toggle() async {
    setState(() => _open = !_open);
    if (!_open || _activity != null) return;
    try {
      final activity = await locator<GetHostActivity>()(widget.device.hostId);
      if (mounted) setState(() => _activity = activity);
    } on ApiException catch (error) {
      if (mounted) setState(() => _error = error.message);
    }
  }

  @override
  Widget build(BuildContext context) {
    final d = widget.device;
    final stamp = DateFormat('yyyy-MM-dd HH:mm');
    final events = (_activity?.events ?? const <HostEvent>[]).where((e) => e.kind.group == HostEventGroup.health).toList();

    return DeviceHealthRowCard(
      hostname: d.hostname,
      detail: d.why,
      lastEventUtc: d.lastEventUtc,
      status: d.isUnstable ? DeviceHealthStatus.unstable : DeviceHealthStatus.watch,
      onToggle: _toggle,
      onOpenReport: () => context.go(Routes.deviceReport(d.hostId)),
      expanded: !_open
          ? null
          : Column(crossAxisAlignment: CrossAxisAlignment.start, children: [
              if (_error != null) AlertBox.error(_error!),
              if (_activity == null && _error == null) const HintText('Loading…'),
              for (final e in events.take(12))
                Padding(
                  padding: const EdgeInsets.symmetric(vertical: 4),
                  child: Row(crossAxisAlignment: CrossAxisAlignment.start, children: [
                    SizedBox(width: 140, child: HintText(stamp.format(e.occurredUtc.toLocal()))),
                    Expanded(
                      child: Column(crossAxisAlignment: CrossAxisAlignment.start, children: [
                        Text(e.summary),
                        if (e.detail != null) HintText(e.detail!),
                        HintText(kintsugiNear(e, _activity!.events)),
                      ]),
                    ),
                  ]),
                ),
              const SizedBox(height: 6),
              LinkText(label: 'Open this host', onTap: () => context.go(Routes.hostActivity(d.hostId))),
            ]),
    );
  }
}

/// The key figures against their bands. The status is a word as well as a colour.
class _Kpis extends StatelessWidget {
  const _Kpis({required this.data});

  final Autonomy data;

  @override
  Widget build(BuildContext context) {
    if (data.kpis.isEmpty) return const SizedBox.shrink();

    String statusKey(String s) => switch (s) {
          'R' => 'patch-failed',
          'A' => 'update-available',
          'G' => 'up-to-date',
          _ => 'unknown',
        };

    return KintsugiTable(
      minWidth: 900,
      columns: const [
        TableColumnSpec(label: 'KPI'),
        TableColumnSpec(label: 'Green', width: FixedColumnWidth(80)),
        TableColumnSpec(label: 'Amber', width: FixedColumnWidth(80)),
        TableColumnSpec(label: 'Red', width: FixedColumnWidth(80)),
        TableColumnSpec(label: 'Now', width: FixedColumnWidth(90), alignRight: true),
        TableColumnSpec(label: 'Prior', width: FixedColumnWidth(90), alignRight: true),
        TableColumnSpec(label: 'Change', width: FixedColumnWidth(100), alignRight: true),
        TableColumnSpec(label: 'Status', width: FixedColumnWidth(120)),
      ],
      rows: [
        for (final k in data.kpis)
          KintsugiTableRow(cells: [
            Column(crossAxisAlignment: CrossAxisAlignment.start, children: [Text(k.definition), HintText('${k.category} · ${k.numerator} of ${k.denominator}')]),
            Text(bandsOf(k).green),
            Text(bandsOf(k).amber),
            Text(bandsOf(k).red),
            Text(kpiValue(k)),
            k.prior == null ? const NoValue() : Text(kpiValue(k, k.prior)),
            Text(kpiChange(k)),
            StatusChip(statusWord(k.status), statusKey: statusKey(k.status)),
          ]),
      ],
    );
  }
}

/// Two trends against their targets, from the daily snapshots. Honest when there is not enough
/// history yet: a trend needs at least two days.
class _Trends extends StatelessWidget {
  const _Trends({required this.data});

  final Autonomy data;

  @override
  Widget build(BuildContext context) {
    final lines = <ReportLine>[];
    for (final id in trendKpis) {
      final kpi = data.kpis.where((k) => k.id == id).firstOrNull;
      final points = data.historyOf(id);
      if (kpi == null || points.length < 2) continue;
      lines.add(ReportLine(
        kpi.definition,
        [for (final p in points) DateFormat('d MMM').format(p.dateUtc.toLocal())],
        [for (final p in points) p.value],
        target: kpi.green,
        targetLabel: 'target ${bandsOf(kpi).green}',
        suffix: kpi.isPercent ? '%' : '',
      ));
    }

    return KintsugiPanel(
      child: Padding(
        padding: const EdgeInsets.all(16),
        child: Column(crossAxisAlignment: CrossAxisAlignment.start, children: [
          Text('TRENDS AGAINST THE TARGET', style: Theme.of(context).textTheme.titleMedium),
          const SizedBox(height: 6),
          if (lines.isEmpty)
            const HintText(
              'History is building. Kintsugi records each figure once a day, so a trend needs at least two days '
              'of snapshots. The first is recorded a few minutes after the server starts.',
            )
          else ...[
            const HintText('The dashed line is the green target.'),
            for (final line in lines) ReportPreviewBlock(block: line),
          ],
        ]),
      ),
    );
  }
}

class _LadderAndGaps extends StatelessWidget {
  const _LadderAndGaps();

  @override
  Widget build(BuildContext context) {
    Widget panel(String title, List<Widget> children) => KintsugiPanel(
          child: Padding(
            padding: const EdgeInsets.all(16),
            child: Column(crossAxisAlignment: CrossAxisAlignment.start, children: [
              Text(title.toUpperCase(), style: Theme.of(context).textTheme.titleMedium),
              const SizedBox(height: 8),
              ...children,
            ]),
          ),
        );

    final ladder = panel('How far the hosts decide for themselves', [
      const HintText('An honest status, step by step. It moves when the agents report more.'),
      for (final (question, detail, partial) in autonomyLadder)
        Padding(
          padding: const EdgeInsets.only(top: 10),
          child: Row(crossAxisAlignment: CrossAxisAlignment.start, children: [
            StatusChip(partial ? 'Partial' : 'Not yet', statusKey: partial ? 'update-available' : 'unknown'),
            const SizedBox(width: 10),
            Expanded(child: Column(crossAxisAlignment: CrossAxisAlignment.start, children: [Text(question), HintText(detail)])),
          ]),
        ),
    ]);

    final gaps = panel('Not measured yet', [
      for (final item in notMeasuredYet) Padding(padding: const EdgeInsets.only(top: 6), child: Text('•  $item')),
      const SizedBox(height: 8),
      const HintText(
        'These are decided on the host and only logged there. Showing them needs a change in all '
        'three agents, and they describe what a person was doing at their computer. This page does '
        'not use any of it.',
      ),
    ]);

    return LayoutBuilder(
      builder: (context, box) => box.maxWidth < 900
          ? Column(crossAxisAlignment: CrossAxisAlignment.stretch, children: [ladder, const SizedBox(height: 12), gaps])
          : Row(crossAxisAlignment: CrossAxisAlignment.start, children: [
              Expanded(child: ladder),
              const SizedBox(width: 12),
              Expanded(child: gaps),
            ]),
    );
  }
}

class _Events extends StatelessWidget {
  const _Events({required this.events});

  final List<AutonomyEvent> events;

  @override
  Widget build(BuildContext context) {
    final palette = context.palette;
    final stamp = DateFormat('yyyy-MM-dd HH:mm');

    Color dot(String kind) => switch (kind) {
          'PatchFailed' => palette.red,
          'ApplicationPatched' || 'OperatingSystemPatched' => palette.green,
          _ => palette.amber,
        };

    return KintsugiPanel(
      child: Padding(
        padding: const EdgeInsets.all(16),
        child: Column(
          crossAxisAlignment: CrossAxisAlignment.start,
          children: [
            Text('RECENT EVENTS', style: Theme.of(context).textTheme.titleMedium),
            const SizedBox(height: 6),
            if (events.isEmpty) const HintText('Nothing recorded in this period.'),
            for (final e in events)
              Padding(
                padding: const EdgeInsets.symmetric(vertical: 6),
                child: Row(crossAxisAlignment: CrossAxisAlignment.start, children: [
                  SizedBox(width: 140, child: HintText(stamp.format(e.occurredUtc.toLocal()))),
                  Padding(
                    padding: const EdgeInsets.only(top: 5, right: 10),
                    child: Container(width: 10, height: 10, decoration: BoxDecoration(color: dot(e.kind), shape: BoxShape.circle)),
                  ),
                  Expanded(
                    child: Column(crossAxisAlignment: CrossAxisAlignment.start, children: [
                      Text(eventSummary(e)),
                      HintText('${e.hostname} · by ${e.actor ?? 'the host'}'),
                    ]),
                  ),
                ]),
              ),
          ],
        ),
      ),
    );
  }
}
