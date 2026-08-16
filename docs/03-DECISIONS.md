# Architecture Decision Records

Short, dated records of every judgement call — especially where the source documents were
silent or contradicted each other. **Do not change a decision silently; append a new ADR.**

---

## ADR-001 — Skill order is configuration, default `Reading → Listening → Speaking → Writing → Spelling`
**Date:** 2026-08-12 · **Status:** Accepted

**Context.** The documents disagree on the order of skills 3 and 4:

| Document | Stated order |
|---|---|
| `Project Purpose.txt` §5, §9 | Reading → Listening → **Speaking → Writing** → Spelling |
| `MVP Core.txt` §12, §15, §68 | Reading → Listening → **Speaking → Writing** → Spelling |
| `Word Life Cycle.txt` §7, §33 | Reading → Listening → **Speaking → Writing** → Spelling |
| `Core Components.txt` §8 | Reading → Listening → **Writing → Speaking** → Spelling |
| `User Flow.txt` §2, §54, §59 | Reading → Listening → **Writing → Speaking** → Spelling |

**Decision.** The order is a configuration value (`skills_order`), not a constant in code —
which the documents themselves demand (`User Flow.txt` §59 lists "Skills Order" as a
configurable MVP value). The seeded default follows the 3-document majority *and* the
pedagogical argument that production in speech precedes considered written production:
`Reading → Listening → Speaking → Writing → Spelling`.

**Consequence.** Changing the order is a config change, not a code change. Flag for the product
owner to confirm the default; nothing breaks either way.

---

## ADR-002 — Build the Flutter client first against a contract-accurate mock
**Date:** 2026-08-12 · **Status:** Accepted

**Context.** The user asked to start with Flutter. The backend (C#) and AI service (Python) are
later phases, and no .NET SDK is installed on this machine.

**Decision.** Define the REST contract up front (`05-API-CONTRACT.md`), code the client against
a `WordOsApi` interface, and provide two implementations: `HttpWordOsApi` (Dio, the real thing)
and `MockWordOsApi` (in-memory). The mock's rule simulation lives **only** in
`mobile/lib/mock_backend/`, is marked as disposable, and is deleted in Phase 7.

**Consequence.** Rule R1 ("no business logic in Flutter") is preserved in production code: the
feature layer only ever consumes `WordOsApi`. Switching to the real backend is a one-line
provider override.

---

## ADR-003 — Riverpod + go_router, no code generation
**Date:** 2026-08-12 · **Status:** Accepted

State management: `flutter_riverpod` (compile-safe DI, easy provider overrides — which is
exactly how mock↔real swapping works). Routing: `go_router` with redirect guards for
auth/onboarding. No build_runner codegen in the MVP: it slows iteration and buys little at this
size. Models are hand-written with explicit `fromJson`/`toJson` mirroring the C# DTOs.

---

## ADR-004 — Bilingual UI (English + Arabic) with full RTL
**Date:** 2026-08-12 · **Status:** Accepted

The learner audience is Arabic-speaking (word meanings are Arabic throughout the documents),
but the study content is English. The interface ships localized `en` + `ar` with proper RTL,
selectable in Settings and defaulting to the device locale. Learning content (passages,
questions, target words) always stays English; meanings/translations stay Arabic.

---

## ADR-005 — No runtime-downloaded fonts
**Date:** 2026-08-12 · **Status:** Accepted

`google_fonts` fetches fonts over the network at first paint, which harms cold start and fails
offline. We use the platform type stack with a tuned Material 3 text theme. If a brand typeface
is chosen later it will be bundled as an asset, not downloaded.

---

## ADR-006 — Exposure Count is priority-only
**Date:** 2026-08-12 · **Status:** Accepted

`Word Life Cycle.txt` §26 states there is no exposure limit in this MVP, while `MVP Core.txt`
part 2 §39 mentions per-level exposure limits. Resolution: exposure **never** removes a word
from Active and never triggers archiving on its own. A per-CEFR-level `exposure_soft_cap`
config value exists but only *dampens priority* when sending candidates to the AI. Archiving
remains governed by System-Validated Level (R6).

---

## ADR-020 — The Speaking conversation is hands-free, and never scored on pronunciation
**Date:** 2026-08-16 · **Status:** Accepted

**Context.** Speaking was typed. The product owner asked for a real spoken
conversation: the tutor talks, the learner answers out loud, and neither side
presses anything. Gemini Live is explicitly out of scope, and so is
pronunciation scoring.

**Decision.** The loop is built from the two platform services the app already
had a use for: on-device TTS speaks the tutor's turn, and on-device speech
recognition transcribes the learner's. Each step waits for the previous one to
*finish* — `speakToCompletion` resolves on the engine's completion callback, and
the microphone opens only then. Silence ends the learner's turn
(`pauseFor`), so nothing is tapped.

Pronunciation is not assessed anywhere. What reaches the backend is a
recogniser's best guess, so a "mispronunciation" cannot be told apart from a
recognition error — scoring it would punish the learner for their microphone and
their accent.

**Consequence.** A device that cannot listen — no permission, no recogniser, or
the iOS Simulator — is not an error state: the panel offers typing, and every
other part of the session is unchanged. An open microphone during playback would
record the tutor's own voice and send it back as the learner's answer, so the
phases are one enum rather than two booleans, and a widget test asserts the two
never overlap.

---

## ADR-019 — Speaking is judged once, on the whole conversation
**Date:** 2026-08-16 · **Status:** Accepted

**Context.** Speaking previously passed a word on a crude test: the word
appearing in a turn of at least five words (ADR-016). That cannot tell "I did
some research yesterday" from "The research is my telephone".

**Decision.** At the end of the session, the whole transcript and the target
words go to `/ai/speaking/evaluate`, which returns per-word **observations** —
used, meaning correct, understandable, grammar acceptable, major grammar
problem. `SpeakingRules.Passed` in C# decides:

```
used && meaningCorrect && understandable && !majorGrammarProblem
```

Ordinary grammar mistakes are recorded and ignored (`MVP Core.txt` §32): "I
research about AI yesterday" passes. A major problem — grammar broken enough to
obscure the meaning — fails, because at that point nobody can tell whether the
word was used correctly.

**Why once, at the end.** A learner who fumbles a word early and uses it well
later should be judged on the exchange as a whole; and evaluating after every
turn would multiply a session's cost by however much the learner says.

**Consequence.** The verdict cannot drift with a prompt edit — there is no
`passed` field in the response for one to fill in — and the rule is testable
without a model. When the evaluation is unavailable, the old heuristic still
applies rather than failing the learner for an outage. Verified against real
Gemini: correct-use-wrong-tense passes, wrong-meaning fails, never-used fails.

---

## ADR-018 — Exposure is an event the server derives, not a number anyone reports
**Date:** 2026-08-15 · **Status:** Accepted

**Context.** `Word Life Cycle.txt` §24–26 has Active words reused by the AI, with
exposure prioritising which ones come back. Exposure also gates archiving
(ADR-013), so an inflated count retires a word the learner barely met — and the
obvious implementations all inflate it. Asking the model which words it used
trusts a generator to grade itself; counting occurrences double-counts a word
repeated three times in one passage; counting per turn double-counts a
conversation.

**Decision.** Active words are *offered* to the generator (least-exposed first),
and the server then reads the content it received and decides what was actually
used — `ActiveWordReuseDetector`, a pure function matching on word boundaries
with common inflections but not derivations (`researched` counts, `researcher`
does not). Each credit writes a `word_exposures` row naming its source and the
session or review that caused it, under a unique index on
`(word_id, source, source_id)`.

**Consequence.** One exposure per word per generating event, enforced by the
database rather than by remembering to check — a repeated mention, a re-read
session, a retried request and a requeued review answer all collapse to one. A
*new* session counts again, which is the signal working. No endpoint accepts an
exposure count in any form, and the weekly review is no longer the only source.

---

## ADR-017 — Spelling content difficulty follows the Reading level
**Date:** 2026-08-15 · **Status:** Accepted

**Context.** Spelling carries no CEFR level of its own (ADR-008), yet
`MVP Core.txt` §33–34 makes the clue depend on level: B2 and above get an English
definition and type freely; below that, the Arabic meaning and letter tiles. A
skill with no level cannot answer the question the rule asks.

**Decision.** Spelling borrows the learner's **Reading** level for content
difficulty only. Whether an English definition is a usable clue is a
reading-comprehension question, so Reading is the honest proxy. Placement may
still soften the input mode (`SpellingSupportMode`) but never harden it.

**Consequence.** No CEFR level is ever stored against Spelling; nothing about
Spelling performance moves any level. `PATCH /settings/skill-level` still returns
`SKILL_NOT_LEVELLED` for Spelling. If the borrowed level later proves a poor fit,
this ADR is the single place the mapping is defined.

---

## ADR-016 — A speaking word passes on substantial use, not on mention
**Date:** 2026-08-15 · **Status:** Accepted

**Context.** Speaking is a conversation, not a queue of questions, so the
first-attempt rule that governs the other four skills has nothing to attach to.
Something still has to decide whether a word passed.

**Decision.** A word passes Speaking when the learner used it in a turn of at
least five words. Repeating the word alone, or echoing the prompt, does not pass
it. The judgement is made in C# from the stored transcript — the model is asked
to converse, never to award a pass (R2).

**Consequence.** The threshold is deliberately crude and deliberately visible: it
is one constant in `SessionEndpoints.SpeakingPassed`, easy to re-tune once real
transcripts exist. A learner who says nothing substantial simply retries in two
days, losing none of the four other skills (R5). Revisit once the MVP has enough
transcripts to measure how often it is wrong in each direction.

---

## ADR-015 — The AI observes; the backend decides pass/fail
**Date:** 2026-08-15 · **Status:** Accepted

**Context.** `MVP Core.txt` §32 says a small grammar mistake must not fail a
sentence that uses the word correctly. The tempting implementation is to ask
Gemini for a verdict and store it.

**Decision.** `WritingObservation` has **no** `passed` field, by construction.
The model reports what it saw — `usedWord`, `meaningCorrect`, `usageCorrect`,
`understandable`, `grammarNote` — and one line of C# decides:
`passed = usedWord && understandable`. The same holds for content: output that
would leave the learner with nothing to answer is rejected rather than shown.

**Consequence.** Editing a prompt can never silently change what passing means,
and the rule is testable without invoking a model (a test asserts that a reported
grammar slip still passes). It also means an AI outage degrades content quality
without touching a single learner's progress: fallbacks are marked
`usedAiFallback: true` so a quality dip is attributable to the outage rather than
read as learners getting worse (`MVP Core.txt` §62).

---

## ADR-014 — Gemini via the Python service, pinned to flash-lite
**Date:** 2026-08-15 · **Status:** Accepted

**Provider.** Google Gemini, reached only from the Python AI service. The key
lives in `ai-service/.env` (mode 600, gitignored) and exists nowhere else — not
in Flutter, where a decompiled binary would surrender it; not in C#, which talks
to the Python service rather than to Gemini.

**Model: `gemini-3.1-flash-lite`.** The product owner asked for
`gemini-2.5-flash`. Google has closed the entire 2.5 family to new accounts —
every variant tested returns `404 no longer available to new users`, including
`2.5-flash-lite` and dated builds. `3.1-flash-lite` is the same cheapest tier
and was the fastest of six models tested with real calls (1140 ms).

> Worth recording: `GET /models` **lists** `gemini-2.5-flash`, which then fails
> at `generateContent`. A model appearing in the listing is not evidence it can
> be called. Probe with a real request before pinning.

**Not a `latest` alias**, though `gemini-flash-latest` was marginally faster:
`latest` moves under you, so generated content and evaluation results would
change without a code change. Acceptable in an experiment, not in a system that
measures learners' levels.

**A cost guard refuses to start** on any model matching `-pro`, `ultra`,
`deep-research` or `-max`, overridable only by an explicit
`GEMINI_ALLOW_EXPENSIVE=true`. The budget is the binding constraint, and a
stray edit should not be able to start billing at several times the rate.

**Consequence.** Swapping provider means rewriting `ai-service/app/gemini.py`
alone; prompts, schemas and every WordOS rule are untouched.

---

## ADR-013 — The proven level is the *weakest* skill, and archiving needs a four-step gap
**Date:** 2026-08-15 · **Status:** Accepted

**Context.** `Word Life Cycle.txt` §28–30 makes archiving depend on the
**system-validated** level, never the learner's own choice. But levels are
per-skill and independent (R6), so "the user's level" is not a single number,
and the documents do not say how to collapse the five into one.

`MVP Core.txt` §22–23 gives the progression thresholds (≥ 85 % promotes, < 70 %
demotes, one ladder step at a time, over accumulated data) but not the archiving
distance.

**Decisions.**

1. **The proven level is the minimum across the CEFR skills**, not the average.
   Archiving removes a word from active rotation; it should follow the weakest
   evidence rather than a figure flattered by one strong skill. A learner who
   reads at C1 but listens at A2 has not outgrown A2 vocabulary.
2. **Spelling is excluded** from both the progression engine and the proven-level
   calculation — it carries no CEFR band (ADR-008).
3. **The archiving gap is four ladder steps** (`archiveLevelGapSteps`), which is
   two full CEFR bands. This matches the worked example in §30: a word at A1
   becomes an archive candidate once the system has proven B1.
4. **Exposure is a floor, not a limit** (rule R8). `archiveMinExposure` (3) only
   prevents retiring a word the learner has never actually met in content. It
   never removes a word and never triggers archiving on its own.
5. **The evaluation window resets after every decision**, including a hold.
   Otherwise a learner sitting at 90 % would be promoted again on the next
   session against evidence already spent.
6. **Only growth archives.** A demotion never archives and never un-archives.

**Consequence.** Every number lives in `LevelPolicy` and is seeded from
`configurations` in Phase 5 (rule R3). Changing how conservative archiving is
becomes a config edit. The full rule set is pinned by
`test/level_progression_test.dart`.

---

## ADR-012 — Vocabulary source: a server-side lexicon, no AI-generated meanings
**Date:** 2026-08-15 · **Status:** Accepted

**Context.** A learner may never type their own meaning, and AI may not invent
dictionary data (demo review §16–18). The product owner asked for a stable, free
source giving **English word + Arabic meaning + CEFR level**, loaded into
PostgreSQL and served through our own API — explicitly *not* AI-generated
meanings.

No single source provides all three. The decision is to compose three, joined
offline at build time into one `lexicon_entries` table:

| Layer | Source | Gives us | Licence |
|---|---|---|---|
| Validity + level | [CEFR-J Vocabulary Profile 1.5](https://github.com/openlanguageprofiles/olp-en-cefrj) (+ Octanove C1/C2) | is this a real word, POS, CEFR band | Commercial use permitted with citation; Octanove is CC BY-SA 4.0 |
| English senses | [Open English WordNet](https://en-word.net/) | synsets, definitions, one row per *sense* | CC BY 4.0 |
| Arabic gloss | Arabic WordNet, linked by synset id | the Arabic meaning of **that sense** | CC BY (see caveat) |

**Why WordNet rather than a definitions API.** WordOS's core identity is
`word + intended meaning` — `book = كتاب` and `book = يحجز` are two independent
words with independent journeys (`04-DATA-MODEL.md`). A WordNet **synset is
exactly that identity**, and it is the same key on both the English and Arabic
side. A free-text definitions API would give us prose we would then have to
re-align to meanings ourselves. Loading a dataset also removes a runtime
third-party dependency from the critical path of adding a word, which is the
single most important interaction in the product.

**Consequence.** `GET /words/lookup` is served from our own table. A string that
is not in the lexicon is **rejected** (`WORD_NOT_FOUND`) with edit-distance
suggestions — `hch` never becomes a vocabulary item. CEFR level comes from the
lexicon; AI is not consulted for word data at all.

**Caveat that needs a follow-up decision.** Arabic WordNet **4.0** (109k synsets)
reached that coverage by *machine-translating* Open English WordNet with LLMs.
Using it would contradict "no AI-generated meanings", just with the AI step moved
upstream and unaudited. Arabic WordNet **2.0** is human-curated but covers only
~11k synsets, so common senses will be missing. Options for the gap, in
preference order: (a) ship AWN 2.0 and have a human review a prioritised list of
missing high-frequency senses; (b) ship AWN 2.0 and mark AI-filled senses
explicitly in the data so they are auditable and replaceable. **Not decided.**

---

## ADR-011 — Backend is ASP.NET Core + PostgreSQL, not Firebase
**Date:** 2026-08-15 · **Status:** Accepted

**Context.** The production brief asks for "a real Firebase-backed system"
(Authentication, Firestore, Cloud Functions, Security Rules). The seven source
documents specify a different architecture, and the nine non-negotiable rules
are written against it: **ASP.NET Core + PostgreSQL** owns the WordOS algorithm
and every state transition (R1, R2), with Python for AI and Flutter for
presentation only (`System Archticture.txt` §20, §1028–1119).

These are not compatible as stated. Firestore-with-client-SDK in particular
would put eligibility, pass/fail and scheduling decisions in the Flutter client,
which is a direct violation of R1 and R2.

**Options.**

1. **Keep C# + PostgreSQL** (the documents). Firebase is used, if at all, only
   for auth and push. Highest fidelity to the specification; no rework.
2. **Firebase as the backend, logic in Cloud Functions.** R1/R2 survive because
   the algorithm still runs server-side; PostgreSQL is replaced by Firestore.
   This means re-deriving the whole data model (`04-DATA-MODEL.md`) for a
   document store, and the analytics/aggregation work in Phase 8 gets harder.
3. **Firebase Auth + Firestore accessed directly from Flutter.** Fastest to
   build and **rejected**: it breaks R1, R2 and R4 simultaneously.

**Decision.** Option 1 — **keep ASP.NET Core + PostgreSQL**, confirmed by the
product owner on 2026-08-15. The nine rules stay intact, `04-DATA-MODEL.md`
needs no rework, and the analytics/aggregation work in Phase 8 stays
straightforward on a relational store. Firebase may still be used later for
push notifications or as an auth provider; it will not own domain state.

**Consequence.** Phase 5 proceeds as originally planned. Nothing in the client
changes — every call already goes through `WordOsApi`.

---

## ADR-010 — Language and theme are the only device-owned state
**Date:** 2026-08-15 · **Status:** Accepted

Rule R4 says the device is never the source of truth. UI language and theme are
not learning state — they describe how one installation renders — so they live in
`SharedPreferences` behind `AppPreferences`. The default locale is **Arabic**,
and the device locale is deliberately ignored: the audience is Arabic-speaking,
so an English phone must still open in Arabic until the learner says otherwise.
No other client-side persistence is permitted.

---

## ADR-009 — Adaptive placement by Rasch/EAP with expert-assigned difficulties
**Date:** 2026-08-15 · **Status:** Accepted

**Context.** The demo scored a fixed 8-item form by counting correct answers per
skill. That cannot separate A1 from C1, wastes items far from the learner's
level, and cannot express uncertainty. The brief asked for the best current
approach to be researched rather than assumed.

**Decision.** A per-skill adaptive test: Rasch (1PL) response model, item
difficulties assigned from the CEFR band each item was written for, ability
re-estimated by EAP after every answer, next item chosen by maximum Fisher
information, stopping on a posterior-SE target or an item cap.

Full 2PL/3PL IRT was rejected because its parameters require calibration data
that a pre-pilot product does not have; fitting them to zero learners would be
false precision. Rasch-with-assigned-difficulties is the standard interim for a
new item bank and upgrades to a calibrated CAT by changing **data, not code**.

**Consequence.** Placement becomes a three-call protocol
(`start` / `answer` / `complete`) because an adaptive test cannot hand the client
its items up front. Method, tuning surface, limitations and replacement path are
documented in [`06-PLACEMENT-ALGORITHM.md`](06-PLACEMENT-ALGORITHM.md).

---

## ADR-008 — Spelling is measured but carries no CEFR level
**Date:** 2026-08-15 · **Status:** Accepted

**Context.** The demo assigned Spelling a CEFR band like the other four skills.
The product owner rejected this: `Spelling A1` / `Spelling B2` are not
meaningful, because a CEFR band describes communicative competence, not
orthographic accuracy. The source documents support this — `MVP Core.txt` §33
and §34 describe Spelling entirely in terms of clue type and correctness, and
never assign it a level.

**Decision.** `SkillLevel.userSelectedLevel` and `systemAssessedLevel` become
**nullable**, and are null for Spelling. Spelling instead carries an accuracy
figure and a starting input mode (`LETTER_TILES` / `FREE_TYPING`). Call sites
branch on `SkillLevel.carriesCefrLevel`; `PATCH /settings/skill-level` rejects
Spelling with `SKILL_NOT_LEVELLED`.

Spelling *content* still needs a difficulty (Arabic meaning vs. English
definition as the clue). It is derived from the learner's **Reading** level:
whether an English definition is a usable clue is a reading-comprehension
question, not a spelling one.

**Consequence.** The Hub shows no level badge on the Spelling card, Settings
shows no level dropdown for it, and the placement result reports "3 of 4
correct" instead of a band.

---

## ADR-007 — Placement test scoring is server-side
**Date:** 2026-08-12 · **Status:** Accepted

The client submits raw answers; the backend (with the AI service for the writing item) computes
per-skill CEFR levels and persists them as both the initial *system-assessed* level and the
initial *user-selected* level. The client never derives a level. Manual level edits in Settings
change only the user-selected value and are logged as `USER_MANUAL_CHANGE`.
