import 'package:flutter/material.dart';
import 'package:intl/intl.dart';

import '../theme/app_theme.dart';
import '../theme/kintsugi_palette.dart';

/// Monospace text for anything that is literally a token — a hash, a serial number, a command,
/// a repository slug. `<code>`.
class CodeText extends StatelessWidget {
  const CodeText(this.text, {super.key, this.size = 12.8, this.muted = false});

  final String text;
  final double size;
  final bool muted;

  @override
  Widget build(BuildContext context) => Text(
        text,
        style: AppTheme.mono(
          color: muted ? context.palette.muted : context.palette.neonSoft,
          size: size,
        ),
      );
}

/// Small muted explanatory text — `.hint` and `.muted-text`.
class HintText extends StatelessWidget {
  const HintText(this.text, {super.key, this.textAlign});

  final String text;
  final TextAlign? textAlign;

  @override
  Widget build(BuildContext context) =>
      Text(text, textAlign: textAlign, style: Theme.of(context).textTheme.bodySmall);
}

/// An em dash, for a cell with nothing in it. Its own widget so "no value" looks identical
/// everywhere rather than being a literal in forty places.
class NoValue extends StatelessWidget {
  const NoValue({super.key});

  @override
  Widget build(BuildContext context) =>
      Text('—', style: TextStyle(color: context.palette.muted));
}

/// A timestamp, in the visitor's own timezone.
///
/// The server sends every timestamp as UTC with an offset because it does not know where the
/// visitor is; converting is this client's job, and it is the same job the old `data-utc` script
/// did after the page had already rendered the UTC value.
class LocalTimestamp extends StatelessWidget {
  const LocalTimestamp(this.utc, {super.key});

  final DateTime? utc;

  static final _format = DateFormat('yyyy-MM-dd HH:mm');

  @override
  Widget build(BuildContext context) {
    final value = utc;
    if (value == null) return const NoValue();
    return Text(
      _format.format(value.toLocal()),
      style: Theme.of(context).textTheme.bodySmall,
    );
  }
}

/// Formats a byte count the way the Clients table did.
String formatFileSize(int bytes) {
  const units = ['B', 'KB', 'MB', 'GB'];
  var size = bytes.toDouble();
  var unit = 0;
  while (size >= 1024 && unit < units.length - 1) {
    size /= 1024;
    unit++;
  }
  final rendered = size == size.roundToDouble() ? size.round().toString() : size.toStringAsFixed(1);
  return '$rendered ${units[unit]}';
}

/// The first [length] characters of a hash or fingerprint, which is all a table column has room
/// for and all a human compares.
String shorten(String value, int length) =>
    value.length <= length ? value : value.substring(0, length);
