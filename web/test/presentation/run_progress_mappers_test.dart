import 'package:flutter_test/flutter_test.dart';
import 'package:kintsugi_web/domain/entities/upgrade_path.dart';
import 'package:kintsugi_web/presentation/applications/run_progress_mappers.dart';

/// What the two runs say when they finish, which for "Check for Updates" is the *only* account of
/// it anywhere: a check writes nothing to the row it checked, so the Applications table's
/// "Check Failed" badge — the AI scan's `UpgradePathStatus.Failed` — never appears on the back of
/// one. A count of failures with no rows behind it sends a reader looking through the table for
/// eight rows that are not there.
void main() {
  UpdateCheckStatus status({
    int failed = 0,
    int skipped = 0,
    List<String> notes = const [],
  }) =>
      UpdateCheckStatus(
        isRunning: false,
        total: 10,
        completed: 10,
        updated: 2,
        unchanged: 8 - failed - skipped,
        failed: failed,
        skipped: skipped,
        startedUtc: null,
        completedUtc: null,
        faultReason: null,
        notes: notes,
      );

  test('reports rows with nothing to check apart from rows that failed', () {
    final progress = updateCheckProgress(status(failed: 1, skipped: 8));

    expect(progress.summary, contains('1 failed'));
    expect(progress.summary, contains('8 with nothing to check'));
    expect(progress.detail, contains('8 with nothing to check'));
  });

  test('carries every reason through, so the counts name their rows', () {
    final progress = updateCheckProgress(status(
      skipped: 2,
      notes: const [
        'Slack (pm:Homebrew): No update script to check.',
        'Zoom (pm:Homebrew): No update script to check.',
      ],
    ));

    expect(progress.notes, hasLength(2));
    expect(progress.notes.first, startsWith('Slack (pm:Homebrew)'));
  });

  test('says nothing extra when a run had nothing to report', () {
    expect(updateCheckProgress(status()).notes, isEmpty);
  });

  /// A note names the row it is about, and the diagnostics panel turns that into a link to it —
  /// otherwise reading the note leaves somebody to find one name among everything the fleet has
  /// installed, which is the work the note creates rather than saves.
  ///
  /// Both coordinators build a note as `{ApplicationName} ({Platform}): {Note}` — see
  /// `UpdateCheckCoordinator.cs` and `UpgradePathScanCoordinator.cs`. Nothing enforces that from
  /// here, which is why anything that does not match falls back to plain text rather than to a
  /// link somewhere wrong.
  group('noteLine', () {
    test('links a note to the application it names', () {
      final line = noteLine('Slack (pm:Homebrew): No update script to check.');

      expect(line.text, 'Slack (pm:Homebrew): No update script to check.');
      expect(line.target, '/applications?search=Slack');
    });

    test('takes the whole name when the name itself has brackets in it', () {
      // The shape this exists for: Windows redistributables carry an architecture in brackets, and
      // a lazy match would stop at the first one and link to half a name.
      final line = noteLine(
        'Microsoft Visual C++ 2015-2022 Redistributable (x64) (Windows): the script failed.',
      );

      expect(
        line.target,
        '/applications?search=Microsoft+Visual+C%2B%2B+2015-2022+Redistributable+%28x64%29',
      );
    });

    test('leaves the overflow line alone, because it names no row', () {
      final line = noteLine('...and 12 more not listed. The counts above cover all of them.');

      expect(line.target, isNull);
    });

    test('leaves anything it does not recognise alone rather than guessing', () {
      expect(noteLine('The run could not reach the script repository.').target, isNull);
    });
  });
}
