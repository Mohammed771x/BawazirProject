import 'package:flutter/material.dart';
import 'package:shared_preferences/shared_preferences.dart';

/// Device-local *presentation* preferences — language and theme.
///
/// Rule R4 says the device is never the source of truth for learning state.
/// These two values are not learning state: they describe how this particular
/// installation renders the UI, so they legitimately live on the device and are
/// deliberately kept apart from anything the backend owns.
abstract class AppPreferences {
  /// The product decision (demo review §2): the first launch is Arabic, because
  /// the current audience is Arabic-speaking. The device locale does **not**
  /// override this — only an explicit choice in Settings does.
  static const Locale defaultLocale = Locale('ar');

  static const List<Locale> supportedLocales = [Locale('ar'), Locale('en')];

  /// Loads the real, disk-backed store. Falls back to an in-memory store if the
  /// platform channel is unavailable, so a preferences failure can never keep
  /// the app from starting.
  static Future<AppPreferences> load() async {
    try {
      return _StoredAppPreferences(await SharedPreferences.getInstance());
    } catch (_) {
      return InMemoryAppPreferences();
    }
  }

  Locale get locale;

  Future<void> setLocale(Locale locale);

  ThemeMode get themeMode;

  Future<void> setThemeMode(ThemeMode mode);
}

class _StoredAppPreferences implements AppPreferences {
  _StoredAppPreferences(this._prefs);

  static const _localeKey = 'wordos.ui.locale';
  static const _themeKey = 'wordos.ui.themeMode';

  final SharedPreferences _prefs;

  @override
  Locale get locale => switch (_prefs.getString(_localeKey)) {
        'en' => const Locale('en'),
        'ar' => const Locale('ar'),
        _ => AppPreferences.defaultLocale,
      };

  @override
  Future<void> setLocale(Locale locale) async {
    try {
      await _prefs.setString(_localeKey, locale.languageCode);
    } catch (_) {
      // Non-fatal: the choice still applies for this app run.
    }
  }

  @override
  ThemeMode get themeMode => switch (_prefs.getString(_themeKey)) {
        'light' => ThemeMode.light,
        'dark' => ThemeMode.dark,
        _ => ThemeMode.system,
      };

  @override
  Future<void> setThemeMode(ThemeMode mode) async {
    try {
      await _prefs.setString(_themeKey, mode.name);
    } catch (_) {
      // Non-fatal.
    }
  }
}

/// Used by tests and as the fallback when the platform store is unavailable.
class InMemoryAppPreferences implements AppPreferences {
  InMemoryAppPreferences({Locale? locale, ThemeMode? themeMode})
      : locale = locale ?? AppPreferences.defaultLocale,
        themeMode = themeMode ?? ThemeMode.system;

  @override
  Locale locale;

  @override
  ThemeMode themeMode;

  @override
  Future<void> setLocale(Locale value) async => locale = value;

  @override
  Future<void> setThemeMode(ThemeMode mode) async => themeMode = mode;
}
