using System.Text.Json;
using WordOs.Application.Abstractions;
using WordOs.Application.Sessions;
using WordOs.Domain.Common;
using WordOs.Domain.Sessions;
using WordOs.Domain.Words;

namespace WordOs.Domain.Tests;

/// <summary>
/// The four options a Reading or Listening question offers for
/// <i>what does this word mean here?</i> (ADR-084).
/// </summary>
/// <remarks>
/// One rule, and it is about what the question actually measures: a learner
/// must have to know the word.
///
/// They did not have to. Every word's wrong answers were the <b>other target
/// words' meanings</b>, so the five questions of a session shared one set of
/// five answers between them. Answer the first correctly and that meaning is
/// spent; by the fifth there is one option left that has not already been used,
/// and it can be chosen without reading the question. A learner who noticed
/// could pass the back half of every session on bookkeeping.
/// </remarks>
public class SessionOptionsTests
{
    private static readonly WordOsConfiguration Config = new();
    private static readonly DateTimeOffset T0 =
        new(2026, 9, 16, 9, 0, 0, TimeSpan.Zero);
    private static readonly Guid UserId = Guid.CreateVersion7();

    private static Word Word_(string text, string meaning) => Word.Add(
        userId: UserId,
        senseId: $"sense-{text}",
        text: text,
        meaning: meaning,
        definitionEn: $"to {text}",
        partOfSpeech: "verb",
        cefrLevel: CefrLevel.B1,
        config: Config,
        now: T0);

    /// <param name="withWritten">
    /// Whether the generator produced per-word wrong meanings. False stands in
    /// for an AI outage, where the fallback pool is all there is.
    /// </param>
    private static (SkillSession Session, List<Word> Words) Build(
        bool withWritten,
        bool listening = false,
        MeaningOptionStyle style = MeaningOptionStyle.ArabicMeaning,
        bool withEnglish = true)
    {
        var words = new List<Word>
        {
            Word_("sell", "يبيع"),
            Word_("eat", "يأكل"),
            Word_("drink", "يشرب"),
            Word_("carry", "يحمل"),
        };

        var contexts = words.Select((w, i) => new GeneratedWordContext(
            Word: w.Text,
            Before: "Before.",
            Sentence: $"He will {w.Text} it tomorrow.",
            After: "After.",
            WrongMeanings: withWritten
                ? [$"معنى خاطئ {i}أ", $"معنى خاطئ {i}ب", $"معنى خاطئ {i}ج"]
                : null,
            SimpleDefinition: withWritten && withEnglish
                ? $"to {w.Text} something, plainly put"
                : null,
            WrongDefinitions: withWritten && withEnglish
                ? [$"wrong definition {i}a", $"wrong definition {i}b",
                   $"wrong definition {i}c"]
                : null)).ToList();

        var content = new GeneratedContent(
            Text: "A passage.",
            Sentences: ["A passage."],
            Comprehension: [],
            Contexts: contexts,
            PromptVersion: "test",
            Model: "test",
            Tokens: 0,
            FromFallback: false);

        var session = SkillSession.Start(
            UserId,
            listening ? SkillType.Listening : SkillType.Reading,
            CefrLevel.B1,
            T0);

        SessionContentBuilder.BuildComprehensionItems(
            session, content, words, listening, new Random(7), style);

        return (session, words);
    }

    private static List<string> Options(SessionItem item) =>
        JsonSerializer.Deserialize<List<string>>(item.OptionsJson) ?? [];

    [Fact]
    public void A_word_question_never_offers_another_words_answer()
    {
        var (session, words) = Build(withWritten: true);
        var answers = words.Select(w => w.Meaning).ToHashSet();

        foreach (var item in session.Items
            .Where(i => i.Type == SessionItemType.TargetWord))
        {
            var wrong = Options(item)
                .Where(o => o != item.CorrectAnswer)
                .ToList();

            Assert.All(wrong, o => Assert.DoesNotContain(o, answers));
        }
    }

    [Fact]
    public void Answering_one_question_eliminates_nothing_in_the_next()
    {
        var (session, _) = Build(withWritten: true);

        var items = session.Items
            .Where(i => i.Type == SessionItemType.TargetWord)
            .ToList();

        // Walk the session the way a learner does, striking off every meaning
        // already shown to be an answer. If the options were drawn from a
        // shared pool this set would shrink each question's choices; here it
        // must never touch them.
        var known = new HashSet<string>();
        foreach (var item in items)
        {
            var remaining = Options(item)
                .Where(o => !known.Contains(o))
                .ToList();

            Assert.Equal(4, remaining.Count);
            known.Add(item.CorrectAnswer!);
        }
    }

    [Fact]
    public void Every_word_gets_four_distinct_options_including_the_answer()
    {
        foreach (var written in new[] { true, false })
        {
            var (session, _) = Build(withWritten: written);

            foreach (var item in session.Items
                .Where(i => i.Type == SessionItemType.TargetWord))
            {
                var options = Options(item);

                // Four, always. A model that skipped the field, or an outage,
                // degrades to the old pool rather than to a question with two
                // options — which is not a question.
                Assert.Equal(4, options.Count);
                Assert.Equal(4, options.Distinct().Count());
                Assert.Contains(item.CorrectAnswer, options);
            }
        }
    }

    // ── What a Listening question may say (ADR-085) ──────────────────────
    //
    // Listening is the one skill where the learner must not know how the word
    // is written: they hear it inside a sentence and say what it meant. A
    // question that prints it hands over the spelling and turns the exercise
    // into reading with audio attached.

    [Fact]
    public void A_listening_question_never_names_the_word()
    {
        var (session, words) = Build(withWritten: true, listening: true);

        foreach (var item in session.Items
            .Where(i => i.Type == SessionItemType.TargetWord))
        {
            var word = words.Single(w => w.Id == item.WordId);

            Assert.DoesNotContain(
                word.Text, item.Prompt, StringComparison.OrdinalIgnoreCase);

            // And it carries the key, so the client says it in the learner's
            // own language rather than showing this English (ADR-035).
            Assert.Equal(SessionPromptKey.ListeningWordMeaning, item.PromptKey);

            // The sentence is still spoken — the word is heard, just not seen.
            Assert.False(string.IsNullOrWhiteSpace(item.AudioText));
            Assert.Contains(
                word.Text, item.AudioText!, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void A_reading_question_still_names_it()
    {
        // Per-skill, not global. A Reading learner is looking at the word
        // inside its sentences, so refusing to name it would answer a problem
        // Reading does not have.
        var (session, words) = Build(withWritten: true, listening: false);

        foreach (var item in session.Items
            .Where(i => i.Type == SessionItemType.TargetWord))
        {
            var word = words.Single(w => w.Id == item.WordId);

            Assert.Contains(
                word.Text, item.Prompt, StringComparison.OrdinalIgnoreCase);
            Assert.Null(item.PromptKey);
        }
    }

    [Fact]
    public void A_generator_that_repeats_the_answer_cannot_create_two_right_ones()
    {
        var word = Word_("sell", "يبيع");

        var content = new GeneratedContent(
            Text: "A passage.",
            Sentences: ["A passage."],
            Comprehension: [],
            // The one failure a learner cannot recover from: the model returns
            // the correct meaning as a wrong one, and the question has two
            // right answers with only one of them marked.
            Contexts: [new GeneratedWordContext(
                "sell", "B.", "He will sell it.", "A.",
                ["يبيع", "  يبيع  ", "معنى آخر"])],
            PromptVersion: "test",
            Model: "test",
            Tokens: 0,
            FromFallback: false);

        var session = SkillSession.Start(
            UserId, SkillType.Listening, CefrLevel.B1, T0);

        SessionContentBuilder.BuildComprehensionItems(
            session, content, [word], listening: true, new Random(3));

        var options = Options(session.Items.Single());

        Assert.Equal(4, options.Count);
        Assert.Single(options, o => o == "يبيع");
    }

    // ── The register the options are written in (ADR-088) ────────────────────
    //
    // One question at every band; only the four lines under it change language.
    // An A2 learner choosing between English definitions is being tested on the
    // definitions rather than on the word, and a C1 learner choosing between
    // Arabic words is being asked to translate, which is easier than the word
    // is.

    [Theory]
    [InlineData(CefrLevel.A1, MeaningOptionStyle.ArabicMeaning)]
    [InlineData(CefrLevel.A1Plus, MeaningOptionStyle.ArabicMeaning)]
    [InlineData(CefrLevel.A2, MeaningOptionStyle.ArabicMeaning)]
    [InlineData(CefrLevel.A2Plus, MeaningOptionStyle.ArabicMeaning)]
    [InlineData(CefrLevel.B1, MeaningOptionStyle.SimpleDefinition)]
    [InlineData(CefrLevel.B1Plus, MeaningOptionStyle.SimpleDefinition)]
    [InlineData(CefrLevel.B2, MeaningOptionStyle.SimpleDefinition)]
    [InlineData(CefrLevel.B2Plus, MeaningOptionStyle.DictionaryDefinition)]
    [InlineData(CefrLevel.C1, MeaningOptionStyle.DictionaryDefinition)]
    [InlineData(CefrLevel.C1Plus, MeaningOptionStyle.DictionaryDefinition)]
    [InlineData(CefrLevel.C2, MeaningOptionStyle.DictionaryDefinition)]
    public void Every_rung_of_the_ladder_lands_in_a_band(
        CefrLevel level, MeaningOptionStyle expected)
    {
        // Every rung, named individually. The boundaries are the product
        // owner's and they are the whole rule — a ladder read with `>` instead
        // of `>=` moves exactly one band and nothing else would catch it.
        Assert.Equal(expected, LevelBands.OptionStyleFor(level));
    }

    [Fact]
    public void The_easier_bands_answer_in_Arabic()
    {
        var (session, words) = Build(
            withWritten: true, style: MeaningOptionStyle.ArabicMeaning);

        foreach (var item in TargetItems(session))
        {
            Assert.Contains(item.CorrectAnswer, words.Select(w => w.Meaning));
        }
    }

    [Fact]
    public void The_middle_bands_answer_in_the_generators_plain_English()
    {
        var (session, words) = Build(
            withWritten: true, style: MeaningOptionStyle.SimpleDefinition);

        foreach (var item in TargetItems(session))
        {
            Assert.Contains("plainly put", item.CorrectAnswer);
            Assert.DoesNotContain(
                item.CorrectAnswer, words.Select(w => w.Meaning));

            // Every option in the same register, or the correct one is the odd
            // one out and the question can be passed without knowing the word.
            Assert.All(Options(item), o => Assert.False(HasArabic(o)));
        }
    }

    [Fact]
    public void The_highest_bands_answer_in_the_dictionarys_own_words()
    {
        var (session, words) = Build(
            withWritten: true, style: MeaningOptionStyle.DictionaryDefinition);

        foreach (var item in TargetItems(session))
        {
            // The lexicon's gloss, unchanged. The correct answer at this band
            // comes from this service, not from the generator — the model is
            // only trusted to write the three wrong ones.
            Assert.Contains(
                item.CorrectAnswer, words.Select(w => w.DefinitionEn));
            Assert.All(Options(item), o => Assert.False(HasArabic(o)));
        }
    }

    [Fact]
    public void A_word_with_no_English_definition_falls_back_to_Arabic()
    {
        // A guard, not a path: a word added when the lexicon had never heard of
        // it still gets a definition from the meaning checker (ADR-075). But an
        // empty correct option is a question with no right answer, which is
        // worse than one word of a session reading in the other language.
        var word = Word.Add(
            UserId, "sense-blank", "gizmo", "أداة", definitionEn: "   ",
            partOfSpeech: "noun", cefrLevel: CefrLevel.C1, config: Config,
            now: T0);

        var session = SkillSession.Start(
            UserId, SkillType.Reading, CefrLevel.C1, T0);

        SessionContentBuilder.BuildComprehensionItems(
            session,
            new GeneratedContent(
                "A passage.", ["A passage."], [],
                [new GeneratedWordContext(
                    "gizmo", null, "He held the gizmo.", null)],
                "test", "test", 0, false),
            [word], listening: false, new Random(7),
            MeaningOptionStyle.DictionaryDefinition);

        var item = TargetItems(session).Single();
        Assert.Equal("أداة", item.CorrectAnswer);
        Assert.Equal(4, Options(item).Count);
    }

    [Fact]
    public void An_English_band_with_no_generated_options_still_asks_four()
    {
        // The AI wrote nothing usable. The question is still a question, and
        // the four lines are still all in one language — an English question
        // topped up from an Arabic filler pool would give the answer away by
        // its script alone.
        var (session, _) = Build(
            withWritten: true,
            style: MeaningOptionStyle.DictionaryDefinition,
            withEnglish: false);

        foreach (var item in TargetItems(session))
        {
            var options = Options(item);
            Assert.Equal(4, options.Count);
            Assert.All(options, o => Assert.False(HasArabic(o)));
            Assert.Contains(item.CorrectAnswer, options);
        }
    }

    private static IEnumerable<SessionItem> TargetItems(SkillSession session) =>
        session.Items.Where(i => i.Type == SessionItemType.TargetWord);

    private static bool HasArabic(string text) =>
        text.Any(c => c >= '\u0600' && c <= '\u06FF');
}
