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

    /// <summary>Where <see cref="Meaning"/> came from (ADR-072).</summary>
    /// <remarks>
    /// Not a display concern — the learner is shown a meaning, not its
    /// provenance. It is here so the Owner's analytics can separate a curated
    /// gloss from one the learner typed, which is the only way to tell whether
    /// letting them type it helped or hurt.
    /// </remarks>
    public MeaningSource MeaningSource { get; private set; } = MeaningSource.Lexicon;

    /// <summary>
    /// What the checker made of a learner-written meaning (ADR-074), or null
    /// when there was nothing to check — a lexicon or passage gloss is not the
    /// learner's guess.
    /// </summary>
    public MeaningCheckResult? MeaningCheck { get; private set; }

    public string DefinitionEn { get; private set; } = string.Empty;

    public string PartOfSpeech { get; private set; } = string.Empty;

    public CefrLevel CefrLevel { get; private set; }

    public WordState State { get; private set; } = WordState.Learning;

    public SkillType? CurrentSkill { get; private set; }

    public DateTimeOffset AddedAt { get; private set; }

    public DateTimeOffset? MaturedAt { get; private set; }

    public DateTimeOffset? ActivatedAt { get; private set; }

    public DateTimeOffset? ArchivedAt { get; private set; }

    /// <summary>When the learner removed it (ADR-071); null while they have it.</summary>
    public DateTimeOffset? DeletedAt { get; private set; }

    /// <summary>
    /// How often the AI has reused this word in generated content. A priority
    /// signal only — it never removes a word and never triggers archiving on
    /// its own (rule R8).
    /// </summary>
    public int ExposureCount { get; private set; }

    /// <summary>The last time the weekly challenge asked about this word.</summary>
    public DateTimeOffset? LastReviewedAt { get; private set; }

    /// <summary>
    /// When the learner first recalled this word correctly in a weekly
    /// challenge — and therefore stopped being asked about it (ADR-099).
    /// </summary>
    /// <remarks>
    /// Separate from <see cref="LastReviewedAt"/> because the two answer
    /// different questions. "When was it last asked" sets when it ripens
    /// again; "was it ever recalled" decides whether it ripens at all.
    /// Rule R9 is untouched: neither field moves a word through the pipeline.
    /// </remarks>
    public DateTimeOffset? ReviewPassedAt { get; private set; }

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
        DateTimeOffset now,
        MeaningSource meaningSource = MeaningSource.Lexicon,
        MeaningCheckResult? meaningCheck = null)
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
            MeaningSource = meaningSource,
            MeaningCheck = meaningCheck,
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
    /// The next skill this word still owes, in pipeline order, or null when it
    /// has passed them all.
    /// </summary>
    /// <remarks>
    /// <b>Not</b> "the one after the skill just passed" (ADR-087). The pipeline
    /// order is configuration and it changed once already — Writing moved
    /// behind Spelling — and on the day it changed every word in flight was
    /// standing somewhere in the old order. Advancing by position would have
    /// walked a word sitting at the old last skill straight past the new one:
    /// it passes Writing, the index says there is nothing after Writing, and
    /// the word matures having never been asked to spell it.
    ///
    /// Asking instead which skills are still unpassed is correct under any
    /// order, needs no migration, and takes nothing away from anyone: a word
    /// mid-flight finishes in the order it started, and the skill it has not
    /// done yet is still waiting for it afterwards. It is also the honest
    /// reading of what the pipeline means — five skills, each demonstrated
    /// once, in a preferred order (rule R5).
    /// </remarks>
    public SkillType? NextPendingSkill(WordOsConfiguration config)
    {
        foreach (var skill in config.SkillsOrder)
        {
            // A skill this word does not carry at all: the order gained an
            // entry after the word was added. It owes it like any other.
            var state = _skills.SingleOrDefault(s => s.Skill == skill);
            if (state is null || state.Status != SkillStatus.Passed) return skill;
        }

        return null;
    }

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

        var next = NextPendingSkill(config);
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

        // Seeded here rather than assumed, for the same reason NextPendingSkill
        // exists: a word added under an older configuration may not carry a row
        // for a skill the order has since gained, and `Single` on a missing row
        // is an exception thrown at the moment a learner passes something.
        var pending = _skills.SingleOrDefault(s => s.Skill == next.Value);
        if (pending is null)
        {
            pending = WordSkillState.Pending(Id, next.Value);
            _skills.Add(pending);
        }

        pending.ScheduleAt(availableAt);

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

    /// <summary>
    /// Removes the word from the learner's vocabulary (ADR-071).
    /// </summary>
    /// <remarks>
    /// A state change, not a <c>DELETE</c>. The learner's experience is that the
    /// word is gone — a global query filter keeps <see cref="WordState.Deleted"/>
    /// out of every list, session and review, and the unique index that stops a
    /// word being added twice ignores deleted rows, so they may add it again and
    /// get a genuinely fresh journey.
    ///
    /// <para>What survives is the evidence: five skill rows, the event log, the
    /// exposures. Rule R8 forbids the <i>system</i> ever removing a word; this is
    /// the learner removing one, which the rules never spoke to, and keeping the
    /// history is what stops a deletion from quietly rewriting the measurements
    /// the MVP exists to take.</para>
    ///
    /// <para><see cref="CurrentSkill"/> is deliberately left where it was. It is
    /// no longer a schedule — nothing eligible is ever <c>Deleted</c> — but it is
    /// the answer to the question the Owner will actually ask about a deletion:
    /// how far had the word got before the learner gave up on it.</para>
    ///
    /// <para>Deleting twice is not an error. The second call is what a retried
    /// request looks like, and it must not turn into a failure the learner sees.</para>
    /// </remarks>
    public void Delete(DateTimeOffset now)
    {
        if (State == WordState.Deleted) return;

        State = WordState.Deleted;
        DeletedAt = now;
        _events.Add(WordEvent.Create(Id, WordEventType.Deleted, null, now));
    }

    /// <summary>
    /// Rewrites the Arabic meaning of this word, and nothing else (ADR-101).
    /// </summary>
    /// <remarks>
    /// The learner may decide that <c>create</c> is better written
    /// <c>يصنع</c> than <c>أنشأ</c>, months after adding it, and be right. What
    /// they must not do is turn it into a different word by the back door —
    /// whether the new meaning still belongs to this English word is decided
    /// before this is called, because it needs the dictionary and the checker,
    /// and neither belongs in the aggregate.
    ///
    /// <para><b>Nothing about the journey moves.</b> Not the state, not the
    /// current skill, not a single skill's status, attempts or schedule, not
    /// the exposure count, not <see cref="AddedAt"/>. Same word, same queue,
    /// same position — which is the whole promise the feature makes. A word
    /// that must start again does so by being replaced, not by being
    /// edited.</para>
    ///
    /// <para>The sense may be adopted along with the meaning when the new
    /// wording is a sense the dictionary holds <i>for this same word</i>:
    /// leaving the old sense id behind would keep an English definition that
    /// describes the meaning the learner just rejected. The caller is
    /// responsible for the identity check — <c>(UserId, SenseId)</c> is unique
    /// and the learner may already own the sense being adopted.</para>
    /// </remarks>
    public void ChangeMeaning(
        string meaning,
        MeaningSource source,
        MeaningCheckResult? check,
        DateTimeOffset now,
        string? senseId = null,
        string? definitionEn = null,
        string? partOfSpeech = null)
    {
        Meaning = meaning;
        MeaningSource = source;
        MeaningCheck = check;

        if (senseId is { Length: > 0 }) SenseId = senseId;
        if (definitionEn is not null) DefinitionEn = definitionEn;
        if (partOfSpeech is not null) PartOfSpeech = partOfSpeech;

        _events.Add(
            WordEvent.Create(Id, WordEventType.MeaningChanged, null, now));
    }

    public void RecordExposure(DateTimeOffset now)
    {
        ExposureCount++;
        _events.Add(
            WordEvent.Create(Id, WordEventType.ExposureIncremented, null, now));
    }

    /// <summary>
    /// Records that the weekly challenge asked about this word.
    /// </summary>
    /// <param name="passed">
    /// Whether the learner named it correctly on the <b>first</b> attempt. A
    /// word rescued on the second try was not remembered, which is the same
    /// standard the weekly score is computed to (R9) — so it comes back.
    /// </param>
    /// <remarks>
    /// Passing is recorded once and never withdrawn: the first correct recall
    /// retires the word from the challenge for good (ADR-099).
    /// </remarks>
    public void MarkReviewed(DateTimeOffset now, bool passed)
    {
        LastReviewedAt = now;
        if (passed) ReviewPassedAt ??= now;
    }

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
