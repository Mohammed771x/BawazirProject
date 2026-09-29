"""Text-to-speech for the tutor and for Listening (ADR-108, ADR-110).

    app ──text──► backend ──text──► here ──► providers, in order (TTS_PROVIDERS)
                                               edge   — MP3 + its own word timings
                                               gemini — PCM ──► Groq word timings
                                                          └── align to the text ──► MP3

Edge's "Ava Multilingual" voice is first: the product owner chose it by ear
in labs/tts_lab, it needs no key, and it reports where each word is as it
speaks, so no second service is asked. It is unofficial — the read-aloud
service behind Microsoft Edge — so it can stop working any day. That is why
the providers are a list in configuration: when one fails, the next is tried,
and replacing one is a setting, not a release.

The phone's own voice sounded like a screen reader. Two things it gave for
free have to be rebuilt here:

* **Where each word is.** Listening plays sentence by sentence and draws a
  playhead and a clock (ADR-082). Edge says where each word starts. Gemini
  returns audio and nothing else, so its audio is sent to Groq's Whisper for
  word timestamps. Either way they are matched back onto the text that was
  asked for.
* **That the audio says the text.** Measured: a TTS model read a style prefix
  aloud as part of the speech. The same alignment is the check — if too few
  of the text's words were heard, or too many words were heard that the text
  does not have, the audio is refused and the next model is tried. A Listening
  passage that says something the page does not show is worse than the
  phone's voice.

When no model produces acceptable audio this fails, and the app falls back to
the phone's voice for that utterance. When Groq is unavailable the audio is
still used, with timings estimated from the length of each word — the
highlight is then approximate, which beats silence.

Nothing is stored: a small in-memory cache (minutes, not days) spares
regenerating a passage the learner replays or slows down. Every number here
is configuration (rule R3).
"""

from __future__ import annotations

import base64
import difflib
import hashlib
import io
import array
import logging
import math
import os
import sys
import re
import threading
import time
import wave
from collections import OrderedDict
from dataclasses import dataclass, field

import asyncio

from .gemini import GeminiError, GeminiSpeech, generate_speech
from .speech import GroqError, groq_post

log = logging.getLogger("wordos.ai.tts")

# A word as the page shows it: letters or digits, with inner apostrophes and
# hyphens kept ("don't", "well-known"). Spans are into the text as sent.
_WORD = re.compile(r"[A-Za-z0-9]+(?:['’\-][A-Za-z0-9]+)*")


class SynthesisError(RuntimeError):
    """No model produced audio that says the text."""


class EdgeError(RuntimeError):
    """Edge's voice did not answer, or answered with nothing."""


def _env_float(name: str, default: float) -> float:
    raw = os.environ.get(name, "").strip()
    return float(raw) if raw else default


def _env_int(name: str, default: int) -> int:
    raw = os.environ.get(name, "").strip()
    return int(raw) if raw else default


@dataclass(frozen=True)
class TtsSettings:
    gemini_api_key: str
    # Tried in order; the free tier's limits are per model, so a refusal from
    # one is not a refusal from the next.
    models: tuple[str, ...]
    voice: str
    gemini_timeout: float
    groq_api_key: str
    groq_model: str
    groq_timeout: float
    # Share of the text's words that must be heard in the audio.
    min_match: float
    # Words heard that the text does not have, as a share of the text's words.
    max_extra: float
    max_chars: int
    mp3_kbps: int
    cache_entries: int
    cache_seconds: float
    # A noise burst at either end of a clip, cut before anyone hears it —
    # see clean_edges. Defaults so a settings object built by hand keeps them.
    burst_max_ms: int = 300
    burst_min_gap_ms: int = 100
    burst_min_rms: float = 12000
    click_max_ms: int = 30
    # Tried in order (ADR-110). "edge" needs no key; "gemini" needs the free
    # voice key above.
    providers: tuple[str, ...] = ("edge", "gemini")
    edge_voice: str = "en-US-AvaMultilingualNeural"
    edge_timeout: float = 20
    edge_parallel: int = 4
    # When a sentence is asked for twice (ADR-117): after this many seconds,
    # plus this many per character. 0 turns it off.
    edge_hedge_seconds: float = 2.0
    edge_hedge_per_char: float = 0.02

    @property
    def configured(self) -> bool:
        return any(self.usable(p) for p in self.providers)

    def usable(self, provider: str) -> bool:
        if provider == "edge":
            return bool(self.edge_voice)
        if provider == "gemini":
            return bool(self.gemini_api_key and self.models)
        return False

    @classmethod
    def from_env(cls) -> "TtsSettings":
        # The free key given for the microphone — never GEMINI_API_KEY, which
        # is the paid key that writes lessons (the product owner's rule).
        key = (os.environ.get("TTS_GEMINI_API_KEY", "").strip()
               or os.environ.get("STT_GEMINI_API_KEY", "").strip())
        models = tuple(
            m.strip() for m in os.environ.get(
                "TTS_MODELS",
                "gemini-3.8-flash-tts,gemini-3.8-flash-lite-tts,"
                "gemini-2.5-flash-preview-tts",
            ).split(",") if m.strip()
        )
        return cls(
            gemini_api_key=key,
            models=models,
            voice=os.environ.get("TTS_VOICE", "Kore").strip() or "Kore",
            gemini_timeout=_env_float("TTS_GEMINI_TIMEOUT_SECONDS", 30),
            groq_api_key=os.environ.get("GROQ_API_KEY", "").strip(),
            groq_model=os.environ.get(
                "TTS_TIMING_MODEL", "whisper-large-v3-turbo").strip(),
            groq_timeout=_env_float("TTS_TIMING_TIMEOUT_SECONDS", 15),
            min_match=_env_float("TTS_MIN_MATCH", 0.85),
            max_extra=_env_float("TTS_MAX_EXTRA", 0.15),
            max_chars=_env_int("TTS_MAX_CHARS", 1500),
            mp3_kbps=_env_int("TTS_MP3_KBPS", 48),
            cache_entries=_env_int("TTS_CACHE_ENTRIES", 200),
            cache_seconds=_env_float("TTS_CACHE_SECONDS", 3600),
            burst_max_ms=_env_int("TTS_BURST_MAX_MS", 300),
            burst_min_gap_ms=_env_int("TTS_BURST_MIN_GAP_MS", 100),
            burst_min_rms=_env_float("TTS_BURST_MIN_RMS", 12000),
            click_max_ms=_env_int("TTS_CLICK_MAX_MS", 30),
            providers=tuple(
                p.strip().lower() for p in os.environ.get(
                    "TTS_PROVIDERS", "edge,gemini").split(",") if p.strip()
            ),
            edge_voice=os.environ.get(
                "TTS_EDGE_VOICE", "en-US-AvaMultilingualNeural").strip(),
            edge_timeout=_env_float("TTS_EDGE_TIMEOUT_SECONDS", 20),
            edge_parallel=_env_int("TTS_EDGE_PARALLEL", 4),
            edge_hedge_seconds=_env_float("TTS_EDGE_HEDGE_SECONDS", 2.0),
            edge_hedge_per_char=_env_float("TTS_EDGE_HEDGE_PER_CHAR", 0.02),
        )


@dataclass(frozen=True)
class TimedWord:
    start_ms: int
    end_ms: int
    char_start: int
    char_end: int


@dataclass(frozen=True)
class Synthesis:
    audio: bytes
    mime_type: str
    duration_ms: int
    words: list[TimedWord]
    # "aligned" — measured from the audio; "estimated" — spread by length
    # because the timing engine could not answer.
    timing: str
    model: str
    rejected: list[str] = field(default_factory=list)


# ── Alignment ────────────────────────────────────────────────────────────────

def _norm(word: str) -> str:
    return re.sub(r"[^a-z0-9]", "", word.lower().replace("’", "'"))


def text_words(text: str) -> list[tuple[str, int, int]]:
    """Every word of the text with its character span."""
    return [(m.group(0), m.start(), m.end()) for m in _WORD.finditer(text)]


def align(
    text: str,
    heard: list[dict],
    duration_ms: int,
) -> tuple[list[TimedWord], float, float]:
    """Puts a time on every word of ``text`` from what Whisper heard.

    Returns the timed words, the share of the text's words that were heard,
    and the share of heard words the text does not contain. Words the
    recogniser split or merged differently are matched in sequence
    (``difflib``), and anything unmatched is given a time between its matched
    neighbours in proportion to its length — so every word of the page has a
    place on the playhead, heard exactly or not.
    """
    words = text_words(text)
    if not words:
        return [], 1.0, 0.0

    heard_words = [
        (_norm(str(h.get("word", ""))), float(h.get("start", 0)),
         float(h.get("end", 0)))
        for h in heard
    ]
    heard_words = [h for h in heard_words if h[0]]

    a = [_norm(w) for w, _, _ in words]
    b = [h[0] for h in heard_words]
    matcher = difflib.SequenceMatcher(None, a, b, autojunk=False)

    times: list[tuple[float, float] | None] = [None] * len(words)
    matched = 0
    for block in matcher.get_matching_blocks():
        for k in range(block.size):
            _, start, end = heard_words[block.b + k]
            times[block.a + k] = (start * 1000, end * 1000)
            matched += 1

    match = matched / len(words)
    extra = (len(b) - matched) / len(words)

    _fill_gaps(words, times, duration_ms)

    timed = []
    last_end = 0
    for (_, cs, ce), t in zip(words, times):
        start, end = t  # type: ignore[misc]
        # Monotonic, whatever the recogniser's rounding did: a playhead that
        # steps backwards reads as a bug.
        start = max(start, last_end)
        end = max(end, start)
        last_end = end
        timed.append(TimedWord(int(start), int(end), cs, ce))
    return timed, match, extra


def _fill_gaps(words, times, duration_ms: int) -> None:
    """Times for unmatched words, spread by length between matched ones."""
    n = len(words)
    i = 0
    while i < n:
        if times[i] is not None:
            i += 1
            continue
        j = i
        while j < n and times[j] is None:
            j += 1
        left = times[i - 1][1] if i > 0 else 0.0
        right = times[j][0] if j < n else float(duration_ms)
        right = max(right, left)
        lengths = [max(1, words[k][2] - words[k][1]) for k in range(i, j)]
        total = sum(lengths)
        cursor = left
        for k, length in zip(range(i, j), lengths):
            span = (right - left) * length / total
            times[k] = (cursor, cursor + span)
            cursor += span
        i = j


def estimate(text: str, duration_ms: int) -> list[TimedWord]:
    """Timings from length alone — used only when Groq cannot answer."""
    words = text_words(text)
    times: list[tuple[float, float] | None] = [None] * len(words)
    _fill_gaps(words, times, duration_ms)
    return [
        TimedWord(int(t[0]), int(t[1]), cs, ce)  # type: ignore[index]
        for (_, cs, ce), t in zip(words, times)
    ]


# ── Cleaning ─────────────────────────────────────────────────────────────────

_FRAME_MS = 10
_SILENT_RMS = 200
_FADE_MS = 8


def clean_edges(pcm: bytes, rate: int, *, burst_max_ms: int = 300,
                burst_min_gap_ms: int = 100, burst_min_rms: float = 12000,
                click_max_ms: int = 30) -> bytes:
    """The clip with any noise burst at either end silenced.

    Measured: gemini-3.8-flash-lite-tts ends every clip with ~125 ms of
    near-full-scale static after the speech has stopped, and opens some with
    a 5 ms click. The tutor's reply is spoken in pieces, so the learner heard
    a radio-like "tsh" at the end of each one. Speech never looks like that:
    its loudest 10 ms stays near 13,000 RMS and its runs are joined, while the
    burst stands alone behind a silence at over 15,000 RMS throughout.

    So an edge run is silenced when silence separates it from the speech and
    it is either very short (a click) or short and loud throughout (a burst).
    The clip keeps its length, so timings measured on it still hold, and
    ends fade in and out so the cut itself never clicks.
    """
    samples = array.array("h")
    samples.frombytes(pcm)
    if sys.byteorder == "big":
        samples.byteswap()
    frame = max(1, rate * _FRAME_MS // 1000)
    count = len(samples) // frame
    if count == 0:
        return pcm

    loud = []
    level = []
    for i in range(count):
        chunk = samples[i * frame:(i + 1) * frame]
        rms = math.sqrt(sum(x * x for x in chunk) / len(chunk))
        level.append(rms)
        loud.append(rms >= _SILENT_RMS)

    # Runs of sound, as [start, end) frame indexes.
    runs: list[list[int]] = []
    for i, on in enumerate(loud):
        if on and (not runs or runs[-1][1] != i):
            runs.append([i, i + 1])
        elif on:
            runs[-1][1] = i + 1

    def noise(run: list[int], gap: int) -> bool:
        length_ms = (run[1] - run[0]) * _FRAME_MS
        if gap * _FRAME_MS < burst_min_gap_ms:
            return False
        if length_ms <= click_max_ms:
            return True
        mean = sum(level[run[0]:run[1]]) / (run[1] - run[0])
        return length_ms <= burst_max_ms and mean >= burst_min_rms

    silenced: list[list[int]] = []
    # Never the only run: a clip that is nothing but one short sound is left
    # alone rather than emptied.
    if len(runs) >= 2 and noise(runs[-1], runs[-1][0] - runs[-2][1]):
        silenced.append(runs.pop())
    if len(runs) >= 2 and noise(runs[0], runs[1][0] - runs[0][1]):
        silenced.append(runs.pop(0))
    for start, end in silenced:
        stop = len(samples) if end == count else end * frame
        for j in range(start * frame, stop):
            samples[j] = 0

    fade = min(len(samples) // 2, rate * _FADE_MS // 1000)
    for j in range(fade):
        samples[j] = int(samples[j] * j / fade)
        samples[-1 - j] = int(samples[-1 - j] * j / fade)

    if silenced:
        log.info("tts: silenced %d noise run(s) at the clip's edge",
                 len(silenced))
    if sys.byteorder == "big":
        samples.byteswap()
    return samples.tobytes()


# ── Edge ─────────────────────────────────────────────────────────────────────

_SENTENCE = re.compile(r"[^.!?]+(?:[.!?]+[\"')\]]*|$)\s*")


def sentences(text: str, shortest: int = 25) -> list[str]:
    """The text as sentences, a very short one joined to the next.

    "Great!" on its own would be a request for half a second of speech.
    """
    parts = [m.group(0) for m in _SENTENCE.finditer(text) if m.group(0).strip()]
    merged: list[str] = []
    carry = ""
    for part in parts:
        carry += part
        if len(carry.strip()) >= shortest:
            merged.append(carry.strip())
            carry = ""
    if carry.strip():
        if merged:
            merged[-1] = f"{merged[-1]} {carry.strip()}"
        else:
            merged.append(carry.strip())
    return merged or [text.strip()]


#: Open connections to Edge from this whole process, not per request
#: (ADR-116). The app fetches two pieces of a reply at once and each is split
#: into sentences; measured, that put eight sentences in flight and one piece
#: of 146 characters took 14 s. Edge serves four at once well and more
#: slowly, so four is the ceiling however many requests are asking.
_edge_gate: threading.BoundedSemaphore | None = None
_edge_gate_size = 0
_edge_gate_lock = threading.Lock()


def _edge_connections(size: int) -> threading.BoundedSemaphore:
    global _edge_gate, _edge_gate_size
    with _edge_gate_lock:
        if _edge_gate is None or _edge_gate_size != size:
            _edge_gate = threading.BoundedSemaphore(max(1, size))
            _edge_gate_size = size
        return _edge_gate


def edge_speak(text: str, voice: str, timeout: float,
               parallel: int = 4, hedge_after: float = 0,
               hedge_per_char: float = 0) -> tuple[bytes, list[dict]]:
    """MP3 from Edge's read-aloud voice, and where each word starts.

    Edge makes speech at roughly the pace it is spoken — measured, 57 s for a
    1,400-character passage. So the text is asked for a sentence at a time,
    ``parallel`` at once (eight at once was measured slower than four), and
    the pieces are joined: its MP3 is bare frames, which follow one another
    cleanly, and each piece's word marks are moved by the length of the
    pieces before it. The same passage then took 11 s.

    Words come back in the shape Whisper uses — ``{"word", "start", "end"}``
    in seconds — so the one ``align`` serves both providers. Every failure,
    whatever raised it, is an ``EdgeError``: the service is unofficial, and
    what it breaks with next is not knowable in advance.

    A sentence still unfinished ``hedge_after + hedge_per_char × length``
    seconds after it started is asked for a second time, and whichever copy
    finishes first is used (ADR-117). Edge usually answers a sentence in about
    two seconds, and now and then one simply stalls: measured, the same reply
    took 0.85 s, 2.2 s and 10.9 s in three tries. The tutor's words wait for
    their voice (ADR-116), so one stalled sentence held the whole reply for
    ten seconds. Zero turns the second request off.
    """
    try:
        import edge_tts  # noqa: PLC0415 - optional at import time
    except ImportError as exc:
        raise EdgeError("edge-tts is not installed") from exc

    gate = _edge_connections(parallel)

    async def one(piece: str,
                  started: asyncio.Event | None = None) -> tuple[bytes, list[dict]]:
        # Polled, not blocked on: a request that times out while waiting
        # must not leave a thread behind that takes a slot nobody releases.
        while not gate.acquire(blocking=False):
            await asyncio.sleep(0.02)
        try:
            if started is not None:
                started.set()
            return await _speak_piece(piece)
        finally:
            gate.release()

    async def hedged(piece: str) -> tuple[bytes, list[dict]]:
        if hedge_after <= 0:
            return await one(piece)
        started = asyncio.Event()
        first = asyncio.ensure_future(one(piece, started))
        # The clock starts when the first copy has a connection, not while it
        # queues for one: a long passage waiting its turn is not a stall.
        waiting = asyncio.ensure_future(started.wait())
        await asyncio.wait({first, waiting}, return_when=asyncio.FIRST_COMPLETED)
        waiting.cancel()
        done, _ = await asyncio.wait(
            {first}, timeout=hedge_after + hedge_per_char * len(piece))
        if done:
            return first.result()

        copies = {first, asyncio.ensure_future(one(piece))}
        failure: BaseException | None = None
        try:
            while copies:
                done, copies = await asyncio.wait(
                    copies, return_when=asyncio.FIRST_COMPLETED)
                for finished in done:
                    if finished.exception() is None:
                        return finished.result()
                    failure = finished.exception()
            raise failure  # both copies failed
        finally:
            for copy in copies:
                copy.cancel()

    async def _speak_piece(piece: str) -> tuple[bytes, list[dict]]:
        stream = edge_tts.Communicate(piece, voice, boundary="WordBoundary")
        audio = bytearray()
        words: list[dict] = []
        async for chunk in stream.stream():
            if chunk["type"] == "audio":
                audio += chunk["data"]
            elif chunk["type"] == "WordBoundary":
                # Offsets are in 100-nanosecond ticks.
                start = chunk["offset"] / 1e7
                words.append({"word": chunk["text"], "start": start,
                              "end": start + chunk["duration"] / 1e7})
        if not audio:
            raise EdgeError("Edge returned no audio")
        return bytes(audio), words

    async def run() -> list[tuple[bytes, list[dict]]]:
        gate = asyncio.Semaphore(max(1, parallel))

        async def gated(piece: str) -> tuple[bytes, list[dict]]:
            async with gate:
                return await hedged(piece)

        return await asyncio.gather(*(gated(p) for p in sentences(text)))

    try:
        pieces = asyncio.run(asyncio.wait_for(run(), timeout))
    except EdgeError:
        raise
    except Exception as exc:  # noqa: BLE001 - see the docstring
        raise EdgeError(f"{type(exc).__name__}: {exc}") from exc

    audio = bytearray()
    words: list[dict] = []
    offset = 0.0
    for piece_audio, piece_words in pieces:
        length = mp3_duration_ms(piece_audio)
        if length is None:
            raise EdgeError("Edge returned audio that is not MP3")
        words += [{"word": w["word"], "start": w["start"] + offset,
                   "end": w["end"] + offset} for w in piece_words]
        audio += piece_audio
        offset += length / 1000
    return bytes(audio), words


_MP3_RATES = {3: (44100, 48000, 32000), 2: (22050, 24000, 16000),
              0: (11025, 12000, 8000)}
_MP3_KBPS_V1 = (0, 32, 40, 48, 56, 64, 80, 96, 112, 128, 160, 192, 224, 256,
                320)
_MP3_KBPS_V2 = (0, 8, 16, 24, 32, 40, 48, 56, 64, 80, 96, 112, 128, 144, 160)


def mp3_duration_ms(data: bytes) -> int | None:
    """How long an MP3 plays, counted frame by frame.

    Not from the bit rate and the size: an ID3 tag or a variable rate would
    make that wrong, and the playhead and the clock are drawn from this.
    None when no Layer III frame is found.
    """
    i = 0
    if data[:3] == b"ID3" and len(data) >= 10:
        size = 0
        for byte in data[6:10]:
            size = (size << 7) | (byte & 0x7F)
        i = 10 + size
    seconds = 0.0
    frames = 0
    while i + 4 <= len(data):
        header = int.from_bytes(data[i:i + 4], "big")
        version = (header >> 19) & 3
        layer = (header >> 17) & 3
        rate_index = (header >> 12) & 0xF
        sample_index = (header >> 10) & 3
        if ((header >> 21) & 0x7FF != 0x7FF or version == 1 or layer != 1
                or rate_index in (0, 15) or sample_index == 3):
            i += 1
            continue
        sample_rate = _MP3_RATES[version][sample_index]
        mpeg1 = version == 3
        kbps = (_MP3_KBPS_V1 if mpeg1 else _MP3_KBPS_V2)[rate_index]
        padding = (header >> 9) & 1
        length = (144 if mpeg1 else 72) * kbps * 1000 // sample_rate + padding
        seconds += (1152 if mpeg1 else 576) / sample_rate
        frames += 1
        i += max(length, 1)
    return int(seconds * 1000) if frames else None


# ── Encoding ─────────────────────────────────────────────────────────────────

def to_wav(pcm: bytes, rate: int) -> bytes:
    buffer = io.BytesIO()
    with wave.open(buffer, "wb") as w:
        w.setnchannels(1)
        w.setsampwidth(2)
        w.setframerate(rate)
        w.writeframes(pcm)
    return buffer.getvalue()


def to_compressed(pcm: bytes, rate: int, kbps: int) -> tuple[bytes, str]:
    """MP3 when the encoder is installed, WAV otherwise.

    Uncompressed speech is 48 KB a second — a Listening passage would be
    megabytes over a phone connection. MP3 at 48 kbit/s is an eighth of that
    and plays everywhere.
    """
    try:
        import lameenc  # noqa: PLC0415 - optional at import time
    except ImportError:
        return to_wav(pcm, rate), "audio/wav"
    encoder = lameenc.Encoder()
    encoder.set_bit_rate(kbps)
    encoder.set_in_sample_rate(rate)
    encoder.set_channels(1)
    encoder.set_quality(2)
    data = encoder.encode(pcm) + encoder.flush()
    return bytes(data), "audio/mpeg"


# ── The synthesizer ──────────────────────────────────────────────────────────

class _Cache:
    """Recently spoken text, for minutes. A convenience, never a store."""

    def __init__(self, entries: int, seconds: float) -> None:
        self._entries = entries
        self._seconds = seconds
        self._items: OrderedDict[str, tuple[float, Synthesis]] = OrderedDict()
        self._lock = threading.Lock()

    def get(self, key: str) -> Synthesis | None:
        with self._lock:
            item = self._items.get(key)
            if item is None:
                return None
            stored, value = item
            if time.monotonic() - stored > self._seconds:
                del self._items[key]
                return None
            self._items.move_to_end(key)
            return value

    def put(self, key: str, value: Synthesis) -> None:
        if self._entries <= 0:
            return
        with self._lock:
            self._items[key] = (time.monotonic(), value)
            self._items.move_to_end(key)
            while len(self._items) > self._entries:
                self._items.popitem(last=False)


class Synthesizer:
    def __init__(self, settings: TtsSettings) -> None:
        self._settings = settings
        self._cache = _Cache(settings.cache_entries, settings.cache_seconds)

    @property
    def settings(self) -> TtsSettings:
        return self._settings

    @property
    def configured(self) -> bool:
        return self._settings.configured

    def synthesize(self, text: str) -> Synthesis:
        s = self._settings
        key = hashlib.sha256(
            f"{','.join(s.providers)}|{s.edge_voice}|{s.voice}\n{text}".encode()
        ).hexdigest()
        cached = self._cache.get(key)
        if cached is not None:
            return cached

        rejected: list[str] = []
        for provider in s.providers:
            if not s.usable(provider):
                rejected.append(f"{provider}: not configured")
                continue
            if provider == "edge":
                result = self._edge(text, rejected)
            else:
                result = self._gemini(text, rejected)
            if result is not None:
                self._cache.put(key, result)
                return result

        raise SynthesisError("no model produced usable audio: "
                             + "; ".join(rejected))

    def _edge(self, text: str, rejected: list[str]) -> Synthesis | None:
        s = self._settings
        name = f"edge:{s.edge_voice}"
        try:
            audio, heard = edge_speak(text, s.edge_voice, s.edge_timeout,
                                      s.edge_parallel, s.edge_hedge_seconds,
                                      s.edge_hedge_per_char)
        except EdgeError as exc:
            rejected.append(f"{name}: {str(exc)[:80]}")
            log.warning("tts: %s failed — %s", name, str(exc)[:160])
            return None

        duration_ms = mp3_duration_ms(audio)
        if duration_ms is None:
            rejected.append(f"{name}: audio is not MP3")
            log.warning("tts: %s returned audio that is not MP3", name)
            return None

        words, match, extra = align(text, heard, duration_ms)
        timing = "aligned"
        if match < s.min_match or extra > s.max_extra:
            # Edge reads exactly what it is given; a poor match means its
            # word marks split the text differently (numbers, symbols), not
            # that the audio says something else. So the audio is kept, and
            # only the ruler is replaced.
            log.warning("tts: %s word marks matched %.0f%% (+%.0f%%), "
                        "estimating", name, match * 100, extra * 100)
            words, timing = estimate(text, duration_ms), "estimated"
        return Synthesis(audio, "audio/mpeg", duration_ms, words, timing,
                         name, list(rejected))

    def _gemini(self, text: str, rejected: list[str]) -> Synthesis | None:
        s = self._settings
        for model in s.models:
            try:
                speech = self._speak(model, text)
            except (GeminiError, OSError) as exc:
                status = getattr(exc, "status", None)
                rejected.append(f"{model}: {status or type(exc).__name__}")
                log.warning("tts: %s failed — %s", model, str(exc)[:160])
                continue

            speech = GeminiSpeech(
                clean_edges(speech.pcm, speech.sample_rate,
                            burst_max_ms=s.burst_max_ms,
                            burst_min_gap_ms=s.burst_min_gap_ms,
                            burst_min_rms=s.burst_min_rms,
                            click_max_ms=s.click_max_ms),
                speech.sample_rate, speech.model)
            duration_ms = int(len(speech.pcm) / 2 / speech.sample_rate * 1000)
            words, timing, verdict = self._time(text, speech, duration_ms)
            if verdict is not None:
                rejected.append(f"{model}: {verdict}")
                log.warning("tts: %s audio refused — %s", model, verdict)
                continue

            audio, mime = to_compressed(speech.pcm, speech.sample_rate,
                                        s.mp3_kbps)
            return Synthesis(audio, mime, duration_ms, words, timing,
                             model, list(rejected))
        return None

    def _speak(self, model: str, text: str) -> GeminiSpeech:
        return generate_speech(self._settings.gemini_api_key, model, text,
                               self._settings.voice,
                               self._settings.gemini_timeout)

    def _time(
        self, text: str, speech: GeminiSpeech, duration_ms: int,
    ) -> tuple[list[TimedWord], str, str | None]:
        """Word timings, and a reason to refuse the audio — or None."""
        s = self._settings
        if not s.groq_api_key:
            return estimate(text, duration_ms), "estimated", None
        try:
            payload = groq_post(
                s.groq_api_key,
                [("model", s.groq_model), ("language", "en"),
                 ("temperature", "0"), ("response_format", "verbose_json"),
                 ("timestamp_granularities[]", "word")],
                to_wav(speech.pcm, speech.sample_rate),
                "audio/wav",
                s.groq_timeout,
            )
        except GroqError as exc:
            # The audio is fine; only the ruler is missing. Estimated timings
            # beat sending the learner back to the phone's voice.
            log.warning("tts: timings unavailable, estimating — %s", exc)
            return estimate(text, duration_ms), "estimated", None

        words, match, extra = align(text, payload.get("words") or [],
                                    duration_ms)
        if match < s.min_match:
            return words, "aligned", f"only {match:.0%} of the text was heard"
        if extra > s.max_extra:
            return words, "aligned", f"{extra:.0%} extra words were heard"
        return words, "aligned", None


def encode_audio(audio: bytes) -> str:
    return base64.b64encode(audio).decode("ascii")
