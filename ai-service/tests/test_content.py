"""What the service does with what Gemini hands back.

Rule R2 says the model returns structured output and the backend decides. That
puts a specific burden here: whatever the model produces, this service must
turn it into something the backend can act on, or refuse it outright. It must
never pass a half-formed answer through and let the C# side discover it.

Every test stubs the provider, so none of them costs anything to run.
"""

from __future__ import annotations

import json

import pytest


def _content_request(**overrides) -> dict:
    body = {
        "level": "B1",
        "interests": ["technology"],
        "words": [
            {"text": "garden", "meaning": "بستان", "definition": "a plot of ground",
             "part_of_speech": "n"},
        ],
        "listening": False,
        "comprehension_count": 5,
    }
    body.update(overrides)
    return body


def _payload(**overrides) -> dict:
    body = {
        "sentences": [
            "The morning was quiet.",
            "She walked through the garden before breakfast.",
            "Later the sun came out.",
        ],
        "targets": [{"word": "garden", "sentence_index": 1}],
        "comprehension": [
            {"prompt": "Where did she walk?", "correct": "The garden",
             "distractors": ["The street", "The shop", "The office", "Extra"]},
        ],
        "glossary": [
            {"word": "garden", "meaning_ar": "بستان", "part_of_speech": "n"},
        ],
    }
    body.update(overrides)
    return body


def test_a_passage_comes_back_whole(client, auth, stub_gemini):
    stub_gemini(_payload(), tokens=456)

    response = client.post("/ai/content", json=_content_request(), headers=auth)

    assert response.status_code == 200
    body = response.json()
    assert body["sentences"] == _payload()["sentences"]
    assert body["text"].startswith("The morning was quiet.")
    assert body["tokens"] == 456
    assert body["prompt_version"] == "reading-v3"


def test_the_target_word_carries_its_neighbours(client, auth, stub_gemini):
    """The context is read out of the array, never trusted from the model."""
    stub_gemini(_payload())

    body = client.post(
        "/ai/content", json=_content_request(), headers=auth).json()

    context = body["contexts"][0]
    assert context["word"] == "garden"
    assert context["before"] == "The morning was quiet."
    assert context["sentence"].startswith("She walked through the garden")
    assert context["after"] == "Later the sun came out."


def test_a_target_index_out_of_range_falls_back_to_searching(
        client, auth, stub_gemini):
    """An off-by-one in the model must not produce a context without the word."""
    stub_gemini(_payload(targets=[{"word": "garden", "sentence_index": 99}]))

    body = client.post(
        "/ai/content", json=_content_request(), headers=auth).json()

    assert body["contexts"][0]["sentence"].startswith("She walked through")


def test_a_target_the_passage_never_used_is_dropped_not_faked(
        client, auth, stub_gemini):
    stub_gemini(_payload(targets=[{"word": "absent", "sentence_index": None}]))

    body = client.post(
        "/ai/content", json=_content_request(), headers=auth).json()

    assert body["contexts"] == []


def test_a_first_sentence_target_has_no_before(client, auth, stub_gemini):
    stub_gemini(_payload(targets=[{"word": "morning", "sentence_index": 0}]))

    context = client.post(
        "/ai/content", json=_content_request(), headers=auth).json()["contexts"][0]

    assert context["before"] is None
    assert context["after"] is not None


def test_every_question_gets_exactly_four_options(client, auth, stub_gemini):
    """Five distractors were sent; three survive, so the client shows four."""
    stub_gemini(_payload())

    body = client.post(
        "/ai/content", json=_content_request(), headers=auth).json()

    assert len(body["comprehension"][0]["distractors"]) == 3


def test_a_question_missing_its_answer_is_discarded(client, auth, stub_gemini):
    stub_gemini(_payload(comprehension=[
        {"prompt": "Unanswerable?", "distractors": ["a", "b", "c"]},
        {"prompt": "Where did she walk?", "correct": "The garden",
         "distractors": ["a", "b", "c"]},
    ]))

    body = client.post(
        "/ai/content", json=_content_request(), headers=auth).json()

    assert [q["prompt"] for q in body["comprehension"]] == ["Where did she walk?"]


def test_the_glossary_is_deduplicated_on_the_surface_form(
        client, auth, stub_gemini):
    stub_gemini(_payload(glossary=[
        {"word": "garden", "meaning_ar": "بستان", "part_of_speech": "n"},
        {"word": "Garden", "meaning_ar": "بستان", "part_of_speech": "n"},
        {"word": "sun", "meaning_ar": "شمس", "part_of_speech": "n"},
        {"word": "", "meaning_ar": "x", "part_of_speech": "n"},
        {"word": "quiet", "meaning_ar": "", "part_of_speech": "adj"},
    ]))

    glossary = client.post(
        "/ai/content", json=_content_request(), headers=auth).json()["glossary"]

    assert [g["word"] for g in glossary] == ["garden", "sun"]


def test_a_passage_with_no_sentences_is_refused_not_returned(
        client, auth, stub_gemini):
    """The backend's fallback exists for exactly this; it needs to be told."""
    stub_gemini(_payload(sentences=["", "   "]))

    response = client.post("/ai/content", json=_content_request(), headers=auth)

    assert response.status_code == 502
    assert response.json()["error"]["code"] == "AI_UNAVAILABLE"


def test_unparseable_json_from_the_model_is_a_502(client, auth, monkeypatch):
    from app import main
    from app.gemini import GeminiResponse

    monkeypatch.setattr(main.CLIENT, "generate", lambda *a, **k: GeminiResponse(
        text="I'm afraid I can't do that.", model="m",
        prompt_tokens=1, output_tokens=1))

    response = client.post("/ai/content", json=_content_request(), headers=auth)

    assert response.status_code == 502
    assert response.json()["error"]["code"] == "AI_UNAVAILABLE"


def test_a_provider_failure_is_a_502_with_a_stable_code(
        client, auth, monkeypatch):
    from app import main
    from app.gemini import GeminiError

    def explode(*a, **k):
        raise GeminiError("upstream refused", status=500)

    monkeypatch.setattr(main.CLIENT, "generate", explode)

    response = client.post("/ai/content", json=_content_request(), headers=auth)

    assert response.status_code == 502
    assert response.json()["error"]["code"] == "AI_UNAVAILABLE"


def test_a_provider_failure_never_becomes_an_unhandled_500(
        client, auth, monkeypatch):
    """A 500 here would reach the learner as a broken session, not a fallback."""
    from app import main
    from app.gemini import GeminiError

    monkeypatch.setattr(main.CLIENT, "generate", lambda *a, **k: (_ for _ in ()).throw(
        GeminiError("timeout")))

    assert client.post(
        "/ai/content", json=_content_request(), headers=auth).status_code == 502


# ── Request validation ───────────────────────────────────────────────────────

@pytest.mark.parametrize("body", [
    {},
    _content_request(comprehension_count=0),
    _content_request(comprehension_count=99),
    _content_request(level="x" * 20),
    _content_request(words=[{"text": "a", "meaning": "b"}] * 40),
    _content_request(interests=["x"] * 50),
])
def test_a_malformed_request_is_422_not_a_generation(client, auth, body):
    """Nothing reaches Gemini: a bad request must not cost anything."""
    assert client.post("/ai/content", json=body, headers=auth).status_code == 422


def test_a_request_carrying_only_a_level_is_valid_and_does_generate(
        client, auth, stub_gemini):
    """Every other field has a default, so this is a practice passage.

    Worth pinning down: it looks like an incomplete request and is not, and a
    test written on the opposite assumption is what first sent a live call to
    the provider.
    """
    recorder = stub_gemini(_payload(targets=[]))

    assert client.post(
        "/ai/content", json={"level": "B1"}, headers=auth).status_code == 200
    assert len(recorder.prompts) == 1


def test_a_passage_with_no_words_is_allowed(client, auth, stub_gemini):
    """Practice has no vocabulary attached to it (Part 2 §5)."""
    stub_gemini(_payload(targets=[]))

    response = client.post(
        "/ai/content", json=_content_request(words=[]), headers=auth)

    assert response.status_code == 200


def test_writing_evaluation_returns_observations_not_a_verdict(
        client, auth, stub_gemini):
    """R2: this service observes, the backend decides. No pass/fail field."""
    stub_gemini({
        "used_word": True,
        "meaning_correct": True,
        "usage_correct": False,
        "understandable": True,
        "grammar_note": "tense",
        "feedback": "قريب جدًا.",
        "suggestion": "I went to the garden.",
    })

    body = client.post("/ai/writing", headers=auth, json={
        "word": "garden",
        "meaning": "بستان",
        "definition": "a plot of ground",
        "level": "B1",
        "sentence": "I go to the garden yesterday.",
    }).json()

    assert body["used_word"] is True
    assert body["usage_correct"] is False
    assert "passed" not in body
    assert "correct" not in body


def test_writing_coerces_a_missing_field_rather_than_crashing(
        client, auth, stub_gemini):
    """A model that omits half its schema still yields a usable observation."""
    stub_gemini({"used_word": True})

    response = client.post("/ai/writing", headers=auth, json={
        "word": "garden", "meaning": "بستان", "level": "B1",
        "sentence": "The garden is green.",
    })

    assert response.status_code == 200
    body = response.json()
    assert body["meaning_correct"] is False
    assert body["grammar_note"] == "none"


def test_an_empty_written_sentence_is_refused(client, auth):
    response = client.post("/ai/writing", headers=auth, json={
        "word": "garden", "meaning": "بستان", "level": "B1", "sentence": "",
    })

    assert response.status_code == 422
