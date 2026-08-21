import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:wordos/core/api/api_providers.dart';
import 'package:wordos/core/api/wordos_api.dart';
import 'package:wordos/core/l10n/app_strings.dart';
import 'package:wordos/features/auth/session_controller.dart';

import 'support/test_harness.dart';

/// What the learner is shown when something goes wrong.
///
/// The rule this file exists to hold: **a learner never reads an English
/// sentence the server wrote, and never reads a Dart error at all.** The API
/// answers with a stable `code` and an English fallback; the code is the
/// contract and the fallback is for a version of the app older than the API
/// (ADR-035).
///
/// The list below is that contract, written out. When the backend gains a code
/// it goes here too — and if it does not, this suite says so rather than the
/// learner discovering it as English text mid-session.
void main() {
  const en = AppStrings(Locale('en'));
  const ar = AppStrings(Locale('ar'));

  /// Every code the API can answer with, taken from the `Problems.*` calls in
  /// `backend/src/WordOs.Api/Endpoints/` plus the transport codes the client
  /// raises for itself in `http_wordos_api.dart`.
  const codes = <String>[
    // Getting in.
    'INVALID_CREDENTIALS', 'EMAIL_TAKEN', 'INVALID_PHONE',
    'INVALID_REFRESH', 'REFRESH_REUSED', 'UNAUTHORIZED', 'FORBIDDEN',
    // Words.
    'WORD_ALREADY_ADDED', 'WORD_NOT_FOUND', 'QUERY_TOO_LONG', 'BAD_WORD',
    // Sessions.
    'NO_WORDS_DUE', 'SESSION_COMPLETE', 'SESSION_NOT_FOUND',
    'SESSION_STARTED', 'SESSION_STARTING', 'SESSION_RACE',
    'ITEM_NOT_CURRENT', 'ITEM_NOT_FOUND', 'EMPTY_ANSWER', 'EMPTY_TURN',
    'RELEVEL_UNAVAILABLE', 'NO_CONTENT', 'NO_WARMUP', 'WRONG_SKILL',
    'WRONG_ITEM_TYPE', 'LEVEL_NOT_ADJUSTABLE', 'SKILL_NOT_LEVELLED',
    // Onboarding, placement and review.
    'NO_INTERESTS', 'TOO_MANY_INTERESTS', 'PLACEMENT_NOT_FOUND',
    'PLACEMENT_COMPLETE', 'PLACEMENT_INCOMPLETE', 'REVIEW_COMPLETE',
    'REVIEW_NOT_FOUND', 'NO_WORDS_IN_PERIOD', 'EMPTY_FEEDBACK',
    // Overload, which is not failure (ADR-051).
    'SERVER_BUSY', 'AI_BUSY',
    // Faults.
    'INTERNAL_ERROR', 'SERVER_ERROR', 'BAD_RESPONSE', 'UNEXPECTED',
    'INVALID_PARAMETER', 'INVALID_SKILL', 'INVALID_LEVEL', 'INVALID_STATE',
    'INVALID_STATUS', 'VALIDATION_FAILED', 'NOT_FOUND', 'USER_NOT_FOUND',
    'CONFLICT', 'UNKNOWN',
    // Transport.
    'NETWORK', 'TIMEOUT', 'INSECURE_CONNECTION', 'RATE_LIMITED',
  ];

  /// The one code with a deliberately empty message: a cancelled request is the
  /// app tidying up after a screen the learner already left, and there is
  /// nobody to tell.
  const silent = {'CANCELLED'};

  group('every failure has words of its own', () {
    for (final code in codes) {
      test('$code is said in both languages', () {
        const serverSentence = 'A server sentence in English.';

        final english = en.apiError(code, serverSentence);
        final arabic = ar.apiError(code, serverSentence);

        expect(english, isNotEmpty, reason: '$code has no English message');
        expect(arabic, isNotEmpty, reason: '$code has no Arabic message');

        // The heart of it: the code was recognised, so neither language fell
        // through to the sentence the server wrote.
        expect(arabic, isNot(serverSentence),
            reason: '$code falls through — an Arabic learner reads English');
        expect(english, isNot(serverSentence),
            reason: '$code is not localized at all');

        // And the Arabic really is Arabic, not the English string copied over.
        expect(RegExp(r'[؀-ۿ]').hasMatch(arabic), isTrue,
            reason: '$code has no Arabic letters in its Arabic message');
      });
    }

    test('a cancelled request says nothing, on purpose', () {
      for (final code in silent) {
        expect(en.apiError(code, 'ignored'), isEmpty);
        expect(ar.apiError(code, 'ignored'), isEmpty);
      }
    });

    test('a code from a newer API falls back to the server sentence', () {
      const future = 'SOMETHING_ADDED_AFTER_THIS_BUILD';
      expect(ar.apiError(future, 'The server explains itself.'),
          'The server explains itself.');
    });

    test('an unknown code with no sentence still says something', () {
      // A blank fallback would otherwise render a dialog with no text in it.
      expect(ar.apiError('MYSTERY', ''), ar.somethingWentWrong);
      expect(ar.apiError('MYSTERY', '   '), ar.somethingWentWrong);
    });
  });

  group('nothing reaches the learner untyped', () {
    test('an ApiException passes through as itself', () {
      const original = ApiException('NO_WORDS_DUE', 'Nothing due.');
      expect(identical(ApiException.from(original), original), isTrue);
    });

    test('a Dart error becomes UNEXPECTED, and loses its text', () {
      // The shape of a real one: `fromJson` meeting a field it did not expect.
      final parseFailure = TypeError();
      final converted = ApiException.from(parseFailure);

      expect(converted.code, 'UNEXPECTED');
      expect(converted.message, isNot(contains('TypeError')));
      // Whatever it was, the learner reads this.
      expect(ar.apiError(converted.code, converted.message),
          ar.somethingWentWrong);
    });

    test('a thrown string does not become the message', () {
      final converted = ApiException.from('internal detail: table users');
      expect(converted.message, isNot(contains('users')));
    });
  });

  group('retry is offered only where it could work', () {
    test('being offline, slow, overloaded or rate-limited is retryable', () {
      for (final e in [
        const ApiException('NETWORK', ''),
        const ApiException('TIMEOUT', ''),
        const ApiException('SERVER_BUSY', '', statusCode: 503),
        const ApiException('AI_BUSY', '', statusCode: 503),
        const ApiException('RATE_LIMITED', '', statusCode: 429),
        const ApiException('SESSION_STARTING', '', statusCode: 409),
        const ApiException('UNEXPECTED', ''),
      ]) {
        expect(e.isRetryable, isTrue, reason: '${e.code} should offer retry');
      }
    });

    test('a settled refusal is not', () {
      // Pressing again returns the same answer for ever, and a button that
      // cannot help reads as an app that is broken.
      for (final e in [
        const ApiException('SESSION_COMPLETE', '', statusCode: 409),
        const ApiException('NO_WORDS_DUE', '', statusCode: 409),
        const ApiException('ITEM_NOT_CURRENT', '', statusCode: 409),
        const ApiException('WORD_ALREADY_ADDED', '', statusCode: 409),
        const ApiException('INVALID_CREDENTIALS', '', statusCode: 401),
        const ApiException('FORBIDDEN', '', statusCode: 403),
      ]) {
        expect(e.isRetryable, isFalse, reason: '${e.code} should not');
      }
    });
  });

  group('the wording matches what the learner can do about it', () {
    test('offline and slow are told apart', () {
      // Collapsing both into one message sends people to restart a router that
      // was never the problem.
      expect(ar.apiError('NETWORK', ''), isNot(ar.apiError('TIMEOUT', '')));
      expect(en.apiError('NETWORK', '').toLowerCase(), contains('connection'));
      expect(en.apiError('TIMEOUT', '').toLowerCase(), contains('slow'));
    });

    test('overload does not apologise as though the work were lost', () {
      // A 503 means the request was never attempted; the learner's session is
      // untouched (ADR-051). "Something went wrong" would suggest otherwise.
      for (final code in ['SERVER_BUSY', 'AI_BUSY']) {
        expect(en.apiError(code, '').toLowerCase(), contains('try again'));
        expect(en.apiError(code, ''), isNot(en.somethingWentWrong));
      }
    });

    test('an ended session says to sign in, whichever way it ended', () {
      // Expired, revoked elsewhere, or a replayed token that revoked the
      // family — the learner did nothing wrong and needs one instruction.
      final expected = ar.apiError('UNAUTHORIZED', '');
      expect(ar.apiError('INVALID_REFRESH', ''), expected);
      expect(ar.apiError('REFRESH_REUSED', ''), expected);
      expect(en.apiError('REFRESH_REUSED', '').toLowerCase(),
          contains('sign in'));
    });

    test('a fault says nothing about what broke', () {
      // The detail belongs in the server's log, where an attacker cannot read
      // it (docs/07-SECURITY.md §8).
      final message = en.apiError('INTERNAL_ERROR', '').toLowerCase();
      for (final leak in ['sql', 'exception', 'null', 'stack', 'database']) {
        expect(message, isNot(contains(leak)));
      }
    });
  });

  group('a session that ends underneath the learner', () {
    testWidgets('signs them out and says why, instead of stranding them',
        (tester) async {
      await bootAndSignIn(tester);
      final container = ProviderScope.containerOf(
        tester.element(find.byType(Scaffold).first),
      );
      expect(container.read(sessionProvider).isSignedIn, isTrue);

      // What the API layer does when a refresh is refused. It cannot end the
      // session itself — it is what the session controller is built from — so
      // it raises this and the controller acts on it.
      container.read(sessionExpiredProvider).value++;
      await tester.pumpAndSettle();

      final session = container.read(sessionProvider);
      expect(session.isSignedIn, isFalse,
          reason: 'the router keeps the learner on a dead screen otherwise');
      expect(session.error, isNotNull,
          reason: 'being thrown out with no explanation reads as a crash');
      expect(session.error, contains('sign in'));

      // And they are actually taken there, with the reason on screen.
      expect(find.text('Sign in'), findsWidgets);
      expect(find.textContaining('session has ended'), findsOneWidget);
    });

    testWidgets('does nothing when nobody was signed in', (tester) async {
      await bootApp(tester);
      await tester.pumpAndSettle();
      final container = ProviderScope.containerOf(
        tester.element(find.byType(Scaffold).first),
      );

      container.read(sessionExpiredProvider).value++;
      await tester.pumpAndSettle();

      // No error banner for a session nobody had.
      expect(container.read(sessionProvider).error, isNull);
    });
  });

  group('the form refuses what the API would refuse', () {
    test('the password minimum matches the server rule', () {
      // `AuthEndpoints.RegisterRequest` carries `MinLength(8)`. The form said
      // six, so a seven-character password was accepted here, refused there,
      // and came back naming no field.
      expect(AppStrings.minPasswordLength, 8);
      expect(en.passwordRequired, contains('8'));
      expect(ar.passwordRequired, contains('8'));
    });
  });
}
