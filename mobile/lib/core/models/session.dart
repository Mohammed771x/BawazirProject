import 'enums.dart';

/// A highlighted target word inside generated reading/listening content.
class TargetSpan {
  const TargetSpan({
    required this.wordId,
    required this.start,
    required this.length,
  });

  final String wordId;
  final int start;
  final int length;

  int get end => start + length;

  factory TargetSpan.fromJson(Map<String, dynamic> json) => TargetSpan(
        wordId: json['wordId'] as String,
        start: (json['start'] as num).toInt(),
        length: (json['length'] as num).toInt(),
      );

  Map<String, dynamic> toJson() => {
        'wordId': wordId,
        'start': start,
        'length': length,
      };
}

/// One word of the passage, with the meaning it carries **there**.
///
/// Written by the generator while it was composing the sentence, so tapping a
/// word costs nothing and always answers about this sentence. A dictionary
/// lookup would return every sense the word has ever had — "bank" has six, and
/// five of them are wrong in any given passage.
class GlossaryEntry {
  const GlossaryEntry({
    required this.word,
    required this.meaning,
    required this.partOfSpeech,
  });

  final String word;
  final String meaning;

  /// noun, verb, adjective, auxiliary… A learner needs this to understand what
  /// they are adding: "will" as an auxiliary is a different thing to learn
  /// than "will" as a noun.
  final String partOfSpeech;

  factory GlossaryEntry.fromJson(Map<String, dynamic> json) => GlossaryEntry(
        word: json['word'] as String? ?? '',
        meaning: json['meaning'] as String? ?? '',
        partOfSpeech: json['partOfSpeech'] as String? ?? '',
      );

  Map<String, dynamic> toJson() => {
        'word': word,
        'meaning': meaning,
        'partOfSpeech': partOfSpeech,
      };
}

class SessionContent {
  const SessionContent({
    required this.text,
    required this.targetSpans,
    required this.revealTextAfterTest,
    this.glossary = const [],
    this.canChangeLevel = false,
  });

  final String text;
  final List<TargetSpan> targetSpans;

  /// Listening hides the transcript during the test and reveals it afterwards.
  final bool revealTextAfterTest;

  /// Every word of this passage with the meaning it carries here.
  final List<GlossaryEntry> glossary;

  /// Whether the passage may still be re-told at another level — only before
  /// the learner has answered anything, because re-telling replaces the
  /// questions their answers belong to.
  final bool canChangeLevel;

  /// The glossary entry for [word], matched case-insensitively.
  ///
  /// Null for a word the generator did not gloss, which the caller answers by
  /// asking the lexicon instead.
  GlossaryEntry? glossaryFor(String word) {
    final needle = word.trim().toLowerCase();
    for (final entry in glossary) {
      if (entry.word.trim().toLowerCase() == needle) return entry;
    }
    return null;
  }

  factory SessionContent.fromJson(Map<String, dynamic> json) => SessionContent(
        text: json['text'] as String? ?? '',
        targetSpans: (json['targetSpans'] as List<dynamic>? ?? const [])
            .map((e) => TargetSpan.fromJson(e as Map<String, dynamic>))
            .toList(),
        revealTextAfterTest: json['revealTextAfterTest'] as bool? ?? false,
        glossary: (json['glossary'] as List<dynamic>? ?? const [])
            .map((e) => GlossaryEntry.fromJson(e as Map<String, dynamic>))
            .toList(),
        canChangeLevel: json['canChangeLevel'] as bool? ?? false,
      );

  Map<String, dynamic> toJson() => {
        'text': text,
        'targetSpans': targetSpans.map((e) => e.toJson()).toList(),
        'revealTextAfterTest': revealTextAfterTest,
        'glossary': glossary.map((e) => e.toJson()).toList(),
        'canChangeLevel': canChangeLevel,
      };
}

/// One word of the warm-up a Speaking session opens with.
///
/// A spoken conversation gives the learner no time to look anything up, so the
/// meanings are checked before it starts — actively, not merely displayed. The
/// correct answer is deliberately absent: the server marks it (rule R1).
class WarmupWord {
  const WarmupWord({
    required this.wordId,
    required this.text,
    required this.options,
  });

  final String wordId;
  final String text;
  final List<String> options;

  factory WarmupWord.fromJson(Map<String, dynamic> json) => WarmupWord(
        wordId: json['wordId'] as String? ?? '',
        text: json['text'] as String? ?? '',
        options: (json['options'] as List<dynamic>? ?? const [])
            .map((e) => e as String)
            .toList(),
      );

  Map<String, dynamic> toJson() => {
        'wordId': wordId,
        'text': text,
        'options': options,
      };
}

/// The verdict on one warm-up answer. Recorded nowhere.
class WarmupResult {
  const WarmupResult({
    required this.wordId,
    required this.isCorrect,
    required this.correctAnswer,
  });

  final String wordId;
  final bool isCorrect;
  final String correctAnswer;

  factory WarmupResult.fromJson(Map<String, dynamic> json) => WarmupResult(
        wordId: json['wordId'] as String? ?? '',
        isCorrect: json['isCorrect'] as bool? ?? false,
        correctAnswer: json['correctAnswer'] as String? ?? '',
      );
}

class SessionTargetWord {
  const SessionTargetWord({
    required this.wordId,
    required this.text,
    required this.meaning,
  });

  final String wordId;
  final String text;
  final String meaning;

  factory SessionTargetWord.fromJson(Map<String, dynamic> json) =>
      SessionTargetWord(
        wordId: json['wordId'] as String,
        text: json['text'] as String,
        meaning: json['meaning'] as String? ?? '',
      );

  Map<String, dynamic> toJson() => {
        'wordId': wordId,
        'text': text,
        'meaning': meaning,
      };
}

/// The three sentences shown around a target word so the learner can infer its
/// meaning from context rather than recall a translation (demo review §26–27).
class WordContext {
  const WordContext({
    required this.before,
    required this.sentence,
    required this.after,
  });

  final String? before;
  final String sentence;
  final String? after;

  factory WordContext.fromJson(Map<String, dynamic> json) => WordContext(
        before: json['before'] as String?,
        sentence: json['sentence'] as String? ?? '',
        after: json['after'] as String?,
      );

  Map<String, dynamic> toJson() => {
        'before': before,
        'sentence': sentence,
        'after': after,
      };
}

/// One question / task inside a session. Options arrive **already shuffled by
/// the backend** (rule R7) and must be rendered in the given order.
class SessionItem {
  const SessionItem({
    required this.id,
    required this.type,
    required this.wordId,
    required this.prompt,
    this.promptKey,
    required this.options,
    required this.context,
    required this.clue,
    required this.clueKind,
    required this.letters,
    required this.inputMode,
    this.audioText,
    this.hints = const [],
  });

  final String id;
  final SessionItemType type;
  final String? wordId;
  /// The instruction as the server wrote it, in English. Shown only when
  /// [promptKey] is absent — that is, when the text *is* the content.
  final String prompt;

  /// Which fixed instruction this is, if it is one (ADR-035).
  final SessionPromptKey? promptKey;

  final List<String> options;

  /// Present on Reading target-word items only. Listening carries the same
  /// content as [audioText] instead, because showing it would turn a listening
  /// task into a reading task (demo review §34).
  final WordContext? context;

  final String? clue;
  final SpellingClueKind? clueKind;
  final List<String> letters;
  final SpellingInputMode? inputMode;

  /// Spoken by TTS. Listening items carry the sentence here and never in text.
  final String? audioText;

  /// The hint ladder for a spelling task, easiest last (Part 2 §38–§40).
  ///
  /// The first rung is already shown as [clue]; each press of "hint" reveals
  /// the next one. What each rung says was decided by the backend from the
  /// learner's level, so the client never picks the help itself.
  final List<SpellingHint> hints;

  factory SessionItem.fromJson(Map<String, dynamic> json) => SessionItem(
        id: json['id'] as String,
        type: SessionItemType.fromWire(json['type'] as String?),
        wordId: json['wordId'] as String?,
        prompt: json['prompt'] as String? ?? '',
        promptKey: SessionPromptKey.fromWire(json['promptKey'] as String?),
        options:
            (json['options'] as List<dynamic>? ?? const []).cast<String>(),
        context: json['context'] == null
            ? null
            : WordContext.fromJson(json['context'] as Map<String, dynamic>),
        clue: json['clue'] as String?,
        clueKind: json['clueKind'] == null
            ? null
            : SpellingClueKind.fromWire(json['clueKind'] as String),
        letters:
            (json['letters'] as List<dynamic>? ?? const []).cast<String>(),
        inputMode: json['inputMode'] == null
            ? null
            : SpellingInputMode.fromWire(json['inputMode'] as String),
        audioText: json['audioText'] as String?,
        hints: (json['hints'] as List<dynamic>? ?? const [])
            .map((h) => SpellingHint.fromJson(h as Map<String, dynamic>))
            .toList(),
      );

  Map<String, dynamic> toJson() => {
        'id': id,
        'type': type.wire,
        'wordId': wordId,
        'prompt': prompt,
        'promptKey': promptKey?.wire,
        'options': options,
        'context': context?.toJson(),
        'clue': clue,
        'clueKind': clueKind?.wire,
        'letters': letters,
        'inputMode': inputMode?.wire,
        'audioText': audioText,
        'hints': hints.map((h) => h.toJson()).toList(),
      };
}

/// One rung of a spelling task's hint ladder.
class SpellingHint {
  const SpellingHint({required this.kind, required this.text});

  final SpellingClueKind kind;
  final String text;

  factory SpellingHint.fromJson(Map<String, dynamic> json) => SpellingHint(
        kind: SpellingClueKind.fromWire(json['kind'] as String?),
        text: json['text'] as String? ?? '',
      );

  Map<String, dynamic> toJson() => {'kind': kind.wire, 'text': text};
}

/// One line of a speaking conversation.
class ConversationTurn {
  const ConversationTurn({required this.fromAi, required this.text});

  final bool fromAi;
  final String text;

  factory ConversationTurn.fromJson(Map<String, dynamic> json) =>
      ConversationTurn(
        fromAi: json['fromAi'] as bool? ?? false,
        text: json['text'] as String? ?? '',
      );

  Map<String, dynamic> toJson() => {'fromAi': fromAi, 'text': text};
}

class ConversationSetup {
  const ConversationSetup({
    required this.opening,
    required this.maxTurns,
    this.turns = const [],
  });

  final String opening;
  final int maxTurns;

  /// The exchange so far. Empty on a fresh session, populated on a resumed
  /// one — the client keeps no transcript of its own (rule R1), so this is the
  /// only way a returning learner sees what was already said.
  final List<ConversationTurn> turns;

  factory ConversationSetup.fromJson(Map<String, dynamic> json) =>
      ConversationSetup(
        opening: json['opening'] as String? ?? '',
        maxTurns: (json['maxTurns'] as num?)?.toInt() ?? 8,
        turns: (json['turns'] as List<dynamic>? ?? const [])
            .map((e) => ConversationTurn.fromJson(e as Map<String, dynamic>))
            .toList(),
      );

  Map<String, dynamic> toJson() => {
        'opening': opening,
        'maxTurns': maxTurns,
        'turns': turns.map((e) => e.toJson()).toList(),
      };
}

class SkillSession {
  const SkillSession({
    required this.id,
    required this.skill,
    required this.levelUsed,
    required this.content,
    required this.targetWords,
    required this.items,
    required this.conversation,
    this.progress,
    this.usedAiFallback = false,
    this.isPractice = false,
    this.warmup = const [],
  });

  final String id;
  final SkillType skill;
  final CefrLevel levelUsed;
  final SessionContent? content;
  final List<SessionTargetWord> targetWords;
  final List<SessionItem> items;
  final ConversationSetup? conversation;

  /// Where the learner is. Present on a resumed session, which is why the
  /// client must start from this rather than from the first item — after a
  /// restart, item one may already be answered.
  final SessionProgress? progress;

  /// The session ran without the AI service, so its content is weaker. Surfaced
  /// rather than hidden, so a poor session is not read as poor learning
  /// (`MVP Core.txt` §62).
  final bool usedAiFallback;

  /// Practice: real content, real questions, no vocabulary attached (§5).
  /// Said on screen, so the learner is never left wondering whether what they
  /// just did counted towards anything.
  final bool isPractice;

  /// Speaking only: the words to check before the conversation starts. Empty
  /// when there are none, and then the learner goes straight in.
  final List<WarmupWord> warmup;

  factory SkillSession.fromJson(Map<String, dynamic> json) => SkillSession(
        id: json['id'] as String,
        skill: SkillType.fromWire(json['skill'] as String?),
        levelUsed: CefrLevel.fromWire(json['levelUsed'] as String?),
        content: json['content'] == null
            ? null
            : SessionContent.fromJson(json['content'] as Map<String, dynamic>),
        targetWords: (json['targetWords'] as List<dynamic>? ?? const [])
            .map((e) => SessionTargetWord.fromJson(e as Map<String, dynamic>))
            .toList(),
        items: (json['items'] as List<dynamic>? ?? const [])
            .map((e) => SessionItem.fromJson(e as Map<String, dynamic>))
            .toList(),
        conversation: json['conversation'] == null
            ? null
            : ConversationSetup.fromJson(
                json['conversation'] as Map<String, dynamic>),
        progress: json['progress'] == null
            ? null
            : SessionProgress.fromJson(
                json['progress'] as Map<String, dynamic>),
        usedAiFallback: json['usedAiFallback'] as bool? ?? false,
        isPractice: json['isPractice'] as bool? ?? false,
        warmup: (json['warmup'] as List<dynamic>? ?? const [])
            .map((e) => WarmupWord.fromJson(e as Map<String, dynamic>))
            .toList(),
      );

  Map<String, dynamic> toJson() => {
        'id': id,
        'skill': skill.wire,
        'levelUsed': levelUsed.wire,
        'content': content?.toJson(),
        'targetWords': targetWords.map((e) => e.toJson()).toList(),
        'items': items.map((e) => e.toJson()).toList(),
        'conversation': conversation?.toJson(),
        'progress': progress?.toJson(),
        'usedAiFallback': usedAiFallback,
        'isPractice': isPractice,
        'warmup': warmup.map((e) => e.toJson()).toList(),
      };
}

/// Where the session goes after an answer.
///
/// The **server** decides this, including whether a wrong item comes back
/// (rule R1). A wrong answer never discards the word: it is recorded and the
/// item is requeued for reinforcement inside the same session
/// (demo review §29–31, §47–48).
class SessionProgress {
  const SessionProgress({
    required this.nextItemId,
    required this.remaining,
    required this.answered,
    required this.total,
    this.attempted = false,
  });

  /// `null` means every item is cleared — the client should complete the
  /// session.
  final String? nextItemId;

  /// Items still in the queue, including requeued ones.
  final int remaining;

  /// Items cleared so far, out of [total] distinct items.
  final int answered;
  final int total;

  /// Whether anything has been attempted yet — not the same as [answered],
  /// since a wrong answer requeues the item and clears nothing. A resumed
  /// session uses this to know the passage or audio step is already behind the
  /// learner.
  final bool attempted;

  double get ratio => total == 0 ? 0 : (answered / total).clamp(0.0, 1.0);

  factory SessionProgress.fromJson(Map<String, dynamic> json) =>
      SessionProgress(
        nextItemId: json['nextItemId'] as String?,
        remaining: (json['remaining'] as num?)?.toInt() ?? 0,
        answered: (json['answered'] as num?)?.toInt() ?? 0,
        total: (json['total'] as num?)?.toInt() ?? 0,
        attempted: json['attempted'] as bool? ?? false,
      );

  Map<String, dynamic> toJson() => {
        'nextItemId': nextItemId,
        'remaining': remaining,
        'answered': answered,
        'total': total,
        'attempted': attempted,
      };
}

class AnswerResult {
  const AnswerResult({
    required this.itemId,
    required this.isCorrect,
    required this.correctAnswer,
    required this.wordId,
    required this.progress,
    this.explanation,
    this.requeued = false,
    this.attemptNumber = 1,
  });

  final String itemId;
  final bool isCorrect;
  final String correctAnswer;
  final String? wordId;
  final SessionProgress progress;

  /// Why that is the answer — shown after every attempt so a wrong answer
  /// teaches instead of only scoring (demo review §28).
  final String? explanation;

  /// True when this item will be asked again later in this session.
  final bool requeued;

  final int attemptNumber;

  factory AnswerResult.fromJson(Map<String, dynamic> json) => AnswerResult(
        itemId: json['itemId'] as String,
        isCorrect: json['isCorrect'] as bool? ?? false,
        correctAnswer: json['correctAnswer'] as String? ?? '',
        wordId: json['wordId'] as String?,
        progress: SessionProgress.fromJson(
            json['progress'] as Map<String, dynamic>? ?? const {}),
        explanation: json['explanation'] as String?,
        requeued: json['requeued'] as bool? ?? false,
        attemptNumber: (json['attemptNumber'] as num?)?.toInt() ?? 1,
      );

  Map<String, dynamic> toJson() => {
        'itemId': itemId,
        'isCorrect': isCorrect,
        'correctAnswer': correctAnswer,
        'wordId': wordId,
        'progress': progress.toJson(),
        'explanation': explanation,
        'requeued': requeued,
        'attemptNumber': attemptNumber,
      };
}

class WritingEvaluation {
  const WritingEvaluation({
    required this.itemId,
    required this.passed,
    required this.usedWord,
    required this.meaningCorrect,
    required this.usageCorrect,
    required this.understandable,
    required this.grammarNote,
    required this.feedback,
    required this.suggestion,
    required this.progress,
    this.requeued = false,
    this.attemptNumber = 1,
  });

  final String itemId;
  final bool passed;
  final bool usedWord;
  final bool meaningCorrect;
  final bool usageCorrect;
  final bool understandable;
  final String grammarNote;
  final String feedback;
  final String? suggestion;
  final SessionProgress progress;
  final bool requeued;
  final int attemptNumber;

  factory WritingEvaluation.fromJson(Map<String, dynamic> json) =>
      WritingEvaluation(
        itemId: json['itemId'] as String,
        passed: json['passed'] as bool? ?? false,
        usedWord: json['usedWord'] as bool? ?? false,
        meaningCorrect: json['meaningCorrect'] as bool? ?? false,
        usageCorrect: json['usageCorrect'] as bool? ?? false,
        understandable: json['understandable'] as bool? ?? false,
        grammarNote: json['grammarNote'] as String? ?? '',
        feedback: json['feedback'] as String? ?? '',
        suggestion: json['suggestion'] as String?,
        progress: SessionProgress.fromJson(
            json['progress'] as Map<String, dynamic>? ?? const {}),
        requeued: json['requeued'] as bool? ?? false,
        attemptNumber: (json['attemptNumber'] as num?)?.toInt() ?? 1,
      );

  Map<String, dynamic> toJson() => {
        'itemId': itemId,
        'passed': passed,
        'usedWord': usedWord,
        'meaningCorrect': meaningCorrect,
        'usageCorrect': usageCorrect,
        'understandable': understandable,
        'grammarNote': grammarNote,
        'feedback': feedback,
        'suggestion': suggestion,
        'progress': progress.toJson(),
        'requeued': requeued,
        'attemptNumber': attemptNumber,
      };
}

class SpeakingEvaluation {
  const SpeakingEvaluation({
    required this.wordId,
    required this.usedWord,
    required this.meaningCorrect,
    required this.usageCorrect,
    required this.pronunciationAcceptable,
    required this.understandable,
    required this.passed,
    required this.feedback,
  });

  final String wordId;
  final bool usedWord;
  final bool meaningCorrect;
  final bool usageCorrect;
  final bool pronunciationAcceptable;
  final bool understandable;
  final bool passed;
  final String feedback;

  factory SpeakingEvaluation.fromJson(Map<String, dynamic> json) =>
      SpeakingEvaluation(
        wordId: json['wordId'] as String,
        usedWord: json['usedWord'] as bool? ?? false,
        meaningCorrect: json['meaningCorrect'] as bool? ?? false,
        usageCorrect: json['usageCorrect'] as bool? ?? false,
        pronunciationAcceptable:
            json['pronunciationAcceptable'] as bool? ?? false,
        understandable: json['understandable'] as bool? ?? false,
        passed: (json['result'] as String? ?? 'FAIL') == 'PASS',
        feedback: json['feedback'] as String? ?? '',
      );

  Map<String, dynamic> toJson() => {
        'wordId': wordId,
        'usedWord': usedWord,
        'meaningCorrect': meaningCorrect,
        'usageCorrect': usageCorrect,
        'pronunciationAcceptable': pronunciationAcceptable,
        'understandable': understandable,
        'result': passed ? 'PASS' : 'FAIL',
        'feedback': feedback,
      };
}

class SpeakingTurn {
  const SpeakingTurn({
    required this.aiMessage,
    required this.isFinal,
    required this.evaluations,
  });

  final String aiMessage;
  final bool isFinal;
  final List<SpeakingEvaluation> evaluations;

  factory SpeakingTurn.fromJson(Map<String, dynamic> json) => SpeakingTurn(
        aiMessage: json['aiMessage'] as String? ?? '',
        isFinal: json['isFinal'] as bool? ?? false,
        evaluations: (json['evaluations'] as List<dynamic>? ?? const [])
            .map((e) => SpeakingEvaluation.fromJson(e as Map<String, dynamic>))
            .toList(),
      );

  Map<String, dynamic> toJson() => {
        'aiMessage': aiMessage,
        'isFinal': isFinal,
        'evaluations': evaluations.map((e) => e.toJson()).toList(),
      };
}

/// What happened to one word at the end of a session.
class WordOutcome {
  const WordOutcome({
    required this.wordId,
    required this.text,
    required this.meaning,
    required this.passed,
    required this.newStatus,
    required this.nextSkill,
    required this.nextEligibleAt,
    required this.becameActive,
    this.firstAttemptCorrect = false,
    this.attemptsInSession = 1,
    this.feedback,
    this.evidence,
    this.better,
  });

  final String wordId;
  final String text;
  final String meaning;
  final bool passed;
  final SkillStatus newStatus;
  final SkillType? nextSkill;
  final DateTime? nextEligibleAt;
  final bool becameActive;

  /// What the conversation showed about this word, addressed to the learner.
  ///
  /// Speaking only, and the point of the result screen there: a learner told
  /// only that a word failed has learned that they failed and nothing else
  /// (ADR-048).
  final String? feedback;

  /// Their own words containing it, quoted back.
  final String? evidence;

  /// One English sentence using the word well — theirs repaired, or a model.
  final String? better;

  /// Only a first-attempt success passes the skill (demo review §31). A word
  /// answered correctly on a later attempt is still reinforced in-session, but
  /// it is rescheduled rather than marked passed.
  final bool firstAttemptCorrect;

  final int attemptsInSession;

  factory WordOutcome.fromJson(Map<String, dynamic> json) => WordOutcome(
        wordId: json['wordId'] as String,
        text: json['text'] as String? ?? '',
        meaning: json['meaning'] as String? ?? '',
        passed: json['passed'] as bool? ?? false,
        newStatus: SkillStatus.fromWire(json['newStatus'] as String?),
        nextSkill: json['nextSkill'] == null
            ? null
            : SkillType.fromWire(json['nextSkill'] as String),
        nextEligibleAt:
            DateTime.tryParse(json['nextEligibleAt'] as String? ?? '')?.toUtc(),
        becameActive: json['becameActive'] as bool? ?? false,
        firstAttemptCorrect: json['firstAttemptCorrect'] as bool? ?? false,
        attemptsInSession: (json['attemptsInSession'] as num?)?.toInt() ?? 1,
        feedback: json['feedback'] as String?,
        evidence: json['evidence'] as String?,
        better: json['better'] as String?,
      );

  Map<String, dynamic> toJson() => {
        'wordId': wordId,
        'text': text,
        'meaning': meaning,
        'passed': passed,
        'newStatus': newStatus.wire,
        'nextSkill': nextSkill?.wire,
        'nextEligibleAt': nextEligibleAt?.toIso8601String(),
        'becameActive': becameActive,
        'firstAttemptCorrect': firstAttemptCorrect,
        'attemptsInSession': attemptsInSession,
      };
}

class SessionResult {
  const SessionResult({
    required this.sessionId,
    required this.skill,
    required this.comprehensionCorrect,
    required this.comprehensionTotal,
    required this.words,
    required this.durationMs,
    this.summary,
  });

  final String sessionId;
  final SkillType skill;
  final int comprehensionCorrect;
  final int comprehensionTotal;
  final List<WordOutcome> words;
  final int durationMs;

  /// How the whole conversation went. Speaking only.
  final String? summary;

  int get passedCount => words.where((w) => w.passed).length;

  factory SessionResult.fromJson(Map<String, dynamic> json) {
    final comprehension =
        json['comprehension'] as Map<String, dynamic>? ?? const {};
    return SessionResult(
      sessionId: json['sessionId'] as String,
      skill: SkillType.fromWire(json['skill'] as String?),
      comprehensionCorrect: (comprehension['correct'] as num?)?.toInt() ?? 0,
      comprehensionTotal: (comprehension['total'] as num?)?.toInt() ?? 0,
      words: (json['words'] as List<dynamic>? ?? const [])
          .map((e) => WordOutcome.fromJson(e as Map<String, dynamic>))
          .toList(),
      durationMs: (json['durationMs'] as num?)?.toInt() ?? 0,
      summary: json['summary'] as String?,
    );
  }

  Map<String, dynamic> toJson() => {
        'sessionId': sessionId,
        'skill': skill.wire,
        'comprehension': {
          'correct': comprehensionCorrect,
          'total': comprehensionTotal,
        },
        'words': words.map((e) => e.toJson()).toList(),
        'durationMs': durationMs,
      };
}
