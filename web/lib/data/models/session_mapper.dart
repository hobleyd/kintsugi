import '../../domain/entities/session.dart';

/// Reads a `SessionDto`.
Session sessionFromJson(Map<String, dynamic> json) => Session(
      authenticationSettingsSaved: json['authenticationSettingsSaved'] as bool? ?? false,
      authenticationEnabled: json['authenticationEnabled'] as bool? ?? false,
      signedIn: json['signedIn'] as bool? ?? false,
      // Absent on a server that predates the administrators list; treating that as "authorised"
      // keeps a newer UI from denying everyone in front of an older API.
      authorized: json['authorized'] as bool? ?? true,
      userName: json['userName'] as String?,
      email: json['email'] as String?,
      providerDisplayName: json['providerDisplayName'] as String? ?? 'single sign-on',
      canSignIn: json['canSignIn'] as bool? ?? false,
      callbackUrl: json['callbackUrl'] as String? ?? '',
      signOutCallbackUrl: json['signOutCallbackUrl'] as String? ?? '',
    );
