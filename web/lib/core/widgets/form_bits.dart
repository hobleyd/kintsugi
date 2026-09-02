import 'package:flutter/material.dart';

import '../theme/app_theme.dart';
import '../theme/kintsugi_palette.dart';
import 'panel.dart';

/// A label above a control, with optional hint text under it — `.field`.
class LabelledField extends StatelessWidget {
  const LabelledField({super.key, required this.label, required this.child, this.hints = const []});

  final String label;
  final Widget child;

  /// One paragraph each. Several of the settings fields carry two or three, and on the GitHub
  /// screen those paragraphs are the only reason the screen is open.
  final List<Widget> hints;

  @override
  Widget build(BuildContext context) => Column(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          Text(label.toUpperCase(), style: Theme.of(context).textTheme.labelLarge),
          const SizedBox(height: 6),
          child,
          for (final hint in hints) Padding(padding: const EdgeInsets.only(top: 6), child: hint),
        ],
      );
}

/// A single-line text input. Monospace, because what goes in one of these is a repository slug, a
/// client ID, an issuer URL or a model name.
class KintsugiTextField extends StatelessWidget {
  const KintsugiTextField({
    super.key,
    required this.controller,
    this.hintText,
    this.obscureText = false,
    this.keyboardType,
    this.errorText,
    this.onChanged,
    this.onEditingComplete,
    this.enabled = true,
  });

  final TextEditingController controller;
  final String? hintText;
  final bool obscureText;
  final TextInputType? keyboardType;

  /// A field-level validation failure, taken from the API's `ValidationProblemDetails`. The Razor
  /// forms could only render one flat list because `ModelState.AddModelError(string.Empty, …)`
  /// threw the property name away; the response has always carried it.
  final String? errorText;

  final ValueChanged<String>? onChanged;
  final VoidCallback? onEditingComplete;
  final bool enabled;

  @override
  Widget build(BuildContext context) => TextField(
        controller: controller,
        obscureText: obscureText,
        keyboardType: keyboardType,
        enabled: enabled,
        onChanged: onChanged,
        onEditingComplete: onEditingComplete,
        style: AppTheme.mono(color: context.palette.text),
        decoration: InputDecoration(hintText: hintText, errorText: errorText),
      );
}

/// A select. Its own widget so the four settings screens' dropdowns look identical and so the
/// popup is drawn on the panel surface rather than Material's default.
class KintsugiDropdown<T> extends StatelessWidget {
  const KintsugiDropdown({
    super.key,
    required this.value,
    required this.items,
    required this.onChanged,
    this.labelOf,
  });

  final T value;
  final List<T> items;
  final ValueChanged<T>? onChanged;

  /// How to render each item. Defaults to `toString()`, which is right for the model-name lists
  /// and wrong for the enums — those pass their own `label`.
  final String Function(T value)? labelOf;

  @override
  Widget build(BuildContext context) {
    final palette = context.palette;
    return DropdownButtonFormField<T>(
      initialValue: items.contains(value) ? value : null,
      isExpanded: true,
      dropdownColor: palette.backgroundAlt,
      icon: Icon(Icons.expand_more, color: palette.neonDim, size: 18),
      style: AppTheme.mono(color: palette.text),
      onChanged: onChanged == null ? null : (next) => next == null ? null : onChanged!(next),
      items: [
        for (final item in items)
          DropdownMenuItem(
            value: item,
            child: Text(labelOf?.call(item) ?? item.toString(), overflow: TextOverflow.ellipsis),
          ),
      ],
    );
  }
}

/// A checkbox with its label beside it — `.field-inline`.
class KintsugiCheckbox extends StatelessWidget {
  const KintsugiCheckbox({super.key, required this.label, required this.value, required this.onChanged});

  final String label;
  final bool value;
  final ValueChanged<bool> onChanged;

  @override
  Widget build(BuildContext context) => InkWell(
        onTap: () => onChanged(!value),
        child: Padding(
          padding: const EdgeInsets.symmetric(vertical: 4),
          child: Row(
            children: [
              Checkbox(value: value, onChanged: (next) => onChanged(next ?? false)),
              const SizedBox(width: 4),
              Expanded(child: Text(label, style: Theme.of(context).textTheme.bodyMedium)),
            ],
          ),
        ),
      );
}

/// The panel a settings form sits in — `.settings-form`, including its 480px cap.
class SettingsFormPanel extends StatelessWidget {
  const SettingsFormPanel({super.key, required this.children, this.maxWidth = 480});

  final List<Widget> children;
  final double maxWidth;

  @override
  Widget build(BuildContext context) => ConstrainedBox(
        constraints: BoxConstraints(maxWidth: maxWidth),
        child: KintsugiPanel(
          padding: const EdgeInsets.all(28),
          child: Column(
            crossAxisAlignment: CrossAxisAlignment.stretch,
            children: [
              for (var i = 0; i < children.length; i++) ...[
                if (i > 0) const SizedBox(height: 22),
                children[i],
              ],
            ],
          ),
        ),
      );
}

/// A form beside reference material — `.settings-columns`.
///
/// Wraps rather than switching at a breakpoint, and the two flex bases decide when it does: the
/// aside drops beneath the form at roughly a 900px viewport. Keep them small for that reason — a
/// comfortable-looking basis here reads as "the instructions are at the bottom" on a laptop.
class SettingsColumns extends StatelessWidget {
  const SettingsColumns({super.key, required this.form, required this.aside});

  final Widget form;
  final Widget aside;

  @override
  Widget build(BuildContext context) => LayoutBuilder(
        builder: (context, constraints) {
          const gap = 24.0;
          final stacked = constraints.maxWidth < 616 + gap;

          if (stacked) {
            return Column(
              crossAxisAlignment: CrossAxisAlignment.stretch,
              children: [form, const SizedBox(height: gap), aside],
            );
          }

          return Row(
            crossAxisAlignment: CrossAxisAlignment.start,
            children: [
              Expanded(flex: 22, child: form),
              const SizedBox(width: gap),
              Expanded(flex: 15, child: aside),
            ],
          );
        },
      );
}

/// The reference panel beside a form — `.settings-aside`.
class SettingsAside extends StatelessWidget {
  const SettingsAside({super.key, required this.title, required this.children});

  final String title;
  final List<Widget> children;

  @override
  Widget build(BuildContext context) => KintsugiPanel(
        padding: const EdgeInsets.all(28),
        child: Column(
          crossAxisAlignment: CrossAxisAlignment.start,
          children: [
            Text(title.toUpperCase(), style: Theme.of(context).textTheme.titleLarge),
            const SizedBox(height: 12),
            ...children,
          ],
        ),
      );
}

/// A numbered list of steps, for the setup instructions the settings screens carry inline.
class NumberedSteps extends StatelessWidget {
  const NumberedSteps(this.steps, {super.key});

  final List<Widget> steps;

  @override
  Widget build(BuildContext context) => Column(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          for (var i = 0; i < steps.length; i++)
            Padding(
              padding: const EdgeInsets.only(bottom: 6),
              child: Row(
                crossAxisAlignment: CrossAxisAlignment.start,
                children: [
                  SizedBox(
                    width: 20,
                    child: Text(
                      '${i + 1}.',
                      style: Theme.of(context).textTheme.bodySmall?.copyWith(
                            color: context.palette.neonDim,
                          ),
                    ),
                  ),
                  Expanded(child: steps[i]),
                ],
              ),
            ),
        ],
      );
}

/// An `<h3>` with no leading gap, for use as the first thing inside a panel.
///
/// Distinct from [SubHeading], which carries the space a heading needs when it follows a table.
class SubHeadingTight extends StatelessWidget {
  const SubHeadingTight(this.text, {super.key});

  final String text;

  @override
  Widget build(BuildContext context) => Padding(
        padding: const EdgeInsets.only(bottom: 4),
        child: Text(text.toUpperCase(), style: Theme.of(context).textTheme.titleMedium),
      );
}
