import 'package:equatable/equatable.dart';

/// Whether sign-in is configured, whether it is required, and whether this browser has done it.
/// Mirrors `SessionDto`.
///
/// This is what the app reads before it renders anything. It replaces three server-side redirects
/// that a static bundle cannot receive — see `Program.cs`, which explains where each of them went.
class Session extends Equatable {
  const Session({
    required this.authenticationSettingsSaved,
    required this.authenticationEnabled,
    required this.signedIn,
    required this.userName,
    required this.providerDisplayName,
    required this.canSignIn,
    required this.callbackUrl,
    required this.signOutCallbackUrl,
  });

  /// False on a fresh deploy. The app then locks itself to the Authentication settings screen,
  /// which is what the old redirect to `/settings/authentication` did: there is no way to sign in
  /// and no administrator has decided whether sign-in is required, so leaving the rest of the UI
  /// reachable would be the wrong default in the other direction.
  final bool authenticationSettingsSaved;

  final bool authenticationEnabled;
  final bool signedIn;
  final String? userName;
  final String providerDisplayName;

  /// Whether the sign-in button should do anything: sign-in is both enabled and configured
  /// completely enough to challenge.
  final bool canSignIn;

  /// The redirect URI to register with the identity provider.
  final String callbackUrl;

  /// The post-sign-out redirect URI. Not optional at the provider — signing out signs out of the
  /// provider too, and without this registered the provider rejects the sign-out.
  final String signOutCallbackUrl;

  /// Whether the rest of the UI should be reachable at all.
  ///
  /// The first clause is the fresh-deploy lockdown. The second is the ordinary gate, and it
  /// mirrors `RequireAdminSessionAttribute` exactly rather than being a second opinion: a saved
  /// row with sign-in disabled means the administrator has deliberately chosen to run this site
  /// open, and refusing to render it here while the API happily answers would be a lock on the
  /// wrong side of the door.
  bool get canUseApp =>
      authenticationSettingsSaved && (!authenticationEnabled || signedIn);

  @override
  List<Object?> get props => [
        authenticationSettingsSaved,
        authenticationEnabled,
        signedIn,
        userName,
        providerDisplayName,
        canSignIn,
        callbackUrl,
        signOutCallbackUrl,
      ];
}
