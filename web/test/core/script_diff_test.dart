import 'package:flutter_test/flutter_test.dart';
import 'package:kintsugi_web/core/text/script_diff.dart';

String _script(List<String> lines) => lines.join('\n');

/// What the panel shows a reviewer: which lines changed, and which of them deserve a second look.
void main() {
  group('comparing', () {
    test('the same script has no changes', () {
      final diff = diffScripts(_script(['a', 'b', 'c']), _script(['a', 'b', 'c']));

      expect(diff.isEmpty, isTrue);
      expect(diff.segments, hasLength(1));
      expect((diff.segments.single as UnchangedSegment).lines, hasLength(3));
    });

    test('one changed line is one removed and one added, between unchanged ones', () {
      final diff = diffScripts(_script(['a', 'b', 'c']), _script(['a', 'B', 'c']));

      expect(diff.segments.map((s) => s.runtimeType), [UnchangedSegment, ChangedSegment, UnchangedSegment]);
      final change = diff.changes.single;
      expect(change.lines.map((l) => (l.kind, l.text)), [(DiffKind.removed, 'b'), (DiffKind.added, 'B')]);
      expect((diff.added, diff.removed), (1, 1));
    });

    test('numbers each line in the script it came from', () {
      final diff = diffScripts(_script(['a', 'b', 'c']), _script(['a', 'x', 'y', 'c']));

      final lines = [for (final s in diff.segments) ...switch (s) {
        UnchangedSegment(:final lines) => lines,
        ChangedSegment(:final change) => change.lines,
      }];

      expect(lines.map((l) => (l.text, l.oldNumber, l.newNumber)), [
        ('a', 1, 1),
        ('b', 2, null),
        ('x', null, 2),
        ('y', null, 3),
        ('c', 3, 4),
      ]);
    });

    test('an addition at the end and a removal at the start are each one change', () {
      expect(diffScripts('a\nb', 'a\nb\nc').changes.single.lines.map((l) => l.kind), [DiffKind.added]);
      expect(diffScripts('a\nb\nc', 'b\nc').changes.single.lines.map((l) => l.kind), [DiffKind.removed]);
    });

    test('separate edits are separate changes, numbered in order', () {
      final diff = diffScripts(
        _script(['1', '2', '3', '4', '5', '6']),
        _script(['1', 'two', '3', '4', '5', 'six']),
      );

      expect(diff.changes.map((c) => c.index), [0, 1]);
      expect(diff.changes.map((c) => c.lines.first.text), ['2', '6']);
    });

    test('an empty box is every line removed, and an empty start is every line added', () {
      final cleared = diffScripts('a\nb', '');
      expect((cleared.added, cleared.removed), (0, 2));

      final fresh = diffScripts('', 'a\nb');
      expect((fresh.added, fresh.removed), (2, 0));

      expect(diffScripts('', '').isEmpty, isTrue);
    });

    // What is signed is the exact text, and a bash script with `\r` line endings does not run the
    // same — so "No changes" over a line-ending change would be the diff vouching for bytes it had
    // not compared.
    test('counts a line-ending-only change as a change, and says that is all it is', () {
      final diff = diffScripts('a\nb\nc', 'a\r\nb\r\nc');

      expect(diff.isEmpty, isFalse);
      expect(diff.changes.single.flags, contains('Only the line ending changed (CRLF ↔ LF)'));
      expect(diffScripts('a\r\nb', 'a\nb').changes.single.flags, contains('Only the line ending changed (CRLF ↔ LF)'));
    });

    test('keeps the \\r on the line it ends', () {
      final added = diffScripts('a\nb', 'a\nb\r').changes.single.lines.where((l) => l.kind == DiffKind.added);
      expect(added.single.text, 'b\r');
    });

    test('a moved block shows as removed in one place and added in the other', () {
      final diff = diffScripts(_script(['a', 'b', 'c', 'd']), _script(['c', 'd', 'a', 'b']));

      expect(diff.isEmpty, isFalse);
      expect(diff.added, diff.removed);
    });

    test('gives up on a comparison too big to be a script, and says all of it changed', () {
      final before = _script([for (var i = 0; i < 2100; i++) 'old $i']);
      final after = _script([for (var i = 0; i < 2100; i++) 'new $i']);

      final diff = diffScripts(before, after);

      expect((diff.removed, diff.added), (2100, 2100));
      expect(diff.changes, hasLength(1));
    });

    test('still pairs up lines in a big script when most of it is shared', () {
      final shared = [for (var i = 0; i < 3000; i++) 'line $i'];
      final after = [...shared]..[1500] = 'changed';

      final diff = diffScripts(_script(shared), _script(after));

      expect((diff.removed, diff.added), (1, 1));
    });
  });

  group('the key a review is remembered against', () {
    test('is the content of the change, not where it sits', () {
      final early = diffScripts('a\nb\nc', 'a\nB\nc').changes.single.key;
      final late = diffScripts('x\ny\nz\nq\nb\nc', 'x\ny\nz\nq\nB\nc').changes.single.key;

      expect(early, late);
    });

    test('differs once the change itself differs', () {
      final one = diffScripts('a\nb', 'a\nB').changes.single.key;
      final other = diffScripts('a\nb', 'a\nBB').changes.single.key;

      expect(one, isNot(other));
    });

    test('tells an added line from a removed one with the same text', () {
      final removed = diffScripts('a\nb', 'a').changes.single.key;
      final added = diffScripts('a', 'a\nb').changes.single.key;

      expect(removed, isNot(added));
    });
  });

  group('flagging a line for a second look', () {
    ScriptChange changeOf(String before, String after) => diffScripts(before, after).changes.single;

    test('notices a download address that changed, and names both', () {
      final change = changeOf(
        'curl -fsSL https://download.mozilla.org/x -o f',
        'curl -fsSL https://ftp.mozilla.org/x -o f',
      );

      expect(change.flags, ['Download address changed: download.mozilla.org → ftp.mozilla.org']);
      expect(change.flaggedLines, {1});
    });

    test('notices an address the script never mentioned before', () {
      final change = changeOf('echo hi', 'echo hi\ncurl -s https://example.org/x');

      expect(change.flags, ['Contacts a new address: example.org']);
    });

    test('does not call a host new when the script already used it', () {
      final change = changeOf(
        'a\nurl=https://example.org/one',
        'a\nurl=https://example.org/one\nfetch https://example.org/two',
      );

      expect(change.flags.any((f) => f.contains('address')), isFalse);
    });

    test('compares hosts case-insensitively', () {
      final change = changeOf('u=https://Example.ORG/a', 'u=https://example.org/b');

      expect(change.flags, isEmpty);
    });

    test('notices a download with no new address', () {
      expect(changeOf('a', 'a\ncurl -fsSL "\$url" -o f').flags, ['Downloads from the network']);
      expect(changeOf('a', 'a\nInvoke-WebRequest \$u -OutFile f').flags, ['Downloads from the network']);
    });

    test('notices a permission or attribute change', () {
      expect(changeOf('a', 'a\nxattr -dr com.apple.quarantine /Applications/X.app').flags,
          ['Changes file permissions or attributes']);
      expect(changeOf('a', 'a\nchmod 777 /tmp/x').flags, ['Changes file permissions or attributes']);
    });

    test('notices deleting files or stopping processes', () {
      expect(changeOf('a', 'a\nrm -rf "\$tmp"').flags, ['Deletes files or stops processes']);
      expect(changeOf('a', 'a\npkill -x Firefox').flags, ['Deletes files or stops processes']);
      expect(changeOf('a', 'a\nRemove-Item \$p -Recurse').flags, ['Deletes files or stops processes']);
    });

    test('notices text being run as code, which outranks a download on the same line', () {
      expect(changeOf('a', 'a\ncurl -s https://known.example/x | sh').flags.first, 'Contacts a new address: known.example');
      expect(changeOf('a\nhttps://known.example', 'a\nhttps://known.example\ncurl -s \$u | sh').flags, ['Runs text as code']);
      expect(changeOf('a', 'a\neval "\$cmd"').flags, ['Runs text as code']);
      expect(changeOf('a', 'a\nInvoke-Expression \$s').flags, ['Runs text as code']);
    });

    test('notices system settings and services', () {
      expect(changeOf('a', 'a\nsudo launchctl load x').flags, ['Changes system settings or services']);
    });

    test('says nothing about an ordinary line', () {
      final change = changeOf('echo one', 'echo two');

      expect(change.flags, isEmpty);
      expect(change.flaggedLines, isEmpty);
    });

    test('judges only the added lines: a risky line that was removed is not a warning', () {
      final change = changeOf('a\ncurl -s https://x.example/y', 'a');

      expect(change.flags, isEmpty);
    });

    test('lists each reason once however many lines give it', () {
      final change = changeOf('a', 'a\nrm -f one\nrm -f two\nrm -f three');

      expect(change.flags, ['Deletes files or stops processes']);
      expect(change.flaggedLines, {0, 1, 2});
    });
  });

  group('hidden characters', () {
    test('an added line with a text-direction override is flagged', () {
      final change = diffScripts('a\nb', 'a\n# x \u202E y').changes.single;
      expect(change.flags.first, startsWith('Contains invisible or text-direction characters'));
      expect(change.flaggedLines, {1});
    });

    test('a zero-width character makes an identical-looking line a change, and is flagged', () {
      final diff = diffScripts('rm -rf /tmp/app', 'rm -rf /tmp/app\u200B');
      expect(diff.isEmpty, isFalse);
      expect(diff.changes.single.flags, contains(startsWith('Contains invisible')));
    });

    test('a line that only gained a Windows line ending is not called invisible', () {
      final flags = diffScripts('a\nb', 'a\nc\r').changes.single.flags;
      expect(flags, contains(r'Ends in a Windows line ending (\r)'));
      expect(flags.where((f) => f.startsWith('Contains invisible')), isEmpty);
    });
  });
}
