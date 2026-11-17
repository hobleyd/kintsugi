import 'package:flutter_test/flutter_test.dart';
import 'package:kintsugi_web/data/models/upgrade_path_mapper.dart';

/// What `UpgradePathRunResultsDto` carries, and what the client does with an outcome it has not
/// heard of.
void main() {
  test('reads each row with its outcome, whatever case the server wrote it in', () {
    final results = runResultsFromJson({
      'rows': [
        {'applicationName': 'Firefox', 'platform': 'macOS', 'outcome': 'Resolved', 'note': null},
        {'applicationName': 'ncstreamer', 'platform': 'macOS', 'outcome': 'NoKnownPath', 'note': 'no upstream source found.'},
        {'applicationName': 'Slack', 'platform': 'macOS', 'outcome': 'failed', 'note': 'x'},
        {'applicationName': 'Zoom', 'platform': 'macOS', 'outcome': 'NothingToCheck', 'note': null},
      ],
      'unlisted': 3,
    });

    expect(results.rows.map((r) => r.outcome), ['resolved', 'noKnownPath', 'failed', 'nothingToCheck']);
    expect(results.rows[1].note, 'no upstream source found.');
    expect(results.rows[0].note, isNull);
    expect(results.unlisted, 3);
  });

  test('an outcome this client does not know leaves the row uncategorised rather than failing', () {
    final results = runResultsFromJson({
      'rows': [
        {'applicationName': 'Firefox', 'platform': 'macOS', 'outcome': 'SomethingNewer'},
      ],
    });

    expect(results.rows.single.outcome, isNull);
    expect(results.rows.single.applicationName, 'Firefox');
  });

  test('an absent or empty body is no rows', () {
    expect(runResultsFromJson({}).rows, isEmpty);
    expect(runResultsFromJson({'rows': null, 'unlisted': null}).unlisted, 0);
  });

  test('skips a row that is not an object', () {
    final results = runResultsFromJson({
      'rows': [
        'nope',
        {'applicationName': 'Firefox', 'platform': 'macOS', 'outcome': 'Resolved'},
      ],
    });

    expect(results.rows, hasLength(1));
  });
}
