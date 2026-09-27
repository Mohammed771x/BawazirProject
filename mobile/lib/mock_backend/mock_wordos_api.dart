import '../core/api/wordos_api.dart';
import '../core/models/models.dart';
import 'engine/mock_engine.dart';

/// ⚠️ DISPOSABLE — the development stand-in for the ASP.NET Core backend.
///
/// It implements the *exact* contract in `docs/05-API-CONTRACT.md`, adds a
/// small artificial latency so loading states are real, and delegates every
/// rule to [MockEngine]. Deleted in Phase 7.
class MockWordOsApi implements WordOsApi {
  MockWordOsApi({
    required this.tokenReader,
    MockEngine? engine,
    this.latencyScale = 1.0,
    this.onChanged,
  }) : engine = engine ?? MockEngine();

  final MockEngine engine;
  final String? Function() tokenReader;

  /// Called after every call that changed something (ADR-094).
  final void Function()? onChanged;

  /// Multiplier on the artificial latency. Tests pass `0` so no timer outlives
  /// the widget tree — a fire-and-forget call in `dispose` would otherwise be
  /// pending at teardown.
  final double latencyScale;

  static const Duration _latency = Duration(milliseconds: 320);
  static const Duration _aiLatency = Duration(milliseconds: 900);

  /// A call that changes something, and says so afterwards.
  ///
  /// The real client announces these from its HTTP verbs, where the
  /// classification is free (ADR-094). A stand-in has no verbs, so the same
  /// list lives here — every method that is a POST, PUT, PATCH or DELETE in
  /// `docs/05-API-CONTRACT.md` goes through this instead of [_delay].
  ///
  /// It matters that the mock mirrors it: every widget test in the suite runs
  /// against this class, so a screen that failed to refresh would pass its
  /// tests here and fail on a real phone.
  ///
  /// Only on success — a write that threw changed nothing.
  Future<T> _write<T>(T Function() body, [Duration? duration]) async {
    final result = await _delay(body, duration);
    onChanged?.call();
    return result;
  }

  Future<T> _delay<T>(T Function() body, [Duration? duration]) async {
    final base = duration ?? _latency;
    final scaled = Duration(
      microseconds: (base.inMicroseconds * latencyScale).round(),
    );
    if (scaled > Duration.zero) await Future<void>.delayed(scaled);
    return body();
  }

  MockUser get _user => engine.requireUser(tokenReader());

  @override
  Future<AuthResponse> register({
    required String email,
    required String password,
    required String displayName,
    String? phoneCountryCode,
    String? phoneNumber,
  }) =>
      _write(() => engine.register(
            email,
            password,
            displayName,
            phoneCountryCode: phoneCountryCode,
            phoneNumber: phoneNumber,
          ));

  @override
  Future<AuthResponse> login({
    required String email,
    required String password,
  }) =>
      _write(() => engine.login(email, password));

  @override
  Future<void> logout() => _write(() => engine.logout(tokenReader()));

  @override
  Future<void> requestPasswordReset(String email) =>
      _write(() => engine.requestPasswordReset(email));

  @override
  Future<void> resetPassword({
    required String email,
    required String code,
    required String newPassword,
  }) =>
      _write(() => engine.resetPassword(
            email: email,
            code: code,
            newPassword: newPassword,
          ));

  @override
  Future<UserProfile> me() => _delay(() => engine.profile(_user));

  @override
  Future<List<InterestOption>> interestOptions() =>
      _delay(() => MockEngine.interestOptions);

  @override
  Future<UserProfile> saveInterests(List<String> interests) =>
      _write(() => engine.saveInterests(_user, interests));

  @override
  Future<PlacementStep> startPlacement() =>
      _write(() => engine.startPlacement(_user), _aiLatency);

  @override
  Future<PlacementStep> answerPlacement({
    required String sessionId,
    required String itemId,
    required String answer,
  }) =>
      _write(
        () => engine.answerPlacement(_user, sessionId, itemId, answer),
        const Duration(milliseconds: 380),
      );

  @override
  Future<PlacementResult> completePlacement(String sessionId) =>
      _write(() => engine.completePlacement(_user, sessionId), _aiLatency);

  @override
  Future<HubState> hub() => _delay(() => engine.hub(_user));

  @override
  Future<List<WordCandidate>> lookupWord(String query) =>
      _delay(() => engine.lookup(query), const Duration(milliseconds: 220));

  @override
  Future<WordDefinition> defineWord(String word) =>
      _delay(() => engine.define(word), const Duration(milliseconds: 180));

  @override
  Future<Word> addWord(WordCandidate candidate) =>
      _write(() => engine.addWord(_user, candidate), _aiLatency);

  @override
  Future<Word> addWordWithMeaning({
    required String text,
    required String meaning,
    bool acceptAnyway = false,
  }) =>
      _write(
        () => engine.addWordWithMeaning(
          _user,
          text: text,
          meaning: meaning,
          acceptAnyway: acceptAnyway,
        ),
        _aiLatency,
      );

  @override
  Future<Word> addWordFromPassage({
    required String sessionId,
    required String word,
  }) =>
      _write(
        () => engine.addWordFromPassage(_user, sessionId: sessionId, word: word),
        _aiLatency,
      );

  @override
  Future<Word> changeWordMeaning({
    required String wordId,
    required String meaning,
    bool acceptAnyway = false,
  }) =>
      _write(() => engine.changeWordMeaning(
            _user,
            wordId,
            meaning: meaning,
            acceptAnyway: acceptAnyway,
          ));

  @override
  Future<Word> replaceWord({
    required String wordId,
    required String meaning,
    required String senseId,
  }) =>
      _write(() => engine.replaceWord(_user, wordId, senseId: senseId));

  @override
  Future<void> deleteWord(String wordId) =>
      _write(() => engine.deleteWord(_user, wordId));

  @override
  Future<List<DailyReminder>> dailyReminders() =>
      _delay(() => engine.dailyReminders(_user));

  @override
  Future<WordPage> words({WordState? state, int page = 0, String? query}) =>
      _delay(() => engine.words(_user, state, query: query));

  @override
  Future<WordDetail> wordDetail(String wordId) =>
      _delay(() => engine.wordDetail(_user, wordId));

  @override
  Future<SkillSession> startSession(SkillType skill, {bool practice = false}) =>
      _write(
          () => engine.startSession(_user, skill, practice: practice),
          _aiLatency);

  @override
  Future<SkillSession> changeSessionLevel(String sessionId, CefrLevel level) =>
      _write(() => engine.changeSessionLevel(_user, sessionId, level),
          _aiLatency);

  @override
  Future<WarmupResult> answerWarmup({
    required String sessionId,
    required String wordId,
    required String answer,
  }) =>
      _write(() => engine.answerWarmup(_user, sessionId, wordId, answer));

  @override
  Future<SkillSession> resumeSession(String sessionId) =>
      _delay(() => engine.resumeSession(_user, sessionId));

  @override
  Future<AnswerResult> submitAnswer({
    required String sessionId,
    required String itemId,
    required String answer,
    int? timeMs,
  }) =>
      _write(
        () => engine.submitAnswer(_user, sessionId, itemId, answer),
        const Duration(milliseconds: 180),
      );

  @override
  Future<WritingEvaluation> submitWriting({
    required String sessionId,
    required String itemId,
    required String sentence,
  }) =>
      _write(
        () => engine.submitWriting(_user, sessionId, itemId, sentence),
        _aiLatency,
      );

  @override
  Future<SpeakingTurn> submitSpeakingTurn({
    required String sessionId,
    required String transcript,
  }) =>
      _write(
        () => engine.submitSpeakingTurn(_user, sessionId, transcript),
        _aiLatency,
      );

  @override
  Future<SessionResult> completeSession(String sessionId) =>
      _write(() => engine.completeSession(_user, sessionId));

  @override
  Future<void> abandonSession(String sessionId) =>
      _write(() => engine.abandonSession(_user, sessionId));

  @override
  Future<WeeklyReviewSession> startWeeklyReview() =>
      _write(() => engine.startWeeklyReview(_user), _aiLatency);

  @override
  Future<ReviewAnswerResult> answerWeeklyReview({
    required String reviewId,
    required String itemId,
    required String answer,
  }) =>
      _write(
        () => engine.answerWeeklyReview(_user, reviewId, itemId, answer),
        const Duration(milliseconds: 180),
      );

  @override
  Future<WeeklyReviewResult> completeWeeklyReview(String reviewId) =>
      _write(() => engine.completeWeeklyReview(_user, reviewId));

  @override
  Future<SkillLevel> updateSkillLevel({
    required SkillType skill,
    required CefrLevel level,
  }) =>
      _write(() => engine.updateSkillLevel(_user, skill, level));

  @override
  Future<SkillLevel> updateDailyTarget({
    required SkillType skill,
    required int target,
  }) =>
      _write(() => engine.updateDailyTarget(_user, skill, target));

  @override
  Future<PublicConfig> config() => _delay(() => MockEngine.configuration);

  // The role check happens inside the engine, not here — see [MockAdmin].

  @override
  Future<AdminOverview> adminOverview({int? days}) =>
      _delay(() => engine.adminOverview(_user, days: days));

  @override
  Future<AdminUserPage> adminUsers({String? query, int? days, int page = 0}) =>
      _delay(() => engine.adminUsers(_user, query: query, days: days));

  @override
  Future<AdminWordPage> adminUserWords(
    String userId, {
    WordState? state,
    String? query,
    int page = 0,
  }) =>
      _delay(() =>
          engine.adminUserWords(_user, userId, state: state, query: query));

  @override
  Future<AdminWordJourney> adminWordJourney(String wordId) =>
      _delay(() => engine.adminWordJourney(_user, wordId));

  @override
  Future<PlacementEvidence> adminPlacementEvidence(String userId) =>
      _delay(() => engine.adminPlacementEvidence(_user, userId));

  @override
  Future<AdminUserDetail> adminUserDetail(String userId) =>
      _delay(() => engine.adminUserDetail(_user, userId));

  @override
  Future<ScheduleAdvance> adminAdvanceSchedule(String userId, {int days = 2}) =>
      _write(() => engine.adminAdvanceSchedule(_user, userId, days: days));

  @override
  Future<void> sendFeedback(String body) =>
      _write(() => engine.sendFeedback(_user, body));

  @override
  Future<FeedbackPage> adminFeedback({bool? handledOnly, int page = 0}) =>
      _delay(() => engine.adminFeedback(_user, handledOnly: handledOnly));

  @override
  Future<void> adminSetFeedbackHandled(String id, bool handled) =>
      _delay(() => engine.adminSetFeedbackHandled(_user, id, handled));
}
