"""The Groq client behind WordOS AI (ADR-134): what it asks for, and how it
lives inside the free tier's per-minute limit. No test reaches Groq —
``_post`` is replaced, so these run offline."""

from __future__ import annotations

import json

import pytest

from app import agent_prompts
from app.groq_chat import GroqChatClient, GroqError, GroqSettings, _retry_after


def _settings(**overrides) -> GroqSettings:
    values = dict(api_key="k", model="openai/gpt-oss-120b", reasoning_effort="low",
                  max_output_tokens=4096, min_output_tokens=1024, timeout=5, max_wait=45)
    values.update(overrides)
    return GroqSettings(**values)


def _ok(content: dict | str, finish="stop") -> dict:
    text = content if isinstance(content, str) else json.dumps(content)
    return {"choices": [{"message": {"content": text}, "finish_reason": finish}],
            "usage": {"prompt_tokens": 4000, "completion_tokens": 300}}


def _error(status: int, detail: str, retry_after: float | None = None) -> GroqError:
    e = GroqError(detail, status=status)
    e.retry_after = retry_after
    e.over_by = None
    import re
    m = re.search(r"Limit (\d+), Requested (\d+)", detail)
    if m:
        e.over_by = int(m.group(2)) - int(m.group(1))
    return e


class _Script:
    def __init__(self, *outcomes):
        self.outcomes = list(outcomes)
        self.bodies: list[dict] = []

    def __call__(self, body):
        self.bodies.append(json.loads(json.dumps(body)))
        outcome = self.outcomes.pop(0)
        if isinstance(outcome, Exception):
            raise outcome
        return outcome


def _client(script, sleeps=None, **overrides):
    client = GroqChatClient(_settings(**overrides), sleep=(sleeps.append if sleeps is not None else lambda _s: None))
    client._post = script
    return client


def test_asks_for_the_chosen_model_low_reasoning_and_the_schema():
    script = _Script(_ok({"action": "answer"}))
    response = _client(script).generate("q", system_instruction="sys",
                                         json_schema=agent_prompts.AGENT_SCHEMA, temperature=0.3)
    body = script.bodies[0]
    assert body["model"] == "openai/gpt-oss-120b"
    assert body["reasoning_effort"] == "low"
    assert body["temperature"] == 0.3
    assert body["messages"] == [{"role": "system", "content": "sys"}, {"role": "user", "content": "q"}]
    fmt = body["response_format"]
    assert fmt["type"] == "json_schema"
    assert fmt["json_schema"]["schema"] == agent_prompts.AGENT_SCHEMA
    assert response.prompt_tokens == 4000 and response.output_tokens == 300
    assert response.model == "openai/gpt-oss-120b"


def test_an_answer_groq_rejects_for_its_shape_is_asked_for_once_more():
    bad = _error(400, "Groq returned 400: json_validate_failed")
    script = _Script(bad, _ok({"action": "answer"}))
    _client(script).generate("q", json_schema=agent_prompts.AGENT_SCHEMA)
    assert len(script.bodies) == 2
    with pytest.raises(GroqError):
        _client(_Script(bad, bad)).generate("q", json_schema=agent_prompts.AGENT_SCHEMA)


def test_waits_out_the_per_minute_limit_then_answers():
    sleeps: list[float] = []
    script = _Script(_error(429, "Rate limit reached", retry_after=7), _ok({"action": "answer"}))
    _client(script, sleeps).generate("q", json_schema={"type": "object", "properties": {}})
    assert len(script.bodies) == 2
    assert sleeps and 7 <= sleeps[0] < 8


def test_gives_up_when_the_wait_would_pass_its_ceiling():
    sleeps: list[float] = []
    script = _Script(_error(429, "x", retry_after=30), _error(429, "x", retry_after=30))
    with pytest.raises(GroqError) as exc:
        _client(script, sleeps, max_wait=45).generate("q")
    assert exc.value.status == 429 and exc.value.retry_after == 30
    assert len(sleeps) == 1


def test_the_daily_limit_is_never_waited_out():
    sleeps: list[float] = []
    script = _Script(_error(429, "tokens per day (TPD): Limit 200000", retry_after=991))
    with pytest.raises(GroqError) as exc:
        _client(script, sleeps).generate("q")
    assert exc.value.retry_after == 991 and not sleeps


def test_a_request_too_large_for_the_minute_retries_with_a_smaller_ceiling():
    script = _Script(_error(413, "Request too large: Limit 8000, Requested 9500"), _ok("{}"))
    _client(script).generate("q")
    assert script.bodies[0]["max_completion_tokens"] == 4096
    assert script.bodies[1]["max_completion_tokens"] == 4096 - 1500 - 64


def test_a_request_too_large_for_any_useful_answer_fails():
    script = _Script(_error(413, "Request too large: Limit 8000, Requested 12000"))
    with pytest.raises(GroqError) as exc:
        _client(script).generate("q")
    assert exc.value.status == 413
    assert len(script.bodies) == 1


def test_a_cut_off_or_empty_answer_is_an_error_not_an_answer():
    with pytest.raises(GroqError):
        _client(_Script(_ok('{"action":"ans', finish="length"))).generate("q")
    with pytest.raises(GroqError):
        _client(_Script(_ok(""))).generate("q")


def test_without_a_key_it_fails_without_calling():
    script = _Script()
    with pytest.raises(GroqError) as exc:
        _client(script, api_key="").generate("q")
    assert exc.value.status == 503 and not script.bodies


def test_reads_how_long_to_wait_from_the_header_or_the_message():
    assert _retry_after("12", "") == 12
    assert _retry_after(None, "Please try again in 3.5s.") == 3.5
    assert _retry_after(None, "Please try again in 1m2.5s.") == 62.5
    assert _retry_after(None, "Please try again in 450ms.") == 0.45
    assert _retry_after(None, "nothing here") is None


def test_the_agent_endpoint_reports_the_groq_model(client, auth, stub_gemini):
    stub_gemini({"action": "answer", "answer": "٤٢ متعلمًا."})
    data = client.post("/ai/admin/agent", headers=auth, json={
        "message": "كم؟", "tools": "-", "dictionary": "-", "today": "2026-11-03"}).json()
    assert data["model"] == "openai/gpt-oss-120b"
    assert data["answer"] == "٤٢ متعلمًا."


def test_a_used_up_quota_reaches_the_backend_as_429_with_the_wait(client, auth, monkeypatch):
    from app import main
    monkeypatch.setattr(main, "AGENT_FALLBACK", False)

    def limited(*_a, **_k):
        e = GroqError("Groq limit", status=429)
        e.retry_after = 990.4
        raise e

    monkeypatch.setattr(main.AGENT_CLIENT, "generate", limited)
    response = client.post("/ai/admin/agent", headers=auth, json={
        "message": "كم؟", "tools": "-", "dictionary": "-", "today": "2026-11-03"})
    assert response.status_code == 429
    assert response.json()["error"] == {"code": "AI_RATE_LIMITED",
                                        "message": "The model's free quota is used up for now.",
                                        "retry_after_seconds": 991}


def _limited(*_a, **_k):
    e = GroqError("Groq limit", status=429)
    e.retry_after = 990
    raise e


def _down(*_a, **_k):
    raise GroqError("Could not reach Groq")


@pytest.mark.parametrize("failure, reason", [(_limited, "quota"), (_down, "unavailable")])
def test_when_groq_cannot_answer_gemini_does_and_says_so(client, auth, monkeypatch, failure, reason):
    from app import main
    from app.gemini import GeminiResponse

    asked = []

    def gemini(prompt, **kwargs):
        asked.append(kwargs["system_instruction"])
        return GeminiResponse(text=json.dumps({"action": "answer", "answer": "١٢ متعلمًا."}),
                              model="gemini", prompt_tokens=10, output_tokens=5)

    monkeypatch.setattr(main.AGENT_CLIENT, "generate", failure)
    monkeypatch.setattr(main.CLIENT, "generate", gemini)
    data = client.post("/ai/admin/agent", headers=auth, json={
        "message": "كم؟", "tools": "-", "dictionary": "-", "today": "2026-11-03"}).json()
    assert data["fallback"] == reason
    assert data["model"] == main.SETTINGS.gemini_model
    assert data["answer"] == "١٢ متعلمًا."
    assert asked == [agent_prompts.SYSTEM]


def test_groq_answering_is_not_a_fallback(client, auth, stub_gemini):
    stub_gemini({"action": "answer", "answer": "x"})
    data = client.post("/ai/admin/agent", headers=auth, json={
        "message": "كم؟", "tools": "-", "dictionary": "-", "today": "2026-11-03"}).json()
    assert data["fallback"] is None
