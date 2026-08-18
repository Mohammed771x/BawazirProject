using WordOs.Application.Lexicon;

namespace WordOs.Domain.Tests;

/// <summary>
/// Base-form candidates for words tapped inside a passage (Part 2 §17).
/// </summary>
/// <remarks>
/// The contract is deliberately weak: propose spellings, let the lexicon judge.
/// What must hold is that the exact spelling is offered first and that no
/// proposal is nonsense-short — everything else is the database's decision.
/// </remarks>
public class SurfaceFormTests
{
    [Theory]
    [InlineData("researching", "research")]
    [InlineData("studies", "study")]
    [InlineData("studied", "study")]
    [InlineData("boxes", "box")]
    [InlineData("words", "word")]
    [InlineData("walked", "walk")]
    [InlineData("liked", "like")]
    [InlineData("stopped", "stop")]
    [InlineData("running", "run")]
    [InlineData("making", "make")]
    [InlineData("quickly", "quick")]
    public void The_base_form_is_among_the_candidates(string surface, string expected)
    {
        Assert.Contains(expected, SurfaceForms.CandidatesFor(surface));
    }

    [Fact]
    public void The_exact_spelling_is_always_tried_first()
    {
        // Otherwise "bus" resolves to "bu" and "ring" to "r" — a real word
        // replaced by the wreckage of stemming it.
        Assert.Equal("bus", SurfaceForms.CandidatesFor("bus")[0]);
        Assert.Equal("ring", SurfaceForms.CandidatesFor("Ring")[0]);
        Assert.Equal("news", SurfaceForms.CandidatesFor("news")[0]);
    }

    [Fact]
    public void Candidates_are_never_shorter_than_a_real_headword()
    {
        foreach (var word in new[] { "ing", "ed", "es", "running", "a", "to" })
        {
            Assert.All(
                SurfaceForms.CandidatesFor(word).Skip(1),
                c => Assert.True(c.Length >= 2, $"'{c}' from '{word}'"));
        }
    }

    [Fact]
    public void The_undoubled_form_is_only_a_fallback()
    {
        // "running" and "falling" are the same shape, so both "run"/"runn" and
        // "fall"/"fal" have to be proposed. What keeps that safe is the order:
        // the plain stem is tried first, so "falling" resolves to "fall" and
        // only a word that isn't there falls through to the undoubled guess.
        var falling = SurfaceForms.CandidatesFor("falling");
        Assert.True(falling.ToList().IndexOf("fall") < falling.ToList().IndexOf("fal"));
        Assert.Contains("run", SurfaceForms.CandidatesFor("running"));
    }

    [Fact]
    public void An_empty_or_blank_word_produces_nothing()
    {
        Assert.Empty(SurfaceForms.CandidatesFor(""));
        Assert.Empty(SurfaceForms.CandidatesFor("   "));
    }
}
