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
      // Tours on: this store forgets on restart, so a learner sees each one
      // once per launch — better than an app nobody ever explained.
      return InMemoryAppPreferences(toursSeen: false);
    }
  }

  Locale get locale;

  Future<void> setLocale(Locale locale);

  ThemeMode get themeMode;

  Future<void> setThemeMode(ThemeMode mode);

  /// Whether the product tour has been seen on this installation.
  ///
  /// Device-local on purpose, and not learning state: onboarding explains what
  /// WordOS *is*, so it belongs to the install rather than to the account. A
  /// learner who has seen it never sees it again, including before they have an
  /// account to attach it to — which is exactly when it is shown.
  bool get onboardingSeen;

  Future<void> setOnboardingSeen(bool seen);

  /// Whether this installation raises daily reminders (ADR-076).
  ///
  /// Device-local, and not a contradiction of rule R4. What is being stored is
  /// not learning state: it is whether *this phone* should set alarms for
  /// itself. A learner with the app on a tablet they use at home and a phone
  /// they carry has a real reason to want one and not the other, and a
  /// server-side flag could not express it.
  ///
  /// Defaults to on. The OS still asks before a single notification is shown,
  /// so a learner who does not want them says no once and is never asked again;
  /// defaulting to off would mean the feature never ran for anybody who did not
  /// go looking for a switch.
  bool get remindersEnabled;

  Future<void> setRemindersEnabled(bool enabled);

  /// Whether the guided tour with this id has been shown here (ADR-128).
  ///
  /// Device-local for the same reason as [onboardingSeen]: a tour explains
  /// what a screen *is*, which belongs to the install rather than to learning
  /// state. Marked when a tour starts, not when it ends, so a learner who
  /// closed the app half-way through is not cornered by it on every launch.
  bool tourSeen(String id);

  Future<void> markTourSeen(String id);

  /// Every tour shows again — "Replay the tour" in Settings.
  Future<void> resetTours();
}

class _StoredAppPreferences implements AppPreferences {
  _StoredAppPreferences(this._prefs);

  static const _localeKey = 'wordos.ui.locale';
  static const _themeKey = 'wordos.ui.themeMode';
  static const _onboardingKey = 'wordos.ui.onboardingSeen';
  static const _remindersKey = 'wordos.ui.remindersEnabled';
  static const _toursKey = 'wordos.ui.toursSeen';

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

  @override
  bool get onboardingSeen => _prefs.getBool(_onboardingKey) ?? false;

  @override
  Future<void> setOnboardingSeen(bool seen) async {
    try {
      await _prefs.setBool(_onboardingKey, seen);
    } catch (_) {
      // Non-fatal: worst case the tour is offered once more.
    }
  }

  @override
  bool get remindersEnabled => _prefs.getBool(_remindersKey) ?? true;

  @override
  Future<void> setRemindersEnabled(bool enabled) async {
    try {
      await _prefs.setBool(_remindersKey, enabled);
    } catch (_) {
      // Non-fatal: the choice still applies for this app run.
    }
  }

  @override
  bool tourSeen(String id) =>
      _prefs.getStringList(_toursKey)?.contains(id) ?? false;

  @override
  Future<void> markTourSeen(String id) async {
    final seen = _prefs.getStringList(_toursKey) ?? const <String>[];
    if (seen.contains(id)) return;
    try {
      await _prefs.setStringList(_toursKey, [...seen, id]);
    } catch (_) {
      // Non-fatal: worst case the tour is offered once more.
    }
  }

  @override
  Future<void> resetTours() async {
    try {
      await _prefs.remove(_toursKey);
    } catch (_) {
      // Non-fatal.
    }
  }
}

/// Used by tests and as the fallback when the platform store is unavailable.
class InMemoryAppPreferences implements AppPreferences {
  InMemoryAppPreferences({
    Locale? locale,
    ThemeMode? themeMode,
    // Tests start past the tour unless they are testing the tour itself.
    this.onboardingSeen = true,
    this.remindersEnabled = true,
    // Tests start past every guided tour too: an overlay that swallows taps
    // would fail every test that was not written about it.
    bool toursSeen = true,
  })  : _allToursSeen = toursSeen,
        locale = locale ?? AppPreferences.defaultLocale,
        themeMode = themeMode ?? ThemeMode.system;

  @override
  bool onboardingSeen;

  @override
  Future<void> setOnboardingSeen(bool seen) async => onboardingSeen = seen;

  @override
  Locale locale;

  @override
  ThemeMode themeMode;

  @override
  Future<void> setLocale(Locale value) async => locale = value;

  @override
  Future<void> setThemeMode(ThemeMode mode) async => themeMode = mode;

  @override
  bool remindersEnabled;

  @override
  Future<void> setRemindersEnabled(bool enabled) async =>
      remindersEnabled = enabled;

  bool _allToursSeen;
  final Set<String> _toursSeen = {};

  @override
  bool tourSeen(String id) => _allToursSeen || _toursSeen.contains(id);

  @override
  Future<void> markTourSeen(String id) async => _toursSeen.add(id);

  @override
  Future<void> resetTours() async {
    _allToursSeen = false;
    _toursSeen.clear();
  }
}
