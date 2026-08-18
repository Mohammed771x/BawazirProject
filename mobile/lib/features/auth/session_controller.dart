import 'dart:async';

import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../core/api/api_providers.dart';
import '../../core/api/wordos_api.dart';
import '../../core/models/models.dart';
import '../../core/l10n/app_strings.dart';
import '../../core/storage/preferences_providers.dart';

class SessionState {
  const SessionState({
    this.user,
    this.restoring = true,
    this.busy = false,
    this.error,
  });

  final UserProfile? user;
  final bool restoring;
  final bool busy;
  final String? error;

  bool get isSignedIn => user != null;

  SessionState copyWith({
    UserProfile? user,
    bool? restoring,
    bool? busy,
    String? error,
    bool clearUser = false,
    bool clearError = false,
  }) =>
      SessionState(
        user: clearUser ? null : (user ?? this.user),
        restoring: restoring ?? this.restoring,
        busy: busy ?? this.busy,
        error: clearError ? null : (error ?? this.error),
      );
}

/// Owns authentication and the cached user profile. Onboarding progress is a
/// server-side fact (`onboardingStage`) that the router simply follows.
class SessionController extends Notifier<SessionState> {
  @override
  SessionState build() {
    Future.microtask(restore);
    return const SessionState();
  }

  WordOsApi get _api => ref.read(wordOsApiProvider);

  Future<void> restore() async {
    final tokens = ref.read(tokenStoreProvider);
    final token = await tokens.restore();

    // A stored refresh token is enough to carry on: the access token may have
    // expired while the app was closed, and the API layer will exchange it on
    // the first 401.
    if (token == null && tokens.refreshToken == null) {
      state = state.copyWith(restoring: false, clearUser: true);
      return;
    }
    try {
      final user = await _api.me();
      state = SessionState(user: user, restoring: false);
    } on ApiException catch (e) {
      // Only a rejected session signs the user out. Being offline at launch
      // must not discard a perfectly good session — the user opens the app on a
      // train and finds themselves logged out otherwise.
      if (e.isUnauthorized || e.isForbidden) {
        await tokens.clear();
        state = const SessionState(restoring: false);
      } else {
        state = SessionState(restoring: false, error: _message(e));
      }
    }
  }

  Future<bool> signIn(String email, String password) =>
      _authenticate(() => _api.login(email: email, password: password));

  Future<bool> signUp(
    String email,
    String password,
    String displayName, {
    String? phoneCountryCode,
    String? phoneNumber,
  }) =>
      _authenticate(() => _api.register(
            email: email,
            password: password,
            displayName: displayName,
            phoneCountryCode: phoneCountryCode,
            phoneNumber: phoneNumber,
          ));

  Future<bool> _authenticate(Future<AuthResponse> Function() call) async {
    state = state.copyWith(busy: true, clearError: true);
    try {
      final result = await call();
      await ref
          .read(tokenStoreProvider)
          .save(result.token, refreshToken: result.refreshToken);
      state = SessionState(user: result.user, restoring: false);
      return true;
    } on ApiException catch (e) {
      state = state.copyWith(
          busy: false, error: _message(e), restoring: false);
      return false;
    }
  }

  /// Applies a profile the server just returned (interests, placement, levels).
  void updateUser(UserProfile user) =>
      state = state.copyWith(user: user, busy: false, clearError: true);

  Future<void> refresh() async {
    try {
      updateUser(await _api.me());
    } on ApiException {
      // Keep the cached profile; the failing screen surfaces the error.
    }
  }

  /// Ends the session on this device, immediately.
  ///
  /// Local first, server second, and deliberately not the other way round.
  /// Awaiting the round-trip meant that a slow, unreachable or simply
  /// unresponsive server left the button looking dead for up to the receive
  /// timeout — the learner taps "sign out" and nothing whatsoever happens.
  ///
  /// Revoking the refresh tokens still matters, so it is still sent; it is
  /// just no longer allowed to hold the learner hostage. If it fails, this
  /// device has already forgotten its tokens, and the refresh token expires
  /// on its own.
  Future<void> signOut() async {
    await ref.read(tokenStoreProvider).clear();

    // The product tour is for a device that has never had an account on it.
    // Someone signing out is trying to reach the login form — usually to come
    // back as somebody else — and putting a slide show in front of them makes
    // them re-answer a question they answered when they installed the app.
    await ref.read(appPreferencesProvider).setOnboardingSeen(true);
    state = const SessionState(restoring: false);

    unawaited(_api.logout().catchError((Object _) {}));
  }

  /// The failure in the learner's language.
  ///
  /// "Wrong email or password" is the app talking to the learner, so it is said
  /// in the language they read the app in; the server's English sentence is the
  /// fallback for a code this app has not met (ADR-035).
  String _message(ApiException e) =>
      ref.read(stringsProvider).apiError(e.code, e.message);

  void clearError() => state = state.copyWith(clearError: true);
}

final sessionProvider =
    NotifierProvider<SessionController, SessionState>(SessionController.new);
