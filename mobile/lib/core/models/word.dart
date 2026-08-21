import 'enums.dart';

/// A candidate returned by the word lookup — the user picks the *intended*
/// meaning before the word is created, because `book = كتاب` and `book = يحجز`
/// are two different learning journeys.
/// One selectable entry from the lexicon: an English word **in one specific
/// sense**, with the Arabic meaning of that sense and its CEFR level.
///
/// [senseId] is the join key across the three sources the lexicon is built from
/// (ADR-012): a WordNet synset id ties the English sense to its Arabic gloss,
/// and the CEFR level is attached to the same row. It is the stable identity —
/// `book` alone is not, because `book = كتاب` and `book = يحجز` are different
/// senses and therefore different vocabulary items.
class WordCandidate {
  const WordCandidate({
    required this.text,
    required this.meaning,
    required this.definitionEn,
    required this.partOfSpeech,
    required this.suggestedLevel,
    required this.isSpellingSuggestion,
    this.senseId,
  });

  final String text;
  final String meaning;
  final String definitionEn;
  final String partOfSpeech;
  final CefrLevel suggestedLevel;
  final bool isSpellingSuggestion;

  /// Lexicon sense identifier. Null only for entries that predate the synset
  /// mapping; the server falls back to `(text, meaning)` in that case.
  final String? senseId;

  factory WordCandidate.fromJson(Map<String, dynamic> json) => WordCandidate(
        text: json['text'] as String,
        meaning: json['meaning'] as String? ?? '',
        definitionEn: json['definitionEn'] as String? ?? '',
        partOfSpeech: json['partOfSpeech'] as String? ?? '',
        suggestedLevel: CefrLevel.fromWire(json['suggestedLevel'] as String?),
        isSpellingSuggestion: json['isSpellingSuggestion'] as bool? ?? false,
        senseId: json['senseId'] as String?,
      );

  Map<String, dynamic> toJson() => {
        'text': text,
        'meaning': meaning,
        'definitionEn': definitionEn,
        'partOfSpeech': partOfSpeech,
        'suggestedLevel': suggestedLevel.wire,
        'isSpellingSuggestion': isSpellingSuggestion,
        'senseId': senseId,
      };
}

/// What a word tapped inside a passage turns out to be (Part 2 §17).
///
/// [matchedText] is the spelling the lexicon actually answered on, which is not
/// always what was tapped: the passage says "researching", the entry says
/// "research". Showing both is the difference between a definition and a
/// non-sequitur.
///
/// An empty [senses] list is an ordinary answer, not a failure — proper nouns
/// and numbers appear in generated text and simply have no entry.
class WordDefinition {
  const WordDefinition({
    required this.query,
    required this.matchedText,
    required this.senses,
  });

  final String query;
  final String? matchedText;
  final List<WordCandidate> senses;

  bool get isEmpty => senses.isEmpty;

  factory WordDefinition.fromJson(Map<String, dynamic> json) => WordDefinition(
        query: json['query'] as String? ?? '',
        matchedText: json['matchedText'] as String?,
        senses: (json['senses'] as List<dynamic>? ?? [])
            .map((e) => WordCandidate.fromJson(e as Map<String, dynamic>))
            .toList(),
      );
}

class WordSkillState {
  const WordSkillState({
    required this.skill,
    required this.status,
    required this.availableAt,
    required this.attempts,
  });

  final SkillType skill;
  final SkillStatus status;
  final DateTime? availableAt;
  final int attempts;

  factory WordSkillState.fromJson(Map<String, dynamic> json) => WordSkillState(
        skill: SkillType.fromWire(json['skill'] as String?),
        status: SkillStatus.fromWire(json['status'] as String?),
        availableAt: DateTime.tryParse(json['availableAt'] as String? ?? '')?.toUtc(),
        attempts: (json['attempts'] as num?)?.toInt() ?? 0,
      );

  Map<String, dynamic> toJson() => {
        'skill': skill.wire,
        'status': status.wire,
        'availableAt': availableAt?.toIso8601String(),
        'attempts': attempts,
      };

  WordSkillState copyWith({
    SkillStatus? status,
    DateTime? availableAt,
    int? attempts,
  }) =>
      WordSkillState(
        skill: skill,
        status: status ?? this.status,
        availableAt: availableAt ?? this.availableAt,
        attempts: attempts ?? this.attempts,
      );
}

class Word {
  const Word({
    required this.id,
    required this.text,
    required this.meaning,
    required this.definitionEn,
    required this.partOfSpeech,
    required this.cefrLevel,
    required this.state,
    this.form,
    required this.currentSkill,
    required this.addedAt,
    required this.nextEligibleAt,
    required this.exposureCount,
    required this.skills,
  });

  final String id;
  final String text;
  final String meaning;
  final String definitionEn;
  final String partOfSpeech;

  /// Which form of the word this entry is — `past`, `pastParticiple`, `ing`,
  /// `plural` — or null when it is the word itself (ADR-056).
  ///
  /// A key, not a sentence: the learner reads it in their own language, and
  /// the server does not know which that is.
  final String? form;

  final CefrLevel cefrLevel;
  final WordState state;
  final SkillType? currentSkill;
  final DateTime addedAt;
  final DateTime? nextEligibleAt;
  final int exposureCount;
  final List<WordSkillState> skills;

  WordSkillState skillState(SkillType skill) =>
      skills.firstWhere((s) => s.skill == skill);

  int get passedSkillCount =>
      skills.where((s) => s.status == SkillStatus.passed).length;

  factory Word.fromJson(Map<String, dynamic> json) => Word(
        id: json['id'] as String,
        text: json['text'] as String,
        meaning: json['meaning'] as String? ?? '',
        definitionEn: json['definitionEn'] as String? ?? '',
        partOfSpeech: json['partOfSpeech'] as String? ?? '',
        form: json['form'] as String?,
        cefrLevel: CefrLevel.fromWire(json['cefrLevel'] as String?),
        state: WordState.fromWire(json['state'] as String?),
        currentSkill: json['currentSkill'] == null
            ? null
            : SkillType.fromWire(json['currentSkill'] as String),
        addedAt: DateTime.tryParse(json['addedAt'] as String? ?? '')?.toUtc() ??
            DateTime.now().toUtc(),
        nextEligibleAt:
            DateTime.tryParse(json['nextEligibleAt'] as String? ?? '')?.toUtc(),
        exposureCount: (json['exposureCount'] as num?)?.toInt() ?? 0,
        skills: (json['skills'] as List<dynamic>? ?? const [])
            .map((e) => WordSkillState.fromJson(e as Map<String, dynamic>))
            .toList(),
      );

  Map<String, dynamic> toJson() => {
        'id': id,
        'text': text,
        'meaning': meaning,
        'definitionEn': definitionEn,
        'partOfSpeech': partOfSpeech,
        'form': form,
        'cefrLevel': cefrLevel.wire,
        'state': state.wire,
        'currentSkill': currentSkill?.wire,
        'addedAt': addedAt.toIso8601String(),
        'nextEligibleAt': nextEligibleAt?.toIso8601String(),
        'exposureCount': exposureCount,
        'skills': skills.map((e) => e.toJson()).toList(),
      };
}

class WordEvent {
  const WordEvent({
    required this.type,
    required this.skill,
    required this.createdAt,
    this.note,
  });

  final WordEventType type;
  final SkillType? skill;
  final DateTime createdAt;
  final String? note;

  factory WordEvent.fromJson(Map<String, dynamic> json) => WordEvent(
        type: WordEventType.fromWire(json['type'] as String?),
        skill: json['skill'] == null
            ? null
            : SkillType.fromWire(json['skill'] as String),
        createdAt:
            DateTime.tryParse(json['createdAt'] as String? ?? '')?.toUtc() ??
                DateTime.now().toUtc(),
        note: json['note'] as String?,
      );

  Map<String, dynamic> toJson() => {
        'type': type.wire,
        'skill': skill?.wire,
        'createdAt': createdAt.toIso8601String(),
        'note': note,
      };
}

class WordDetail {
  const WordDetail({required this.word, required this.events});

  final Word word;
  final List<WordEvent> events;

  factory WordDetail.fromJson(Map<String, dynamic> json) => WordDetail(
        word: Word.fromJson(json),
        events: (json['events'] as List<dynamic>? ?? const [])
            .map((e) => WordEvent.fromJson(e as Map<String, dynamic>))
            .toList(),
      );

  Map<String, dynamic> toJson() => {
        ...word.toJson(),
        'events': events.map((e) => e.toJson()).toList(),
      };
}

class WordPage {
  const WordPage({
    required this.items,
    required this.total,
    this.page = 0,
    this.hasMore = false,
  });

  final List<Word> items;

  /// How many words match, not how many are on this page — the count the
  /// learner is shown.
  final int total;

  final int page;
  final bool hasMore;

  factory WordPage.fromJson(Map<String, dynamic> json) => WordPage(
        items: (json['items'] as List<dynamic>? ?? const [])
            .map((e) => Word.fromJson(e as Map<String, dynamic>))
            .toList(),
        total: (json['total'] as num?)?.toInt() ?? 0,
        page: (json['page'] as num?)?.toInt() ?? 0,
        hasMore: json['hasMore'] as bool? ?? false,
      );

  Map<String, dynamic> toJson() => {
        'items': items.map((e) => e.toJson()).toList(),
        'total': total,
        'page': page,
        'hasMore': hasMore,
      };
}
