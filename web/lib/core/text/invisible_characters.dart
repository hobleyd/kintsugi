/// Characters a reviewer cannot see in a script but the shell still runs.
///
/// A script is signed after a person reads it, so anything that makes the text on screen differ from
/// the text executed defeats the review. Two families do that. Bidirectional controls reorder what is
/// drawn without reordering what is parsed ("Trojan Source", CVE-2021-42574): a right-to-left override
/// can make `rm -rf /tmp/x # safe` display with the comment where the path is. Zero-width and other
/// invisible characters, and control characters, draw as nothing or as a line break, so two lines
/// that look identical are not. A carriage return is in the second family — it is a line-ending-only
/// change, which the diff used to hide.
///
/// Used by `script_diff.dart` to flag a change, by `script_diff_view.dart` to draw each one as a
/// visible escape, and by `instructions_panel.dart` to warn before signing a script that has any.
library;

/// One stretch of a line: either ordinary text, or a single character drawn as [text] because it is
/// invisible or reorders what is around it.
class VisibleSpan {
  const VisibleSpan(this.text, {this.escaped = false});

  final String text;
  final bool escaped;
}

/// Where one such character is in a script, for a warning that has to say where to look.
class InvisibleCharacter {
  const InvisibleCharacter({required this.line, required this.column, required this.rune});

  /// From 1, counting `\n` as the only line break.
  final int line;

  /// From 1, in characters (runes), not UTF-16 units.
  final int column;
  final int rune;

  String get code => codeOf(rune);
  String get name => nameOf(rune);
}

/// Whether [rune] is one of the characters this file is about. A tab and `\n` are not: both are
/// ordinary in a script and both are drawn.
bool isInvisibleRune(int rune) {
  if (rune == 0x09 || rune == 0x0A) return false;
  return rune < 0x20 || // C0 controls, \r among them
      (rune >= 0x7F && rune <= 0x9F) || // DEL and C1 controls
      rune == 0x00AD || // soft hyphen
      rune == 0x034F || // combining grapheme joiner
      rune == 0x061C || // Arabic letter mark
      rune == 0x115F || rune == 0x1160 || rune == 0x3164 || rune == 0xFFA0 || // Hangul fillers
      rune == 0x180E || // Mongolian vowel separator
      (rune >= 0x200B && rune <= 0x200F) || // zero-width space/joiners, LRM, RLM
      (rune >= 0x2028 && rune <= 0x202E) || // line/paragraph separators, LRE RLE PDF LRO RLO
      (rune >= 0x2060 && rune <= 0x206F) || // word joiner, invisible operators, LRI RLI FSI PDI, ...
      (rune >= 0xFE00 && rune <= 0xFE0F) || // variation selectors
      rune == 0xFEFF || // zero-width no-break space / BOM
      (rune >= 0xFFF9 && rune <= 0xFFFB) || // interlinear annotation
      (rune >= 0xE0000 && rune <= 0xE007F) || // tag characters
      (rune >= 0xE0100 && rune <= 0xE01EF); // variation selectors supplement
}

/// Whether [rune] reorders the text around it — the Trojan Source family specifically.
bool isBidiControl(int rune) =>
    rune == 0x061C ||
    rune == 0x200E ||
    rune == 0x200F ||
    (rune >= 0x202A && rune <= 0x202E) ||
    (rune >= 0x2066 && rune <= 0x2069);

bool containsInvisible(String text) => text.runes.any(isInvisibleRune);

/// Every invisible character in [text], in order.
List<InvisibleCharacter> findInvisible(String text) {
  final found = <InvisibleCharacter>[];
  var line = 1;
  var column = 0;
  for (final rune in text.runes) {
    if (rune == 0x0A) {
      line++;
      column = 0;
      continue;
    }
    column++;
    if (isInvisibleRune(rune)) found.add(InvisibleCharacter(line: line, column: column, rune: rune));
  }
  return found;
}

/// [line] split into what can be drawn as-is and what has to be drawn as an escape.
List<VisibleSpan> visibleSpans(String line) {
  final spans = <VisibleSpan>[];
  final plain = StringBuffer();
  for (final rune in line.runes) {
    if (isInvisibleRune(rune)) {
      if (plain.isNotEmpty) {
        spans.add(VisibleSpan(plain.toString()));
        plain.clear();
      }
      spans.add(VisibleSpan(escapeOf(rune), escaped: true));
    } else {
      plain.writeCharCode(rune);
    }
  }
  if (plain.isNotEmpty) spans.add(VisibleSpan(plain.toString()));
  return spans;
}

/// How [rune] is drawn in its place: `\r` for a carriage return, since that is how anyone reading a
/// script would write it, and `<U+202E>` for everything else.
String escapeOf(int rune) => switch (rune) {
      0x0D => r'\r',
      0x00 => r'\0',
      0x1B => r'\e',
      _ => '<${codeOf(rune)}>',
    };

String codeOf(int rune) => 'U+${rune.toRadixString(16).toUpperCase().padLeft(4, '0')}';

/// A name for the warning, where there is a well-known one.
String nameOf(int rune) => switch (rune) {
      0x0D => 'carriage return',
      0x1B => 'escape',
      0x00AD => 'soft hyphen',
      0x061C => 'Arabic letter mark',
      0x200B => 'zero-width space',
      0x200C => 'zero-width non-joiner',
      0x200D => 'zero-width joiner',
      0x200E => 'left-to-right mark',
      0x200F => 'right-to-left mark',
      0x2028 => 'line separator',
      0x2029 => 'paragraph separator',
      0x202A => 'left-to-right embedding',
      0x202B => 'right-to-left embedding',
      0x202C => 'pop directional formatting',
      0x202D => 'left-to-right override',
      0x202E => 'right-to-left override',
      0x2060 => 'word joiner',
      0x2066 => 'left-to-right isolate',
      0x2067 => 'right-to-left isolate',
      0x2068 => 'first strong isolate',
      0x2069 => 'pop directional isolate',
      0xFEFF => 'zero-width no-break space',
      _ when rune < 0x20 || (rune >= 0x7F && rune <= 0x9F) => 'control character',
      _ => 'invisible character',
    };
