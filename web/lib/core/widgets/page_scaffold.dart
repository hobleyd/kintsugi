import 'package:flutter/material.dart';

import '../theme/kintsugi_palette.dart';

/// A screen's title, subtitle and body, laid out the way every screen lays them out.
///
/// The scrolling and the padding live here rather than in each screen so a new screen cannot get
/// the page rhythm subtly wrong, and so the one thing that has to be true of all of them — wide
/// content scrolls inside its own box rather than making the page scroll sideways — is true by
/// construction.
class PageScaffold extends StatelessWidget {
  const PageScaffold({super.key, required this.title, this.subtitle, required this.children});

  final String title;
  final String? subtitle;
  final List<Widget> children;

  @override
  Widget build(BuildContext context) => SingleChildScrollView(
        padding: const EdgeInsets.fromLTRB(24, 48, 24, 64),
        child: Column(
          crossAxisAlignment: CrossAxisAlignment.start,
          children: [
            Text(title.toUpperCase(), style: Theme.of(context).textTheme.headlineLarge),
            if (subtitle != null) ...[
              const SizedBox(height: 6),
              Text(
                subtitle!,
                style: Theme.of(context).textTheme.bodyMedium?.copyWith(color: context.palette.muted),
              ),
            ],
            const SizedBox(height: 32),
            ...children,
          ],
        ),
      );
}

/// An `<h2>` with explanatory prose, and the screen's buttons opposite — `.section-header`.
class SectionHeader extends StatelessWidget {
  const SectionHeader({super.key, required this.title, this.hints = const [], this.actions = const []});

  final String title;

  /// One paragraph each. Kept as a list rather than one string because these run to two or three
  /// paragraphs on the busier screens and they need the spacing between them.
  final List<Widget> hints;

  final List<Widget> actions;

  @override
  Widget build(BuildContext context) => Padding(
        padding: const EdgeInsets.only(bottom: 16),
        child: Row(
          crossAxisAlignment: CrossAxisAlignment.start,
          children: [
            Expanded(
              child: Column(
                crossAxisAlignment: CrossAxisAlignment.start,
                children: [
                  Text(title.toUpperCase(), style: Theme.of(context).textTheme.titleLarge),
                  for (final hint in hints) Padding(padding: const EdgeInsets.only(top: 8), child: hint),
                ],
              ),
            ),
            if (actions.isNotEmpty) ...[
              const SizedBox(width: 24),
              Wrap(spacing: 12, runSpacing: 8, children: actions),
            ],
          ],
        ),
      );
}

/// An `<h3>`.
class SubHeading extends StatelessWidget {
  const SubHeading(this.text, {super.key});

  final String text;

  @override
  Widget build(BuildContext context) => Padding(
        padding: const EdgeInsets.only(top: 32, bottom: 12),
        child: Text(text.toUpperCase(), style: Theme.of(context).textTheme.titleMedium),
      );
}
