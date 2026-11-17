import 'dart:typed_data';

import 'invisible_characters.dart';

/// Whether a line is in both scripts, only in the old one, or only in the new one.
enum DiffKind { same, removed, added }

/// One line of a comparison, with its number in the script it came from: [oldNumber] for a line the
/// old script has, [newNumber] for one the new script has. A line in both has both.
class DiffLine {
  const DiffLine(this.kind, this.text, {this.oldNumber, this.newNumber});

  final DiffKind kind;
  final String text;
  final int? oldNumber;
  final int? newNumber;
}

/// A run of removed and added lines with unchanged lines on either side: one thing that changed.
///
/// [key] is what a reviewer's "I have read this" is remembered against. It is the content of the
/// change and nothing about where it is, so a mark survives an edit elsewhere in the script — and is
/// void, correctly, the moment the change itself is edited.
class ScriptChange {
  const ScriptChange({
    required this.index,
    required this.lines,
    required this.flags,
    required this.flaggedLines,
  });

  /// Position among the script's changes, from 0.
  final int index;
  final List<DiffLine> lines;

  /// Why the change deserves a second look, one sentence each, in the order they were found.
  final List<String> flags;

  /// Which of [lines] (by position) the flags are about, so they can be marked.
  final Set<int> flaggedLines;

  int get added => lines.where((l) => l.kind == DiffKind.added).length;
  int get removed => lines.where((l) => l.kind == DiffKind.removed).length;

  String get key => [
        for (final line in lines)
          if (line.kind != DiffKind.same) '${line.kind == DiffKind.added ? '+' : '-'}${line.text}',
      ].join('\n');
}

/// Either a run of lines that did not change or one [ScriptChange]. Alternate in a [ScriptDiff].
sealed class DiffSegment {
  const DiffSegment();
}

final class UnchangedSegment extends DiffSegment {
  const UnchangedSegment(this.lines);

  final List<DiffLine> lines;
}

final class ChangedSegment extends DiffSegment {
  const ChangedSegment(this.change);

  final ScriptChange change;
}

class ScriptDiff {
  const ScriptDiff(this.segments);

  final List<DiffSegment> segments;

  List<ScriptChange> get changes => [
        for (final segment in segments)
          if (segment is ChangedSegment) segment.change,
      ];

  bool get isEmpty => changes.isEmpty;
  int get added => changes.fold(0, (sum, c) => sum + c.added);
  int get removed => changes.fold(0, (sum, c) => sum + c.removed);
}

/// More cells than this and the line-by-line comparison is skipped for a plain "all of it changed".
/// A script is a few hundred lines; this is the edge where somebody has pasted something that is not
/// one, and a memory-hungry diff of it would be the worse answer.
const _maxCells = 4000000;

/// Compares [before] with [after] line by line.
///
/// **Every byte is compared, line endings included.** Lines are split on `\n` only, so a `\r` stays on
/// the end of its line and a CRLF/LF change is a change. This used to fold `\r\n` into `\n` so that a
/// script pasted from a Windows editor did not show every line as changed — but what is signed is the
/// exact text, and a bash script whose lines end in `\r` does not run the same, so "No changes" over a
/// line-ending change was the diff vouching for something it had not compared. Such a change is
/// instead flagged as one, and the `\r` drawn (see `invisible_characters.dart`).
ScriptDiff diffScripts(String before, String after) {
  final a = _lines(before);
  final b = _lines(after);
  return ScriptDiff(_segments(_align(a, b), before: a));
}

List<String> _lines(String text) {
  if (text.isEmpty) return const [];
  return text.split('\n');
}

List<DiffLine> _align(List<String> a, List<String> b) {
  var start = 0;
  while (start < a.length && start < b.length && a[start] == b[start]) {
    start++;
  }
  var endA = a.length;
  var endB = b.length;
  while (endA > start && endB > start && a[endA - 1] == b[endB - 1]) {
    endA--;
    endB--;
  }

  final middle = _alignMiddle(a, b, start, endA, endB);

  return [
    for (var i = 0; i < start; i++) DiffLine(DiffKind.same, a[i], oldNumber: i + 1, newNumber: i + 1),
    ...middle,
    for (var i = 0; i < a.length - endA; i++)
      DiffLine(DiffKind.same, a[endA + i], oldNumber: endA + i + 1, newNumber: endB + i + 1),
  ];
}

/// The lines between the shared start and the shared end, by longest common subsequence.
List<DiffLine> _alignMiddle(List<String> a, List<String> b, int start, int endA, int endB) {
  final n = endA - start;
  final m = endB - start;

  DiffLine removed(int i) => DiffLine(DiffKind.removed, a[i], oldNumber: i + 1);
  DiffLine added(int j) => DiffLine(DiffKind.added, b[j], newNumber: j + 1);

  if (n == 0) return [for (var j = start; j < endB; j++) added(j)];
  if (m == 0) return [for (var i = start; i < endA; i++) removed(i)];
  if (n * m > _maxCells) {
    return [for (var i = start; i < endA; i++) removed(i), for (var j = start; j < endB; j++) added(j)];
  }

  // lcs[i][j]: the longest common subsequence of a[start+i..] and b[start+j..].
  final lcs = List.generate(n + 1, (_) => Uint16List(m + 1));
  for (var i = n - 1; i >= 0; i--) {
    for (var j = m - 1; j >= 0; j--) {
      lcs[i][j] = a[start + i] == b[start + j]
          ? lcs[i + 1][j + 1] + 1
          : (lcs[i + 1][j] >= lcs[i][j + 1] ? lcs[i + 1][j] : lcs[i][j + 1]);
    }
  }

  final out = <DiffLine>[];
  var i = 0;
  var j = 0;
  while (i < n && j < m) {
    if (a[start + i] == b[start + j]) {
      out.add(DiffLine(DiffKind.same, a[start + i], oldNumber: start + i + 1, newNumber: start + j + 1));
      i++;
      j++;
    } else if (lcs[i + 1][j] >= lcs[i][j + 1]) {
      out.add(removed(start + i));
      i++;
    } else {
      out.add(added(start + j));
      j++;
    }
  }
  while (i < n) {
    out.add(removed(start + i++));
  }
  while (j < m) {
    out.add(added(start + j++));
  }
  return out;
}

List<DiffSegment> _segments(List<DiffLine> lines, {required List<String> before}) {
  final knownHosts = {for (final line in before) ...?_hostsIn(line)};
  final segments = <DiffSegment>[];
  var run = <DiffLine>[];
  var changing = false;
  var index = 0;

  void close() {
    if (run.isEmpty) return;
    segments.add(
      changing
          ? ChangedSegment(_change(index++, run, knownHosts))
          : UnchangedSegment(run),
    );
    run = [];
  }

  for (final line in lines) {
    final isChange = line.kind != DiffKind.same;
    if (isChange != changing) {
      close();
      changing = isChange;
    }
    run.add(line);
  }
  close();
  return segments;
}

ScriptChange _change(int index, List<DiffLine> lines, Set<String> knownHosts) {
  final removedHosts = {
    for (final line in lines)
      if (line.kind == DiffKind.removed) ...?_hostsIn(line.text),
  };
  final removedText = {
    for (final line in lines)
      if (line.kind == DiffKind.removed) line.text,
  };
  final removedWithoutCr = {for (final text in removedText) _withoutCr(text)};

  final flags = <String>[];
  final flagged = <int>{};

  void flag(int at, String reason) {
    flagged.add(at);
    if (!flags.contains(reason)) flags.add(reason);
  }

  for (var at = 0; at < lines.length; at++) {
    final line = lines[at];
    if (line.kind != DiffKind.added) continue;

    // Ahead of everything else, and never instead of it: these are about whether the line on screen
    // is the line that runs, which a reader needs to know before judging what it appears to do.
    final withoutCr = _withoutCr(line.text);
    if (containsInvisible(withoutCr)) {
      flag(at, 'Contains invisible or text-direction characters, shown as <U+…>');
    }
    if (!removedText.contains(line.text) && removedWithoutCr.contains(withoutCr)) {
      flag(at, 'Only the line ending changed (CRLF ↔ LF)');
    } else if (line.text.endsWith('\r')) {
      flag(at, r'Ends in a Windows line ending (\r)');
    }

    final newHosts = _hostsIn(line.text)?.where((host) => !knownHosts.contains(host)).toList() ?? const [];
    if (newHosts.isNotEmpty) {
      flag(
        at,
        removedHosts.isEmpty
            ? 'Contacts a new address: ${newHosts.first}'
            : 'Download address changed: ${removedHosts.first} → ${newHosts.first}',
      );
      continue;
    }

    for (final rule in _riskRules) {
      if (rule.pattern.hasMatch(line.text)) {
        flag(at, rule.reason);
        break;
      }
    }
  }

  return ScriptChange(index: index, lines: lines, flags: flags, flaggedLines: flagged);
}

String _withoutCr(String text) => text.endsWith('\r') ? text.substring(0, text.length - 1) : text;

final _host = RegExp('https?://([^/"\'?\\s)\\]>]+)');

/// The hosts a line mentions, or null when it names none.
List<String>? _hostsIn(String line) {
  final hosts = [for (final match in _host.allMatches(line)) match.group(1)!.toLowerCase()];
  return hosts.isEmpty ? null : hosts;
}

class _Rule {
  const _Rule(this.pattern, this.reason);

  final RegExp pattern;
  final String reason;
}

/// What makes an added line worth a second look. Deliberately few and plain: a script is read by a
/// person, and a long list of warnings is one nobody reads. Only *added* lines are judged — a line
/// that was already there was already reviewed once.
///
/// Order matters: the first rule that matches names the line.
final _riskRules = [
  _Rule(
    RegExp(r'\beval\b|Invoke-Expression|\biex\b|\|\s*(ba|z)?sh\b|\bsh\s+-c\b|\bbash\s+-c\b'),
    'Runs text as code',
  ),
  _Rule(RegExp(r'\bcurl\b|\bwget\b|Invoke-WebRequest|Invoke-RestMethod|Start-BitsTransfer'), 'Downloads from the network'),
  _Rule(RegExp(r'\bxattr\b|\bchmod\b|\bchown\b|\bchflags\b|\bicacls\b|\btakeown\b'), 'Changes file permissions or attributes'),
  _Rule(RegExp(r'\brm\s|\bpkill\b|\bkill\b|\bkillall\b|Remove-Item|Stop-Process|\brmdir\b'), 'Deletes files or stops processes'),
  _Rule(RegExp(r'\bsudo\b|\blaunchctl\b|\bsystemctl\b|Set-ExecutionPolicy|\bdefaults\s+write\b'), 'Changes system settings or services'),
];
