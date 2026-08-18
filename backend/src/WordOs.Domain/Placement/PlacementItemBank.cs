using WordOs.Domain.Common;

namespace WordOs.Domain.Placement;

/// <summary>
/// The hand-calibrated item pool the adaptive placement test draws from.
/// </summary>
/// <remarks>
/// Each item carries the CEFR band it was written for; <see cref="AbilityScale"/>
/// turns that band into a Rasch difficulty. When real response data exists, a
/// measured difficulty replaces the band-derived one and nothing else in the
/// algorithm changes — that is the whole point of keeping the bank separate
/// from the estimator (<c>docs/06-PLACEMENT-ALGORITHM.md</c> §9).
///
/// Generated from the behavioural specification in
/// <c>mobile/lib/mock_backend/engine/placement/placement_item_bank.dart</c> so
/// the two cannot drift.
/// </remarks>
public static class PlacementItemBank
{
    /// <summary>
    /// Skills that receive a CEFR level from placement.
    /// </summary>
    /// <remarks>
    /// Spelling is deliberately absent: it is measured, but a CEFR band is not
    /// a meaningful description of orthographic accuracy, so the product does
    /// not assign one (ADR-008).
    /// </remarks>
    public static readonly IReadOnlyList<SkillType> CefrSkills =
    [
        SkillType.Reading,
        SkillType.Listening,
        SkillType.Speaking,
        SkillType.Writing,
    ];

    public static IReadOnlyList<BankItem> ForSkill(SkillType skill) =>
        All.Where(i => i.Skill == skill).ToList();

    public static BankItem? Find(string id) =>
        All.FirstOrDefault(i => i.Id == id);

    public static readonly IReadOnlyList<BankItem> All =
    [
        new()
        {
            Id = "rd_a1_1",
            Skill = SkillType.Reading,
            Level = CefrLevel.A1,
            Type = PlacementItemType.MultipleChoice,
            Prompt = "Where does the cat sleep?",
            Options = ["On her bed", "In the garden", "Under the table", "On a chair"],
            CorrectAnswer = "On her bed",
            Passage = "Nora has a small cat. The cat is black and white. It sleeps on her bed every night.",
            AudioText = null,
            ExpectedWords = 0,
        },
        new()
        {
            Id = "rd_a1_2",
            Skill = SkillType.Reading,
            Level = CefrLevel.A1,
            Type = PlacementItemType.MultipleChoice,
            Prompt = "Choose the word that completes: \"I ___ to school every day.\"",
            Options = ["go", "goes", "going", "gone"],
            CorrectAnswer = "go",
            Passage = null,
            AudioText = null,
            ExpectedWords = 0,
        },
        new()
        {
            Id = "rd_a2_1",
            Skill = SkillType.Reading,
            Level = CefrLevel.A2,
            Type = PlacementItemType.MultipleChoice,
            Prompt = "Why does Sami take the train?",
            Options = ["Because the roads are busy", "Because he does not own a car", "Because the train is free", "Because he lives far from the city"],
            CorrectAnswer = "Because the roads are busy",
            Passage = "Sami works in a small office near the station. Every morning he takes the train at seven because the roads are busy. He likes his job, but he hopes to work from home two days a week next year.",
            AudioText = null,
            ExpectedWords = 0,
        },
        new()
        {
            Id = "rd_a2_2",
            Skill = SkillType.Reading,
            Level = CefrLevel.A2,
            Type = PlacementItemType.MultipleChoice,
            Prompt = "Choose the word that completes: \"She was tired, ___ she finished the work.\"",
            Options = ["but", "because", "so that", "unless"],
            CorrectAnswer = "but",
            Passage = null,
            AudioText = null,
            ExpectedWords = 0,
        },
        new()
        {
            Id = "rd_b1_1",
            Skill = SkillType.Reading,
            Level = CefrLevel.B1,
            Type = PlacementItemType.MultipleChoice,
            Prompt = "What surprised the library staff?",
            Options = ["When the new visitors arrived", "How few visitors came", "That students preferred another library", "That the manager changed the plan"],
            CorrectAnswer = "When the new visitors arrived",
            Passage = "The library extended its opening hours last month. Staff expected students to arrive in the evening, but most of the new visitors came before nine in the morning. The manager now plans to open earlier instead of closing later.",
            AudioText = null,
            ExpectedWords = 0,
        },
        new()
        {
            Id = "rd_b1_2",
            Skill = SkillType.Reading,
            Level = CefrLevel.B1,
            Type = PlacementItemType.MultipleChoice,
            Prompt = "Choose the word that completes: \"The results were ___ enough to change the plan.\"",
            Options = ["significant", "signature", "signal", "signing"],
            CorrectAnswer = "significant",
            Passage = null,
            AudioText = null,
            ExpectedWords = 0,
        },
        new()
        {
            Id = "rd_b2_1",
            Skill = SkillType.Reading,
            Level = CefrLevel.B2,
            Type = PlacementItemType.MultipleChoice,
            Prompt = "What do the defenders of the policy claim?",
            Options = ["An imperfect measure is better than none for now", "The policy removes the underlying cause", "Critics have misread the evidence entirely", "A long-term remedy is already available"],
            CorrectAnswer = "An imperfect measure is better than none for now",
            Passage = "Critics of the policy argue that it addresses the symptom rather than the cause. Its defenders counter that no long-term remedy is available yet, and that leaving the symptom untreated would be worse than an imperfect intervention.",
            AudioText = null,
            ExpectedWords = 0,
        },
        new()
        {
            Id = "rd_b2_2",
            Skill = SkillType.Reading,
            Level = CefrLevel.B2,
            Type = PlacementItemType.MultipleChoice,
            Prompt = "Choose the word that completes: \"The evidence was ___ , so the committee postponed its decision.\"",
            Options = ["inconclusive", "inconsiderate", "incomparable", "inconvenient"],
            CorrectAnswer = "inconclusive",
            Passage = null,
            AudioText = null,
            ExpectedWords = 0,
        },
        new()
        {
            Id = "rd_c1_1",
            Skill = SkillType.Reading,
            Level = CefrLevel.C1,
            Type = PlacementItemType.MultipleChoice,
            Prompt = "What is the writer's main criticism?",
            Options = ["An untested assumption is treated as though it were evidence", "The data were gathered using the wrong method", "The earlier study has been misquoted", "The conclusions contradict the data presented"],
            CorrectAnswer = "An untested assumption is treated as though it were evidence",
            Passage = "What the report presents as a finding is, on closer reading, an assumption carried over from the earlier study. The authors never test it; they simply inherit it, and the conclusions rest on that inheritance rather than on the data gathered here.",
            AudioText = null,
            ExpectedWords = 0,
        },
        new()
        {
            Id = "rd_c1_2",
            Skill = SkillType.Reading,
            Level = CefrLevel.C1,
            Type = PlacementItemType.MultipleChoice,
            Prompt = "Choose the phrase that completes: \"Her argument, ___ , fails to account for the exceptions.\"",
            Options = ["compelling though it is", "compelling as it be", "that it is compelling", "being compelled"],
            CorrectAnswer = "compelling though it is",
            Passage = null,
            AudioText = null,
            ExpectedWords = 0,
        },
        new()
        {
            Id = "ls_a1_1",
            Skill = SkillType.Listening,
            Level = CefrLevel.A1,
            Type = PlacementItemType.MultipleChoice,
            Prompt = "When does the shop close?",
            Options = ["At five", "At nine", "At seven", "At three"],
            CorrectAnswer = "At five",
            Passage = null,
            AudioText = "The shop opens at nine and closes at five.",
            ExpectedWords = 0,
        },
        new()
        {
            Id = "ls_a1_2",
            Skill = SkillType.Listening,
            Level = CefrLevel.A1,
            Type = PlacementItemType.MultipleChoice,
            Prompt = "What is the brother's job?",
            Options = ["A teacher", "A driver", "A doctor", "A student"],
            CorrectAnswer = "A teacher",
            Passage = null,
            AudioText = "My brother is a teacher. He works at a school near our house.",
            ExpectedWords = 0,
        },
        new()
        {
            Id = "ls_a2_1",
            Skill = SkillType.Listening,
            Level = CefrLevel.A2,
            Type = PlacementItemType.MultipleChoice,
            Prompt = "When does the meeting start?",
            Options = ["09:30", "08:30", "10:00", "09:00"],
            CorrectAnswer = "09:30",
            Passage = null,
            AudioText = "The meeting will start at half past nine in the main hall. Please bring your notebook, because the schedule will change next week.",
            ExpectedWords = 0,
        },
        new()
        {
            Id = "ls_a2_2",
            Skill = SkillType.Listening,
            Level = CefrLevel.A2,
            Type = PlacementItemType.MultipleChoice,
            Prompt = "How did the speaker travel?",
            Options = ["By bus", "On foot", "By car", "By train"],
            CorrectAnswer = "By bus",
            Passage = null,
            AudioText = "I wanted to walk to the museum, but it started raining, so I took the bus instead.",
            ExpectedWords = 0,
        },
        new()
        {
            Id = "ls_b1_1",
            Skill = SkillType.Listening,
            Level = CefrLevel.B1,
            Type = PlacementItemType.MultipleChoice,
            Prompt = "What is the disadvantage of the new method?",
            Options = ["It costs more", "It takes longer", "It is less accurate", "It needs more people"],
            CorrectAnswer = "It costs more",
            Passage = null,
            AudioText = "Researchers say the new method saves time, although it costs more than the traditional approach.",
            ExpectedWords = 0,
        },
        new()
        {
            Id = "ls_b1_2",
            Skill = SkillType.Listening,
            Level = CefrLevel.B1,
            Type = PlacementItemType.MultipleChoice,
            Prompt = "Why was the event moved?",
            Options = ["The caterer could not come", "The hall was double-booked", "Not enough guests replied", "The weather was bad"],
            CorrectAnswer = "The caterer could not come",
            Passage = null,
            AudioText = "We had booked the hall for Saturday, but the caterer was not available, so we moved everything to the following weekend.",
            ExpectedWords = 0,
        },
        new()
        {
            Id = "ls_b2_1",
            Skill = SkillType.Listening,
            Level = CefrLevel.B2,
            Type = PlacementItemType.MultipleChoice,
            Prompt = "What is the speaker objecting to?",
            Options = ["The schedule, not the proposal itself", "The proposal as a whole", "The cost of the proposal", "The people who wrote the proposal"],
            CorrectAnswer = "The schedule, not the proposal itself",
            Passage = null,
            AudioText = "I am not saying the proposal is unworkable. I am saying that the timeline attached to it is, and that is a different objection entirely.",
            ExpectedWords = 0,
        },
        new()
        {
            Id = "ls_b2_2",
            Skill = SkillType.Listening,
            Level = CefrLevel.B2,
            Type = PlacementItemType.MultipleChoice,
            Prompt = "What does the speaker conclude?",
            Options = ["Growth has stopped because the market is saturated", "Attendance is falling sharply", "The three-month rise was a measurement error", "The market will keep growing quickly"],
            CorrectAnswer = "Growth has stopped because the market is saturated",
            Passage = null,
            AudioText = "Attendance rose for three consecutive months, then levelled off. Rather than a decline, what we are seeing is the market reaching its natural ceiling.",
            ExpectedWords = 0,
        },
        new()
        {
            Id = "ls_c1_1",
            Skill = SkillType.Listening,
            Level = CefrLevel.C1,
            Type = PlacementItemType.MultipleChoice,
            Prompt = "What is the speaker's criticism?",
            Options = ["He raised the risk but did not press it afterwards", "He failed to notice the risk at all", "He raised the risk far too late", "He overstated a risk that never materialised"],
            CorrectAnswer = "He raised the risk but did not press it afterwards",
            Passage = null,
            AudioText = "To be fair to him, he did flag the risk early. What he did not do, and this is where the criticism lands, was insist on it once the decision had gone the other way.",
            ExpectedWords = 0,
        },

        // ── Grammar ──────────────────────────────────────────────────────────
        //
        // Measured, never displayed. Grammar is the clearest single signal of
        // production ability, so §19 asks for it as supporting evidence for the
        // two skills a learner is otherwise judged on from very few prompts:
        // Speaking (six items) and Writing. Each of these is scored into
        // Writing and echoed into Speaking via `AlsoEvidenceFor`.
        new()
        {
            Id = "gr_a1_1",
            Skill = SkillType.Writing,
            Domain = PlacementDomain.Grammar,
            AlsoEvidenceFor = SkillType.Speaking,
            Level = CefrLevel.A1,
            Prompt = "She ___ to school every day.",
            Options = ["go", "goes", "going", "gone"],
            CorrectAnswer = "goes",
        },
        new()
        {
            Id = "gr_a1_2",
            Skill = SkillType.Writing,
            Domain = PlacementDomain.Grammar,
            AlsoEvidenceFor = SkillType.Speaking,
            Level = CefrLevel.A1,
            Prompt = "There ___ two books on the table.",
            Options = ["is", "are", "be", "was"],
            CorrectAnswer = "are",
        },
        new()
        {
            Id = "gr_a2_1",
            Skill = SkillType.Writing,
            Domain = PlacementDomain.Grammar,
            AlsoEvidenceFor = SkillType.Speaking,
            Level = CefrLevel.A2,
            Prompt = "I ___ my keys yesterday, so I could not open the door.",
            Options = ["lose", "lost", "have lost", "was losing"],
            CorrectAnswer = "lost",
        },
        new()
        {
            Id = "gr_a2_2",
            Skill = SkillType.Writing,
            Domain = PlacementDomain.Grammar,
            AlsoEvidenceFor = SkillType.Speaking,
            Level = CefrLevel.A2,
            Prompt = "This bag is ___ than that one.",
            Options = ["heavy", "heavier", "heaviest", "more heavy"],
            CorrectAnswer = "heavier",
        },
        new()
        {
            Id = "gr_b1_1",
            Skill = SkillType.Writing,
            Domain = PlacementDomain.Grammar,
            AlsoEvidenceFor = SkillType.Speaking,
            Level = CefrLevel.B1,
            Prompt = "If I ___ more time, I would learn another language.",
            Options = ["have", "had", "will have", "am having"],
            CorrectAnswer = "had",
        },
        new()
        {
            Id = "gr_b1_2",
            Skill = SkillType.Writing,
            Domain = PlacementDomain.Grammar,
            AlsoEvidenceFor = SkillType.Speaking,
            Level = CefrLevel.B1,
            Prompt = "The report ___ by the team last week.",
            Options = ["wrote", "has wrote", "was written", "is writing"],
            CorrectAnswer = "was written",
        },
        new()
        {
            Id = "gr_b2_1",
            Skill = SkillType.Writing,
            Domain = PlacementDomain.Grammar,
            AlsoEvidenceFor = SkillType.Speaking,
            Level = CefrLevel.B2,
            Prompt = "She suggested ___ the meeting until Monday.",
            Options = ["to postpone", "postponing", "postpone", "postponed"],
            CorrectAnswer = "postponing",
        },
        new()
        {
            Id = "gr_b2_2",
            Skill = SkillType.Writing,
            Domain = PlacementDomain.Grammar,
            AlsoEvidenceFor = SkillType.Speaking,
            Level = CefrLevel.B2,
            Prompt = "Hardly ___ the train when it started to rain.",
            Options = ["we had left", "had we left", "we left", "did we leave"],
            CorrectAnswer = "had we left",
        },
        new()
        {
            Id = "gr_c1_1",
            Skill = SkillType.Writing,
            Domain = PlacementDomain.Grammar,
            AlsoEvidenceFor = SkillType.Speaking,
            Level = CefrLevel.C1,
            Prompt = "___ for the delay, the shipment would have arrived on time.",
            Options = ["If it was not", "Were it not", "Had not it", "If not it"],
            CorrectAnswer = "Were it not",
        },
        new()
        {
            Id = "sp_a1_1",
            Skill = SkillType.Speaking,
            Level = CefrLevel.A1,
            Type = PlacementItemType.Spoken,
            Prompt = "Introduce yourself in one or two sentences. Say your name and where you live.",
            Options = [],
            CorrectAnswer = null,
            Passage = null,
            AudioText = null,
            ExpectedWords = 8,
        },
        new()
        {
            Id = "sp_a2_1",
            Skill = SkillType.Speaking,
            Level = CefrLevel.A2,
            Type = PlacementItemType.Spoken,
            Prompt = "Describe what you did yesterday. Use two or three sentences.",
            Options = [],
            CorrectAnswer = null,
            Passage = null,
            AudioText = null,
            ExpectedWords = 16,
        },
        new()
        {
            Id = "sp_b1_1",
            Skill = SkillType.Speaking,
            Level = CefrLevel.B1,
            Type = PlacementItemType.Spoken,
            Prompt = "Someone asks: \"Could you explain what you are studying and why you chose it?\" Answer them.",
            Options = [],
            CorrectAnswer = null,
            Passage = null,
            AudioText = null,
            ExpectedWords = 28,
        },
        new()
        {
            Id = "sp_b2_1",
            Skill = SkillType.Speaking,
            Level = CefrLevel.B2,
            Type = PlacementItemType.Spoken,
            Prompt = "Some people learn better alone, others in a group. Give your view and one reason for it.",
            Options = [],
            CorrectAnswer = null,
            Passage = null,
            AudioText = null,
            ExpectedWords = 40,
        },
        new()
        {
            Id = "sp_c1_1",
            Skill = SkillType.Speaking,
            Level = CefrLevel.C1,
            Type = PlacementItemType.Spoken,
            Prompt = "A colleague proposes a plan you partly disagree with. Explain which part you accept, which you do not, and why.",
            Options = [],
            CorrectAnswer = null,
            Passage = null,
            AudioText = null,
            ExpectedWords = 55,
        },
        new()
        {
            Id = "wr_a1_1",
            Skill = SkillType.Writing,
            Level = CefrLevel.A1,
            Type = PlacementItemType.FreeText,
            Prompt = "Write one sentence about your family.",
            Options = [],
            CorrectAnswer = null,
            Passage = null,
            AudioText = null,
            ExpectedWords = 6,
        },
        new()
        {
            Id = "wr_a2_1",
            Skill = SkillType.Writing,
            Level = CefrLevel.A2,
            Type = PlacementItemType.FreeText,
            Prompt = "Write two sentences describing what you usually do to study English.",
            Options = [],
            CorrectAnswer = null,
            Passage = null,
            AudioText = null,
            ExpectedWords = 16,
        },
        new()
        {
            Id = "wr_b1_1",
            Skill = SkillType.Writing,
            Level = CefrLevel.B1,
            Type = PlacementItemType.FreeText,
            Prompt = "Write a short paragraph about a skill you would like to learn and why.",
            Options = [],
            CorrectAnswer = null,
            Passage = null,
            AudioText = null,
            ExpectedWords = 30,
        },
        new()
        {
            Id = "wr_b2_1",
            Skill = SkillType.Writing,
            Level = CefrLevel.B2,
            Type = PlacementItemType.FreeText,
            Prompt = "Write a short paragraph arguing for or against working from home. Support your view with one example.",
            Options = [],
            CorrectAnswer = null,
            Passage = null,
            AudioText = null,
            ExpectedWords = 45,
        },
        new()
        {
            Id = "wr_c1_1",
            Skill = SkillType.Writing,
            Level = CefrLevel.C1,
            Type = PlacementItemType.FreeText,
            Prompt = "Summarise an idea you have changed your mind about, and explain what changed it.",
            Options = [],
            CorrectAnswer = null,
            Passage = null,
            AudioText = null,
            ExpectedWords = 60,
        },
        new()
        {
            Id = "sl_1",
            Skill = SkillType.Spelling,
            Level = CefrLevel.A1,
            Type = PlacementItemType.MultipleChoice,
            Prompt = "Which spelling is correct?",
            Options = ["because", "becuase", "becouse", "becaus"],
            CorrectAnswer = "because",
            Passage = null,
            AudioText = null,
            ExpectedWords = 0,
        },
        new()
        {
            Id = "sl_2",
            Skill = SkillType.Spelling,
            Level = CefrLevel.A2,
            Type = PlacementItemType.MultipleChoice,
            Prompt = "Which spelling is correct?",
            Options = ["friend", "freind", "frend", "friand"],
            CorrectAnswer = "friend",
            Passage = null,
            AudioText = null,
            ExpectedWords = 0,
        },
        new()
        {
            Id = "sl_3",
            Skill = SkillType.Spelling,
            Level = CefrLevel.B1,
            Type = PlacementItemType.MultipleChoice,
            Prompt = "Which spelling is correct?",
            Options = ["environment", "enviroment", "envirnoment", "enviornment"],
            CorrectAnswer = "environment",
            Passage = null,
            AudioText = null,
            ExpectedWords = 0,
        },
        new()
        {
            Id = "sl_4",
            Skill = SkillType.Spelling,
            Level = CefrLevel.B1Plus,
            Type = PlacementItemType.MultipleChoice,
            Prompt = "Which spelling is correct?",
            Options = ["necessary", "neccessary", "necesary", "neccesary"],
            CorrectAnswer = "necessary",
            Passage = null,
            AudioText = null,
            ExpectedWords = 0,
        },
        new()
        {
            Id = "sl_5",
            Skill = SkillType.Spelling,
            Level = CefrLevel.B2,
            Type = PlacementItemType.MultipleChoice,
            Prompt = "Which spelling is correct?",
            Options = ["accommodate", "acommodate", "accomodate", "acomodate"],
            CorrectAnswer = "accommodate",
            Passage = null,
            AudioText = null,
            ExpectedWords = 0,
        },
        new()
        {
            Id = "sl_6",
            Skill = SkillType.Spelling,
            Level = CefrLevel.C1,
            Type = PlacementItemType.MultipleChoice,
            Prompt = "Which spelling is correct?",
            Options = ["conscientious", "concientious", "conscientous", "consciencious"],
            CorrectAnswer = "conscientious",
            Passage = null,
            AudioText = null,
            ExpectedWords = 0,
        },
    ];
}

public enum PlacementItemType
{
    MultipleChoice,
    FreeText,

    /// <summary>
    /// Answered out loud: the prompt is spoken, the learner replies by voice,
    /// and the transcript is what reaches the server.
    /// </summary>
    /// <remarks>
    /// Distinct from <see cref="FreeText"/> on purpose. Speaking and Writing are
    /// different skills and must be tested differently (§17) — showing a text
    /// box for a speaking item measures writing and calls it speech.
    /// </remarks>
    Spoken,
}

/// <summary>
/// What a placement item actually measures.
/// </summary>
/// <remarks>
/// Deliberately not <see cref="SkillType"/>. The four skills a learner sees are
/// Reading, Listening, Speaking and Writing; Grammar and Spelling are measured
/// too, but as *evidence* feeding those four rather than as levels of their own
/// (§13, §19, §20). Keeping the two vocabularies apart is what stops "we
/// measured it" from turning into "we must display it".
/// </remarks>
public enum PlacementDomain
{
    Reading,
    Listening,
    Speaking,
    Writing,
    Spelling,
    Grammar,
}

/// <summary>
/// One item as stored in the bank, before it is projected to the client.
/// </summary>
/// <remarks>
/// <see cref="CorrectAnswer"/> never leaves the server — the client receives
/// shuffled options only (rule R7).
/// </remarks>
public sealed record BankItem
{
    public required string Id { get; init; }

    public required SkillType Skill { get; init; }

    /// <summary>The CEFR band the item was authored for.</summary>
    public required CefrLevel Level { get; init; }

    /// <summary>
    /// What this item measures. Defaults to the item's own skill; grammar and
    /// spelling items set it explicitly, because what they measure is not what
    /// they score into.
    /// </summary>
    public PlacementDomain? Domain { get; init; }

    /// <summary>
    /// A second skill this item is evidence for.
    /// </summary>
    /// <remarks>
    /// Grammar is the reason this exists: §19 asks for it to strengthen
    /// <b>both</b> Speaking and Writing, which no single-skill field can express.
    /// The response is emitted for both skills, so a learner with solid grammar
    /// is not placed low in production skills on the strength of two prompts.
    /// </remarks>
    public SkillType? AlsoEvidenceFor { get; init; }

    public PlacementItemType Type { get; init; } = PlacementItemType.MultipleChoice;

    public required string Prompt { get; init; }

    public IReadOnlyList<string> Options { get; init; } = [];

    public string? CorrectAnswer { get; init; }

    public string? Passage { get; init; }

    /// <summary>Spoken by TTS; the learner never sees it (demo review §34).</summary>
    public string? AudioText { get; init; }

    /// <summary>
    /// Rough production length a competent answer at this band reaches. Used
    /// only by the offline fallback scorer, never by the AI evaluator.
    /// </summary>
    public int ExpectedWords { get; init; }

    /// <summary>Anything the learner produces rather than selects.</summary>
    public bool IsFreeText =>
        Type is PlacementItemType.FreeText or PlacementItemType.Spoken;

    public bool IsSpoken => Type == PlacementItemType.Spoken;

    public PlacementDomain MeasuredDomain => Domain ?? Skill switch
    {
        SkillType.Reading => PlacementDomain.Reading,
        SkillType.Listening => PlacementDomain.Listening,
        SkillType.Speaking => PlacementDomain.Speaking,
        SkillType.Writing => PlacementDomain.Writing,
        _ => PlacementDomain.Spelling,
    };
}