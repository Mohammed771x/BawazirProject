"""Overload and cost, which are the two ways this service hurts without failing.

Both guards here exist because the failure they prevent is invisible until the
bill or the timeout arrives:

* the admission gate turns a queue nobody can serve into a refusal the caller
  can retry (ADR-051);
* the model-cost guard stops a one-word edit from moving every call to a tier
  that costs several times as much.
"""

from __future__ import annotations

import threading

import pytest

from app import config


# ── Admission (ADR-051) ──────────────────────────────────────────────────────

def test_a_saturated_service_refuses_rather_than_queues(
        client, auth, monkeypatch):
    """503 after a short wait beats a timeout after ninety seconds."""
    from app import main

    # One slot, already taken, and no patience.
    taken = threading.BoundedSemaphore(1)
    taken.acquire()
    monkeypatch.setattr(main, "_in_flight", taken)
    monkeypatch.setattr(main, "_ADMISSION_WAIT_SECONDS", 0.01)

    response = client.post("/ai/content", json={"level": "B1"}, headers=auth)

    assert response.status_code == 503
    assert response.json()["error"]["code"] == "AI_BUSY"


def test_a_refusal_is_distinguishable_from_a_provider_failure(
        client, auth, monkeypatch):
    """The backend branches on this: 503 means retry, 502 means fall back."""
    from app import main

    taken = threading.BoundedSemaphore(1)
    taken.acquire()
    monkeypatch.setattr(main, "_in_flight", taken)
    monkeypatch.setattr(main, "_ADMISSION_WAIT_SECONDS", 0.01)

    busy = client.post("/ai/content", json={"level": "B1"}, headers=auth)

    assert busy.status_code == 503
    assert busy.json()["error"]["code"] != "AI_UNAVAILABLE"


def test_a_slot_is_released_even_when_the_provider_fails(
        client, auth, monkeypatch):
    """A leaked slot is a service that gets permanently slower after an outage."""
    from app import main
    from app.gemini import GeminiError

    gate = threading.BoundedSemaphore(2)
    monkeypatch.setattr(main, "_in_flight", gate)

    def explode(*a, **k):
        raise GeminiError("upstream down")

    monkeypatch.setattr(main.CLIENT, "generate", explode)

    for _ in range(5):
        assert client.post(
            "/ai/content", json={"level": "B1"}, headers=auth).status_code == 502

    # Both slots are free again: nothing was held by the failures.
    assert gate.acquire(blocking=False)
    assert gate.acquire(blocking=False)


def test_a_slot_is_released_on_an_unparseable_answer(
        client, auth, monkeypatch):
    from app import main
    from app.gemini import GeminiResponse

    gate = threading.BoundedSemaphore(1)
    monkeypatch.setattr(main, "_in_flight", gate)
    monkeypatch.setattr(main.CLIENT, "generate", lambda *a, **k: GeminiResponse(
        text="not json", model="m", prompt_tokens=1, output_tokens=1))

    assert client.post(
        "/ai/content", json={"level": "B1"}, headers=auth).status_code == 502
    assert gate.acquire(blocking=False)


# ── Token accounting ─────────────────────────────────────────────────────────

def test_the_token_count_travels_with_its_own_answer(
        client, auth, stub_gemini):
    """It is the experiment's own measurement; a shared global would mix it up
    between two learners arriving together (ADR-051)."""
    payload = {
        "sentences": ["A short passage about a garden."],
        "targets": [],
        "comprehension": [
            {"prompt": "What is it about?", "correct": "A garden",
             "distractors": ["a", "b", "c"]}],
        "glossary": [],
    }
    stub_gemini(payload, tokens=777)

    body = client.post(
        "/ai/content", json={"level": "B1"}, headers=auth).json()

    assert body["tokens"] == 777


# ── Model cost ───────────────────────────────────────────────────────────────

@pytest.mark.parametrize("model", [
    "gemini-3.1-pro",
    "gemini-pro-latest",
    "gemini-3-ultra",
    "gemini-deep-research",
    "gemini-3-max",
])
def test_an_expensive_tier_is_refused_by_default(monkeypatch, model):
    monkeypatch.delenv("GEMINI_ALLOW_EXPENSIVE", raising=False)

    with pytest.raises(config.ConfigurationError) as raised:
        config._guard_model_cost(model)

    assert "higher-cost tier" in str(raised.value)


@pytest.mark.parametrize("model", [
    "gemini-3.1-flash-lite",
    "gemini-2.5-flash",
])
def test_the_flash_tier_passes(monkeypatch, model):
    monkeypatch.delenv("GEMINI_ALLOW_EXPENSIVE", raising=False)

    config._guard_model_cost(model)  # does not raise


def test_spending_more_takes_an_explicit_opt_in(monkeypatch):
    monkeypatch.setenv("GEMINI_ALLOW_EXPENSIVE", "true")

    config._guard_model_cost("gemini-3.1-pro")  # does not raise


def test_startup_refuses_a_missing_gemini_key(monkeypatch):
    monkeypatch.setenv("GEMINI_API_KEY", "")
    monkeypatch.setenv("AI_SERVICE_TOKEN", "t")

    with pytest.raises(config.ConfigurationError) as raised:
        config.load_settings()

    assert "GEMINI_API_KEY is not set" in str(raised.value)
