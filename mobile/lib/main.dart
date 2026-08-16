import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import 'app/wordos_app.dart';
import 'core/storage/app_preferences.dart';
import 'core/storage/preferences_providers.dart';

Future<void> main() async {
  WidgetsFlutterBinding.ensureInitialized();

  // Preferences are read before the first frame so the app never flashes the
  // wrong language or theme on launch.
  final preferences = await AppPreferences.load();

  runApp(
    ProviderScope(
      overrides: [appPreferencesProvider.overrideWithValue(preferences)],
      child: const WordOsApp(),
    ),
  );
}
