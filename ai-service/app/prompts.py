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

READING_PROMPT_VERSION = "reading-v4"

READING_SCHEMA = {
    "type": "object",
    "properties": {
        # What a textbook or a magazine would print above the passage. A page of
        # prose that starts with no title reads as an extract from something
        # else; a learner opening a reading task should be told what they are
        # about to read about.
        "title": {
            "type": "string",
            "description": "A short English title for the passage.",
        },
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
        # Where the paragraphs start, as sentence indexes.
        #
        # Carried beside the sentences rather than as blank elements inside
        # them: the model also reports which sentence holds each target word,
        # by index, and an array with holes in it would shift every one of
        # those by the number of breaks above it.
        "paragraph_breaks": {
            "type": "array",
            "items": {"type": "integer"},
            "description": "0-based indexes of sentences that begin a paragraph.",
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
    "required": ["title", "sentences", "comprehension", "targets", "glossary"],
}


#: What `glossary` must contain, written once and used by every prompt that
#: produces a passage.
#:
#: A tap on a word answers from this and nothing else: a dictionary lookup at tap
#: time returns every sense a word has ever had, and "bank" alone has six, five
#: of them wrong here. So a passage whose glossary covers only its interesting
#: words is a passage where most taps quietly become dictionary lookups — which
#: is what happened to re-told passages until this rule was shared rather than
#: paraphrased.
GLOSSARY_RULE = """- Fill `glossary` with EVERY word in the passage that carries meaning — nouns,
  verbs, adjectives, adverbs, and any preposition or auxiliary a learner could
  stumble on. Skip nothing a learner might tap.
  * `word`: exactly as it appears in the passage, same spelling and inflection.
  * `meaning_ar`: what it means *in this sentence*, in Arabic. One sense, the
    one you intended — not a list, and not the word's other meanings.
  * `part_of_speech`: its role in this sentence. The same word is a noun in one
    sentence and a verb in another; answer for this one."""


#: From this band upwards a passage is expected to be *built*, not merely
#: written: an opening that states what it is about, body paragraphs that each
#: develop one idea, and a close.
#:
#: Below it there is nothing to structure. An A1 passage is eighty words of
#: short declarative sentences, and asking a model to give that a thesis and a
#: conclusion produces a parody of academic writing rather than a beginner's
#: text (ADR-068).
_STRUCTURE_FROM_RANK = 4  # B1

_LADDER = ["A1", "A1_PLUS", "A2", "A2_PLUS", "B1", "B1_PLUS",
           "B2", "B2_PLUS", "C1", "C1_PLUS", "C2"]


def _wants_structure(level: str) -> bool:
    """Whether this band's passage is long enough to have a shape."""
    wire = level.upper().replace("+", "_PLUS")
    return wire in _LADDER and _LADDER.index(wire) >= _STRUCTURE_FROM_RANK


#: What "organised like a real reading text" means, spelled out.
#:
#: The passages read as one undifferentiated block: every sentence connected to
#: the next, no sentence announcing what the paragraph was for, and no ending —
#: the text simply stopped. That is what a model produces when it is asked for
#: N sentences and nothing about their arrangement.
STRUCTURE_RULE = """- STRUCTURE IT LIKE A REAL READING TEXT — the way a passage
  in a TOEFL, IELTS or Cambridge paper is built, not one long undifferentiated
  block:
  * Open with a sentence that says what the whole passage is about. A reader
    should know from the first line what they are about to read.
  * Then two to four paragraphs. Each begins with its own topic sentence naming
    the one idea that paragraph develops, and the sentences after it support
    that idea — an example, a reason, a consequence, a contrast.
  * Close with a sentence that lands the passage: what it amounts to, what
    follows from it, or where it leaves the subject. Do not stop mid-thought.
  * Report where the paragraphs begin in `paragraph_breaks`: the 0-based index
    of each sentence that starts a new paragraph (never 0 — the first sentence
    always starts the first one).
  * Let the joins show — "However", "As a result", "By contrast", "In practice"
    — so a reader can follow the argument rather than infer it."""


#: Above this many words, the passage is written *without* its glossary and the
#: glossary is built separately, in parallel batches.
#:
#: Asking for both at once is cheapest when the passage is short — one call,
#: one round trip. It stops being cheap at length: a C2 passage measured 43
#: seconds and 18,000 tokens when the model wrote its 430 glossary entries
#: inline, and 8 seconds when it did not. The same entries come back from three
#: batch calls issued together in about fifteen. Re-levelling was the first
#: place this showed, because a learner sits and waits for it (ADR-067).
INLINE_GLOSSARY_MAX_WORDS = 260


def reading_schema(*, inline_glossary: bool) -> dict:
    """The answer shape, with or without the glossary."""
    if inline_glossary:
        return READING_SCHEMA

    schema = {
        "type": "object",
        "properties": {
            k: v for k, v in READING_SCHEMA["properties"].items()
            if k != "glossary"
        },
        "required": [k for k in READING_SCHEMA["required"] if k != "glossary"],
    }
    return schema


#: A second pass that fills whatever the first one left out.
#:
#: The rule above asks for every word, and a small model does not always obey
#: it — it glosses the interesting words and skips "was", "before", "quiet",
#: which are exactly the words a beginner taps. Whether it obeys varies with the
#: passage, so the shortfall is not something a better sentence in the prompt
#: can be trusted to fix: it has to be measured and repaired.
#:
#: This asks for a named list rather than the whole glossary again — the words
#: are known, only their meanings are missing, so re-generating the passage's
#: entire vocabulary would cost more and could still come back short.
GLOSSARY_REPAIR_VERSION = "glossary-repair-v1"

GLOSSARY_REPAIR_SCHEMA = {
    "type": "object",
    "properties": {
        "glossary": READING_SCHEMA["properties"]["glossary"],
    },
    "required": ["glossary"],
}


#: The easier bands get a shape too, just a much plainer one.
_SIMPLE_SHAPE_RULE = """- Keep it one connected little text: a first sentence
  that says what it is about, then what happens or what is true, then a last
  sentence that finishes it. No paragraph breaks and no essay structure — at
  this level that would be a parody of writing rather than writing."""


#: What replaces the glossary rule when the glossary is built separately.
_NO_INLINE_GLOSSARY = """- Do NOT return a glossary. The meanings of this
  passage's words are asked for separately, so spend everything you have on the
  passage itself."""


def glossary_repair_prompt(*, sentences: list[str], missing: list[str]) -> str:
    """Asks for the meaning of named words as this passage uses them."""
    numbered = "\n".join(f"{i}. {s}" for i, s in enumerate(sentences))
    wanted = ", ".join(f'"{w}"' for w in missing)

    return f"""Here is a passage a learner is reading:

{numbered}

Give the meaning of each of these words **as this passage uses it**: {wanted}

- One entry per word, and every word above must appear exactly once.
- `word`: copy it exactly as given above, same spelling and inflection.
- `meaning_ar`: what it means *in the sentence it appears in*, in Arabic. One
  sense — the one this passage uses — not a list of the word's meanings.
- `part_of_speech`: its role in that sentence.
- Function words count: "the", "was", "before" and the like each get a plain
  Arabic gloss. A learner tapped it, so it needs an answer.
"""


def _reuse_shape(word: dict) -> str:
    """An already-known word, and the form it was learned in."""
    text = word["text"]

    if word.get("form"):
        return f'"{text}" (the {word["form"]}, exactly)'
    if word.get("may_pluralise"):
        return f'"{text}" (singular or plural)'
    return f'"{text}" (exactly)'


def _target_line(word: dict) -> str:
    """One target word, and the shape the passage must put it in.

    Reading and Listening are the two skills that show a word inside real
    language, so they are the two that have to be told what "this word" means:
    the entry the learner added is `played` the participle or `mouse` the
    singular, and using a neighbouring form tests something else (ADR-047).
    """
    text = word["text"]
    pos = word.get("part_of_speech") or ""
    definition = word.get("definition") or ""
    form = word.get("form")

    if form:
        # An inflected entry: it is the form that was added, so it is the form
        # the passage must use.
        shape = f'use exactly "{text}" — it is the {form} of this verb'
    elif word.get("may_pluralise"):
        # A plain noun whose plural is the word plus s/es. Either is the same
        # word to a learner, and meeting both is worth more than meeting one.
        shape = (
            f'use "{text}" or its plural, whichever the sentence wants '
            f"— both are this word"
        )
    else:
        shape = f'use exactly "{text}"'

    return f'- "{text}" ({pos}) — means: {definition}\n  {shape}'


#: How long a passage should be at each band, in **words**, and how long its
#: sentences should run.
#:
#: Measured against what public examinations actually put in front of a learner
#: rather than guessed (ADR-066):
#:
#: * A2 Key — no reading text exceeds **230 words**.
#: * B1 Preliminary — its texts run from ~150 (the gapped one) to ~300.
#: * B2 First, Part 5 — a single text of **500–600 words**.
#: * C1 Advanced — **3,000–3,500 words** across the whole paper, its long
#:   Part 5 text around 700–800.
#: * C2 Proficiency — longer and denser again than C1.
#:
#: Sentence length moves with it: under 10 words is A1–A2, 10–16 is B1–B2,
#: 16–23 is B2–C1, and over 23 is C1–C2 and academic prose.
#:
#: The app asks for one passage with five comprehension questions, so these sit
#: at the length of a single exam text, not of a whole paper.
_PASSAGE_SHAPE = {
    #            words  typical sentence length   average
    "A1":       (80,    "6 to 9 words",           8),
    "A1_PLUS":  (110,   "7 to 10 words",          9),
    "A2":       (150,   "8 to 12 words",          10),
    "A2_PLUS":  (190,   "9 to 13 words",          11),
    "B1":       (240,   "11 to 15 words",         13),
    "B1_PLUS":  (300,   "12 to 16 words",         14),
    "B2":       (400,   "14 to 20 words",         17),
    "B2_PLUS":  (480,   "15 to 21 words",         18),
    "C1":       (560,   "17 to 24 words",         20),
    "C1_PLUS":  (640,   "18 to 26 words",         22),
    "C2":       (720,   "20 to 30 words",         25),
}


def _passage_shape(
    level: str, word_count: int, *, listening: bool
) -> tuple[int, str, int]:
    """The target length in words, and the sentence length that goes with it.

    A listening clip is heard once with nothing to go back to, so it is
    deliberately shorter than the same content on a page (Part 2 §24) — but its
    sentences keep their band's shape, because what makes listening hard is the
    clause, not the paragraph.

    The floor is set by the words themselves: every target word must appear in
    a sentence whose neighbours give a clue to its meaning, and that costs
    roughly thirty words each however easy the band.
    """
    words, sentence_length, average = _PASSAGE_SHAPE.get(
        level.upper().replace("+", "_PLUS"), _PASSAGE_SHAPE["B1"])

    if listening:
        words = int(words * 0.6)

    words = max(words, 40 + word_count * 30)

    # A sentence count as well as a word count, because a word count alone is
    # not something a model can hold itself to: asked for 720 words at C2 it
    # returned 361, and asked for 29 sentences it returns something close to
    # 29. Counting is the instruction it can actually follow (ADR-066).
    return words, sentence_length, max(4, round(words / average))


def wants_inline_glossary(
    level: str, word_count: int, *, listening: bool
) -> bool:
    """Whether this passage is short enough to be glossed in the same call."""
    length, _, _ = _passage_shape(level, word_count, listening=listening)
    return length <= INLINE_GLOSSARY_MAX_WORDS


def reading_prompt(
    *,
    level: str,
    interests: list[str],
    words: list[dict],
    listening: bool,
    comprehension_count: int,
    reuse_words: list[str] | None = None,
    inline_glossary: bool = True,
) -> str:
    """Builds the passage prompt for Reading or Listening.

    The two share a generator because they share a learning rhythm, but the
    instructions differ where it matters: a listening script has to work when
    heard once, so it avoids the long subordinate clauses a reader can re-read.
    """
    topic = ", ".join(interests[:3]) if interests else "everyday student life"
    length, sentence_length, sentence_count = _passage_shape(
        level, len(words), listening=listening)
    word_lines = "\n".join(_target_line(w) for w in words)

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
        # Same rule as the target words: a form the learner learned is the form
        # they should meet again (ADR-047). A caller with nothing more to say
        # passes plain strings; one that knows the shape passes objects.
        reuse_list = ", ".join(
            f'"{w}"' if isinstance(w, str) else _reuse_shape(w)
            for w in reuse_words
        )
        reuse_block = f"""
The learner already knows these words: {reuse_list}.
Reuse as many of them as fit naturally — do not force any of them in, do not
explain them, and do not ask questions about them. If one does not belong in
this passage, leave it out. Where a shape is given, keep it: these are the forms
they learned.
"""

    # A practice passage: no vocabulary is due, so the learner reads for its own
    # sake and answers comprehension questions (Part 2 §5). Nothing is being
    # tested about any particular word, so nothing is required to appear.
    target_block = (
        f"""The passage must use each of these target words exactly once, in the
shape given after it:
{word_lines}

Use no other form of these words. A learner practising "played" as a past
participle is learning "I have played"; a passage that writes "play" or
"playing" instead has tested something they did not ask for, and a passage that
writes "mice" where "mouse" was given has used a word they have not learned."""
        if words
        else "Use ordinary vocabulary for this level. There are no required words."
    )

    # The learner's interests, kept in their place.
    #
    # They used to open the prompt — "Write a passage about technology" — and
    # the model obeyed the topic before it obeyed anything else, bending the
    # target words to fit it. That produces sentences nobody would write: a
    # tin "can" appearing in a paragraph about programming teaches a use of the
    # word that does not exist, which is worse than not meeting the word at
    # all. The interests are a setting, not the subject, and they yield.
    topic_block = f"""Setting (lowest priority): something around {topic}, IF that
setting lets every target word appear the way it is genuinely used. If it does
not, write about something else entirely — an ordinary scene, a short piece of
non-fiction, a small story. A passage on the wrong subject is fine; a passage
that misuses a word is not.

Never name the learner's interests and never explain your choice of subject.
No "Since you are interested in technology…", no "As a football fan…". The
learner set those interests to make the reading pleasant, not to be reminded
of them."""

    return f"""Write a passage in English at CEFR level {level}.

{medium}

{target_block}
{reuse_block}
{topic_block}

NATURAL USE COMES FIRST — before the level, before the length, before the
subject. Every target word must sit in the sentence a fluent writer would
actually have put it in: its ordinary collocations, its ordinary subject and
object, the register it really belongs to. A sentence invented to squeeze a
word into a topic is the one thing this passage must never contain.

Requirements:
- Give the passage a title of 3 to 8 words: what a magazine or a textbook would
  print above it. Not a summary, not a question, and it must not name any
  target word.
- Write **{sentence_count} sentences**, returned one per array element, in
  order. Count them. This is the length that matters most after natural use: a
  passage half this long is not the reading practice this level calls for, and
  a short one is the single most common way this task is got wrong.
- Each sentence about {sentence_length}, so the passage comes to roughly
  {length} words in total.
- Each target word, if any, must appear in a sentence whose neighbours give a
  real clue to its meaning, WITHOUT defining it.
{STRUCTURE_RULE if _wants_structure(level) else _SIMPLE_SHAPE_RULE}
{GLOSSARY_RULE if inline_glossary else _NO_INLINE_GLOSSARY}
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
- suggestion: **their own sentence, written the way a {level} writer would \
write it.** Same idea, same content, same intent — expressed at {level}. This is \
not a grammar correction and not a better sentence: a learner at {level} is \
shown what their own thought sounds like at the level they are working at, so \
raise or simplify the vocabulary and structure to match {level} even when their \
grammar is already correct. Keep the word "{word}" in it. Omit only if their \
sentence is already exactly how a {level} writer would put it."""


# ── Speaking conversation ────────────────────────────────────────────────────

SPEAKING_PROMPT_VERSION = "speaking-v6"

SPEAKING_TURN_SCHEMA = {
    "type": "object",
    "properties": {
        "reply": {"type": "string"},
        "words_only_named": {"type": "array", "items": {"type": "string"}},
    },
    "required": ["reply", "words_only_named"],
}


#: What speaking at a level actually means, band by band.
#:
#: "Speak at their level" is not an instruction a model can follow: asked to
#: talk to an A1 learner it still produced "run the same lines of code until a
#: condition is met", which is B2 vocabulary in a short sentence. A learner who
#: changes the level because the tutor is too hard has to hear the difference,
#: so the difference is spelled out.
_REGISTER = {
    "A1": "Very short sentences, five to eight words. The commonest words only. "
          "One clause — no 'until', 'although', 'which'. Present simple.",
    "A2": "Short sentences. Everyday words. At most one simple linking word "
          "('and', 'but', 'because'). Present and past simple.",
    "B1": "Ordinary conversational English. Common words, one subordinate "
          "clause at most.",
    "B2": "Natural adult conversation. Some less common vocabulary, longer "
          "sentences where they carry meaning.",
    "C1": "Precise, varied vocabulary. Complex sentences where they are the "
          "clearest way to say it.",
    "C2": "Fully idiomatic. Nuance, abstraction and shading as with a peer.",
}


def register_for(level: str) -> str:
    """The register rule for a band, ignoring the `+` half-steps."""
    return _REGISTER.get((level or "B1").upper().replace("_PLUS", "").replace("+", ""),
                         _REGISTER["B1"])


def _speaking_shape_line(shape: dict) -> str:
    """What the tutor must know about one remaining word.

    Its **sense** above all (ADR-069). The tutor used to be handed bare
    strings: shown "can", it had no way to tell the modal from the tin from
    preserving fruit, so it asked about whatever the learner's interests
    suggested and the word could not honestly answer. The learner added one
    specific meaning, and that meaning is the whole exercise.

    Then its form (ADR-047) and the near misses (ADR-050).
    """
    word = shape.get("text", "")
    form = shape.get("form")
    pos = shape.get("part_of_speech")
    definition = (shape.get("definition") or "").strip()

    sense = f'"{word}"'
    if pos:
        sense += f" ({pos})"
    if definition:
        sense += f" — means: {definition}"

    if form:
        return (
            f"{sense}. This is the {form}, and the learner is practising THIS "
            f"form, so ask a question whose natural answer needs it. Another "
            f"form of the same verb is a different word to them and does not "
            f"count."
        )
    if shape.get("may_pluralise"):
        return f"{sense}. Singular or plural is the same word; both count."
    return f"{sense}. Use it as it is."


def speaking_turn_prompt(
    *,
    learner_name: str,
    level: str,
    remaining_words: list[str],
    used_words: list[str],
    transcript: list[dict],
    interests: list[str] | None = None,
    remaining_shapes: list[dict] | None = None,
    form_reminders: list[dict] | None = None,
    unused_words: list[str] | None = None,
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
    # What they like, for choosing between the scenes a word could live in —
    # never a reason to force a word somewhere it does not belong.
    likes = ", ".join(interests or []) or "not stated"

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
- Plain spoken English. **At {level}:** {register_for(level)}
- No lists, no emoji, no markdown."""

    if not remaining_words:
        # Two ways a conversation ends: every word done, or it has run long
        # enough. Both get a closing turn — the second used to end mid-question
        # (ADR-070) — but only the first may be congratulated on finishing.
        missed = [w for w in (unused_words or []) if w]
        outcome = (
            f'They practised: {", ".join(used_words) or "none"}. '
            f'The conversation is ending before they got to: '
            f'{", ".join(missed)}.'
            if missed
            else f'They have now practised every word for today: '
                 f'{", ".join(used_words) or "none"}.'
        )
        missed_rule = (
            "\n- Do NOT list what they missed, do NOT apologise for it, and do "
            "NOT tell them they got anything wrong. They will read about that "
            "afterwards, in detail and in their own language. This is a "
            "goodbye: warm, short, and about what they did do."
            if missed
            else ""
        )

        return f"""You are a warm, patient English tutor speaking with \
{learner_name}, a learner at CEFR level {level}. This is a spoken conversation: \
your reply is read aloud.

Conversation so far:
{history}

{outcome}

Write the closing turn. A conversation that simply stops is the one thing this \
must never be: it opened with a greeting and it closes properly, whatever \
happened in between.
- React to what {learner_name} just said, specifically.
- Say something true about how they did — name a word they handled well, or \
something they expressed clearly.
- Close warmly, and make it sound like an ending: thank them, or say you \
enjoyed talking, or that you will pick it up next time.
- Two or three short sentences.
- Do NOT ask another question. Do NOT ask for a word, and do NOT invent one.{missed_rule}
- No lists, no markdown, no emoji — every character is spoken aloud.
- Report `words_only_named` by the same rule as always: usually empty."""

    # After a few exchanges with a word still untouched, the tutor may ask for it
    # outright — but only then, and only once the conversation is somewhere the
    # word belongs. Nagging from the first turn is what makes it feel like an
    # exam; asking for it in the wrong place makes it impossible to answer.
    # The shape of each word still to practise, and the near misses. Both are
    # facts the tutor cannot read off a list of bare words.
    shapes = "\n".join(
        f"- {_speaking_shape_line(sh)}" for sh in (remaining_shapes or [])
    )
    shapes_block = f"\n\nWhat those words are:\n{shapes}" if shapes else ""

    repair = ""
    if form_reminders:
        first = form_reminders[0]
        repair = (
            f'\n- {learner_name} just said "{first["said"]}" where the word '
            f'they are practising is "{first["word"]}" — the '
            f'{first["form"]}. They reached for it and missed by one step, so '
            f"say which step, warmly and in one short sentence: name the form "
            f'and the word, as in "Almost — I need the {first["form"]}: '
            f'\'{first["word"]}\'. Can you say that again?" Then let them try '
            f"the same idea again. Do NOT move to another word, and do NOT "
            f"treat this as a mistake to be corrected at length — they nearly "
            f"had it."
        )

    nudge = ""
    if remaining_words and len(learner_turns) >= 2:
        nudge = (
            f'\n- They have not used "{remaining_words[0]}" yet. Move the '
            f'conversation to a situation where it belongs and ask about that. '
            f'Only if the conversation is ALREADY somewhere the word fits, and '
            f'only if this is your second attempt, may you ask outright: '
            f'"Try to use the word \'{remaining_words[0]}\' in your answer." '
            f'Never attach that sentence to a question the word does not fit.'
        )

    return f"""You are a warm, patient English tutor speaking with {learner_name}, \
a learner at CEFR level {level}. This is a spoken conversation: your reply is \
read aloud and answered out loud.

Conversation so far:
{history}

Target words still to practise: {remaining}{shapes_block}
Already used naturally: {", ".join(used_words) or "none yet"}

{learner_name} is interested in: {likes}. That is the LOWEST priority here. It
decides between two situations that would both work for a word; it is never a
reason to bring a word into a conversation it has nothing to do with, and you
must never say it back to them ("since you like technology…").

NATURAL USE COMES FIRST — before the level, before their interests, before \
anything else here. A target word must be asked for in a situation where a \
person would really say it, in the sense the learner is practising. A question \
built to fit a word into a topic is a question with no honest answer, and it \
teaches a use of the word that does not exist.

Write your next turn:
- React to what {learner_name} actually just said. Acknowledge it specifically — \
never a generic "great job".
- Then ask ONE question whose natural answer contains the next target word —
  a question the learner can actually ANSWER, in that word's real sense.

  Read the word's meaning above before you write the question. The learner
  added one specific sense of it; a question that suits a different sense of
  the same spelling is a question they cannot answer. "can" as a tin belongs
  with food and shopping, not with what a computer can do.

  Work backwards from the word: think of a real situation where a person would \
say it, and ask about that situation. Do NOT ask an unrelated question and then \
tell them to fit the word in. "What do you want to be when you finish studying? \
Try to use 'hook' in your answer." is exactly the failure — the question is \
about careers, the word is a piece of metal, and there is no honest answer.

  If the word does not fit what you are talking about, CHANGE THE SUBJECT. A \
conversation is allowed to move: "That reminds me — do you ever go fishing?" is \
a tutor doing their job. Steering to where a word lives is the skill; forcing it \
where it does not is not.

  Their interests are for choosing BETWEEN the situations a word could live in, \
when more than one would work. They are never a reason to bolt a word onto a \
topic it has nothing to do with.

  Then say plainly which word to use, as the last thing in your turn: \
Try to use the word "…" in your answer. Say it every time, even when the \
question makes it obvious — a learner cannot see the list on the screen the way \
you can, and guessing which word is wanted is not the exercise.

  That sentence is the ONLY place a word is named. It goes at the end of a \
question the word already fits, never as a repair for a question it does not.

  When there are no target words left, do not name one and do not ask for one. \
Close warmly instead: say what they did well and that you enjoyed talking. \
Asking for a word that is already done is the clearest possible sign that \
nobody is listening.

  And {learner_name} is never obliged to use it. If they answer without the \
word, that is a good answer: take what they said seriously, and find the word \
another opening later.{repair}{nudge}
- Two or three short sentences, the way a person speaks.
- **Speak at {level}:** {register_for(level)} This is what the learner chose,
  and it is the whole reason the level can be changed mid-conversation: if they
  drop it because you were too hard, the very next thing you say has to be
  easier.
- No lists, no markdown, no emoji, no stage directions — every character is \
spoken aloud.
- Also report `words_only_named`: any target word that appears in \
{learner_name}'s last message where they are talking *about* the word rather \
than using it — "let me use 'hook' in a sentence", or echoing your question back \
word for word. **This list is usually empty.**

  Whether a word was used is decided from their words, not from this list; you \
are being asked only about the one case a reader of the text cannot settle. So \
do NOT list a word merely because it was used badly: "I want to become a \
software engineering" is an attempt, and attempts count. How well they used it \
is judged once, at the end, by someone else."""


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
                    "better": {"type": "string"},
                },
                # `better` is required: a learner told what was wrong and not
                # shown what right looks like has been marked, not taught
                # (ADR-048). Structured output guarantees the field; the prompt
                # says what to put in it when there is nothing to repair.
                "required": [
                    "word", "used", "meaning_correct", "understandable",
                    "grammar_acceptable", "major_grammar_problem",
                    "evidence", "feedback", "better",
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
- feedback: what {learner_name} should take away about THIS word. Two or three \
sentences, and never generic — a learner who reads it should know what they did \
and what to do next time (ADR-048).

  * **Used it well:** say so and quote where. Then, at {level}, show the one \
thing that would make it better — a more natural phrasing, a preposition, a \
tense. If it was genuinely fine as it stands, say that instead of inventing a \
correction.
  * **Used it wrongly:** say plainly what was wrong and *why*, then give them a \
correct sentence with the word in it, built from what they were actually talking \
about. Add that the word comes back another day, so nothing is lost — a learner \
who fails a word and is not told why learns only that they failed.
  * **Never said it:** say so without blame, give one sentence showing it in \
use, and note it will come round again.

  {feedback_language_rule(feedback_language)}
- better: one English sentence using the word correctly — their own repaired \
where it can be, otherwise a short model sentence built from what they were \
talking about. Always give one: a learner told what was wrong and not shown \
what right looks like has been marked, not taught.

Judge meaning and use, NOT pronunciation and NOT spelling — this is a \
transcript, and transcription errors are not the learner's mistakes.

A small grammar slip with clear, correct use is fine: "I research about AI \
yesterday" is acceptable use of "research" — tense is wrong, meaning is right. \
Using a word for the wrong thing is not: "The database is my phone" uses the \
word but the meaning is wrong.

- summary: two or three sentences to {learner_name} about the conversation as a \
whole — what went well, and the one thing to work on next time.

**Everything the learner reads is written TO them, not about them.** "You used \
`went` well" — never "the learner used" or "{learner_name} used". This is the \
message they open after a conversation, and a report written in the third \
person reads like a file somebody keeps on them (ADR-059).

  {feedback_language_rule(feedback_language)}

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

RELEVEL_PROMPT_VERSION = "relevel-v2"


def relevel_prompt(
    *,
    text: str,
    from_level: str,
    to_level: str,
    words: list[dict],
    comprehension_count: int,
    inline_glossary: bool = True,
) -> str:
    """Re-tells one passage at a different CEFR level.

    Deliberately *not* a new passage. A learner who finds the text too hard and
    asks for an easier one has already invested in this story — its topic, its
    people, what they were doing. Handing back a different story throws that
    away and reads as though the app ignored them.

    The questions have to be regenerated regardless: they would otherwise ask
    about sentences that no longer exist.
    """
    word_lines = "\n".join(_target_line(w) for w in words)
    targets = (
        f"""These target words must still appear, exactly once each:
{word_lines}"""
        if words
        else "There are no required words."
    )

    easier = to_level < from_level
    length, sentence_length, sentence_count = _passage_shape(
        to_level, len(words), listening=False)

    return f"""Re-tell this passage at CEFR level {to_level}. It is currently
written at {from_level}.

THE PASSAGE:
{text}

Keep the SAME story: the same subject, the same people, the same events, in the
same order. This is the same text at a different level, not a new one on a
similar topic. A learner asked for this because the language was
{"too hard" if easier else "too easy"}, not because they disliked the story.

{targets}

KEEP THE SHAPE. If the passage has paragraphs, it still has them; if it opens
by saying what it is about and closes by landing it, so does the re-telling.
Report the paragraph starts again in `paragraph_breaks`.

NATURAL USE STILL COMES FIRST. Each target word must sit where a fluent writer
would really have put it. Re-telling at another level is no licence to bend a
word into a sentence nobody would write.

Change only the language:
- {"Shorter sentences, commoner words, fewer clauses." if easier else
   "Richer vocabulary, more varied sentence structure, more precise wording."}
- **{sentence_count} sentences**, each about {sentence_length}, coming to
  roughly {length} words. Count them.
  {"Say the same things more plainly" if easier else
   "Say the same things with more detail and precision"} — the story does not
  change, only how fully it is told. Invent no new events, and drop none.

Also give the passage a title of 3 to 8 words. It is the same story, so it may
be the same title said differently; it must not name any target word.

Return it split into sentences, with {comprehension_count} fresh comprehension
questions about the NEW text — the old questions ask about sentences that no
longer exist{" — and a glossary of the new passage" if inline_glossary else ""}:

{GLOSSARY_RULE if inline_glossary else _NO_INLINE_GLOSSARY}

{"The glossary is not optional and not a summary: the learner taps words in this text to see what they mean here, and a word missing from it falls through to a dictionary, which answers about every sense the word has ever had instead of this one." if inline_glossary else "The learner is waiting for this. Write the passage and nothing else."}"""


MEANING_CHECK_PROMPT_VERSION = "meaning-check-v1"

MEANING_CHECK_SCHEMA = {
    "type": "object",
    "properties": {
        # Does the Arabic the learner typed actually mean what the English word
        # means? The only question that matters; everything else explains it.
        "matches": {"type": "boolean"},
        # The learner's own wording, spelled correctly — null when it already
        # was. Distinct from `suggestions`: this is *their* meaning fixed, not a
        # different meaning offered.
        "corrected": {"type": "string"},
        # Arabic meanings that would be right, when theirs is not.
        "suggestions": {
            "type": "array",
            "items": {"type": "string"},
        },
        # One sentence to the learner, in the language they read the app in.
        "note": {"type": "string"},
    },
    "required": ["matches", "note"],
}


def meaning_check_prompt(*, word: str, definitions: list[str],
                         part_of_speech: str, meaning: str,
                         feedback_language: str = "ar") -> str:
    """Checks an Arabic meaning a learner typed for an English word (ADR-074).

    Note what this does NOT do: decide whether the word may be added. It
    reports; the backend applies the rule, and the learner may overrule it
    (rule R2). That matters more here than anywhere else in this service,
    because this whole feature exists *because* an automated source of meanings
    was wrong often enough to be unusable — replacing a bad dictionary with a
    confident model that is also sometimes wrong, and giving the learner no way
    past it, would be the same mistake wearing a different hat.

    So the prompt is deliberately generous. A meaning is `matches: true` when it
    is one of the word's real senses, in any register — dialect, a short gloss,
    a longer paraphrase. It is false only when the Arabic names something the
    word does not mean.

    `definitions` is every sense the lexicon holds, and it has to be. Passing
    only the commonest one rejected `book = يحجز` — which is correct, and is
    exactly the kind of meaning this whole feature exists to let a learner
    write.
    """
    # `part_of_speech` is deliberately not printed. It names the *commonest*
    # sense's part of speech, and stating it above a list that already carries
    # one per sense told the model "book is a noun" while the list said book is
    # also a verb — and the model believed the headline. The list is the truth.
    senses = "\n".join(f"  - {d}" for d in definitions if d) or "  - (unknown)"
    return f"""A learner is adding the English word "{word}" to their \
vocabulary, and has written what they think it means in Arabic.

The word: {word}
Everything it can mean in English:
{senses}

The learner wrote this Arabic meaning:
"{meaning}"

Judge it:

- matches: true if the Arabic names one of this word's real meanings. Be \
generous. Accept a dialect word, a one-word gloss, a longer paraphrase, a \
different but valid synonym, and **any** of the senses listed above — not only \
the first. The learner is allowed to mean any of them, and is also allowed to \
mean a real sense the list happens to omit. Set false ONLY when the Arabic \
names something "{word}" does not mean at all.

- corrected: if the Arabic has a spelling mistake but is clearly aiming at a \
correct meaning, give their wording with the spelling fixed and nothing else \
changed. Otherwise omit it. Never use this to reword a meaning you simply \
would have phrased differently.
  If you mention a spelling problem in `note`, you MUST also fill `corrected` \
with the fixed wording. A note saying the spelling is wrong, with no \
correction beside it, tells the learner they made a mistake and leaves them to \
guess what it was.

- suggestions: when matches is false, up to three correct Arabic meanings of \
"{word}", commonest first, each a short gloss rather than a sentence. Omit \
when matches is true.

- note: one short sentence addressed to the learner. If it is right, say so \
plainly. If the spelling needs fixing, say which word. If the meaning is \
wrong, say what "{word}" actually means. Do not scold, and do not hedge. \
{feedback_language_rule(feedback_language)}"""
