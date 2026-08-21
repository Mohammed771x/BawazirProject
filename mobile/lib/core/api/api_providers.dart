import 'package:flutter/foundation.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../mock_backend/mock_wordos_api.dart';
import '../storage/preferences_providers.dart';
import '../storage/token_store.dart';
import 'http_wordos_api.dart';
import 'wordos_api.dart';

/// Which backend the app talks to.
///
/// Phase 7 flips [useMockBackend] to `false` (or passes `--dart-define`) and
/// nothing else in the app changes — that is the whole point of the
/// [WordOsApi] abstraction.
class AppEnvironment {
  const AppEnvironment({required this.useMockBackend, required this.baseUrl});

  final bool useMockBackend;
  final String baseUrl;

  /// No secrets are read here, and none ever may be.
  ///
  /// A `--dart-define` is **not** a secret store: its values are embedded in the
  /// compiled binary and can be recovered from an installed app. Only the base
  /// URL and the mock switch live here. Anything the app needs from an AI or
  /// dictionary provider is fetched *through our backend*, which holds the
  /// credentials server-side (`docs/07-SECURITY.md` §1).
  static const AppEnvironment current = AppEnvironment(
    useMockBackend: bool.fromEnvironment('WORDOS_MOCK', defaultValue: true),
    baseUrl: String.fromEnvironment(
      'WORDOS_API_BASE_URL',
      defaultValue: 'http://localhost:5080/api',
    ),
  );

  /// The build, for attaching to a bug report (ADR-053).
  ///
  /// A constant rather than a package lookup: this is stamped at build time and
  /// a report should never fail to send because reading the bundle failed.
  static const String version =
      String.fromEnvironment('WORDOS_VERSION', defaultValue: 'dev');

  /// Which platform the report came from — iOS, Android, web.
  static String get platformName {
    if (kIsWeb) return 'web';
    return defaultTargetPlatform.name;
  }

  bool get isSecureTransport =>
      Uri.tryParse(baseUrl)?.scheme.toLowerCase() == 'https';

  /// Localhost over plain HTTP is fine while developing against a local
  /// backend; anything else must be TLS.
  bool get isLoopback {
    final host = Uri.tryParse(baseUrl)?.host.toLowerCase() ?? '';
    return host == 'localhost' || host == '127.0.0.1' || host == '10.0.2.2';
  }

  /// A private-network address — `192.168.x.x`, `10.x.x.x`, `172.16–31.x.x`,
  /// `169.254.x.x`, or a `.local` name.
  ///
  /// Used **only** to allow a debug build on a physical device to reach a
  /// backend running on the developer's own Mac. A phone cannot reach the Mac's
  /// loopback address, so testing on real hardware would otherwise require
  /// either TLS on a development machine or turning the check off.
  ///
  /// `169.254/16` is link-local: the address a phone and a Mac give themselves
  /// over the USB cable when there is no router between them. It is included
  /// because it is the *narrowest* of these ranges — by definition it cannot be
  /// routed off the physical link, so it reaches less far than a home network
  /// does — and because a cable is the one path that works when the phone and
  /// the Mac are not on the same Wi-Fi.
  bool get isPrivateNetwork {
    final host = Uri.tryParse(baseUrl)?.host.toLowerCase() ?? '';
    if (host.endsWith('.local')) return true;

    final octets = host.split('.');
    if (octets.length != 4) return false;
    final parts = octets.map(int.tryParse).toList();
    if (parts.any((p) => p == null)) return false;

    return switch (parts) {
      [10, _, _, _] => true,
      [192, 168, _, _] => true,
      [172, final second?, _, _] when second >= 16 && second <= 31 => true,
      [169, 254, _, _] => true,
      _ => false,
    };
  }

  /// Fails fast rather than silently sending bearer tokens over cleartext.
  ///
  /// The private-network exception is gated on [kDebugMode] on purpose: a
  /// release build sends tokens over TLS or not at all, whatever address it is
  /// pointed at. Nothing here is relaxed by configuration — a build has to be a
  /// debug build, and the host has to be on a private network
  /// (`docs/07-SECURITY.md` §2).
  void assertTransportIsSafe() {
    if (useMockBackend || isSecureTransport || isLoopback) return;
    if (kDebugMode && isPrivateNetwork) return;

    throw StateError(
      'WORDOS_API_BASE_URL must use https (got: $baseUrl). '
      'Bearer tokens must never travel over cleartext.',
    );
  }
}

final appEnvironmentProvider =
    Provider<AppEnvironment>((ref) => AppEnvironment.current);

final tokenStoreProvider = Provider<TokenStore>((ref) => TokenStore());

final wordOsApiProvider = Provider<WordOsApi>((ref) {
  final env = ref.watch(appEnvironmentProvider);
  final tokens = ref.watch(tokenStoreProvider);
  if (env.useMockBackend) {
    return MockWordOsApi(tokenReader: () => tokens.token);
  }
  env.assertTransportIsSafe();
  return HttpWordOsApi(
    baseUrl: env.baseUrl,
    tokenReader: () => tokens.token,
    refreshTokenReader: () => tokens.refreshToken,
    // Read at call time, not captured: a learner who changes the language in
    // Settings gets their next feedback in the new one.
    languageReader: () => ref.read(appPreferencesProvider).locale.languageCode,
    // The rotated pair is persisted here, in the store that owns it — the
    // networking layer never touches the keystore.
    onRefreshed: (token, refresh) => tokens.save(token, refreshToken: refresh),
    // Reached only when the refresh token is gone too. Dropping both stops a
    // dead token being replayed on every subsequent request.
    onUnauthorized: tokens.clear,
  );
});

/// Non-null only while the mock backend is in use — powers the developer
/// "time travel" control that demonstrates the multi-day skill gaps.
final mockApiProvider = Provider<MockWordOsApi?>((ref) {
  final api = ref.watch(wordOsApiProvider);
  return api is MockWordOsApi ? api : null;
});
