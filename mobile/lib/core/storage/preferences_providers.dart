import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import 'app_preferences.dart';

/// Overridden in `main()` with the store loaded from disk, and in tests with
/// [InMemoryAppPreferences]. Reading it without an override would mean the UI
/// silently ran on throwaway preferences, so it fails loudly instead.
final appPreferencesProvider = Provider<AppPreferences>(
  (ref) => throw UnimplementedError(
    'appPreferencesProvider must be overridden with a loaded AppPreferences.',
  ),
);

/// User-selectable theme mode, persisted across launches.
class ThemeModeController extends StateNotifier<ThemeMode> {
  ThemeModeController(this._prefs) : super(_prefs.themeMode);

  final AppPreferences _prefs;

  Future<void> setThemeMode(ThemeMode mode) async {
    if (mode == state) return;
    state = mode;
    await _prefs.setThemeMode(mode);
  }
}

final themeModeProvider = StateNotifierProvider<ThemeModeController, ThemeMode>(
  (ref) => ThemeModeController(ref.watch(appPreferencesProvider)),
);
