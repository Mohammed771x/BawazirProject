using WordOs.Domain.Common;

namespace WordOs.Domain.Words;

/// <summary>
/// One vocabulary item: a word <b>in one specific sense</b>, owned by one
/// learner.
/// </summary>
/// <remarks>
/// Identity is <c>(UserId, SenseId)</c>, not the text: <c>book = كتاب</c> and
/// <c>book = يحجز</c> are different senses and therefore independent words with
/// independent journeys (ADR-012, <c>docs/04-DATA-MODEL.md</c>).
/// </remarks>
public class Word
{
    private readonly List<WordSkillState> _skills = [];
    private readonly List<WordEvent> _events = [];

    private Word() { } // EF Core

    public Guid Id { get; private set; } = Guid.CreateVersion7();

    public Guid UserId { get; private set; }

    /// <summary>WordNet synset id — the join key into <c>lexicon_entries</c>.</summary>
    public string SenseId { get; private set; } = string.Empty;

    public string Text { get; private set; } = string.Empty;

    /// <summary>The Arabic meaning of this sense, copied from the lexicon.</summary>
    public string Meaning { get; private set; } = string.Empty;

    public string DefinitionEn { get; private set; } = string.Empty;

    public string PartOfSpeech { get; private set; } = string.Empty;

    public CefrLevel CefrLevel { get; private set; }

    public WordState State { get; private set; } = WordState.Learning;

    public SkillType? CurrentSkill { get; private set; }

    public DateTimeOffset AddedAt { get; private set; }

    public DateTimeOffset? MaturedAt { get; private set; }

    public DateTimeOffset? ActivatedAt { get; private set; }

    public DateTimeOffset? ArchivedAt { get; private set; }

    /// <summary>
    /// How often the AI has reused this word in generated content. A priority
    /// signal only — it never removes a word and never triggers archiving on
    /// its own (rule R8).
    /// </summary>
    public int ExposureCount { get; private set; }

    public DateTimeOffset? LastReviewedAt { get; private set; }

    public IReadOnlyList<WordSkillState> Skills => _skills;

    public IReadOnlyList<WordEvent> Events => _events;

    /// <summary>
    /// Adds a word to the pipeline. Only the <b>first</b> skill opens; the other
    /// four stay <see cref="SkillStatus.Pending"/>.
    /// </summary>
    public static Word Add(
        Guid userId,
        string senseId,
        string text,
        string meaning,
        string definitionEn,
        string partOfSpeech,
        CefrLevel cefrLevel,
        WordOsConfiguration config,
        DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(senseId);
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        ArgumentException.ThrowIfNullOrWhiteSpace(meaning);

        var word = new Word
        {
            UserId = userId,
            SenseId = senseId,
            Text = text.Trim(),
            Meaning = meaning.Trim(),
            DefinitionEn = definitionEn,
            PartOfSpeech = partOfSpeech,
            CefrLevel = cefrLevel,
            AddedAt = now,
            CurrentSkill = config.FirstSkill,
        };

        foreach (var skill in config.SkillsOrder)
        {
            word._skills.Add(
                skill == config.FirstSkill
                    ? WordSkillState.Available(word.Id, skill, now)
                    : WordSkillState.Pending(word.Id, skill));
        }

        word._events.Add(WordEvent.Create(word.Id, WordEventType.Added, null, now));
        return word;
    }

    public WordSkillState SkillState(SkillType skill) =>
        _skills.Single(s => s.Skill == skill);

    /// <summary>
    /// Eligibility for a session: still learning, it is this word's current
    /// skill, and the scheduled gap has elapsed.
    /// </summary>
    public bool IsEligibleFor(SkillType skill, DateTimeOffset now)
    {
        if (State != WordState.Learning) return false;
        if (CurrentSkill != skill) return false;

        var state = SkillState(skill);
        if (state.Status == SkillStatus.Passed) return false;
        return state.AvailableAt is null || state.AvailableAt <= now;
    }

    /// <summary>
    /// Applies the outcome of a session for <paramref name="skill"/>.
    /// </summary>
    /// <remarks>
    /// Rule R5: a failure reschedules <b>only</b> the failed skill. Skills the
    /// learner has already demonstrated are never reset — that is the single
    /// most important property of the pipeline.
    /// </remarks>
    public WordOutcome ApplySessionResult(
        SkillType skill,
        bool passed,
        WordOsConfiguration config,
        DateTimeOffset now)
    {
        if (State != WordState.Learning)
            throw new InvalidOperationException(
                $"Word {Id} is {State} and is not in the pipeline.");
        if (CurrentSkill != skill)
            throw new InvalidOperationException(
                $"Word {Id} is at {CurrentSkill}, not {skill}.");

        var state = SkillState(skill);
        state.RecordAttempt(now);

        if (!passed)
        {
            state.Fail(now, now.AddDays(config.SkillIntervalDays));
            _events.Add(WordEvent.Create(Id, WordEventType.SkillFailed, skill, now));

            return new WordOutcome(
                WordId: Id,
                Passed: false,
                NewStatus: state.Status,
                NextSkill: skill,
                NextEligibleAt: state.AvailableAt,
                BecameActive: false);
        }

        state.Pass(now);
        _events.Add(WordEvent.Create(Id, WordEventType.SkillPassed, skill, now));

        var next = config.NextSkillAfter(skill);
        if (next is null)
        {
            // All five passed → Mature → Active (Word Life Cycle §22, §34).
            State = WordState.Active;
            CurrentSkill = null;
            MaturedAt = now;
            ActivatedAt = now;
            _events.Add(WordEvent.Create(Id, WordEventType.BecameMature, null, now));
            _events.Add(WordEvent.Create(Id, WordEventType.EnteredActive, null, now));

            return new WordOutcome(Id, true, state.Status, null, null, true);
        }

        var availableAt = now.AddDays(config.SkillIntervalDays);
        CurrentSkill = next;
        SkillState(next.Value).ScheduleAt(availableAt);

        return new WordOutcome(Id, true, state.Status, next, availableAt, false);
    }

    /// <summary>
    /// Retires the word from active rotation. Never deletes: the row and its
    /// whole history survive (rule R8, lifecycle §31).
    /// </summary>
    public void Archive(DateTimeOffset now)
    {
        if (State != WordState.Active)
            throw new InvalidOperationException(
                "Only an Active word may be archived.");

        State = WordState.Archived;
        ArchivedAt = now;
        _events.Add(WordEvent.Create(Id, WordEventType.Archived, null, now));
    }

    public void RecordExposure(DateTimeOffset now)
    {
        ExposureCount++;
        _events.Add(
            WordEvent.Create(Id, WordEventType.ExposureIncremented, null, now));
    }

    public void MarkReviewed(DateTimeOffset now) => LastReviewedAt = now;

    /// <summary>
    /// Brings every waiting skill of this word forward by <paramref name="days"/>.
    /// </summary>
    /// <remarks>
    /// The Owner's testing tool (ADR-037). It shifts *scheduled* dates only, so
    /// a word waiting for its two-day gap becomes available and a word that has
    /// already passed or failed stays exactly as it is.
    /// </remarks>
    public void AdvanceSchedule(int days)
    {
        foreach (var state in Skills) state.AdvanceSchedule(days);
    }

    public void RecordSkillStarted(SkillType skill, DateTimeOffset now) =>
        _events.Add(WordEvent.Create(Id, WordEventType.SkillStarted, skill, now));
}

/// <summary>What happened to one word at the end of a session.</summary>
public sealed record WordOutcome(
    Guid WordId,
    bool Passed,
    SkillStatus NewStatus,
    SkillType? NextSkill,
    DateTimeOffset? NextEligibleAt,
    bool BecameActive);
