using System.Text.RegularExpressions;

namespace WordOs.Domain.Sessions;

/// <summary>
/// Which parts of a Speaking turn are an <b>answer</b>, and which are the
/// learner asking the tutor for something (ADR-113).
/// </summary>
/// <remarks>
/// A target word only counts as used when it appears in an answer. "Can you
/// give me another question using football?", "What does football mean?" and
/// "How can I use football in a sentence?" all contain the word and use none
/// of it: they are requests, and counting them marked a word done that the
/// learner had never once said in a sentence of their own.
///
/// <para>This is the first of two independent checks. The model is asked the
/// same question in its own words (<c>words_only_named</c> and the turn's
/// intent), but a model is sometimes wrong, and it was — so the phrasings a
/// learner actually uses to ask are recognised here, in code that can be
/// tested and cannot drift with a prompt. A word counts only when neither
/// check calls it a request.</para>
///
/// <para>Judged a sentence at a time, because a turn can be both: "I play
/// football every weekend. Can you ask me another question?" used the word
/// in its first sentence, and that sentence still counts.</para>
///
/// <para>Deliberately narrow. Every pattern here names the act of asking the
/// tutor — for a question, a meaning, an explanation, a sentence — never a
/// topic. "Do you like football?" is the learner using the word in a question
/// of their own, which is conversation, and it counts.</para>
/// </remarks>
public static class LearnerRequests
{
    private const RegexOptions Options =
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled;

    private static readonly TimeSpan Timeout = TimeSpan.FromMilliseconds(100);

    /// <summary>Asking the tutor for something, whatever the word.</summary>
    private static readonly Regex[] Asks =
    [
        // "another question", "a new sentence", "an easier one"
        new(@"\b(another|new|different|next|easier|simpler|harder|one\s+more)\s+(question|sentence|example|one)\b", Options, Timeout),
        // "give me an example", "ask me a new question", "tell me the meaning"
        new(@"\b(give|ask|tell|show|send)\s+me\s+(another|a\s+new|a\s+different|one\s+more|an?\s+easier|an?\s+simpler|an?\s+example|examples|some\s+examples|a\s+sentence|the\s+meaning|what\b|how\b|more\s+questions)", Options, Timeout),
        // "what does it mean", "what is the meaning", "meaning of"
        new(@"\bwhat\s+(does|do|did|is|was|'s)\b.*\bmean(s|t|ing)?\b", Options, Timeout),
        new(@"\bmeaning\s+of\b", Options, Timeout),
        new(@"\bwhat\s+do\s+you\s+mean\b", Options, Timeout),
        // "how can I use", "how do you say", "how to use"
        new(@"\bhow\s+(can|do|should|could|would|might|to)\s+(i\s+|we\s+|you\s+)?(use|say|pronounce|spell)\b", Options, Timeout),
        // "in a sentence" — the learner asking how the word goes, not using it
        new(@"\bin\s+a\s+sentence\b", Options, Timeout),
        // Talking about the word as a word: "the word football", "let me use"
        new(@"\bthe\s+word\b", Options, Timeout),
        new(@"\blet\s+me\s+(try\s+to\s+)?use\b", Options, Timeout),
        // "explain it", "explain what…", "can you explain?", "repeat please"
        new(@"\b(explain|define|clarify|translate|rephrase|repeat)\b(\s+(it|this|that|again))?(\s+(to|for)\s+me)?(\s+please)?\s*(what\b|how\b|the\s+meaning|the\s+question|[?.!]*$)", Options, Timeout),
        // "I don't understand the question", "I don't know what it means"
        new(@"\bi\s+(don't|do\s+not|didn't|did\s+not|can't|cannot)\s+(understand|know|get)\b.*\b(word|means?|meaning|meant|question)\b", Options, Timeout),
        new(@"\b(change|switch)\s+the\s+(topic|subject|question)\b", Options, Timeout),
    ];

    /// <summary>
    /// The sentences of <paramref name="turn"/> that are answers — everything
    /// that is not a request — joined back together.
    /// </summary>
    /// <param name="targetWords">
    /// The words being practised. Needed for the two requests that are only a
    /// request because of which word they name: "Explain football." and
    /// "What is football?".
    /// </param>
    public static string AnswerPart(string turn, IEnumerable<string> targetWords)
    {
        var words = targetWords.Where(w => !string.IsNullOrWhiteSpace(w)).ToList();
        return string.Join(' ', Sentences(turn).Where(s => !IsRequest(s, words)));
    }

    /// <summary>Whether one sentence asks the tutor for something.</summary>
    public static bool IsRequest(string sentence, IReadOnlyCollection<string> targetWords)
    {
        var text = Normalise(sentence);
        if (text.Length == 0) return false;

        if (Asks.Any(pattern => pattern.IsMatch(text))) return true;

        foreach (var word in targetWords)
        {
            var escaped = Regex.Escape(word.Trim());

            // "Explain football." / "Define football, please."
            if (Regex.IsMatch(text,
                    $@"\b(explain|define|clarify|translate)\s+(the\s+word\s+)?[""']?{escaped}\b",
                    Options, Timeout))
                return true;

            // "What is football?" — the whole sentence, and nothing else.
            // "What is your favourite football team?" is conversation.
            if (Regex.IsMatch(text,
                    $@"^(so\s+|and\s+|but\s+|sorry,?\s+)?(what|who)\s+(is|are|'s)\s+(a\s+|an\s+|the\s+)?[""']?{escaped}[""']?\s*[?.!]*$",
                    Options, Timeout))
                return true;
        }

        return false;
    }

    /// <summary>
    /// A turn cut into sentences at full stops, question and exclamation marks,
    /// and line breaks. A transcript with no punctuation is one sentence.
    /// </summary>
    public static IReadOnlyList<string> Sentences(string turn) =>
        Regex.Split(turn ?? string.Empty, @"(?<=[.!?])\s+|\r?\n+", RegexOptions.None, Timeout)
            .Select(s => s.Trim())
            .Where(s => s.Length > 0)
            .ToList();

    /// <summary>Curly quotes and doubled spaces made plain, so one pattern fits both.</summary>
    private static string Normalise(string sentence) =>
        Regex.Replace(
                (sentence ?? string.Empty).Replace('’', '\'').Replace('‘', '\'')
                    .Replace('“', '"').Replace('”', '"'),
                @"\s+", " ", RegexOptions.None, Timeout)
            .Trim();
}
