import 'package:flutter_secure_storage/flutter_secure_storage.dart';

/// Holds the session tokens in memory and mirrors them to the platform keystore
/// so the user stays signed in across app launches.
///
/// Two tokens, with different lifetimes and different jobs:
///
/// * the **access token** is short-lived and sent on every request;
/// * the **refresh token** is long-lived, sent only to `/auth/refresh`, and
///   rotated on each use — the server revokes the whole family if an already
///   used one is ever presented, so a stolen token is worth one call at most.
///
/// Both live in the keystore and nowhere else: never in preferences, never in
/// a log, never in a URL. The API implementations read them through callbacks,
/// which keeps session ownership out of the networking layer.
class TokenStore {
  TokenStore({FlutterSecureStorage? storage})
      : _storage = storage ?? const FlutterSecureStorage();

  static const _accessKey = 'wordos.session.token';
  static const _refreshKey = 'wordos.session.refresh';

  final FlutterSecureStorage _storage;
  String? _token;
  String? _refreshToken;

  String? get token => _token;

  String? get refreshToken => _refreshToken;

  Future<String?> restore() async {
    try {
      // A wedged keystore must never leave the app stuck on the splash screen.
      _token = await _read(_accessKey);
      _refreshToken = await _read(_refreshKey);
    } catch (_) {
      // Keystore unavailable (e.g. unsigned desktop build) — stay signed out.
      _token = null;
      _refreshToken = null;
    }
    return _token;
  }

  Future<String?> _read(String key) => _storage
      .read(key: key)
      .timeout(const Duration(seconds: 3), onTimeout: () => null);

  Future<void> save(String token, {String? refreshToken}) async {
    _token = token;
    if (refreshToken != null) _refreshToken = refreshToken;
    try {
      await _storage.write(key: _accessKey, value: token);
      if (refreshToken != null) {
        await _storage.write(key: _refreshKey, value: refreshToken);
      }
    } catch (_) {
      // Non-fatal: the session still works for this app run.
    }
  }

  Future<void> clear() async {
    _token = null;
    _refreshToken = null;
    try {
      await _storage.delete(key: _accessKey);
      await _storage.delete(key: _refreshKey);
    } catch (_) {
      // Ignore.
    }
  }
}
