"""WordOS AI service.

    C# Backend  ──POST──►  this service  ──►  Gemini

Owns prompts and provider communication, and nothing else. It never decides
whether a learner passed: it returns observations, and the backend applies the
rule (rule R2, `System Archticture.txt` §11).

Only the backend may call it — the token check below is what stops anyone who
can reach the port from spending the API budget.
"""

from __future__ import annotations

import json
import logging
import os
import re
import secrets
import threading
import time
from concurrent.futures import ThreadPoolExecutor
from typing import Annotated, NamedTuple

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

    Skipped when no token is configured — which now takes an explicit
    ``AI_ALLOW_UNAUTHENTICATED=true`` to reach, because startup refuses a
    missing token outright (see ``config._require_service_token``). Leaving the
    variable unset used to land here silently and accept every caller.
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
    # Which form the learner is practising — "past participle", "plural" — or
    # nothing for the word itself. The passage has to use *that* form, because
    # that is what they added (ADR-047).
    form: str | None = Field(default=None, max_length=32)
    # Whether the passage may write it in the plural. True only when the plural
    # is the word plus s/es: `books` teaches a `book` learner something, `mice`
    # is a word they have not met.
    may_pluralise: bool = False


class ContentRequest(BaseModel):
    level: str = Field(max_length=8)
    interests: list[str] = Field(default_factory=list, max_length=20)
    # Empty is legitimate: a practice passage has no vocabulary attached to it
    # (Part 2 §5). Still bounded above, so a request cannot inflate the prompt.
    words: list[TargetWord] = Field(default_factory=list, max_length=15)
    listening: bool = False
    comprehension_count: int = Field(default=5, ge=1, le=10)
    # Active vocabulary to re-encounter, not to test. Bounded like every other
    # list here so an oversized request cannot inflate the prompt.
    # Either a bare word or one carrying the form the learner knows it in —
    # `gone` the participle should come back as `gone` (ADR-047).
    reuse_words: list[str | TargetWord] = Field(default_factory=list, max_length=10)


class WordContext(BaseModel):
    word: str
    before: str | None
    sentence: str
    after: str | None


class ComprehensionQuestion(BaseModel):
    prompt: str
    correct: str
    distractors: list[str]


class GlossaryEntry(BaseModel):
    """One word of the passage, with the meaning it carries *there*.

    Produced while the passage is being written, because that is when the
    model knows which sense it meant. A dictionary consulted afterwards can
    only offer every sense the word has ever had.
    """

    word: str
    meaning_ar: str
    part_of_speech: str


class ContentResponse(BaseModel):
    # Empty when the model omitted it. The client renders a passage without a
    # title rather than an empty heading, so a missing one costs nothing.
    title: str = ""
    text: str
    sentences: list[str]
    comprehension: list[ComprehensionQuestion]
    contexts: list[WordContext]
    glossary: list[GlossaryEntry] = []
    prompt_version: str
    model: str
    tokens: int


class RelevelRequest(BaseModel):
    # A C2 passage runs to some 720 words, and re-telling sends the whole
    # of it back up. 8,000 characters was a comfortable ceiling when passages
    # were a dozen sentences and a tight one now.
    text: str = Field(min_length=1, max_length=20000)
    from_level: str = Field(max_length=8)
    to_level: str = Field(max_length=8)
    words: list[TargetWord] = Field(default_factory=list, max_length=15)
    comprehension_count: int = Field(default=5, ge=1, le=10)


class WritingRequest(BaseModel):
    word: str = Field(max_length=128)
    meaning: str = Field(max_length=256)
    definition: str = Field(default="", max_length=1024)
    level: str = Field(max_length=8)
    sentence: str = Field(min_length=1, max_length=2000)
    # The language the learner reads the app in. Only the feedback follows it;
    # the sentence they wrote and the word they used are untouched (ADR-035).
    feedback_language: str = Field(default="ar", max_length=8)


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


class MeaningCheckRequest(BaseModel):
    """An Arabic meaning a learner typed, for checking (ADR-074)."""

    word: str = Field(max_length=128)
    # *Every* sense the lexicon has for this word, not the commonest one.
    #
    # Sending one was a real bug: `book` resolved to "a set of printed pages",
    # so a learner writing "يحجز" — correct, and the reason they were allowed to
    # type a meaning at all — was told they were wrong. A word the learner may
    # mean any sense of must be judged against all of them.
    definitions: list[str] = Field(default_factory=list, max_length=12)
    part_of_speech: str = Field(default="", max_length=32)
    meaning: str = Field(min_length=1, max_length=256)
    # The language the note to the learner is written in (ADR-035).
    feedback_language: str = Field(default="ar", max_length=8)


class MeaningCheckResponse(BaseModel):
    matches: bool
    corrected: str | None = None
    suggestions: list[str] = Field(default_factory=list)
    note: str
    prompt_version: str
    model: str
    tokens: int


class TranscriptTurn(BaseModel):
    from_ai: bool
    text: str = Field(max_length=4000)


class FormReminder(BaseModel):
    """A word the learner all but used: they said another form of it.

    "I go there every year" is not `went`, but it is one step away — and "try to
    use went" does not say which step (ADR-050).
    """

    word: str = Field(max_length=128)
    form: str = Field(max_length=32)
    said: str = Field(max_length=128)


class SpeakingRequest(BaseModel):
    # Colours the conversation; never decides whether a word is asked for.
    interests: list[str] = Field(default_factory=list, max_length=20)
    learner_name: str = Field(max_length=120)
    level: str = Field(max_length=8)
    remaining_words: list[str] = Field(default_factory=list, max_length=15)
    used_words: list[str] = Field(default_factory=list, max_length=15)
    transcript: list[TranscriptTurn] = Field(default_factory=list, max_length=40)
    # What each remaining word is — the past tense, a plural, or the plain word
    # (ADR-047). A question that invites the wrong form cannot be answered with
    # the word being practised.
    remaining_shapes: list[TargetWord] = Field(default_factory=list, max_length=15)
    form_reminders: list[FormReminder] = Field(default_factory=list, max_length=15)
    # Words the conversation never reached, sent only when it is being closed
    # anyway. The goodbye must not congratulate a learner on words they never
    # said (ADR-070).
    unused_words: list[str] = Field(default_factory=list, max_length=15)


class SpeakingResponse(BaseModel):
    reply: str
    # The one judgement a reader of the transcript cannot make: whether a word
    # that appears there was used or merely named (ADR-048). Usually empty.
    words_only_named: list[str]
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
    feedback_language: str = Field(default="ar", max_length=8)


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
    # The model sentence: their own words repaired, or one to copy (ADR-048).
    better: str = ""


class PlacementAnswerIn(BaseModel):
    item_id: str = Field(max_length=64)
    level: str = Field(max_length=8)
    prompt: str = Field(max_length=2000)
    answer: str = Field(default="", max_length=4000)


class PlacementEvalRequest(BaseModel):
    skill: str = Field(max_length=16)
    answers: list[PlacementAnswerIn] = Field(min_length=1, max_length=10)


class PlacementAnswerRating(BaseModel):
    """What the model thought of one answer — never the learner's final band.

    The backend combines these into a level with its own estimator and its own
    confidence rules (rule R2). `score` is partial credit in [0, 1], which is
    what that estimator actually consumes.
    """

    item_id: str
    estimated_level: str
    score: float
    evidence: str = ""


class PlacementEvalResponse(BaseModel):
    answers: list[PlacementAnswerRating]
    overall_level: str
    summary: str
    prompt_version: str
    model: str
    tokens: int


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

    # A short passage is glossed in the same call; a long one is written first
    # and glossed by the parallel batches below, which is far quicker than
    # asking one call for four hundred entries (ADR-067).
    inline = prompts.wants_inline_glossary(
        request.level, len(request.words), listening=request.listening)

    prompt = prompts.reading_prompt(
        level=request.level,
        interests=request.interests,
        words=[w.model_dump() for w in request.words],
        listening=request.listening,
        comprehension_count=request.comprehension_count,
        reuse_words=[
            w if isinstance(w, str) else w.model_dump()
            for w in request.reuse_words
        ],
        inline_glossary=inline,
    )

    generated = _generate_json(
        prompt, prompts.reading_schema(inline_glossary=inline))
    return _shape_content(
        generated.payload, prompts.READING_PROMPT_VERSION, started,
        generated.tokens)


#: Every tappable word of a passage, matching the client's own rule: letters
#: and digits, plus the apostrophes and hyphens that live *inside* a word, so
#: "doesn't" and "well-known" are one word each. The two patterns must agree —
#: a glossary keyed on anything the client does not tap is a glossary with
#: holes in it.
_WORD = re.compile(r"[^\W_](?:[^\W_]|['’-])*")

#: How many words one repair call may ask about, and how many calls a passage
#: may spend.
#:
#: One call was enough while passages were a dozen sentences. Sized like the
#: exam texts they stand in for (ADR-066) a C2 passage runs to 750 words and
#: some 430 distinct ones, and at that length the model stops glossing almost
#: entirely — one measured run returned 40 entries for 430 words. A single
#: repair capped at 200 then left 227 words a learner could tap and get a
#: dictionary list for, which is the whole bug back again at the top of the
#: ladder.
#:
#: So the repair works in batches until the passage is covered. The ceiling is
#: on the number of calls, not on the number of words: four batches cover any
#: passage this app can produce, and a fifth would mean the model is refusing
#: rather than running out of room.
_REPAIR_BATCH_WORDS = 150
_MAX_REPAIR_CALLS = 6


def _uncovered_words(sentences: list[str], glossary: list[GlossaryEntry]) -> list[str]:
    """The passage's words that the glossary does not answer for.

    Compared case-insensitively and on the surface form, because that is how
    the client looks an entry up: it hands over the word it drew on screen.
    A gloss of "study" does not answer a tap on "studying".
    """
    glossed = {entry.word.strip().lower() for entry in glossary}

    missing: list[str] = []
    seen: set[str] = set()
    for sentence in sentences:
        for match in _WORD.finditer(sentence):
            word = match.group(0)
            key = word.lower()
            if key in glossed or key in seen:
                continue
            seen.add(key)
            missing.append(word)
    return missing


def _repair_glossary(
    sentences: list[str], glossary: list[GlossaryEntry]
) -> tuple[list[GlossaryEntry], int]:
    """Builds whatever the passage still lacks, in one round of parallel calls.

    The passage is the product here: a tap that cannot be answered from the
    glossary falls back to the lexicon, which returns every sense the word has
    ever had — six for "bank", five of them wrong in any given sentence.

    **Issued together, not one after another.** Sequentially, a C2 passage's
    three batches cost fourteen seconds each and the learner sat through
    forty-two of them; re-levelling reached seventy-seven seconds end to end and
    started tripping the backend's budget, which answered the learner with "the
    passage could not be rewritten". The batches do not depend on each other —
    each asks about a different set of words — so there was never a reason to
    wait between them (ADR-067).

    Failure is never fatal and never raises: a passage with an incomplete
    glossary is weaker content, not a broken session, and refusing it would send
    the learner to the deterministic fallback — a canned passage nobody asked
    for.
    """
    missing = _uncovered_words(sentences, glossary)
    if not missing:
        log.info("glossary complete as written: %d entries", len(glossary))
        return glossary, 0

    batches = [
        missing[i:i + _REPAIR_BATCH_WORDS]
        for i in range(0, len(missing), _REPAIR_BATCH_WORDS)
    ][:_MAX_REPAIR_CALLS]

    log.info(
        "glossary short by %d words; asking in %d parallel batches",
        len(missing), len(batches))

    def ask(batch: list[str]) -> tuple[list[dict], int]:
        try:
            generated = _generate_json(
                prompts.glossary_repair_prompt(
                    sentences=sentences, missing=batch),
                prompts.GLOSSARY_REPAIR_SCHEMA,
                # Not a creative task: there is one right answer per word, and
                # the sentence it sits in already fixes which sense that is.
                temperature=0.2,
            )
        except HTTPException as exc:
            log.warning("glossary batch failed: %s", exc.detail)
            return [], 0
        return generated.payload.get("glossary", []), generated.tokens

    total_tokens = 0
    answers: list[list[dict]] = []

    if len(batches) == 1:
        entries, tokens = ask(batches[0])
        answers.append(entries)
        total_tokens += tokens
    else:
        with ThreadPoolExecutor(max_workers=len(batches)) as pool:
            for entries, tokens in pool.map(ask, batches):
                answers.append(entries)
                total_tokens += tokens

    known = {entry.word.strip().lower() for entry in glossary}
    for entries in answers:
        for entry in entries:
            word = str(entry.get("word", "")).strip()
            meaning = str(entry.get("meaning_ar", "")).strip()
            if not word or not meaning or word.lower() in known:
                continue
            known.add(word.lower())
            glossary.append(GlossaryEntry(
                word=word,
                meaning_ar=meaning,
                part_of_speech=str(entry.get("part_of_speech") or "other"),
            ))

    still_missing = len(_uncovered_words(sentences, glossary))
    log.info(
        "glossary settled: %d entries, %d words still unglossed",
        len(glossary), still_missing)
    return glossary, total_tokens


def _join_into_paragraphs(sentences: list[str], breaks: object) -> str:
    """The passage as one string, with its paragraphs kept.

    A reading text is built — an opening that says what it is about, body
    paragraphs each developing one idea, a close (ADR-068) — and a wall of
    prose hides that structure however well the sentences were written. The
    breaks arrive as sentence indexes, so a blank line goes in front of each.

    Anything the model reports that is not a usable index is ignored rather
    than trusted: a break at 0, past the end, or not an integer at all would
    only produce a stray blank line at the top of the passage.
    """
    starts = {
        index for index in (breaks if isinstance(breaks, list) else [])
        if isinstance(index, int) and 0 < index < len(sentences)
    }

    if not starts:
        return " ".join(sentences)

    out: list[str] = []
    for index, sentence in enumerate(sentences):
        out.append(("\n\n" if index in starts else " ") + sentence
                   if index else sentence)
    return "".join(out)


def _shape_content(
    payload: dict,
    prompt_version: str,
    started: float,
    tokens: int,
) -> ContentResponse:
    """Turns a raw generation payload into the response both callers return.

    Shared by a fresh passage and a re-told one: they differ in the prompt, not
    in the shape of what comes back.
    """
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
    log.info(
        "content prompt=%s sentences=%d questions=%d tokens=%d %dms",
        prompt_version, len(sentences), len(questions), tokens, elapsed,
    )

    # Deduplicated on the surface form: the model sometimes lists a word once
    # per occurrence, and the client only ever needs one entry per spelling.
    glossary: list[GlossaryEntry] = []
    seen: set[str] = set()
    for entry in payload.get("glossary", []):
        word = str(entry.get("word", "")).strip()
        meaning = str(entry.get("meaning_ar", "")).strip()
        if not word or not meaning or word.lower() in seen:
            continue
        seen.add(word.lower())
        glossary.append(GlossaryEntry(
            word=word,
            meaning_ar=meaning,
            part_of_speech=str(entry.get("part_of_speech") or "other"),
        ))

    # Whatever the model skipped is asked for again, by name. The cost of the
    # second call is reported with the first: it belongs to this passage.
    glossary, repair_tokens = _repair_glossary(sentences, glossary)

    return ContentResponse(
        title=str(payload.get("title") or "").strip(),
        text=_join_into_paragraphs(sentences, payload.get("paragraph_breaks")),
        sentences=sentences,
        comprehension=questions,
        contexts=contexts,
        glossary=glossary,
        prompt_version=prompt_version,
        model=SETTINGS.gemini_model,
        tokens=tokens + repair_tokens,
    )


@app.post(
    "/ai/content/relevel",
    response_model=ContentResponse,
    dependencies=[Depends(require_service_token)],
)
def relevel_content(request: RelevelRequest) -> ContentResponse:
    """Re-tells an existing passage at a different level.

    Same story, different language. The learner asked for this because the text
    was too hard or too easy, not because they wanted a different subject.
    """
    started = time.monotonic()

    inline = prompts.wants_inline_glossary(
        request.to_level, len(request.words), listening=False)

    prompt = prompts.relevel_prompt(
        text=request.text,
        from_level=request.from_level,
        to_level=request.to_level,
        words=[w.model_dump() for w in request.words],
        comprehension_count=request.comprehension_count,
        inline_glossary=inline,
    )

    generated = _generate_json(
        prompt, prompts.reading_schema(inline_glossary=inline))
    return _shape_content(
        generated.payload, prompts.RELEVEL_PROMPT_VERSION, started,
        generated.tokens)


@app.post(
    "/ai/writing",
    response_model=WritingResponse,
    dependencies=[Depends(require_service_token)],
)
def evaluate_writing(request: WritingRequest) -> WritingResponse:
    """Reports observations about one learner sentence.

    Deliberately returns no pass/fail — the backend owns that decision (R2).
    """
    generated = _generate_json(
        prompts.writing_prompt(
            word=request.word,
            meaning=request.meaning,
            definition=request.definition,
            level=request.level,
            sentence=request.sentence,
            feedback_language=request.feedback_language,
        ),
        prompts.WRITING_SCHEMA,
        temperature=0.2,
    )

    payload, tokens = generated
    log.info("writing word=%s tokens=%d", request.word, tokens)

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
        tokens=tokens,
    )


@app.post(
    "/ai/meaning/check",
    response_model=MeaningCheckResponse,
    dependencies=[Depends(require_service_token)],
)
def check_meaning(request: MeaningCheckRequest) -> MeaningCheckResponse:
    """Judges an Arabic meaning a learner wrote for an English word (ADR-074).

    Reports; does not decide. The backend refuses the add on `matches: false`,
    and the learner may still insist — which is the point, because this feature
    exists precisely because an automated source of meanings was wrong too
    often to trust blindly (rule R2, ADR-072).

    Temperature is low: two learners typing the same meaning for the same word
    should get the same answer, and "is this a meaning of this word" is not a
    question that benefits from invention.
    """
    generated = _generate_json(
        prompts.meaning_check_prompt(
            word=request.word,
            definitions=request.definitions,
            part_of_speech=request.part_of_speech,
            meaning=request.meaning,
            feedback_language=request.feedback_language,
        ),
        prompts.MEANING_CHECK_SCHEMA,
        temperature=0.1,
    )

    payload, tokens = generated
    matches = bool(payload.get("matches"))

    # A correction is only meaningful when it actually differs. Models return
    # the input unchanged often enough that passing it through would show the
    # learner "did you mean: <exactly what you typed>".
    corrected = (payload.get("corrected") or "").strip() or None
    if corrected == request.meaning.strip():
        corrected = None

    suggestions = [
        s.strip()
        for s in (payload.get("suggestions") or [])
        if isinstance(s, str) and s.strip()
    ][:3]

    log.info(
        "meaning-check word=%s matches=%s tokens=%d",
        request.word, matches, tokens,
    )

    return MeaningCheckResponse(
        matches=matches,
        corrected=corrected,
        # Nothing to suggest when the meaning was accepted.
        suggestions=[] if matches else suggestions,
        note=str(payload.get("note", "")),
        prompt_version=prompts.MEANING_CHECK_PROMPT_VERSION,
        model=SETTINGS.gemini_model,
        tokens=tokens,
    )


@app.post(
    "/ai/speaking/turn",
    response_model=SpeakingResponse,
    dependencies=[Depends(require_service_token)],
)
def speaking_turn(request: SpeakingRequest) -> SpeakingResponse:
    generated = _generate_json(
        prompts.speaking_turn_prompt(
            learner_name=request.learner_name,
            level=request.level,
            remaining_words=request.remaining_words,
            used_words=request.used_words,
            transcript=[t.model_dump() for t in request.transcript],
            interests=request.interests,
            remaining_shapes=[w.model_dump() for w in request.remaining_shapes],
            form_reminders=[r.model_dump() for r in request.form_reminders],
            unused_words=request.unused_words,
        ),
        prompts.SPEAKING_TURN_SCHEMA,
        temperature=0.8,
    )

    payload, tokens = generated
    log.info("speaking remaining=%d tokens=%d",
             len(request.remaining_words), tokens)

    return SpeakingResponse(
        reply=str(payload.get("reply", "")),
        words_only_named=[
            str(w) for w in payload.get("words_only_named", [])
        ],
        prompt_version=prompts.SPEAKING_PROMPT_VERSION,
        model=SETTINGS.gemini_model,
        tokens=tokens,
    )


# ── Provider plumbing ────────────────────────────────────────────────────────


# How many model calls this worker will have in flight at once, and how long a
# request waits for a slot before being turned away (ADR-051).
#
# These endpoints are synchronous, so Starlette runs them on a thread pool and
# every in-flight call holds a thread and its own memory for the seconds Gemini
# takes. Without a ceiling the queue is unbounded: arrivals keep being accepted,
# each one waits longer than the last, and the client times out on work the
# server is still dutifully doing. A refusal after two seconds is a better
# answer than a timeout after ninety.
_MAX_IN_FLIGHT = max(1, int(os.environ.get("WORDOS_AI_MAX_IN_FLIGHT", "16")))
_ADMISSION_WAIT_SECONDS = float(
    os.environ.get("WORDOS_AI_ADMISSION_WAIT", "2.0"))

_in_flight = threading.BoundedSemaphore(_MAX_IN_FLIGHT)


class Generated(NamedTuple):
    """One model answer and what it cost.

    The cost travels *with* the answer. It used to be left in a module-level
    global for the endpoint to pick up afterwards — which works exactly as long
    as one request is in flight at a time. These endpoints are synchronous, so
    Starlette runs them on a thread pool: two learners arriving together read
    each other's token counts, and the token count is the experiment's own
    measurement (ADR-051).
    """

    payload: dict
    tokens: int


def _generate_json(
    prompt: str, schema: dict, temperature: float = 0.7
) -> Generated:
    """Calls Gemini and returns the answer with its token cost.

    A provider failure becomes a 502 with a stable code: the backend needs to
    distinguish "AI unavailable" from "bad request" so it can fall back rather
    than fail the learner's session.
    """
    if not _in_flight.acquire(timeout=_ADMISSION_WAIT_SECONDS):
        log.warning("at capacity: %d calls in flight", _MAX_IN_FLIGHT)
        raise HTTPException(
            status_code=status.HTTP_503_SERVICE_UNAVAILABLE,
            detail={
                "code": "AI_BUSY",
                "message": "The AI service is at capacity. Try again shortly.",
            },
        )

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
    finally:
        _in_flight.release()

    tokens = (response.prompt_tokens or 0) + (response.output_tokens or 0)

    try:
        return Generated(json.loads(response.text), tokens)
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
    generated = _generate_json(
        prompts.speaking_eval_prompt(
            learner_name=request.learner_name,
            level=request.level,
            words=[w.model_dump() for w in request.words],
            transcript=[t.model_dump() for t in request.transcript],
            feedback_language=request.feedback_language,
        ),
        prompts.SPEAKING_EVAL_SCHEMA,
        # Low temperature: this is a judgement, and the same conversation should
        # not pass one evening and fail the next.
        temperature=0.1,
    )

    payload, tokens = generated

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
            # Room for two or three sentences of teaching: the feedback is now
            # what the learner reads after a conversation, not a one-liner
            # (ADR-048).
            feedback=str(row.get("feedback", ""))[:1200],
            better=str(row.get("better", ""))[:400],
        ))

    log.info(
        "speaking eval words=%d turns=%d tokens=%d",
        len(request.words), len(request.transcript), tokens,
    )

    return SpeakingEvalResponse(
        words=observations,
        summary=str(payload.get("summary", ""))[:1000],
        prompt_version=prompts.SPEAKING_EVAL_PROMPT_VERSION,
        model=SETTINGS.gemini_model,
        tokens=tokens,
    )


@app.post(
    "/ai/placement/evaluate",
    response_model=PlacementEvalResponse,
    dependencies=[Depends(require_service_token)],
)
def placement_evaluate(request: PlacementEvalRequest) -> PlacementEvalResponse:
    """Rates the productive half of the placement test.

    Reading and Listening are not here on purpose: their answers are compared
    with a known key, so a model would add cost, latency and disagreement to a
    question that already has a right answer.

    Speaking and Writing have no key. Until now they were scored on length and
    lexical variety, which cannot tell a short fluent answer from a padded weak
    one — the gap this closes.
    """
    started = time.monotonic()

    generated = _generate_json(
        prompts.placement_eval_prompt(
            skill=request.skill,
            answers=[a.model_dump() for a in request.answers],
        ),
        prompts.PLACEMENT_EVAL_SCHEMA,
        # A placement result should not depend on the evening it was taken.
        temperature=0.1,
    )

    payload, tokens = generated

    rated = {
        str(a.get("item_id", "")).strip(): a
        for a in payload.get("answers", [])
    }

    # Answered per requested item, not per returned row: a model that drops an
    # item must not make that item vanish from the learner's evidence.
    ratings = []
    for answer in request.answers:
        row = rated.get(answer.item_id, {})
        ratings.append(PlacementAnswerRating(
            item_id=answer.item_id,
            estimated_level=str(row.get("estimated_level") or answer.level),
            score=max(0.0, min(1.0, float(row.get("score") or 0))),
            evidence=str(row.get("evidence") or ""),
        ))

    log.info(
        "placement eval skill=%s items=%d in %.2fs",
        request.skill, len(ratings), time.monotonic() - started,
    )

    return PlacementEvalResponse(
        answers=ratings,
        overall_level=str(payload.get("overall_level") or ""),
        summary=str(payload.get("summary") or ""),
        prompt_version=prompts.PLACEMENT_EVAL_PROMPT_VERSION,
        model=SETTINGS.gemini_model,
        # Was reading a key the payload never had, so placement always reported
        # zero cost — the one AI call whose price nobody could see.
        tokens=tokens,
    )
