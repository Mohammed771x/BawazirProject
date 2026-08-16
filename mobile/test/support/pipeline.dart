import 'package:wordos/core/models/models.dart';
import 'package:wordos/mock_backend/engine/mock_engine.dart';

/// Walks a word up to [skill] through the mock engine.
///
/// A precondition helper, not a test: driving the whole pipeline through the UI
/// is what the integration journeys are for. Here it exists so a test about one
/// screen can start with a word already waiting at that screen.
///
/// Only the **target word** questions are answered correctly, which is all the
/// pipeline needs — comprehension answers are not revealed until an attempt is
/// made, and they decide the content level rather than the word's fate.
void advanceToSkill(MockEngine engine, MockUser user, SkillType skill) {
  for (final current in MockEngine.configuration.skillsOrder) {
    if (current == skill) return;

    final session = engine.startSession(user, current);

    if (current == SkillType.speaking) {
      engine.submitSpeakingTurn(
        user,
        session.id,
        session.targetWords
            .map((w) => 'I use ${w.text} often in my daily study routine.')
            .join(' '),
      );
    } else {
      String? currentId = session.items.first.id;
      while (currentId != null) {
        final item = session.items.firstWhere((i) => i.id == currentId);
        final word = item.wordId == null
            ? null
            : session.targetWords.firstWhere((w) => w.wordId == item.wordId);

        final SessionProgress progress;
        switch (item.type) {
          case SessionItemType.writingTask:
            progress = engine
                .submitWriting(user, session.id, item.id,
                    'The ${word!.text} helped me finish my project on time.')
                .progress;
          case SessionItemType.spellingTask:
            progress = engine
                .submitAnswer(user, session.id, item.id, word!.text)
                .progress;
          case SessionItemType.targetWord:
            progress = engine
                .submitAnswer(user, session.id, item.id, word!.meaning)
                .progress;
          default:
            progress = engine
                .submitAnswer(user, session.id, item.id, item.options.first)
                .progress;
        }
        currentId = progress.nextItemId;
      }
    }

    engine.completeSession(user, session.id);

    // Cross the spaced gap. The gap has its own tests; here it is only in the
    // way of reaching the skill under test.
    engine.advanceClock(
      Duration(days: MockEngine.configuration.skillIntervalDays),
    );
  }
}
