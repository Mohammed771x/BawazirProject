using WordOs.Application.Abstractions;
using WordOs.Domain.Common;
using WordOs.Domain.Sessions;
using WordOs.Domain.Words;

namespace WordOs.Application.Sessions;

/// <summary>
/// Turns generated content into the items a session asks.
/// </summary>
/// <remarks>
/// Each skill keeps the shape the specification requires:
///
/// <list type="bullet">
/// <item><b>Reading</b> — passage, then exactly five comprehension questions,
/// then one context question per target word showing the previous/target/next
/// sentence so the meaning is inferred rather than recalled (§24–27).</item>
/// <item><b>Listening</b> — the same rhythm, but the sentence is spoken and
/// never shown; otherwise it is reading with audio in the background
/// (§32–34).</item>
/// <item><b>Writing</b> — "use this word", never an unrelated topic
/// (§41–43).</item>
/// <item><b>Spelling</b> — clue and input method follow the level, and a hint
/// is always available (<c>MVP Core.txt</c> §33–34). Never levelled
/// (ADR-008).</item>
/// <item><b>Speaking</b> — no items at all; it is a conversation, driven turn
/// by turn (§35–39).</item>
/// </list>
///
/// Distractors are drawn from the learner's own other words first: a plausible
/// wrong answer teaches more than an absurd one.
/// </remarks>
public static class SessionContentBuilder
{
    public static void BuildComprehensionItems(
        SkillSession session,
        GeneratedContent content,
        IReadOnlyList<Word> words,
        bool listening,
        Random random)
    {
        foreach (var question in content.Comprehension)
        {
            var options = new List<string> { question.Correct };
            options.AddRange(question.Distractors.Take(3));
            Shuffle(options, random);

            session.AddItem(SessionItem.Comprehension(
                question.Prompt, options, question.Correct));
        }

        var meanings = words.Select(w => w.Meaning).ToList();

        foreach (var word in words)
        {
            var context = content.Contexts.FirstOrDefault(c =>
                string.Equals(c.Word, word.Text, StringComparison.OrdinalIgnoreCase));

            var options = BuildMeaningOptions(word.Meaning, meanings, random);

            session.AddItem(SessionItem.TargetWord(
                wordId: word.Id,
                // About *this* use of the word, not the dictionary entry — the
                // learner is practising inference.
                prompt: $"What does \"{word.Text}\" mean here?",
                options: options,
                correct: word.Meaning,
                // Reading shows the sentences; Listening hears them.
                context: listening || context is null
                    ? null
                    : new { context.Before, context.Sentence, context.After },
                audioText: listening
                    ? JoinContext(context) ?? word.DefinitionEn
                    : null));
        }
    }

    public static void BuildWritingItems(
        SkillSession session,
        IReadOnlyList<Word> words)
    {
        for (var i = 0; i < words.Count; i++)
        {
            var word = words[i];

            // Alternating so a session is not five identical instructions, but
            // both forms are about *this word* — never a generic topic.
            var prompt = i % 2 == 0
                ? $"Write one sentence using \"{word.Text}\"."
                : $"Write one sentence about your own life using \"{word.Text}\".";

            session.AddItem(SessionItem.WritingTask(word.Id, prompt, word.Text));
        }
    }

    public static void BuildSpellingItems(
        SkillSession session,
        IReadOnlyList<Word> words,
        CefrLevel level,
        SpellingInputMode? preferredMode,
        Random random)
    {
        // B2 and above get an English definition and type freely; lower levels
        // get the Arabic meaning and letter tiles (MVP Core §33–34).
        var advanced = level.Rank() >= CefrLevel.B2.Rank();

        // Placement can only make the task *easier* than the level implies,
        // never harder (ADR-008).
        var useTiles = preferredMode == SpellingInputMode.LetterTiles || !advanced;

        foreach (var word in words)
        {
            SpellingClueKind clueKind;
            string clue;

            if (advanced && !string.IsNullOrWhiteSpace(word.DefinitionEn))
            {
                clueKind = SpellingClueKind.DefinitionEn;
                clue = word.DefinitionEn;
            }
            else
            {
                clueKind = SpellingClueKind.ArabicMeaning;
                clue = word.Meaning;
            }

            var letters = word.Text
                .Replace(" ", string.Empty)
                .Select(c => c.ToString())
                .ToList();
            Shuffle(letters, random);

            session.AddItem(SessionItem.SpellingTask(
                wordId: word.Id,
                clue: clue,
                clueKind: clueKind,
                letters: useTiles ? letters : null,
                inputMode: useTiles
                    ? SpellingInputMode.LetterTiles
                    : SpellingInputMode.FreeTyping,
                hint: SpellingHint(word.Text),
                word: word.Text));
        }
    }

    /// <summary>
    /// Reveals the opening letters and the length — enough to unstick a learner
    /// without giving the answer away.
    /// </summary>
    private static string SpellingHint(string word)
    {
        var revealed = word.Length <= 4 ? 1 : 2;
        var masked = string.Concat(word.Select((c, i) =>
            i < revealed || c == ' ' ? c : '_'));
        return $"{masked}  ({word.Replace(" ", string.Empty).Length} letters)";
    }

    private static List<string> BuildMeaningOptions(
        string correct,
        IReadOnlyList<string> otherMeanings,
        Random random)
    {
        // The learner's own other words make the best distractors: they are
        // real meanings the learner is currently studying, so a guess based on
        // vague familiarity does not work.
        var pool = otherMeanings
            .Where(m => !string.Equals(m, correct, StringComparison.Ordinal))
            .Distinct(StringComparer.Ordinal)
            .ToList();
        Shuffle(pool, random);

        var options = new List<string> { correct };
        options.AddRange(pool.Take(3));

        // Not enough of the learner's own words yet — top up from a fixed pool
        // rather than showing a question with two options.
        foreach (var filler in FillerMeanings)
        {
            if (options.Count >= 4) break;
            if (!options.Contains(filler, StringComparer.Ordinal))
                options.Add(filler);
        }

        Shuffle(options, random);
        return options;
    }

    private static readonly string[] FillerMeanings =
    [
        "لوحة مفاتيح", "شبكة الإنترنت", "قاعدة بيانات", "متصفح",
        "مكتبة عامة", "مطار دولي", "وجبة خفيفة", "ملعب رياضي",
    ];

    private static string? JoinContext(GeneratedWordContext? context) =>
        context is null
            ? null
            : string.Join(' ',
                new[] { context.Before, context.Sentence, context.After }
                    .Where(s => !string.IsNullOrWhiteSpace(s)));

    private static void Shuffle<T>(IList<T> list, Random random)
    {
        for (var i = list.Count - 1; i > 0; i--)
        {
            var j = random.Next(i + 1);
            (list[i], list[j]) = (list[j], list[i]);
        }
    }
}
