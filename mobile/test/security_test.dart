import 'dart:io';

import 'package:flutter_test/flutter_test.dart';
import 'package:wordos/core/api/api_providers.dart';

/// Guards the security properties that are cheap to regress silently.
/// The full requirement set is `docs/07-SECURITY.md`.
void main() {
  group('secrets never ship in the client', () {
    // Matches assignments of key-shaped things, not the words themselves, so a
    // comment mentioning "API key" does not trip it.
    final secretAssignment = RegExp(
      r'''(api[_-]?key|apikey|secret|access[_-]?token|private[_-]?key|client[_-]?secret)\s*[:=]\s*['"][^'"]{8,}['"]''',
      caseSensitive: false,
    );
    final providerKey = RegExp(r'''['"](sk-|AIza|ghp_|xox[baprs]-)''');

    test('no key-shaped literal exists anywhere in lib/', () {
      final offenders = <String>[];

      for (final entity in Directory('lib').listSync(recursive: true)) {
        if (entity is! File || !entity.path.endsWith('.dart')) continue;
        final source = entity.readAsStringSync();

        // The mock backend holds seeded demo passwords on purpose; it is
        // deleted in Phase 7 and never reachable from a release build.
        final isMock = entity.path.contains('mock_backend');

        for (final line in source.split('\n')) {
          if (line.trimLeft().startsWith('//')) continue;
          if (providerKey.hasMatch(line) ||
              (!isMock && secretAssignment.hasMatch(line))) {
            offenders.add('${entity.path}: ${line.trim()}');
          }
        }
      }

      expect(
        offenders,
        isEmpty,
        reason: 'secrets must live server-side only — see docs/07-SECURITY.md '
            '§1. Offending lines:\n${offenders.join('\n')}',
      );
    });

    test('the environment carries only a base URL and the mock switch', () {
      const env = AppEnvironment.current;

      // If this ever needs a third field, that field must not be a credential.
      expect(env.baseUrl, isNotEmpty);
      expect(env.useMockBackend, isA<bool>());
    });
  });

  group('transport', () {
    test('a cleartext remote base URL is refused', () {
      const env = AppEnvironment(
        useMockBackend: false,
        baseUrl: 'http://api.wordos.app/api',
      );

      expect(env.isSecureTransport, isFalse);
      expect(env.isLoopback, isFalse);
      expect(env.assertTransportIsSafe, throwsA(isA<StateError>()));
    });

    test('https is accepted', () {
      const env = AppEnvironment(
        useMockBackend: false,
        baseUrl: 'https://api.wordos.app/api',
      );

      expect(env.isSecureTransport, isTrue);
      expect(env.assertTransportIsSafe, returnsNormally);
    });

    test('plain http on loopback is allowed for local development', () {
      for (final host in ['localhost', '127.0.0.1', '10.0.2.2']) {
        final env = AppEnvironment(
          useMockBackend: false,
          baseUrl: 'http://$host:5080/api',
        );
        expect(env.assertTransportIsSafe, returnsNormally,
            reason: '$host is a local backend, not a network hop');
      }
    });

    test('a private address is a debug-only exception, and covers the cable',
        () {
      // Testing on real hardware needs *some* way in: a phone cannot reach the
      // Mac's loopback. `169.254` is the address the two give themselves over
      // the USB cable when no router is involved, and it is the narrowest of
      // these ranges — it cannot be routed off the physical link.
      for (final host in [
        '192.168.1.35', '10.0.0.4', '172.20.1.9', '169.254.210.154',
        'macbook.local',
      ]) {
        final env = AppEnvironment(
          useMockBackend: false,
          baseUrl: 'http://$host:5199/api',
        );

        expect(env.isPrivateNetwork, isTrue, reason: '$host is private');
        // The exception is gated on the build being a debug build, which a
        // test run is. A release build sends tokens over TLS or not at all.
        expect(env.assertTransportIsSafe, returnsNormally);
      }

      // And nothing else gets in on the same reasoning.
      for (final host in ['8.8.8.8', '172.32.0.1', 'api.wordos.app']) {
        final env = AppEnvironment(
          useMockBackend: false,
          baseUrl: 'http://$host:5199/api',
        );

        expect(env.isPrivateNetwork, isFalse, reason: '$host is not private');
        expect(env.assertTransportIsSafe, throwsA(isA<StateError>()));
      }
    });

    test('the mock backend bypasses the check, since nothing leaves the device',
        () {
      const env = AppEnvironment(
        useMockBackend: true,
        baseUrl: 'http://anything',
      );
      expect(env.assertTransportIsSafe, returnsNormally);
    });
  });

  group('client-side storage', () {
    test('device preferences hold only presentation state', () {
      // AppPreferences is the *only* sanctioned client persistence besides the
      // token keystore (ADR-010). If it grows a learning-state field, R4 is
      // broken and this test should be the thing that notices.
      final source =
          File('lib/core/storage/app_preferences.dart').readAsStringSync();

      for (final forbidden in [
        'word',
        'skill',
        'level',
        'session',
        'progress',
        'score',
      ]) {
        expect(
          RegExp('String get $forbidden|set$forbidden', caseSensitive: false)
              .hasMatch(source),
          isFalse,
          reason: 'device storage must never hold learning state (rule R4)',
        );
      }
    });
  });
}
