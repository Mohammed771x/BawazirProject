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
  }) : engine = engine ?? MockEngine();

  final MockEngine engine;
  final String? Function() tokenReader;

  /// Multiplier on the artificial latency. Tests pass `0` so no timer outlives
  /// the widget tree — a fire-and-forget call in `dispose` would otherwise be
  /// pending at teardown.
  final double latencyScale;

  static const Duration _latency = Duration(milliseconds: 320);
  static const Duration _aiLatency = Duration(milliseconds: 900);

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
  }) =>
      _delay(() => engine.register(email, password, displayName));

  @override
  Future<AuthResponse> login({
    required String email,
    required String password,
  }) =>
      _delay(() => engine.login(email, password));

  @override
  Future<void> logout() => _delay(() => engine.logout(tokenReader()));

  @override
  Future<UserProfile> me() => _delay(() => engine.profile(_user));

  @override
  Future<List<InterestOption>> interestOptions() =>
      _delay(() => MockEngine.interestOptions);

  @override
  Future<UserProfile> saveInterests(List<String> interests) =>
      _delay(() => engine.saveInterests(_user, interests));

  @override
  Future<PlacementStep> startPlacement() =>
      _delay(() => engine.startPlacement(_user), _aiLatency);

  @override
  Future<PlacementStep> answerPlacement({
    required String sessionId,
    required String itemId,
    required String answer,
  }) =>
      _delay(
        () => engine.answerPlacement(_user, sessionId, itemId, answer),
        const Duration(milliseconds: 380),
      );

  @override
  Future<PlacementResult> completePlacement(String sessionId) =>
      _delay(() => engine.completePlacement(_user, sessionId), _aiLatency);

  @override
  Future<HubState> hub() => _delay(() => engine.hub(_user));

  @override
  Future<List<WordCandidate>> lookupWord(String query) =>
      _delay(() => engine.lookup(query), const Duration(milliseconds: 220));

  @override
  Future<Word> addWord(WordCandidate candidate) =>
      _delay(() => engine.addWord(_user, candidate), _aiLatency);

  @override
  Future<WordPage> words({WordState? state, int page = 0}) =>
      _delay(() => engine.words(_user, state));

  @override
  Future<WordDetail> wordDetail(String wordId) =>
      _delay(() => engine.wordDetail(_user, wordId));

  @override
  Future<SkillSession> startSession(SkillType skill) =>
      _delay(() => engine.startSession(_user, skill), _aiLatency);

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
      _delay(
        () => engine.submitAnswer(_user, sessionId, itemId, answer),
        const Duration(milliseconds: 180),
      );

  @override
  Future<WritingEvaluation> submitWriting({
    required String sessionId,
    required String itemId,
    required String sentence,
  }) =>
      _delay(
        () => engine.submitWriting(_user, sessionId, itemId, sentence),
        _aiLatency,
      );

  @override
  Future<SpeakingTurn> submitSpeakingTurn({
    required String sessionId,
    required String transcript,
  }) =>
      _delay(
        () => engine.submitSpeakingTurn(_user, sessionId, transcript),
        _aiLatency,
      );

  @override
  Future<SessionResult> completeSession(String sessionId) =>
      _delay(() => engine.completeSession(_user, sessionId));

  @override
  Future<void> abandonSession(String sessionId) =>
      _delay(() => engine.abandonSession(_user, sessionId));

  @override
  Future<WeeklyReviewSession> startWeeklyReview() =>
      _delay(() => engine.startWeeklyReview(_user), _aiLatency);

  @override
  Future<ReviewAnswerResult> answerWeeklyReview({
    required String reviewId,
    required String itemId,
    required String answer,
  }) =>
      _delay(
        () => engine.answerWeeklyReview(_user, reviewId, itemId, answer),
        const Duration(milliseconds: 180),
      );

  @override
  Future<WeeklyReviewResult> completeWeeklyReview(String reviewId) =>
      _delay(() => engine.completeWeeklyReview(_user, reviewId));

  @override
  Future<SkillLevel> updateSkillLevel({
    required SkillType skill,
    required CefrLevel level,
  }) =>
      _delay(() => engine.updateSkillLevel(_user, skill, level));

  @override
  Future<SkillLevel> updateDailyTarget({
    required SkillType skill,
    required int target,
  }) =>
      _delay(() => engine.updateDailyTarget(_user, skill, target));

  @override
  Future<PublicConfig> config() => _delay(() => MockEngine.configuration);

  // The role check happens inside the engine, not here — see [MockAdmin].

  @override
  Future<AdminOverview> adminOverview() =>
      _delay(() => engine.adminOverview(_user));

  @override
  Future<List<AdminUserSummary>> adminUsers() =>
      _delay(() => engine.adminUsers(_user));

  @override
  Future<AdminUserDetail> adminUserDetail(String userId) =>
      _delay(() => engine.adminUserDetail(_user, userId));
}
