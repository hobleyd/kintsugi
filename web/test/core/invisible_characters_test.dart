import 'package:flutter_test/flutter_test.dart';
import 'package:kintsugi_web/core/text/invisible_characters.dart';

/// What counts as a character a reviewer cannot see, and how it is drawn instead.
void main() {
  test('ordinary script text, tabs and newlines included, has none', () {
    expect(containsInvisible('#!/bin/bash\n\tset -e\necho "héllo — ünïcode"'), isFalse);
    expect(findInvisible('a\n\tb'), isEmpty);
  });

  test('finds every bidi control (the Trojan Source set)', () {
    for (final rune in [0x202A, 0x202B, 0x202C, 0x202D, 0x202E, 0x2066, 0x2067, 0x2068, 0x2069, 0x200E, 0x200F, 0x061C]) {
      expect(isInvisibleRune(rune), isTrue, reason: codeOf(rune));
      expect(isBidiControl(rune), isTrue, reason: codeOf(rune));
    }
  });

  test('finds zero-width, control and tag characters', () {
    for (final rune in [0x200B, 0x200C, 0x200D, 0x2060, 0xFEFF, 0x00AD, 0x0D, 0x00, 0x1B, 0x7F, 0x85, 0x2028, 0xE0041]) {
      expect(isInvisibleRune(rune), isTrue, reason: codeOf(rune));
    }
  });

  test('says where each one is, in lines and characters', () {
    final found = findInvisible('ok\nx\u202Ey\r\n😀\u200B');

    expect([for (final c in found) (c.line, c.column, c.code)], [
      (2, 2, 'U+202E'),
      (2, 4, 'U+000D'),
      (3, 2, 'U+200B'),
    ]);
    expect(found.first.name, 'right-to-left override');
  });

  test('draws each one as an escape in its place and leaves the rest alone', () {
    final spans = visibleSpans('a\u202Eb\r');

    expect([for (final s in spans) (s.text, s.escaped)], [
      ('a', false),
      ('<U+202E>', true),
      ('b', false),
      (r'\r', true),
    ]);
  });

  test('a character outside the BMP is drawn by its code point, not its surrogates', () {
    expect(visibleSpans('\u{E0041}').single.text, '<U+E0041>');
  });
}
