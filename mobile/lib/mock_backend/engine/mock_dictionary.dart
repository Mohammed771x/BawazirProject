import '../../core/models/models.dart';

/// A small offline "dictionary + AI word analysis" used by the mock backend.
///
/// DISPOSABLE — replaced in Phase 6 by the Python AI service (`/ai/word-analysis`).
class MockDictionary {
  const MockDictionary._();

  static const Map<String, List<WordCandidate>> entries = {
    'book': [
      WordCandidate(
        text: 'book',
        meaning: 'كتاب',
        definitionEn: 'a set of printed pages held together in a cover',
        partOfSpeech: 'noun',
        suggestedLevel: CefrLevel.a1,
        isSpellingSuggestion: false,
      ),
      WordCandidate(
        text: 'book',
        meaning: 'يحجز',
        definitionEn: 'to arrange to have a seat, room or ticket kept for you',
        partOfSpeech: 'verb',
        suggestedLevel: CefrLevel.a2,
        isSpellingSuggestion: false,
      ),
    ],
    'operating system': [
      WordCandidate(
        text: 'operating system',
        meaning: 'نظام تشغيل',
        definitionEn:
            'software that manages a computer\'s hardware and software resources',
        partOfSpeech: 'noun',
        suggestedLevel: CefrLevel.b1,
        isSpellingSuggestion: false,
      ),
    ],
    'interface': [
      WordCandidate(
        text: 'interface',
        meaning: 'واجهة',
        definitionEn: 'the point where two systems meet and interact',
        partOfSpeech: 'noun',
        suggestedLevel: CefrLevel.b1,
        isSpellingSuggestion: false,
      ),
    ],
    'hardware': [
      WordCandidate(
        text: 'hardware',
        meaning: 'العتاد / المكونات المادية',
        definitionEn: 'the physical parts of a computer',
        partOfSpeech: 'noun',
        suggestedLevel: CefrLevel.a2Plus,
        isSpellingSuggestion: false,
      ),
    ],
    'software': [
      WordCandidate(
        text: 'software',
        meaning: 'برمجيات',
        definitionEn: 'the programs that run on a computer',
        partOfSpeech: 'noun',
        suggestedLevel: CefrLevel.a2Plus,
        isSpellingSuggestion: false,
      ),
    ],
    'achieve': [
      WordCandidate(
        text: 'achieve',
        meaning: 'يُحقّق',
        definitionEn: 'to succeed in doing something after effort',
        partOfSpeech: 'verb',
        suggestedLevel: CefrLevel.b1,
        isSpellingSuggestion: false,
      ),
    ],
    'research': [
      WordCandidate(
        text: 'research',
        meaning: 'بحث علمي',
        definitionEn: 'careful study to discover new facts',
        partOfSpeech: 'noun',
        suggestedLevel: CefrLevel.b1,
        isSpellingSuggestion: false,
      ),
    ],
    'significant': [
      WordCandidate(
        text: 'significant',
        meaning: 'مُهِم / جوهري',
        definitionEn: 'large or important enough to have an effect',
        partOfSpeech: 'adjective',
        suggestedLevel: CefrLevel.b2,
        isSpellingSuggestion: false,
      ),
    ],
    'environment': [
      WordCandidate(
        text: 'environment',
        meaning: 'بيئة',
        definitionEn: 'the conditions in which someone or something exists',
        partOfSpeech: 'noun',
        suggestedLevel: CefrLevel.b1,
        isSpellingSuggestion: false,
      ),
    ],
    'allocate': [
      WordCandidate(
        text: 'allocate',
        meaning: 'يُخصّص',
        definitionEn: 'to give something to someone for a particular purpose',
        partOfSpeech: 'verb',
        suggestedLevel: CefrLevel.b2,
        isSpellingSuggestion: false,
      ),
    ],
    'estimate': [
      WordCandidate(
        text: 'estimate',
        meaning: 'يُقدّر',
        definitionEn: 'to judge the value or size of something approximately',
        partOfSpeech: 'verb',
        suggestedLevel: CefrLevel.b1Plus,
        isSpellingSuggestion: false,
      ),
    ],
    'reliable': [
      WordCandidate(
        text: 'reliable',
        meaning: 'موثوق',
        definitionEn: 'able to be trusted to do what is expected',
        partOfSpeech: 'adjective',
        suggestedLevel: CefrLevel.b1Plus,
        isSpellingSuggestion: false,
      ),
    ],
    'evidence': [
      WordCandidate(
        text: 'evidence',
        meaning: 'دليل',
        definitionEn: 'facts that show whether something is true',
        partOfSpeech: 'noun',
        suggestedLevel: CefrLevel.b1Plus,
        isSpellingSuggestion: false,
      ),
    ],
    'schedule': [
      WordCandidate(
        text: 'schedule',
        meaning: 'جدول زمني',
        definitionEn: 'a plan of activities with the times they will happen',
        partOfSpeech: 'noun',
        suggestedLevel: CefrLevel.b1,
        isSpellingSuggestion: false,
      ),
    ],
    'improve': [
      WordCandidate(
        text: 'improve',
        meaning: 'يُحسّن',
        definitionEn: 'to make something better than before',
        partOfSpeech: 'verb',
        suggestedLevel: CefrLevel.a2,
        isSpellingSuggestion: false,
      ),
    ],
  };

  /// Distractor pool for building multiple-choice options.
  static const List<String> distractorMeanings = [
    'لوحة مفاتيح',
    'شبكة الإنترنت',
    'برنامج للرسم',
    'مكتبة عامة',
    'مطار دولي',
    'قاعدة بيانات',
    'متصفح',
    'كرة',
    'وجبة خفيفة',
    'رحلة قصيرة',
    'سيارة أجرة',
    'ملعب رياضي',
  ];

  /// English synonyms, used as a spelling clue at lower levels
  /// (`MVP Core.txt` §33). Absent entries fall back to another clue kind.
  static const Map<String, String> synonyms = {
    'achieve': 'accomplish',
    'allocate': 'assign',
    'estimate': 'approximate',
    'evidence': 'proof',
    'improve': 'get better',
    'reliable': 'dependable',
    'research': 'investigation',
    'schedule': 'timetable',
    'significant': 'important',
    'hardware': 'physical parts',
    'software': 'programs',
    'interface': 'meeting point',
  };

  static String? synonymFor(String word) => synonyms[word.trim().toLowerCase()];

  /// Stable identity of one *sense*.
  ///
  /// In the real lexicon this is the WordNet synset id that joins the English
  /// sense to its Arabic gloss and CEFR level (ADR-012). Here it is derived
  /// deterministically from the same parts, so the client and the validation
  /// path agree on what identifies a vocabulary item.
  static String senseIdFor({
    required String text,
    required String partOfSpeech,
    required String meaning,
  }) =>
      'sense:${text.trim().toLowerCase()}'
      ':${partOfSpeech.trim().toLowerCase()}'
      ':${meaning.trim()}';

  static WordCandidate _withSenseId(
    WordCandidate c, {
    bool isSpellingSuggestion = false,
  }) =>
      WordCandidate(
        text: c.text,
        meaning: c.meaning,
        definitionEn: c.definitionEn,
        partOfSpeech: c.partOfSpeech,
        suggestedLevel: c.suggestedLevel,
        isSpellingSuggestion: isSpellingSuggestion,
        senseId: senseIdFor(
          text: c.text,
          partOfSpeech: c.partOfSpeech,
          meaning: c.meaning,
        ),
      );

  /// Prefix search for the Add Word field.
  ///
  /// Typing `bo` returns every sense whose word starts with those letters, each
  /// row carrying the word, its CEFR level and the Arabic meaning of *that*
  /// sense — so the learner picks a word **and** a meaning in one action
  /// (product decision §2, 2026-08-15).
  static List<WordCandidate> lookup(String rawQuery) {
    final query = rawQuery.trim().toLowerCase();
    if (query.isEmpty) return const [];

    // Exact matches first, then other words that start with the query, so a
    // complete word is never buried under its own prefixes.
    final matches = <WordCandidate>[
      ...?entries[query],
      for (final entry in entries.entries)
        if (entry.key != query && entry.key.startsWith(query)) ...entry.value,
    ];
    if (matches.isNotEmpty) {
      return matches.map((c) => _withSenseId(c)).toList();
    }

    // Spelling help: nearest known entry within a small edit distance.
    final suggestions = entries.entries
        .map((e) => (key: e.key, distance: _editDistance(query, e.key)))
        .where((e) => e.distance <= 2)
        .toList()
      ..sort((a, b) => a.distance.compareTo(b.distance));

    // Nothing is invented here. If the string is not a word we know, the
    // learner gets spelling suggestions and nothing else — there is deliberately
    // no "add it anyway with your own meaning" path, because that is how `hch`
    // and mistyped or mis-defined entries get into a learner's vocabulary
    // (demo review §16–17, ADR-012).
    return [
      for (final s in suggestions.take(2))
        ...entries[s.key]!.map(
          (c) => _withSenseId(c, isSpellingSuggestion: true),
        ),
    ];
  }

  /// Resolves a candidate the client submitted back to the lexicon row it
  /// claims to be.
  ///
  /// The server re-checks this on every write: a client could post a
  /// hand-assembled candidate that never came from a lookup, so nothing in the
  /// request body is trusted except as a *lookup key*. The returned entry — not
  /// the request — is what gets stored, which is why a forged level or
  /// definition cannot get in either.
  static WordCandidate? resolve({
    required String text,
    required String meaning,
    String? senseId,
  }) {
    final rows = entries[text.trim().toLowerCase()] ?? const <WordCandidate>[];
    for (final row in rows) {
      final resolved = _withSenseId(row);
      if (senseId != null && senseId.isNotEmpty) {
        if (resolved.senseId == senseId) return resolved;
        continue;
      }
      if (row.meaning.trim() == meaning.trim()) return resolved;
    }
    return null;
  }

  static int _editDistance(String a, String b) {
    final rows = List.generate(
      a.length + 1,
      (i) => List<int>.filled(b.length + 1, 0),
    );
    for (var i = 0; i <= a.length; i++) {
      rows[i][0] = i;
    }
    for (var j = 0; j <= b.length; j++) {
      rows[0][j] = j;
    }
    for (var i = 1; i <= a.length; i++) {
      for (var j = 1; j <= b.length; j++) {
        final cost = a[i - 1] == b[j - 1] ? 0 : 1;
        rows[i][j] = [
          rows[i - 1][j] + 1,
          rows[i][j - 1] + 1,
          rows[i - 1][j - 1] + cost,
        ].reduce((x, y) => x < y ? x : y);
      }
    }
    return rows[a.length][b.length];
  }
}
