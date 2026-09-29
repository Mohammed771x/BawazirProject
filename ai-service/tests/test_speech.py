"""Speech-to-text: Gemini first, Groq when it fails (ADR-107).

No test reaches either provider. The Gemini client and the Groq HTTP call are
replaced per test, and the ``no_network`` fixture below makes an unstubbed
call fail loudly instead of quietly spending a free quota.
"""

from __future__ import annotations

import io
import json
import urllib.error
import urllib.request

import pytest

from app import speech
from app.gemini import GeminiError, GeminiResponse
from app.speech import SpeechSettings, Transcriber, TranscriptionError

from conftest import TOKEN

AUDIO = b"\x00\x00\x00\x18ftypM4A fake audio bytes"


def settings(**overrides) -> SpeechSettings:
    values = dict(
        gemini_api_key="stt-gemini-test-key",
        gemini_model="gemini-3.1-flash-lite",
        gemini_timeout=5,
        gemini_retries=1,
        gemini_retry_delay=0,
        groq_api_key="gsk_test",
        groq_model="whisper-large-v3",
        groq_timeout=5,
        groq_no_speech_threshold=0.6,
        max_audio_bytes=1024,
    )
    values.update(overrides)
    return SpeechSettings(**values)


@pytest.fixture(autouse=True)
def no_network(monkeypatch):
    def refuse(*_args, **_kwargs):
        raise AssertionError("A speech test reached the network. Stub it.")

    monkeypatch.setattr(urllib.request, "urlopen", refuse)


class FakeGemini:
    """Answers from a script: a string is a transcript, an exception is raised."""

    def __init__(self, *script):
        self.script = list(script)
        self.calls = []

    def generate(self, prompt, **kwargs):
        self.calls.append((prompt, kwargs))
        step = self.script.pop(0)
        if isinstance(step, BaseException):
            raise step
        return GeminiResponse(text=step, model="m", prompt_tokens=1,
                              output_tokens=1)


def transcriber(monkeypatch, gemini=None, groq=None, **overrides):
    t = Transcriber(settings(**overrides))
    if gemini is not None:
        t._gemini = gemini
    groq_calls = []

    def fake_groq(audio, mime_type):
        groq_calls.append((audio, mime_type))
        if groq is None:
            raise AssertionError("Groq was called but should not have been")
        if isinstance(groq, BaseException):
            raise groq
        return groq

    monkeypatch.setattr(t, "_with_groq", fake_groq)
    return t, groq_calls


# ── Engine order ─────────────────────────────────────────────────────────────

def test_gemini_answers_and_groq_is_never_called(monkeypatch):
    gemini = FakeGemini("I go to school yesterday.")
    t, groq_calls = transcriber(monkeypatch, gemini=gemini)

    result = t.transcribe(AUDIO, "audio/mp4")

    assert result.text == "I go to school yesterday."
    assert result.engine == "gemini"
    assert result.fallback_reason is None
    assert groq_calls == []
    # The audio reaches Gemini with its type, at temperature zero.
    _, kwargs = gemini.calls[0]
    assert kwargs["audio"] == (AUDIO, "audio/mp4")
    assert kwargs["temperature"] == 0


def test_a_busy_gemini_is_retried_before_falling_back(monkeypatch):
    gemini = FakeGemini(GeminiError("high demand", status=503), "Hello there.")
    t, groq_calls = transcriber(monkeypatch, gemini=gemini)

    result = t.transcribe(AUDIO, "audio/mp4")

    assert result.engine == "gemini"
    assert result.text == "Hello there."
    assert len(gemini.calls) == 2
    assert groq_calls == []


def test_gemini_still_refusing_after_retries_falls_back_to_groq(monkeypatch):
    gemini = FakeGemini(GeminiError("busy", status=503),
                        GeminiError("busy", status=503))
    t, groq_calls = transcriber(monkeypatch, gemini=gemini,
                                groq="He don't like coffee.")

    result = t.transcribe(AUDIO, "audio/mp4")

    assert result.engine == "groq"
    assert result.text == "He don't like coffee."
    assert "503" in result.fallback_reason
    assert groq_calls == [(AUDIO, "audio/mp4")]


def test_a_permanent_gemini_error_is_not_retried(monkeypatch):
    # A bad key or a refused format will not fix itself in a second and a
    # half — the learner goes straight to Groq.
    gemini = FakeGemini(GeminiError("bad key", status=400))
    t, _ = transcriber(monkeypatch, gemini=gemini, groq="ok")

    assert t.transcribe(AUDIO, "audio/mp4").engine == "groq"
    assert len(gemini.calls) == 1


def test_a_gemini_timeout_falls_back_without_retrying(monkeypatch):
    gemini = FakeGemini(TimeoutError("read timed out"))
    t, _ = transcriber(monkeypatch, gemini=gemini, groq="Fallback words.")

    result = t.transcribe(AUDIO, "audio/mp4")

    assert result.engine == "groq"
    assert result.text == "Fallback words."
    assert len(gemini.calls) == 1


def test_without_a_gemini_key_groq_answers_alone(monkeypatch):
    t, groq_calls = transcriber(monkeypatch, groq="Only Groq.",
                                gemini_api_key="")

    result = t.transcribe(AUDIO, "audio/mp4")

    assert result.engine == "groq"
    assert result.fallback_reason == "gemini not configured"
    assert len(groq_calls) == 1


def test_both_engines_failing_is_an_error_not_an_empty_transcript(monkeypatch):
    # An empty string would read as "you said nothing"; the learner must be
    # told the recogniser failed, so they can type instead.
    gemini = FakeGemini(GeminiError("bad", status=400))
    t, _ = transcriber(monkeypatch, gemini=gemini,
                       groq=speech.GroqError("down", status=500))

    with pytest.raises(TranscriptionError):
        t.transcribe(AUDIO, "audio/mp4")


def test_the_text_key_is_never_used_for_speech(monkeypatch):
    monkeypatch.setenv("GEMINI_API_KEY", "paid-text-key")
    monkeypatch.delenv("STT_GEMINI_API_KEY", raising=False)
    monkeypatch.delenv("GROQ_API_KEY", raising=False)

    loaded = SpeechSettings.from_env()

    assert loaded.gemini_api_key == ""
    assert not loaded.configured


# ── What comes back ──────────────────────────────────────────────────────────

def test_the_prompt_forbids_correcting_the_learner(monkeypatch):
    # Speaking is judged on this text. A recogniser that fixes "he don't" to
    # "he doesn't" passes a learner who got it wrong.
    gemini = FakeGemini("x")
    t, _ = transcriber(monkeypatch, gemini=gemini)
    t.transcribe(AUDIO, "audio/mp4")

    prompt = gemini.calls[0][0].lower()
    assert "verbatim" in prompt
    assert "never correct" in prompt


def test_silence_from_gemini_is_an_empty_transcript(monkeypatch):
    t, groq_calls = transcriber(monkeypatch, gemini=FakeGemini("[NO_SPEECH]"))

    result = t.transcribe(AUDIO, "audio/mp4")

    assert result.text == ""
    assert result.engine == "gemini"
    assert groq_calls == []


def test_quotes_wrapped_around_the_transcript_are_removed(monkeypatch):
    t, _ = transcriber(monkeypatch, gemini=FakeGemini('"I like tea."'))
    assert t.transcribe(AUDIO, "audio/mp4").text == "I like tea."


def test_whisper_segments_that_were_probably_silence_are_dropped():
    payload = {
        "text": " I like tea. Thank you.",
        "segments": [
            {"text": " I like tea.", "no_speech_prob": 0.01},
            {"text": " Thank you.", "no_speech_prob": 0.93},
        ],
    }
    assert speech._clean_whisper(payload, 0.6) == "I like tea."


def test_groq_request_is_well_formed(monkeypatch):
    sent = {}

    class Response:
        def __enter__(self):
            return self

        def __exit__(self, *_):
            return False

        def read(self):
            return json.dumps({"text": "hi", "segments": [
                {"text": "hi", "no_speech_prob": 0.0}]}).encode()

    def fake_urlopen(request, timeout):
        sent["url"] = request.full_url
        sent["headers"] = {k.lower(): v for k, v in request.header_items()}
        sent["body"] = request.data
        sent["timeout"] = timeout
        return Response()

    monkeypatch.setattr(urllib.request, "urlopen", fake_urlopen)
    t = Transcriber(settings(gemini_api_key=""))

    result = t.transcribe(AUDIO, "audio/mp4")

    assert result.text == "hi"
    assert sent["url"] == speech._GROQ_URL
    assert sent["headers"]["authorization"] == "Bearer gsk_test"
    # Groq's edge refuses Python's default User-Agent with a bare 403.
    assert "python" not in sent["headers"]["user-agent"].lower()
    assert b'name="model"\r\n\r\nwhisper-large-v3\r\n' in sent["body"]
    assert b'filename="speech.m4a"' in sent["body"]
    assert AUDIO in sent["body"]


def test_a_groq_http_error_becomes_a_groq_error(monkeypatch):
    def fake_urlopen(request, timeout):
        raise urllib.error.HTTPError(
            request.full_url, 429, "rate", {}, io.BytesIO(b'{"error":"rate"}'))

    monkeypatch.setattr(urllib.request, "urlopen", fake_urlopen)
    t = Transcriber(settings(gemini_api_key=""))

    with pytest.raises(TranscriptionError):
        t.transcribe(AUDIO, "audio/mp4")


# ── The endpoint ─────────────────────────────────────────────────────────────

def _post(client, body=AUDIO, content_type="audio/mp4", token=TOKEN):
    headers = {"Content-Type": content_type}
    if token:
        headers["X-Service-Token"] = token
    return client.post("/ai/transcribe", content=body, headers=headers)


@pytest.fixture
def stub_transcriber(monkeypatch):
    from app import main

    fake = Transcriber(settings())
    monkeypatch.setattr(main, "TRANSCRIBER", fake)
    monkeypatch.setattr(main, "SPEECH_SETTINGS", fake._settings)
    return fake


def test_endpoint_needs_the_service_token(client, stub_transcriber):
    assert _post(client, token=None).status_code == 401


def test_endpoint_returns_the_transcript(client, stub_transcriber, monkeypatch):
    monkeypatch.setattr(
        stub_transcriber, "transcribe",
        lambda audio, mime: speech.Transcription("I go home.", "gemini"))

    response = _post(client)

    assert response.status_code == 200
    assert response.json() == {"text": "I go home.", "engine": "gemini",
                               "fallback_reason": None}


def test_endpoint_refuses_what_is_not_audio(client, stub_transcriber):
    response = _post(client, content_type="application/json")
    assert response.status_code == 415
    assert response.json()["error"]["code"] == "UNSUPPORTED_AUDIO"


def test_endpoint_refuses_an_empty_body(client, stub_transcriber):
    assert _post(client, body=b"").status_code == 400


def test_endpoint_refuses_a_recording_over_the_limit(client, stub_transcriber):
    response = _post(client, body=b"x" * 2048)
    assert response.status_code == 413
    assert response.json()["error"]["code"] == "AUDIO_TOO_LARGE"


def test_both_engines_down_is_a_502(client, stub_transcriber, monkeypatch):
    def fail(audio, mime):
        raise TranscriptionError("both down")

    monkeypatch.setattr(stub_transcriber, "transcribe", fail)

    response = _post(client)
    assert response.status_code == 502
    assert response.json()["error"]["code"] == "AI_UNAVAILABLE"


def test_no_speech_keys_is_a_503(client, monkeypatch):
    from app import main

    empty = Transcriber(settings(gemini_api_key="", groq_api_key=""))
    monkeypatch.setattr(main, "TRANSCRIBER", empty)

    response = _post(client)
    assert response.status_code == 503
    assert response.json()["error"]["code"] == "SPEECH_UNAVAILABLE"
