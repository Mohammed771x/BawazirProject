import 'dart:async';

import 'package:flutter/material.dart';
import 'package:flutter_localizations/flutter_localizations.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../core/analytics/analytics_tracker.dart';
import '../core/analytics/app_signals.dart';
import '../core/models/models.dart';
import '../core/l10n/app_strings.dart';
import '../core/notifications/reminder_providers.dart';
import '../core/storage/app_preferences.dart';
import '../core/storage/preferences_providers.dart';
import '../core/theme/app_theme.dart';
import '../features/auth/session_controller.dart';
import 'router.dart';

/// The app, and the one place daily reminders are kept up to date (ADR-076).
///
/// Stateful only for that. The reminders the phone holds are a snapshot of what
/// the server believed when they were scheduled, and they go stale the moment
/// the learner practises anything — so they are rewritten whenever there is a
/// reason to think they might be wrong: at sign-in, and every time the app
/// comes back to the foreground.
///
/// Here rather than on the hub because it must also happen when the app is
/// opened *to* somewhere else, and must not happen twice because two screens
/// both thought it was their job.
class WordOsApp extends ConsumerStatefulWidget {
  const WordOsApp({super.key});

  @override
  ConsumerState<WordOsApp> createState() => _WordOsAppState();
}

class _WordOsAppState extends ConsumerState<WordOsApp>
    with WidgetsBindingObserver {
  final List<StreamSubscription<Object?>> _signals = [];

  @override
  void initState() {
    super.initState();
    WidgetsBinding.instance.addObserver(this);

    // What the HTTP layer and the notification plugin noticed, turned into
    // events for the admin area (ADR-125). Only for a signed-in learner: an
    // event needs an account to belong to.
    _signals.add(AppSignals.apiFailures.listen((f) {
      if (!ref.read(sessionProvider).isSignedIn) return;
      ref.read(analyticsTrackerProvider).track(ClientEvents.apiError,
          result: 'error', props: {'code': f.code, 'path': f.path, 'status': f.status});
    }));
    _signals.add(AppSignals.notificationTaps.listen((_) {
      if (!ref.read(sessionProvider).isSignedIn) return;
      ref.read(analyticsTrackerProvider).track(ClientEvents.notificationOpened);
    }));

    // After the first frame: this runs during `build` otherwise, and a provider
    // read that touches the network there is a build that does I/O.
    WidgetsBinding.instance.addPostFrameCallback((_) {
      _refreshReminders();
      _trackForeground();
    });
  }

  @override
  void dispose() {
    for (final s in _signals) {
      s.cancel();
    }
    WidgetsBinding.instance.removeObserver(this);
    super.dispose();
  }

  @override
  void didChangeAppLifecycleState(AppLifecycleState state) {
    if (state == AppLifecycleState.resumed) {
      _refreshReminders();
      _trackForeground();
    }
    // App Time ends when the app leaves the screen (ADR-125).
    if (state == AppLifecycleState.paused &&
        ref.read(sessionProvider).isSignedIn) {
      ref.read(analyticsTrackerProvider).appBackgrounded();
    }
  }

  /// App Time starts — only for a signed-in learner, whose events have an
  /// account to belong to.
  void _trackForeground() {
    if (!mounted || !ref.read(sessionProvider).isSignedIn) return;
    ref.read(analyticsTrackerProvider).appOpened();
  }

  /// Rewrites the pending reminders, if there is anybody to remind.
  ///
  /// Signed out there is nothing to say and nobody to say it to — and asking
  /// the server for a learner's reminders without a learner would be a 401 on
  /// every cold start.
  void _refreshReminders() {
    if (!mounted || !ref.read(sessionProvider).isSignedIn) return;
    // Deliberately not awaited. Nothing on screen depends on it, and the
    // controller swallows its own failures.
    unawaited(ref.read(reminderControllerProvider).refresh());
  }

  @override
  Widget build(BuildContext context) {
    final router = ref.watch(routerProvider);

    // Signing in is the other moment the reminders are certainly wrong: the
    // previous account's are still pending, or there were none at all.
    ref.listen(sessionProvider, (previous, next) {
      if (previous?.isSignedIn != true && next.isSignedIn) _refreshReminders();
      if (previous?.isSignedIn == true && !next.isSignedIn) {
        unawaited(ref.read(notificationSchedulerProvider).cancelAll());
      }
    });

    // Switching the interface language has to rewrite them too. The sentences
    // were composed when they were scheduled, so a learner who switches to
    // English would otherwise keep being reminded in Arabic for a week — long
    // enough to look like the setting did not take.
    ref.listen(localeProvider, (previous, next) {
      if (previous != next) _refreshReminders();
    });

    return MaterialApp.router(
      title: 'WordOS',
      debugShowCheckedModeBanner: false,
      routerConfig: router,
      theme: AppTheme.light(),
      darkTheme: AppTheme.dark(),
      themeMode: ref.watch(themeModeProvider),
      locale: ref.watch(localeProvider),
      supportedLocales: AppPreferences.supportedLocales,
      localizationsDelegates: const [
        GlobalMaterialLocalizations.delegate,
        GlobalWidgetsLocalizations.delegate,
        GlobalCupertinoLocalizations.delegate,
      ],
    );
  }
}
