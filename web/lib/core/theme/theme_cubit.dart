import 'package:flutter/material.dart';
import 'package:flutter_bloc/flutter_bloc.dart';
import 'package:shared_preferences/shared_preferences.dart';

/// Holds the chosen theme and remembers it across visits.
///
/// A per-browser preference with no server-side record, exactly as it was before — there is no
/// account setting for it and no reason to add one.
///
/// The storage key is coupled to `web/index.html`, which reads the same key inline before Flutter
/// boots so the loading screen does not paint the dark background at a light-mode visitor. Note
/// the prefix: `shared_preferences` namespaces everything it writes under `flutter.`, so the key
/// on the wire is `flutter.kintsugi-theme` and that is what the script there looks for. Changing
/// this name means changing it there too.
class ThemeCubit extends Cubit<ThemeMode> {
  ThemeCubit(this._preferences) : super(_read(_preferences));

  static const storageKey = 'kintsugi-theme';

  final SharedPreferences _preferences;

  static ThemeMode _read(SharedPreferences preferences) => switch (preferences.getString(storageKey)) {
        'light' => ThemeMode.light,
        // Anything else, including nothing stored, keeps the dark default the product is designed
        // in. Deliberately not ThemeMode.system: the light theme is a choice made here, not a
        // reflection of the operating system's.
        _ => ThemeMode.dark,
      };

  Future<void> toggle() async {
    final next = state == ThemeMode.light ? ThemeMode.dark : ThemeMode.light;
    emit(next);
    await _preferences.setString(storageKey, next == ThemeMode.light ? 'light' : 'dark');
  }
}
