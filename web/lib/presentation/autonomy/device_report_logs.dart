import 'dart:async';

import 'package:flutter/material.dart';
import 'package:intl/intl.dart';

import '../../core/di/locator.dart';
import '../../core/network/api_exception.dart';
import '../../core/platform/file_saver.dart';
import '../../core/theme/app_theme.dart';
import '../../core/theme/kintsugi_palette.dart';
import '../../core/widgets/alert_box.dart';
import '../../core/widgets/buttons.dart';
import '../../core/widgets/form_bits.dart';
import '../../core/widgets/text_bits.dart';
import '../../domain/entities/device_report.dart';
import '../../domain/usecases/device_report_usecases.dart';
import 'device_report_model.dart';

/// Fixed on the server, and repeated on the page and in the dialog so an administrator reads them
/// before pressing the button.
const logCapNote = 'Stops at 5 MB compressed and keeps the newest part. Kept on the server for 30 days. '
    'Only administrators can see it, and this request is written to the audit log.';

const _pageSize = 150;
const _searchDelay = Duration(milliseconds: 400);

/// Asks the agent to collect its log. Pops with the new collection when the server accepted the request,
/// and shows the server's own message in the dialog when it did not (a collection is already open, say).
Future<LogCollection?> showCollectLogsDialog(BuildContext context, DeviceReportHost host) =>
    showDialog<LogCollection>(context: context, builder: (_) => _CollectDialog(host: host));

class _CollectDialog extends StatefulWidget {
  const _CollectDialog({required this.host});

  final DeviceReportHost host;

  @override
  State<_CollectDialog> createState() => _CollectDialogState();
}

class _CollectDialogState extends State<_CollectDialog> {
  int _days = 30;
  LogDetail _detail = LogDetail.warningsAndErrors;
  bool _sending = false;
  String? _error;

  Future<void> _send() async {
    setState(() {
      _sending = true;
      _error = null;
    });
    try {
      final created = await locator<RequestLogCollection>()(widget.host.id, periodDays: _days, detail: _detail);
      if (mounted) Navigator.of(context).pop(created);
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
    final source = logSourceFor(widget.host.platform);

    Widget choice(String label, bool selected, VoidCallback onTap) => ChoiceButton(label: label, selected: selected, onTap: _sending ? null : onTap);

    return AlertDialog(
      backgroundColor: palette.backgroundAlt,
      shape: RoundedRectangleBorder(borderRadius: BorderRadius.circular(6), side: BorderSide(color: palette.border)),
      title: Text('Collect logs from ${widget.host.hostname}', style: Theme.of(context).textTheme.titleLarge),
      content: SizedBox(
        width: 560,
        child: SingleChildScrollView(
          child: Column(mainAxisSize: MainAxisSize.min, crossAxisAlignment: CrossAxisAlignment.start, children: [
            const HintText('The agent reads the log and sends it. Nothing on the device changes.'),
            const SizedBox(height: 14),
            LabelledField(
              label: 'How far back',
              child: Wrap(spacing: 8, runSpacing: 8, children: [
                for (final d in const [7, 30]) choice('Last $d days', _days == d, () => setState(() => _days = d)),
              ]),
            ),
            const SizedBox(height: 12),
            LabelledField(
              label: 'How much',
              child: Wrap(spacing: 8, runSpacing: 8, children: [
                choice('Warnings and errors (recommended)', _detail == LogDetail.warningsAndErrors, () => setState(() => _detail = LogDetail.warningsAndErrors)),
                choice('Everything', _detail == LogDetail.everything, () => setState(() => _detail = LogDetail.everything)),
              ]),
            ),
            const SizedBox(height: 12),
            LabelledField(
              label: 'What will be read on this device',
              child: Text('${source[0].toUpperCase()}${source.substring(1)}.'),
            ),
            const SizedBox(height: 14),
            AlertBox.info(
              logCapNote,
              child: _detail == LogDetail.everything
                  ? Text('Everything can include user names and file paths.', style: TextStyle(color: palette.amber, fontWeight: FontWeight.w700))
                  : null,
            ),
            if (_error != null) AlertBox.error(_error!),
          ]),
        ),
      ),
      actions: [
        SecondaryButton(label: 'Cancel', onPressed: _sending ? null : () => Navigator.of(context).pop()),
        PrimaryButton(label: 'Ask the device', busy: _sending, onPressed: _send),
      ],
    );
  }
}

/// A toggle-style button, the same look as the tab buttons on the Autonomy page.
class ChoiceButton extends StatelessWidget {
  const ChoiceButton({super.key, required this.label, required this.selected, required this.onTap});

  final String label;
  final bool selected;
  final VoidCallback? onTap;

  @override
  Widget build(BuildContext context) {
    final palette = context.palette;

    return InkWell(
      onTap: onTap,
      child: Container(
        padding: const EdgeInsets.symmetric(horizontal: 12, vertical: 8),
        decoration: BoxDecoration(
          border: Border.all(color: selected ? palette.neon : palette.border),
          borderRadius: BorderRadius.circular(3),
          color: selected ? palette.neon.withValues(alpha: 0.08) : null,
        ),
        child: Text(
          label.toUpperCase(),
          style: AppTheme.display(color: selected ? palette.neon : palette.muted, size: 10.5, letterSpacing: 1),
        ),
      ),
    );
  }
}

/// A bordered, tinted notice with a lead word.
class _Notice extends StatelessWidget {
  const _Notice({required this.color, required this.children});

  final Color color;
  final List<Widget> children;

  @override
  Widget build(BuildContext context) => Container(
        width: double.infinity,
        padding: const EdgeInsets.symmetric(horizontal: 16, vertical: 12),
        decoration: BoxDecoration(
          color: color.withValues(alpha: 0.08),
          border: Border.all(color: color),
          borderRadius: BorderRadius.circular(4),
        ),
        child: Column(crossAxisAlignment: CrossAxisAlignment.start, children: children),
      );
}

/// The Logs panel: what has been collected from the device, in whichever state it is in, and a viewer of
/// the entries once it is complete.
class DeviceLogsPanel extends StatelessWidget {
  const DeviceLogsPanel({
    super.key,
    required this.host,
    required this.logs,
    required this.lastCompleted,
    required this.now,
    required this.around,
    required this.aroundLabel,
    required this.onShowEverything,
    required this.onCollect,
    required this.onCancel,
    required this.onDelete,
  });

  final DeviceReportHost host;

  /// The newest collection, in whatever state it is in.
  final LogCollection? logs;

  /// The last collection that completed. When the newest one is waiting, collecting or failed, this is shown
  /// beneath its notice, so a new request does not hide the log that was already collected.
  final LogCollection? lastCompleted;
  final DateTime now;

  /// When an event is selected, the moment the viewer narrows to; null shows the whole log.
  final DateTime? around;
  final String? aroundLabel;
  final VoidCallback onShowEverything;
  final VoidCallback onCollect;
  final Future<void> Function(LogCollection logs) onCancel;
  final Future<void> Function(LogCollection logs) onDelete;

  @override
  Widget build(BuildContext context) {
    final palette = context.palette;
    final l = logs;

    // The older completed log, when the newest collection is not itself the readable one.
    final kept = (l?.isCompleted ?? false) ? null : lastCompleted;

    Widget completed(LogCollection c) => _Completed(
          // A new collection is a new viewer: its page, search and filter start again.
          key: ValueKey(c.id),
          host: host,
          logs: c,
          around: around,
          aroundLabel: aroundLabel,
          onShowEverything: onShowEverything,
          onDelete: onDelete,
        );

    final Widget body;
    if (l == null && kept != null) {
      body = completed(kept);
    } else if (l == null) {
      body = Column(crossAxisAlignment: CrossAxisAlignment.start, children: [
        HintText(
          'Nothing has been collected from this device. Collecting asks the agent to read ${logSourceFor(host.platform)} and '
          'send what it finds. It happens when the device next checks in, which is about once an hour.',
        ),
        const SizedBox(height: 12),
        PrimaryButton(label: 'Collect logs now', onPressed: onCollect),
      ]);
    } else {
      body = switch (l.status) {
        LogCollectionStatus.requested => _Requested(host: host, logs: l, now: now, onCancel: onCancel),
        LogCollectionStatus.collecting => _Notice(color: palette.neon, children: [
            Text('Collecting.', style: TextStyle(color: palette.neon, fontWeight: FontWeight.w700)),
            const SizedBox(height: 4),
            const Text('The device is reading its log…'),
            const SizedBox(height: 8),
            LinearProgressIndicator(color: palette.neon, backgroundColor: palette.border),
          ]),
        LogCollectionStatus.failed => Column(crossAxisAlignment: CrossAxisAlignment.start, children: [
            _Notice(color: palette.red, children: [
              Text('Failed.', style: TextStyle(color: palette.red, fontWeight: FontWeight.w700)),
              const SizedBox(height: 4),
              Text(l.failureReason ?? 'The device did not send its logs.', style: TextStyle(color: palette.red)),
            ]),
            const SizedBox(height: 12),
            PrimaryButton(label: 'Try again', onPressed: onCollect),
          ]),
        LogCollectionStatus.completed => completed(l),
      };
    }

    return Column(crossAxisAlignment: CrossAxisAlignment.start, children: [
      body,
      if (l != null && kept != null) ...[
        const SizedBox(height: 16),
        Container(
          width: double.infinity,
          padding: const EdgeInsets.only(top: 14),
          decoration: BoxDecoration(border: Border(top: BorderSide(color: palette.border))),
          child: Column(crossAxisAlignment: CrossAxisAlignment.start, children: [
            Text('LAST COLLECTED LOG', style: AppTheme.display(color: palette.neonDim, size: 10.5, letterSpacing: 1)),
            const SizedBox(height: 6),
            const HintText(
              'This is the log from before the request above. It stays here, and in the guesses, until a newer one '
              'completes or you delete it.',
            ),
            const SizedBox(height: 10),
            completed(kept),
          ]),
        ),
      ],
      if (around != null && l?.isCompleted != true && kept == null) ...[
        const SizedBox(height: 10),
        const HintText('Collect logs to read what the device wrote around this time.'),
      ],
    ]);
  }
}

class _Requested extends StatefulWidget {
  const _Requested({required this.host, required this.logs, required this.now, required this.onCancel});

  final DeviceReportHost host;
  final LogCollection logs;
  final DateTime now;
  final Future<void> Function(LogCollection logs) onCancel;

  @override
  State<_Requested> createState() => _RequestedState();
}

class _RequestedState extends State<_Requested> {
  bool _busy = false;

  @override
  Widget build(BuildContext context) {
    final palette = context.palette;
    final seen = widget.host.lastSeenUtc;

    return Column(crossAxisAlignment: CrossAxisAlignment.start, children: [
      _Notice(color: palette.amber, children: [
        Text(
          'Requested ${stampLabel(widget.logs.requestedUtc)} by ${widget.logs.requestedBy}. Waiting for the device to check in '
          '(last seen ${seen == null ? 'never' : relativeAgo(seen, widget.now)}). Devices check in about once an hour, and the '
          'request waits up to 24 hours, then shows as failed.',
          style: TextStyle(color: palette.amber),
        ),
      ]),
      const SizedBox(height: 12),
      SecondaryButton(
        label: 'Cancel request',
        busy: _busy,
        onPressed: () async {
          setState(() => _busy = true);
          await widget.onCancel(widget.logs);
          if (mounted) setState(() => _busy = false);
        },
      ),
    ]);
  }
}

class _Completed extends StatefulWidget {
  const _Completed({
    super.key,
    required this.host,
    required this.logs,
    required this.around,
    required this.aroundLabel,
    required this.onShowEverything,
    required this.onDelete,
  });

  final DeviceReportHost host;
  final LogCollection logs;
  final DateTime? around;
  final String? aroundLabel;
  final VoidCallback onShowEverything;
  final Future<void> Function(LogCollection logs) onDelete;

  @override
  State<_Completed> createState() => _CompletedState();
}

class _CompletedState extends State<_Completed> {
  final _searchController = TextEditingController();
  Timer? _debounce;
  LogSeverityFilter _severity = LogSeverityFilter.all;
  String _search = '';
  List<LogEntry> _items = const [];
  int _total = 0;
  bool _loading = true;
  bool _busy = false;
  String? _error;

  /// Answers that arrive after a newer question was asked are dropped.
  int _ticket = 0;

  @override
  void initState() {
    super.initState();
    _load();
  }

  @override
  void didUpdateWidget(_Completed old) {
    super.didUpdateWidget(old);
    if (old.around != widget.around) _load();
  }

  @override
  void dispose() {
    _debounce?.cancel();
    _searchController.dispose();
    super.dispose();
  }

  Future<void> _load({bool more = false}) async {
    final ticket = ++_ticket;
    setState(() {
      _loading = true;
      _error = null;
    });
    try {
      final page = await locator<GetLogEntries>()(
        widget.host.id,
        widget.logs.id,
        severity: _severity,
        search: _search,
        around: widget.around,
        skip: more ? _items.length : 0,
        take: _pageSize,
      );
      if (!mounted || ticket != _ticket) return;
      setState(() {
        _items = more ? [..._items, ...page.items] : page.items;
        _total = page.total;
        _loading = false;
      });
    } on ApiException catch (error) {
      if (!mounted || ticket != _ticket) return;
      setState(() {
        _error = error.message;
        _loading = false;
      });
    }
  }

  void _chooseSeverity(LogSeverityFilter s) {
    if (s == _severity) return;
    _severity = s;
    _load();
  }

  void _onSearch(String value) {
    _debounce?.cancel();
    _debounce = Timer(_searchDelay, () {
      _search = value.trim();
      _load();
    });
  }

  Future<void> _download() async {
    setState(() {
      _busy = true;
      _error = null;
    });
    try {
      final bytes = await locator<DownloadLogCollection>()(widget.host.id, widget.logs.id);
      await locator<FileSaver>().save(bytes, '${widget.host.hostname}-logs.zip', mimeType: 'application/zip');
    } on ApiException catch (error) {
      if (mounted) setState(() => _error = error.message);
    } finally {
      if (mounted) setState(() => _busy = false);
    }
  }

  Future<void> _delete() async {
    final confirmed = await showDialog<bool>(
      context: context,
      builder: (context) {
        final palette = context.palette;
        return AlertDialog(
          backgroundColor: palette.backgroundAlt,
          shape: RoundedRectangleBorder(borderRadius: BorderRadius.circular(6), side: BorderSide(color: palette.border)),
          title: Text('Delete this collection now?', style: Theme.of(context).textTheme.titleLarge),
          content: const SizedBox(
            width: 460,
            child: Text('The collected log is removed from the server and cannot be downloaded again. The deletion is written to the audit log.'),
          ),
          actions: [
            SecondaryButton(label: 'Cancel', onPressed: () => Navigator.of(context).pop(false)),
            PrimaryButton(label: 'Delete', onPressed: () => Navigator.of(context).pop(true)),
          ],
        );
      },
    );
    if (confirmed != true || !mounted) return;
    setState(() => _busy = true);
    await widget.onDelete(widget.logs);
    if (mounted) setState(() => _busy = false);
  }

  @override
  Widget build(BuildContext context) {
    final palette = context.palette;
    final l = widget.logs;
    final day = DateFormat('MMM d');

    return Column(crossAxisAlignment: CrossAxisAlignment.start, children: [
      _Notice(color: palette.green, children: [
        Text.rich(TextSpan(children: [
          const TextSpan(text: 'Collected ', style: TextStyle(fontWeight: FontWeight.w700)),
          TextSpan(
            text: '${stampLabel(l.completedUtc ?? l.requestedUtc)} · last ${l.periodDays} days · ${l.detail.label} · '
                '${NumberFormat.decimalPattern().format(l.entryCount)} entries · ${formatFileSize(l.sizeBytes)} compressed · '
                'kept until ${day.format(l.keepUntilUtc.toLocal())}',
          ),
        ])),
        if (l.truncatedBeforeUtc != null) ...[
          const SizedBox(height: 4),
          Text(
            'Cut at the 5 MB cap: nothing before ${stampLabel(l.truncatedBeforeUtc!)} is included.',
            style: TextStyle(color: palette.amber),
          ),
        ],
      ]),
      const SizedBox(height: 12),
      Wrap(spacing: 8, runSpacing: 8, crossAxisAlignment: WrapCrossAlignment.center, children: [
        for (final s in LogSeverityFilter.values) ChoiceButton(label: s.label, selected: _severity == s, onTap: () => _chooseSeverity(s)),
        SizedBox(
          width: 260,
          child: KintsugiTextField(controller: _searchController, hintText: 'Search the log', onChanged: _onSearch),
        ),
        SecondaryButton(label: 'Download (.zip)', busy: _busy, onPressed: _download),
        SecondaryButton(label: 'Delete now', onPressed: _busy ? null : _delete),
      ]),
      if (widget.around != null) ...[
        const SizedBox(height: 12),
        _Notice(color: palette.neon, children: [
          Wrap(spacing: 8, crossAxisAlignment: WrapCrossAlignment.center, children: [
            Text('Showing 10 minutes before and after ${widget.aroundLabel ?? stampLabel(widget.around!)}.'),
            LinkText(label: 'Show everything', onTap: widget.onShowEverything),
          ]),
        ]),
      ],
      if (_error != null) ...[const SizedBox(height: 12), AlertBox.error(_error!)],
      const SizedBox(height: 12),
      HintText(_loading && _items.isEmpty ? 'Loading…' : '${NumberFormat.decimalPattern().format(_total)} ${_total == 1 ? 'entry' : 'entries'}'),
      const SizedBox(height: 6),
      Container(
        constraints: const BoxConstraints(maxHeight: 520),
        decoration: BoxDecoration(border: Border.all(color: palette.border), borderRadius: BorderRadius.circular(4)),
        child: SingleChildScrollView(
          padding: const EdgeInsets.all(8),
          child: Column(crossAxisAlignment: CrossAxisAlignment.stretch, children: [
            if (!_loading && _items.isEmpty) const HintText('Nothing matches.'),
            for (final e in _items) _EntryRow(entry: e),
            if (_items.length < _total)
              Padding(
                padding: const EdgeInsets.only(top: 8),
                child: Align(
                  alignment: Alignment.centerLeft,
                  child: LinkText(label: 'Show $_pageSize more', onTap: _loading ? () {} : () => _load(more: true)),
                ),
              ),
          ]),
        ),
      ),
    ]);
  }
}

class _EntryRow extends StatelessWidget {
  const _EntryRow({required this.entry});

  final LogEntry entry;

  static final _time = DateFormat('MMM d HH:mm:ss');

  @override
  Widget build(BuildContext context) {
    final p = context.palette;
    final color = switch (entry.severity) {
      LogSeverity.critical || LogSeverity.error => p.red,
      LogSeverity.warning => p.amber,
      LogSeverity.info => p.muted,
    };
    final mono = AppTheme.mono(color: p.text, size: 13);

    return Padding(
      padding: const EdgeInsets.symmetric(vertical: 2),
      child: Row(crossAxisAlignment: CrossAxisAlignment.start, children: [
        SizedBox(width: 124, child: Text(_time.format(entry.time.toLocal()), style: mono.copyWith(color: p.muted))),
        SizedBox(width: 74, child: Text(entry.severity.name.toUpperCase(), style: mono.copyWith(color: color, fontWeight: FontWeight.w700))),
        SizedBox(width: 150, child: Text(entry.source, overflow: TextOverflow.ellipsis, style: mono.copyWith(color: p.muted))),
        Expanded(child: Text(entry.message, style: mono)),
      ]),
    );
  }
}
