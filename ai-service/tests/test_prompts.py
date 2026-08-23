"""The prompt builders.

These are pure functions and the only part of this service that encodes
teaching decisions — how long a passage should be, which form of a word the
passage must use, which language the feedback is written in. They are also the
easiest thing in the project to break silently: a prompt that quietly stops
saying "use the past participle" produces content that looks perfect and tests
the wrong word.
"""

from __future__ import annotations

import pytest

from app import prompts


# ── Passage length ───────────────────────────────────────────────────────────
#
# Sized against what public examinations actually put in front of a learner at
# each band, not guessed (ADR-066): A2 Key caps a text at 230 words, B2 First's
# Part 5 text runs 500–600, and C1 Advanced's whole paper is 3,000–3,500.


def _passage_words(level: str, count: int = 1, *, listening: bool = False) -> int:
    return prompts._passage_shape(level, count, listening=listening)[0]


def test_a_higher_band_gets_a_longer_passage():
    assert _passage_words("A1") < _passage_words("C1")


def test_the_ladder_never_goes_backwards():
    lengths = [_passage_words(level) for level in [
        "A1", "A1_PLUS", "A2", "A2_PLUS", "B1", "B1_PLUS",
        "B2", "B2_PLUS", "C1", "C1_PLUS", "C2"]]

    assert lengths == sorted(lengths), lengths


def test_the_bands_are_sized_like_the_exams_they_stand_in_for():
    """A C2 passage of a dozen sentences is not C2 reading practice, whatever
    its vocabulary — the complaint that started this."""
    # A2 Key: no reading text exceeds 230 words.
    assert _passage_words("A2") <= 230
    # B2 First, Part 5: a single text of 500–600 words.
    assert 350 <= _passage_words("B2") <= 600
    # C1 Advanced's long text, and C2 above it.
    assert _passage_words("C1") >= 500
    assert _passage_words("C2") >= 650


def test_a_listening_clip_is_shorter_than_the_same_reading():
    """Heard once, with nothing to go back to (Part 2 §24)."""
    assert _passage_words("B2", listening=True) < _passage_words("B2")


def test_every_target_word_still_fits_when_there_are_many():
    """The floor is the words themselves, whatever the band says: each needs a
    sentence of its own with neighbours that hint at its meaning."""
    assert _passage_words("A1", 12, listening=True) >= 12 * 30


def test_an_unknown_band_falls_back_rather_than_raising():
    assert _passage_words("Z9") > 0


def test_the_half_steps_are_sized_like_their_own_band():
    """A1+ is a beginner reading a beginner's passage. The table used to know
    only the whole bands, so every "+" level fell through to the default and an
    A1+ learner was handed a B1-length text — five of the eleven levels."""
    for lower, upper in [("A1", "A1_PLUS"), ("A2", "A2_PLUS"),
                         ("B1", "B1_PLUS"), ("B2", "B2_PLUS"),
                         ("C1", "C1_PLUS")]:
        assert _passage_words(lower) <= _passage_words(upper) <= _passage_words("C2"), f"{upper} is sized wrong"

    assert _passage_words("A1_PLUS") < _passage_words("B1")


def test_sentence_length_climbs_with_the_band():
    """Under 10 words is A1–A2, 10–16 is B1–B2, over 23 is C1–C2 prose."""
    assert "6 to 9" in prompts._passage_shape("A1", 1, listening=False)[1]
    assert prompts._passage_shape("C2", 1, listening=False)[1] != \
        prompts._passage_shape("A1", 1, listening=False)[1]


# ── What the passage is *for* ────────────────────────────────────────────────

def test_the_interests_are_a_setting_and_say_so():
    """A tin "can" in a paragraph about programming teaches a use of the word
    that does not exist. The words come first; the topic yields."""
    prompt = prompts.reading_prompt(
        level="B1", interests=["technology"],
        words=[{"text": "can", "meaning": "علبة", "definition": "a metal container",
                "part_of_speech": "n"}],
        listening=False, comprehension_count=5)

    assert "NATURAL USE COMES FIRST" in prompt
    assert "lowest priority" in prompt
    # And the model is told not to announce the topic back at the learner.
    assert "Since you are interested in" in prompt


def test_the_passage_is_asked_for_a_title():
    prompt = prompts.reading_prompt(
        level="B1", interests=[], words=[], listening=False,
        comprehension_count=5)

    assert "title" in prompt.lower()
    assert "title" in prompts.READING_SCHEMA["required"]


def test_a_re_told_passage_keeps_the_rules_that_matter():
    prompt = prompts.relevel_prompt(
        text="A short passage.", from_level="B1", to_level="C1",
        words=[], comprehension_count=5)

    assert "NATURAL USE STILL COMES FIRST" in prompt
    assert "title" in prompt.lower()
    # And it is sized for the band it is moving to.
    assert str(_passage_words("C1")) in prompt


def test_no_prompt_leads_with_the_learners_interests():
    """Natural use first, everywhere (ADR-066, ADR-069).

    The interests exist to make the practice pleasant. They opened the reading
    prompt once, and the model obeyed the topic before it obeyed the words —
    which is how a tin "can" ended up in a paragraph about programming. Any
    prompt that mentions them must rank them, and must rank them last.
    """
    reading = prompts.reading_prompt(
        level="B1", interests=["technology"], words=[], listening=False,
        comprehension_count=5)
    speaking = prompts.speaking_turn_prompt(
        learner_name="Ahmed", level="B1", remaining_words=["can"],
        used_words=[], interests=["technology"],
        transcript=[
            {"from_ai": True, "text": "Hello."},
            {"from_ai": False, "text": "Hi, I was working on my computer."},
        ],
        remaining_shapes=[{"text": "can", "meaning": "علبة",
                           "definition": "a metal container",
                           "part_of_speech": "noun"}])

    for name, prompt in [("reading", reading), ("speaking", speaking)]:
        lowered = prompt.lower()
        assert "lowest priority" in lowered, f"{name} does not rank interests"
        # And the word's own sense outranks the topic in both.
        assert "natural use" in lowered, f"{name} does not lead with natural use"

    # The speaking tutor is shown what the word actually means, not just how it
    # is spelled — without that it cannot tell a tin from the modal verb.
    assert "a metal container" in speaking


def test_the_closing_turn_is_a_goodbye_even_when_a_word_was_missed():
    """A conversation opens with a greeting and closes with a closing turn.
    Running out of turns is not a reason to stop mid-question (ADR-070)."""
    closing = prompts.speaking_turn_prompt(
        learner_name="Ahmed", level="B1", remaining_words=[],
        used_words=["research"], unused_words=["several"],
        transcript=[
            {"from_ai": True, "text": "And what did you read?"},
            {"from_ai": False, "text": "A book about the sea."},
        ])

    assert "Do NOT ask another question" in closing
    assert "several" in closing, 'the goodbye must know which word was missed'
    assert "Do NOT list what they missed" in closing
    assert "practised every word" not in closing, \
        'it must not congratulate them on a word they never said'


def test_a_half_step_band_uses_its_base_register():
    assert prompts.register_for("B1_PLUS") == prompts.register_for("B1")
    assert prompts.register_for("B1+") == prompts.register_for("B1")


def test_an_unknown_band_gets_the_middle_register():
    assert prompts.register_for("nonsense") == prompts.register_for("B1")
    assert prompts.register_for("") == prompts.register_for("B1")


# ── Which form of the word the passage must use (ADR-047) ────────────────────

def test_an_inflected_entry_demands_that_exact_form():
    line = prompts._target_line(
        {"text": "gone", "part_of_speech": "v", "definition": "past participle of go",
         "form": "past participle"})

    assert 'use exactly "gone"' in line
    assert "past participle" in line


def test_a_regular_noun_may_appear_in_the_plural():
    line = prompts._target_line(
        {"text": "book", "part_of_speech": "n", "definition": "a written work",
         "may_pluralise": True})

    assert "plural" in line


def test_an_irregular_noun_may_not():
    """`mice` is a word a `mouse` learner has not met."""
    line = prompts._target_line(
        {"text": "mouse", "part_of_speech": "n", "definition": "a small rodent"})

    assert 'use exactly "mouse"' in line
    assert "plural" not in line


def test_the_form_wins_over_pluralisation():
    line = prompts._target_line(
        {"text": "played", "form": "past", "may_pluralise": True,
         "part_of_speech": "v", "definition": "did play"})

    assert "the past" in line
    assert "plural" not in line


def test_a_reused_word_is_asked_for_in_the_form_it_was_learned():
    assert "the past, exactly" in prompts._reuse_shape(
        {"text": "went", "form": "past"})
    assert "singular or plural" in prompts._reuse_shape(
        {"text": "book", "may_pluralise": True})
    assert "(exactly)" in prompts._reuse_shape({"text": "mouse"})


# ── Feedback language (ADR-035) ──────────────────────────────────────────────

@pytest.mark.parametrize("tag", ["ar", "AR", "ar-SA", "ar_EG"])
def test_arabic_feedback_is_asked_for_in_arabic(tag):
    rule = prompts.feedback_language_rule(tag)

    assert "Arabic" in rule
    # And the English being taught is explicitly protected from it.
    assert "do not transliterate" in rule


@pytest.mark.parametrize("tag", ["en", "en-GB", "", None])
def test_anything_else_defaults_sensibly(tag):
    rule = prompts.feedback_language_rule(tag)

    assert "Arabic" in rule if (tag or "ar").lower().startswith("ar") \
        else "English" in rule


# ── The assembled passage prompt ─────────────────────────────────────────────

def _words() -> list[dict]:
    return [
        {"text": "garden", "meaning": "بستان", "definition": "a plot of ground",
         "part_of_speech": "n", "may_pluralise": True},
        {"text": "gone", "meaning": "ذهب", "definition": "past participle of go",
         "part_of_speech": "v", "form": "past participle"},
    ]


def test_the_prompt_names_every_target_word():
    prompt = prompts.reading_prompt(
        level="B1", interests=["technology"], words=_words(),
        listening=False, comprehension_count=5)

    assert "garden" in prompt
    assert "gone" in prompt


def test_the_prompt_carries_the_learners_interests():
    prompt = prompts.reading_prompt(
        level="B1", interests=["football", "cooking"], words=_words(),
        listening=False, comprehension_count=5)

    assert "football" in prompt


def test_no_interests_still_produces_a_usable_prompt():
    prompt = prompts.reading_prompt(
        level="B1", interests=[], words=_words(),
        listening=False, comprehension_count=5)

    assert "everyday student life" in prompt


def test_the_requested_number_of_questions_reaches_the_prompt():
    prompt = prompts.reading_prompt(
        level="B1", interests=[], words=_words(),
        listening=False, comprehension_count=7)

    assert "7" in prompt


def test_listening_and_reading_ask_for_different_things():
    common = dict(level="B1", interests=["travel"], words=_words(),
                  comprehension_count=5)

    assert prompts.reading_prompt(listening=True, **common) \
        != prompts.reading_prompt(listening=False, **common)


def test_the_prompt_version_is_stated_and_stable():
    """It is recorded against every session, so it must not drift silently."""
    assert prompts.READING_PROMPT_VERSION == "reading-v4"
    assert prompts.WRITING_PROMPT_VERSION == "writing-eval-v1"


def test_the_schema_constrains_the_answer():
    """Structured output is what makes rule R2 possible at all."""
    assert prompts.READING_SCHEMA["type"] == "object"
    assert "sentences" in prompts.READING_SCHEMA["properties"]
    assert "comprehension" in prompts.READING_SCHEMA["properties"]
