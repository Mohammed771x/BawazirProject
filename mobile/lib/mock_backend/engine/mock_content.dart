import 'dart:math';

import '../../core/models/models.dart';
import 'mock_dictionary.dart';

/// Simulates the Python AI service's content generation (Phase 6 replaces this).
///
/// It produces the same *shape* the real service must return, so swapping in the
/// real generator is a change of source, not of contract. Options are shuffled
/// here so the client can never rely on their order (rule R7).
class GeneratedSession {
  GeneratedSession({
    required this.content,
    required this.items,
    required this.correctAnswers,
  });

  final SessionContent? content;
  final List<SessionItem> items;

  /// itemId → correct answer, kept server-side only.
  final Map<String, String> correctAnswers;
}

class MockContentGenerator {
  MockContentGenerator([Random? random]) : _random = random ?? Random();

  final Random _random;
  int _seq = 0;

  /// Comprehension questions per Reading/Listening session. Fixed at five by
  /// the product spec (demo review §24, §32.1).
  static const int comprehensionQuestionCount = 5;

  String _id(String prefix) =>
      '${prefix}_${DateTime.now().microsecondsSinceEpoch}_${_seq++}';

  static const List<String> _learners = ['Nora', 'Ahmed', 'Layla', 'Omar', 'Sara'];

  static const Map<String, String> _topicLabels = {
    'technology': 'technology',
    'programming': 'programming',
    'ai': 'artificial intelligence',
    'football': 'football',
    'business': 'business',
    'entrepreneurship': 'entrepreneurship',
    'economics': 'economics',
    'medicine': 'medicine',
    'travel': 'travel',
    'history': 'history',
    'science': 'science',
  };

  String _topicFor(List<String> interests) {
    if (interests.isEmpty) return 'her studies';
    final pick = interests[_random.nextInt(interests.length)];
    return _topicLabels[pick] ?? pick;
  }

  String _meaningOf(SessionTargetWord w) =>
      w.meaning.trim().isEmpty ? '—' : w.meaning.trim();

  List<String> _optionsFor(String correct, List<String> otherMeanings) {
    final pool = <String>{...otherMeanings, ...MockDictionary.distractorMeanings}
      ..remove(correct);
    final distractors = pool.toList()..shuffle(_random);
    final options = [correct, ...distractors.take(3)]..shuffle(_random);
    return options;
  }

  // ── Reading & Listening ────────────────────────────────────────────────────

  /// Builds the passage (or audio script) plus five comprehension questions and
  /// one context question per target word.
  ///
  /// The two skills share a generator because they share a *learning rhythm*,
  /// but they differ where it matters: Reading shows the target word's
  /// surrounding sentences as text; Listening speaks that same sentence and
  /// shows nothing, so it stays a listening task rather than reading with audio
  /// in the background (demo review §34).
  GeneratedSession buildComprehension({
    required List<SessionTargetWord> words,
    required List<String> definitions,
    required List<String> interests,
    required bool listening,
  }) {
    final learner = _learners[_random.nextInt(_learners.length)];
    final topic = _topicFor(interests);

    // The passage is assembled as a list of sentences so the neighbours of each
    // target word can be handed to the context question exactly.
    final sentences = <String>[];
    final sentenceIndexOfWord = <String, int>{};

    sentences.add('$learner is a student who is interested in $topic.');
    sentences.add(
      'Last week $learner joined a small study group at her university.',
    );

    for (var i = 0; i < words.length; i++) {
      final word = words[i];
      final definition = definitions[i].isEmpty
          ? 'an important idea in this field'
          : definitions[i];
      final lead = i == 0
          ? 'During the first meeting the teacher explained that'
          : 'Then the group discussed';
      sentenceIndexOfWord[word.wordId] = sentences.length;
      sentences.add('$lead ${word.text}, which is $definition.');
      sentences.add(
        i.isEven
            ? 'Everyone in the group wrote that down carefully.'
            : '$learner asked a question about it before they moved on.',
      );
    }

    sentences.add(
      'At the end of the meeting $learner wrote all the new terms in her notebook.',
    );

    final text = sentences.join(' ');

    // Character offsets for highlighting, derived from the assembled text so
    // they cannot drift out of sync with it.
    final spans = <TargetSpan>[];
    for (final word in words) {
      final index = text.indexOf(word.text);
      if (index >= 0) {
        spans.add(TargetSpan(
          wordId: word.wordId,
          start: index,
          length: word.text.length,
        ));
      }
    }

    final items = <SessionItem>[];
    final correct = <String, String>{};

    void addComprehension(
      String prompt,
      String answer,
      List<String> distractors,
    ) {
      final id = _id('it');
      items.add(
        SessionItem(
          id: id,
          type: SessionItemType.comprehension,
          wordId: null,
          prompt: prompt,
          options: [answer, ...distractors]..shuffle(_random),
          context: null,
          clue: null,
          clueKind: null,
          letters: const [],
          inputMode: null,
        ),
      );
      correct[id] = answer;
    }

    // Exactly five, always — the learner's sense of the session's shape depends
    // on it being predictable.
    addComprehension(
      'Where did $learner join the study group?',
      'At her university',
      ['At a hospital', 'At an airport', 'In a factory'],
    );
    addComprehension(
      'What is $learner interested in?',
      topic,
      _topicLabels.values.where((t) => t != topic).take(3).toList(),
    );
    addComprehension(
      'When did $learner join the group?',
      'Last week',
      ['Last year', 'This morning', 'Two months ago'],
    );
    addComprehension(
      'What did $learner do at the end of the meeting?',
      'She wrote the new terms in her notebook',
      [
        'She left without taking notes',
        'She taught the group herself',
        'She cancelled the next meeting',
      ],
    );
    addComprehension(
      'Who explained the new terms first?',
      'The teacher',
      ['Another student', 'A visitor', 'Nobody did'],
    );

    assert(items.length == comprehensionQuestionCount);

    // One context question per target word.
    for (final word in words) {
      final meaning = _meaningOf(word);
      final others = words.map(_meaningOf).where((m) => m != meaning).toList();
      final index = sentenceIndexOfWord[word.wordId];
      final sentence = index == null ? '' : sentences[index];
      final before = index == null || index == 0 ? null : sentences[index - 1];
      final after = index == null || index + 1 >= sentences.length
          ? null
          : sentences[index + 1];

      final id = _id('it');
      items.add(
        SessionItem(
          id: id,
          type: SessionItemType.targetWord,
          wordId: word.wordId,
          // The question is about *this* use of the word, not the dictionary
          // entry — the learner is practising inference, not recall.
          prompt: 'What does "${word.text}" mean here?',
          options: _optionsFor(meaning, others),
          context: listening
              ? null
              : WordContext(before: before, sentence: sentence, after: after),
          // Listening hears the same three sentences and sees none of them.
          audioText: listening
              ? [before, sentence, after].whereType<String>().join(' ')
              : null,
          clue: null,
          clueKind: null,
          letters: const [],
          inputMode: null,
        ),
      );
      correct[id] = meaning;
    }

    return GeneratedSession(
      content: SessionContent(
        text: text,
        targetSpans: spans,
        revealTextAfterTest: listening,
        // The real generator glosses every content word while it writes. This
        // stand-in glosses what it can: the target words, whose meanings it
        // knows, plus a handful of common words the passage always contains.
        glossary: [
          for (final word in words)
            GlossaryEntry(
              word: word.text,
              meaning: word.meaning,
              partOfSpeech: 'noun',
            ),
          const GlossaryEntry(
              word: 'student', meaning: 'طالب', partOfSpeech: 'noun'),
          const GlossaryEntry(
              word: 'the', meaning: 'أداة تعريف', partOfSpeech: 'determiner'),
        ],
        canChangeLevel: true,
      ),
      items: items,
      correctAnswers: correct,
    );
  }

  // ── Writing ────────────────────────────────────────────────────────────────

  /// One task per target word, and the task is always *use this word*.
  ///
  /// Deliberately not "write about football": an unrelated topic tests writing
  /// in general, while the point of this skill in WordOS is whether the learner
  /// can produce **this word** correctly (demo review §41–43).
  GeneratedSession buildWriting({
    required List<SessionTargetWord> words,
    required List<String> interests,
  }) {
    final items = <SessionItem>[];
    final correct = <String, String>{};

    for (var i = 0; i < words.length; i++) {
      final word = words[i];
      final id = _id('it');
      // Alternate between a bare prompt and one that pushes for a personal
      // context, so a session is not five identical instructions.
      final promptKey = i.isEven
          ? SessionPromptKey.writeASentence
          : SessionPromptKey.writeASentenceAboutYourself;
      // The English text is still sent, as the fallback for a client that does
      // not know the key; what the learner sees is said in their own language
      // (ADR-035).
      final prompt = i.isEven
          ? 'Write one sentence using "${word.text}".'
          : 'Write one sentence about your own life using "${word.text}".';
      items.add(
        SessionItem(
          id: id,
          type: SessionItemType.writingTask,
          wordId: word.wordId,
          prompt: prompt,
          promptKey: promptKey,
          options: const [],
          context: null,
          clue: _meaningOf(word),
          clueKind: SpellingClueKind.arabicMeaning,
          letters: const [],
          inputMode: null,
        ),
      );
      correct[id] = word.text;
    }
    return GeneratedSession(content: null, items: items, correctAnswers: correct);
  }

  // ── Spelling ───────────────────────────────────────────────────────────────

  /// `MVP Core.txt` §33–34: the clue and the input method both follow the
  /// learner's level. Lower levels get the Arabic meaning, a simple definition
  /// or a synonym plus shuffled letters to arrange; B2 and above get an English
  /// definition and type the word freely. A hint is always available.
  /// The word's letters plus decoys, shuffled — the same shape the real
  /// backend builds (Part 2 §36–§37). A pool holding exactly the right letters
  /// can be cleared by using every tile, which is not spelling.
  List<String> _letterPool(String word) {
    final letters = word.replaceAll(' ', '').toLowerCase().split('');
    final decoyCount = (letters.length ~/ 2).clamp(3, 6);

    final used = letters.toSet();
    final available = 'abcdefghijklmnopqrstuvwxyz'
        .split('')
        .where((c) => !used.contains(c))
        .toList()
      ..shuffle(_random);

    return [...letters, ...available.take(decoyCount)]..shuffle(_random);
  }

  GeneratedSession buildSpelling({
    required List<SessionTargetWord> words,
    required List<String> definitions,
    required CefrLevel level,
    required SpellingInputMode? preferredMode,
  }) {
    final advanced = level.rank >= CefrLevel.b2.rank;
    // Placement measures whether the learner needs letter support; it can only
    // make the task *easier* than the level implies, never harder (ADR-008).
    final useTiles = preferredMode == SpellingInputMode.letterTiles || !advanced;

    final items = <SessionItem>[];
    final correct = <String, String>{};

    for (var i = 0; i < words.length; i++) {
      final word = words[i];
      final id = _id('it');
      final definition = definitions[i];
      final synonym = MockDictionary.synonymFor(word.text);

      final ladder = _hintLadder(word, definition, synonym, level);
      final letters = _letterPool(word.text);

      items.add(
        SessionItem(
          id: id,
          type: SessionItemType.spellingTask,
          wordId: word.wordId,
          prompt: 'Write the word',
          promptKey: SessionPromptKey.writeTheWord,
          options: const [],
          context: null,
          // The first rung is what the learner sees before asking for
          // anything; the rest arrive one press at a time.
          clue: ladder.first.text,
          clueKind: ladder.first.kind,
          hints: ladder,
          letters: useTiles ? letters : const [],
          inputMode: useTiles
              ? SpellingInputMode.letterTiles
              : SpellingInputMode.freeTyping,
        ),
      );
      correct[id] = word.text;
    }
    return GeneratedSession(content: null, items: items, correctAnswers: correct);
  }

  /// The hint ladder for one word, from where this learner joins it.
  ///
  /// Mirrors `SessionContentBuilder.BuildHintLadder` in the C# backend: one
  /// ladder of five rungs, each easier than the last, entered at the rung that
  /// suits the level. Deleted with the rest of `mock_backend/` in Phase 7.
  List<SpellingHint> _hintLadder(
    SessionTargetWord word,
    String definition,
    String? synonym,
    CefrLevel level,
  ) {
    final entry = switch (level.rank) {
      final r when r >= CefrLevel.c1.rank => SpellingClueKind.definitionEn,
      final r when r >= CefrLevel.b2.rank =>
        SpellingClueKind.simplifiedDefinition,
      final r when r >= CefrLevel.b1.rank => SpellingClueKind.synonym,
      _ => SpellingClueKind.arabicMeaning,
    };

    final ladder = <SpellingHint>[];
    void rung(SpellingClueKind kind, String? text) {
      // Anything above the learner's rung is skipped, and a rung with nothing
      // to say is skipped too: a press that changes nothing reads as broken.
      if (kind.index < entry.index) return;
      if (text == null || text.trim().isEmpty) return;
      ladder.add(SpellingHint(kind: kind, text: text.trim()));
    }

    rung(SpellingClueKind.definitionEn, definition);
    rung(SpellingClueKind.simplifiedDefinition, _simplifyDefinition(definition));
    rung(SpellingClueKind.synonym, synonym);
    rung(SpellingClueKind.arabicMeaning, _meaningOf(word));
    rung(SpellingClueKind.letterCount,
        '${word.text.replaceAll(' ', '').length}');

    if (ladder.length > 1 &&
        ladder[0].kind == SpellingClueKind.definitionEn &&
        ladder[1].kind == SpellingClueKind.simplifiedDefinition &&
        ladder[0].text == ladder[1].text) {
      ladder.removeAt(1);
    }

    return ladder;
  }

  /// WordNet stacks alternatives behind semicolons; the first clause is the
  /// definition and the rest is elaboration.
  static String _simplifyDefinition(String definition) {
    final text = definition.trim();
    final cut = text.indexOf(';');
    return cut > 0 ? text.substring(0, cut).trim() : text;
  }

  // ── Speaking ───────────────────────────────────────────────────────────────

  /// Opens the conversation the way a tutor would: greet the learner by name,
  /// say how much work there is, then ask a real question
  /// (demo review §36–37).
  ConversationSetup buildConversation({
    required List<SessionTargetWord> words,
    required List<String> interests,
    required String learnerName,
  }) {
    final topic = _topicFor(interests);
    final first = words.first;
    return ConversationSetup(
      opening: 'Hello $learnerName, how are you today? '
          '${_wordCountSentence(words.length)} '
          "Let's start with something easy — tell me about $topic this week. "
          'Try to use "${first.text}" in your answer.',
      // Enough turns for one per word plus room for follow-ups.
      maxTurns: words.length + 3,
    );
  }

  static String _wordCountSentence(int count) => count == 1
      ? 'We have one word to practise today.'
      : 'We have $count words to practise today.';

  /// A conversational reply that reacts to what the learner said and *then*
  /// steers to the next target word, rather than reading out a fixed quiz
  /// (demo review §39).
  String conversationReply({
    required List<SessionTargetWord> remaining,
    required List<SessionTargetWord> justUsed,
    required int turn,
  }) {
    final buffer = StringBuffer();

    if (justUsed.isNotEmpty) {
      final used = justUsed.map((w) => '"${w.text}"').join(' and ');
      buffer.write('Good — $used sounded natural there. ');
    } else if (turn > 0) {
      buffer.write('Thanks. ');
    }

    if (remaining.isEmpty) {
      buffer.write('That covers every word for today. Let me score this now.');
      return buffer.toString();
    }

    final next = remaining.first;
    const followUps = [
      'Can you say a little more about that?',
      'Why do you think that is?',
      'How would you explain that to a friend?',
      'What would you change about it?',
    ];
    buffer.write('${followUps[turn % followUps.length]} ');
    buffer.write('This time, use "${next.text}" in your answer.');
    return buffer.toString();
  }

  /// Mock Weekly Review item: recognise the meaning of a word added this period.
  ReviewItem buildReviewItem({
    required String wordId,
    required String text,
    required String meaning,
    required List<String> otherMeanings,
  }) {
    final answer = meaning.trim().isEmpty ? '—' : meaning.trim();
    return ReviewItem(
      id: _id('ri'),
      wordId: wordId,
      prompt: text,
      options: _optionsFor(answer, otherMeanings),
    );
  }
}
