import '../../domain/entities/session.dart';

/// Reads a `SessionDto`.
Session sessionFromJson(Map<String, dynamic> json) => Session(
      authenticationSettingsSaved: json['authenticationSettingsSaved'] as bool? ?? false,
      authenticationEnabled: json['authenticationEnabled'] as bool? ?? false,
      signedIn: json['signedIn'] as bool? ?? false,
      userName: json['userName'] as String?,
      providerDisplayName: json['providerDisplayName'] as String? ?? 'single sign-on',
      canSignIn: json['canSignIn'] as bool? ?? false,
      callbackUrl: json['callbackUrl'] as String? ?? '',
      signOutCallbackUrl: json['signOutCallbackUrl'] as String? ?? '',
    );
