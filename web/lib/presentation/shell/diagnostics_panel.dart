import 'package:flutter/material.dart';
import 'package:intl/intl.dart';

import '../../core/diagnostics/diagnostics_log.dart';
import '../../core/theme/app_theme.dart';
import '../../core/theme/kintsugi_palette.dart';

/// The error and log output of everything that has run, sliding in from the left of the page.
///
/// **Between the sidebar and the page, not over either.** That is forced by what the panel is
/// for: it stays open across navigations, so it must not cover the navigation, and a strip that
/// permanently hid a third of the page it was meant to be read alongside would be worse than the
/// alerts it replaces. So it takes width from the page rather than floating above it, and the
/// page reflows — which the Applications table, the widest in the product, answers by scrolling
/// horizontally inside its own panel. See `applications_screen.dart`'s `minWidth`.
///
/// The slide is [Align] with a growing `widthFactor` over a fixed-width child pinned to its right
/// edge: the box opens left-to-right while the content inside it travels right into place, which
/// is what a drawer hinged on the left looks like. An overlay with a `SlideTransition` would look
/// the same for 200ms and then sit on top of the page, which is the thing this must not do.
class DiagnosticsPanel extends StatelessWidget {
  const DiagnosticsPanel({super.key, required this.log});

  final DiagnosticsLog log;

  /// Narrow on purpose. 320 rather than the 380 this started at, because the page beside it is
  /// what the panel is for: the Applications table floors at 1100pt, the sidebar takes 240, and
  /// every point taken here is a point that table loses on a laptop display.
  static const width = 320.0;

  @override
  Widget build(BuildContext context) => AnimatedBuilder(
        animation: log,
        builder: (context, _) => TweenAnimationBuilder<double>(
          tween: Tween(begin: 0, end: log.isOpen ? 1 : 0),
          duration: const Duration(milliseconds: 180),
          curve: Curves.easeOutCubic,
          builder: (context, t, child) => t == 0
              // Gone rather than clipped to nothing. An `Align` at `widthFactor: 0` still lays its
              // child out and still answers to a semantics walk, so a hidden panel would take Tab
              // stops and be read aloud by a screen reader on every screen in the app.
              ? const SizedBox.shrink()
              : ClipRect(
                  child: Align(
                    // Right, so the body travels rightward out from under the sidebar as the box
                    // opens rather than being wiped into view from a standing start.
                    alignment: Alignment.centerRight,
                    widthFactor: t,
                    child: child,
                  ),
                ),
          // Built once and handed through, so opening does not rebuild the entry list 11 times.
          child: SizedBox(width: width, child: _Body(log: log)),
        ),
      );
}

class _Body extends StatelessWidget {
  const _Body({required this.log});

  final DiagnosticsLog log;

  @override
  Widget build(BuildContext context) {
    final palette = context.palette;
    final entries = log.entries;

    return Container(
      decoration: BoxDecoration(
        color: palette.panel,
        // One side, like the sidebar's, rather than the rounded card `KintsugiPanel` draws: this
        // is a region of the window, not something sitting on a page.
        border: Border(right: BorderSide(color: palette.border)),
      ),
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.stretch,
        children: [
          _Header(log: log),
          Expanded(
            child: entries.isEmpty
                ? Padding(
                    padding: const EdgeInsets.fromLTRB(18, 24, 18, 24),
                    child: Text(
                      'Nothing has been recorded yet. The output of a run — what it skipped, what '
                      'it could not check — appears here when one finishes.',
                      style: Theme.of(context).textTheme.bodySmall,
                    ),
                  )
                : ListView.separated(
                    padding: const EdgeInsets.fromLTRB(14, 14, 14, 24),
                    itemCount: entries.length,
                    separatorBuilder: (_, _) => const SizedBox(height: 12),
                    itemBuilder: (context, index) => _EntryCard(
                      entry: entries[index],
                      onDismiss: () => log.dismiss(entries[index].id),
                    ),
                  ),
          ),
        ],
      ),
    );
  }
}

class _Header extends StatelessWidget {
  const _Header({required this.log});

  final DiagnosticsLog log;

  @override
  Widget build(BuildContext context) {
    final palette = context.palette;

    return Container(
      padding: const EdgeInsets.fromLTRB(18, 16, 8, 16),
      decoration: BoxDecoration(
        border: Border(bottom: BorderSide(color: palette.border)),
      ),
      child: Row(
        children: [
          Expanded(
            child: Text(
              'DIAGNOSTICS',
              style: AppTheme.display(color: palette.neon, size: 11.5, letterSpacing: 1.38),
            ),
          ),
          if (!log.isEmpty)
            IconButton(
              icon: const Icon(Icons.delete_sweep_outlined, size: 17),
              color: palette.muted,
              tooltip: 'Clear everything recorded',
              onPressed: log.clear,
            ),
          IconButton(
            icon: const Icon(Icons.close, size: 17),
            color: palette.muted,
            // Named as hiding rather than as closing, because it is: what has been recorded
            // survives, and the sidebar's own Diagnostics button brings it back.
            tooltip: 'Hide this panel',
            onPressed: log.close,
          ),
        ],
      ),
    );
  }
}

/// One recorded entry: its tone, when it happened, what it said, and its output in full.
class _EntryCard extends StatelessWidget {
  const _EntryCard({required this.entry, required this.onDismiss});

  final DiagnosticsEntry entry;
  final VoidCallback onDismiss;

  static final _time = DateFormat('HH:mm:ss');

  @override
  Widget build(BuildContext context) {
    final palette = context.palette;
    final color = switch (entry.kind) {
      DiagnosticsKind.success => palette.green,
      DiagnosticsKind.error => palette.red,
      DiagnosticsKind.info => palette.neonSoft,
    };

    return Container(
      padding: const EdgeInsets.fromLTRB(12, 10, 8, 12),
      decoration: BoxDecoration(
        color: color.withValues(alpha: 0.06),
        border: Border.all(color: entry.kind == DiagnosticsKind.info ? palette.neonDim : color),
        borderRadius: BorderRadius.circular(6),
      ),
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          Row(
            crossAxisAlignment: CrossAxisAlignment.start,
            children: [
              Expanded(
                child: Text(
                  entry.title,
                  style: Theme.of(context)
                      .textTheme
                      .bodyMedium
                      ?.copyWith(color: color, fontWeight: FontWeight.w600),
                ),
              ),
              // Local time, like every other timestamp in this UI — see `LocalTimestamp`. Seconds
              // are shown where the tables show minutes: two runs of the same thing a few seconds
              // apart are the case this list is read in.
              Text(
                _time.format(entry.recordedAt),
                style: Theme.of(context).textTheme.bodySmall?.copyWith(color: palette.muted),
              ),
              const SizedBox(width: 2),
              IconButton(
                icon: const Icon(Icons.close, size: 14),
                color: palette.muted,
                visualDensity: VisualDensity.compact,
                constraints: const BoxConstraints.tightFor(width: 26, height: 26),
                padding: EdgeInsets.zero,
                tooltip: 'Dismiss',
                onPressed: onDismiss,
              ),
            ],
          ),
          if (entry.source case final source?)
            Text(
              source,
              style: Theme.of(context).textTheme.bodySmall?.copyWith(color: palette.muted),
            ),
          if (entry.summary case final summary?) ...[
            const SizedBox(height: 6),
            Text(summary, style: Theme.of(context).textTheme.bodySmall?.copyWith(color: color)),
          ],
          if (entry.lines.isNotEmpty) ...[
            const SizedBox(height: 8),
            // Monospace and one line per item, because that is what the producer meant by a list:
            // these are rows of a report, not prose, and they are read by scanning down them and
            // usually copied out. The app-wide SelectionArea in `main.dart` already covers this,
            // so nothing here adds one — nesting them misbehaves.
            for (final line in entry.lines)
              Padding(
                padding: const EdgeInsets.only(bottom: 3),
                child: Text(line, style: AppTheme.mono(color: palette.text, size: 11.5)),
              ),
          ],
        ],
      ),
    );
  }
}
