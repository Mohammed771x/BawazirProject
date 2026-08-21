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
            var key = i % 2 == 0
                ? SessionPromptKey.WriteASentence
                : SessionPromptKey.WriteASentenceAboutYourself;

            var prompt = key == SessionPromptKey.WriteASentence
                ? $"Write one sentence using \"{word.Text}\"."
                : $"Write one sentence about your own life using \"{word.Text}\".";

            session.AddItem(
                SessionItem.WritingTask(word.Id, prompt, key, word.Text));
        }
    }

    public static void BuildSpellingItems(
        SkillSession session,
        IReadOnlyList<Word> words,
        CefrLevel level,
        SpellingInputMode? preferredMode,
        Random random,
        IReadOnlyDictionary<Guid, string>? synonyms = null)
    {
        // B2 and above type freely; lower levels get letter tiles
        // (MVP Core §33–34).
        var advanced = level.Rank() >= CefrLevel.B2.Rank();

        // Placement can only make the task *easier* than the level implies,
        // never harder (ADR-008).
        var useTiles = preferredMode == SpellingInputMode.LetterTiles || !advanced;

        foreach (var word in words)
        {
            var ladder = BuildHintLadder(
                word, level, synonyms?.GetValueOrDefault(word.Id));

            session.AddItem(SessionItem.SpellingTask(
                wordId: word.Id,
                // The first rung is what the learner sees before asking for
                // anything; the rest arrive one press at a time.
                clue: ladder[0].Text,
                clueKind: ladder[0].Kind,
                hints: ladder,
                letters: useTiles ? LetterPool(word.Text, random) : null,
                inputMode: useTiles
                    ? SpellingInputMode.LetterTiles
                    : SpellingInputMode.FreeTyping,
                word: word.Text));
        }
    }

    /// <summary>
    /// The hint ladder for one word, from where this learner joins it.
    /// </summary>
    /// <remarks>
    /// One ladder, five rungs, each easier than the last:
    ///
    /// <code>
    /// dictionary definition → simplified definition → synonym
    ///                       → translation → number of letters
    /// </code>
    ///
    /// Where a learner joins depends on their level, because the point of a
    /// hint is to be usable: a full WordNet definition is often harder than the
    /// word it defines, so handing one to an A2 learner tests their reading
    /// rather than helping them spell. So C1 starts at the top, B2 one rung
    /// down, B1 at the synonym, and A1/A2 at the translation.
    ///
    /// The rest of the ladder is still theirs — every press of "hint" steps
    /// down one — but they never have to climb.
    ///
    /// Rungs with nothing to say are skipped: a word with no synonym in the
    /// lexicon simply has one fewer step. The translation is always present, so
    /// the ladder can never come back empty.
    /// </remarks>
    private static List<(SpellingClueKind Kind, string Text)> BuildHintLadder(
        Word word,
        CefrLevel level,
        string? synonym)
    {
        var rank = level.Rank();

        // Where this learner joins. Anything above their rung is skipped, not
        // shown later — climbing back up is not what a hint is for.
        var entry = rank switch
        {
            var r when r >= CefrLevel.C1.Rank() => SpellingClueKind.DefinitionEn,
            var r when r >= CefrLevel.B2.Rank() =>
                SpellingClueKind.SimplifiedDefinition,
            var r when r >= CefrLevel.B1.Rank() => SpellingClueKind.Synonym,
            _ => SpellingClueKind.ArabicMeaning,
        };

        var ladder = new List<(SpellingClueKind, string)>();

        void Rung(SpellingClueKind kind, string? text)
        {
            if (kind < entry) return;
            if (string.IsNullOrWhiteSpace(text)) return;
            ladder.Add((kind, text.Trim()));
        }

        Rung(SpellingClueKind.DefinitionEn, word.DefinitionEn);
        Rung(SpellingClueKind.SimplifiedDefinition,
            SimplifyDefinition(word.DefinitionEn));
        Rung(SpellingClueKind.Synonym, synonym);
        Rung(SpellingClueKind.ArabicMeaning, word.Meaning);
        Rung(SpellingClueKind.LetterCount, LetterCount(word.Text));

        // The simplified definition is only a rung when it says something
        // different; otherwise the learner presses "hint" and nothing changes.
        if (ladder.Count > 1 &&
            ladder[0].Item1 == SpellingClueKind.DefinitionEn &&
            ladder[1].Item1 == SpellingClueKind.SimplifiedDefinition &&
            ladder[0].Item2 == ladder[1].Item2)
        {
            ladder.RemoveAt(1);
        }

        // A word with no definition and no synonym still gets its translation,
        // so this can only be empty if the word itself is empty.
        return ladder.Count > 0
            ? ladder
            : [(SpellingClueKind.LetterCount, LetterCount(word.Text))];
    }

    /// <summary>
    /// The first gloss of a WordNet definition.
    /// </summary>
    /// <remarks>
    /// WordNet stacks alternatives and examples behind semicolons; the first
    /// clause is the definition and the rest is elaboration a learner reaching
    /// for a hint does not need.
    /// </remarks>
    private static string SimplifyDefinition(string definition)
    {
        var text = (definition ?? string.Empty).Trim();
        var cut = text.IndexOf(';');
        if (cut > 0) text = text[..cut].Trim();
        return text;
    }

    private static string LetterCount(string word)
    {
        var letters = word.Replace(" ", string.Empty).Length;
        return $"{letters}";
    }

    /// <summary>
    /// The tiles a learner picks from: the word's own letters plus decoys.
    /// </summary>
    /// <remarks>
    /// A pool holding exactly the word's letters is not a spelling task — it is
    /// an anagram with the answer built in, and a learner can finish it by
    /// using every tile up without ever knowing the word (Part 2 §36–§37).
    /// The decoys are drawn from letters that are *not* in the word, so a
    /// duplicate never makes a correct answer ambiguous.
    /// </remarks>
    private static List<string> LetterPool(string word, Random random)
    {
        // The spaces are tiles too. "alarm clock" is one vocabulary item, and a
        // pool of its letters with the spaces stripped out is a puzzle with no
        // solution: the learner can lay out every letter and still not have
        // written the word.
        var letters = word
            .ToLowerInvariant()
            .Select(c => c.ToString())
            .ToList();

        // Enough to matter, not so many that the pool becomes a wall of tiles.
        // Counted on the letters, not the spaces: a space is not something a
        // learner has to guess.
        var decoyCount = Math.Clamp(
            letters.Count(l => l != " ") / 2, 3, 6);

        var used = letters.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var available = "abcdefghijklmnopqrstuvwxyz"
            .Select(c => c.ToString())
            .Where(c => !used.Contains(c))
            .ToList();
        Shuffle(available, random);

        letters.AddRange(available.Take(decoyCount));
        Shuffle(letters, random);
        return letters;
    }

    /// <summary>
    /// The warm-up a Speaking session opens with: each of its words, with four
    /// meanings to choose from.
    /// </summary>
    /// <remarks>
    /// A spoken conversation gives the learner no time to look anything up. By
    /// the time they realise they cannot remember what a word means, the tutor
    /// has already asked the question — so the meanings are checked *actively*
    /// first, rather than merely displayed.
    ///
    /// It measures nothing. Nothing is recorded, no level moves and no word
    /// passes or fails on it; a learner who misses one simply meets it again
    /// until they have it. Its only job is that nobody walks into a
    /// conversation about words they cannot recall.
    /// </remarks>
    public static IReadOnlyList<(Guid WordId, string Text, List<string> Options)>
        BuildWarmup(IReadOnlyList<Word> words, Random random)
    {
        var meanings = words.Select(w => w.Meaning).ToList();

        return words
            .Select(w => (
                w.Id,
                w.Text,
                BuildMeaningOptions(w.Meaning, meanings, random)))
            .ToList();
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
