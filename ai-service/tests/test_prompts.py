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


# ── Passage length (Part 2 §24) ──────────────────────────────────────────────

def test_a_higher_band_gets_a_longer_passage():
    a1 = prompts._sentence_count("A1", 1, listening=False)
    c1 = prompts._sentence_count("C1", 1, listening=False)

    assert a1 < c1


def test_a_listening_clip_is_shorter_than_the_same_reading():
    read = prompts._sentence_count("B2", 1, listening=False)
    heard = prompts._sentence_count("B2", 1, listening=True)

    assert heard == read - 2


def test_every_target_word_still_fits_when_there_are_many():
    """The floor is the words themselves, whatever the band says."""
    assert prompts._sentence_count("A1", 12, listening=True) >= 12


def test_an_unknown_band_falls_back_rather_than_raising():
    assert prompts._sentence_count("Z9", 1, listening=False) > 0


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
    assert prompts.READING_PROMPT_VERSION == "reading-v3"
    assert prompts.WRITING_PROMPT_VERSION == "writing-eval-v1"


def test_the_schema_constrains_the_answer():
    """Structured output is what makes rule R2 possible at all."""
    assert prompts.READING_SCHEMA["type"] == "object"
    assert "sentences" in prompts.READING_SCHEMA["properties"]
    assert "comprehension" in prompts.READING_SCHEMA["properties"]
