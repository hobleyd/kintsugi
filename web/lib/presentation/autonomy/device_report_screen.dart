import 'dart:async';

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
import '../../core/widgets/kintsugi_table.dart';
import '../../core/widgets/page_scaffold.dart';
import '../../core/widgets/panel.dart';
import '../../core/widgets/status_chip.dart';
import '../../core/widgets/text_bits.dart';
import '../../domain/entities/device_report.dart';
import '../../domain/usecases/device_report_usecases.dart';
import 'autonomy_pdf.dart';
import 'device_report_logs.dart';
import 'device_report_model.dart';
import 'device_report_pdf_model.dart';
import 'device_report_timeline.dart';

/// One device: what happened to it, whether Kintsugi was involved, what it could be, and the log an
/// administrator asked its agent to collect. One device at a time; it never lists a group.
///
/// Opened from a device in Autonomy's Device health. The explanations are the server's: this page draws
/// what it is given and does not rank or judge anything itself, so the "what it could be" text is the
/// same wherever it is read.
class DeviceReportScreen extends StatefulWidget {
  const DeviceReportScreen({super.key, required this.hostId});

  final String hostId;

  /// How often the latest collection is read while the device has not answered yet.
  static const pollInterval = Duration(seconds: 5);

  @override
  State<DeviceReportScreen> createState() => _DeviceReportScreenState();
}

class _DeviceReportScreenState extends State<DeviceReportScreen> {
  int _days = 7;
  DeviceReport? _report;
  LogCollection? _logs;

  /// The last collection that completed. Kept apart from [_logs], which is the newest and may be a request
  /// that is still waiting or has failed.
  LogCollection? _lastCompleted;
  DateTime _now = DateTime.now();
  DeviceReportEvent? _selected;
  bool _showAll = false;
  bool _loading = false;
  bool _saving = false;
  String? _error;
  String? _notice;
  Timer? _poll;
  bool _polling = false;

  @override
  void initState() {
    super.initState();
    _load();
  }

  @override
  void dispose() {
    _poll?.cancel();
    super.dispose();
  }

  Future<void> _load() async {
    setState(() {
      _loading = true;
      _error = null;
    });
    try {
      final report = await locator<GetDeviceReport>()(widget.hostId, days: _days);
      if (!mounted) return;
      setState(() {
        _report = report;
        _logs = report.logs;
        _lastCompleted = report.lastCompletedLogs;
        _now = DateTime.now();
        if (_selected != null && !report.events.contains(_selected)) _selected = null;
      });
      _syncPolling();
    } on ApiException catch (error) {
      if (mounted) setState(() => _error = error.message);
    } finally {
      if (mounted) setState(() => _loading = false);
    }
  }

  /// The log the page can read: the newest when it completed, else the last one that did. A newer request that
  /// is waiting or failed does not take the readable log away.
  LogCollection? get _readable => (_logs?.isCompleted ?? false) ? _logs : _lastCompleted;

  void _choosePeriod(int days) {
    if (days == _days) return;
    setState(() {
      _days = days;
      _selected = null;
      _showAll = false;
    });
    _load();
  }

  /// Polls while the latest collection is waiting on the device, and only then.
  void _syncPolling() {
    final waiting = _logs?.isOpen ?? false;
    if (waiting && _poll == null) {
      _poll = Timer.periodic(DeviceReportScreen.pollInterval, (_) => _tick());
    } else if (!waiting) {
      _poll?.cancel();
      _poll = null;
    }
  }

  Future<void> _tick() async {
    if (_polling) return;
    _polling = true;
    try {
      final latest = await locator<GetLatestLogCollection>()(widget.hostId);
      if (!mounted) return;
      if (latest != null && latest.isOpen) {
        setState(() => _logs = latest);
        return;
      }
      if (latest == null) {
        setState(() => _logs = null);
        _syncPolling();
        return;
      }
      // Completed or failed: the hypotheses can read the log now, so read the whole report again.
      _poll?.cancel();
      _poll = null;
      await _load();
    } on ApiException {
      // A poll that fails is tried again at the next tick; the page keeps what it has.
    } finally {
      _polling = false;
    }
  }

  Future<void> _collect() async {
    final report = _report;
    if (report == null) return;
    final created = await showCollectLogsDialog(context, report.host);
    if (created == null || !mounted) return;
    setState(() {
      _logs = created;
      _notice = 'Requested. The device will be asked at its next check-in.';
    });
    _syncPolling();
  }

  Future<void> _removeLogs(LogCollection logs, String done) async {
    try {
      await locator<DeleteLogCollection>()(widget.hostId, logs.id);
      if (!mounted) return;
      setState(() {
        _logs = null;
        if (_lastCompleted?.id == logs.id) _lastCompleted = null;
        _notice = done;
      });
      _syncPolling();
      // What the newest collection is now, and what the guesses read, both depend on what was removed.
      await _load();
    } on ApiException catch (error) {
      if (mounted) setState(() => _error = error.message);
    }
  }

  void _select(DeviceReportEvent event) => setState(() {
        _selected = _selected == event ? null : event;
        _showAll = false;
      });

  Future<void> _downloadPdf(DeviceReport report, DeviceReportOptions options) async {
    setState(() {
      _saving = true;
      _notice = null;
      _error = null;
    });
    try {
      final logs = _readable;
      final groups = <LogLinesGroup>[];
      if (options.logLines && logs != null) {
        final selected = _selected;
        final page = await locator<GetLogEntries>()(
          widget.hostId,
          logs.id,
          severity: LogSeverityFilter.errors,
          around: selected?.occurredUtc,
          take: maxPdfLogLines,
        );
        groups.add(LogLinesGroup(around: selected, lines: page.items.take(maxPdfLogLines).toList()));
      }

      final now = DateTime.now();
      final bytes = await reportToPdf(buildDeviceReport(report, options, now, logGroups: groups));
      final host = report.host.hostname.replaceAll(RegExp(r'[^A-Za-z0-9._-]'), '_');
      await locator<FileSaver>().save(
        bytes,
        'kintsugi-$host-report-${DateFormat('yyyy-MM-dd').format(now)}.pdf',
        mimeType: 'application/pdf',
      );
      if (mounted) setState(() => _notice = 'The report was saved as a PDF. It was made in this browser; nothing was sent anywhere.');
    } on ApiException catch (error) {
      if (mounted) setState(() => _error = 'Could not read the log lines for the PDF: ${error.message}');
    } on Object catch (error) {
      if (mounted) setState(() => _error = 'Could not make the PDF: $error');
    } finally {
      if (mounted) setState(() => _saving = false);
    }
  }

  Future<void> _openPdfDialog(DeviceReport report) async {
    final options = await showDialog<DeviceReportOptions>(
      context: context,
      builder: (_) => _PdfDialog(report: report, logsReady: _readable != null, selected: _selected),
    );
    if (options != null && mounted) await _downloadPdf(report, options);
  }

  @override
  Widget build(BuildContext context) {
    final report = _report;
    final logs = _logs;
    final readable = _readable;

    return PageScaffold(
      title: 'Device report',
      children: [
        Padding(
          padding: const EdgeInsets.only(bottom: 12),
          child: Wrap(spacing: 8, crossAxisAlignment: WrapCrossAlignment.center, children: [
            LinkText(label: 'Autonomy', onTap: () => context.go(Routes.autonomy)),
            const HintText('›'),
            HintText(report?.host.hostname ?? 'Device'),
          ]),
        ),
        if (_error != null) ...[
          AlertBox.error(_error!),
          if (report == null) SecondaryButton(label: 'Try again', onPressed: _loading ? null : _load),
        ],
        if (_notice != null) AlertBox.success(_notice!),
        if (report == null)
          EmptyPanel(_loading ? 'Loading…' : 'Could not load the device report.')
        else ...[
          _Header(report: report, now: _now),
          const SizedBox(height: 14),
          _Toolbar(
            days: _days,
            onPeriod: _choosePeriod,
            onCollect: logs?.isOpen ?? false ? null : _collect,
            onPdf: _saving ? null : () => _openPdfDialog(report),
            saving: _saving,
          ),
          const SizedBox(height: 14),
          _WhatHappened(report: report),
          const SizedBox(height: 14),
          _TimelineSection(
            report: report,
            now: _now,
            selected: _selected,
            logsReady: readable != null,
            onSelect: _select,
          ),
          const SizedBox(height: 14),
          _Involved(report: report, selected: _selected, onSelect: _select),
          const SizedBox(height: 14),
          _Hypotheses(
            hypotheses: report.hypotheses,
            // The guesses read the last completed log; say so when a newer request has not produced one.
            logNote: readable != null && readable != logs ? _evidenceNote(readable, logs) : null,
          ),
          const SizedBox(height: 14),
          _InstallOutput(runs: report.installRuns),
          const SizedBox(height: 14),
          _Section(
            title: 'Logs',
            child: DeviceLogsPanel(
              host: report.host,
              logs: logs,
              lastCompleted: _lastCompleted,
              now: _now,
              around: _selected != null && !_showAll ? _selected!.occurredUtc : null,
              aroundLabel: _selected == null ? null : '${stampLabel(_selected!.occurredUtc)} (${_selected!.kind.label.toLowerCase()})',
              onShowEverything: () => setState(() => _showAll = true),
              onCollect: _collect,
              onCancel: (l) => _removeLogs(l, 'Request cancelled.'),
              onDelete: (l) => _removeLogs(l, 'Collection deleted.'),
            ),
          ),
          const SizedBox(height: 14),
          const _DoesAndDoesNot(),
        ],
      ],
    );
  }
}

/// A titled panel, the same arrangement as the Autonomy page's.
class _Section extends StatelessWidget {
  const _Section({required this.title, required this.child, this.hint});

  final String title;
  final String? hint;
  final Widget child;

  @override
  Widget build(BuildContext context) => KintsugiPanel(
        child: Padding(
          padding: const EdgeInsets.all(16),
          child: Column(crossAxisAlignment: CrossAxisAlignment.start, children: [
            Text(title.toUpperCase(), style: Theme.of(context).textTheme.titleMedium),
            if (hint != null) ...[const SizedBox(height: 4), HintText(hint!)],
            const SizedBox(height: 10),
            child,
          ]),
        ),
      );
}

String _healthKey(DeviceHealthStatus s) => switch (s) {
      DeviceHealthStatus.unstable => 'patch-failed',
      DeviceHealthStatus.watch => 'update-available',
      DeviceHealthStatus.healthy => 'up-to-date',
    };

String _healthLabel(DeviceHealthStatus s) => switch (s) {
      DeviceHealthStatus.unstable => 'Unstable',
      DeviceHealthStatus.watch => 'Watch',
      DeviceHealthStatus.healthy => 'Healthy',
    };

class _Header extends StatelessWidget {
  const _Header({required this.report, required this.now});

  final DeviceReport report;
  final DateTime now;

  @override
  Widget build(BuildContext context) {
    final palette = context.palette;
    final host = report.host;
    final why = report.health.why.trim();
    final seen = host.lastSeenUtc;

    Widget fact(String label, String value) => SizedBox(
          width: 230,
          child: Column(crossAxisAlignment: CrossAxisAlignment.start, children: [
            Text(label.toUpperCase(), style: AppTheme.display(color: palette.muted, size: 9.9, letterSpacing: 0.99)),
            const SizedBox(height: 2),
            Text(value, style: const TextStyle(fontWeight: FontWeight.w600)),
          ]),
        );

    return KintsugiPanel(
      child: Padding(
        padding: const EdgeInsets.all(18),
        child: Column(crossAxisAlignment: CrossAxisAlignment.start, children: [
          Wrap(spacing: 14, runSpacing: 6, crossAxisAlignment: WrapCrossAlignment.center, children: [
            Text(host.hostname, style: AppTheme.display(color: palette.neon, size: 20)),
            StatusChip(_healthLabel(report.health.status), statusKey: _healthKey(report.health.status)),
            HintText(platformLabel(host.platform)),
          ]),
          const SizedBox(height: 14),
          Wrap(spacing: 18, runSpacing: 12, children: [
            fact('Operating system', host.operatingSystem ?? 'Unknown'),
            fact('Serial number', host.serialNumber.isEmpty ? 'Unknown' : host.serialNumber),
            fact('Agent', host.agentVersion ?? 'Unknown'),
            fact('Last check-in', seen == null ? 'Never' : '${relativeAgo(seen, now)} (${stampLabel(seen)})'),
          ]),
          const SizedBox(height: 14),
          Text.rich(TextSpan(children: [
            TextSpan(text: 'Why it is flagged: ', style: TextStyle(color: palette.neonSoft, fontWeight: FontWeight.w700)),
            TextSpan(text: why.isEmpty ? 'Nothing unusual.' : (why.endsWith('.') ? why : '$why.')),
          ])),
        ]),
      ),
    );
  }
}

class _Toolbar extends StatelessWidget {
  const _Toolbar({
    required this.days,
    required this.onPeriod,
    required this.onCollect,
    required this.onPdf,
    required this.saving,
  });

  final int days;
  final void Function(int days) onPeriod;
  final VoidCallback? onCollect;
  final VoidCallback? onPdf;
  final bool saving;

  @override
  Widget build(BuildContext context) => KintsugiPanel(
        child: Padding(
          padding: const EdgeInsets.all(12),
          child: Wrap(spacing: 8, runSpacing: 8, crossAxisAlignment: WrapCrossAlignment.center, children: [
            const HintText('Period'),
            for (final d in const [7, 30]) ChoiceButton(label: 'Last $d days', selected: days == d, onTap: () => onPeriod(d)),
            const SizedBox(width: 24),
            PrimaryButton(label: 'Collect logs now', onPressed: onCollect),
            SecondaryButton(label: 'Download report (PDF)', busy: saving, onPressed: onPdf),
          ]),
        ),
      );
}

class _WhatHappened extends StatelessWidget {
  const _WhatHappened({required this.report});

  final DeviceReport report;

  @override
  Widget build(BuildContext context) {
    final likely = mostLikelyText(report);

    return _Section(
      title: 'What happened',
      child: Column(crossAxisAlignment: CrossAxisAlignment.start, children: [
        Text.rich(TextSpan(children: [
          for (final (text, bold) in summarySpans(report))
            TextSpan(text: text, style: bold ? const TextStyle(fontWeight: FontWeight.w700) : null),
        ])),
        if (likely != null) ...[
          const SizedBox(height: 8),
          Wrap(spacing: 8, children: [
            Text(likely),
            const HintText('These are hints read from the logs, not a diagnosis.'),
          ]),
        ],
      ]),
    );
  }
}

class _TimelineSection extends StatelessWidget {
  const _TimelineSection({
    required this.report,
    required this.now,
    required this.selected,
    required this.logsReady,
    required this.onSelect,
  });

  final DeviceReport report;
  final DateTime now;
  final DeviceReportEvent? selected;
  final bool logsReady;
  final void Function(DeviceReportEvent event) onSelect;

  @override
  Widget build(BuildContext context) {
    final palette = context.palette;

    return Column(crossAxisAlignment: CrossAxisAlignment.stretch, children: [
      _Section(
        title: 'Timeline',
        hint: 'Click an event to read the logs around it. Hover a mark to read what it is.',
        child: Column(crossAxisAlignment: CrossAxisAlignment.start, children: [
          DeviceTimeline(report: report, now: now, selected: selected, onSelect: onSelect),
          const SizedBox(height: 8),
          const DeviceTimelineLegend(),
          if (selected != null) ...[
            const SizedBox(height: 10),
            HintText(
              logsReady
                  ? 'The logs below show 10 minutes around ${stampLabel(selected!.occurredUtc)}.'
                  : 'Collect logs to read what the device wrote around this time.',
            ),
          ],
        ]),
      ),
      const SizedBox(height: 12),
      KintsugiTable(
        minWidth: 900,
        columns: const [
          TableColumnSpec(label: 'When', width: FixedColumnWidth(140)),
          TableColumnSpec(label: 'What', width: FixedColumnWidth(210)),
          TableColumnSpec(label: 'Source', width: FlexColumnWidth(1.2)),
          TableColumnSpec(label: 'What it said', width: FlexColumnWidth(2.4)),
          TableColumnSpec(label: 'Kintsugi close by', width: FixedColumnWidth(150)),
        ],
        rows: [
          for (final e in report.events)
            KintsugiTableRow(key: ValueKey('event-${e.occurredUtc.microsecondsSinceEpoch}-${e.kind.name}'), cells: [
              _Tap(
                onTap: () => onSelect(e),
                child: Text(
                  stampLabel(e.occurredUtc),
                  style: e == selected ? TextStyle(color: palette.neon, fontWeight: FontWeight.w700) : null,
                ),
              ),
              _Tap(
                onTap: () => onSelect(e),
                child: StatusChip(
                  e.kind.label,
                  statusKey: e.kind == DeviceReportEventKind.hardwareErrorCorrected ? 'update-available' : 'patch-failed',
                ),
              ),
              _Tap(onTap: () => onSelect(e), child: HintText(e.source)),
              _Tap(onTap: () => onSelect(e), child: Text(e.detail ?? '')),
              if (!e.isShutdown)
                const HintText('·')
              else if (e.kintsugiNearby?.within30Minutes ?? false)
                const StatusChip('Possibly', statusKey: 'update-available')
              else
                const StatusChip('No', statusKey: 'up-to-date'),
            ]),
        ],
      ),
      if (report.events.isEmpty)
        const Padding(padding: EdgeInsets.only(top: 8), child: HintText('Nothing in this period.')),
    ]);
  }
}

class _Tap extends StatelessWidget {
  const _Tap({required this.onTap, required this.child});

  final VoidCallback onTap;
  final Widget child;

  @override
  Widget build(BuildContext context) => MouseRegion(
        cursor: SystemMouseCursors.click,
        child: GestureDetector(behavior: HitTestBehavior.opaque, onTap: onTap, child: child),
      );
}

class _Involved extends StatelessWidget {
  const _Involved({required this.report, required this.selected, required this.onSelect});

  final DeviceReport report;
  final DeviceReportEvent? selected;
  final void Function(DeviceReportEvent event) onSelect;

  @override
  Widget build(BuildContext context) {
    final palette = context.palette;
    final shutdowns = report.shutdowns;
    final near = report.shutdownsNearKintsugi;

    return _Section(
      title: 'Was Kintsugi involved?',
      hint: 'Kintsugi counts as possibly involved only if it installed an update, or a system update that can restart the '
          'device, in the 30 minutes before the shutdown. Kintsugi never restarts a device without asking the person '
          'first, and that restart is not counted as unexpected.',
      child: Column(crossAxisAlignment: CrossAxisAlignment.start, children: [
        Align(
          alignment: Alignment.centerLeft,
          child: StatusChip('$near of ${shutdowns.length} possibly involved', statusKey: near > 0 ? 'update-available' : 'up-to-date'),
        ),
        if (shutdowns.isEmpty)
          const Padding(padding: EdgeInsets.only(top: 10), child: HintText('No unexpected shutdowns in this period.')),
        for (final s in shutdowns)
          Builder(builder: (context) {
            final v = involvement(s);
            return Padding(
              padding: const EdgeInsets.only(top: 8),
              child: InkWell(
                onTap: () => onSelect(s),
                child: Container(
                  padding: const EdgeInsets.all(10),
                  decoration: BoxDecoration(
                    border: Border.all(color: s == selected ? palette.neon : palette.border),
                    borderRadius: BorderRadius.circular(4),
                    color: s == selected ? palette.neon.withValues(alpha: 0.06) : null,
                  ),
                  child: Wrap(spacing: 12, runSpacing: 4, crossAxisAlignment: WrapCrossAlignment.center, children: [
                    Text(stampLabel(s.occurredUtc), style: const TextStyle(fontWeight: FontWeight.w700)),
                    StatusChip(v.possibly ? 'Possibly' : 'Not involved', statusKey: v.possibly ? 'update-available' : 'up-to-date'),
                    HintText(v.text),
                  ]),
                ),
              ),
            );
          }),
      ]),
    );
  }
}

/// Which log "What it could be" read when it is not the newest request's: its date and period, and what the
/// newer request is doing.
String _evidenceNote(LogCollection read, LogCollection? newest) {
  final when = stampLabel(read.completedUtc ?? read.requestedUtc);
  final newer = newest == null
      ? ''
      : newest.status == LogCollectionStatus.failed
          ? ' A newer request failed.'
          : ' A newer request has not finished.';
  return 'Evidence includes the log collected $when (last ${read.periodDays} days).$newer';
}

class _Hypotheses extends StatelessWidget {
  const _Hypotheses({required this.hypotheses, this.logNote});

  final List<DeviceHypothesis> hypotheses;

  /// Which collected log the guesses read, shown only when it is not the newest request's.
  final String? logNote;

  @override
  Widget build(BuildContext context) {
    final palette = context.palette;

    Color colorOf(HypothesisConfidence c) => switch (c) {
          HypothesisConfidence.high => palette.red,
          HypothesisConfidence.medium => palette.amber,
          HypothesisConfidence.unlikely => palette.green,
          HypothesisConfidence.low => palette.muted,
        };
    String keyOf(HypothesisConfidence c) => switch (c) {
          HypothesisConfidence.high => 'patch-failed',
          HypothesisConfidence.medium => 'update-available',
          HypothesisConfidence.unlikely => 'up-to-date',
          HypothesisConfidence.low => 'unknown',
        };

    Widget list(String label, List<String> items) => Column(crossAxisAlignment: CrossAxisAlignment.start, children: [
          Text(label.toUpperCase(), style: AppTheme.display(color: palette.neonDim, size: 10.5, letterSpacing: 1)),
          const SizedBox(height: 4),
          for (final t in items)
            Padding(padding: const EdgeInsets.only(top: 3), child: Text('•  $t')),
        ]);

    return _Section(
      title: 'What it could be',
      hint: 'Ranked from the evidence below. A hint to start the checking, not a diagnosis.',
      child: Column(crossAxisAlignment: CrossAxisAlignment.stretch, children: [
        if (logNote != null) HintText(logNote!),
        if (hypotheses.isEmpty) const HintText('Nothing to report.'),
        for (final h in hypotheses)
          Container(
            margin: const EdgeInsets.only(top: 10),
            padding: const EdgeInsets.all(14),
            decoration: BoxDecoration(
              border: Border.all(color: h.confidence == HypothesisConfidence.high ? palette.red : palette.border),
              borderRadius: BorderRadius.circular(4),
            ),
            child: Column(crossAxisAlignment: CrossAxisAlignment.start, children: [
              Row(children: [
                Expanded(
                  child: Wrap(spacing: 12, runSpacing: 6, crossAxisAlignment: WrapCrossAlignment.center, children: [
                    Text(h.title, style: const TextStyle(fontWeight: FontWeight.w700, fontSize: 15)),
                    StatusChip(h.confidence.label, statusKey: keyOf(h.confidence)),
                  ]),
                ),
                const SizedBox(width: 12),
                Row(mainAxisSize: MainAxisSize.min, children: [
                  for (var i = 1; i <= 3; i++)
                    Container(
                      width: 18,
                      height: 6,
                      margin: const EdgeInsets.only(left: 3),
                      decoration: BoxDecoration(
                        color: i <= h.confidence.pips ? colorOf(h.confidence) : palette.border,
                        borderRadius: BorderRadius.circular(2),
                      ),
                    ),
                ]),
              ]),
              const SizedBox(height: 10),
              LayoutBuilder(
                builder: (context, box) {
                  final evidence = list('Evidence', h.evidence);
                  final next = list('Check next', h.checkNext);
                  return box.maxWidth < 700
                      ? Column(crossAxisAlignment: CrossAxisAlignment.start, children: [evidence, const SizedBox(height: 10), next])
                      : Row(crossAxisAlignment: CrossAxisAlignment.start, children: [
                          Expanded(child: evidence),
                          const SizedBox(width: 18),
                          Expanded(child: next),
                        ]);
                },
              ),
            ]),
          ),
      ]),
    );
  }
}

class _InstallOutput extends StatefulWidget {
  const _InstallOutput({required this.runs});

  final List<DeviceInstallRun> runs;

  @override
  State<_InstallOutput> createState() => _InstallOutputState();
}

class _InstallOutputState extends State<_InstallOutput> {
  final _open = <int>{};

  @override
  Widget build(BuildContext context) {
    final palette = context.palette;
    final runs = widget.runs;

    return Column(crossAxisAlignment: CrossAxisAlignment.stretch, children: [
      Text('INSTALL OUTPUT', style: Theme.of(context).textTheme.titleMedium),
      const SizedBox(height: 4),
      const HintText('What Kintsugi installed on this device, newest first.'),
      const SizedBox(height: 10),
      if (runs.isEmpty)
        const EmptyPanel('Kintsugi installed nothing on this device in this period.')
      else
        KintsugiTable(
          minWidth: 700,
          columns: const [
            TableColumnSpec(label: 'When', width: FixedColumnWidth(140)),
            TableColumnSpec(label: 'What'),
            TableColumnSpec(label: 'Result', width: FixedColumnWidth(130)),
            TableColumnSpec(label: '', width: FixedColumnWidth(120)),
          ],
          rows: [
            for (final (i, r) in runs.indexed)
              KintsugiTableRow(
                key: ValueKey('run-$i'),
                cells: [
                  Text(stampLabel(r.atUtc)),
                  Text(r.subject),
                  StatusChip(r.succeeded ? 'Installed' : 'Failed', statusKey: r.succeeded ? 'up-to-date' : 'patch-failed'),
                  if ((r.output ?? '').isNotEmpty)
                    LinkText(
                      label: _open.contains(i) ? 'Hide output' : 'Output',
                      onTap: () => setState(() => _open.contains(i) ? _open.remove(i) : _open.add(i)),
                    )
                  else
                    const SizedBox.shrink(),
                ],
                expanded: _open.contains(i)
                    ? SelectableText(r.output!, style: AppTheme.mono(color: r.succeeded ? palette.text : palette.amber, size: 13))
                    : null,
              ),
          ],
        ),
    ]);
  }
}

class _DoesAndDoesNot extends StatelessWidget {
  const _DoesAndDoesNot();

  @override
  Widget build(BuildContext context) {
    final palette = context.palette;

    Widget column(String title, List<String> items) => Column(crossAxisAlignment: CrossAxisAlignment.start, children: [
          Text(title.toUpperCase(), style: AppTheme.display(color: palette.neonDim, size: 10.5, letterSpacing: 1)),
          const SizedBox(height: 4),
          for (final t in items) Padding(padding: const EdgeInsets.only(top: 4), child: Text('•  $t')),
        ]);

    final does = column('Does', const [
      "Reads the operating system's own log, read-only, for the period you pick.",
      'Keeps warnings and errors by default. Everything is an option.',
      'Stops at 5 MB compressed and keeps the newest part, and says so.',
      'Only an administrator can ask for it or download it, and both are written to the audit log.',
      'Is deleted from the server after 30 days, or sooner on request.',
    ]);
    final doesNot = column('Does not', const [
      'No file contents, no browsing history, no screenshots.',
      'No application names or what the person was doing (that capture is not built).',
      'Nothing runs on the device except reading its log.',
      'No change to the device. The agent only reads and sends.',
      'Nothing is collected until an administrator presses the button.',
    ]);

    return _Section(
      title: 'What collecting logs does, and does not do',
      child: LayoutBuilder(
        builder: (context, box) => box.maxWidth < 700
            ? Column(crossAxisAlignment: CrossAxisAlignment.start, children: [does, const SizedBox(height: 14), doesNot])
            : Row(crossAxisAlignment: CrossAxisAlignment.start, children: [
                Expanded(child: does),
                const SizedBox(width: 24),
                Expanded(child: doesNot),
              ]),
      ),
    );
  }
}

/// Which sections go into the PDF. The log lines are offered only once a collection is complete, and the
/// whole log is not offered at all: a zip cannot ride inside a PDF made in the browser.
class _PdfDialog extends StatefulWidget {
  const _PdfDialog({required this.report, required this.logsReady, required this.selected});

  final DeviceReport report;
  final bool logsReady;
  final DeviceReportEvent? selected;

  @override
  State<_PdfDialog> createState() => _PdfDialogState();
}

class _PdfDialogState extends State<_PdfDialog> {
  late DeviceReportOptions _options = DeviceReportOptions(logLines: widget.logsReady);

  @override
  Widget build(BuildContext context) {
    final palette = context.palette;

    Widget option(String label, bool value, ValueChanged<bool> onChanged, {String? disabledHint}) {
      final off = disabledHint != null;
      return Opacity(
        opacity: off ? 0.5 : 1,
        child: InkWell(
          onTap: off ? null : () => setState(() => onChanged(!value)),
          child: Padding(
            padding: const EdgeInsets.symmetric(vertical: 2),
            child: Row(children: [
              Checkbox(value: value && !off, onChanged: off ? null : (v) => setState(() => onChanged(v ?? false))),
              Expanded(
                child: Column(crossAxisAlignment: CrossAxisAlignment.start, children: [
                  Text(label),
                  if (off) HintText(disabledHint),
                ]),
              ),
            ]),
          ),
        ),
      );
    }

    return AlertDialog(
      backgroundColor: palette.backgroundAlt,
      shape: RoundedRectangleBorder(borderRadius: BorderRadius.circular(6), side: BorderSide(color: palette.border)),
      title: Text('Report for ${widget.report.host.hostname}', style: Theme.of(context).textTheme.titleLarge),
      content: SizedBox(
        width: 560,
        child: SingleChildScrollView(
          child: Column(mainAxisSize: MainAxisSize.min, crossAxisAlignment: CrossAxisAlignment.start, children: [
            const HintText('The specific report: one device, what happened and why. Pick what goes in.'),
            const SizedBox(height: 8),
            option('What happened', _options.summary, (v) => _options = _options.copyWith(summary: v)),
            option('Timeline chart', _options.timeline, (v) => _options = _options.copyWith(timeline: v)),
            option('Was Kintsugi involved?', _options.involvement, (v) => _options = _options.copyWith(involvement: v)),
            option('What it could be', _options.hypotheses, (v) => _options = _options.copyWith(hypotheses: v)),
            option('Install output', _options.installOutput, (v) => _options = _options.copyWith(installOutput: v)),
            option(
              'Log lines around the selected events (errors only, up to $maxPdfLogLines lines)',
              _options.logLines,
              (v) => _options = _options.copyWith(logLines: v),
              disabledHint: widget.logsReady ? null : 'Collect logs first.',
            ),
            const SizedBox(height: 8),
            HintText(
              'Period: last ${widget.report.days} days.'
              '${widget.logsReady ? (widget.selected == null ? ' With no event selected, the newest errors in the log are used.' : ' The log lines are the ones around the selected event.') : ''}'
              ' Made in this browser; nothing is sent anywhere.',
            ),
          ]),
        ),
      ),
      actions: [
        SecondaryButton(label: 'Cancel', onPressed: () => Navigator.of(context).pop()),
        PrimaryButton(label: 'Generate PDF', onPressed: () => Navigator.of(context).pop(_options)),
      ],
    );
  }
}
