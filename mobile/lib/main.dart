import 'package:flutter/foundation.dart';
import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import 'app/wordos_app.dart';
import 'core/storage/app_preferences.dart';
import 'core/storage/preferences_providers.dart';
import 'core/widgets/app_widgets.dart';

Future<void> main() async {
  WidgetsFlutterBinding.ensureInitialized();

  // ── The last resort ────────────────────────────────────────────────────────
  //
  // Every failure the app can name is caught and said in the learner's language
  // long before it gets here. This is for the ones it cannot: an error thrown
  // inside a `build`, which no `try` around a call will ever see.
  //
  // Flutter's default for that is the red screen in debug and a grey rectangle
  // in release, both of which look to a learner exactly like the app breaking —
  // and the red one prints the exception text, in English, on top of their
  // session.
  //
  // The error still goes to the console, where it belongs; what changes is what
  // the person holding the phone is shown.
  ErrorWidget.builder = (details) {
    if (kDebugMode) return ErrorWidget(details.exception);
    return const AppErrorBox();
  };

  // An error escaping an async gap has no widget to replace, so there is
  // nothing to show — but swallowing it silently would turn a crash into a
  // freeze. Reported, and the app stays up.
  FlutterError.onError = FlutterError.presentError;
  PlatformDispatcher.instance.onError = (error, stack) {
    FlutterError.reportError(
      FlutterErrorDetails(exception: error, stack: stack, library: 'wordos'),
    );
    return true;
  };

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
