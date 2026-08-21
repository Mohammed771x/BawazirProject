using WordOs.LexiconImporter;

namespace WordOs.Api.Tests;

/// <summary>
/// Which forms of a word become vocabulary of their own (ADR-045).
/// </summary>
/// <remarks>
/// The product rule, in one line: a form a learner has to recognise separately
/// is a word; a form that is the word with an <c>s</c> on the end is not.
/// </remarks>
public class InflectionTests
{
    /// <summary>What Open English WordNet lists, for the words used below.</summary>
    private static readonly Dictionary<string, string[]> Listed = new()
    {
        ["go"] = ["gone", "went"],
        ["take"] = ["taken", "took"],
        ["drink"] = ["drank", "drunk"],
        ["write"] = ["written", "wrote"],
        ["put"] = ["putting"],
        ["stop"] = ["stopped", "stopping"],
        ["study"] = ["studied"],
        ["mouse"] = ["mice"],
        ["child"] = ["children"],
        ["life"] = ["lives"],
        ["hero"] = ["heroes"],
        ["have"] = ["had", "has"],
        ["say"] = ["said"],
        ["win"] = ["winning", "won"],
        ["beat"] = ["beaten"],
        ["run"] = ["ran", "running"],
    };

    private static List<Inflections.Inflected> Forms(string word, string pos) =>
        Inflections.For(word, pos, Listed.GetValueOrDefault(word, []));

    /// <summary>The distinct spellings — a spelling may serve two roles.</summary>
    private static List<string> Texts(string word, string pos) =>
        Forms(word, pos)
            .Select(f => f.Text)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(t => t)
            .ToList();

    [Theory]
    // The irregulars come from the dataset; the -ing form is derived, because
    // the dataset only records spellings that break the rule.
    [InlineData("go", new[] { "going", "gone", "went" })]
    [InlineData("take", new[] { "taken", "taking", "took" })]
    // Regular verbs are derived entirely.
    [InlineData("walk", new[] { "walked", "walking" })]
    [InlineData("lunge", new[] { "lunged", "lunging" })]
    [InlineData("study", new[] { "studied", "studying" })]
    [InlineData("stop", new[] { "stopped", "stopping" })]
    public void A_verb_brings_its_forms(string verb, string[] expected)
    {
        Assert.Equal(expected, Texts(verb, "v"));
    }

    [Theory]
    // WordNet lists nothing for these, because there is no irregular spelling
    // to record — their past *is* the word. A rule reading that silence as
    // "regular" produces "readed" and "costed", which is worse than missing.
    [InlineData("read")]
    [InlineData("cost")]
    [InlineData("hurt")]
    [InlineData("spread")]
    [InlineData("broadcast")]
    public void A_verb_whose_past_is_itself_invents_nothing(string verb)
    {
        var texts = Texts(verb, "v");

        Assert.DoesNotContain(verb + "ed", texts);
        // The -ing form is still real and still added.
        Assert.Contains(verb + "ing", texts);
    }

    [Fact]
    public void Put_keeps_its_doubled_ing_and_gains_no_past()
    {
        var texts = Texts("put", "v");

        Assert.Equal(["putting"], texts);
        Assert.DoesNotContain("putted", texts);
    }

    [Theory]
    // Ends in `n`: gone, taken, written. Or the ablaut pair, where the past
    // carries `a` and the participle `u`: drank/drunk.
    [InlineData("go", "went", "gone")]
    [InlineData("take", "took", "taken")]
    [InlineData("write", "wrote", "written")]
    [InlineData("drink", "drank", "drunk")]
    public void The_past_and_the_participle_are_not_swapped(
        string verb, string past, string participle)
    {
        var forms = Forms(verb, "v");

        // The dataset lists them alphabetically — "gone, went" — so taking the
        // first as the past taught that "gone" is the past tense of "go".
        Assert.Equal(Inflections.Form.Past,
            forms.Single(f => f.Text == past).Form);
        Assert.Equal(Inflections.Form.PastParticiple,
            forms.Single(f => f.Text == participle).Form);
    }

    [Theory]
    [InlineData("mouse", "mice")]
    [InlineData("child", "children")]
    // Missing from the dataset, so written by hand.
    [InlineData("woman", "women")]
    [InlineData("person", "people")]
    public void A_plural_that_looks_different_is_a_word(string noun, string plural)
    {
        var forms = Forms(noun, "n");

        Assert.Equal(plural, forms.Single().Text);
        Assert.Equal(Inflections.Form.Plural, forms.Single().Form);
    }

    [Theory]
    // The rule the product asked for: an `-s` plural is the same word, and
    // adding it would fill a learner's vocabulary with rows they already know.
    [InlineData("book")]
    [InlineData("city")]
    [InlineData("box")]
    [InlineData("hero")]
    public void A_plural_that_is_just_an_s_is_not(string noun)
    {
        Assert.Empty(Forms(noun, "n"));
    }

    [Theory]
    // One spelling, two things to learn: "I played" and "I have played". The
    // learner picks which they are practising, and the session says which it
    // is asking for (ADR-046).
    [InlineData("play", "played")]
    [InlineData("walk", "walked")]
    [InlineData("say", "said")]
    [InlineData("win", "won")]
    public void A_spelling_that_serves_both_roles_is_offered_as_both(
        string verb, string form)
    {
        var forms = Inflections.For(verb, "v", Listed.GetValueOrDefault(verb, []));
        var roles = forms.Where(f => f.Text == form).Select(f => f.Form).ToList();

        Assert.Contains(Inflections.Form.Past, roles);
        Assert.Contains(Inflections.Form.PastParticiple, roles);
    }

    [Fact]
    public void A_form_that_is_only_the_participle_is_not_called_the_past()
    {
        // `beat / beat / beaten` — the past is the word itself, so claiming
        // "beaten" is the past tense would teach something false. The data
        // cannot say this: the other form is the base, so there is nothing for
        // WordNet to list.
        var forms = Inflections.For("beat", "v", ["beaten"]);

        Assert.Equal(Inflections.Form.PastParticiple,
            forms.Single(f => f.Text == "beaten").Form);
        Assert.DoesNotContain(forms, f =>
            f.Text == "beaten" && f.Form == Inflections.Form.Past);
    }

    [Fact]
    public void A_form_that_is_only_the_past_is_not_called_the_participle()
    {
        // `run / ran / run`.
        var forms = Inflections.For("run", "v", ["ran", "running"]);

        Assert.Equal(Inflections.Form.Past,
            forms.Single(f => f.Text == "ran").Form);
        Assert.DoesNotContain(forms, f =>
            f.Text == "ran" && f.Form == Inflections.Form.PastParticiple);
    }

    [Fact]
    public void A_form_already_authored_by_hand_is_not_added_twice()
    {
        // `has` is written as an auxiliary with the meaning a learner needs
        // (ADR-033); `had` likewise. The verb "have" must not shadow them.
        var texts = Texts("have", "v");

        Assert.DoesNotContain("has", texts);
        Assert.DoesNotContain("had", texts);
    }

    [Fact]
    public void A_phrase_is_left_alone()
    {
        // "give up" inflects on its head verb, which is more than a spelling
        // rule can do honestly.
        Assert.Empty(Inflections.For("give up", "v", []));
        Assert.Empty(Inflections.For("alarm clock", "n", []));
    }
}
