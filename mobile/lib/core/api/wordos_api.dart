import '../models/models.dart';

/// The complete contract between the app and the WordOS backend.
///
/// Implemented twice: [HttpWordOsApi] (the real ASP.NET Core backend, Phase 5)
/// and `MockWordOsApi` (Phase 1–4 development, deleted in Phase 7).
///
/// Every decision that the documents assign to the backend — eligibility,
/// pass/fail, scheduling, maturity, archiving, level changes — arrives through
/// this interface as data. The feature layer never recomputes any of it.
abstract class WordOsApi {
  // ── Auth & profile ────────────────────────────────────────────────────────
  Future<AuthResponse> register({
    required String email,
    required String password,
    required String displayName,
  });

  Future<AuthResponse> login({
    required String email,
    required String password,
  });

  Future<void> logout();

  Future<UserProfile> me();

  // ── Onboarding ────────────────────────────────────────────────────────────
  Future<List<InterestOption>> interestOptions();

  Future<UserProfile> saveInterests(List<String> interests);

  /// Starts an adaptive placement test and returns its first question.
  ///
  /// The test is adaptive, so the client cannot be handed every item up front:
  /// which question comes next depends on how the previous one was answered
  /// (`docs/06-PLACEMENT-ALGORITHM.md`).
  Future<PlacementStep> startPlacement();

  Future<PlacementStep> answerPlacement({
    required String sessionId,
    required String itemId,
    required String answer,
  });

  Future<PlacementResult> completePlacement(String sessionId);

  // ── Hub ───────────────────────────────────────────────────────────────────
  Future<HubState> hub();

  // ── Words ─────────────────────────────────────────────────────────────────
  Future<List<WordCandidate>> lookupWord(String query);

  Future<Word> addWord(WordCandidate candidate);

  Future<WordPage> words({WordState? state, int page = 0});

  Future<WordDetail> wordDetail(String wordId);

  // ── Skill sessions ────────────────────────────────────────────────────────
  Future<SkillSession> startSession(SkillType skill);

  /// Re-reads a session as the server has it.
  ///
  /// The client keeps no session state of its own (rule R1), so after a crash,
  /// a backgrounded app or a lost connection it asks where it was rather than
  /// reconstructing it. The stored content is replayed — starting again would
  /// generate a different passage and lose the answers already given.
  Future<SkillSession> resumeSession(String sessionId);

  Future<AnswerResult> submitAnswer({
    required String sessionId,
    required String itemId,
    required String answer,
    int? timeMs,
  });

  Future<WritingEvaluation> submitWriting({
    required String sessionId,
    required String itemId,
    required String sentence,
  });

  Future<SpeakingTurn> submitSpeakingTurn({
    required String sessionId,
    required String transcript,
  });

  Future<SessionResult> completeSession(String sessionId);

  Future<void> abandonSession(String sessionId);

  // ── Weekly review ─────────────────────────────────────────────────────────
  Future<WeeklyReviewSession> startWeeklyReview();

  Future<ReviewAnswerResult> answerWeeklyReview({
    required String reviewId,
    required String itemId,
    required String answer,
  });

  Future<WeeklyReviewResult> completeWeeklyReview(String reviewId);

  // ── Settings & config ─────────────────────────────────────────────────────
  Future<SkillLevel> updateSkillLevel({
    required SkillType skill,
    required CefrLevel level,
  });

  Future<SkillLevel> updateDailyTarget({
    required SkillType skill,
    required int target,
  });

  Future<PublicConfig> config();

  // ── Owner/Admin analytics ─────────────────────────────────────────────────
  //
  // Every one of these is authorized **server-side** against the caller's role.
  // Hiding the UI is not the access control — a normal user calling these
  // directly must be refused with `FORBIDDEN` (403).

  Future<AdminOverview> adminOverview();

  Future<List<AdminUserSummary>> adminUsers();

  Future<AdminUserDetail> adminUserDetail(String userId);
}

/// A failure surfaced to the UI. `code` mirrors the backend error code so
/// messages can be localized instead of showing raw server text.
class ApiException implements Exception {
  const ApiException(
    this.code,
    this.message, {
    this.statusCode,
    this.fieldErrors = const {},
  });

  final String code;
  final String message;
  final int? statusCode;

  /// Per-field messages from a validation failure, keyed by field name.
  final Map<String, List<String>> fieldErrors;

  bool get isUnauthorized => statusCode == 401;
  bool get isForbidden => statusCode == 403;
  bool get isNotFound => statusCode == 404;

  /// The request was understood but not possible right now — no words due, a
  /// session already finished, an item that is no longer the current one. These
  /// are normal states, not errors to apologise for.
  bool get isConflict => statusCode == 409;

  bool get isRateLimited => statusCode == 429;
  bool get isServerError => (statusCode ?? 0) >= 500;
  bool get isNetwork => code == 'NETWORK' || code == 'TIMEOUT';

  /// Worth offering a retry button for; a 404 or 409 is not.
  bool get isRetryable => isNetwork || isServerError || isRateLimited;

  @override
  String toString() => 'ApiException($code, $message)';
}
