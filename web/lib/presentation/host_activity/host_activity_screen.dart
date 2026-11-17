import 'package:flutter/material.dart';
import 'package:flutter/services.dart';
import 'package:go_router/go_router.dart';
import 'package:intl/intl.dart';

import '../../core/di/locator.dart';
import '../../core/network/api_exception.dart';
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
import '../../domain/entities/enums.dart';
import '../../domain/entities/host_activity.dart';
import '../../domain/usecases/host_activity_usecases.dart';
import '../patch_health/patch_health_model.dart';
import '../patch_health/patch_now_dialog.dart';

/// Everything about one host in one place: what is wrong with it, what happened to it and when, what
/// is waiting to be installed, and what the installs said when they failed.
///
/// The timeline is the server's own record (see `HostEvent`): administrators' Patch now presses with
/// their names, the host picking them up, patches, failures and dismissals. What the person at the
/// host did (the warning shown, a Delay click) is not on it, because no agent reports that yet, and
/// the page says so rather than leaving the gap to be read as "nothing happened".
class HostActivityScreen extends StatefulWidget {
  const HostActivityScreen({super.key, required this.hostId});

  final String hostId;

  @override
  State<HostActivityScreen> createState() => _HostActivityScreenState();
}

enum _Tab {
  overview('Overview'),
  activity('Activity'),
  pending('Pending updates'),
  logs('Logs');

  const _Tab(this.label);

  final String label;
}

class _HostActivityScreenState extends State<HostActivityScreen> {
  HostActivity? _data;
  String? _error;
  bool _loading = false;
  String? _notice;
  _Tab _tab = _Tab.overview;
  HostEventGroup? _group;
  final Set<String> _open = {};

  @override
  void initState() {
    super.initState();
    _load();
  }

  @override
  void didUpdateWidget(HostActivityScreen oldWidget) {
    super.didUpdateWidget(oldWidget);
    if (oldWidget.hostId != widget.hostId) _load();
  }

  Future<void> _load() async {
    setState(() {
      _loading = true;
      _error = null;
    });
    try {
      final data = await locator<GetHostActivity>()(widget.hostId);
      if (mounted) setState(() => _data = data);
    } on ApiException catch (error) {
      if (mounted) setState(() => _error = error.message);
    } finally {
      if (mounted) setState(() => _loading = false);
    }
  }

  /// The row Patch now's dialog describes. Null when this host cannot take it: Windows only, and
  /// only with an operating system update waiting or failed, because that is all the install does.
  HealthItem? _patchNowItem(HostActivity data) {
    if (!data.isWindows) return null;
    final osFailure = data.openFailures.where((f) => f.isOperatingSystemUpdate).firstOrNull;
    final osPending = data.pendingUpdates.any((p) => p.isOperatingSystem);
    if (osFailure == null && !osPending) return null;

    return HealthItem(
      key: data.hostId,
      kind: data.status == HostStatus.offline
          ? HealthKind.offline
          : HealthKind.osUpdate,
      hostId: data.hostId,
      hostname: data.hostname,
      operatingSystem: data.operatingSystem,
      subject: windowsPatchNowName,
      cause: osFailure?.causeTitle ?? 'Updates waiting',
      explanation: osFailure?.causeExplanation ?? '',
      suggestedFix: osFailure?.suggestedFix ?? '',
      tries: osFailure?.failureCount ?? 0,
      since: osFailure?.firstFailedUtc,
      details: osFailure?.details,
      osUpdatePending: osPending,
    );
  }

  Future<void> _patchNow(HealthItem item) async {
    final result = await showPatchNowDialog(context, [item]);
    if (result == null || !mounted) return;
    setState(() => _notice = 'Patch now sent. It is on the timeline with your name; the host picks it up '
        'within a minute of its next check for work.');
    await _load();
  }

  Future<void> _changeRetention(HostActivity data) async {
    final months = await showDialog<int>(
      context: context,
      builder: (_) => _RetentionDialog(current: data.retentionMonths),
    );
    if (months == null || !mounted) return;
    try {
      await locator<SetHostActivityRetention>()(months);
      if (!mounted) return;
      await _load();
    } on ApiException catch (error) {
      if (mounted) setState(() => _error = error.message);
    }
  }

  @override
  Widget build(BuildContext context) {
    final data = _data;
    final patchNow = data == null ? null : _patchNowItem(data);

    return PageScaffold(
      title: data?.hostname ?? 'Host',
      children: [
        SectionHeader(
          title: data == null ? 'Host activity' : 'Activity on ${data.hostname}',
          hints: [
            if (data != null)
              HintText(
                [
                  if (data.operatingSystem != null) data.operatingSystem!,
                  if (data.agentVersion != null) 'agent ${data.agentVersion}',
                  if (data.lastSeenUtc != null)
                    'last check-in ${DateFormat('yyyy-MM-dd HH:mm').format(data.lastSeenUtc!.toLocal())}',
                ].join(' · '),
              ),
          ],
          actions: [
            SecondaryButton(label: 'Back to hosts', onPressed: () => context.go(Routes.hosts)),
            SecondaryButton(label: 'Reload', onPressed: _loading ? null : _load),
            if (patchNow != null) PrimaryButton(label: 'Patch now', onPressed: () => _patchNow(patchNow)),
          ],
        ),
        if (_error != null) ...[AlertBox.error(_error!), const SizedBox(height: 12)],
        if (_notice != null) ...[AlertBox.success(_notice!), const SizedBox(height: 12)],
        if (data == null)
          EmptyPanel(_loading ? 'Loading…' : 'Could not load this host.')
        else ...[
          Row(
            children: [
              StatusChip(data.status.label, statusKey: data.status.key),
            ],
          ),
          const SizedBox(height: 12),
          Wrap(
            spacing: 8,
            runSpacing: 8,
            children: [
              for (final tab in _Tab.values)
                _TabButton(label: tab.label, selected: _tab == tab, onTap: () => setState(() => _tab = tab)),
            ],
          ),
          const SizedBox(height: 16),
          switch (_tab) {
            _Tab.overview => _Overview(data: data, onGo: (tab) => setState(() => _tab = tab)),
            _Tab.activity => _Activity(
                data: data,
                group: _group,
                open: _open,
                onGroup: (group) => setState(() => _group = group),
                onToggle: (id) => setState(() => _open.contains(id) ? _open.remove(id) : _open.add(id)),
                onChangeRetention: () => _changeRetention(data),
              ),
            _Tab.pending => _Pending(data: data),
            _Tab.logs => _Logs(data: data),
          },
        ],
      ],
    );
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

/// "3 days ago", "5 hours ago", "just now" — how long since [when], for the tile.
String _ago(DateTime? when) {
  if (when == null) return '—';
  final diff = DateTime.now().toUtc().difference(when.toUtc());
  if (diff.inDays >= 1) return '${diff.inDays} d';
  if (diff.inHours >= 1) return '${diff.inHours} h';
  return '${diff.inMinutes.clamp(0, 59)} min';
}

final _stamp = DateFormat('yyyy-MM-dd HH:mm');

class _Overview extends StatelessWidget {
  const _Overview({required this.data, required this.onGo});

  final HostActivity data;
  final void Function(_Tab tab) onGo;

  @override
  Widget build(BuildContext context) {
    final palette = context.palette;
    final tiles = <(String, String, Color, _Tab)>[
      ('Updates pending', '${data.pendingUpdates.length}', data.pendingUpdates.isEmpty ? palette.neon : palette.amber, _Tab.pending),
      ('Failed tries (open)', '${data.failedTries}', data.failedTries > 0 ? palette.red : palette.neon, _Tab.activity),
      ('Since last patched', _ago(data.lastPatchedUtc), palette.neon, _Tab.activity),
      ('Patched in 30 days', '${data.patchedLast30Days}', palette.neon, _Tab.activity),
    ];

    return Column(
      crossAxisAlignment: CrossAxisAlignment.stretch,
      children: [
        Wrap(
          spacing: 12,
          runSpacing: 12,
          children: [
            for (final (label, value, color, tab) in tiles)
              SizedBox(
                width: 250,
                child: InkWell(
                  onTap: () => onGo(tab),
                  child: KintsugiPanel(
                    child: Padding(
                      padding: const EdgeInsets.all(14),
                      child: Column(
                        crossAxisAlignment: CrossAxisAlignment.start,
                        children: [
                          Text(value, style: AppTheme.display(color: color, size: 28)),
                          const SizedBox(height: 4),
                          Text(
                            label.toUpperCase(),
                            style: AppTheme.display(color: palette.muted, size: 9.9, letterSpacing: 0.99),
                          ),
                        ],
                      ),
                    ),
                  ),
                ),
              ),
          ],
        ),
        if (data.restartRequiredSinceUtc != null) ...[
          const SizedBox(height: 16),
          KintsugiPanel(
            child: Container(
              padding: const EdgeInsets.all(16),
              decoration: BoxDecoration(border: Border.all(color: palette.amber), borderRadius: BorderRadius.circular(6)),
              child: Column(
                crossAxisAlignment: CrossAxisAlignment.start,
                children: [
                  Text('RESTART NEEDED', style: Theme.of(context).textTheme.titleMedium?.copyWith(color: palette.amber)),
                  const SizedBox(height: 8),
                  const Text('The updates are installed, but a restart is needed to finish them. '
                      'Kintsugi does not restart a Linux host by itself yet: restart it when it suits.'),
                  const SizedBox(height: 6),
                  HintText('Since ${_stamp.format(data.restartRequiredSinceUtc!.toLocal())}'
                      '${data.restartRequiredBecause == null ? '' : ' · because of ${data.restartRequiredBecause}'}'),
                ],
              ),
            ),
          ),
        ],
        const SizedBox(height: 16),
        KintsugiPanel(
          child: Padding(
            padding: const EdgeInsets.all(16),
            child: Column(
              crossAxisAlignment: CrossAxisAlignment.start,
              children: [
                Text(
                  data.openFailures.isEmpty ? 'NO OPEN FAILURES' : 'WHY IT IS FAILING',
                  style: Theme.of(context).textTheme.titleMedium,
                ),
                if (data.openFailures.isEmpty)
                  const Padding(
                    padding: EdgeInsets.only(top: 8),
                    child: HintText('Nothing has failed on this host that is still open.'),
                  ),
                for (final failure in data.openFailures)
                  Padding(
                    padding: const EdgeInsets.only(top: 12),
                    child: Column(
                      crossAxisAlignment: CrossAxisAlignment.start,
                      children: [
                        StatusChip(failure.causeTitle ?? '${failure.applicationName} failed', statusKey: 'patch-failed'),
                        const SizedBox(height: 8),
                        Text(failure.causeExplanation ??
                            'The update for ${failure.applicationName} failed ${failure.failureCount} '
                                'time${failure.failureCount == 1 ? '' : 's'}. The Logs tab has what the install said.'),
                        if (failure.suggestedFix != null)
                          Padding(
                            padding: const EdgeInsets.only(top: 6),
                            child: HintText('Suggested fix: ${failure.suggestedFix}'),
                          ),
                      ],
                    ),
                  ),
              ],
            ),
          ),
        ),
        const SizedBox(height: 16),
        KintsugiPanel(
          child: Padding(
            padding: const EdgeInsets.all(16),
            child: Column(
              crossAxisAlignment: CrossAxisAlignment.start,
              children: [
                Text('LATEST ACTIVITY', style: Theme.of(context).textTheme.titleMedium),
                const SizedBox(height: 8),
                if (data.events.isEmpty)
                  const HintText('Nothing recorded yet. The timeline starts when this version is deployed.'),
                for (final event in data.events.take(3))
                  Padding(
                    padding: const EdgeInsets.symmetric(vertical: 3),
                    child: Text('${_stamp.format(event.occurredUtc.toLocal())}   ${event.summary}'),
                  ),
                const SizedBox(height: 8),
                LinkText(label: 'See all activity →', onTap: () => onGo(_Tab.activity)),
              ],
            ),
          ),
        ),
      ],
    );
  }
}

class _Activity extends StatelessWidget {
  const _Activity({
    required this.data,
    required this.group,
    required this.open,
    required this.onGroup,
    required this.onToggle,
    required this.onChangeRetention,
  });

  final HostActivity data;
  final HostEventGroup? group;
  final Set<String> open;
  final void Function(HostEventGroup? group) onGroup;
  final void Function(String id) onToggle;
  final VoidCallback onChangeRetention;

  @override
  Widget build(BuildContext context) {
    final palette = context.palette;
    final events = [
      for (final e in data.events)
        if (group == null || e.kind.group == group) e,
    ];

    Color dot(HostEventKind kind) => switch (kind.group) {
          HostEventGroup.failures || HostEventGroup.health => palette.red,
          HostEventGroup.patched => palette.green,
          HostEventGroup.adminActions => palette.amber,
        };

    return Column(
      crossAxisAlignment: CrossAxisAlignment.stretch,
      children: [
        Wrap(
          spacing: 8,
          runSpacing: 8,
          crossAxisAlignment: WrapCrossAlignment.center,
          children: [
            _TabButton(label: 'All', selected: group == null, onTap: () => onGroup(null)),
            for (final g in HostEventGroup.values)
              _TabButton(label: g.label, selected: group == g, onTap: () => onGroup(g)),
            const SizedBox(width: 12),
            HintText('Kept for ${data.retentionMonths} month${data.retentionMonths == 1 ? '' : 's'}.'),
            LinkText(label: 'Change', onTap: onChangeRetention),
          ],
        ),
        const SizedBox(height: 12),
        KintsugiPanel(
          child: Padding(
            padding: const EdgeInsets.symmetric(horizontal: 16, vertical: 8),
            child: events.isEmpty
                ? const Padding(
                    padding: EdgeInsets.symmetric(vertical: 12),
                    child: HintText('Nothing here.'),
                  )
                : Column(
                    children: [
                      for (final event in events)
                        InkWell(
                          onTap: () => onToggle(event.id),
                          child: Padding(
                            padding: const EdgeInsets.symmetric(vertical: 10),
                            child: Row(
                              crossAxisAlignment: CrossAxisAlignment.start,
                              children: [
                                SizedBox(
                                  width: 140,
                                  child: HintText(_stamp.format(event.occurredUtc.toLocal())),
                                ),
                                Padding(
                                  padding: const EdgeInsets.only(top: 5, right: 10),
                                  child: Container(
                                    width: 10,
                                    height: 10,
                                    decoration: BoxDecoration(color: dot(event.kind), shape: BoxShape.circle),
                                  ),
                                ),
                                Expanded(
                                  child: Column(
                                    crossAxisAlignment: CrossAxisAlignment.start,
                                    children: [
                                      Text(event.summary),
                                      HintText(event.actor == null
                                          ? 'by the agent on this host'
                                          : 'by ${event.actor}'),
                                      if (open.contains(event.id) && event.detail != null)
                                        Padding(
                                          padding: const EdgeInsets.only(top: 6),
                                          child: SelectableText(
                                            event.detail!,
                                            style: AppTheme.mono(color: palette.amber, size: 12.8),
                                          ),
                                        ),
                                    ],
                                  ),
                                ),
                              ],
                            ),
                          ),
                        ),
                    ],
                  ),
          ),
        ),
        const SizedBox(height: 12),
        const AlertBox.info(
          'This is what the server saw: Patch now and who pressed it, the host picking it up, patches, '
          'failed tries and dismissals. It does not yet show what the person at the host did (the '
          'warning appearing, a Delay click): the agents do not report that.',
        ),
      ],
    );
  }
}

class _Pending extends StatelessWidget {
  const _Pending({required this.data});

  final HostActivity data;

  @override
  Widget build(BuildContext context) {
    if (data.pendingUpdates.isEmpty) {
      return const EmptyPanel('Nothing is waiting to be installed on this host.');
    }

    return KintsugiTable(
      minWidth: 700,
      columns: const [
        TableColumnSpec(label: 'Update'),
        TableColumnSpec(label: 'Type', width: FixedColumnWidth(140)),
        TableColumnSpec(label: 'Now'),
        TableColumnSpec(label: 'Available'),
        TableColumnSpec(label: 'State', width: FixedColumnWidth(180)),
      ],
      rows: [
        for (final update in data.pendingUpdates)
          KintsugiTableRow(
            cells: [
              Text(update.name),
              Text(update.isOperatingSystem ? 'Operating system' : 'Application'),
              update.currentVersion == null ? const NoValue() : Text(update.currentVersion!),
              update.availableVersion == null ? const NoValue() : Text(update.availableVersion!),
              update.failed
                  ? const StatusChip('Failed', statusKey: 'patch-failed')
                  : const StatusChip('Waiting', statusKey: 'update-available'),
            ],
          ),
      ],
    );
  }
}

class _Logs extends StatelessWidget {
  const _Logs({required this.data});

  final HostActivity data;

  @override
  Widget build(BuildContext context) {
    final palette = context.palette;
    final failures = [
      for (final e in data.events)
        if (e.kind == HostEventKind.patchFailed && e.detail != null) e,
    ];

    if (failures.isEmpty) {
      return const EmptyPanel('No install output has been recorded for this host.');
    }

    return Column(
      crossAxisAlignment: CrossAxisAlignment.stretch,
      children: [
        const HintText(
          'What the host reported when an install failed, newest first. The same text as Failed '
          'Updates, kept here with its date.',
        ),
        const SizedBox(height: 12),
        for (final event in failures)
          Padding(
            padding: const EdgeInsets.only(bottom: 12),
            child: KintsugiPanel(
              child: Padding(
                padding: const EdgeInsets.all(14),
                child: Column(
                  crossAxisAlignment: CrossAxisAlignment.start,
                  children: [
                    Row(
                      children: [
                        Expanded(child: Text('${_stamp.format(event.occurredUtc.toLocal())}   ${event.summary}')),
                        SecondaryButton(
                          label: 'Copy',
                          onPressed: () => Clipboard.setData(ClipboardData(text: event.detail!)),
                        ),
                      ],
                    ),
                    const SizedBox(height: 8),
                    SelectableText(event.detail!, style: AppTheme.mono(color: palette.amber, size: 12.8)),
                  ],
                ),
              ),
            ),
          ),
      ],
    );
  }
}

/// How long to keep a host's history. A short list of sensible choices rather than a free number:
/// the server accepts 1 to 60 months, and these are the ones anybody asks for.
const _retentionChoices = [1, 3, 6, 12, 24, 36, 60];

class _RetentionDialog extends StatefulWidget {
  const _RetentionDialog({required this.current});

  final int current;

  @override
  State<_RetentionDialog> createState() => _RetentionDialogState();
}

class _RetentionDialogState extends State<_RetentionDialog> {
  late int _months = widget.current;

  @override
  Widget build(BuildContext context) {
    final palette = context.palette;
    final choices = {..._retentionChoices, widget.current}.toList()..sort();

    return AlertDialog(
      backgroundColor: palette.backgroundAlt,
      shape: RoundedRectangleBorder(
        borderRadius: BorderRadius.circular(6),
        side: BorderSide(color: palette.border),
      ),
      title: Text('How long to keep activity', style: Theme.of(context).textTheme.titleLarge),
      content: SizedBox(
        width: 420,
        child: Column(
          mainAxisSize: MainAxisSize.min,
          crossAxisAlignment: CrossAxisAlignment.start,
          children: [
            LabelledField(
              label: 'Keep for',
              child: KintsugiDropdown<int>(
                value: _months,
                items: choices,
                labelOf: (m) => '$m month${m == 1 ? '' : 's'}',
                onChanged: (m) => setState(() => _months = m),
              ),
            ),
            const SizedBox(height: 10),
            const HintText(
              'Applies to every host. Older events are deleted once a day; a shorter time deletes '
              'history that cannot be brought back.',
            ),
          ],
        ),
      ),
      actions: [
        SecondaryButton(label: 'Cancel', onPressed: () => Navigator.of(context).pop()),
        PrimaryButton(label: 'Save', onPressed: () => Navigator.of(context).pop(_months)),
      ],
    );
  }
}
