"""Speech-to-text for Speaking: Gemini first, Groq Whisper when it fails.

    mobile ──audio──► backend ──audio──► here ──► Gemini ─(fails)─► Groq

The phone's own recogniser was the weak link in Speaking — Android's in
particular misheard learners badly enough that the transcript, which is what
the whole skill is judged on, was often not what they said (ADR-107). Both
engines here were measured against the same clips before this was built:
Gemini was the more accurate, Groq the faster and the one that never refused.
So Gemini answers, and Groq answers when Gemini will not.

This module transcribes and nothing else. It never judges what was said —
the transcript goes back to the learner to read and correct before it is sent,
exactly as the on-device text did (ADR-069), and the backend decides
everything after that (rule R2).

Both keys are **separate from the text-generation key** on purpose. The
product owner gave a free-tier Gemini key for the microphone so audio never
spends on the paid key that writes lessons; ``STT_GEMINI_API_KEY`` is never
defaulted to ``GEMINI_API_KEY``.

Every number below is configuration, not a constant (rule R3).
"""

from __future__ import annotations

import json
import logging
import os
import time
import urllib.error
import urllib.request
import uuid
from dataclasses import dataclass

from .gemini import GeminiClient, GeminiError

log = logging.getLogger("wordos.ai.speech")

_GROQ_URL = "https://api.groq.com/openai/v1/audio/transcriptions"

# What Gemini says when there is nothing to transcribe. Asking for a marker
# rather than an empty answer matters: the client treats an empty answer as a
# provider failure, and silence is not a failure — it is "say it again".
_NO_SPEECH = "[NO_SPEECH]"

_GEMINI_PROMPT = (
    "Transcribe this English audio verbatim. The speaker is an Arabic-speaking "
    "learner of English. Write exactly the words they said, in the order they "
    "said them, keeping every grammar mistake, wrong word form and unfinished "
    "sentence: never correct, complete, translate or rephrase anything. "
    "Keep false starts and repeated words if they were spoken. "
    "Leave out hesitation sounds such as 'um' and 'uh'. "
    f"If nobody speaks, output exactly {_NO_SPEECH}. "
    "Output only the transcript, nothing else."
)


class TranscriptionError(RuntimeError):
    """Neither engine produced a transcript."""


@dataclass(frozen=True)
class Transcription:
    text: str
    # Which engine answered — kept for analytics, so the owner can see how
    # often Gemini refused and Groq carried the load.
    engine: str
    # Why the first engine was passed over, when it was. Never shown to the
    # learner; it goes to the log and the backend's records.
    fallback_reason: str | None = None


def _env_float(name: str, default: float) -> float:
    raw = os.environ.get(name, "").strip()
    return float(raw) if raw else default


def _env_int(name: str, default: int) -> int:
    raw = os.environ.get(name, "").strip()
    return int(raw) if raw else default


@dataclass(frozen=True)
class SpeechSettings:
    gemini_api_key: str
    gemini_model: str
    gemini_timeout: float
    # Retries only for a refusal that clears in seconds (503 "high demand",
    # 429). A timeout is not retried: the learner has already waited once.
    gemini_retries: int
    gemini_retry_delay: float
    groq_api_key: str
    groq_model: str
    groq_timeout: float
    # A Whisper segment is dropped when the model itself says it was probably
    # silence — Whisper is known to "hear" a polite phrase in an empty clip.
    groq_no_speech_threshold: float
    max_audio_bytes: int

    @property
    def configured(self) -> bool:
        return bool(self.gemini_api_key or self.groq_api_key)

    @classmethod
    def from_env(cls) -> "SpeechSettings":
        return cls(
            gemini_api_key=os.environ.get("STT_GEMINI_API_KEY", "").strip(),
            gemini_model=os.environ.get(
                "STT_GEMINI_MODEL", "gemini-3.1-flash-lite").strip(),
            gemini_timeout=_env_float("STT_GEMINI_TIMEOUT_SECONDS", 15),
            gemini_retries=_env_int("STT_GEMINI_RETRIES", 1),
            gemini_retry_delay=_env_float("STT_GEMINI_RETRY_DELAY_SECONDS", 1.5),
            groq_api_key=os.environ.get("GROQ_API_KEY", "").strip(),
            groq_model=os.environ.get(
                "GROQ_STT_MODEL", "whisper-large-v3").strip(),
            groq_timeout=_env_float("GROQ_TIMEOUT_SECONDS", 20),
            groq_no_speech_threshold=_env_float(
                "GROQ_NO_SPEECH_THRESHOLD", 0.6),
            max_audio_bytes=_env_int("STT_MAX_AUDIO_BYTES", 8 * 1024 * 1024),
        )


class GroqError(RuntimeError):
    def __init__(self, message: str, *, status: int | None = None) -> None:
        super().__init__(message)
        self.status = status


class Transcriber:
    def __init__(self, settings: SpeechSettings) -> None:
        self._settings = settings
        self._gemini = (
            GeminiClient(settings.gemini_api_key, settings.gemini_model,
                         timeout=settings.gemini_timeout)
            if settings.gemini_api_key else None
        )

    @property
    def configured(self) -> bool:
        return self._settings.configured

    def transcribe(self, audio: bytes, mime_type: str) -> Transcription:
        reason: str | None = None

        if self._gemini is not None:
            try:
                return Transcription(self._with_gemini(audio, mime_type), "gemini")
            except (GeminiError, OSError) as exc:
                # OSError covers a socket timeout, which urllib raises bare
                # rather than wrapped — a slow Gemini is exactly the case the
                # fallback exists for.
                status = getattr(exc, "status", None)
                reason = f"gemini {status or type(exc).__name__}: {str(exc)[:160]}"
                log.warning("speech: gemini failed, falling back — %s", reason)
        else:
            reason = "gemini not configured"

        if self._settings.groq_api_key:
            try:
                text = self._with_groq(audio, mime_type)
                return Transcription(text, "groq", reason)
            except GroqError as exc:
                log.warning("speech: groq failed too — %s", exc)
                raise TranscriptionError(
                    f"both engines failed ({reason}; groq: {exc})") from exc

        raise TranscriptionError(f"no engine could answer ({reason})")

    # ── Gemini ───────────────────────────────────────────────────────────────

    def _with_gemini(self, audio: bytes, mime_type: str) -> str:
        attempts = 1 + max(0, self._settings.gemini_retries)
        for attempt in range(1, attempts + 1):
            try:
                response = self._gemini.generate(
                    _GEMINI_PROMPT,
                    audio=(audio, mime_type),
                    temperature=0,
                )
                return _clean_gemini(response.text)
            except GeminiError as exc:
                transient = exc.status in (429, 500, 503)
                if not transient or attempt == attempts:
                    raise
                log.info("speech: gemini %s, retry %d", exc.status, attempt)
                time.sleep(self._settings.gemini_retry_delay)
        raise GeminiError("unreachable")  # pragma: no cover

    # ── Groq ─────────────────────────────────────────────────────────────────

    def _with_groq(self, audio: bytes, mime_type: str) -> str:
        payload = groq_post(
            self._settings.groq_api_key,
            {
                "model": self._settings.groq_model,
                "language": "en",
                "temperature": "0",
                # verbose_json carries each segment's no-speech probability,
                # which is how a hallucinated "Thank you." over silence is
                # caught.
                "response_format": "verbose_json",
            },
            audio,
            mime_type,
            self._settings.groq_timeout,
        )
        return _clean_whisper(payload, self._settings.groq_no_speech_threshold)


def groq_post(
    api_key: str,
    fields: dict[str, str] | list[tuple[str, str]],
    audio: bytes,
    mime_type: str,
    timeout: float,
) -> dict:
    """One multipart call to Groq's transcription endpoint.

    Shared by transcription (this module) and by the word timings that
    text-to-speech needs (``tts.py``). ``fields`` may be a list of pairs,
    because Groq takes ``timestamp_granularities[]`` as a repeated field.
    """
    boundary = uuid.uuid4().hex
    pairs = list(fields.items()) if isinstance(fields, dict) else list(fields)
    body = bytearray()
    for name, value in pairs:
        body += (f"--{boundary}\r\nContent-Disposition: form-data; "
                 f'name="{name}"\r\n\r\n{value}\r\n').encode()
    body += (f"--{boundary}\r\nContent-Disposition: form-data; name=\"file\"; "
             f'filename="speech{_extension(mime_type)}"\r\n'
             f"Content-Type: {mime_type}\r\n\r\n").encode()
    body += audio
    body += f"\r\n--{boundary}--\r\n".encode()

    request = urllib.request.Request(
        _GROQ_URL,
        data=bytes(body),
        headers={
            "Authorization": f"Bearer {api_key}",
            "Content-Type": f"multipart/form-data; boundary={boundary}",
            # Groq's edge answers Python's default User-Agent with a bare
            # 403 — measured while benchmarking, not assumed.
            "User-Agent": "wordos-ai-service/1.0",
        },
        method="POST",
    )
    try:
        with urllib.request.urlopen(request, timeout=timeout) as response:
            return json.loads(response.read())
    except urllib.error.HTTPError as exc:
        detail = exc.read().decode("utf-8", errors="replace")[:300]
        raise GroqError(f"Groq returned {exc.code}: {detail}",
                        status=exc.code) from exc
    except OSError as exc:  # URLError, a reset, or a bare socket timeout
        raise GroqError(f"Could not reach Groq: {exc}") from exc
    except json.JSONDecodeError as exc:
        raise GroqError("Groq returned unparseable JSON") from exc


def _clean_gemini(text: str) -> str:
    text = text.strip()
    if _NO_SPEECH in text:
        return ""
    # A model occasionally wraps the answer in quotes despite being told not to.
    if len(text) >= 2 and text[0] == text[-1] and text[0] in "\"'":
        text = text[1:-1].strip()
    return text


def _clean_whisper(payload: dict, no_speech_threshold: float) -> str:
    segments = payload.get("segments")
    if not isinstance(segments, list) or not segments:
        return str(payload.get("text", "")).strip()
    kept = [
        str(s.get("text", "")).strip()
        for s in segments
        if float(s.get("no_speech_prob", 0) or 0) < no_speech_threshold
    ]
    return " ".join(t for t in kept if t).strip()


def _extension(mime_type: str) -> str:
    return {
        "audio/mp4": ".m4a",
        "audio/m4a": ".m4a",
        "audio/x-m4a": ".m4a",
        "audio/aac": ".aac",
        "audio/wav": ".wav",
        "audio/x-wav": ".wav",
        "audio/ogg": ".ogg",
        "audio/webm": ".webm",
        "audio/mpeg": ".mp3",
        "audio/flac": ".flac",
    }.get(mime_type, ".m4a")
