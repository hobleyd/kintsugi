import 'package:flutter_test/flutter_test.dart';
import 'package:kintsugi_web/core/network/json_reader.dart';

enum _Colour { red, green, blue }

void main() {
  group('enumFromJson', () {
    const values = _Colour.values;
    const names = ['Red', 'Green', 'Blue'];

    test('reads an ordinal, because several server-side enums carry no JSON converter', () {
      expect(enumFromJson(1, values, names, _Colour.red), _Colour.green);
    });

    test('reads a name, because the ones that do carry a converter write names', () {
      expect(enumFromJson('Blue', values, names, _Colour.red), _Colour.blue);
    });

    test('matches a name case-insensitively', () {
      expect(enumFromJson('blue', values, names, _Colour.red), _Colour.blue);
    });

    test('falls back rather than throwing on an unknown name', () {
      // A member added to a server-side enum should degrade to "unknown" on an older client, not
      // blank the screen.
      expect(enumFromJson('Chartreuse', values, names, _Colour.red), _Colour.red);
    });

    test('falls back on an out-of-range ordinal', () {
      expect(enumFromJson(99, values, names, _Colour.red), _Colour.red);
      expect(enumFromJson(-1, values, names, _Colour.red), _Colour.red);
    });

    test('falls back on null and on a type it cannot read', () {
      expect(enumFromJson(null, values, names, _Colour.green), _Colour.green);
      expect(enumFromJson(const {}, values, names, _Colour.green), _Colour.green);
    });
  });

  group('dateTimeFromJson', () {
    test('parses an offset timestamp and converts it to local time', () {
      final parsed = dateTimeFromJson('2026-09-02T10:30:00+00:00');
      expect(parsed, isNotNull);
      expect(parsed!.isUtc, isFalse);
      expect(parsed.toUtc(), DateTime.utc(2026, 9, 2, 10, 30));
    });

    test('returns null rather than throwing for a missing or unparseable value', () {
      expect(dateTimeFromJson(null), isNull);
      expect(dateTimeFromJson(''), isNull);
      expect(dateTimeFromJson('not a date'), isNull);
      expect(dateTimeFromJson(42), isNull);
    });
  });

  group('list helpers', () {
    test('stringListFromJson tolerates a missing array', () {
      expect(stringListFromJson(null), isEmpty);
      expect(stringListFromJson(['a', 'b']), ['a', 'b']);
    });

    test('listFromJson skips entries that are not objects', () {
      final result = listFromJson<String>(
        [
          {'name': 'kept'},
          'dropped',
        ],
        (json) => json['name'] as String,
      );
      expect(result, ['kept']);
    });
  });
}
