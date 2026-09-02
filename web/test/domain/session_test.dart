import 'package:flutter_test/flutter_test.dart';
import 'package:kintsugi_web/domain/entities/session.dart';

Session session({
  required bool saved,
  required bool enabled,
  required bool signedIn,
}) =>
    Session(
      authenticationSettingsSaved: saved,
      authenticationEnabled: enabled,
      signedIn: signedIn,
      userName: signedIn ? 'admin@example.com' : null,
      providerDisplayName: 'Google Workspace',
      canSignIn: saved && enabled,
      callbackUrl: 'https://kintsugi.example.com/signin-oidc',
      signOutCallbackUrl: 'https://kintsugi.example.com/signout-callback-oidc',
    );

void main() {
  group('Session.canUseApp', () {
    test('is false on a fresh deploy, whatever else is true', () {
      // The fresh-deploy lockdown: nothing has been saved, so there is no way to sign in and no
      // administrator has decided whether sign-in is required. Everything but the Authentication
      // screen stays closed until one has, which is what the redirect in Program.cs used to do.
      expect(session(saved: false, enabled: false, signedIn: false).canUseApp, isFalse);
      expect(session(saved: false, enabled: true, signedIn: false).canUseApp, isFalse);
      expect(session(saved: false, enabled: true, signedIn: true).canUseApp, isFalse);
    });

    test('is true when a provider is saved and sign-in is deliberately switched off', () {
      // Mirrors RequireAdminSessionAttribute exactly rather than being a second opinion: a saved
      // row with sign-in disabled means the administrator has chosen to run this site open, and
      // refusing to render it while the API happily answers would be a lock on the wrong side of
      // the door.
      expect(session(saved: true, enabled: false, signedIn: false).canUseApp, isTrue);
    });

    test('requires a signed-in caller once sign-in is enabled', () {
      expect(session(saved: true, enabled: true, signedIn: false).canUseApp, isFalse);
      expect(session(saved: true, enabled: true, signedIn: true).canUseApp, isTrue);
    });
  });
}
