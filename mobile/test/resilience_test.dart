import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:wordos/app/wordos_app.dart';
import 'package:wordos/core/api/api_providers.dart';
import 'package:wordos/core/api/wordos_api.dart';
import 'package:wordos/core/audio/tts_service.dart';
import 'package:wordos/core/models/models.dart';
import 'package:wordos/mock_backend/engine/mock_engine.dart';
import 'package:wordos/mock_backend/mock_wordos_api.dart';

import 'support/test_harness.dart';

/// Failure injection: the app must degrade, never crash or strand the learner
/// (demo review §51–52).
///
/// [failures] maps a method name to how many times it should throw before
/// behaving normally, so a test can simulate "the network dropped once".
class FlakyWordOsApi implements WordOsApi {
  FlakyWordOsApi(this._inner, this.failures);

  final WordOsApi _inner;
  final Map<String, int> failures;

  final List<String> calls = [];

  Future<T> _run<T>(String name, Future<T> Function() call) async {
    calls.add(name);
    final remaining = failures[name] ?? 0;
    if (remaining > 0) {
      failures[name] = remaining - 1;
      throw const ApiException('NETWORK', 'No connection. Check your network.');
    }
    return call();
  }

  @override
  Future<SkillSession> startSession(SkillType skill) =>
      _run('startSession', () => _inner.startSession(skill));

  @override
  Future<AnswerResult> submitAnswer({
    required String sessionId,
    required String itemId,
    required String answer,
    int? timeMs,
  }) =>
      _run(
        'submitAnswer',
        () => _inner.submitAnswer(
          sessionId: sessionId,
          itemId: itemId,
          answer: answer,
          timeMs: timeMs,
        ),
      );

  @override
  Future<SessionResult> completeSession(String sessionId) =>
      _run('completeSession', () => _inner.completeSession(sessionId));

  @override
  Future<List<WordCandidate>> lookupWord(String query) =>
      _run('lookupWord', () => _inner.lookupWord(query));

  @override
  Future<HubState> hub() => _run('hub', () => _inner.hub());

  // Everything else passes straight through.
  @override
  dynamic noSuchMethod(Invocation invocation) =>
      throw UnimplementedError('${invocation.memberName} not stubbed');

  @override
  Future<AuthResponse> register({
    required String email,
    required String password,
    required String displayName,
  }) =>
      _inner.register(
          email: email, password: password, displayName: displayName);

  @override
  Future<AuthResponse> login({
    required String email,
    required String password,
  }) =>
      _inner.login(email: email, password: password);

  @override
  Future<void> logout() => _inner.logout();

  @override
  Future<UserProfile> me() => _inner.me();

  @override
  Future<List<InterestOption>> interestOptions() => _inner.interestOptions();

  @override
  Future<UserProfile> saveInterests(List<String> interests) =>
      _inner.saveInterests(interests);

  @override
  Future<PlacementStep> startPlacement() => _inner.startPlacement();

  @override
  Future<PlacementStep> answerPlacement({
    required String sessionId,
    required String itemId,
    required String answer,
  }) =>
      _inner.answerPlacement(
          sessionId: sessionId, itemId: itemId, answer: answer);

  @override
  Future<PlacementResult> completePlacement(String sessionId) =>
      _inner.completePlacement(sessionId);

  @override
  Future<Word> addWord(WordCandidate candidate) => _inner.addWord(candidate);

  @override
  Future<WordPage> words({WordState? state, int page = 0}) =>
      _inner.words(state: state, page: page);

  @override
  Future<WordDetail> wordDetail(String wordId) => _inner.wordDetail(wordId);

  @override
  Future<WritingEvaluation> submitWriting({
    required String sessionId,
    required String itemId,
    required String sentence,
  }) =>
      _inner.submitWriting(
          sessionId: sessionId, itemId: itemId, sentence: sentence);

  @override
  Future<SpeakingTurn> submitSpeakingTurn({
    required String sessionId,
    required String transcript,
  }) =>
      _inner.submitSpeakingTurn(
          sessionId: sessionId, transcript: transcript);

  @override
  Future<void> abandonSession(String sessionId) =>
      _inner.abandonSession(sessionId);

  @override
  Future<WeeklyReviewSession> startWeeklyReview() => _inner.startWeeklyReview();

  @override
  Future<ReviewAnswerResult> answerWeeklyReview({
    required String reviewId,
    required String itemId,
    required String answer,
  }) =>
      _inner.answerWeeklyReview(
          reviewId: reviewId, itemId: itemId, answer: answer);

  @override
  Future<WeeklyReviewResult> completeWeeklyReview(String reviewId) =>
      _inner.completeWeeklyReview(reviewId);

  @override
  Future<SkillLevel> updateSkillLevel({
    required SkillType skill,
    required CefrLevel level,
  }) =>
      _inner.updateSkillLevel(skill: skill, level: level);

  @override
  Future<SkillLevel> updateDailyTarget({
    required SkillType skill,
    required int target,
  }) =>
      _inner.updateDailyTarget(skill: skill, target: target);

  @override
  Future<PublicConfig> config() => _inner.config();

  @override
  Future<AdminOverview> adminOverview() => _inner.adminOverview();

  @override
  Future<List<AdminUserSummary>> adminUsers() => _inner.adminUsers();

  @override
  Future<AdminUserDetail> adminUserDetail(String userId) =>
      _inner.adminUserDetail(userId);
}

/// A TTS that always fails, standing in for a device with no speech engine.
///
/// The parent's platform calls are already guarded, so the real plugin object
/// it builds is harmless in a test — every method that matters is overridden.
class BrokenTts extends TtsService {
  @override
  Future<bool> speak(String text, {bool slow = false}) async => false;

  @override
  Future<void> stop() async {}

  @override
  void dispose() {}
}

/// Lets the mock backend's artificial latency timers fire before teardown.
Future<void> drain(WidgetTester tester) async {
  await tester.pump(const Duration(seconds: 2));
  await tester.pumpAndSettle();
}

/// A mock backend wired to the harness's fake token store, with the artificial
/// latency turned off so no timer outlives the widget tree.
Override mockApiOverride({Map<String, int> failures = const {}}) =>
    wordOsApiProvider.overrideWith((ref) {
      final api = MockWordOsApi(
        tokenReader: () => ref.watch(tokenStoreProvider).token,
        latencyScale: 0,
      );
      return failures.isEmpty
          ? api
          : FlakyWordOsApi(api, Map<String, int>.from(failures));
    });

void main() {
  group('engine-level failures', () {
    late MockEngine engine;
    late MockUser user;

    setUp(() {
      engine = MockEngine();
      user = engine
          .requireUser(engine.login('demo@wordos.app', 'wordos123').token);
    });

    test('starting a session with nothing due is a typed refusal, not a crash',
        () {
      // Spelling has no due words for the seeded learner.
      expect(
        () => engine.startSession(user, SkillType.spelling),
        throwsA(isA<ApiException>()
            .having((e) => e.code, 'code', 'NO_WORDS_DUE')
            .having((e) => e.statusCode, 'status', 409)),
      );
    });

    test('an abandoned session cannot be answered or completed afterwards', () {
      final session = engine.startSession(user, SkillType.reading);
      engine.abandonSession(user, session.id);

      expect(
        () => engine.completeSession(user, session.id),
        throwsA(isA<ApiException>()
            .having((e) => e.code, 'code', 'SESSION_NOT_FOUND')),
      );
    });

    test('one learner cannot touch another learner\'s session', () {
      final session = engine.startSession(user, SkillType.reading);
      final other = engine
          .requireUser(engine.login('sara@wordos.app', 'wordos123').token);

      expect(
        () => engine.completeSession(other, session.id),
        throwsA(isA<ApiException>()
            .having((e) => e.code, 'code', 'SESSION_NOT_FOUND')),
        reason: 'a foreign id must look missing, not forbidden',
      );
    });

    test('an unknown token is rejected', () {
      expect(
        () => engine.requireUser('not-a-real-token'),
        throwsA(isA<ApiException>()
            .having((e) => e.statusCode, 'status', 401)),
      );
    });

    test('an empty or whitespace lookup returns nothing rather than throwing',
        () {
      expect(engine.lookup(''), isEmpty);
      expect(engine.lookup('    '), isEmpty);
    });

    test('a very long query is handled without error', () {
      expect(() => engine.lookup('a' * 5000), returnsNormally);
    });
  });

  group('session UI recovers from a dropped connection', () {
    testWidgets('a failed session start shows a retry that works',
        (tester) async {
      await tester.pumpWidget(
        ProviderScope(
          // Fail the first session start, then succeed.
          overrides: [
            ...testOverrides(),
            mockApiOverride(failures: {'startSession': 1}),
          ],
          child: const WordOsApp(),
        ),
      );
      await tester.pumpAndSettle();
      await tester.tap(find.widgetWithText(FilledButton, 'Sign in'));
      await tester.pumpAndSettle();

      await tester.tap(find.text('Reading'));
      await tester.pumpAndSettle();

      // The failure is shown as a recoverable error, not a crash or a blank
      // screen.
      expect(find.textContaining('No connection'), findsOneWidget);
      expect(find.widgetWithText(OutlinedButton, 'Try again'), findsOneWidget);
      expect(tester.takeException(), isNull);

      await tester.tap(find.widgetWithText(OutlinedButton, 'Try again'));
      await tester.pumpAndSettle();

      // Second attempt succeeds and the session runs.
      expect(find.text('Read the passage'), findsOneWidget);

      await drain(tester);
    });

    testWidgets('a failed answer keeps the session alive and retryable',
        (tester) async {
      await tester.pumpWidget(
        ProviderScope(
          overrides: [
            ...testOverrides(),
            mockApiOverride(failures: {'submitAnswer': 1}),
          ],
          child: const WordOsApp(),
        ),
      );
      await tester.pumpAndSettle();
      await tester.tap(find.widgetWithText(FilledButton, 'Sign in'));
      await tester.pumpAndSettle();

      await tester.tap(find.text('Reading'));
      await tester.pumpAndSettle();
      await tester.tap(find.widgetWithText(FilledButton, 'I finished reading'));
      await tester.pumpAndSettle();

      // First answer fails.
      await tester.tap(find.byType(InkWell).first, warnIfMissed: false);
      await tester.pumpAndSettle();
      expect(tester.takeException(), isNull);

      // The question is still on screen and can be answered again.
      expect(find.textContaining('Question'), findsOneWidget);

      await tester.tap(find.byType(InkWell).first, warnIfMissed: false);
      await tester.pumpAndSettle();

      // The retry landed: the session moved on to showing feedback.
      expect(find.widgetWithText(FilledButton, 'Next'), findsOneWidget);

      await drain(tester);
    });
  });

  group('audio failure', () {
    testWidgets('listening falls back to the transcript when TTS is dead',
        (tester) async {
      tester.view.physicalSize = const Size(1200, 2600);
      tester.view.devicePixelRatio = 3;
      addTearDown(tester.view.reset);

      await tester.pumpWidget(
        ProviderScope(
          overrides: [
            ...testOverrides(),
            mockApiOverride(),
            ttsServiceProvider.overrideWithValue(BrokenTts()),
          ],
          child: const WordOsApp(),
        ),
      );
      await tester.pumpAndSettle();
      await tester.tap(find.widgetWithText(FilledButton, 'Sign in'));
      await tester.pumpAndSettle();

      await tester.tap(find.text('Listening'));
      await tester.pumpAndSettle();

      // Nothing crashed, and the play control is present.
      expect(tester.takeException(), isNull);

      await tester.tap(find.byIcon(Icons.play_arrow_rounded).first);
      await tester.pumpAndSettle();

      expect(find.textContaining('Audio is unavailable'), findsOneWidget,
          reason: 'a silent device must not trap the learner');

      await drain(tester);
    });
  });
}
