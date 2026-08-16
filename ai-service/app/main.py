"""WordOS AI service.

    C# Backend  ──POST──►  this service  ──►  Gemini

Owns prompts and provider communication, and nothing else. It never decides
whether a learner passed: it returns observations, and the backend applies the
rule (rule R2, `System Archticture.txt` §11).

Only the backend may call it — the token check below is what stops anyone who
can reach the port from spending the API budget.
"""

from __future__ import annotations

import logging
import secrets
import time
from typing import Annotated

from fastapi import Depends, FastAPI, Header, HTTPException, status
from fastapi.responses import JSONResponse
from pydantic import BaseModel, Field

from .config import ConfigurationError, load_settings
from .gemini import GeminiClient, GeminiError
from . import prompts

logging.basicConfig(
    level=logging.INFO,
    format="%(asctime)s %(levelname)-5s %(message)s",
)
log = logging.getLogger("wordos.ai")

try:
    SETTINGS = load_settings()
except ConfigurationError as exc:  # pragma: no cover - startup failure path
    raise SystemExit(f"\n{exc}\n") from exc

CLIENT = GeminiClient(SETTINGS.gemini_api_key, SETTINGS.gemini_model)

app = FastAPI(title="WordOS AI Service", version="1.0.0")


# ── Authentication ───────────────────────────────────────────────────────────

def require_service_token(
    x_service_token: Annotated[str | None, Header()] = None,
) -> None:
    """Only the backend may call this service.

    Skipped when no token is configured, which keeps local development simple —
    but the service refuses to be useful in that state to anyone off-host,
    because it should never be exposed directly.
    """
    if not SETTINGS.service_token:
        return

    # Constant-time: a byte-by-byte comparison would leak the token's prefix.
    if not x_service_token or not secrets.compare_digest(
        x_service_token, SETTINGS.service_token
    ):
        raise HTTPException(
            status_code=status.HTTP_401_UNAUTHORIZED,
            detail={"code": "UNAUTHORIZED", "message": "Invalid service token."},
        )


# ── Contracts ────────────────────────────────────────────────────────────────

class TargetWord(BaseModel):
    text: str = Field(max_length=128)
    meaning: str = Field(max_length=256)
    definition: str = Field(default="", max_length=1024)
    part_of_speech: str = Field(default="", max_length=32)


class ContentRequest(BaseModel):
    level: str = Field(max_length=8)
    interests: list[str] = Field(default_factory=list, max_length=20)
    words: list[TargetWord] = Field(min_length=1, max_length=15)
    listening: bool = False
    comprehension_count: int = Field(default=5, ge=1, le=10)
    # Active vocabulary to re-encounter, not to test. Bounded like every other
    # list here so an oversized request cannot inflate the prompt.
    reuse_words: list[str] = Field(default_factory=list, max_length=10)


class WordContext(BaseModel):
    word: str
    before: str | None
    sentence: str
    after: str | None


class ComprehensionQuestion(BaseModel):
    prompt: str
    correct: str
    distractors: list[str]


class ContentResponse(BaseModel):
    text: str
    sentences: list[str]
    comprehension: list[ComprehensionQuestion]
    contexts: list[WordContext]
    prompt_version: str
    model: str
    tokens: int


class WritingRequest(BaseModel):
    word: str = Field(max_length=128)
    meaning: str = Field(max_length=256)
    definition: str = Field(default="", max_length=1024)
    level: str = Field(max_length=8)
    sentence: str = Field(min_length=1, max_length=2000)


class WritingResponse(BaseModel):
    used_word: bool
    meaning_correct: bool
    usage_correct: bool
    understandable: bool
    grammar_note: str
    feedback: str
    suggestion: str | None = None
    prompt_version: str
    model: str
    tokens: int


class TranscriptTurn(BaseModel):
    from_ai: bool
    text: str = Field(max_length=4000)


class SpeakingRequest(BaseModel):
    learner_name: str = Field(max_length=120)
    level: str = Field(max_length=8)
    remaining_words: list[str] = Field(default_factory=list, max_length=15)
    used_words: list[str] = Field(default_factory=list, max_length=15)
    transcript: list[TranscriptTurn] = Field(default_factory=list, max_length=40)


class SpeakingResponse(BaseModel):
    reply: str
    words_used_naturally: list[str]
    prompt_version: str
    model: str
    tokens: int


class EvalTargetWord(BaseModel):
    text: str = Field(max_length=128)
    meaning: str = Field(max_length=256)
    definition: str = Field(default="", max_length=2048)


class SpeakingEvalRequest(BaseModel):
    learner_name: str = Field(max_length=120)
    level: str = Field(max_length=8)
    words: list[EvalTargetWord] = Field(min_length=1, max_length=15)
    transcript: list[TranscriptTurn] = Field(min_length=1, max_length=60)


class SpeakingWordObservation(BaseModel):
    """What was observed about one word — never whether it passed.

    The verdict is the backend's (rule R2, ADR-015), so there is deliberately no
    `passed` field here for a prompt tweak to start filling in.
    """

    word: str
    used: bool
    meaning_correct: bool
    understandable: bool
    grammar_acceptable: bool
    major_grammar_problem: bool
    evidence: str = ""
    feedback: str = ""


class SpeakingEvalResponse(BaseModel):
    words: list[SpeakingWordObservation]
    summary: str
    prompt_version: str
    model: str
    tokens: int


# ── Endpoints ────────────────────────────────────────────────────────────────

@app.get("/health")
def health() -> dict:
    """Liveness. Deliberately does not call Gemini — that would bill a request
    for every health check."""
    return {"status": "ok", "model": SETTINGS.gemini_model}


@app.post(
    "/ai/content",
    response_model=ContentResponse,
    dependencies=[Depends(require_service_token)],
)
def generate_content(request: ContentRequest) -> ContentResponse:
    """Generates a Reading or Listening passage with its questions."""
    started = time.monotonic()

    prompt = prompts.reading_prompt(
        level=request.level,
        interests=request.interests,
        words=[w.model_dump() for w in request.words],
        listening=request.listening,
        comprehension_count=request.comprehension_count,
        reuse_words=request.reuse_words,
    )

    payload = _generate_json(prompt, prompts.READING_SCHEMA)
    sentences = [s.strip() for s in payload.get("sentences", []) if s.strip()]

    if not sentences:
        raise _upstream("Gemini returned no sentences")

    # The model reports which sentence holds each target word; the neighbours
    # are taken from the array rather than trusting it to repeat them, so an
    # off-by-one in the model cannot silently produce a context that omits the
    # word (demo review §26).
    contexts: list[WordContext] = []
    for target in payload.get("targets", []):
        word = str(target.get("word", "")).strip()
        index = target.get("sentence_index")

        if not isinstance(index, int) or not 0 <= index < len(sentences):
            index = next(
                (i for i, s in enumerate(sentences)
                 if word.lower() in s.lower()),
                None,
            )
        if index is None:
            continue

        contexts.append(WordContext(
            word=word,
            before=sentences[index - 1] if index > 0 else None,
            sentence=sentences[index],
            after=sentences[index + 1] if index + 1 < len(sentences) else None,
        ))

    questions = [
        ComprehensionQuestion(
            prompt=q["prompt"],
            correct=q["correct"],
            # Exactly three, so every question has four options.
            distractors=list(q.get("distractors", []))[:3],
        )
        for q in payload.get("comprehension", [])
        if q.get("prompt") and q.get("correct")
    ]

    elapsed = int((time.monotonic() - started) * 1000)
    tokens = _last_tokens
    log.info(
        "content level=%s words=%d listening=%s questions=%d tokens=%d %dms",
        request.level, len(request.words), request.listening,
        len(questions), tokens, elapsed,
    )

    return ContentResponse(
        text=" ".join(sentences),
        sentences=sentences,
        comprehension=questions,
        contexts=contexts,
        prompt_version=prompts.READING_PROMPT_VERSION,
        model=SETTINGS.gemini_model,
        tokens=tokens,
    )


@app.post(
    "/ai/writing",
    response_model=WritingResponse,
    dependencies=[Depends(require_service_token)],
)
def evaluate_writing(request: WritingRequest) -> WritingResponse:
    """Reports observations about one learner sentence.

    Deliberately returns no pass/fail — the backend owns that decision (R2).
    """
    payload = _generate_json(
        prompts.writing_prompt(
            word=request.word,
            meaning=request.meaning,
            definition=request.definition,
            level=request.level,
            sentence=request.sentence,
        ),
        prompts.WRITING_SCHEMA,
        temperature=0.2,
    )

    log.info("writing word=%s tokens=%d", request.word, _last_tokens)

    return WritingResponse(
        used_word=bool(payload.get("used_word")),
        meaning_correct=bool(payload.get("meaning_correct")),
        usage_correct=bool(payload.get("usage_correct")),
        understandable=bool(payload.get("understandable")),
        grammar_note=str(payload.get("grammar_note", "none")),
        feedback=str(payload.get("feedback", "")),
        suggestion=(payload.get("suggestion") or None),
        prompt_version=prompts.WRITING_PROMPT_VERSION,
        model=SETTINGS.gemini_model,
        tokens=_last_tokens,
    )


@app.post(
    "/ai/speaking/turn",
    response_model=SpeakingResponse,
    dependencies=[Depends(require_service_token)],
)
def speaking_turn(request: SpeakingRequest) -> SpeakingResponse:
    payload = _generate_json(
        prompts.speaking_turn_prompt(
            learner_name=request.learner_name,
            level=request.level,
            remaining_words=request.remaining_words,
            used_words=request.used_words,
            transcript=[t.model_dump() for t in request.transcript],
        ),
        prompts.SPEAKING_TURN_SCHEMA,
        temperature=0.8,
    )

    log.info("speaking remaining=%d tokens=%d",
             len(request.remaining_words), _last_tokens)

    return SpeakingResponse(
        reply=str(payload.get("reply", "")),
        words_used_naturally=[
            str(w) for w in payload.get("words_used_naturally", [])
        ],
        prompt_version=prompts.SPEAKING_PROMPT_VERSION,
        model=SETTINGS.gemini_model,
        tokens=_last_tokens,
    )


# ── Provider plumbing ────────────────────────────────────────────────────────

_last_tokens = 0


def _generate_json(prompt: str, schema: dict, temperature: float = 0.7) -> dict:
    """Calls Gemini and records the token cost.

    A provider failure becomes a 502 with a stable code: the backend needs to
    distinguish "AI unavailable" from "bad request" so it can fall back rather
    than fail the learner's session.
    """
    global _last_tokens

    try:
        response = CLIENT.generate(
            prompt,
            system_instruction=prompts.system_instruction(),
            json_schema=schema,
            temperature=temperature,
        )
    except GeminiError as exc:
        log.warning("gemini failure: %s", exc)
        raise _upstream(str(exc)) from exc

    _last_tokens = (response.prompt_tokens or 0) + (response.output_tokens or 0)

    import json
    try:
        return json.loads(response.text)
    except json.JSONDecodeError as exc:
        log.warning("unparseable JSON from Gemini: %s", response.text[:200])
        raise _upstream("Gemini returned unparseable JSON") from exc


def _upstream(message: str) -> HTTPException:
    return HTTPException(
        status_code=status.HTTP_502_BAD_GATEWAY,
        detail={"code": "AI_UNAVAILABLE", "message": message},
    )


@app.exception_handler(HTTPException)
def http_exception_handler(_, exc: HTTPException) -> JSONResponse:
    detail = exc.detail
    if not isinstance(detail, dict):
        detail = {"code": "ERROR", "message": str(detail)}
    return JSONResponse(status_code=exc.status_code, content={"error": detail})


@app.post(
    "/ai/speaking/evaluate",
    response_model=SpeakingEvalResponse,
    dependencies=[Depends(require_service_token)],
)
def speaking_evaluate(request: SpeakingEvalRequest) -> SpeakingEvalResponse:
    """Judges a finished conversation, once.

    Called at the end rather than after every turn: a learner who fumbles a word
    early and uses it well later deserves to be judged on the whole exchange,
    and per-turn evaluation would also multiply the cost of a session by the
    number of things the learner says.
    """
    payload = _generate_json(
        prompts.speaking_eval_prompt(
            learner_name=request.learner_name,
            level=request.level,
            words=[w.model_dump() for w in request.words],
            transcript=[t.model_dump() for t in request.transcript],
        ),
        prompts.SPEAKING_EVAL_SCHEMA,
        # Low temperature: this is a judgement, and the same conversation should
        # not pass one evening and fail the next.
        temperature=0.1,
    )

    reported = {
        str(w.get("word", "")).strip().lower(): w
        for w in payload.get("words", [])
    }

    # Answered per requested word rather than per returned row: a model that
    # silently drops a word must not make that word disappear from the result.
    observations = []
    for word in request.words:
        row = reported.get(word.text.strip().lower(), {})
        observations.append(SpeakingWordObservation(
            word=word.text,
            used=bool(row.get("used")),
            meaning_correct=bool(row.get("meaning_correct")),
            understandable=bool(row.get("understandable")),
            grammar_acceptable=bool(row.get("grammar_acceptable")),
            major_grammar_problem=bool(row.get("major_grammar_problem")),
            evidence=str(row.get("evidence", ""))[:500],
            feedback=str(row.get("feedback", ""))[:500],
        ))

    log.info(
        "speaking eval words=%d turns=%d tokens=%d",
        len(request.words), len(request.transcript), _last_tokens,
    )

    return SpeakingEvalResponse(
        words=observations,
        summary=str(payload.get("summary", ""))[:1000],
        prompt_version=prompts.SPEAKING_EVAL_PROMPT_VERSION,
        model=SETTINGS.gemini_model,
        tokens=_last_tokens,
    )
