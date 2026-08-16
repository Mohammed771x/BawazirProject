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
  const WordPage({required this.items, required this.total});

  final List<Word> items;
  final int total;

  factory WordPage.fromJson(Map<String, dynamic> json) => WordPage(
        items: (json['items'] as List<dynamic>? ?? const [])
            .map((e) => Word.fromJson(e as Map<String, dynamic>))
            .toList(),
        total: (json['total'] as num?)?.toInt() ?? 0,
      );

  Map<String, dynamic> toJson() => {
        'items': items.map((e) => e.toJson()).toList(),
        'total': total,
      };
}
