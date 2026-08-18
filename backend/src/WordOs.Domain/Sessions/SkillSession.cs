using WordOs.Domain.Common;

namespace WordOs.Domain.Sessions;

public enum SessionItemType
{
    Comprehension,
    TargetWord,
    WritingTask,
    SpeakingTurn,
    SpellingTask,
}

/// <summary>
/// One skill session, including the <b>in-session learning loop</b>.
/// </summary>
/// <remarks>
/// The queue is the important part. A wrong answer does not drop the item: it
/// is recorded and pushed to the back of the queue so the learner meets it
/// again before the session ends (demo review §29–31, §47).
///
/// Only a <b>first-attempt</b> success passes the word for that skill. Anything
/// else leaves the word to be rescheduled, which is what turns a mistake into
/// reinforcement rather than a dead end (§31). Wrong never means discarded —
/// that is the whole philosophy of WordOS (§48).
/// </remarks>
public class SkillSession
{
    private readonly List<SessionItem> _items = [];

    private SkillSession() { } // EF Core

    public Guid Id { get; private set; } = Guid.CreateVersion7();

    public Guid UserId { get; private set; }

    public SkillType Skill { get; private set; }

    /// <summary>The CEFR level the content was generated at.</summary>
    public CefrLevel LevelUsed { get; private set; }

    public DateTimeOffset StartedAt { get; private set; }

    public DateTimeOffset? CompletedAt { get; private set; }

    public bool IsComplete { get; private set; }

    /// <summary>
    /// A session with no vocabulary attached to it (Part 2 §5).
    /// </summary>
    /// <remarks>
    /// When nothing is due, a learner who opened the app to practise should be
    /// able to practise. A practice session generates real content and asks
    /// real comprehension questions, but it owns no words: nothing passes,
    /// nothing fails, no gap is scheduled and no level moves. It is the one
    /// session type that measures nothing — which is exactly why it is safe to
    /// offer whenever the pipeline is empty.
    /// </remarks>
    public bool IsPractice { get; private set; }

    /// <summary>The passage or audio script, for Reading and Listening.</summary>
    public string? ContentText { get; private set; }

    /// <summary>
    /// Every word of the passage with the meaning it carries there, as JSON.
    /// </summary>
    /// <remarks>
    /// Written when the passage is generated, because that is when the model
    /// knows which sense it meant. Tapping a word then costs nothing and always
    /// answers about *this* sentence — a dictionary lookup would return every
    /// sense the word has ever had.
    /// </remarks>
    public string? GlossaryJson { get; private set; }

    /// <summary>Which item the learner is looking at right now.</summary>
    public Guid? CurrentItemId { get; private set; }

    /// <summary>Speaking only — the conversation so far, as JSON.</summary>
    public string? TranscriptJson { get; private set; }

    /// <summary>
    /// True when any part of this session was produced without the AI service.
    /// Recorded so a quality dip can be attributed to an outage rather than to
    /// learners (<c>MVP Core.txt</c> §62).
    /// </summary>
    public bool UsedAiFallback { get; private set; }

    public string PromptVersion { get; private set; } = string.Empty;

    public string AiModel { get; private set; } = string.Empty;

    public int AiTokens { get; private set; }

    public IReadOnlyList<SessionItem> Items => _items;

    public static SkillSession Start(
        Guid userId,
        SkillType skill,
        CefrLevel levelUsed,
        DateTimeOffset now,
        bool isPractice = false) =>
        new()
        {
            UserId = userId,
            Skill = skill,
            LevelUsed = levelUsed,
            StartedAt = now,
            IsPractice = isPractice,
        };

    public void SetContent(
        string? text,
        string promptVersion,
        string model,
        int tokens,
        bool fromFallback,
        string? glossaryJson = null)
    {
        ContentText = text;
        GlossaryJson = glossaryJson;
        PromptVersion = promptVersion;
        AiModel = model;
        AiTokens += tokens;
        if (fromFallback) UsedAiFallback = true;
    }

    /// <summary>
    /// Replaces the passage and its questions with a re-telling at another
    /// level, keeping the same session and the same words.
    /// </summary>
    /// <remarks>
    /// Only legal before the learner has answered anything. Past that point the
    /// questions they already cleared would be about sentences that no longer
    /// exist, and their answers would be silently discarded — so the level is
    /// locked once the questions begin.
    /// </remarks>
    public bool CanChangeLevel => _items.All(i => i.Attempts == 0);

    /// <summary>
    /// Sets the level a conversation runs at, from the next turn onwards.
    /// </summary>
    /// <remarks>
    /// Speaking has no passage to replace: the level is an input to the next
    /// thing the tutor says, so changing it takes effect immediately and needs
    /// nothing regenerated.
    /// </remarks>
    public void SetLevel(CefrLevel level) => LevelUsed = level;

    public void ReplaceContent(CefrLevel level, string? text, string? glossaryJson)
    {
        LevelUsed = level;
        ContentText = text;
        GlossaryJson = glossaryJson;
        _items.Clear();
        CurrentItemId = null;
    }

    public void MarkFallbackUsed() => UsedAiFallback = true;

    public void AddTokens(int tokens) => AiTokens += tokens;

    /// <summary>
    /// Records which model and prompt produced an AI call that is not content
    /// generation — a speaking turn or a writing evaluation.
    /// </summary>
    /// <remarks>
    /// Without this, Speaking and Writing sessions carry no attribution at all,
    /// so a drop in their pass rates cannot be traced to a prompt edit the way
    /// Reading's and Listening's can (`MVP Core.txt` §62).
    ///
    /// The first real attribution wins: a session is one prompt version, and a
    /// fallback (which reports none) must not erase the record of the calls that
    /// did reach Gemini.
    /// </remarks>
    public void RecordAiCall(string promptVersion, string model, int tokens)
    {
        AiTokens += tokens;

        if (string.IsNullOrEmpty(promptVersion) || string.IsNullOrEmpty(model))
            return;

        if (string.IsNullOrEmpty(PromptVersion)) PromptVersion = promptVersion;
        if (string.IsNullOrEmpty(AiModel)) AiModel = model;
    }

    public void SetTranscript(string json) => TranscriptJson = json;

    public SessionItem AddItem(SessionItem item)
    {
        item.AttachTo(Id, _items.Count);
        _items.Add(item);
        if (CurrentItemId is null) CurrentItemId = item.Id;
        return item;
    }

    public SessionItem? CurrentItem =>
        CurrentItemId is null ? null : _items.FirstOrDefault(i => i.Id == CurrentItemId);

    /// <summary>Items still to clear, in the order they will be asked.</summary>
    public IReadOnlyList<SessionItem> Queue =>
        _items.Where(i => !i.IsCleared)
            .OrderBy(i => i.RequeuedAt ?? int.MinValue)
            .ThenBy(i => i.Position)
            .ToList();

    public int TotalItems => _items.Count;

    public int ClearedCount => _items.Count(i => i.IsCleared);

    /// <summary>
    /// Records an attempt and decides what happens to the item.
    /// </summary>
    /// <returns>True when the item was requeued for another try.</returns>
    public bool RecordAttempt(
        SessionItem item,
        bool isCorrect,
        WordOsConfiguration config,
        int sequence)
    {
        var requeued = item.RecordAttempt(isCorrect, config.MaxAttemptsPerItem, sequence);

        // The next item is whatever is at the head of the queue, which puts a
        // requeued item behind everything still unseen.
        CurrentItemId = Queue.FirstOrDefault()?.Id;
        return requeued;
    }

    /// <summary>
    /// Whether a word passed this session.
    /// </summary>
    /// <remarks>
    /// Every item for the word must have been right on its <b>first</b>
    /// attempt. A word rescued on a retry is reinforced but not passed (§31).
    /// </remarks>
    public bool PassedFor(Guid wordId)
    {
        var forWord = _items.Where(i => i.WordId == wordId).ToList();
        return forWord.Count > 0 && forWord.All(i => i.FirstAttemptCorrect == true);
    }

    public int AttemptsFor(Guid wordId)
    {
        var forWord = _items.Where(i => i.WordId == wordId).ToList();
        return forWord.Count == 0 ? 0 : forWord.Max(i => i.Attempts);
    }

    public void Complete(DateTimeOffset now)
    {
        IsComplete = true;
        CompletedAt = now;
        CurrentItemId = null;
    }
}

/// <summary>One question or task inside a session.</summary>
public class SessionItem
{
    private static readonly System.Text.Json.JsonSerializerOptions WireJson = new()
    {
        PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase,
    };

    private SessionItem() { } // EF Core

    public Guid Id { get; private set; } = Guid.CreateVersion7();

    public Guid SessionId { get; private set; }

    public int Position { get; private set; }

    public SessionItemType Type { get; private set; }

    /// <summary>Null for comprehension items, which decide no word's fate.</summary>
    public Guid? WordId { get; private set; }

    public string Prompt { get; private set; } = string.Empty;

    /// <summary>Options as issued — already shuffled server-side (rule R7).</summary>
    public string OptionsJson { get; private set; } = "[]";

    /// <summary>Never sent to the client.</summary>
    public string CorrectAnswer { get; private set; } = string.Empty;

    /// <summary>Reading: the three sentences around the word, as JSON.</summary>
    public string? ContextJson { get; private set; }

    /// <summary>Listening: the sentence spoken by TTS. Never shown as text.</summary>
    public string? AudioText { get; private set; }

    /// <summary>Spelling: the clue, and what kind of clue it is.</summary>
    public string? Clue { get; private set; }

    public SpellingClueKind? ClueKind { get; private set; }

    /// <summary>
    /// The whole hint ladder for this word, as JSON, easiest last.
    /// </summary>
    /// <remarks>
    /// Built once when the session starts, because it depends on the learner's
    /// level and on what the lexicon holds — neither of which the client can
    /// see. The client reveals one rung per press; it never chooses what a
    /// rung says.
    /// </remarks>
    public string? HintsJson { get; private set; }

    public string? LettersJson { get; private set; }

    public SpellingInputMode? InputMode { get; private set; }


    public int Attempts { get; private set; }

    /// <summary>Null until answered. Only the first attempt counts (§31).</summary>
    public bool? FirstAttemptCorrect { get; private set; }

    public bool IsCleared { get; private set; }

    /// <summary>
    /// Sequence number of the requeue, so a requeued item sorts behind
    /// everything not yet seen but ahead of later requeues.
    /// </summary>
    public int? RequeuedAt { get; private set; }

    public string? LastAnswer { get; private set; }

    /// <summary>
    /// Which instruction this item carries, for items whose instruction is
    /// fixed rather than generated.
    /// </summary>
    /// <remarks>
    /// Null for anything written for this session in particular — a
    /// comprehension question is content, and content is not translated
    /// (ADR-035). <see cref="Prompt"/> stays populated either way, so a client
    /// that does not know a key still has something to show.
    /// </remarks>
    public SessionPromptKey? PromptKey { get; private set; }

    public static SessionItem Comprehension(
        string prompt, IReadOnlyList<string> options, string correct) =>
        new()
        {
            Type = SessionItemType.Comprehension,
            Prompt = prompt,
            OptionsJson = System.Text.Json.JsonSerializer.Serialize(options),
            CorrectAnswer = correct,
        };

    public static SessionItem TargetWord(
        Guid wordId,
        string prompt,
        IReadOnlyList<string> options,
        string correct,
        object? context,
        string? audioText) =>
        new()
        {
            Type = SessionItemType.TargetWord,
            WordId = wordId,
            Prompt = prompt,
            OptionsJson = System.Text.Json.JsonSerializer.Serialize(options),
            CorrectAnswer = correct,
            // camelCase because this JSON is stored verbatim and re-emitted
            // inside the API response. Serialized with the default policy it
            // would reach the client as `Before`/`Sentence`/`After` while every
            // other field is camelCase — a contract break the compiler cannot
            // catch.
            ContextJson = context is null
                ? null
                : System.Text.Json.JsonSerializer.Serialize(context, WireJson),
            AudioText = audioText,
        };

    public static SessionItem WritingTask(
        Guid wordId, string prompt, SessionPromptKey promptKey, string word) =>
        new()
        {
            Type = SessionItemType.WritingTask,
            WordId = wordId,
            Prompt = prompt,
            PromptKey = promptKey,
            CorrectAnswer = word,
        };

    public static SessionItem SpellingTask(
        Guid wordId,
        string clue,
        SpellingClueKind clueKind,
        IReadOnlyList<(SpellingClueKind Kind, string Text)> hints,
        IReadOnlyList<string>? letters,
        SpellingInputMode inputMode,
        string word) =>
        new()
        {
            Type = SessionItemType.SpellingTask,
            WordId = wordId,
            Prompt = "Write the word",
            PromptKey = SessionPromptKey.WriteTheWord,
            CorrectAnswer = word,
            Clue = clue,
            ClueKind = clueKind,
            HintsJson = System.Text.Json.JsonSerializer.Serialize(
                // Property names match the wire record the API reads this back
                // into; the API camel-cases them on the way out.
                hints.Select(h => new { Kind = h.Kind.ToWire(), Text = h.Text })),
            LettersJson = letters is null
                ? null
                : System.Text.Json.JsonSerializer.Serialize(letters),
            InputMode = inputMode,
        };

    internal void AttachTo(Guid sessionId, int position)
    {
        SessionId = sessionId;
        Position = position;
    }

    /// <summary>
    /// Whether missing this item brings it back later in the session.
    /// </summary>
    /// <remarks>
    /// Only the items about a <b>word</b> come back. The word is what the
    /// session exists to teach, and meeting it again is the reinforcement that
    /// makes a miss useful rather than final.
    ///
    /// A comprehension question is not about a word — it measures whether the
    /// passage was pitched right. Asking it again teaches nothing (the learner
    /// has already been shown the answer), and it strands them at the end of a
    /// session re-reading questions they have finished with.
    /// </remarks>
    private bool Repeatable => Type is not SessionItemType.Comprehension;

    internal bool RecordAttempt(bool isCorrect, int maxAttempts, int sequence)
    {
        Attempts++;

        // First attempt only — a later success reinforces the word but does not
        // pass the skill.
        FirstAttemptCorrect ??= isCorrect;

        if (isCorrect || !Repeatable || Attempts >= maxAttempts)
        {
            // Cleared by getting it right, by being a question that is asked
            // once, or by exhausting the retry budget — without the cap a
            // learner who keeps missing would never reach the end
            // (demo review §56).
            IsCleared = true;
            return false;
        }

        RequeuedAt = sequence;
        return true;
    }

    public void SetAnswer(string answer) => LastAnswer = answer;
}
