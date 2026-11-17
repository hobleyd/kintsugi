import 'package:intl/intl.dart';

import '../../domain/entities/device_report.dart';

/// What each platform's agent reads, for the Collect dialog and the Logs panel. The same text on both,
/// and neutral about how much is kept: the choice of detail is made in the dialog.
String logSourceFor(DevicePlatform platform) => switch (platform) {
      DevicePlatform.windows => 'the Windows System and Application event logs and Windows Error Reporting summaries',
      DevicePlatform.linux => 'the systemd journal (kernel included), the memory controller counters and kernel panic records',
      DevicePlatform.macOS => 'the unified log and kernel panic reports (text only)',
      DevicePlatform.unknown => "the operating system's own log",
    };

String platformLabel(DevicePlatform platform) => switch (platform) {
      DevicePlatform.windows => 'Windows',
      DevicePlatform.macOS => 'macOS',
      DevicePlatform.linux => 'Linux',
      DevicePlatform.unknown => 'Unknown platform',
    };

/// "12 min", "2 h 05 min".
String durationLabel(double minutes) {
  if (minutes < 60) return '${minutes.round()} min';
  final whole = minutes.floor();
  final h = whole ~/ 60;
  final m = (minutes - h * 60).round();
  return '$h h ${m.toString().padLeft(2, '0')} min';
}

final _stamp = DateFormat('MMM d, HH:mm');

/// "Nov 9, 14:20", in the browser's own time zone.
String stampLabel(DateTime utc) => _stamp.format(utc.toLocal());

/// "2 minutes ago", "3 hours ago", "5 days ago".
String relativeAgo(DateTime then, DateTime now) {
  final d = now.difference(then);
  if (d.inMinutes < 1) return 'just now';
  if (d.inMinutes < 60) return '${d.inMinutes} min ago';
  if (d.inHours < 48) return '${d.inHours} h ago';
  return '${d.inDays} days ago';
}

String kintsugiActionLabel(KintsugiActionKind kind) =>
    kind == KintsugiActionKind.osUpdate ? 'installed a system update' : 'installed an update';

/// One line of the "What happened" sentence, with the figures marked so the screen can print them bold
/// and the PDF can print them plain.
typedef SummarySpan = (String text, bool bold);

List<SummarySpan> summarySpans(DeviceReport report) {
  final shutdowns = report.events.where((e) => e.isShutdown).length;
  final fatal = report.events.where((e) => e.kind == DeviceReportEventKind.hardwareErrorFatal).length;
  final corrected = report.events.where((e) => e.kind == DeviceReportEventKind.hardwareErrorCorrected).length;
  final near = report.shutdownsNearKintsugi;

  return [
    (report.host.hostname, true),
    (' shut down by itself ', false),
    ('$shutdowns', true),
    (' time${shutdowns == 1 ? '' : 's'} in the last ${report.days} days', false),
    (report.days == 7 ? ' (${report.health.shutdowns30d} in 30 days). ' : '. ', false),
    ('It also logged ', false),
    ('$fatal', true),
    (' fatal and ', false),
    ('$corrected', true),
    (' corrected hardware error${corrected == 1 ? '' : 's'}. ', false),
    ('Kintsugi was close to ', false),
    ('$near', true),
    (' of the $shutdowns shutdown${shutdowns == 1 ? '' : 's'} (an install or system update within 30 minutes before)', false),
    (near == 0 && shutdowns > 0 ? ' and did nothing in the 30 minutes before any of them.' : '.', false),
  ];
}

String summaryText(DeviceReport report) => summarySpans(report).map((s) => s.$1).join();

/// "Most likely: X (high confidence)", or null when the server gave no hypotheses.
String? mostLikelyText(DeviceReport report) {
  final top = report.hypotheses.firstOrNull;
  if (top == null) return null;
  return 'Most likely: ${top.title} (${top.confidence.label.toLowerCase()}).';
}

/// "Possibly" only when the server says Kintsugi acted within 30 minutes before; otherwise "Not
/// involved", with the nearest action and how long before it was, or that there was none.
({bool possibly, String text}) involvement(DeviceReportEvent shutdown) {
  final n = shutdown.kintsugiNearby;
  if (n == null) return (possibly: false, text: 'No Kintsugi action before it.');
  final what = '${n.subject} (${n.action == KintsugiActionKind.osUpdate ? 'system update' : 'install'})';
  final when = '${durationLabel(n.minutesBefore)} before';
  if (n.within30Minutes) {
    return (
      possibly: true,
      text: '$what, $when.${n.action == KintsugiActionKind.osUpdate ? ' A system update can restart the device.' : ''}',
    );
  }
  return (possibly: false, text: 'Nearest Kintsugi action: $what, $when. Too far to be the cause.');
}

/// Unexpected shutdowns and hardware errors per local day over the last [days] days, oldest first, for
/// the PDF chart and its table.
List<(String, int, int)> perDayCounts(List<DeviceReportEvent> events, int days, DateTime now) {
  final today = DateTime(now.year, now.month, now.day);
  final label = days <= 7 ? DateFormat('E d') : DateFormat('d');
  return [
    for (var back = days - 1; back >= 0; back--)
      () {
        final day = DateTime(today.year, today.month, today.day - back);
        final next = DateTime(day.year, day.month, day.day + 1);
        final inDay = events.where((e) {
          final t = e.occurredUtc.toLocal();
          return !t.isBefore(day) && t.isBefore(next);
        });
        return (
          label.format(day),
          inDay.where((e) => e.isShutdown).length,
          inDay.where((e) => !e.isShutdown).length,
        );
      }(),
  ];
}
