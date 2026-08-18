using WordOs.Domain.Common;

namespace WordOs.LexiconImporter;

/// <summary>
/// The closed-class words WordNet does not carry.
/// </summary>
/// <remarks>
/// Open English WordNet is a lexicon of <b>content</b> words: nouns, verbs,
/// adjectives and adverbs. Everything that holds a sentence together — <c>is</c>,
/// <c>are</c>, <c>what</c>, <c>the</c>, <c>because</c>, <c>my</c> — is absent by
/// design, which is why a learner searching for them found nothing and could not
/// add them. Where a homograph does exist it is the wrong word: <c>are</c> is in
/// WordNet only as a unit of area (آر).
///
/// These classes are <i>closed</i>: English gains new nouns constantly and new
/// pronouns almost never, so the set below is finite and can simply be written
/// down. That is what makes hand-authoring the right answer here rather than a
/// stopgap — there is no upstream dataset to wait for, and the list does not go
/// stale.
///
/// Each entry carries the same fields as an imported row, so downstream nothing
/// knows the difference: same search, same senses, same pipeline. The Arabic
/// gloss is the meaning a learner needs, not a linguist's label, and the CEFR
/// band is the level at which the word is normally taught.
///
/// Inflections of the auxiliaries are entries in their own right — a learner
/// types <c>is</c>, not <c>be</c> — sharing the lemma so they are recognisable
/// as forms of one verb.
/// </remarks>
public static class FunctionWords
{
    /// <summary>One authored entry.</summary>
    /// <param name="Text">The word as the learner types it.</param>
    /// <param name="Pos">
    /// A closed-class tag (<c>pron</c>, <c>det</c>, <c>aux</c>, <c>modal</c>,
    /// <c>prep</c>, <c>conj</c>, <c>part</c>, <c>num</c>, <c>intj</c>). These sit
    /// alongside WordNet's <c>n/v/a/r</c> rather than replacing them, so the
    /// preposition <c>like</c> and the verb <c>like</c> stay separate senses.
    /// </param>
    /// <param name="Lemma">
    /// The dictionary form. Equal to <see cref="Text"/> except for inflections
    /// (<c>is</c> → <c>be</c>), which is what groups the forms of one verb.
    /// </param>
    /// <param name="DefinitionEn">What it does, in language a learner can read.</param>
    /// <param name="MeaningAr">The Arabic meaning shown in the app.</param>
    /// <param name="Level">Where it is normally taught.</param>
    public sealed record Entry(
        string Text,
        string Pos,
        string Lemma,
        string DefinitionEn,
        string MeaningAr,
        CefrLevel Level);

    /// <summary>
    /// Builds the rows, in the same shape the WordNet join produces.
    /// </summary>
    /// <remarks>
    /// The sense id is derived from the word and its class, so a re-import
    /// updates these rows in place exactly like every other row, and the
    /// <c>wordos-fn-</c> prefix makes their provenance obvious in the database.
    ///
    /// Frequency rank is fixed low (ahead of every WordNet sense) because these
    /// are the most common words in the language: a learner typing <c>is</c>
    /// must see the auxiliary first, not a rare noun that happens to start with
    /// the same letters.
    /// </remarks>
    public static List<LexiconRow> Build()
    {
        var rows = new List<LexiconRow>(All.Count);

        foreach (var e in All)
        {
            var normalized = e.Text.ToLowerInvariant();

            rows.Add(new LexiconRow(
                SenseId: $"wordos-fn-{e.Pos}-{normalized.Replace(' ', '-')}",
                Text: e.Text,
                TextNormalized: normalized,
                Lemma: e.Lemma,
                PartOfSpeech: e.Pos,
                DefinitionEn: e.DefinitionEn,
                MeaningAr: e.MeaningAr,
                CefrLevel: e.Level,
                // Ahead of every WordNet sense, including the ones ranked 0:
                // a learner typing "are" means the auxiliary, not the unit of
                // area that happens to share the spelling.
                FrequencyRank: -1,
                SourceFlags: "en=wordos-closed-class;ar=wordos-closed-class"));
        }

        return rows;
    }

    private static readonly CefrLevel A1 = CefrLevel.A1;
    private static readonly CefrLevel A2 = CefrLevel.A2;
    private static readonly CefrLevel B1 = CefrLevel.B1;
    private static readonly CefrLevel B2 = CefrLevel.B2;

    public static readonly IReadOnlyList<Entry> All =
    [
        // ── Personal pronouns ────────────────────────────────────────────────
        new("I", "pron", "I", "the person speaking or writing", "أنا", A1),
        new("you", "pron", "you", "the person or people being spoken to", "أنتَ / أنتِ / أنتم", A1),
        new("he", "pron", "he", "a man, boy or male animal already mentioned", "هو", A1),
        new("she", "pron", "she", "a woman, girl or female animal already mentioned", "هي", A1),
        new("it", "pron", "it", "a thing, animal or idea already mentioned", "هو / هي (لغير العاقل)", A1),
        new("we", "pron", "we", "the speaker together with others", "نحن", A1),
        new("they", "pron", "they", "the people or things already mentioned", "هم / هنّ", A1),
        new("me", "pron", "I", "the speaker, as the object of a verb", "ـني / إياي", A1),
        new("him", "pron", "he", "that male person, as the object of a verb", "ـه / إياه", A1),
        new("her", "pron", "she", "that female person, as the object of a verb", "ـها / إياها", A1),
        new("us", "pron", "we", "the speaker and others, as the object of a verb", "ـنا / إيانا", A1),
        new("them", "pron", "they", "those people or things, as the object of a verb", "ـهم / إياهم", A1),

        // ── Possessives ──────────────────────────────────────────────────────
        new("my", "det", "my", "belonging to the speaker", "ـي (ملكي)", A1),
        new("your", "det", "your", "belonging to the person spoken to", "ـك (ملكك)", A1),
        new("his", "det", "his", "belonging to him", "ـه (ملكه)", A1),
        new("its", "det", "its", "belonging to that thing", "ـه / ـها (لغير العاقل)", A1),
        new("our", "det", "our", "belonging to us", "ـنا (ملكنا)", A1),
        new("their", "det", "their", "belonging to them", "ـهم (ملكهم)", A1),
        new("mine", "pron", "mine", "the one belonging to me", "ملكي", A2),
        new("yours", "pron", "yours", "the one belonging to you", "ملكك", A2),
        new("hers", "pron", "hers", "the one belonging to her", "ملكها", A2),
        new("ours", "pron", "ours", "the one belonging to us", "ملكنا", A2),
        new("theirs", "pron", "theirs", "the one belonging to them", "ملكهم", A2),

        // ── Reflexives ───────────────────────────────────────────────────────
        new("myself", "pron", "myself", "the speaker, when they are also the object", "نفسي", A2),
        new("yourself", "pron", "yourself", "the person spoken to, as the object of their own action", "نفسك", A2),
        new("himself", "pron", "himself", "that man, as the object of his own action", "نفسه", A2),
        new("herself", "pron", "herself", "that woman, as the object of her own action", "نفسها", A2),
        new("itself", "pron", "itself", "that thing, as the object of its own action", "نفسه (لغير العاقل)", A2),
        new("ourselves", "pron", "ourselves", "us, as the object of our own action", "أنفسنا", A2),
        new("yourselves", "pron", "yourselves", "you all, as the object of your own action", "أنفسكم", B1),
        new("themselves", "pron", "themselves", "them, as the object of their own action", "أنفسهم", A2),

        // ── The verb "be", as a learner meets it ─────────────────────────────
        // The forms are separate entries because a learner types the form, not
        // the infinitive; the shared lemma is what marks them as one verb.
        new("be", "aux", "be", "to exist, or to have a quality or identity", "يكون", A1),
        new("am", "aux", "be", "the form of \"be\" used with I", "أكون (مع أنا)", A1),
        new("is", "aux", "be", "the form of \"be\" used with he, she or it", "يكون (للمفرد الغائب)", A1),
        new("are", "aux", "be", "the form of \"be\" used with you, we and they", "تكون (للجمع والمخاطب)", A1),
        new("was", "aux", "be", "the past form of \"be\" used with I, he, she and it", "كان", A1),
        new("were", "aux", "be", "the past form of \"be\" used with you, we and they", "كانوا / كنتم", A1),
        new("been", "aux", "be", "the past participle of \"be\", used after have", "قد كان", A2),
        new("being", "aux", "be", "the -ing form of \"be\"", "كائنًا / بينما يكون", B1),

        // ── "have" and "do" as auxiliaries ───────────────────────────────────
        new("have", "aux", "have", "to possess something, or to form a perfect tense", "يملك / قد (للزمن التام)", A1),
        new("has", "aux", "have", "the form of \"have\" used with he, she and it", "يملك (للمفرد الغائب)", A1),
        new("had", "aux", "have", "the past form of \"have\"", "امتلك / كان قد", A1),
        new("having", "aux", "have", "the -ing form of \"have\"", "امتلاكًا / بعد أن", B1),
        new("do", "aux", "do", "to perform an action, or to form questions and negatives", "يفعل / أداة للسؤال والنفي", A1),
        new("does", "aux", "do", "the form of \"do\" used with he, she and it", "يفعل (للمفرد الغائب)", A1),
        new("did", "aux", "do", "the past form of \"do\"", "فعل (في الماضي)", A1),
        new("done", "aux", "do", "the past participle of \"do\"", "مُنجَز / قد فُعل", A2),
        new("doing", "aux", "do", "the -ing form of \"do\"", "يقوم بـ", A1),

        // ── Modals ───────────────────────────────────────────────────────────
        new("can", "modal", "can", "to be able to do something, or to be allowed to", "يستطيع / يمكن", A1),
        new("could", "modal", "can", "the past or polite form of \"can\"", "كان يستطيع / هل يمكن", A2),
        new("may", "modal", "may", "to be possible, or to be permitted", "قد / يُسمح لـ", A2),
        new("might", "modal", "may", "to be possible but not certain", "قد (احتمال أضعف)", B1),
        new("must", "modal", "must", "to be necessary or certain", "يجب / لا بد أن", A2),
        new("shall", "modal", "shall", "used to offer or to state what will happen", "سوف (للعرض أو الالتزام)", B1),
        new("should", "modal", "should", "used to say what is the right thing to do", "ينبغي", A2),
        new("will", "modal", "will", "used to talk about the future", "سوف / سـ", A1),
        new("would", "modal", "will", "the past or polite form of \"will\"", "كان سـ / هل تودّ", A2),
        new("ought", "modal", "ought", "used with \"to\" to say what is right", "يجدر بـ", B2),

        // ── Articles and determiners ─────────────────────────────────────────
        new("a", "det", "a", "one, when the thing is not a particular one", "أداة نكرة", A1),
        new("an", "det", "a", "the form of \"a\" used before a vowel sound", "أداة نكرة (قبل حرف علة)", A1),
        new("the", "det", "the", "used before a particular thing already known", "أداة تعريف (الـ)", A1),
        new("this", "det", "this", "the one here, or the one just mentioned", "هذا / هذه", A1),
        new("that", "det", "that", "the one there, or the one already mentioned", "ذلك / تلك", A1),
        new("these", "det", "these", "the ones here", "هؤلاء / هذه (جمع)", A1),
        new("those", "det", "those", "the ones there", "أولئك / تلك (جمع)", A1),
        new("some", "det", "some", "an amount or number that is not stated", "بعض", A1),
        new("any", "det", "any", "one or some, it does not matter which", "أي / أيّة", A1),
        new("no", "det", "no", "not one, not any", "لا / ليس هناك", A1),
        new("every", "det", "every", "each one of a group, without exception", "كل", A1),
        new("each", "det", "each", "every one, taken separately", "كل واحد", A2),
        new("all", "det", "all", "the whole of something, or every one", "كل / جميع", A1),
        new("both", "det", "both", "the two together", "كلا / كلتا", A2),
        new("either", "det", "either", "one or the other of two", "أيّ من الاثنين", B1),
        new("neither", "det", "neither", "not one and not the other", "لا هذا ولا ذاك", B1),
        new("much", "det", "much", "a large amount of something uncountable", "كثير (لغير المعدود)", A1),
        new("many", "det", "many", "a large number of things", "كثير (للمعدود)", A1),
        new("more", "det", "more", "a larger amount or number", "أكثر", A1),
        new("most", "det", "most", "the largest amount or number", "معظم / الأكثر", A2),
        new("few", "det", "few", "a small number, and not many", "قليل (للمعدود)", A2),
        new("little", "det", "little", "a small amount, and not much", "قليل (لغير المعدود)", A2),
        new("less", "det", "less", "a smaller amount", "أقل", A2),
        new("several", "det", "several", "more than two but not many", "عدة", B1),
        new("enough", "det", "enough", "as much as is needed", "كافٍ", A2),
        new("another", "det", "another", "one more, or a different one", "آخر", A1),
        new("other", "det", "other", "the remaining one, or a different one", "الآخر", A1),
        new("such", "det", "such", "of that kind", "مثل هذا / كهذا", B1),
        new("own", "det", "own", "belonging to that person and nobody else", "خاص بـ", A2),

        // ── Question words ───────────────────────────────────────────────────
        new("what", "pron", "what", "used to ask which thing or which kind", "ماذا / ما", A1),
        new("who", "pron", "who", "used to ask which person", "مَن", A1),
        new("whom", "pron", "who", "the object form of \"who\"", "مَن (مفعولًا به)", B2),
        new("whose", "det", "whose", "used to ask who something belongs to", "لِمَن", A2),
        new("which", "det", "which", "used to ask about one of a known set", "أيّ", A1),
        new("when", "conj", "when", "used to ask or say at what time", "متى / عندما", A1),
        new("where", "conj", "where", "used to ask or say in what place", "أين / حيث", A1),
        new("why", "conj", "why", "used to ask for a reason", "لماذا", A1),
        new("how", "conj", "how", "used to ask in what way, or how much", "كيف / كم", A1),
        new("whether", "conj", "whether", "used to talk about a choice between possibilities", "سواء / إن كان", B1),

        // ── Prepositions ─────────────────────────────────────────────────────
        new("about", "prep", "about", "on the subject of", "عن / حول", A1),
        new("above", "prep", "above", "higher than something", "فوق", A1),
        new("across", "prep", "across", "from one side to the other", "عبر", A2),
        new("after", "prep", "after", "later than something", "بعد", A1),
        new("against", "prep", "against", "in opposition to, or touching", "ضد / مقابل", A2),
        new("along", "prep", "along", "following the length of something", "بمحاذاة", A2),
        new("among", "prep", "among", "in the middle of a group", "بين (جماعة)", B1),
        new("around", "prep", "around", "surrounding, or approximately", "حول / تقريبًا", A1),
        new("as", "prep", "as", "in the role of, or in the same way", "كـ / بصفة", A2),
        new("at", "prep", "at", "in a particular place or at a particular time", "في / عند", A1),
        new("before", "prep", "before", "earlier than something", "قبل", A1),
        new("behind", "prep", "behind", "at the back of something", "خلف", A1),
        new("below", "prep", "below", "lower than something", "تحت / أسفل", A1),
        new("beside", "prep", "beside", "next to something", "بجانب", A2),
        new("besides", "prep", "besides", "in addition to", "إضافة إلى", B1),
        new("between", "prep", "between", "in the space or time separating two things", "بين", A1),
        new("beyond", "prep", "beyond", "on the far side of, or more than", "وراء / أبعد من", B2),
        new("by", "prep", "by", "next to, or showing who did something", "بواسطة / بجانب", A1),
        new("despite", "prep", "despite", "even though something is true", "رغم", B2),
        new("during", "prep", "during", "at some point in a period of time", "خلال / أثناء", A2),
        new("except", "prep", "except", "not including", "باستثناء", B1),
        new("for", "prep", "for", "intended to help or belong to, or lasting a time", "لـ / لمدة", A1),
        new("from", "prep", "from", "starting at a place, time or person", "من", A1),
        new("in", "prep", "in", "inside something, or during a period", "في / داخل", A1),
        new("inside", "prep", "inside", "within the limits of something", "داخل", A2),
        new("into", "prep", "into", "moving to the inside of something", "إلى داخل", A1),
        new("like", "prep", "like", "similar to", "مثل", A1),
        new("near", "prep", "near", "a short distance from", "قرب", A1),
        new("of", "prep", "of", "belonging to, or part of", "لـ / من (إضافة)", A1),
        new("off", "prep", "off", "away from, or no longer on", "بعيدًا عن", A2),
        new("on", "prep", "on", "touching the top or surface of something", "على", A1),
        new("onto", "prep", "onto", "moving to a position on something", "إلى فوق", B1),
        new("out", "prep", "out", "away from the inside", "خارج", A1),
        new("outside", "prep", "outside", "beyond the limits of something", "خارج", A2),
        new("over", "prep", "over", "above, across, or more than", "فوق / أكثر من", A1),
        new("past", "prep", "past", "further than, or after a time", "بعد / متجاوزًا", A2),
        new("since", "prep", "since", "from a time in the past until now", "منذ", A2),
        new("through", "prep", "through", "from one end or side to the other", "عبر / خلال", A2),
        new("throughout", "prep", "throughout", "in every part, or for the whole time", "طوال / في كل أنحاء", B2),
        new("to", "prep", "to", "in the direction of, or used before a verb", "إلى / لـ", A1),
        new("toward", "prep", "toward", "in the direction of something", "نحو", B1),
        new("towards", "prep", "toward", "in the direction of something", "نحو", B1),
        new("under", "prep", "under", "below something, or less than", "تحت / أقل من", A1),
        new("until", "prep", "until", "up to a particular time", "حتى", A2),
        new("upon", "prep", "upon", "on, in more formal English", "على (فصحى)", B2),
        new("with", "prep", "with", "together with, or using", "مع / بواسطة", A1),
        new("within", "prep", "within", "inside a place, a group or a period", "ضمن / خلال", B1),
        new("without", "prep", "without", "not having something", "بدون", A1),

        // ── Conjunctions ─────────────────────────────────────────────────────
        new("and", "conj", "and", "used to join two things together", "و", A1),
        new("but", "conj", "but", "used to introduce something surprising or opposite", "لكن", A1),
        new("or", "conj", "or", "used to give another possibility", "أو", A1),
        new("nor", "conj", "nor", "and not — used after a negative", "ولا", B2),
        new("so", "conj", "so", "used to give a result", "لذلك / إذن", A1),
        new("yet", "conj", "yet", "but even so", "ومع ذلك", B2),
        new("because", "conj", "because", "used to give a reason", "لأن", A1),
        new("although", "conj", "although", "used to say something is true even so", "رغم أن", B1),
        new("though", "conj", "though", "although, or however", "مع أن / ومع ذلك", B1),
        new("if", "conj", "if", "used to talk about something that may happen", "إذا / لو", A1),
        new("unless", "conj", "unless", "except if", "ما لم", B1),
        new("while", "conj", "while", "during the time that, or whereas", "بينما", A2),
        new("whereas", "conj", "whereas", "used to contrast two facts", "في حين أن", B2),
        new("than", "conj", "than", "used to compare two things", "من (في المقارنة)", A1),
        new("once", "conj", "once", "as soon as something happens", "بمجرد أن", B1),

        // ── Particles and negation ───────────────────────────────────────────
        new("not", "part", "not", "used to make a sentence negative", "ليس / لا", A1),
        new("there", "part", "there", "used with \"is\" and \"are\" to say something exists", "هناك (يوجد)", A1),
        new("please", "part", "please", "used to ask for something politely", "من فضلك", A1),
        new("yes", "intj", "yes", "used to agree or to say something is true", "نعم", A1),
        new("hello", "intj", "hello", "used to greet someone", "مرحبًا", A1),
        new("goodbye", "intj", "goodbye", "used when leaving someone", "مع السلامة", A1),
        new("thanks", "intj", "thanks", "used to tell someone you are grateful", "شكرًا", A1),
    ];
}
