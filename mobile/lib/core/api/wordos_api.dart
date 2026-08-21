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
    /// Digits only, without the plus. Kept separate from [phoneNumber] all the
    /// way to the database — see the server's `User.PhoneCountryCode`.
    ///
    /// **Required** (ADR-054): the server refuses a registration without a
    /// number, because an account it cannot reach is one whose learner cannot
    /// be helped when they report a problem. Still nullable on this signature
    /// so the failure is the server's answer — `INVALID_PHONE` — rather than a
    /// compile error that hides which rule was broken.
    String? phoneCountryCode,
    String? phoneNumber,
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

  /// Resolves one word as it appeared in a passage, inflections and all.
  ///
  /// Separate from [lookupWord] because it answers a different question: not
  /// "what could the learner mean?" but "what is *this* word?". The base form
  /// is worked out on the server, so the same tap gives the same answer on
  /// every platform (rule R1).
  Future<WordDefinition> defineWord(String word);

  Future<Word> addWord(WordCandidate candidate);

  /// The learner's own vocabulary, newest first.
  ///
  /// [query] searches the word and its meaning; [state] filters by pipeline
  /// state and is used by the developer views rather than by the learner, who
  /// sees one list (Part 2 §42–§46).
  Future<WordPage> words({WordState? state, int page = 0, String? query});

  Future<WordDetail> wordDetail(String wordId);

  // ── Skill sessions ────────────────────────────────────────────────────────
  /// Starts (or resumes) a skill session.
  ///
  /// [practice] asks for a session with no vocabulary attached, for the days
  /// when nothing is due (Part 2 §5). The server decides whether that is
  /// possible for the skill; the client only ever asks.
  Future<SkillSession> startSession(SkillType skill, {bool practice = false});

  /// Re-reads a session as the server has it.
  ///
  /// The client keeps no session state of its own (rule R1), so after a crash,
  /// a backgrounded app or a lost connection it asks where it was rather than
  /// reconstructing it. The stored content is replayed — starting again would
  /// generate a different passage and lose the answers already given.
  Future<SkillSession> resumeSession(String sessionId);

  /// Re-tells this session's passage at another CEFR level.
  ///
  /// The same story in different language, not a new one. Only legal before
  /// the questions begin — the server refuses it afterwards, because
  /// re-telling replaces the items the learner's answers belong to.
  Future<SkillSession> changeSessionLevel(String sessionId, CefrLevel level);

  /// Marks one warm-up answer before a Speaking conversation.
  ///
  /// Recorded nowhere: it measures nothing and moves nothing. Marked on the
  /// server only because the client must never hold the answer key.
  Future<WarmupResult> answerWarmup({
    required String sessionId,
    required String wordId,
    required String answer,
  });

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

  /// [days] scopes the figures to a window: 1 for today, 5, 10, or any custom
  /// number. Omitted reports all time.
  Future<AdminOverview> adminOverview({int? days});

  /// The learner list, searched and paged server-side.
  ///
  /// [days] narrows it to learners who did something in that window — 1 for
  /// today — and is answered from the activity log, not from a "last login"
  /// column (Part 3 §34–§35).
  Future<AdminUserPage> adminUsers({String? query, int? days, int page = 0});

  Future<AdminUserDetail> adminUserDetail(String userId);

  // ── Feedback (ADR-053) ─────────────────────────────────────────────────────

  /// Sends a message from this learner to the Owner.
  ///
  /// Write-only for a learner: there is no call to read feedback back, their
  /// own included. The Owner reads it in the dashboard, and nothing a learner
  /// can call returns anybody's words but their own — which they already have.
  Future<void> sendFeedback(String body);

  /// The Owner's inbox: unhandled first, newest first.
  ///
  /// [handledOnly] null means everything; true or false filters. Owner-only,
  /// and refused by the API for anyone else regardless of what the UI shows.
  Future<FeedbackPage> adminFeedback({bool? handledOnly, int page = 0});

  /// Marks one message dealt with, or puts it back.
  ///
  /// Reversible on purpose: an Owner reading a long list will mark the wrong
  /// one eventually, and a message that cannot be un-handled is lost.
  Future<void> adminSetFeedbackHandled(String id, bool handled);

  /// Brings a learner's waiting skills forward, for testing the spaced gaps.
  ///
  /// Owner-only, and refused by the API for anyone else. It moves *scheduled*
  /// dates only — nothing that already happened changes — and the server writes
  /// it to the activity log, because a pipeline finished in an afternoon would
  /// otherwise read as an extraordinary learner (ADR-037).
  Future<ScheduleAdvance> adminAdvanceSchedule(String userId, {int days = 2});

  /// One learner's vocabulary, filtered by pipeline state — the Owner's view,
  /// which is deliberately the opposite of the learner's (Part 3).
  Future<AdminWordPage> adminUserWords(
    String userId, {
    WordState? state,
    String? query,
    int page = 0,
  });

  /// One word's whole life, for any learner.
  Future<AdminWordJourney> adminWordJourney(String wordId);

  /// The placement test behind a learner's starting levels, with the answers
  /// that produced them (Part 3).
  Future<PlacementEvidence> adminPlacementEvidence(String userId);
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

  /// Any failure at all, as something the UI can show.
  ///
  /// Everything below the UI is expected to throw [ApiException] — but
  /// "expected to" is not "does". A response whose shape this version of the
  /// app does not recognise fails inside `fromJson` with a `TypeError`, which
  /// is not an [ApiException] and so slips past every `on ApiException catch`
  /// in the app. What the learner sees when that happens is a spinner that
  /// never stops, because the `catch` that would have cleared it never runs.
  ///
  /// So the UI catches everything and converts here. A real [ApiException]
  /// passes through untouched; anything else becomes `UNEXPECTED`, whose text
  /// is written by this app rather than by `toString()` — a Dart error message
  /// is English, internal, and occasionally quotes the data that broke it.
  factory ApiException.from(Object error) => error is ApiException
      ? error
      : const ApiException('UNEXPECTED', 'Something went wrong.');

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

  /// Worth offering a retry button for.
  ///
  /// The test is whether pressing it could change the answer. Being offline,
  /// overloaded or rate-limited all clear on their own, so they qualify; a
  /// finished session or a question that has moved on will refuse identically
  /// for ever, and a button that cannot help is worse than none — the learner
  /// presses it again and again and concludes the app is broken.
  ///
  /// Most 409s are in the second group, which is why the status alone is not
  /// enough: `SESSION_STARTING` is a 409 whose entire meaning is "ask again in
  /// a moment", and it would otherwise be the one refusal that tells the
  /// learner to retry while offering no way to.
  bool get isRetryable =>
      isNetwork ||
      isServerError ||
      isRateLimited ||
      const {
        'SESSION_STARTING',
        'SESSION_RACE',
        'RELEVEL_UNAVAILABLE',
        'UNEXPECTED',
        'BAD_RESPONSE',
      }.contains(code);

  @override
  String toString() => 'ApiException($code, $message)';
}
