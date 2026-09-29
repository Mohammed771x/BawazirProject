"""A tutor turn is kept to its band's length (ADR-117).

The prompt states the length; these pin the one guard behind it — a turn far
past its band gets a single rewrite, and the closing "Try to use the word …"
line comes through that rewrite exactly as it was.
"""

from __future__ import annotations

import pytest

from app import main, prompts

TRY = 'Try to use the word "tissue" in your answer.'
LONG = ("No problem! Think about your body again. Your skin covers the muscle "
        "and the bone underneath it, and all of that is made of the same soft "
        "material that heals when you get hurt. What is the name for that part "
        "of your body? " + TRY)
SHORT = "Sure! What is under your skin when you cut your finger?"


def _turn(level: str = "A2") -> dict:
    return {
        "learner_name": "Sara", "level": level,
        "remaining_words": ["tissue"], "used_words": [],
        "transcript": [
            {"from_ai": True, "text": "What is under your skin?"},
            {"from_ai": False, "text": "Can you give me another question using tissue?"},
        ],
    }


def _answer(reply: str) -> dict:
    return {"learner_intent": "new_question", "reply": reply,
            "words_only_named": ["tissue"]}


def test_the_try_line_is_set_apart_from_the_turn():
    body, tail = prompts.split_try_line(LONG)

    assert tail == TRY
    assert body.endswith("part of your body?")
    assert prompts.split_try_line("How are you?") == ("How are you?", "")


def test_only_a_turn_far_past_its_band_is_too_long():
    # The try line is not counted: 20 words of turn at A2 is on the limit.
    on_the_limit = " ".join(["word"] * 20) + " " + TRY
    assert not prompts.speaking_too_long(on_the_limit, "A2")
    assert prompts.speaking_too_long(LONG, "A2")
    # The same turn is fine for a C2 learner.
    assert not prompts.speaking_too_long(LONG, "C2")


def test_a_long_turn_is_shortened_and_keeps_its_word(client, auth, stub_gemini_sequence):
    recorder = stub_gemini_sequence([_answer(LONG), {"reply": SHORT}])

    body = client.post("/ai/speaking/turn", json=_turn(), headers=auth).json()

    assert body["reply"] == f"{SHORT} {TRY}"
    assert body["learner_intent"] == "new_question"
    assert body["words_only_named"] == ["tissue"]
    assert len(recorder.prompts) == 2
    # The rewrite is asked for at the learner's band, without the try line.
    assert "about 20 words" in recorder.prompts[1]
    assert "Try to use the word" not in recorder.prompts[1]


def test_a_turn_within_its_band_costs_one_call(client, auth, stub_gemini_sequence):
    recorder = stub_gemini_sequence([_answer(f"{SHORT} {TRY}")])

    body = client.post("/ai/speaking/turn", json=_turn(), headers=auth).json()

    assert body["reply"] == f"{SHORT} {TRY}"
    assert len(recorder.prompts) == 1


def test_a_rewrite_that_is_no_shorter_is_ignored(client, auth, stub_gemini_sequence):
    stub_gemini_sequence([_answer(LONG), {"reply": LONG + " And more words here."}])

    body = client.post("/ai/speaking/turn", json=_turn(), headers=auth).json()

    assert body["reply"] == LONG


def test_a_failed_rewrite_keeps_the_long_turn(client, auth, monkeypatch):
    """A long turn is a worse turn, not a broken one."""
    import json

    from app.gemini import GeminiError, GeminiResponse

    calls = []

    def generate(prompt, **kwargs):
        calls.append(prompt)
        if len(calls) == 1:
            return GeminiResponse(text=json.dumps(_answer(LONG)),
                                  model="m", prompt_tokens=1, output_tokens=1)
        raise GeminiError("down")

    monkeypatch.setattr(main.CLIENT, "generate", generate)

    response = client.post("/ai/speaking/turn", json=_turn(), headers=auth)

    assert response.status_code == 200
    assert response.json()["reply"] == LONG
