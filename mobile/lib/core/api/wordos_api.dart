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

  /// Asks the server to email a six-digit reset code (ADR-078).
  ///
  /// Returns normally whether or not the address is registered — the server
  /// answers identically either way, on purpose, so that this endpoint cannot
  /// be used to find out who has an account. The UI must therefore say
  /// "if that email is registered…" and never "no such account".
  Future<void> requestPasswordReset(String email);

  /// Redeems the code and sets a new password.
  ///
  /// Deliberately returns no session: the learner signs in afterwards with the
  /// password they just chose, so an intercepted code alone is not a way in.
  Future<void> resetPassword({
    required String email,
    required String code,
    required String newPassword,
  });

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

  /// Adds the word with a meaning the lexicon supplied.
  ///
  /// The candidate is a **lookup key**, not content: the server re-resolves the
  /// sense and stores its own row, so nothing this client sends decides what
  /// the word means (ADR-012).
  Future<Word> addWord(WordCandidate candidate);

  /// Adds [text] with a meaning the learner wrote themselves (ADR-072).
  ///
  /// The meaning is the one thing in this app the client may genuinely author,
  /// and it is authored by the learner rather than by the app.
  ///
  /// The **word** need not be in the dictionary (ADR-075). It must still be a
  /// word: a CEFR level, a part of speech and an English definition decide
  /// which passages it appears in and how Spelling clues it, so for a word the
  /// lexicon does not hold the checker is asked for all three — and a string it
  /// does not recognise as English throws [WordRejectedException], carrying the
  /// spelling it thinks was meant. Unlike a contested meaning that one cannot
  /// be overridden, because insisting does not make something a word.
  ///
  /// Throws `MEANING_NOT_ARABIC` for a meaning written in English: every skill
  /// marks answers against this string, so an English one makes its own
  /// questions unanswerable.
  /// Throws [MeaningRejectedException] when the checker disagrees (ADR-074),
  /// carrying what it would accept. Pass [acceptAnyway] to save the learner's
  /// wording regardless — only after they have seen what it said and chosen to
  /// keep theirs. The check still runs; the disagreement is recorded.
  ///
  /// Throws `MEANING_CHECK_UNAVAILABLE` when the checker cannot be reached.
  /// Nothing is saved: there is no fallback for "does this Arabic mean what
  /// this English word means", and inventing one would let an unchecked meaning
  /// in wearing the same badge as a checked one.
  Future<Word> addWordWithMeaning({
    required String text,
    required String meaning,
    bool acceptAnyway = false,
  });

  /// Adds [word] with the meaning it carries **in this session's passage**
  /// (ADR-073).
  ///
  /// The meaning is not sent: the server reads it from the glossary it stored
  /// when it generated the passage. That is the whole point of the call. The
  /// client used to fetch the word's dictionary senses and pick whichever read
  /// closest to the passage's gloss, which is a guess — and a wrong guess filed
  /// the word under a meaning the learner had never seen.
  ///
  /// Throws `NOT_IN_PASSAGE` for a word the generator did not gloss, which the
  /// caller answers by offering the ordinary dictionary instead.
  Future<Word> addWordFromPassage({
    required String sessionId,
    required String word,
  });

  /// Removes a word from the learner's vocabulary (ADR-071).
  ///
  /// Gone as far as this app is concerned: it leaves every list, no session
  /// will ask about it again, and adding it back starts a new journey from
  /// Reading. What the server does with the row afterwards is the server's
  /// business — the client neither knows nor renders it.
  ///
  /// Succeeds for a word that is already deleted, so a retry is safe.
  Future<void> deleteWord(String wordId);

  /// The daily reminders this device should schedule (ADR-076).
  ///
  /// One entry per time of day for the next several days, each carrying what
  /// will be true when it fires. They are computed here rather than on the
  /// phone for the ordinary reason (rule R1) and one specific one: a local
  /// notification goes off with no network and usually with the app closed, so
  /// whatever it says has to be settled days in advance.
  ///
  /// Refetched whenever the app is opened, which is also what keeps a learner
  /// who uses the app daily from ever reaching the end of the list.
  Future<List<DailyReminder>> dailyReminders();

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

/// The meaning checker disagreed with what the learner wrote (ADR-074).
///
/// An [ApiException] rather than a result type, because every caller of
/// `addWordWithMeaning` already handles `ApiException` and this must not be the
/// one failure that slips past a `catch` into a spinner that never stops. It
/// carries what the ordinary exception cannot: what the checker would accept.
///
/// [message] is the checker's own sentence, written in the learner's language
/// by the model. It is shown as-is — there is no localized string for "what is
/// wrong with *this* meaning", which is the whole point of asking.
class MeaningRejectedException extends ApiException {
  const MeaningRejectedException({
    required String message,
    required this.suggestions,
    this.corrected,
    int? statusCode,
  }) : super('MEANING_REJECTED', message, statusCode: statusCode);

  /// Meanings the checker would accept, for the learner to tap.
  final List<String> suggestions;

  /// Their own wording with its spelling fixed, when that is all that was
  /// wrong. Null when the meaning itself was the problem.
  final String? corrected;

  /// Whether this is a spelling correction rather than a wrong meaning.
  ///
  /// The two deserve different words to the learner: "you meant the right
  /// thing, spelled slightly wrong" is not "that is not what this word means".
  bool get isSpellingOnly => corrected != null;
}

/// The checker does not believe the typed word is English (ADR-075).
///
/// Only ever raised for a word the lexicon does not hold — a dictionary entry
/// is not re-judged. Sibling of [MeaningRejectedException] and deliberately not
/// the same class: that one is a question the learner may answer "keep mine"
/// to, and this one is not. There is no `acceptAnyway` for it, because a
/// pipeline cannot teach a string that is not a word.
class WordRejectedException extends ApiException {
  const WordRejectedException({
    required String message,
    this.correctedWord,
    int? statusCode,
  }) : super('WORD_NOT_RECOGNIZED', message, statusCode: statusCode);

  /// The spelling the checker thinks was meant, for the learner to tap. Null
  /// when it could not find one — a refusal with nothing beside it is the case
  /// this field exists to avoid, but it cannot always be avoided.
  final String? correctedWord;
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
