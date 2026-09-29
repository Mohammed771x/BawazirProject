"""The tutor's and Listening's voice with word timings (ADR-108, ADR-110).

No test reaches Edge, Gemini or Groq: ``edge_speak``, ``generate_speech`` and
``groq_post`` are replaced per test, and the network itself is refused.
"""

from __future__ import annotations

import array
import asyncio
import io
import math
import sys
import types
import urllib.request
import wave

import pytest

from app import tts
from app.gemini import GeminiError, GeminiSpeech
from app.speech import GroqError
from app.tts import (EdgeError, Synthesizer, SynthesisError, TtsSettings,
                     align, clean_edges, edge_speak, estimate, mp3_duration_ms,
                     sentences, to_compressed)

from conftest import TOKEN

RATE = 24000


def settings(**overrides) -> TtsSettings:
    values = dict(
        gemini_api_key="free-tts-key",
        models=("model-a", "model-b"),
        voice="Kore",
        gemini_timeout=5,
        groq_api_key="gsk_test",
        groq_model="whisper-large-v3-turbo",
        groq_timeout=5,
        min_match=0.85,
        max_extra=0.15,
        max_chars=200,
        mp3_kbps=48,
        cache_entries=10,
        cache_seconds=60,
        # Gemini alone unless a test says otherwise: most of what follows is
        # about Gemini's path, and Edge has its own section.
        providers=("gemini",),
        edge_voice="en-US-AvaMultilingualNeural",
        edge_timeout=5,
    )
    values.update(overrides)
    return TtsSettings(**values)


def pcm(seconds: float) -> bytes:
    return b"\x00\x00" * int(RATE * seconds)


def heard(*words_and_times):
    return [{"word": w, "start": s, "end": e} for w, s, e in words_and_times]


@pytest.fixture(autouse=True)
def no_network(monkeypatch):
    def refuse(*_a, **_k):
        raise AssertionError("A voice test reached the network. Stub it.")

    monkeypatch.setattr(urllib.request, "urlopen", refuse)
    monkeypatch.setattr(tts, "edge_speak", refuse)


@pytest.fixture
def providers(monkeypatch):
    """Scripted Gemini and Groq. Each entry is a result or an exception."""
    state = {"gemini": [], "groq": [], "edge": [], "gemini_calls": [],
             "groq_calls": [], "edge_calls": []}

    def fake_edge(text, voice, timeout, parallel=4, hedge_after=0, hedge_per_char=0):
        state["edge_calls"].append((text, voice))
        step = state["edge"].pop(0)
        if isinstance(step, BaseException):
            raise step
        return step

    def fake_speech(api_key, model, text, voice, timeout):
        state["gemini_calls"].append((api_key, model, text, voice))
        step = state["gemini"].pop(0)
        if isinstance(step, BaseException):
            raise step
        return GeminiSpeech(pcm=step, sample_rate=RATE, model=model)

    def fake_groq(api_key, fields, audio, mime_type, timeout):
        state["groq_calls"].append(dict(fields))
        state.setdefault("groq_audio", []).append(audio)
        step = state["groq"].pop(0)
        if isinstance(step, BaseException):
            raise step
        return {"words": step}

    monkeypatch.setattr(tts, "edge_speak", fake_edge)
    monkeypatch.setattr(tts, "generate_speech", fake_speech)
    monkeypatch.setattr(tts, "groq_post", fake_groq)
    return state


TEXT = "I don't like coffee."
GOOD = heard(("I", 0.1, 0.2), ("don't", 0.2, 0.5), ("like", 0.5, 0.8),
             ("coffee.", 0.8, 1.3))


# ── Alignment ────────────────────────────────────────────────────────────────

def test_every_word_gets_its_heard_time_and_its_place_in_the_text():
    words, match, extra = align(TEXT, GOOD, 1500)

    assert match == 1.0 and extra == 0.0
    assert [TEXT[w.char_start:w.char_end] for w in words] == [
        "I", "don't", "like", "coffee"]
    assert (words[1].start_ms, words[1].end_ms) == (200, 500)


def test_a_word_whisper_missed_is_placed_between_its_neighbours():
    words, match, _ = align(
        TEXT, heard(("I", 0.1, 0.2), ("like", 0.5, 0.8), ("coffee", 0.8, 1.3)),
        1500)

    assert match == 0.75
    missed = words[1]
    assert 200 <= missed.start_ms <= missed.end_ms <= 500


def test_times_never_step_backwards():
    # Whisper occasionally overlaps neighbouring words; a playhead that
    # jumps back reads as a bug.
    words, _, _ = align(
        "one two three",
        heard(("one", 0.0, 0.6), ("two", 0.5, 0.9), ("three", 0.8, 1.2)), 1300)

    starts = [w.start_ms for w in words]
    assert starts == sorted(starts)
    assert all(w.end_ms >= w.start_ms for w in words)


def test_words_heard_that_the_text_does_not_have_are_counted():
    # The case that was measured: the model read an instruction aloud.
    _, match, extra = align(
        "I like tea.",
        heard(("Say", 0, .2), ("warmly", .2, .6), ("I", .8, .9),
              ("like", .9, 1.1), ("tea", 1.1, 1.4)), 1500)

    assert match == 1.0
    assert extra == pytest.approx(2 / 3)


def test_estimated_timings_cover_the_clip_in_order():
    words = estimate("short and a considerably longer word", 3000)

    assert words[0].start_ms == 0
    assert words[-1].end_ms == 3000
    assert [w.start_ms for w in words] == sorted(w.start_ms for w in words)
    # Longer words take longer.
    assert (words[3].end_ms - words[3].start_ms) > (
        words[2].end_ms - words[2].start_ms)


# ── Noise at the clip's edges ────────────────────────────────────────────────

def tone(seconds: float, rms: float, hz: float = 220) -> bytes:
    """A sine standing in for a sound, loud enough to have [rms]."""
    peak = rms * math.sqrt(2)
    n = int(RATE * seconds)
    return array.array(
        "h", (int(peak * math.sin(2 * math.pi * hz * i / RATE))
              for i in range(n))).tobytes()


def samples(audio: bytes) -> array.array:
    out = array.array("h")
    out.frombytes(audio)
    return out


def loudest(audio: bytes, start: float, end: float) -> int:
    return max(abs(x) for x in samples(audio)[int(start * RATE):int(end * RATE)])


def test_a_loud_burst_after_the_speech_is_silenced():
    # What gemini-3.8-flash-lite-tts was measured doing: speech, silence, then
    # ~125 ms of near-full-scale static — a "tsh" at the end of every piece.
    speech = tone(1.0, 5000)
    clip = speech + pcm(0.25) + tone(0.13, 18000, hz=3000)

    cleaned = clean_edges(clip, RATE)

    assert len(cleaned) == len(clip)  # timings measured on it still hold
    assert loudest(cleaned, 1.25, 1.38) == 0
    assert cleaned[RATE // 10 * 2:RATE * 2 * 9 // 10] == \
        clip[RATE // 10 * 2:RATE * 2 * 9 // 10]


def test_a_click_before_the_speech_is_silenced():
    clip = tone(0.005, 6000) + pcm(0.2) + tone(1.0, 5000)

    cleaned = clean_edges(clip, RATE)

    assert loudest(cleaned, 0, 0.2) == 0
    assert loudest(cleaned, 0.4, 1.0) > 6000


def test_a_last_word_after_a_pause_is_kept():
    # "…and that's it. Yes." — a real word standing alone is quieter than
    # the burst throughout, and usually longer.
    clip = tone(1.0, 5000) + pcm(0.3) + tone(0.25, 6000)

    cleaned = clean_edges(clip, RATE)

    assert loudest(cleaned, 1.3, 1.5) > 8000


def test_a_loud_last_word_longer_than_a_burst_is_kept():
    clip = tone(1.0, 5000) + pcm(0.3) + tone(0.4, 15000)

    cleaned = clean_edges(clip, RATE)

    assert loudest(cleaned, 1.35, 1.65) > 20000


def test_a_clip_that_is_one_short_sound_is_not_emptied():
    clip = pcm(0.2) + tone(0.1, 15000) + pcm(0.2)

    assert loudest(clean_edges(clip, RATE), 0.2, 0.3) > 20000


def test_what_the_learner_and_whisper_hear_is_the_cleaned_clip(providers):
    providers["gemini"] = [tone(1.3, 5000) + pcm(0.25) + tone(0.13, 18000)]
    providers["groq"] = [GOOD]

    Synthesizer(settings()).synthesize(TEXT)

    with wave.open(io.BytesIO(providers["groq_audio"][0])) as heard_clip:
        audio = heard_clip.readframes(heard_clip.getnframes())
    assert loudest(audio, 1.55, 1.68) == 0


# ── Synthesis ────────────────────────────────────────────────────────────────

def test_audio_comes_back_compressed_with_aligned_timings(providers):
    providers["gemini"] = [pcm(1.5)]
    providers["groq"] = [GOOD]

    result = Synthesizer(settings()).synthesize(TEXT)

    assert result.model == "model-a"
    assert result.timing == "aligned"
    assert result.duration_ms == 1500
    assert result.mime_type == "audio/mpeg"
    assert len(result.audio) < len(pcm(1.5)) / 4
    assert len(result.words) == 4
    # The voice the product owner chose, the free key, the text alone.
    api_key, _, text, voice = providers["gemini_calls"][0]
    assert (api_key, text, voice) == ("free-tts-key", TEXT, "Kore")
    assert providers["groq_calls"][0]["timestamp_granularities[]"] == "word"


def test_audio_that_does_not_say_the_text_is_refused_and_the_next_model_tried(
        providers):
    providers["gemini"] = [pcm(1.5), pcm(1.5)]
    providers["groq"] = [heard(("something", 0, .5), ("else", .5, 1)), GOOD]

    result = Synthesizer(settings()).synthesize(TEXT)

    assert result.model == "model-b"
    assert "model-a" in result.rejected[0]


def test_audio_with_extra_words_is_refused(providers):
    providers["gemini"] = [pcm(2), pcm(1.5)]
    providers["groq"] = [
        heard(("Say", 0, .1), ("it", .1, .2), ("warmly", .2, .4), *[
            (w["word"], w["start"] + .5, w["end"] + .5) for w in GOOD]),
        GOOD,
    ]

    result = Synthesizer(settings()).synthesize(TEXT)

    assert result.model == "model-b"
    assert "extra" in result.rejected[0]


def test_a_refusing_model_falls_through_to_the_next(providers):
    providers["gemini"] = [GeminiError("quota", status=429), pcm(1.5)]
    providers["groq"] = [GOOD]

    result = Synthesizer(settings()).synthesize(TEXT)

    assert result.model == "model-b"
    assert "429" in result.rejected[0]


def test_every_model_failing_is_an_error_the_app_falls_back_on(providers):
    providers["gemini"] = [GeminiError("x", status=503), TimeoutError()]

    with pytest.raises(SynthesisError):
        Synthesizer(settings()).synthesize(TEXT)


def test_without_groq_the_audio_is_kept_with_estimated_timings(providers):
    providers["gemini"] = [pcm(1.5)]
    providers["groq"] = [GroqError("down", status=500)]

    result = Synthesizer(settings()).synthesize(TEXT)

    assert result.timing == "estimated"
    assert len(result.words) == 4


def test_the_same_text_is_not_generated_twice(providers):
    providers["gemini"] = [pcm(1.5)]
    providers["groq"] = [GOOD]
    synthesizer = Synthesizer(settings())

    first = synthesizer.synthesize(TEXT)
    second = synthesizer.synthesize(TEXT)

    assert first is second
    assert len(providers["gemini_calls"]) == 1


def test_the_text_key_is_never_used_for_the_voice(monkeypatch):
    monkeypatch.setenv("GEMINI_API_KEY", "paid-text-key")
    monkeypatch.delenv("TTS_GEMINI_API_KEY", raising=False)
    monkeypatch.delenv("STT_GEMINI_API_KEY", raising=False)

    loaded = TtsSettings.from_env()
    assert loaded.gemini_api_key == ""
    assert not loaded.usable("gemini")


def test_the_free_microphone_key_is_used_when_no_voice_key_is_set(monkeypatch):
    monkeypatch.delenv("TTS_GEMINI_API_KEY", raising=False)
    monkeypatch.setenv("STT_GEMINI_API_KEY", "free-mic-key")

    loaded = TtsSettings.from_env()

    assert loaded.gemini_api_key == "free-mic-key"
    assert loaded.voice == "Kore"


# ── Edge (ADR-110) ───────────────────────────────────────────────────────────

MP3 = to_compressed(pcm(1.5), RATE, 48)[0]
EDGE = ("edge", "gemini")


def test_edge_speaks_first_with_its_own_word_marks(providers):
    providers["edge"] = [(MP3, GOOD)]

    result = Synthesizer(settings(providers=EDGE)).synthesize(TEXT)

    assert result.model == "edge:en-US-AvaMultilingualNeural"
    assert result.mime_type == "audio/mpeg"
    assert result.audio == MP3
    assert result.timing == "aligned"
    assert abs(result.duration_ms - 1500) < 100
    assert [(w.start_ms, w.end_ms) for w in result.words] == \
        [(100, 200), (200, 500), (500, 800), (800, 1300)]
    assert providers["edge_calls"] == [(TEXT, "en-US-AvaMultilingualNeural")]
    # Nothing else was asked: no Gemini quota, no Groq.
    assert providers["gemini_calls"] == [] and providers["groq_calls"] == []


def test_when_edge_fails_gemini_speaks(providers):
    providers["edge"] = [EdgeError("WSServerHandshakeError: 403")]
    providers["gemini"] = [pcm(1.5)]
    providers["groq"] = [GOOD]

    result = Synthesizer(settings(providers=EDGE)).synthesize(TEXT)

    assert result.model == "model-a"
    assert result.rejected[0].startswith("edge:en-US-AvaMultilingualNeural")


def test_edge_audio_that_is_not_mp3_is_not_sent(providers):
    providers["edge"] = [(b"<html>blocked</html>", GOOD)]
    providers["gemini"] = [pcm(1.5)]
    providers["groq"] = [GOOD]

    result = Synthesizer(settings(providers=EDGE)).synthesize(TEXT)

    assert result.model == "model-a"


def test_edge_marks_that_do_not_fit_the_text_keep_the_audio(providers):
    # Edge reads what it is given; odd marks mean the ruler is off, not the
    # voice. The audio stays and the timings are spread by length.
    providers["edge"] = [(MP3, heard(("Twenty", 0.1, 0.4)))]

    result = Synthesizer(settings(providers=EDGE)).synthesize(TEXT)

    assert result.model.startswith("edge:")
    assert result.timing == "estimated"
    assert len(result.words) == 4
    assert result.words[-1].end_ms <= result.duration_ms
    assert providers["gemini_calls"] == []


def test_every_provider_failing_is_an_error(providers):
    providers["edge"] = [EdgeError("down")]
    providers["gemini"] = [GeminiError("x", status=429),
                           GeminiError("x", status=429)]

    with pytest.raises(SynthesisError) as raised:
        Synthesizer(settings(providers=EDGE)).synthesize(TEXT)
    assert "edge:" in str(raised.value) and "model-b" in str(raised.value)


def test_edge_needs_no_key(providers):
    only_edge = settings(providers=("edge",), gemini_api_key="")
    providers["edge"] = [(MP3, GOOD)]

    assert only_edge.configured
    assert Synthesizer(only_edge).synthesize(TEXT).model.startswith("edge:")


def test_an_unknown_provider_is_skipped_not_fatal(providers):
    providers["edge"] = [(MP3, GOOD)]

    result = Synthesizer(settings(providers=("typo", "edge"))).synthesize(TEXT)

    assert result.rejected == ["typo: not configured"]


def test_edge_then_gemini_is_the_default_and_configurable(monkeypatch):
    monkeypatch.delenv("TTS_PROVIDERS", raising=False)
    monkeypatch.delenv("TTS_EDGE_VOICE", raising=False)
    loaded = TtsSettings.from_env()
    assert loaded.providers == ("edge", "gemini")
    assert loaded.edge_voice == "en-US-AvaMultilingualNeural"

    monkeypatch.setenv("TTS_PROVIDERS", " Gemini , edge ")
    assert TtsSettings.from_env().providers == ("gemini", "edge")


def test_the_mp3_length_is_counted_from_its_frames():
    assert abs(mp3_duration_ms(MP3) - 1500) < 100
    # An ID3 tag in front is skipped, not read as audio.
    tag = b"ID3\x04\x00\x00\x00\x00\x00\x0a" + b"\x00" * 10
    assert abs(mp3_duration_ms(tag + MP3) - 1500) < 100
    assert mp3_duration_ms(b"not audio at all") is None


class _FakeCommunicate:
    """Stands in for edge_tts.Communicate; ``script`` is what it streams."""

    script: list = []
    delay = 0.0

    def __init__(self, text, voice, boundary):
        assert boundary == "WordBoundary"
        self.text = text

    async def stream(self):
        script = type(self).script  # through the class: never a bound method
        if callable(script):
            script = script(self.text)
        for chunk in script:
            if self.delay:
                await asyncio.sleep(self.delay)
            if isinstance(chunk, BaseException):
                raise chunk
            yield chunk


@pytest.fixture
def fake_edge_module(monkeypatch):
    module = types.ModuleType("edge_tts")
    module.Communicate = _FakeCommunicate
    monkeypatch.setitem(sys.modules, "edge_tts", module)
    monkeypatch.setattr(tts, "edge_speak", edge_speak)  # the real one
    _FakeCommunicate.delay = 0.0
    return _FakeCommunicate


def test_edge_word_marks_are_read_in_seconds(fake_edge_module):
    fake_edge_module.script = [
        {"type": "audio", "data": MP3[:len(MP3) // 2]},
        {"type": "WordBoundary", "offset": 1_000_000, "duration": 4_000_000,
         "text": "Hello"},
        {"type": "audio", "data": MP3[len(MP3) // 2:]},
    ]

    audio, words = edge_speak("Hello", "voice", timeout=5)

    assert audio == MP3
    assert words == [{"word": "Hello", "start": 0.1, "end": 0.5}]


def test_a_passage_is_asked_for_a_sentence_at_a_time_and_joined(
        fake_edge_module):
    # Every sentence comes back as the same 1.5 s clip with its first word
    # at 0.1 s, so the second sentence's word must land 1.5 s later.
    asked = []

    def script(text):
        asked.append(text)
        first = text.split()[0]
        return [{"type": "audio", "data": MP3},
                {"type": "WordBoundary", "offset": 1_000_000,
                 "duration": 2_000_000, "text": first}]

    fake_edge_module.script = script
    text = ("Maya works at a small bakery near the station. "
            "Every morning she arrives before sunrise.")

    audio, words = edge_speak(text, "voice", timeout=5)

    assert asked == ["Maya works at a small bakery near the station.",
                     "Every morning she arrives before sunrise."]
    assert audio == MP3 + MP3
    length = mp3_duration_ms(MP3) / 1000
    assert [w["word"] for w in words] == ["Maya", "Every"]
    assert words[1]["start"] == pytest.approx(0.1 + length)
    assert mp3_duration_ms(audio) == pytest.approx(2 * length * 1000, abs=2)


def test_a_piece_that_is_not_mp3_fails_the_whole(fake_edge_module):
    fake_edge_module.script = [{"type": "audio", "data": b"<html>"}]
    with pytest.raises(EdgeError, match="not MP3"):
        edge_speak("Hello there.", "voice", timeout=5)


def test_sentences_are_split_and_short_ones_joined():
    assert sentences("Great! Can you tell me what you did this morning? "
                     "I went to the market with my brother.") == [
        "Great! Can you tell me what you did this morning?",
        "I went to the market with my brother.",
    ]
    assert sentences("No full stop at the end") == ["No full stop at the end"]
    assert sentences("Short. Tail.") == ["Short. Tail."]
    assert sentences('She said "stop." Then she left the room quietly.') == [
        'She said "stop." Then she left the room quietly.']


def test_anything_edge_breaks_with_is_an_edge_error(fake_edge_module):
    fake_edge_module.script = [RuntimeError("handshake refused")]
    with pytest.raises(EdgeError):
        edge_speak("Hello", "voice", timeout=5)

    fake_edge_module.script = []
    with pytest.raises(EdgeError, match="no audio"):
        edge_speak("Hello", "voice", timeout=5)


def test_a_silent_edge_is_given_up_on(fake_edge_module):
    fake_edge_module.script = [{"type": "audio", "data": MP3}]
    fake_edge_module.delay = 1.0

    with pytest.raises(EdgeError, match="Timeout"):
        edge_speak("Hello", "voice", timeout=0.05)


def test_endpoint_speaks_with_edge(client, monkeypatch, providers):
    from app import main

    monkeypatch.setattr(main, "SYNTHESIZER",
                        Synthesizer(settings(providers=EDGE)))
    providers["edge"] = [(MP3, GOOD)]

    body = _post(client).json()

    assert body["model"] == "edge:en-US-AvaMultilingualNeural"
    assert body["mime_type"] == "audio/mpeg"
    assert body["words"][1] == {"start_ms": 200, "end_ms": 500,
                                "char_start": 2, "char_end": 7}


# ── The endpoint ─────────────────────────────────────────────────────────────

@pytest.fixture
def stub_synthesizer(monkeypatch):
    from app import main

    fake = Synthesizer(settings())
    monkeypatch.setattr(main, "SYNTHESIZER", fake)
    return fake


def _post(client, text=TEXT, token=TOKEN):
    headers = {"X-Service-Token": token} if token else {}
    return client.post("/ai/synthesize", json={"text": text}, headers=headers)


def test_endpoint_needs_the_service_token(client, stub_synthesizer):
    assert _post(client, token=None).status_code == 401


def test_endpoint_returns_audio_and_timings(client, stub_synthesizer, providers):
    providers["gemini"] = [pcm(1.5)]
    providers["groq"] = [GOOD]

    response = _post(client)

    assert response.status_code == 200
    body = response.json()
    assert body["mime_type"] == "audio/mpeg"
    assert body["timing"] == "aligned"
    assert body["words"][1] == {"start_ms": 200, "end_ms": 500,
                                "char_start": 2, "char_end": 7}
    assert len(body["audio"]) > 0


def test_endpoint_refuses_empty_and_oversized_text(client, stub_synthesizer):
    assert _post(client, text="   ").status_code == 400
    response = _post(client, text="word " * 100)
    assert response.status_code == 413
    assert response.json()["error"]["code"] == "TEXT_TOO_LONG"


def test_endpoint_failure_is_a_502(client, stub_synthesizer, providers):
    providers["gemini"] = [GeminiError("x", status=503),
                           GeminiError("x", status=503)]

    response = _post(client)
    assert response.status_code == 502
    assert response.json()["error"]["code"] == "AI_UNAVAILABLE"


def test_endpoint_without_a_voice_key_is_a_503(client, monkeypatch):
    from app import main

    # Gemini alone, without its key: nothing can speak.
    monkeypatch.setattr(main, "SYNTHESIZER",
                        Synthesizer(settings(gemini_api_key="")))
    response = _post(client)
    assert response.status_code == 503
    assert response.json()["error"]["code"] == "VOICE_UNAVAILABLE"


def test_edge_connections_are_capped_across_requests(fake_edge_module):
    # ADR-116: two requests of several sentences each once put eight
    # sentences in flight, and Edge slowed to 14 s for one piece.
    import threading as _threading

    live = {"now": 0, "most": 0}
    lock = _threading.Lock()

    class Counting(_FakeCommunicate):
        async def stream(self):
            with lock:
                live["now"] += 1
                live["most"] = max(live["most"], live["now"])
            await asyncio.sleep(0.02)
            with lock:
                live["now"] -= 1
            yield {"type": "audio", "data": MP3}

    fake_edge_module_module = sys.modules["edge_tts"]
    fake_edge_module_module.Communicate = Counting
    passage = " ".join(f"This is sentence number {i} of the passage." for i in range(8))

    threads = [_threading.Thread(target=edge_speak, args=(passage, "v", 10, 4))
               for _ in range(3)]
    for t in threads:
        t.start()
    for t in threads:
        t.join()

    assert live["most"] <= 4
    assert live["now"] == 0


def test_a_request_that_gives_up_waiting_frees_nothing_it_never_took(
        fake_edge_module):
    fake_edge_module.script = [{"type": "audio", "data": MP3}]
    fake_edge_module.delay = 0.2
    passage = " ".join(f"This is sentence number {i} of the passage." for i in range(8))

    with pytest.raises(EdgeError):
        edge_speak(passage, "voice", timeout=0.05, parallel=2)

    # Every slot is free again: a second call gets all of them.
    fake_edge_module.delay = 0.0
    audio, _ = edge_speak("One more sentence here.", "voice", timeout=5, parallel=2)
    assert audio == MP3
    assert tts._edge_connections(2)._value == 2


# ── A stalled sentence is asked for again (ADR-117) ──────────────────────────

def _stalls_first_time(fake_edge_module, stall: float):
    """The first request for each sentence hangs; any later one answers."""
    import threading as _threading

    seen: dict[str, int] = {}
    lock = _threading.Lock()

    class Stalling(_FakeCommunicate):
        async def stream(self):
            with lock:
                seen[self.text] = seen.get(self.text, 0) + 1
                first = seen[self.text] == 1
            if first:
                await asyncio.sleep(stall)
            yield {"type": "audio", "data": MP3}

    sys.modules["edge_tts"].Communicate = Stalling
    return seen


def test_a_stalled_sentence_is_asked_for_again_and_the_first_back_is_used(
        fake_edge_module):
    """Measured: the same reply took 0.85 s, 2.2 s and 10.9 s. The words wait
    for the voice, so one stalled sentence held the whole reply."""
    import time as _time

    seen = _stalls_first_time(fake_edge_module, stall=5)

    started = _time.monotonic()
    audio, _ = edge_speak("Does your shop ever deliver food to you?", "voice",
                          timeout=10, hedge_after=0.1)

    assert _time.monotonic() - started < 2
    assert audio == MP3
    assert list(seen.values()) == [2]
    # The copy that lost is cancelled, and gives its connection back.
    assert tts._edge_connections(4)._value == 4


def test_a_sentence_that_answers_in_time_is_asked_for_once(fake_edge_module):
    seen = _stalls_first_time(fake_edge_module, stall=0)

    edge_speak("Does your shop ever deliver food to you?", "voice",
               timeout=10, hedge_after=0.5)

    assert list(seen.values()) == [1]


def test_without_a_hedge_a_stall_is_waited_out(fake_edge_module):
    seen = _stalls_first_time(fake_edge_module, stall=0.3)

    edge_speak("Does your shop ever deliver food to you?", "voice", timeout=10)

    assert list(seen.values()) == [1]


def test_waiting_for_a_connection_is_not_a_stall(fake_edge_module):
    """A long passage queues for Edge's four connections; a sentence still in
    that queue has not started, and asking for it twice would only lengthen
    the queue."""
    fake_edge_module.script = [{"type": "audio", "data": MP3}]
    fake_edge_module.delay = 0.1
    passage = " ".join(f"This is sentence number {i} of the passage." for i in range(8))
    asked: list[str] = []
    original = fake_edge_module.__init__

    def counting(self, text, voice, boundary):
        asked.append(text)
        original(self, text, voice, boundary)

    fake_edge_module.__init__ = counting
    try:
        edge_speak(passage, "voice", timeout=10, parallel=1, hedge_after=0.15)
    finally:
        fake_edge_module.__init__ = original

    assert len(asked) == 8


def test_both_copies_failing_is_an_edge_error(fake_edge_module):
    fake_edge_module.script = [RuntimeError("socket closed")]
    fake_edge_module.delay = 0.2

    with pytest.raises(EdgeError, match="socket closed"):
        edge_speak("Does your shop ever deliver food to you?", "voice",
                   timeout=10, hedge_after=0.05)
    assert tts._edge_connections(4)._value == 4


def test_the_hedge_is_configured_from_the_environment(monkeypatch):
    monkeypatch.setenv("TTS_EDGE_HEDGE_SECONDS", "3.5")
    monkeypatch.setenv("TTS_EDGE_HEDGE_PER_CHAR", "0")

    settings = tts.TtsSettings.from_env()

    assert settings.edge_hedge_seconds == 3.5
    assert settings.edge_hedge_per_char == 0
