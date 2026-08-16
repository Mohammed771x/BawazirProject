import 'dart:math';

import '../../core/api/wordos_api.dart';
import '../../core/models/models.dart';
import 'mock_admin.dart';
import 'mock_analytics.dart';
import 'mock_content.dart';
import 'levels/level_engine.dart';
import 'mock_dictionary.dart';
import 'placement/placement_engine.dart';

/// ⚠️ DISPOSABLE DEVELOPMENT COMPONENT — deleted in Phase 7.
///
/// This is a faithful *simulation* of the rules that the C# backend will own:
/// scheduling gaps, eligibility, per-skill independence, partial-failure
/// retention, maturity, active vocabulary and weekly review. It lives outside
/// `lib/features/**` on purpose: the app's feature layer must keep treating
/// these decisions as server-owned facts (rule R1).
class MockEngine {
  MockEngine() {
    _seedData();
  }

  final Random _random = Random();
  final MockContentGenerator _content = MockContentGenerator();

  final Map<String, MockUser> _usersByEmail = {};
  final Map<String, MockUser> _usersById = {};
  final Map<String, String> _tokens = {}; // token → userId
  final Map<String, _MockSession> _sessions = {};
  final Map<String, _MockReview> _reviews = {};

  final MockAnalytics analytics = MockAnalytics();
  late final MockAdmin admin = MockAdmin(this, analytics);

  int _idCounter = 0;

  /// Development-only clock shift so the 2-day gaps can be demonstrated
  /// without waiting two days. Never exists in the real backend.
  Duration _clockOffset = Duration.zero;

  DateTime get now => DateTime.now().toUtc().add(_clockOffset);

  Duration get clockOffset => _clockOffset;

  void advanceClock(Duration by) => _clockOffset += by;

  void resetClock() => _clockOffset = Duration.zero;

  String _newId(String prefix) => '${prefix}_${++_idCounter}';

  // ── Configuration (rule R3: never hard-coded in feature code) ─────────────
  static const PublicConfig configuration = PublicConfig(
    skillIntervalDays: 2,
    minDailyTarget: 5,
    maxDailyTarget: 15,
    defaultDailyTarget: 10,
    skillsOrder: [
      SkillType.reading,
      SkillType.listening,
      SkillType.speaking,
      SkillType.writing,
      SkillType.spelling,
    ],
    weeklyReviewPeriodDays: 7,
  );

  /// The CEFR level that content for [skill] should be generated at.
  ///
  /// Spelling carries no band of its own (ADR-008), so its clue difficulty —
  /// Arabic meaning vs. English definition, tiles vs. free typing — is derived
  /// from the learner's **Reading** level: whether an English definition is a
  /// usable clue is a reading-comprehension question, not a spelling one.
  CefrLevel _contentLevelFor(MockUser user, SkillType skill) {
    final own = user.levels[skill]?.userSelectedLevel;
    if (own != null) return own;
    return user.levels[SkillType.reading]?.userSelectedLevel ?? CefrLevel.b1;
  }

  SkillType? _nextSkillAfter(SkillType skill) {
    final order = configuration.skillsOrder;
    final index = order.indexOf(skill);
    if (index < 0 || index >= order.length - 1) return null;
    return order[index + 1];
  }

  // ── Auth ──────────────────────────────────────────────────────────────────
  AuthResponse register(String email, String password, String displayName) {
    final key = email.trim().toLowerCase();
    if (_usersByEmail.containsKey(key)) {
      throw const ApiException('EMAIL_TAKEN', 'This email is already registered.',
          statusCode: 409);
    }
    if (password.length < 6) {
      throw const ApiException(
          'WEAK_PASSWORD', 'Password must be at least 6 characters.',
          statusCode: 400);
    }
    final user = MockUser(
      id: _newId('u'),
      email: key,
      password: password,
      displayName: displayName.trim().isEmpty ? 'Learner' : displayName.trim(),
      createdAt: now,
    );
    _usersByEmail[key] = user;
    _usersById[user.id] = user;
    return _authFor(user);
  }

  AuthResponse login(String email, String password) {
    final user = _usersByEmail[email.trim().toLowerCase()];
    if (user == null || user.password != password) {
      throw const ApiException('INVALID_CREDENTIALS', 'Wrong email or password.',
          statusCode: 401);
    }
    return _authFor(user);
  }

  AuthResponse _authFor(MockUser user) {
    final token = _newId('tok');
    _tokens[token] = user.id;
    analytics.recordSignIn(user.id, now);
    return AuthResponse(token: token, user: _profile(user));
  }

  // ── Owner/Admin ───────────────────────────────────────────────────────────
  //
  // Authorization lives in [MockAdmin], not in the UI: a normal user calling
  // these is refused with 403 exactly as the real backend must refuse them.

  AdminOverview adminOverview(MockUser caller) =>
      admin.overview(caller, _usersById.values.toList());

  List<AdminUserSummary> adminUsers(MockUser caller) =>
      admin.users(caller, _usersById.values.toList());

  AdminUserDetail adminUserDetail(MockUser caller, String userId) =>
      admin.userDetail(caller, _usersById[userId]);

  void logout(String? token) {
    if (token != null) _tokens.remove(token);
  }

  MockUser requireUser(String? token) {
    final id = token == null ? null : _tokens[token];
    final user = id == null ? null : _usersById[id];
    if (user == null) {
      throw const ApiException('UNAUTHORIZED', 'Please sign in again.',
          statusCode: 401);
    }
    return user;
  }

  UserProfile _profile(MockUser user) => UserProfile(
        id: user.id,
        email: user.email,
        displayName: user.displayName,
        role: user.role,
        onboardingStage: user.stage,
        interests: List.unmodifiable(user.interests),
        skillLevels: configuration.skillsOrder
            .map((s) => user.levels[s]!)
            .toList(growable: false),
        createdAt: user.createdAt,
      );

  UserProfile profile(MockUser user) => _profile(user);

  // ── Onboarding ────────────────────────────────────────────────────────────
  static const List<InterestOption> interestOptions = [
    InterestOption(slug: 'technology', labelEn: 'Technology', labelAr: 'التقنية', emoji: '💻'),
    InterestOption(slug: 'programming', labelEn: 'Programming', labelAr: 'البرمجة', emoji: '⌨️'),
    InterestOption(slug: 'ai', labelEn: 'Artificial Intelligence', labelAr: 'الذكاء الاصطناعي', emoji: '🤖'),
    InterestOption(slug: 'football', labelEn: 'Football', labelAr: 'كرة القدم', emoji: '⚽'),
    InterestOption(slug: 'business', labelEn: 'Business', labelAr: 'الأعمال', emoji: '📈'),
    InterestOption(slug: 'entrepreneurship', labelEn: 'Entrepreneurship', labelAr: 'ريادة الأعمال', emoji: '🚀'),
    InterestOption(slug: 'economics', labelEn: 'Economics', labelAr: 'الاقتصاد', emoji: '💹'),
    InterestOption(slug: 'medicine', labelEn: 'Medicine', labelAr: 'الطب', emoji: '🩺'),
    InterestOption(slug: 'travel', labelEn: 'Travel', labelAr: 'السفر', emoji: '✈️'),
    InterestOption(slug: 'history', labelEn: 'History', labelAr: 'التاريخ', emoji: '🏛️'),
    InterestOption(slug: 'science', labelEn: 'Science', labelAr: 'العلوم', emoji: '🔬'),
  ];

  UserProfile saveInterests(MockUser user, List<String> interests) {
    user.interests
      ..clear()
      ..addAll(interests);
    if (user.stage == OnboardingStage.interests) {
      user.stage = OnboardingStage.placement;
    }
    return _profile(user);
  }

  // ── Placement (adaptive — see docs/06-PLACEMENT-ALGORITHM.md) ─────────────

  final PlacementEngine _placement = PlacementEngine();

  PlacementStep startPlacement(MockUser user) =>
      _placement.start(_newId('pt'), user.id);

  PlacementStep answerPlacement(
    MockUser user,
    String sessionId,
    String itemId,
    String answer,
  ) {
    _requireOwnPlacement(user, sessionId);
    return _placement.answer(sessionId, itemId, answer);
  }

  PlacementResult completePlacement(MockUser user, String sessionId) {
    _requireOwnPlacement(user, sessionId);
    final result = _placement.complete(sessionId);

    for (final level in result.levels) {
      user.levels[level.skill] = level;
    }
    user.spellingDiagnostic = result.spelling;
    user.stage = OnboardingStage.complete;

    return PlacementResult(
      levels: result.levels,
      spelling: result.spelling,
      summary: result.hasLowConfidence
          ? 'These are your starting levels. A couple of them are still '
              'provisional — WordOS keeps measuring your real sessions and '
              'will settle them within your first two weeks.'
          : 'Your starting levels are set per skill. They are a starting '
              'point — WordOS keeps measuring your real performance and '
              'adjusts them over time.',
    );
  }

  void _requireOwnPlacement(MockUser user, String sessionId) {
    final run = _placement.runFor(sessionId);
    if (run == null || run.userId != user.id) {
      throw const ApiException(
        'PLACEMENT_NOT_FOUND',
        'This placement test has expired. Please start again.',
        statusCode: 404,
      );
    }
  }


  // ── Words ─────────────────────────────────────────────────────────────────
  List<WordCandidate> lookup(String query) => MockDictionary.lookup(query);

  Word addWord(MockUser user, WordCandidate candidate) {
    final text = candidate.text.trim();
    final meaning = candidate.meaning.trim();

    if (text.isEmpty || meaning.isEmpty) {
      throw const ApiException(
        'INVALID_WORD',
        'A word and its intended meaning are both required.',
        statusCode: 400,
      );
    }

    // The lexicon is the authority, not the request body. A client could post a
    // candidate it assembled itself, so the request is treated purely as a
    // lookup key and everything stored comes from the resolved row — a forged
    // meaning, level or definition cannot get in (ADR-012).
    final resolved = MockDictionary.resolve(
      text: text,
      meaning: meaning,
      senseId: candidate.senseId,
    );
    if (resolved == null) {
      throw const ApiException(
        'WORD_NOT_FOUND',
        'That word and meaning are not in the dictionary.',
        statusCode: 404,
      );
    }

    // Duplicate identity is **word + meaning** (one sense), not word alone:
    // `book = كتاب` and `book = يحجز` are two independent journeys, but adding
    // either of them twice is a duplicate (§19–20, `04-DATA-MODEL.md`).
    final duplicate = user.words.any((w) =>
        w.text.toLowerCase() == resolved.text.toLowerCase() &&
        w.meaning == resolved.meaning);
    if (duplicate) {
      throw const ApiException(
        'WORD_ALREADY_ADDED',
        'You have already added this word with this meaning.',
        statusCode: 409,
      );
    }

    final firstSkill = configuration.skillsOrder.first;
    final record = MockWord(
      id: _newId('w'),
      text: resolved.text,
      meaning: resolved.meaning,
      definitionEn: resolved.definitionEn,
      partOfSpeech: resolved.partOfSpeech,
      level: resolved.suggestedLevel,
      addedAt: now,
      currentSkill: firstSkill,
      skills: {
        for (final skill in configuration.skillsOrder)
          skill: MockSkillState(
            status: skill == firstSkill
                ? SkillStatus.available
                : SkillStatus.pending,
            availableAt: skill == firstSkill ? now : null,
          ),
      },
    );
    record.events.add(WordEvent(
      type: WordEventType.added,
      skill: null,
      createdAt: now,
    ));
    user.words.add(record);
    return _wordModel(record);
  }

  Word _wordModel(MockWord w) => Word(
        id: w.id,
        text: w.text,
        meaning: w.meaning,
        definitionEn: w.definitionEn,
        partOfSpeech: w.partOfSpeech,
        cefrLevel: w.level,
        state: w.state,
        currentSkill: w.currentSkill,
        addedAt: w.addedAt,
        nextEligibleAt: w.currentSkill == null
            ? null
            : w.skills[w.currentSkill]!.availableAt,
        exposureCount: w.exposureCount,
        skills: configuration.skillsOrder
            .map(
              (skill) => WordSkillState(
                skill: skill,
                status: _effectiveStatus(w.skills[skill]!),
                availableAt: w.skills[skill]!.availableAt,
                attempts: w.skills[skill]!.attempts,
              ),
            )
            .toList(),
      );

  /// A `PENDING` skill becomes `AVAILABLE` the moment its scheduled date passes
  /// — the client is told the effective state, never the raw one.
  SkillStatus _effectiveStatus(MockSkillState state) {
    if (state.status == SkillStatus.pending &&
        state.availableAt != null &&
        !state.availableAt!.isAfter(now)) {
      return SkillStatus.available;
    }
    return state.status;
  }

  WordPage words(MockUser user, WordState? state) {
    final items = user.words
        .where((w) => state == null || w.state == state)
        .toList()
      ..sort((a, b) => b.addedAt.compareTo(a.addedAt));
    return WordPage(
      items: items.map(_wordModel).toList(),
      total: items.length,
    );
  }

  WordDetail wordDetail(MockUser user, String wordId) {
    final w = user.words.firstWhere(
      (w) => w.id == wordId,
      orElse: () => throw const ApiException('NOT_FOUND', 'Word not found.',
          statusCode: 404),
    );
    return WordDetail(word: _wordModel(w), events: List.of(w.events));
  }

  // ── Eligibility & hub ─────────────────────────────────────────────────────
  bool _isEligible(MockWord w, SkillType skill) {
    if (w.state != WordState.learning) return false;
    if (w.currentSkill != skill) return false;
    final state = w.skills[skill]!;
    if (state.status == SkillStatus.passed) return false;
    final at = state.availableAt;
    return at == null || !at.isAfter(now);
  }

  List<MockWord> _eligibleWords(MockUser user, SkillType skill) {
    final due = user.words.where((w) => _isEligible(w, skill)).toList();
    // Priority: the longest-overdue first, then the oldest word. Newly eligible
    // words must never be starved (MVP Core part 2 §17).
    due.sort((a, b) {
      final aAt = a.skills[skill]!.availableAt ?? a.addedAt;
      final bAt = b.skills[skill]!.availableAt ?? b.addedAt;
      final cmp = aAt.compareTo(bAt);
      return cmp != 0 ? cmp : a.addedAt.compareTo(b.addedAt);
    });
    return due;
  }

  HubState hub(MockUser user) {
    final today = now;
    final addedToday = user.words
        .where((w) =>
            w.addedAt.year == today.year &&
            w.addedAt.month == today.month &&
            w.addedAt.day == today.day)
        .length;

    final cards = <SkillCard>[];
    for (final skill in configuration.skillsOrder) {
      final due = _eligibleWords(user, skill);
      final upcoming = user.words
          .where((w) =>
              w.state == WordState.learning &&
              w.currentSkill == skill &&
              (w.skills[skill]!.availableAt?.isAfter(now) ?? false))
          .map((w) => w.skills[skill]!.availableAt!)
          .toList()
        ..sort();
      cards.add(
        SkillCard(
          skill: skill,
          availability: due.isEmpty
              ? SkillAvailability.empty
              : SkillAvailability.available,
          dueWordCount: due.length,
          sessionWordCount:
              min(due.length, user.levels[skill]!.dailyTargetWords),
          level: user.levels[skill]!.userSelectedLevel,
          nextDueAt: upcoming.isEmpty ? null : upcoming.first,
        ),
      );
    }

    final periodWords = _weeklyReviewWords(user);
    final lastReview = user.lastWeeklyReviewAt;
    final reviewReady = periodWords.isNotEmpty &&
        (lastReview == null ||
            now.difference(lastReview).inDays >=
                configuration.weeklyReviewPeriodDays);

    return HubState(
      dailyProgress: DailyProgress(
        wordsAddedToday: addedToday,
        dailyTarget: user.levels[configuration.skillsOrder.first]!.dailyTargetWords,
      ),
      skills: cards,
      weeklyReview: WeeklyReviewStatus(
        available: reviewReady,
        wordCount: periodWords.length,
        periodStart:
            now.subtract(Duration(days: configuration.weeklyReviewPeriodDays)),
        nextAvailableAt: reviewReady || lastReview == null
            ? null
            : lastReview.add(
                Duration(days: configuration.weeklyReviewPeriodDays)),
      ),
      vocabulary: VocabularyCounts(
        learning:
            user.words.where((w) => w.state == WordState.learning).length,
        active: user.words.where((w) => w.state == WordState.active).length,
        archived: user.words.where((w) => w.state == WordState.archived).length,
      ),
    );
  }

  // ── Sessions ──────────────────────────────────────────────────────────────
  SkillSession startSession(MockUser user, SkillType skill) {
    final target = user.levels[skill]!.dailyTargetWords;
    final due = _eligibleWords(user, skill).take(target).toList();
    if (due.isEmpty) {
      throw const ApiException(
        'NO_WORDS_DUE',
        'No words are due for this skill yet.',
        statusCode: 409,
      );
    }

    final targetWords = due
        .map((w) => SessionTargetWord(
              wordId: w.id,
              text: w.text,
              meaning: w.meaning,
            ))
        .toList();
    final definitions = due.map((w) => w.definitionEn).toList();
    final level = _contentLevelFor(user, skill);

    final GeneratedSession generated;
    ConversationSetup? conversation;
    switch (skill) {
      case SkillType.reading:
        generated = _content.buildComprehension(
          words: targetWords,
          definitions: definitions,
          interests: user.interests,
          listening: false,
        );
      case SkillType.listening:
        generated = _content.buildComprehension(
          words: targetWords,
          definitions: definitions,
          interests: user.interests,
          listening: true,
        );
      case SkillType.writing:
        generated = _content.buildWriting(
          words: targetWords,
          interests: user.interests,
        );
      case SkillType.spelling:
        generated = _content.buildSpelling(
          words: targetWords,
          definitions: definitions,
          level: level,
          preferredMode: user.spellingDiagnostic?.supportMode,
        );
      case SkillType.speaking:
        generated = GeneratedSession(
          content: null,
          items: const [],
          correctAnswers: const {},
        );
        conversation = _content.buildConversation(
          words: targetWords,
          interests: user.interests,
          learnerName: user.displayName,
        );
    }

    final session = _MockSession(
      id: _newId('s'),
      userId: user.id,
      skill: skill,
      level: level,
      wordIds: due.map((w) => w.id).toList(),
      correctAnswers: generated.correctAnswers,
      startedAt: now,
      targetWords: targetWords,
    );
    _sessions[session.id] = session;
    for (final item in generated.items) {
      session.register(item);
    }

    for (final w in due) {
      w.events.add(WordEvent(
        type: WordEventType.skillStarted,
        skill: skill,
        createdAt: now,
      ));
    }

    return session.snapshot = SkillSession(
      id: session.id,
      skill: skill,
      levelUsed: level,
      content: generated.content,
      targetWords: targetWords,
      items: generated.items,
      conversation: conversation,
      progress: session.progress,
    );
  }

  /// Replays a session as it stands, the way the backend's `GET /sessions/{id}`
  /// does — the content is stored, never regenerated, so resuming shows the
  /// same passage and the queue position the learner left off at.
  SkillSession resumeSession(MockUser user, String sessionId) {
    final session = _requireSession(user, sessionId);
    final snapshot = session.snapshot;
    if (snapshot == null) {
      throw const ApiException('SESSION_NOT_FOUND', 'Session not found.',
          statusCode: 404);
    }
    return SkillSession(
      id: snapshot.id,
      skill: snapshot.skill,
      levelUsed: snapshot.levelUsed,
      content: snapshot.content,
      targetWords: snapshot.targetWords,
      items: snapshot.items,
      conversation: snapshot.conversation,
      progress: session.progress,
    );
  }

  _MockSession _requireSession(MockUser user, String sessionId) {
    final session = _sessions[sessionId];
    if (session == null || session.userId != user.id) {
      throw const ApiException('SESSION_NOT_FOUND', 'Session not found.',
          statusCode: 404);
    }
    return session;
  }

  AnswerResult submitAnswer(
    MockUser user,
    String sessionId,
    String itemId,
    String answer,
  ) {
    final session = _requireSession(user, sessionId);
    final correct = session.correctAnswers[itemId];
    if (correct == null) {
      throw const ApiException('ITEM_NOT_FOUND', 'Question not found.',
          statusCode: 404);
    }
    if (session.currentItemId != itemId) {
      // A retry after a dropped connection, or a client out of step. Rejecting
      // keeps the attempt counters honest.
      throw const ApiException(
        'ITEM_NOT_CURRENT',
        'That question is no longer the active one.',
        statusCode: 409,
      );
    }

    // Spelling is judged case-insensitively; meaning questions are exact
    // matches against the option the backend itself issued.
    final isCorrect = session.skill == SkillType.spelling
        ? answer.trim().toLowerCase() == correct.trim().toLowerCase()
        : answer == correct;

    final requeued =
        session.recordAttempt(itemId: itemId, isCorrect: isCorrect);
    final wordId = session.wordIdForItem(itemId);

    return AnswerResult(
      itemId: itemId,
      isCorrect: isCorrect,
      correctAnswer: correct,
      wordId: wordId,
      explanation: _explanationFor(user, session, itemId, correct),
      requeued: requeued,
      attemptNumber: session.attemptsFor(itemId),
      progress: session.progress,
    );
  }

  /// A short teaching note shown after every attempt, right or wrong, so the
  /// learner leaves the item knowing *why* (demo review §28).
  String? _explanationFor(
    MockUser user,
    _MockSession session,
    String itemId,
    String correct,
  ) {
    final wordId = session.wordIdForItem(itemId);
    if (wordId == null) return null;
    final word = user.words.where((w) => w.id == wordId).firstOrNull;
    if (word == null) return null;

    if (session.skill == SkillType.spelling) {
      return '"${word.text}" — ${word.meaning}. ${word.definitionEn}';
    }
    return '"${word.text}" means $correct. ${word.definitionEn}';
  }

  WritingEvaluation submitWriting(
    MockUser user,
    String sessionId,
    String itemId,
    String sentence,
  ) {
    final session = _requireSession(user, sessionId);
    final word = session.correctAnswers[itemId];
    if (word == null) {
      throw const ApiException('ITEM_NOT_FOUND', 'Task not found.',
          statusCode: 404);
    }
    if (session.currentItemId != itemId) {
      throw const ApiException(
        'ITEM_NOT_CURRENT',
        'That task is no longer the active one.',
        statusCode: 409,
      );
    }

    final evaluation = _evaluateWriting(word: word, sentence: sentence);
    final requeued = session.recordAttempt(
      itemId: itemId,
      isCorrect: evaluation.passed,
    );
    session.writingSentences[itemId] = sentence;

    return WritingEvaluation(
      itemId: itemId,
      passed: evaluation.passed,
      usedWord: evaluation.usedWord,
      meaningCorrect: evaluation.meaningCorrect,
      usageCorrect: evaluation.usageCorrect,
      understandable: evaluation.understandable,
      grammarNote: evaluation.grammarNote,
      feedback: evaluation.feedback,
      suggestion: evaluation.suggestion,
      requeued: requeued,
      attemptNumber: session.attemptsFor(itemId),
      progress: session.progress,
    );
  }

  /// Stand-in for the AI writing evaluator (Phase 6).
  ///
  /// It scores the dimensions the documents name — did they use the word, is
  /// the usage right, is the sentence understandable — and returns feedback
  /// that teaches rather than only reporting pass or fail
  /// (`MVP Core.txt` §32, demo review §44).
  _WritingVerdict _evaluateWriting({
    required String word,
    required String sentence,
  }) {
    final trimmed = sentence.trim();
    final normalized = trimmed.toLowerCase();
    final target = word.toLowerCase();

    // Accept simple inflections so a correct sentence is not failed on
    // morphology: allocate / allocated / allocating / allocates.
    final stem = target.endsWith('e')
        ? target.substring(0, target.length - 1)
        : target;
    final usedWord = normalized.contains(target) ||
        RegExp('\\b${RegExp.escape(stem)}(e?[sd]|ing)\\b')
            .hasMatch(normalized);

    final words =
        trimmed.split(RegExp(r'\s+')).where((w) => w.isNotEmpty).toList();
    final understandable = words.length >= 4;

    // A verb or connective suggests a real clause rather than a bare phrase.
    final hasClause = RegExp(
      r'\b(is|are|was|were|has|have|had|do|does|did|can|will|would|and|but|because|so|when|to)\b',
      caseSensitive: false,
    ).hasMatch(normalized);

    // Small grammar slips must not fail a correct usage (MVP Core §32): the
    // capital letter and full stop are reported, never penalised.
    final tidyStart = trimmed.isNotEmpty &&
        trimmed[0] == trimmed[0].toUpperCase() &&
        trimmed[0] != trimmed[0].toLowerCase();
    final tidyEnd = trimmed.endsWith('.') ||
        trimmed.endsWith('!') ||
        trimmed.endsWith('?');

    final usageCorrect = usedWord && hasClause;
    final passed = usedWord && understandable;

    final String feedback;
    if (trimmed.isEmpty) {
      feedback = 'Write a sentence before checking.';
    } else if (!usedWord) {
      feedback = 'Your sentence does not use "$word". '
          'The task is to use the word itself, in any form.';
    } else if (!understandable) {
      feedback = 'You used "$word", but the sentence is too short to show that '
          'you understand it. Add who, what or why.';
    } else if (!usageCorrect) {
      feedback = 'You used "$word", but the sentence has no clear verb, so the '
          'meaning is hard to judge. Try a full sentence.';
    } else if (!tidyStart || !tidyEnd) {
      feedback = 'Correct — "$word" is used well. '
          'Small thing: start with a capital letter and end with a full stop.';
    } else {
      feedback = 'Correct — "$word" is used naturally and the sentence is '
          'clear.';
    }

    return _WritingVerdict(
      passed: passed,
      usedWord: usedWord,
      meaningCorrect: passed,
      usageCorrect: usageCorrect,
      understandable: understandable,
      grammarNote: !tidyStart || !tidyEnd ? 'punctuation' : 'none',
      feedback: feedback,
      suggestion: passed && !usageCorrect
          ? 'A fuller version: "I had to $word my time carefully last week."'
          : null,
    );
  }


  /// One turn of the Speaking conversation.
  ///
  /// This is a conversation, not a quiz read aloud: the reply reacts to what
  /// the learner just said, acknowledges the target words they managed to use,
  /// and only then steers toward the next one (demo review §36–39). Phase 6
  /// replaces the scripted reply with the AI service; the *shape* — a message
  /// plus, on the final turn, one structured evaluation per word — is the
  /// contract that stays.
  SpeakingTurn submitSpeakingTurn(
    MockUser user,
    String sessionId,
    String transcript,
  ) {
    final session = _requireSession(user, sessionId);

    if (transcript.trim().isEmpty) {
      throw const ApiException(
        'EMPTY_TURN',
        'Say something before sending your turn.',
        statusCode: 400,
      );
    }

    final alreadyUsed = _wordsUsedIn(session.transcripts.join(' '), session);
    session.transcripts.add(transcript);
    final usedNow = _wordsUsedIn(session.transcripts.join(' '), session);

    // Words the learner produced in *this* turn, so the reply can acknowledge
    // them specifically.
    final justUsed = usedNow
        .where((w) => !alreadyUsed.any((u) => u.wordId == w.wordId))
        .toList();
    final remaining = session.targetWords
        .where((w) => !usedNow.any((u) => u.wordId == w.wordId))
        .toList();

    final maxTurns = session.targetWords.length + 3;
    final isFinal =
        remaining.isEmpty || session.transcripts.length >= maxTurns;

    if (!isFinal) {
      return SpeakingTurn(
        aiMessage: _content.conversationReply(
          remaining: remaining,
          justUsed: justUsed,
          turn: session.transcripts.length,
        ),
        isFinal: false,
        evaluations: const [],
      );
    }

    final evaluations = <SpeakingEvaluation>[];
    for (final word in session.targetWords) {
      final turnWithWord = session.transcripts.firstWhere(
        (t) => _mentions(t, word.text),
        orElse: () => '',
      );
      final used = turnWithWord.isNotEmpty;
      // Judge the turn the word appeared in, not the whole transcript: a long
      // conversation should not make a one-word answer look competent.
      final wordCount = turnWithWord
          .split(RegExp(r'\s+'))
          .where((w) => w.isNotEmpty)
          .length;
      final understandable = used && wordCount >= 5;
      final passed = used && understandable;

      session.wordOverrides[word.wordId] = passed;
      evaluations.add(
        SpeakingEvaluation(
          wordId: word.wordId,
          usedWord: used,
          meaningCorrect: passed,
          usageCorrect: passed,
          // Pronunciation cannot be judged from typed input. It is reported as
          // acceptable rather than silently failing the learner, and real
          // assessment arrives with voice capture in Phase 7.
          pronunciationAcceptable: used,
          understandable: understandable,
          passed: passed,
          feedback: passed
              ? 'You used "${word.text}" naturally and in a full sentence.'
              : used
                  ? 'You said "${word.text}", but the answer was too short to '
                      'show you understand it. Try a fuller sentence next time.'
                  : 'You did not use "${word.text}" during the conversation.',
        ),
      );
    }

    return SpeakingTurn(
      aiMessage: 'Thanks — that is enough for today. Here is how it went.',
      isFinal: true,
      evaluations: evaluations,
    );
  }

  static bool _mentions(String text, String word) => RegExp(
        '\\b${RegExp.escape(word.toLowerCase())}',
        caseSensitive: false,
      ).hasMatch(text.toLowerCase());

  List<SessionTargetWord> _wordsUsedIn(String text, _MockSession session) =>
      session.targetWords.where((w) => _mentions(text, w.text)).toList();


  /// Applies the WordOS state machine. This is the rule set the C# backend owns.
  SessionResult completeSession(MockUser user, String sessionId) {
    final session = _requireSession(user, sessionId);
    final skill = session.skill;
    final outcomes = <WordOutcome>[];

    var comprehensionCorrect = 0;
    var comprehensionTotal = 0;
    session.results.forEach((itemId, ok) {
      if (session.wordIdForItem(itemId) == null) {
        comprehensionTotal++;
        if (ok) comprehensionCorrect++;
      }
    });

    for (final wordId in session.wordIds) {
      final word = user.words.firstWhere((w) => w.id == wordId);
      final passed = session.passedFor(wordId);
      final state = word.skills[skill]!;
      state.attempts += 1;

      SkillType? nextSkill;
      DateTime? nextEligibleAt;
      var becameActive = false;

      if (passed) {
        state.status = SkillStatus.passed;
        state.passedAt = now;
        word.events.add(WordEvent(
          type: WordEventType.skillPassed,
          skill: skill,
          createdAt: now,
        ));

        nextSkill = _nextSkillAfter(skill);
        if (nextSkill == null) {
          // All five skills passed → Mature → Active (Word Life Cycle §22).
          word.state = WordState.active;
          word.currentSkill = null;
          word.events
            ..add(WordEvent(
                type: WordEventType.becameMature, skill: null, createdAt: now))
            ..add(WordEvent(
                type: WordEventType.enteredActive, skill: null, createdAt: now));
          becameActive = true;
        } else {
          nextEligibleAt =
              now.add(Duration(days: configuration.skillIntervalDays));
          word.currentSkill = nextSkill;
          word.skills[nextSkill]!
            ..status = SkillStatus.pending
            ..availableAt = nextEligibleAt;
        }
      } else {
        // Failure reschedules ONLY this skill; passed skills keep their status
        // (rule R5 / Word Life Cycle §34).
        state.status = SkillStatus.failed;
        state.availableAt =
            now.add(Duration(days: configuration.skillIntervalDays));
        nextEligibleAt = state.availableAt;
        nextSkill = skill;
        word.events.add(WordEvent(
          type: WordEventType.skillFailed,
          skill: skill,
          createdAt: now,
        ));
      }

      analytics.recordAttempt(
        userId: user.id,
        wordId: word.id,
        skill: skill,
        passed: passed,
        attemptNumber: state.attempts,
        at: now,
      );

      outcomes.add(
        WordOutcome(
          wordId: word.id,
          text: word.text,
          meaning: word.meaning,
          passed: passed,
          newStatus: state.status,
          nextSkill: nextSkill,
          nextEligibleAt: nextEligibleAt,
          becameActive: becameActive,
          firstAttemptCorrect: passed,
          attemptsInSession: session.attemptsForWord(word.id),
        ),
      );
    }

    // Rolling per-skill performance feeds the level engine (User Flow §10–11).
    final level = user.levels[skill]!;
    final accuracy = comprehensionTotal > 0
        ? comprehensionCorrect / comprehensionTotal
        : outcomes.where((o) => o.passed).length /
            max(1, outcomes.length);
    final sessions = level.evaluationSessions + 1;
    final rolling =
        ((level.rollingAccuracy * level.evaluationSessions) + accuracy) /
            sessions;
    user.levels[skill] = level.copyWith(
      evaluationSessions: sessions,
      rollingAccuracy: rolling,
    );

    // With fresh evidence in hand, ask the level engine whether the
    // system-validated level should move — and, if it rose, whether any Active
    // words have now fallen far enough behind to retire (rule R6).
    _reviewLevel(user, skill);

    final durationMs = now.difference(session.startedAt).inMilliseconds.abs();
    analytics.recordSession(
      userId: user.id,
      skill: skill,
      at: now,
      durationMs: durationMs,
      wordCount: session.wordIds.length,
    );

    final result = SessionResult(
      sessionId: session.id,
      skill: skill,
      comprehensionCorrect: comprehensionCorrect,
      comprehensionTotal: comprehensionTotal,
      words: outcomes,
      durationMs: durationMs,
    );
    _sessions.remove(session.id);
    return result;
  }

  // ── Level progression & archiving (see levels/level_engine.dart) ──────────

  final LevelEngine _levels = const LevelEngine();

  /// Runs the level policy for [skill] and, on a promotion, the archiving
  /// sweep. Called after every completed session.
  void _reviewLevel(MockUser user, SkillType skill) {
    final decision = _levels.evaluate(user.levels[skill]!);
    if (decision == null) return;

    user.levels[skill] = _levels.apply(user.levels[skill]!, decision);

    if (!decision.moved) return;

    user.levelChanges.add(
      LevelChangeRecord(
        skill: skill,
        previous: decision.previous,
        next: decision.next,
        changeType: LevelChangeType.systemValidated,
        accuracy: decision.accuracy,
        sessionsConsidered: decision.sessionsConsidered,
        createdAt: now,
      ),
    );

    // Only *growth* can retire words. A demotion must never un-archive or
    // archive anything (`Word Life Cycle.txt` §28).
    if (decision.isPromotion) _archiveEligibleWords(user);
  }

  /// Archives Active words the learner has visibly outgrown.
  ///
  /// Never deletes: the row stays, gains `archivedAt`, and keeps its whole
  /// history (rule R8, lifecycle §31).
  void _archiveEligibleWords(MockUser user) {
    final proven = _levels.systemValidatedLevel(user.levels.values);
    if (proven == null) return;

    for (final word in user.words) {
      final eligible = _levels.shouldArchive(
        wordLevel: word.level,
        state: word.state,
        exposureCount: word.exposureCount,
        systemValidatedLevel: proven,
      );
      if (!eligible) continue;

      word.state = WordState.archived;
      word.archivedAt = now;
      word.events.add(WordEvent(
        type: WordEventType.archived,
        skill: null,
        createdAt: now,
      ));
    }
  }

  /// A manual level change is logged too, so the dashboard can compare manual
  /// against system-validated movement (`MVP Core.txt` §60).
  void _recordManualLevelChange(
    MockUser user,
    SkillType skill,
    CefrLevel? previous,
    CefrLevel next,
  ) =>
      user.levelChanges.add(
        LevelChangeRecord(
          skill: skill,
          previous: previous,
          next: next,
          changeType: LevelChangeType.userManualChange,
          accuracy: 0,
          sessionsConsidered: 0,
          createdAt: now,
        ),
      );

  void abandonSession(MockUser user, String sessionId) {
    final session = _sessions[sessionId];
    if (session != null && session.userId == user.id) {
      _sessions.remove(sessionId);
    }
  }

  // ── Weekly review ─────────────────────────────────────────────────────────
  List<MockWord> _weeklyReviewWords(MockUser user) {
    final start =
        now.subtract(Duration(days: configuration.weeklyReviewPeriodDays));
    return user.words.where((w) => w.addedAt.isAfter(start)).toList();
  }

  WeeklyReviewSession startWeeklyReview(MockUser user) {
    final words = _weeklyReviewWords(user);
    if (words.isEmpty) {
      throw const ApiException(
        'NO_REVIEW_WORDS',
        'No words were added during this period.',
        statusCode: 409,
      );
    }
    final allMeanings = words.map((w) => w.meaning).toList();
    final review = _MockReview(
      id: _newId('wr'),
      userId: user.id,
      periodStart:
          now.subtract(Duration(days: configuration.weeklyReviewPeriodDays)),
      totalWords: words.length,
    );
    for (final w in words) {
      final item = _content.buildReviewItem(
        wordId: w.id,
        text: w.text,
        meaning: w.meaning,
        otherMeanings: allMeanings.where((m) => m != w.meaning).toList(),
      );
      review.queue.add(item);
      review.correct[item.id] = w.meaning.trim().isEmpty ? '—' : w.meaning.trim();
    }
    review.queue.shuffle(_random);
    _reviews[review.id] = review;

    return WeeklyReviewSession(
      id: review.id,
      periodStart: review.periodStart,
      totalWords: review.totalWords,
      queue: List.of(review.queue),
    );
  }

  ReviewAnswerResult answerWeeklyReview(
    MockUser user,
    String reviewId,
    String itemId,
    String answer,
  ) {
    final review = _reviews[reviewId];
    if (review == null || review.userId != user.id) {
      throw const ApiException('REVIEW_NOT_FOUND', 'Review not found.',
          statusCode: 404);
    }
    final correct = review.correct[itemId];
    if (correct == null) {
      throw const ApiException('ITEM_NOT_FOUND', 'Review item not found.',
          statusCode: 404);
    }

    final isCorrect = answer == correct;
    final isFirstAttempt = !review.attempted.contains(itemId);
    review.attempted.add(itemId);
    review.totalAttempts += 1;
    if (isCorrect && isFirstAttempt) review.firstPassCorrect += 1;

    final index = review.queue.indexWhere((i) => i.id == itemId);
    ReviewItem? item;
    if (index >= 0) {
      item = review.queue.removeAt(index);
    }
    // Wrong answers go back to the END of the queue (MVP Core §43).
    if (!isCorrect && item != null) review.queue.add(item);

    return ReviewAnswerResult(
      itemId: itemId,
      isCorrect: isCorrect,
      correctAnswer: correct,
      requeued: !isCorrect,
      remaining: review.queue.length,
      nextItem: review.queue.isEmpty ? null : review.queue.first,
    );
  }

  WeeklyReviewResult completeWeeklyReview(MockUser user, String reviewId) {
    final review = _reviews[reviewId];
    if (review == null || review.userId != user.id) {
      throw const ApiException('REVIEW_NOT_FOUND', 'Review not found.',
          statusCode: 404);
    }
    user.lastWeeklyReviewAt = now;
    _reviews.remove(reviewId);
    // Note: no word state is touched here — measurement only (rule R9).
    return WeeklyReviewResult(
      reviewId: review.id,
      totalWords: review.totalWords,
      firstPassCorrect: review.firstPassCorrect,
      weeklyScore: review.totalWords == 0
          ? 0
          : review.firstPassCorrect / review.totalWords,
      totalAttempts: review.totalAttempts,
    );
  }

  // ── Settings ──────────────────────────────────────────────────────────────
  SkillLevel updateSkillLevel(MockUser user, SkillType skill, CefrLevel level) {
    if (!user.levels[skill]!.carriesCefrLevel) {
      // Spelling has no CEFR band to set (ADR-008). Rejecting rather than
      // silently ignoring means a client bug shows up immediately.
      throw const ApiException(
        'SKILL_NOT_LEVELLED',
        'This skill is measured but does not carry a CEFR level.',
        statusCode: 400,
      );
    }
    // Manual change updates ONLY the user-selected level (rule R6). It is
    // logged, but it can never archive a word or move the validated level.
    final previous = user.levels[skill]!.userSelectedLevel;
    user.levels[skill] = user.levels[skill]!.copyWith(userSelectedLevel: level);
    _recordManualLevelChange(user, skill, previous, level);
    return user.levels[skill]!;
  }

  SkillLevel updateDailyTarget(MockUser user, SkillType skill, int target) {
    final clamped = target.clamp(
      configuration.minDailyTarget,
      configuration.maxDailyTarget,
    );
    user.levels[skill] =
        user.levels[skill]!.copyWith(dailyTargetWords: clamped);
    return user.levels[skill]!;
  }

  // ── Seed data ─────────────────────────────────────────────────────────────

  /// Seeds an owner and two learners at different points in the lifecycle, so
  /// both the learner app and the Owner dashboard are explorable on first run.
  void _seedData() {
    final owner = _addUser(
      email: 'owner@wordos.app',
      password: 'wordos123',
      displayName: 'System Owner',
      daysAgo: 30,
      role: UserRole.owner,
      interests: const ['technology'],
    );
    // The owner is not a learner; nothing else is seeded for them.
    owner.stage = OnboardingStage.complete;

    final demo = _addUser(
      email: 'demo@wordos.app',
      password: 'wordos123',
      displayName: 'Demo Learner',
      daysAgo: 9,
      interests: const ['technology', 'programming', 'ai'],
    );

    // A spread of lifecycle positions so the app is explorable immediately.
    _seedWord(demo, 'operating system', addedDaysAgo: 8, passed: [
      SkillType.reading,
      SkillType.listening,
      SkillType.speaking,
      SkillType.writing,
      SkillType.spelling,
    ], active: true);
    _seedWord(demo, 'interface', addedDaysAgo: 6, passed: [
      SkillType.reading,
      SkillType.listening,
    ]);
    _seedWord(demo, 'hardware', addedDaysAgo: 5, passed: [SkillType.reading]);
    _seedWord(demo, 'software',
        addedDaysAgo: 4, passed: [SkillType.reading], failed: SkillType.listening);
    _seedWord(demo, 'research', addedDaysAgo: 1, passed: const []);
    _seedWord(demo, 'significant', addedDaysAgo: 1, passed: const []);
    _seedWord(demo, 'reliable', addedDaysAgo: 0, passed: const []);
    _seedWord(demo, 'evidence', addedDaysAgo: 0, passed: const []);

    // A second learner, weaker and less consistent, so the dashboard shows a
    // distribution rather than one data point.
    final second = _addUser(
      email: 'sara@wordos.app',
      password: 'wordos123',
      displayName: 'Sara Al-Amri',
      daysAgo: 5,
      interests: const ['medicine', 'science', 'تصوير فوتوغرافي'],
    );
    for (final skill in configuration.skillsOrder) {
      final level = second.levels[skill]!;
      second.levels[skill] = level.carriesCefrLevel
          ? level.copyWith(userSelectedLevel: CefrLevel.a2, systemAssessedLevel: CefrLevel.a2)
          : level;
    }
    _seedWord(second, 'evidence', addedDaysAgo: 4, passed: [SkillType.reading]);
    _seedWord(second, 'reliable',
        addedDaysAgo: 3, passed: const [], failed: SkillType.reading);
    _seedWord(second, 'research', addedDaysAgo: 2, passed: const []);
  }

  MockUser _addUser({
    required String email,
    required String password,
    required String displayName,
    required int daysAgo,
    required List<String> interests,
    UserRole role = UserRole.user,
  }) {
    final user = MockUser(
      id: _newId('u'),
      email: email,
      password: password,
      displayName: displayName,
      createdAt: now.subtract(Duration(days: daysAgo)),
    )
      ..stage = OnboardingStage.complete
      ..role = role
      ..interests.addAll(interests);

    _usersByEmail[user.email] = user;
    _usersById[user.id] = user;

    // Seeded history has to be visible to analytics too, otherwise the
    // dashboard would show an empty system on first run.
    for (var back = daysAgo; back >= 0; back -= 2) {
      analytics.recordSignIn(user.id, now.subtract(Duration(days: back)));
    }
    return user;
  }

  void _seedWord(
    MockUser user,
    String key, {
    required int addedDaysAgo,
    required List<SkillType> passed,
    SkillType? failed,
    bool active = false,
  }) {
    final candidate = MockDictionary.entries[key]!.first;
    final addedAt = now.subtract(Duration(days: addedDaysAgo));
    final skills = <SkillType, MockSkillState>{
      for (final s in configuration.skillsOrder)
        s: MockSkillState(status: SkillStatus.pending),
    };

    final wordId = _newId('w');
    var cursor = addedAt;
    for (final s in passed) {
      skills[s]!
        ..status = SkillStatus.passed
        ..availableAt = cursor
        ..passedAt = cursor
        ..attempts = 1;
      analytics
        ..recordAttempt(
          userId: user.id,
          wordId: wordId,
          skill: s,
          passed: true,
          attemptNumber: 1,
          at: cursor,
        )
        ..recordSession(
          userId: user.id,
          skill: s,
          at: cursor,
          durationMs: 210000,
          wordCount: 1,
        );
      cursor = cursor.add(Duration(days: configuration.skillIntervalDays));
    }

    SkillType? current;
    if (active) {
      current = null;
    } else if (failed != null) {
      skills[failed]!
        ..status = SkillStatus.failed
        ..availableAt = cursor
        ..attempts = 1;
      current = failed;
      analytics
        ..recordAttempt(
          userId: user.id,
          wordId: wordId,
          skill: failed,
          passed: false,
          attemptNumber: 1,
          at: cursor,
        )
        ..recordSession(
          userId: user.id,
          skill: failed,
          at: cursor,
          durationMs: 240000,
          wordCount: 1,
        );
    } else {
      current = configuration.skillsOrder.firstWhere(
        (s) => !passed.contains(s),
        orElse: () => configuration.skillsOrder.last,
      );
      skills[current]!
        ..status = SkillStatus.pending
        ..availableAt = cursor;
    }

    user.words.add(
      MockWord(
        id: wordId,
        text: candidate.text,
        meaning: candidate.meaning,
        definitionEn: candidate.definitionEn,
        partOfSpeech: candidate.partOfSpeech,
        level: candidate.suggestedLevel,
        addedAt: addedAt,
        currentSkill: current,
        skills: skills,
        state: active ? WordState.active : WordState.learning,
        exposureCount: active ? 2 : 0,
      )..events.add(
          WordEvent(type: WordEventType.added, skill: null, createdAt: addedAt),
        ),
    );
  }
}

// ── Internal mutable records ────────────────────────────────────────────────

/// Mock per-skill state (public so [MockWord] can expose its map).
class MockSkillState {
  MockSkillState({this.status = SkillStatus.pending, this.availableAt});

  int attempts = 0;
  DateTime? passedAt;

  SkillStatus status;
  DateTime? availableAt;
}

/// Mock word record (public so [MockUser] can expose its list).
class MockWord {
  MockWord({
    required this.id,
    required this.text,
    required this.meaning,
    required this.definitionEn,
    required this.partOfSpeech,
    required this.level,
    required this.addedAt,
    required this.currentSkill,
    required this.skills,
    this.state = WordState.learning,
    this.exposureCount = 0,
  });

  final String id;
  final String text;
  final String meaning;
  final String definitionEn;
  final String partOfSpeech;
  final CefrLevel level;
  final DateTime addedAt;
  SkillType? currentSkill;
  final Map<SkillType, MockSkillState> skills;
  WordState state;
  int exposureCount;
  DateTime? archivedAt;
  final List<WordEvent> events = [];
}

/// Internal result of the writing evaluator, before it is shaped for the wire.
class _WritingVerdict {
  const _WritingVerdict({
    required this.passed,
    required this.usedWord,
    required this.meaningCorrect,
    required this.usageCorrect,
    required this.understandable,
    required this.grammarNote,
    required this.feedback,
    required this.suggestion,
  });

  final bool passed;
  final bool usedWord;
  final bool meaningCorrect;
  final bool usageCorrect;
  final bool understandable;
  final String grammarNote;
  final String feedback;
  final String? suggestion;
}

/// One in-flight skill session, including the **in-session learning loop**.
///
/// The queue is the important part. A wrong answer does not drop the item: it
/// is recorded and pushed back to the end of the queue so the learner meets it
/// again before the session ends (demo review §29–31, §47). Only a *first*
/// attempt success passes the word for that skill; anything else leaves the
/// word to be rescheduled, which is what turns a mistake into reinforcement
/// rather than a dead end.
class _MockSession {
  _MockSession({
    required this.id,
    required this.userId,
    required this.skill,
    required this.level,
    required this.wordIds,
    required this.correctAnswers,
    required this.startedAt,
    required this.targetWords,
  });

  /// How many times one item may be asked in a single session. Without a cap a
  /// learner who keeps answering wrongly would never reach the end
  /// (demo review §56).
  static const int maxAttemptsPerItem = 3;

  final String id;
  final String userId;
  final SkillType skill;
  final CefrLevel level;
  final List<String> wordIds;
  final Map<String, String> correctAnswers;
  final DateTime startedAt;
  final List<SessionTargetWord> targetWords;

  /// Cleared items → whether they were right on the **first** attempt.
  final Map<String, bool> results = {};

  final Map<String, int> attempts = {};
  final Map<String, String> writingSentences = {};
  final List<String> transcripts = [];

  /// Speaking results are decided by the evaluation, not by per-item answers.
  final Map<String, bool> wordOverrides = {};

  /// The payload handed out at start, kept so a resume replays it rather than
  /// generating different content.
  SkillSession? snapshot;

  final List<String> queue = [];
  final Map<String, String> _itemToWord = {};
  int _totalItems = 0;

  int get totalItems => _totalItems;

  String? get currentItemId => queue.isEmpty ? null : queue.first;

  String? wordIdForItem(String itemId) => _itemToWord[itemId];

  void register(SessionItem item) {
    queue.add(item.id);
    _totalItems++;
    if (item.wordId != null) _itemToWord[item.id] = item.wordId!;
  }

  bool knows(String itemId) => correctAnswers.containsKey(itemId);

  int attemptsFor(String itemId) => attempts[itemId] ?? 0;

  /// Records an attempt and decides what happens to the item.
  ///
  /// Returns true when the item was requeued for another try.
  bool recordAttempt({required String itemId, required bool isCorrect}) {
    final attempt = (attempts[itemId] ?? 0) + 1;
    attempts[itemId] = attempt;

    // First attempt only — a later success reinforces the word but does not
    // pass the skill.
    if (attempt == 1) results[itemId] = isCorrect;

    queue.remove(itemId);

    final canRetry = !isCorrect && attempt < maxAttemptsPerItem;
    if (canRetry) {
      queue.add(itemId);
      return true;
    }
    return false;
  }

  int get clearedCount => _totalItems - queue.length;

  SessionProgress get progress => SessionProgress(
        nextItemId: currentItemId,
        remaining: queue.length,
        answered: clearedCount,
        total: _totalItems,
      );

  List<String> itemsForWord(String wordId) => _itemToWord.entries
      .where((e) => e.value == wordId)
      .map((e) => e.key)
      .toList();

  bool passedFor(String wordId) {
    if (wordOverrides.containsKey(wordId)) return wordOverrides[wordId]!;
    final itemIds = itemsForWord(wordId);
    if (itemIds.isEmpty) return false;
    return itemIds.every((id) => results[id] == true);
  }

  int attemptsForWord(String wordId) {
    final itemIds = itemsForWord(wordId);
    if (itemIds.isEmpty) return 0;
    return itemIds
        .map(attemptsFor)
        .fold(0, (a, b) => a > b ? a : b);
  }
}

class _MockReview {
  _MockReview({
    required this.id,
    required this.userId,
    required this.periodStart,
    required this.totalWords,
  });

  final String id;
  final String userId;
  final DateTime periodStart;
  final int totalWords;

  final List<ReviewItem> queue = [];
  final Map<String, String> correct = {};
  final Set<String> attempted = {};
  int firstPassCorrect = 0;
  int totalAttempts = 0;
}

/// Mock user record. Public so the API adapter can hold a resolved user.
class MockUser {
  MockUser({
    required this.id,
    required this.email,
    required this.password,
    required this.displayName,
    required this.createdAt,
  }) {
    for (final skill in MockEngine.configuration.skillsOrder) {
      levels[skill] = skill == SkillType.spelling
          ? SkillLevel.unlevelled(
              skill: skill,
              evaluationSessions: 0,
              rollingAccuracy: 0,
              dailyTargetWords: MockEngine.configuration.defaultDailyTarget,
            )
          : SkillLevel(
              skill: skill,
              userSelectedLevel: CefrLevel.b1,
              systemAssessedLevel: CefrLevel.b1,
              evaluationSessions: 0,
              rollingAccuracy: 0,
              dailyTargetWords: MockEngine.configuration.defaultDailyTarget,
            );
    }
  }

  final String id;
  final String email;
  final String password;
  final String displayName;
  final DateTime createdAt;

  /// Set at seed time only. Self-service registration always creates a `USER`;
  /// there is deliberately no client-reachable path to becoming an `OWNER`.
  UserRole role = UserRole.user;

  OnboardingStage stage = OnboardingStage.interests;
  final List<String> interests = [];
  final Map<SkillType, SkillLevel> levels = {};
  final List<MockWord> words = [];
  DateTime? lastWeeklyReviewAt;

  /// What placement measured for Spelling instead of a CEFR band (ADR-008).
  SpellingDiagnostic? spellingDiagnostic;

  /// Append-only level history — manual and system-validated alike (rule R6).
  final List<LevelChangeRecord> levelChanges = [];
}
