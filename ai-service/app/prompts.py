"""Every prompt WordOS sends, in one file.

`System Archticture.txt` §11 makes prompt construction this service's job, and
the demo review (§50) asks for prompts to be centralised and versioned rather
than scattered through the code. Keeping them here means tuning the wording
never touches the learning rules.

Each prompt carries a version. When one changes, bump it — the backend logs the
version with every generation, so a drop in pass rates can be traced to a
prompt change rather than blamed on learners.
"""

from __future__ import annotations

# ── Shared framing ───────────────────────────────────────────────────────────

_SYSTEM = """You write material for WordOS, an English vocabulary app whose \
learners are Arabic speakers.

Rules that never change:
- Write natural English at the requested CEFR level. Do not write above it.
- Never translate the passage into Arabic. The learner reads English.
- Never explain the target words inside the text. The learner must infer meaning \
from context; explaining it destroys the exercise.
- Use every target word exactly once, in a sentence where the surrounding \
context genuinely hints at its meaning.
- Return only JSON matching the schema. No commentary, no markdown fences."""


def system_instruction() -> str:
    return _SYSTEM


# ── Reading / Listening content ──────────────────────────────────────────────

READING_PROMPT_VERSION = "reading-v3"

READING_SCHEMA = {
    "type": "object",
    "properties": {
        "sentences": {
            "type": "array",
            "items": {"type": "string"},
            "description": "The passage, split into individual sentences.",
        },
        "comprehension": {
            "type": "array",
            "items": {
                "type": "object",
                "properties": {
                    "prompt": {"type": "string"},
                    "correct": {"type": "string"},
                    "distractors": {
                        "type": "array",
                        "items": {"type": "string"},
                    },
                },
                "required": ["prompt", "correct", "distractors"],
            },
        },
        # Every content word in the passage, with the meaning it carries *here*.
        #
        # Produced during generation rather than looked up on demand: the model
        # has just chosen these words and knows which sense it meant. Asking
        # again later — after the sentence is written — is both slower and a
        # worse question, because by then the only evidence left is the text
        # itself. A dictionary lookup at tap time returns every sense a word
        # has ever had; "bank" alone has six, and five of them are wrong here.
        "glossary": {
            "type": "array",
            "items": {
                "type": "object",
                "properties": {
                    "word": {"type": "string"},
                    "meaning_ar": {"type": "string"},
                    "part_of_speech": {
                        "type": "string",
                        "enum": [
                            "noun", "verb", "adjective", "adverb", "pronoun",
                            "preposition", "conjunction", "determiner",
                            "auxiliary", "interjection", "numeral", "other",
                        ],
                    },
                },
                "required": ["word", "meaning_ar", "part_of_speech"],
            },
        },
        "targets": {
            "type": "array",
            "items": {
                "type": "object",
                "properties": {
                    "word": {"type": "string"},
                    "sentence_index": {"type": "integer"},
                },
                "required": ["word", "sentence_index"],
            },
        },
    },
    "required": ["sentences", "comprehension", "targets", "glossary"],
}


def _sentence_count(level: str, word_count: int, *, listening: bool) -> int:
    """How long the passage should be.

    Length is not a constant: an A1 learner meeting three new words does not
    need the same amount of prose as a C1 learner, and a listening clip is
    heard once with nothing to go back to, so it is deliberately shorter than
    the same content on a page (Part 2 §24).

    The floor is set by the words themselves — every target word must appear,
    each in a sentence that gives a clue to its meaning.
    """
    by_level = {"A1": 6, "A2": 7, "B1": 9, "B2": 10, "C1": 12, "C2": 13}
    target = by_level.get(level.upper(), 9)
    if listening:
        target -= 2
    return max(word_count + 3, target)


def reading_prompt(
    *,
    level: str,
    interests: list[str],
    words: list[dict],
    listening: bool,
    comprehension_count: int,
    reuse_words: list[str] | None = None,
) -> str:
    """Builds the passage prompt for Reading or Listening.

    The two share a generator because they share a learning rhythm, but the
    instructions differ where it matters: a listening script has to work when
    heard once, so it avoids the long subordinate clauses a reader can re-read.
    """
    topic = ", ".join(interests[:3]) if interests else "everyday student life"
    sentences = _sentence_count(level, len(words), listening=listening)
    word_lines = "\n".join(
        f'- "{w["text"]}" ({w["part_of_speech"]}) — means: {w["definition"]}'
        for w in words
    )

    medium = (
        "This text will be SPOKEN ALOUD and the learner will not see it. "
        "Use short sentences and plain word order. Avoid nested clauses, "
        "parenthetical asides and anything that needs re-reading."
        if listening
        else "This text will be READ on screen."
    )

    # Words the learner has already mastered. They are re-encountered here, not
    # re-taught: the backend counts an exposure for each one it finds in the
    # returned text, which is what keeps Active vocabulary in circulation.
    reuse_block = ""
    if reuse_words:
        reuse_list = ", ".join(f'"{w}"' for w in reuse_words)
        reuse_block = f"""
The learner already knows these words: {reuse_list}.
Reuse as many of them as fit naturally — do not force any of them in, do not
explain them, and do not ask questions about them. If one does not belong in
this passage, leave it out.
"""

    # A practice passage: no vocabulary is due, so the learner reads for its own
    # sake and answers comprehension questions (Part 2 §5). Nothing is being
    # tested about any particular word, so nothing is required to appear.
    target_block = (
        f"""The passage must use each of these target words exactly once:
{word_lines}"""
        if words
        else "Use ordinary vocabulary for this level. There are no required words."
    )

    return f"""Write a short passage at CEFR level {level} about {topic}.

{medium}

{target_block}
{reuse_block}

Requirements:
- Split the passage into {sentences} or so sentences, returned
  one per array element, in order.
- Each target word, if any, must appear in a sentence whose neighbours give a
  real clue to its meaning, WITHOUT defining it.
- Fill `glossary` with EVERY word in the passage that carries meaning — nouns,
  verbs, adjectives, adverbs, and any preposition or auxiliary a learner could
  stumble on. Skip nothing a learner might tap.
  * `word`: exactly as it appears in the passage, same spelling and inflection.
  * `meaning_ar`: what it means *in this sentence*, in Arabic. One sense, the
    one you intended — not a list, and not the word's other meanings.
  * `part_of_speech`: its role in this sentence. The same word is a noun in one
    sentence and a verb in another; answer for this one.
- Write exactly {comprehension_count} comprehension questions about the passage.
  They must be answerable from the passage alone, and must NOT be about the
  target words — those are tested separately.
- Each comprehension question needs one correct answer and exactly 3 plausible
  distractors. Distractors must be wrong but not absurd.
- For each target word, report the index (0-based) of the sentence containing it.
"""


# ── Writing evaluation ───────────────────────────────────────────────────────

WRITING_PROMPT_VERSION = "writing-eval-v1"

WRITING_SCHEMA = {
    "type": "object",
    "properties": {
        "used_word": {"type": "boolean"},
        "meaning_correct": {"type": "boolean"},
        "usage_correct": {"type": "boolean"},
        "understandable": {"type": "boolean"},
        "grammar_note": {"type": "string"},
        "feedback": {"type": "string"},
        "suggestion": {"type": "string"},
    },
    "required": [
        "used_word",
        "meaning_correct",
        "usage_correct",
        "understandable",
        "grammar_note",
        "feedback",
    ],
}


def feedback_language_rule(language: str) -> str:
    """How the feedback addressed to the learner must be written.

    Feedback is not the material — it is the app talking to the learner about
    what they just did, so it belongs in the language they read the app in
    (ADR-035). The English being learned is untouched by this: the passage, the
    questions and any quoted sentence stay exactly as they are.
    """
    if (language or "ar").lower().startswith("ar"):
        return (
            "Write `feedback` in Modern Standard Arabic, addressed to the "
            "learner. Quote any English word or sentence in English, inside "
            "the Arabic sentence — do not transliterate it."
        )
    return "Write `feedback` in English, at the learner's level."


def writing_prompt(*, word: str, meaning: str, definition: str,
                   level: str, sentence: str,
                   feedback_language: str = "ar") -> str:
    """Evaluates one learner sentence.

    Note what this prompt does NOT ask for: a pass/fail verdict. The model
    reports observations; the backend applies the rule (rule R2). That is why
    `MVP Core.txt` §32 — a small grammar slip must not fail correct usage — is
    enforced in C#, not here, where a prompt tweak could silently change it.
    """
    return f"""A learner at CEFR level {level} was asked to write one sentence \
using the English word "{word}".

The word means: {definition}
Its Arabic meaning is: {meaning}

The learner wrote:
"{sentence}"

Report your observations:
- used_word: did they use "{word}" (any inflection counts: -s, -ed, -ing)?
- meaning_correct: does their sentence use it with the meaning above, rather \
than a different sense of the same word?
- usage_correct: is it used in a grammatically appropriate position for its \
part of speech?
- understandable: could a reader tell what they meant?
- grammar_note: one of "none", "punctuation", "minor", "unclear".
- feedback: one or two sentences addressed to the learner. Be specific about \
what they did, not generic praise. If something is wrong, say what would fix \
it. {feedback_language_rule(feedback_language)}
- suggestion: an optional more natural rewrite of their sentence. Omit if theirs \
is already natural."""


# ── Speaking conversation ────────────────────────────────────────────────────

SPEAKING_PROMPT_VERSION = "speaking-v2"

SPEAKING_TURN_SCHEMA = {
    "type": "object",
    "properties": {
        "reply": {"type": "string"},
        "words_used_naturally": {"type": "array", "items": {"type": "string"}},
    },
    "required": ["reply", "words_used_naturally"],
}


def speaking_turn_prompt(
    *,
    learner_name: str,
    level: str,
    remaining_words: list[str],
    used_words: list[str],
    transcript: list[dict],
) -> str:
    """One conversational turn.

    A conversation, not a quiz read aloud (demo review §39): the reply reacts to
    what the learner actually said before steering to the next target word.

    The turn is spoken aloud by the app and answered by voice, so it has to work
    as *speech*: short, one question at a time, no lists or punctuation the
    learner cannot hear.
    """
    history = "\n".join(
        f"{'Tutor' if t['from_ai'] else learner_name}: {t['text']}"
        for t in transcript[-6:]
    )
    remaining = ", ".join(f'"{w}"' for w in remaining_words) or "none left"

    # Turn one is a greeting, not an exercise. Opening with "use the word
    # 'research'" makes the whole thing feel like a test being read aloud, which
    # is exactly what the demo review objected to.
    learner_turns = [t for t in transcript if not t["from_ai"]]
    if not transcript:
        return f"""You are a warm, patient English tutor starting a spoken \
conversation with {learner_name}, a learner at CEFR level {level}.

This is the very first thing they will hear, spoken aloud.

Write a short, friendly greeting:
- Greet {learner_name} by name and ask one easy, open question about how they \
are or how their day has been.
- Two sentences at most. Do NOT mention any target word yet, and do NOT set an \
exercise. This is small talk to settle them in.
- Plain spoken English at their level. No lists, no emoji, no markdown."""

    # After a few exchanges with a word still untouched, the tutor may ask for it
    # outright — but only then. Nagging from the first turn is what makes it feel
    # like an exam.
    nudge = ""
    if remaining_words and len(learner_turns) >= 2:
        nudge = (
            f'\n- They have not used "{remaining_words[0]}" yet. Give them one '
            f'more natural opening for it, and if this is the second time you '
            f'have tried, say plainly: "Try to use the word \'{remaining_words[0]}\' '
            f'in your answer."'
        )

    return f"""You are a warm, patient English tutor speaking with {learner_name}, \
a learner at CEFR level {level}. This is a spoken conversation: your reply is \
read aloud and answered out loud.

Conversation so far:
{history}

Target words still to practise: {remaining}
Already used naturally: {", ".join(used_words) or "none yet"}

Write your next turn:
- React to what {learner_name} actually just said. Acknowledge it specifically — \
never a generic "great job".
- Then ask ONE question that gives them a natural reason to use the next target \
word.{nudge}
- Two or three short sentences. Speak at their level, the way a person speaks.
- No lists, no markdown, no emoji, no stage directions — every character is \
spoken aloud.
- Also report which target words they used NATURALLY and CORRECTLY in their last \
message. A word merely mentioned in a fragment does not count."""


# ── Speaking evaluation (end of conversation) ────────────────────────────────

SPEAKING_EVAL_PROMPT_VERSION = "speaking-eval-v1"

SPEAKING_EVAL_SCHEMA = {
    "type": "object",
    "properties": {
        "words": {
            "type": "array",
            "items": {
                "type": "object",
                "properties": {
                    "word": {"type": "string"},
                    "used": {"type": "boolean"},
                    "meaning_correct": {"type": "boolean"},
                    "understandable": {"type": "boolean"},
                    "grammar_acceptable": {"type": "boolean"},
                    "major_grammar_problem": {"type": "boolean"},
                    "evidence": {"type": "string"},
                    "feedback": {"type": "string"},
                },
                "required": [
                    "word", "used", "meaning_correct", "understandable",
                    "grammar_acceptable", "major_grammar_problem",
                    "evidence", "feedback",
                ],
            },
        },
        "summary": {"type": "string"},
    },
    "required": ["words", "summary"],
}


def speaking_eval_prompt(
    *,
    learner_name: str,
    level: str,
    words: list[dict],
    transcript: list[dict],
    feedback_language: str = "ar",
) -> str:
    """Judges the whole conversation once, at the end.

    Deliberately not run per turn: a word the learner fumbles early and uses
    well later should be judged on the conversation as a whole, and re-judging
    after every turn would both cost more and produce a different verdict
    depending on when it happened to run.

    It reports observations only. Whether a word passes is decided in C#
    (rule R2, ADR-015) — this prompt is never asked for a verdict, and there is
    no "passed" field for it to fill in.

    Pronunciation is explicitly out of scope: the transcript comes from speech
    recognition, so a "mispronunciation" here is indistinguishable from a
    recogniser error, and judging it would punish the learner for the
    microphone.
    """
    history = "\n".join(
        f"{'Tutor' if t['from_ai'] else learner_name}: {t['text']}"
        for t in transcript
    )
    word_lines = "\n".join(
        f'- "{w["text"]}" — intended meaning: {w["meaning"]}'
        + (f' ({w["definition"]})' if w.get("definition") else "")
        for w in words
    )

    return f"""Below is a spoken English conversation between a tutor and \
{learner_name}, a learner at CEFR level {level}. The learner's turns were \
transcribed by speech recognition.

Conversation:
{history}

Judge ONLY these target words:
{word_lines}

For each target word report:
- used: did {learner_name} actually use the word themselves? Repeating the \
tutor's question does not count. An inflected form (researched, researching) \
does count.
- meaning_correct: was it used with the intended meaning above, in a way that \
makes sense in context?
- understandable: could a listener follow what they meant?
- grammar_acceptable: was the grammar around the word good enough to be \
understood?
- major_grammar_problem: true ONLY when the grammar is broken enough to \
obscure the meaning.
- evidence: quote the learner's own words containing it, or "" if unused.
- feedback: one short, encouraging sentence for the learner. \
{feedback_language_rule(feedback_language)}

Judge meaning and use, NOT pronunciation and NOT spelling — this is a \
transcript, and transcription errors are not the learner's mistakes.

A small grammar slip with clear, correct use is fine: "I research about AI \
yesterday" is acceptable use of "research" — tense is wrong, meaning is right. \
Using a word for the wrong thing is not: "The database is my phone" uses the \
word but the meaning is wrong.

If a word never appears in {learner_name}'s own turns, report used: false and \
leave the other flags false."""


# ── Placement: the productive skills ─────────────────────────────────────────
#
# Reading and Listening are scored by comparing the learner's answer with the
# key — the correct answer is known, so no model is involved and none should be.
# Speaking and Writing have no fixed answer, and length-and-variety heuristics
# cannot tell a fluent short answer from a padded weak one. That is the whole
# reason this prompt exists.

PLACEMENT_EVAL_PROMPT_VERSION = "placement-eval-v1"

PLACEMENT_EVAL_SCHEMA = {
    "type": "object",
    "properties": {
        "answers": {
            "type": "array",
            "items": {
                "type": "object",
                "properties": {
                    "item_id": {"type": "string"},
                    "estimated_level": {
                        "type": "string",
                        "enum": ["A1", "A2", "B1", "B2", "C1", "C2"],
                    },
                    "score": {"type": "number"},
                    "evidence": {"type": "string"},
                },
                "required": ["item_id", "estimated_level", "score", "evidence"],
            },
        },
        "overall_level": {
            "type": "string",
            "enum": ["A1", "A2", "B1", "B2", "C1", "C2"],
        },
        "summary": {"type": "string"},
    },
    "required": ["answers", "overall_level", "summary"],
}


def placement_eval_prompt(*, skill: str, answers: list[dict]) -> str:
    """Rates a learner's spoken or written placement answers.

    The model estimates; it never decides. The backend turns these numbers into
    a band, applies its own confidence rules and owns the result (rule R2) —
    which is also why `score` is asked for alongside the level: the ability
    estimator works in partial credit, not in labels.
    """
    spoken = skill.upper() == "SPEAKING"

    block = "\n\n".join(
        f"""ITEM {a['item_id']} (written for CEFR {a['level']})
Task: {a['prompt']}
Learner's answer: "{a['answer']}\""""
        for a in answers
    )

    medium = (
        """These answers were SPOKEN and turned into text by speech recognition.
Judge the language, not the transcription: ignore missing punctuation, odd
capitalisation and obvious mis-hearings. Do NOT judge pronunciation — you
cannot hear it, and it is deliberately not measured."""
        if spoken
        else """These answers were WRITTEN by the learner."""
    )

    return f"""You are placing an Arabic-speaking learner of English on the CEFR
scale, from their {skill.lower()} answers.

{medium}

{block}

For each item give:
- estimated_level: the CEFR band this answer demonstrates.
- score: 0.0-1.0, how fully it meets the task *at the band the item was written
  for*. A short but correct and fluent answer scores well; a long, padded or
  off-task one does not. An empty or irrelevant answer scores 0.
- evidence: one short sentence quoting what decided it.

Then give overall_level for {skill.lower()} across all the answers, and a
one-sentence summary addressed to nobody in particular.

Judge only: task achievement, range of vocabulary, grammatical control and
coherence. Be fair to a learner who writes little but writes it correctly, and
do not reward length by itself."""


# ── Re-telling a passage at another level ────────────────────────────────────

RELEVEL_PROMPT_VERSION = "relevel-v1"


def relevel_prompt(
    *,
    text: str,
    from_level: str,
    to_level: str,
    words: list[dict],
    comprehension_count: int,
) -> str:
    """Re-tells one passage at a different CEFR level.

    Deliberately *not* a new passage. A learner who finds the text too hard and
    asks for an easier one has already invested in this story — its topic, its
    people, what they were doing. Handing back a different story throws that
    away and reads as though the app ignored them.

    The questions have to be regenerated regardless: they would otherwise ask
    about sentences that no longer exist.
    """
    word_lines = "\n".join(
        f'- "{w["text"]}" ({w["part_of_speech"]}) — means: {w["definition"]}'
        for w in words
    )
    targets = (
        f"""These target words must still appear, exactly once each:
{word_lines}"""
        if words
        else "There are no required words."
    )

    easier = to_level < from_level

    return f"""Re-tell this passage at CEFR level {to_level}. It is currently
written at {from_level}.

THE PASSAGE:
{text}

Keep the SAME story: the same subject, the same people, the same events, in the
same order. This is the same text at a different level, not a new one on a
similar topic. A learner asked for this because the language was
{"too hard" if easier else "too easy"}, not because they disliked the story.

{targets}

Change only the language:
- {"Shorter sentences, commoner words, fewer clauses." if easier else
   "Richer vocabulary, more varied sentence structure, more precise wording."}
- Keep roughly the same amount of information. Do not summarise it away, and do
  not pad it out.

Return it split into sentences, with {comprehension_count} fresh comprehension
questions about the NEW text — the old questions ask about sentences that no
longer exist — and a full glossary, exactly as for a new passage."""
