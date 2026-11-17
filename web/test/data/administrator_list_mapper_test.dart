import 'package:flutter_test/flutter_test.dart';
import 'package:kintsugi_web/data/models/settings_mapper.dart';

/// What `AdministratorDto` carries: an email, who added it and when — either of the last two null
/// for an address that was listed before they were recorded.
void main() {
  test('reads an entry with who added it and when, as a local time', () {
    final admin = administratorFromJson({
      'email': 'jake@example.com',
      'addedBy': 'juan@example.com',
      'addedUtc': '2026-11-02T09:14:00+00:00',
    });

    expect(admin.email, 'jake@example.com');
    expect(admin.addedBy, 'juan@example.com');
    expect(admin.addedUtc!.toUtc(), DateTime.utc(2026, 11, 2, 9, 14));
  });

  test('reads an entry listed before tracking: no adder, no date', () {
    final admin = administratorFromJson({'email': 'old@example.com', 'addedBy': null, 'addedUtc': null});

    expect(admin.addedBy, isNull);
    expect(admin.addedUtc, isNull);
  });

  test('reads the list in the order the server sent it', () {
    final admins = administratorsFromJson([
      {'email': 'b@example.com'},
      {'email': 'a@example.com'},
    ]);

    expect(admins.map((a) => a.email), ['b@example.com', 'a@example.com']);
  });

  test('an absent or null body is an empty list', () {
    expect(administratorsFromJson(null), isEmpty);
    expect(administratorsFromJson(const []), isEmpty);
  });
}
