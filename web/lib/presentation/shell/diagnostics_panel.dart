import 'package:flutter/material.dart';
import 'package:go_router/go_router.dart';
import 'package:intl/intl.dart';

import '../../core/diagnostics/diagnostics_log.dart';
import '../../core/theme/app_theme.dart';
import '../../core/theme/kintsugi_palette.dart';

/// The error and log output of everything that has run, sliding in from the right of the window.
///
/// **Beside the page, not over it.** That is forced by what the panel is for: it stays open across
/// navigations, so a strip that permanently hid a third of the page it is meant to be read
/// alongside would be worse than the alerts it replaces. So it takes width from the page rather
/// than floating above it, and the page reflows — which the Applications table, the widest in the
/// product, answers by scrolling horizontally inside its own panel. See
/// `applications_screen.dart`'s `minWidth`.
///
/// The slide is [Align] with a growing `widthFactor` over a fixed-width child pinned to its
/// **left** edge: the box opens right-to-left off the window's edge while the child's left edge
/// travels left into place, so the content enters from off-screen rather than being wiped into
/// view from a standing start. Pinning it to the right edge instead would hold the child still and
/// open a shutter over it, which is a different and worse effect. An overlay with a
/// `SlideTransition` would look the same for 200ms and then sit on top of the page, which is the
/// thing this must not do.
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
                    // Left, so the child's left edge starts at the window's right edge and travels
                    // leftward as the box opens — the body enters from off-screen. `centerRight`
                    // here would pin the body in its final place and reveal it through a widening
                    // gap instead, which reads as a shutter rather than a drawer.
                    alignment: Alignment.centerLeft,
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
        // is a region of the window, not something sitting on a page. Left, because the page is
        // what this panel now sits beside — the sidebar's own border is on its right.
        border: Border(left: BorderSide(color: palette.border)),
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
                child: _Line(line: line),
              ),
          ],
        ],
      ),
    );
  }
}

/// One line of output — tappable when the producer worked out what it is about.
///
/// **A line without a route is drawn as plain text, and a line whose router is missing is too.**
/// `GoRouter.maybeOf` is null when this panel is pumped outside the app, which is how the widget
/// tests reach it; underlining a line there would offer a navigation that could not happen. The
/// hover cursor and the underline are the whole affordance, so they have to be exactly as
/// conditional as the tap is.
class _Line extends StatefulWidget {
  const _Line({required this.line});

  final DiagnosticsLine line;

  @override
  State<_Line> createState() => _LineState();
}

class _LineState extends State<_Line> {
  bool _hovering = false;

  @override
  Widget build(BuildContext context) {
    final palette = context.palette;
    final target = widget.line.target;
    final router = GoRouter.maybeOf(context);
    final linked = target != null && router != null;

    final text = Text(
      widget.line.text,
      style: AppTheme.mono(
        color: linked && _hovering ? palette.neon : palette.text,
        size: 11.5,
      ).copyWith(
        decoration: linked ? TextDecoration.underline : null,
        decorationColor: linked ? palette.neonDim : null,
      ),
    );

    if (!linked) return text;

    return MouseRegion(
      cursor: SystemMouseCursors.click,
      onEnter: (_) => setState(() => _hovering = true),
      onExit: (_) => setState(() => _hovering = false),
      child: GestureDetector(
        // The panel stays open across this, which is the point of it being the shell's: the note
        // said which row to look at, and the row and the note are now on screen together.
        onTap: () => router.go(target),
        child: Semantics(button: true, label: widget.line.text, child: text),
      ),
    );
  }
}
