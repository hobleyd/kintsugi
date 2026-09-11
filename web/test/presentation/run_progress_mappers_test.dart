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
}
