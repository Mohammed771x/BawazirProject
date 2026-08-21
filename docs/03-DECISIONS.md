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

## ADR-021 — One speech architecture, provider-swappable
**Date:** 2026-08-17 · **Status:** Accepted

**Context.** Speech had grown in two places: a `TtsService` used by Listening
and the placement audio item, and nothing at all in vocabulary, word detail or
weekly review. Each caller held its own "is it playing" flag, so an icon could
say *playing* after the audio had stopped. The product owner also asked for a
markedly more natural voice, which is a decision with cost implications.

**Decision.** Two layers:

```
SpeakerButton / SpeechPlayButton
            ↓
      SpeechService        ← play/stop state, one utterance at a time
            ↓
      SpeechProvider       ← the voice itself
            ↓
   DeviceSpeechProvider    (today: on-device, free, offline)
```

`SpeechService` owns the only playback state in the app, keyed by an utterance
id, so a speaker button binds to *what is actually speaking* rather than to a
local flag. Starting an utterance stops any other. The on-device provider is
tuned rather than accepted as-is: it selects the best installed English voice
(Enhanced/Premium beat the default Compact), slows the rate from the
screen-reader default, and on iOS uses the playback audio category so speech is
audible with the ringer switch off.

**Consequence.** A cloud neural voice is a second `SpeechProvider`, proxied
through the backend so no key reaches the app — no screen changes. Speech
recognition moved to `SpeechRecognitionService` to end the name collision;
recognition and synthesis are different concerns and now read that way.

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

---

## ADR-022 — Word resolution for tapped words lives on the server
**Date:** 2026-08-17 · **Status:** Accepted

**Context.** Part 2 §17 makes every word in a reading passage tappable: a word
the learner does not know should not stop the passage dead. But the word arrives
inflected — the passage says *researching*, the lexicon knows *research* — and
something has to bridge that. The obvious place is the client, where the tap
happens.

**Decision.** `GET /api/words/define?w=…` resolves the spelling server-side.
`SurfaceForms.CandidatesFor` proposes base forms (exact spelling always first)
and the lexicon confirms each one, so a wrong guess costs a miss rather than a
wrong definition. The response carries `matchedText`, so the sheet can say
"researching → research" rather than appearing to answer a different question.

**Consequence.** One tap gives the same answer on every platform (rule R1), and
improving the resolution is a server deploy. A word with no entry answers
`200` with an empty `senses` array — proper nouns and numbers appear in
generated prose and are not errors.

Target words are the exception: they are pronounced but never defined, because
the session is about to ask what they mean. Which words those are is also the
server's answer — `content.targetSpans` now carries their positions, which the
real backend previously never sent, leaving them un-underlined in the real app.

---

## ADR-023 — Practice: a session that measures nothing
**Date:** 2026-08-17 · **Status:** Accepted

**Context.** Part 2 §5 asks for something to do when no words are eligible.
Every word starts on Reading behind a two-day gap, so a new learner's second
session is often an empty screen — the worst possible moment to have nothing.

**Decision.** `POST /api/sessions/{skill}/start?practice=true` starts a session
with no vocabulary attached: real Gemini content, real comprehension questions,
`IsPractice = true`. It owns no words, so nothing passes or fails; and it is
kept away from the level engine as well, because a promotion archives Active
vocabulary — an activity chosen *instead of* the pipeline must not change it.

Offered only for Reading and Listening (the other three are *about* the words),
and only when the learner asks: `NO_WORDS_DUE` is still the answer to a plain
start, so practice is never silently substituted for what was requested. The
session says "Practice only — your words are not affected" while it runs.

**Consequence.** Practice generates AI content that produces no measurement, so
it costs tokens for engagement rather than for progress. If that proves
expensive, the lever is the offer (or a cheaper prompt), not the guarantee.

---

## ADR-024 — Synonyms come from shared glosses
**Date:** 2026-08-17 · **Status:** Accepted

**Context.** Part 2 §39 wants a B1/B2 spelling clue to be a synonym plus a
simplified definition. The lexicon has no synonym column, and its `SenseId` is a
WordNet *sense key* — one row per lemma-sense — so synonyms are not reachable
through it.

**Decision.** Match on `DefinitionEn`. A WordNet gloss belongs to the synset,
not the word, so every lemma sharing a definition string is a synonym of the
others: *bottom*, *rear* and *backside* carry one identical gloss between them
(28 lemmas share the longest one in the imported data). The B1 clue pairs the
lowest-frequency-rank alternative lemma with the gloss trimmed to its first
clause; C1 gets the full definition, A1/A2 the Arabic meaning.

**Consequence.** No new data and no AI call for a clue. It is exact rather than
fuzzy — identical gloss strings really are one synset — but it finds nothing for
single-lemma synsets, which is why every tier falls back down the list.

---

## ADR-025 — An append-only activity log
**Date:** 2026-08-17 · **Status:** Accepted

**Context.** Part 3 §34–§35 asks for event logging, and the dashboard needed it
before it was asked for: "was this learner active on Tuesday?" was answered by
looking for a completed session or an added word, because `LastLoginAt` records
one moment rather than a history. A learner who opened the app, read a passage
and left produced no evidence at all — and a single "last login" column can
never place someone in two different windows.

**Decision.** `activity_events`: user, type, optional skill, optional entity id,
timestamp. Written at registration, sign-in, word added, session started and
completed (practice distinguished from the real thing), review completed and
placement completed. Append-only — never updated, never deleted.

`activeToday` / `activeThisWeek`, the per-day series, the sign-in count and the
admin list's `days` window are all now computed from it.

**Consequence.** Every figure on the dashboard traces back to rows that produced
it, and a changed analytics query recomputes the same history rather than a
different one. The log holds no free text and nothing sensitive: a type, a
timestamp, and the id of whatever the event was about. What a learner actually
said stays with the session, under their own account.

The cost is one insert per meaningful action, on writes that already hit the
database in the same transaction.

---

## ADR-026 — The AI places Speaking and Writing; nothing else
**Date:** 2026-08-18 · **Status:** Accepted

**Context.** Reading and Listening are matched against an answer key, so the
score is exact and no model is involved. Speaking and Writing had no key either
— and were being scored by `HeuristicFreeResponseScorer`: length, lexical
variety and a connective check. That cannot tell a short fluent answer from a
padded weak one, which is most of what separates B1 from A2.

**Decision.** At `/placement/{id}/complete`, every produced answer for Speaking
and Writing is sent to `/ai/placement/evaluate` — one call per skill, not per
answer. Gemini returns, per item, a CEFR estimate, a score in `[0, 1]` and the
evidence for it. Those scores go back into the same Rasch estimator the
receptive skills use, so the **band is still computed here** (rule R2). The
heuristic remains as the live score during the test, because the adaptive engine
needs a number immediately to choose the next item, and as the fallback when the
AI is unavailable — in which case the result is marked as scored offline.

**Consequence.** Verified against real Gemini: identical multiple-choice
answers, weak produced language placed Speaking A1+/Writing A2+; strong produced
language placed both B1+. Reading and Listening were identical across both runs,
which is the property that matters — the model touches only what has no key.

Two behaviours had to change to make this real: a productive skill may not stop
before at least one produced answer exists (grammar items are filed under
Writing and are multiple-choice, so a learner could otherwise be placed on
Writing without writing anything), and the raw answers must be stored, which
they already were for the evidence view.

---

## ADR-027 — The placement ladder opens easy and never runs uphill
**Date:** 2026-08-18 · **Status:** Accepted

**Context.** The engine opened each skill at the population prior — a B1 item.
That is textbook adaptive testing: the first answer is most informative at the
mean. It is also how a beginner's first contact with WordOS is a question above
their level. Separately, a learner failing everything was walked *upwards* once
the easy items ran out, because "closest remaining difficulty" is all the rule
said, and the closest thing left to someone at the floor is something harder.

**Decision.** Two changes to `NextItem`:

1. The first item of each skill is drawn from the **easiest band available**.
2. No item may be more than one band above the learner's current estimate. When
   nothing is within reach, the skill is finished — the estimate is not going to
   improve by asking something harder.

**Consequence.** A strong learner spends an extra item or two climbing; the
ladder reaches their level from the second answer. A weak learner is never shown
a progressively harder question while already struggling. Verified live:
`A1 → B1 → …` where it used to be `B1 → …`.

The cost is one or two items of test length for the strongest learners, paid for
a result that never reads as a verdict (Part 1).

---

## ADR-028 — Push-to-talk, not hands-free
**Date:** 2026-08-18 · **Status:** Accepted · **Supersedes part of ADR-021's loop**

**Context.** The Speaking session opened the microphone automatically when the
tutor stopped talking, and closed it after three seconds of silence. It sounds
elegant. In use it cuts the learner off mid-sentence, because someone composing
a sentence in a foreign language pauses constantly — which is the entire
population this app is for. No timer value fixes that.

**Decision.** The learner starts and ends their own turn. The tutor speaks, the
microphone stays shut, and the control says "tap to speak". A tap opens it with
effectively no silence cutoff; their words appear live as they are recognised; a
second tap closes the turn and sends it. The same control is used for spoken
placement items.

**Consequence.** One tap at each end of a turn, in exchange for a learner who
can think mid-sentence without losing their answer. `listenOnce` remains in the
service for callers that genuinely want an automatic turn; no learner-facing
screen uses it.

---

## ADR-029 — The passage glosses its own words
**Date:** 2026-08-18 · **Status:** Accepted

**Context.** Tapping a word in a passage returned every sense the lexicon holds
for it. "bank" has six; five of them are wrong in any given sentence, and the
learner is left choosing between meanings on a screen that exists precisely
because they did not know the word.

Disambiguating at tap time means an AI call per tap: cost, a second of latency,
and a worse question — by then the only evidence left is the text.

**Decision.** The generator glosses the passage while it writes it. `READING_SCHEMA`
gained a `glossary` array: for every content word, its meaning **in that
sentence** and its part of speech. It is stored with the session and returned
with the content, so a tap is instant and costs nothing.

The lexicon remains the authority on what may be *added* (ADR-012): adding from
a gloss resolves the word and picks the lexicon sense closest to the meaning the
learner just read, rather than the most common one.

**Consequence.** 81 glossed words in a typical passage, at no extra call. Part
of speech now appears wherever a word does, in the interface language — a
learner adding "will" should know whether they are learning the auxiliary or
the noun.

A word the generator missed falls back to `/api/words/define`, which is the
old behaviour and still correct.

---

## ADR-030 — A passage can be re-told, and the choice sticks
**Date:** 2026-08-18 · **Status:** Accepted

**Context.** A learner who opens a passage above their level could only abandon
the session. The level shown in the header was a label, not a control.

**Decision.** Tapping the CEFR badge re-tells **this** passage at the chosen
level — same story, same people, same events, different language — and
regenerates its questions, which would otherwise ask about sentences that no
longer exist. `POST /api/sessions/{id}/level`.

Only before the questions begin. Afterwards the items the learner already
cleared would be replaced and their answers silently discarded, so the control
disappears and the server answers `409`.

The choice is written to the learner's `UserSelectedLevel` — the same setting
Settings shows. A preference that reverted next session would be worse than no
control at all.

**Consequence.** What it does *not* touch is the validated level, which is
earned from performance and is the only thing allowed to drive progression and
archiving (rule R6). Asking for an easier passage must not quietly demote a
learner, and asking for a harder one must not promote them for free.

---

## ADR-031 — Only words come back
**Date:** 2026-08-18 · **Status:** Accepted · **Refines the §29–31 loop**

**Context.** A wrong answer sent any item to the back of the queue. That is
right for a target word and wrong for a comprehension question: the learner has
already been shown the answer, so asking again teaches nothing, and it leaves
them finishing a session by re-reading questions they were done with.

**Decision.** Only items about a word repeat. A comprehension question is asked
once, recorded, and cleared — it measures whether the passage was pitched at the
right level, and one answer measures that.

Target words keep the existing rule: they come back until answered or until the
retry cap, and a first-attempt miss still fails the word for that skill. The
retry exists to fix the memory, not the score.

**Consequence.** Sessions end where the learner expects them to. The
comprehension figure on the result screen is unchanged — it was always
first-attempt accuracy.

---

## ADR-032 — One hint ladder, entered at the learner's level
**Date:** 2026-08-18 · **Status:** Accepted · **Supersedes the clue tiers of ADR-008**

**Context.** A spelling task showed one clue chosen by level, plus one hint that
revealed the opening letters. Two problems. A learner stuck on the clue had
nowhere to go except the answer's own letters, and a learner given a full
WordNet definition at A2 was being tested on their reading rather than their
spelling.

**Decision.** One ladder, five rungs, each easier than the last:

```
dictionary definition → simplified definition → synonym
                      → translation → number of letters
```

Where a learner joins depends on their level — C1 at the top, B2 one rung down,
B1 at the synonym, A1/A2 at the translation — and every press of "hint" steps
down exactly one rung. Nothing above the entry rung is ever shown: climbing back
up is not what a hint is for.

The ladder is built on the server when the session starts, because it depends on
the learner's level and on what the lexicon holds (rule R1). Rungs with nothing
to say are skipped rather than shown blank — a word with no synonym simply has
one fewer step — and the simplified definition is dropped when it reads the same
as the full one, because a press that changes nothing looks broken.

The masked-letters hint is gone. It gave away the spelling of the word the task
was asking for, which the letter count does not.

**Consequence.** `hint` leaves the wire and `hints` replaces it; the `Hint`
column is dropped. `SpellingClueKind` gains `SIMPLIFIED_DEFINITION` and
`LETTER_COUNT` and its members are ordered hardest-first, so the order of the
enum *is* the ladder. Help always exists: the translation is always present, so
the ladder can never come back empty.

---

## ADR-033 — The closed-class words are authored, not imported
**Date:** 2026-08-18 · **Status:** Accepted · **Extends ADR-012**

**Context.** A learner reported that <code>is</code>, <code>are</code> and
<code>what</code> could not be found or added. They were not filtered out —
they were never there. Open English WordNet is a lexicon of *content* words:
nouns, verbs, adjectives and adverbs. Pronouns, articles, auxiliaries, modals,
prepositions, conjunctions and question words are absent by design, and where a
homograph exists it is the wrong word (`are` is in WordNet only as a unit of
area, آر).

**Decision.** Author them. The classes are *closed* — English gains new nouns
constantly and new pronouns almost never — so the set is finite and can simply
be written down: 167 entries covering pronouns, possessives, reflexives, the
forms of *be*/*have*/*do*, modals, determiners, question words, prepositions,
conjunctions and negation, each with an English definition, an Arabic meaning
and a CEFR band.

They live in the importer beside the join, are upserted by sense id like every
other row (`wordos-fn-…`), and can be applied on their own with
`--closed-class-only` — seconds, and no 166 MB download. Their frequency rank is
`-1`, ahead of every WordNet sense, so the auxiliary `are` outranks the unit of
area.

Two further gaps closed at the same time, because "any real English word" is the
actual requirement:

* **Irregular forms.** No rule turns `went` into `go` or `children` into
  `child`, and those are the first words a learner meets. `SurfaceForms` gained
  a table of them, so search resolves what rules cannot.
* **One-letter words.** `a` and `I` are words. A single letter is now matched
  *exactly* — never as a prefix, which is what the two-character minimum was
  really protecting against (docs/07-SECURITY.md §6).

**Consequence.** A second provenance exists in the lexicon
(`en=wordos-closed-class`), so "every row cites three sources" becomes "every
row cites its own". Nothing else changes: same search, same senses, same
pipeline — a learner can put `because` through five skills exactly like
`research`.

---

## ADR-034 — The dictionary is searchable from either side
**Date:** 2026-08-18 · **Status:** Accepted

**Context.** Search was a prefix match over the English spelling, which assumes
the learner already knows the English word. That is backwards for the person the
product is for: an Arabic speaker knows what they want to say — *يذهب* — and is
looking for how to say it.

**Decision.** A query written in Arabic searches the meanings instead of the
spellings and returns the English words that carry them. Both sides are folded
to one plain form first — diacritics and tatweel stripped, the alef and ya
families collapsed, ta marbuta written as ha — because Arabic WordNet vocalises
a large part of its glosses (45k of them) and nobody types the marks.

The fold is stored, not computed per query: `MeaningArNormalized` alongside the
gloss, with a trigram index, so a substring match over 175k rows stays an index
scan. The displayed gloss is never changed — that is what the learner reads.

Results are ordered by how close the match is before how common the word is: a
gloss that *is* the query outranks one that merely contains it, so `ذهب` offers
the verb before "18-karat gold".

**Consequence.** The Add Word field takes either language and its hint says so.
The direction of the search is inferred from the script, so nothing new is asked
of the learner. A single Arabic letter returns nothing, for the same reason a
single English letter is matched exactly rather than as a prefix: substring
search is the easier of the two to walk.

---

## ADR-035 — The app speaks the learner's language; the material stays English
**Date:** 2026-08-18 · **Status:** Accepted · **Extends ADR-010**

**Context.** Every string the app owns was already bilingual. Everything the
*server* or the AI wrote was English regardless of the language the learner had
chosen: the instruction on a writing task, the feedback on what they wrote, the
sentence after a failed request. An Arabic interface that says
"Write one sentence using «research»." and then "That task is no longer the
active one." is not bilingual; it is an Arabic app that lapses into English
whenever something happens.

**Decision.** Draw the line at what the text *is*, not at where it came from.

* **The app talking to the learner follows the app language** — instructions,
  evaluation feedback, error messages, every label.
* **The material being learned stays English** — the passage, the comprehension
  questions written for it, the answer options, the spoken conversation, and the
  whole placement test. Translating a question changes what it measures, and the
  placement bank's difficulties are calibrated on the English wording.

Three mechanisms, one per kind of text:

1. **Fixed instructions travel as a key**, not a sentence
   (`WRITE_THE_WORD`, `WRITE_A_SENTENCE`, `WRITE_A_SENTENCE_ABOUT_YOURSELF`).
   The server still decides what is being asked; the client says it. The English
   `prompt` is still sent, as the fallback for a client that does not know a
   key, and an item whose text was written for this session carries no key at
   all — that is how "content" is identified on the wire.
2. **Generated feedback is asked for in the learner's language.** The client
   sends `Accept-Language`; the API passes it to the AI service, which instructs
   the model accordingly. English words and sentences stay English *inside* the
   Arabic feedback rather than being transliterated. The conversation itself is
   never affected — it is the skill being practised.
3. **Failures are localised by code.** Every API error already carries a stable
   `code`; the client maps the codes it knows to its own copy and falls back to
   the server's English sentence for anything newer than the app.

The language is sent per request rather than stored on the account, because it
is a device setting (ADR-010): the same learner may read the app in Arabic on a
phone and English on a tablet, and neither is more true than the other.

**Consequence.** `Accept-Language` becomes part of the API contract, defaulting
to Arabic when it is absent or names a language the product does not have.
Session items gain `promptKey`. A learner switching language in Settings sees it
apply from the next request, with no session restart — and nothing they are
being tested on changes when they do.

---

## ADR-036 — What the adversarial pass found
**Date:** 2026-08-19 · **Status:** Accepted

**Context.** Every test until now drove the app the way it is meant to be
driven. A learner reported the opposite: the preset reporting windows worked and
typing a custom one closed the app. That is the class of failure a happy-path
suite cannot see, so the app was driven the way people actually drive it —
mashed buttons, screens left mid-load, pasted rubbish in search boxes, hostile
numbers in every field, audio still playing on the way out.

**Findings, all fixed.**

1. **The reported crash was in the dialog, not the number.** The custom-range
   dialog created a `TextEditingController` and disposed it as soon as
   `showDialog` returned — while the dialog was still animating away, with its
   field still reading it. The next frame threw and took the app with it. The
   dialog now owns its controller as a widget with its own lifetime, accepts
   digits only, and disables Save until the field holds a usable number.
2. **A large window overflowed the server's date arithmetic.** `AddDays` throws
   past year 9999, so `days=999999999` was a 500 and an empty dashboard. The
   window is now clamped in one shared place, at ten years — longer than the
   product has existed, and the same ceiling the client applies.
3. **Any unreadable query value was a 500.** ASP.NET raises a binding failure as
   an exception after the endpoint filters, so `?days=abc` — anything typed into
   a numeric field, or any stale link — was logged as a server fault. It is now
   a 400 with `INVALID_PARAMETER`.
4. **A pasted control character was a 500.** PostgreSQL refuses a NUL byte
   inside a text value, so one pasted character crashed a search. Search terms
   are stripped of control characters before they are compared. This was never
   an injection risk — the queries are parameterised — just a crash.

Two mock/real divergences were closed at the same time, because a mock that is
more permissive than the server hides client bugs until they reach a device: the
mock now resumes an unfinished session instead of starting a second one, and
refuses a search term longer than the server's cap.

**Consequence.** Three suites now exist whose assertion is "nothing was thrown":
`hostile_input_test.dart`, `stress_navigation_test.dart`, and the reporting-window
walk in `developer_dashboard_test.dart`. A hostile sweep of every endpoint ends
with zero unhandled exceptions in the server log, and that log is the check —
a 500 is a defect even when the client renders the failure politely.

---

## ADR-037 — The Owner can move a schedule forward, and it is written down
**Date:** 2026-08-19 · **Status:** Accepted

**Context.** The pipeline puts two days between one skill and the next. That gap
is the behaviour the product exists to measure — and it makes the product
impossible to demonstrate or to test end to end, because seeing one word through
five skills takes over a week of waiting. The mock backend had a "skip 2 days"
control for exactly this reason; against the real backend there was nothing, so
every check of Speaking, Writing or Spelling meant editing timestamps in
PostgreSQL by hand.

**Decision.** An Owner-only endpoint that brings a learner's waiting skills
forward: `POST /api/admin/users/{id}/advance-schedule`, with a **Skip 2 days**
button at the top of that learner's page in the Developer Dashboard.

What it does *not* do is what makes it safe to have:

* **It moves scheduled dates only.** A pass, a failure, an attempt, a session,
  an event — anything that already happened — is untouched. History is the
  evidence the experiment runs on; a tool that rewrote it would turn every
  figure on the dashboard into a guess.
* **It is written to the activity log** as `ScheduleAdvanced`. A pipeline
  finished in an afternoon would otherwise read as an extraordinary learner
  rather than as a moved clock.
* **It is Owner-only twice over** — the route group carries the policy and the
  handler checks again — and refuses an ordinary learner even for their own
  schedule. Hiding a button is not access control.
* **The number of days is clamped to 1–30**, so a typed nonsense value cannot
  push dates outside what a date can hold (the lesson of ADR-036).

**Consequence.** The whole pipeline can be walked in one sitting, on a device,
against the real backend. The mock keeps the same behaviour so development
matches, and the Owner's own account can be skipped like any other — which is
how a single tester walks all five skills alone.

---

## ADR-038 — Writing has a level too, and it decides the rewrite
**Date:** 2026-08-19 · **Status:** Accepted · **Extends ADR-030**

**Context.** Two complaints about the same control. First, a learner who changed
the level inside a session found Settings still showing the old band: the server
had written it through (ADR-030) but the client renders levels from the cached
profile, and nothing re-read it — so the change looked like it had not happened.
Second, Writing had no level control at all, and it is the skill where a level
means the most concrete thing in the product.

**Decision.**

**The client re-reads the profile after any in-session level change.** Settings
and the Skills Hub both render from it, so both show the new band the moment the
learner looks. The server was already correct; the client was showing a stale
copy of it.

**Writing gains the control, and its level is what the rewrite follows.** After
a learner writes their sentence they are shown it again *as a writer at their
level would put it* — the same idea, the same content, raised or simplified to
match the band. It is deliberately **not** a grammar correction:

> *"I did research about sleep and it was very good and helpful for me."*
> **A2** — I did some research on sleep, and it was very helpful for me.
> **B2** — I conducted extensive research on sleep patterns, and the findings were incredibly helpful for my daily routine.
> **C1** — I conducted extensive research on the effects of sleep, which proved to be incredibly insightful and beneficial for my personal well-being.

The card is titled "Your sentence at B2" rather than anything resembling a red
pen, because a learner who reads a rewrite as a correction concludes they made a
mistake even when their sentence was correct.

Writing changes level like Speaking rather than like Reading: neither has a
passage to re-tell, so the new level is simply an input to what happens next —
the tutor's reply, or the rewrite — and nothing the learner has already done is
regenerated or lost. Reading and Listening still lock once the questions begin.
Spelling still refuses: it carries no CEFR band of its own (ADR-008).

**Consequence.** Four of the five skills can be re-levelled from inside the
session, all four write through to the learner's profile, and only the
*user-selected* level moves — the validated one is still earned from performance
(rule R6), which a test pins on the same request.

---

## ADR-039 — A session records what it is for, and one glossary rule serves every passage
**Date:** 2026-08-19 · **Status:** Accepted

**Context.** Two reports from the device, with the same shape: something the
learner had was quietly not there any more.

*Tapping a word gave a dictionary meaning instead of the meaning it carries in
the sentence* — but only after the level had been changed. The generation prompt
spells out that the glossary must contain **every** word a learner might tap; the
re-telling prompt asked for "a full glossary, exactly as for a new passage" and
got four entries for a three-sentence text. Every tap that missed fell through to
the dictionary, which answers about every sense a word has ever had — the one
outcome the glossary exists to prevent (ADR-029).

*Speaking said five words were due and opened with none.* A session's words were
recovered from its items, since every item knows the word it is about. Speaking
is a conversation and has **no items**, so a learner who left and came back got a
session about nothing, permanently: the hub kept offering the skill, and the
resume kept returning the same empty session.

**Decision.**

**One glossary rule, written once.** `GLOSSARY_RULE` is shared by the generation
and re-telling prompts, so a re-told passage is glossed exactly like a new one. A
paraphrase of a requirement is not the requirement. Measured after the change: a
120-word passage glossed 94 words, and its A2 re-telling 68 of 105 — against 4 of
27 before.

**A session records its own words**, in `WordIdsJson`, at the moment it starts.
Every place that asks "which words is this session about?" — resume, level
change, completion — reads that one record instead of inferring it from items or
from "whatever is at this skill right now", which drifts as soon as anything else
in the pipeline moves.

**And an empty session is replaced rather than resumed.** A session saved by an
older build has no record and no items, so it can only ever come back empty;
resuming it hands the learner a screen they can never get past. Dropping it costs
nothing — abandoning never consumed the words — and the request continues as a
fresh start, which is what heals accounts that already have one.

**Consequence.** Tapping any word in any passage, at any level, answers from the
passage. A conversation survives being left and reopened. And the words a session
is about are a fact it carries, not a query whose answer changes underneath it.

---

## ADR-040 — The word chooses the scene, and naming a word is not using it
**Date:** 2026-08-19 · **Status:** Accepted · **Refines ADR-016, ADR-019**

**Context.** From a real conversation on the device, target word `hook`:

```
tutor  : Does that app hook your attention so you use it every day?
learner: Can you please change the topic so I can use hook in a suitable sentence
tutor  : What do you want to become when you finish your English studies?
         Try to use "hook" in your answer.
```

Two failures in three lines. The tutor asked about careers and bolted the word
onto it — there is no honest answer to that, and the learner had already said so.
And their request to move the topic *contained* the word, which the backend read
as having used it: the turn-level check was `transcript.Contains(word)`, so the
tutor stopped steering toward `hook` for the rest of the session, and the word
then failed a conversation it was never given a chance in.

**Decision.**

**Work backwards from the word.** The tutor thinks of a real situation where a
person would say the word, and asks about *that*. Bolting "try to use X" onto an
unrelated question is named in the prompt as the failure it is. If the word does
not fit the current topic, the tutor **changes the subject** — a conversation is
allowed to move, and steering to where a word lives is the skill. The learner is
never obliged to use it: answering without it is a good answer, and the word gets
another opening later.

The learner's **interests** choose between the situations a word could live in,
when more than one would work. They are never a reason to force a word onto a
topic it has nothing to do with — which is what "technology" plus `hook`
produced.

**Talking about a word is not using it.** The turn-level verdict is now the AI's
observation, *verified* by the endpoint against what the learner actually said —
neither half alone is enough. A text search cannot tell using a word from naming
one; an AI claim is an observation, not a verdict (rule R2). And the verdict is
**kept on the session** rather than recomputed by re-scanning the transcript, so
a word counted once stays counted and a word merely named never is.

**And the fact outranks the verdict.** Whether the learner used a word is
something the session watched happen and recorded, turn by turn, with the
sentence in front of it. Whether they used it *well* is the end-of-conversation
judgement. So a verdict of "never used" about a word the session saw used is not
a judgement about quality at all — it is a disagreement about a fact we hold the
evidence for. In that case the observation is set aside and the evidence answers,
rather than failing a learner for a word they were recorded saying.

Everything else about the verdict is unchanged and still lives in
`SpeakingRules.Passed`: used, meant correctly, and understandable, with no
grammar breakdown severe enough to obscure the meaning. Ordinary grammar slips
are recorded and ignored (`MVP Core.txt` §32), and a failure reschedules only
Speaking, after the usual gap (rule R5).

**Consequence.** Verified against real Gemini on the same exchange: the request
to change the topic now produces *"Do you ever go fishing near the water and need
a sharp hook to catch a fish?"*, and `hook` correctly stays in the remaining
list. A conversation on an unrelated topic — a grandmother's cooking — reaches
the word by moving through fish to fishing, rather than demanding it. And a
learner answering in their own words counts even though the tutor used the word
first: that is what the tutor was trying to make happen.

---

## ADR-041 — Moving on is about *use*; how well is judged at the end
**Date:** 2026-08-19 · **Status:** Accepted · **Refines ADR-040**

**Context.** From the device, target word `become`:

> tutor: What do you want to become in the future?
> learner: I want to become a software engineering.
> tutor: *…asks about `become` again.*

The learner used the word. The sentence has a grammar error — "a software
engineering" — and the turn-level rule asked the model for words used "naturally
and correctly", so it reported nothing and the tutor asked for the same word
again. Reproduced three times out of three.

Separately, the tutor had stopped saying which word to practise. That was a
casualty of ADR-040: the outright ask became conditional to stop it being bolted
onto questions the word did not fit, and the effect was that a learner who cannot
see the word list had to guess what was wanted.

**Decision.**

**The turn-level report decides one thing: whether to move on.** It is not a
mark. A word counts the moment the learner says a sentence of their own with it,
**even if it is wrong** — "I want to become a software engineering" is an
attempt, and asking again for a word already attempted is the single most
frustrating thing a tutor can do. Correctness is judged once, at the end, by
someone else (ADR-019), and the rule there is unchanged.

**The word is named every turn**, as the last sentence: *Try to use the word "…"
in your answer.* Even when the question makes it obvious — the learner cannot see
the list. It goes at the end of a question the word already fits, never as a
repair for one it does not, which is the distinction ADR-040 exists for.

**The closing turn is a different prompt**, not an instruction inside the
practising one. Told to always name a word, a model whose list is empty invents
one: a learner who had never been given `obstacle` was asked to use it. Structure
settles what instruction could not — when nothing remains, the tutor reacts, says
what went well, and closes.

**Consequence.** Verified against real Gemini: the imperfect `become` sentence
now counts and the conversation advances to the next word; every practising turn
ends by naming the word; the closing turn names none; and the end-of-session
verdict on that same sentence is a pass — used, meant correctly, understood, with
a grammar slip that §32 says must not fail it.

---

## ADR-042 — A conversation ends on a goodbye, and a space is a letter
**Date:** 2026-08-19 · **Status:** Accepted

**Context.** Two things reported from the device.

A conversation ended on a question. The last exchange was:

> learner: Yes my code make a loop for repeat the message many time.
> tutor: When you create a loop, does the computer run the same task until you
> stop it? **Try to use the word loop in your answer.**

— asked one line *after* they used it, because the reply is written in the same
call that judges the turn. The reply therefore always reflects the list as it
stood *before* the learner spoke, and the learner's reward for finishing is being
asked for the word again.

And a two-word entry could not be spelled at all. The letter pool stripped
spaces, so `alarm clock` offered every letter and no way to put the gap in: a
puzzle with no solution.

**Decision.**

**When the last word lands, the closing turn is asked for properly.** One extra
call, once, at the end of a conversation — with the list now empty, so the tutor
reacts to what was said, names a word they handled well, and says goodbye. If
that call fails the learner keeps the reply they already have; the conversation
is over either way and an error at the moment they finish would be a strange
reward.

**The spaces are tiles.** A pool for `alarm clock` contains one, drawn as a space
bar so it does not read as a rendering fault, and decoys are counted on the
letters because a space is not something to guess. And spelling is judged
**without regard to spacing**: `alarmclock` and `alarm clock` are the same
spelling of the same word, the exercise is the letters, and a learner with every
letter right should not be failed by a gap.

**Consequence.** Verified end to end on the real stack: a conversation whose last
word lands now closes with *"You used the word hook perfectly in your sentence
today. You did a great job with our practice, Mohamed. Have a wonderful day."*,
and a freshly generated `alarm clock` task carries its space tile and accepts the
answer typed with or without it.

---

## ADR-044 — "Speak at their level" is not an instruction a model can follow
**Date:** 2026-08-20 · **Status:** Accepted · **Refines ADR-041**

**Context.** Checking that the level control on a conversation does anything.
It did — the register moved with the band — but the low end did not move far
enough. Asked to talk to an **A1** learner, the tutor said:

> "…do you sometimes have to run the same lines of code many times **until a
> condition is met**?"

Short, yes. A1, no: that is B2 vocabulary in a short sentence. A learner who
lowers the level because the tutor is too hard hears something they still cannot
follow, which makes the control look broken.

**Decision.** Say what each band *means*, rather than naming it. A1 is "five to
eight words, commonest words only, one clause — no 'until', 'although',
'which'"; C2 is "fully idiomatic, nuance and abstraction as with a peer". The
rule is passed into the prompt with the level and applies to the greeting, every
turn and the closing.

**Consequence.** Measured on the same moment of the same conversation, before
and after:

| Band | Before | After |
|---|---|---|
| A1 | 38 words, "until a condition is met" | **16 words** — "Building an app sounds very fun. Do you need to repeat the code in a loop?" |
| B1 | 41 words | 25 words |
| C2 | 48 words | 62 words, "an incredibly demanding undertaking… ironing out the logic" |

And live, mid-conversation: at C2 the tutor asked about "iterating through your
data structures to identify the source of an error"; the learner dropped the
level to A1 and the very next line was "Do not worry, it is okay. Sometimes code
does the same action many times. Do you use a loop to count to ten?"

---

## ADR-043 — A session that cannot be finished is worse than one that fails
**Date:** 2026-08-20 · **Status:** Accepted

**Context.** Reported from the device: opening Spelling showed "Loading…" and
never stopped. The server was answering that request in **five milliseconds**.

Two faults, stacked.

The session had been answered through — eight of eight — but never closed, so it
was resumed on every visit with `nextItemId: null`. The screen asks for "the
current item", is given nothing, and shows a spinner while it waits for one that
does not exist. Any learner whose app dies between the last answer and the result
screen lands in exactly that state.

And it could not have recovered on its own, because *completing* it returned
**500**: one of its words had moved on to Writing since, and the domain refuses
to apply a Spelling result to a word that is no longer at Spelling. That refusal
is right. Throwing it out of the endpoint is not: the session stays open, is
resumed again, and fails again, for ever.

**Decision.**

**A session with nothing left to ask finishes itself.** When a resumed session
comes back with no next item and nothing remaining, the client completes it
rather than waiting — that is the tap the learner was one step away from making.
And if a current item is ever missing anyway, the screen offers **Finish**
instead of a spinner: there is no question to show, and waiting for one is
waiting for nothing.

**A word that has moved on is skipped, not thrown.** Completion marks it
`superseded: true`, applies nothing to it, and closes the session. The word's own
state is untouched — this session is no longer the one deciding what happens to
it.

**And `_start` catches everything, not only `ApiException`.** A malformed
response or a bug in the screen used to leave the spinner turning with no error
and no way out but killing the app.

**Consequence.** Verified on the account that was stuck: the session that
returned 500 now closes with eight outcomes, and Spelling answers `NO_WORDS_DUE`
— the honest state. A spinner that cannot end is the worst failure in the app: it
says nothing, blames nothing, and offers no way out.

---

## ADR-045 — A form of a word is a word
**Date:** 2026-08-20 · **Status:** Accepted · **Extends ADR-033**

**Context.** The dictionary held `go` and not `went`. WordNet is a lexicon of
*lemmas*, so every form a learner actually meets in a sentence — `went`, `gone`,
`going`, `lunged`, `mice` — was unreachable: not searchable, not addable, not
practisable. A learner who has "go" in their pipeline still cannot read a page of
past-tense English.

**Decision.** Each form is an entry of its own, with its own sense id, so it can
be added, scheduled and validated exactly like any other word. What counts as a
form worth adding is the product's rule, and it draws a line:

* **Forms that look different are words.** `went`, `gone`, `going`, `took`,
  `taken`, `mice`, `children`, `women`, and the regular `walked`, `lunging`.
* **A plural that is the word with an `s` on the end is not.** No `books`, no
  `cities`, no `boxes` — a learner who knows `book` does not need a second row.

Three sources, in order, because no single one is complete:

1. **Open English WordNet's own `form` lists**, which record a form precisely
   when its spelling breaks the rule — `went`, `swimming`, `studied`, `mice` —
   and record nothing when it does not. That silence is the same distinction the
   product draws, already made by lexicographers.
2. **Rules**, for the regular forms the dataset therefore omits: `walked`,
   `walking`, `going`, `lunged`.
3. **Two short authored lists**: the ten verbs whose past *is* the base
   (`read`, `cost`, `hurt`, `spread`…), where reading the dataset's silence as
   "regular" would invent `readed` and `costed`; and the three irregular plurals
   it misses (`women`, `people`, `dice`).

**Past and participle are distinguished only when that is knowable.** The
dataset lists them alphabetically — `gone, went` — so position says nothing.
Two patterns settle 100 of the 132 verbs that have both: the participle ends in
`n` (`gone`, `taken`, `written`, `done`), or the pair is an ablaut where the past
carries `a` and the participle `u` (`drank`/`drunk`, `began`/`begun`). For the
rest nothing is guessed: both are labelled the past, which is true of both. The
first cut of this shipped `went` labelled as the third form, which is exactly the
kind of confident wrongness a language app must not teach.

Every form carries what it is, in both languages: *past tense of "go"* and
*(الماضي)* after the Arabic meaning. Its frequency rank sits one step behind its
base, so searching `go` still offers the word before its forms.

**Consequence.** The lexicon goes from 175,778 rows to 216,208. `books` and
`cities` are still absent, `readed` and `costed` were never created, and `putted`
is present — because it is the past of *putt*, the golf verb, which is the kind of
thing only the data can tell you.

---

## ADR-046 — One spelling, two things to learn
**Date:** 2026-08-20 · **Status:** Accepted · **Refines ADR-045**

**Context.** For most English verbs the past and the past participle are the same
word: `played`, `walked`, `said`, `bought`. Adding one entry for it means a
learner can practise "I played" or "I have played" but cannot choose which, and
nothing on the screen says which one the session is asking about.

**Decision.** The spelling is one; the things to learn are two. `played` becomes
**two entries** — *past tense of "play"* / *(الماضي)* and *past participle of
"play"* / *(التصريف الثالث)* — each addable, schedulable and practisable on its
own, and each carrying its label into everything downstream: the search list, the
learner's vocabulary, the spelling clue, the answer options, and the definition
the AI is given, which is how the tutor knows to steer a `played`-the-participle
session towards "I have played".

Three verbs in a hundred are not like that, and claiming both roles for them
would teach something false:

* **The participle alone** — `beat / beat / beaten`. The past is the word
  itself, so `beaten` is never the past tense.
* **The past alone** — `run / ran / run`. The participle is the word itself.

Neither can be read from the data: the missing form *is* the base, so there is
nothing for WordNet to list. Of the 975 verbs with a single listed form, 35 are
ambiguous by shape and they are enumerated by hand — 27 participle-only, 4
past-only, and the rest (`won`, `spun`, `shone`) genuinely serve both.

**And the importer now removes what it no longer produces.** It upserted by sense
id and never deleted, so the first cut of this shipped `beaten` labelled as a
past tense and the corrected run left the wrong row sitting in the table. A row a
learner has already added is kept whatever the rules now say — their vocabulary
is theirs, and it copied what it needed when they added it.

**On the future tense:** English has no future *form* to add. "will play" is
`will` + the base verb, two words, and `will` is already in the lexicon as a
modal (ADR-033). The base entry is what a learner practises; the future belongs
in what a session *asks* — a tutor can require it — not in the dictionary.

**Consequence.** The lexicon goes from 216,208 rows to 234,359. Verified against
the running stack: `played` and `won` offer both roles, `beaten` only the
participle, `ran` only the past — and a writing answer of "I have played football
since I was a child" comes back judged as correct use of the perfect tense.

---

## ADR-047 — The passage is told what shape the word is in
**Date:** 2026-08-20 · **Status:** Accepted · **Extends ADR-045, ADR-046**

**Context.** Once a form is a vocabulary item of its own, "use this word in the
passage" stops being a complete instruction. A learner practising `played` as the
past participle is learning "I have played"; a passage that writes "play" has
tested something they did not ask for. And a learner who added `mouse` has not
been taught `mice` — that is a different entry, with its own pipeline.

The other half is the opposite problem: for a noun whose plural is just an `s`,
insisting on the singular teaches less than allowing either. `book` and `books`
are one word to a learner, and meeting both is worth more than meeting one.

**Decision.** Reading and Listening are the two skills that put a word inside
real language, so they are the two told what shape it may take. Each target word
now arrives with its shape:

| The word | What the passage is told |
|---|---|
| `played`, the participle | *use exactly "played" — it is the past participle of this verb* |
| `book` (regular plural) | *use "book" or its plural, whichever the sentence wants* |
| `mouse` (plural is `mice`) | *use exactly "mouse"* |
| `mice`, `went`, any form | *use exactly, it is the form they added* |

Whether a plural is regular is not guessed: the lexicon is asked whether it
carries a differently-spelled plural for that word (ADR-045 put them there), once
per session. The form is not stored twice either — it is read back from the sense
id the word was copied from, which already ends in `#pst`, `#pp`, `#ing` or
`#pl`.

**Active vocabulary is reused under the same rule.** A word the learner knows as
`gone` comes back as `gone`, not `go`.

**Spelling is deliberately untouched.** It asks for the word exactly as the
learner added it and nothing else: it is the one skill where the spelling *is*
the question, and offering a variant would be asking about a word they did not
choose.

**Consequence.** Verified against real Gemini. A passage given `played` (the
participle), `book` and `mouse` produced *"Many teenagers **have played**
competitive video games…"*, *"…reading a physical **book**"* and *"…move a small
plastic **mouse** across the desk"* — with `mice` absent. Across three runs of
`book` + `child`, the passage chose `books` once and `book` twice, and never
wrote `children`.

---

## ADR-048 — A conversation ends in coaching, not a verdict
**Date:** 2026-08-20 · **Status:** Accepted · **Completes ADR-019, ADR-040, ADR-041**

**Context.** Speaking judged every word at the end of the conversation, and the
judgement carried, per word, what the learner said, whether it was right, and
what they should do about it. All of that was used to decide pass or fail and
then **thrown away**. What reached the learner was a tick or a cross. A learner
who fails a word and is not told why has learned that they failed, and nothing
else — which is the opposite of the point.

A second fault sat underneath it. Whether a word had been used was asked of the
model on every turn, and the model sometimes answered "none" for a message that
plainly contained the word — so the tutor asked again for a word the learner had
just said.

**Decision.**

**The end of a conversation is a lesson.** Every word comes back with:

* **what happened** — two or three sentences addressed to the learner: what they
  did with the word, and, when it was wrong, *why* it was wrong and that the
  word returns another day so nothing is lost;
* **their own words**, quoted, so the advice has something to point at;
* **one sentence to copy**, in English, using the word correctly — their own
  repaired where it can be, otherwise a model built from what they were talking
  about.

All of it in the language they read the app in (ADR-035), pitched at their level.
The model sentence is a *required* field: a learner told what was wrong and not
shown what right looks like has been marked, not taught.

**And the turn count no longer depends on the model.** The transcript cannot be
wrong about whether a word appears in it, so the transcript settles that. The
model is asked only the one thing the text cannot say — whether a word that
appears was being *named* rather than used ("let me use hook in a sentence") —
which is a small, reliable judgement and is usually an empty list.

**One conversation at a time.** A session left mid-way is resumed, not replaced,
even when the learner adds words while they are away — those words wait for the
next conversation rather than appearing in the middle of this one (ADR-039). A
completed session gives way to a new one, which is what makes a second day of
practice possible.

**Consequence.** Verified end to end against real Gemini, with one word used well
and one misused:

> **hook** — *"لقد استخدمت كلمة hook بمعنى غير صحيح هنا… سنقوم بمراجعة هذه الكلمة
> في درس آخر، فلا تقلق."* · say it like this: *"I hung my coat on the hook by the
> door."*
>
> **loop** — *"لقد استخدمت كلمة loop بشكل ممتاز وصحيح تماماً… ولا تحتاج إلى أي
> تعديل."*

---

## ADR-049 — Saying the word, and saying a different word
**Date:** 2026-08-20 · **Status:** Accepted · **Refines ADR-048, applies ADR-045/047**

**Context.** A turn decided the learner had used a word if the transcript
*started with* it anywhere — a prefix test. That answered two different
questions wrongly at once.

It said **yes** too easily: `booking a flight` and `the bookshop` both passed
`book`. A learner who has not used the word is moved on from it and never
practises it.

And it said **no** where a teacher would say yes. The tutor asks *"how many
books do you read?"*; the learner answers *"three books"*; the tutor asks for
`book` again. To the learner the app has stopped listening — and they are right,
because `book` and `books` are one word to them.

**Decision.** A word is said when the transcript contains it **as a whole word**.
Punctuation and spacing are separators; a multi-word entry (`go back`) is matched
as the phrase it is.

**A regular plural is the same word.** If the entry is a plain noun whose plural
is the word plus `s`/`es` (or `y`→`ies`), the plural counts and the conversation
moves on. This is decided from exactly the fact the passage generator already
uses before it writes a plural (ADR-047) — one question about the word's shape,
answered in one place, `WordForms.MayPluralise`.

**Anything else is a different word.** `mice` is not `mouse`, `children` is not
`child`: those are entries of their own with their own pipelines (ADR-045), and a
learner practising one has not practised the other. Neither is another tense —
`researched` and `researching` are not `research`, and a verb is never
pluralised.

**One spelling is one thing to say.** A learner may hold `book` twice, the object
and the verb (ADR-046). A transcript cannot tell two senses apart, so a turn
treats the spelling as one thing: said once, reported once, and both senses move
on together. (Found by this change — looking each word up by name crashed the
turn with a duplicate key for exactly this learner.)

**Consequence.** Verified live against real Gemini on an account holding both
senses of `book` plus `went`:

| the learner says | the target | counted |
|---|---|---|
| "I am **booking** a flight to Cairo" | book | no — the tutor asks again |
| "I read three **books** every week" | book | yes — both senses move on |
| "I **go** to the sea every summer" | went | no |
| "Last summer I **went** to Aden" | went | yes |

Ten test theories cover the rule, including `bookshop`, `mice` for `mouse`, and
the two-senses turn that used to return a 500.

---

## ADR-050 — A conversation is about its own words, and names the form it wants
**Date:** 2026-08-20 · **Status:** Accepted · **Fixes ADR-039 in Speaking, extends ADR-047/049**

**Context.** Reported from the device: a Speaking session announced its words
and then asked about something else entirely — `went`, `went`, `decrease`, and a
turn later `loop`. It looked like a corrupted account. It was not: the account
was intact and the session record was correct. The **turn** was wrong.

Every other part of a session reads the words the session recorded when it
opened (ADR-039). The speaking turn alone re-read the queue:

```csharp
var words = await db.Words.Where(w => w.CurrentSkill == SkillType.Speaking)
```

So a conversation about two words was held about twelve — every word parked at
Speaking joined it, including words already passed and words the learner added
while the conversation was open, which ADR-048 says must wait for the next one.
The screen promised two words; the tutor asked for a dozen.

A second gap sat beside it. `went` is the past tense of `go` and an entry of its
own (ADR-045), but the tutor was handed a list of bare words. It could not ask a
question that needs a past tense, and when the learner reached for it and said
"I **go** to my village every year", all it could say was *try to use the word
went* — which is the one thing they had just tried to do.

**Decision.**

**A conversation is about the words it was opened for.** The turn loads the
session's own record, like every other reader. Words that arrive at Speaking
meanwhile wait for the next conversation.

**The tutor is told what each remaining word is** — the past tense, a plural, or
the plain word — the same shape fact Reading uses before it writes a sentence
(ADR-047). A question that invites the wrong form cannot be answered with the
word being practised.

**And a near miss is named, not repeated.** When the learner says another form
of the same verb, the tutor says which step they missed and lets them try the
same idea again: *"Almost — I need the past tense: 'went'."* It does not move to
another word, and it does not turn it into a correction — they nearly had it.
Which forms belong to one verb is not guessed: every entry carries the lemma it
was built from, so `go`, `went`, `going` and `gone` are one family in the
lexicon already.

Nothing about passing changes. Using the word is still what moves the
conversation on, wrong grammar and all (ADR-041); how well it was used is still
judged once at the end (ADR-019, ADR-048).

**Consequence.** Verified end to end on a **new account** against real Gemini —
warm-up, greeting, both words, closing, assessment:

> **warm-up** — `book` and `went`, four shuffled meanings each.
> **tutor** — "Did you read an interesting book today? Try to use the word 'book'."
> **learner** — "I **go** to my village every year with my family."
> **tutor** — "Visiting your village sounds lovely. **Almost — I need the past
> tense: 'went'.** Can you tell me what you did when you went there?"
> **learner** — "Last year I **went** to my village…" → `went` passes.
> **learner** — "I read three **books** every week…" → `book` passes (ADR-049).
> **closing** — "You handled the past tense word went perfectly today… hope you
> have a wonderful rest of your day." No further question.
> **assessment** — per word, in Arabic, with the learner's own sentence quoted
> and one English sentence to copy.

---

## ADR-051 — What happens when a thousand people arrive at once
**Date:** 2026-08-20 · **Status:** Accepted

**Context.** The service had never been asked to serve more than one person. The
question was whether it would survive a real audience, so it was measured rather
than reasoned about: `ab`, a local instance, real PostgreSQL.

It did not survive. At **200 concurrent readers** the database answered
`53300: remaining connection slots are reserved for roles with the SUPERUSER
attribute`, and the learner got a 500. The cause is arithmetic: PostgreSQL
accepts 100 connections, Npgsql's pool defaults to 100 per instance, so a single
instance can claim the entire server — and a second instance, a migration, or a
person with `psql` then gets an error rather than a queue.

Three more faults sat behind it:

* **Sign-in could exhaust the machine.** Argon2id costs 19 MiB and a core per
  verification — deliberately, that is what makes a stolen database expensive to
  crack. A thousand at once asks for 19 GiB. Rate limiting does not see this: it
  limits one caller, and a crowd is a thousand callers.
* **A slow model was an outage everywhere.** Every request waiting on Gemini
  held a thread, a database connection and its memory for the whole wait, with
  no ceiling. A bad minute at the provider would take down signing in and the
  word list, which never call it.
* **The AI service miscounted its own cost.** Token totals were left in a
  module-level global for the endpoint to read afterwards. The endpoints are
  synchronous, so Starlette runs them on a thread pool: two learners arriving
  together read each other's numbers. The token count is the experiment's own
  measurement, and it was quietly wrong the moment there was more than one user.

**Decision.** Bound what the process consumes, rather than hoping the arrivals
are few. Every ceiling is configuration, not a constant (rule R3), because the
right number depends on the machine and on how many instances share the
database.

* **Database.** An explicit pool well below PostgreSQL's limit, so waiting
  happens inside this process where it is cheap and ordered. Pooled contexts,
  and transient failures — a blip, a failover — retried instead of becoming a
  500.
* **Password hashing.** One hash per core, the rest queued, and a queue that
  grows too long answered as *busy* — never as a wrong password, because nothing
  was checked. The wait is **asynchronous**: the first attempt blocked a thread
  per waiter and cut sign-in throughput from 97 requests a second to 48 while
  starving everything else in the process. That is in a test now.
* **AI calls.** A shared ceiling on calls in flight, and past it a fast 503 with
  the session untouched. Deliberately *not* the deterministic fallback: that
  exists for an AI that answered badly, not for a queue that clears in seconds.
  The AI service enforces its own ceiling too, and runs multiple workers.
* **Kestrel.** Bounded connections and request bodies.
* **Readiness** reports free AI slots, so saturation is visible before learners
  find it.

**Consequence.** Measured on the same machine, before and after:

| | before | after |
|---|---|---|
| 200 concurrent readers | **3 × 500**, p99 402 ms | **0 errors**, p99 111 ms |
| 500 concurrent readers | — | 0 errors, 4 508 req/s, p99 196 ms |
| **1 000 concurrent readers** | — | **0 errors, 5 490 req/s, p99 208 ms**, slowest 291 ms |
| sign-in, 50 concurrent | 100 req/s, slowest 2 268 ms | 97 req/s, slowest **1 105 ms** |
| sign-in, 200 concurrent | unbounded memory | 110 req/s, 10 hashes in flight |

Six tests pin the properties: neither ceiling is exceeded, an overflow is
refused rather than queued for ever, no slot leaks, and waiting does not consume
threads.

**What this does not solve.** Gemini's own quota. A thousand people starting
sessions in the same minute is a thousand model calls, and no amount of local
capacity changes what the provider will accept — the ceiling turns that into
"try again in a moment" instead of a collapse, which is the honest best the
service can do alone. Beyond one instance, the next steps are horizontal: more
instances behind a balancer, a connection pooler such as PgBouncer in front of
PostgreSQL, and a shared cache for the rate limiter so budgets are per learner
rather than per instance.

---

## ADR-052 — A number on a dashboard has to say what it counts
**Date:** 2026-08-20 · **Status:** Accepted

**Context.** Reported from the dashboard: *"24 sessions · 11 passed · 4 failed —
those don't add up."* They do not, and they never could: a session covers
several words, so the first number counts sessions and the other two count
words. The figures were right; the line invited a sum that does not exist.

Auditing every figure against independent SQL found the label was the smallest
of five faults. Most of the dashboard was exact — learner count, activity,
words, pipeline completion, per-skill sessions and outcomes, level histograms,
interests, the per-word journey, all matched to the row. These did not:

* **First-attempt accuracy divided words by attempts.** The numerator counts
  words that passed without ever failing; the denominator counted pass and fail
  *events*, so a word that failed twice inflated it by two while the numerator
  could not move. Speaking read **67%** where the answer is **86%**. The tooltip
  already described the correct maths — "share of words passed without needing a
  retry" — which is how the mismatch was visible at all.
* **An Owner's own testing was reported as how learners are doing.** The
  denominators counted `Role = User`; the numerators counted everybody. On the
  real database that was 47 of 288 sessions, 25 of 184 words and **114 of 392
  skill decisions** — a developer's afternoon of trying the product, mixed into
  the audience's results and inflating sessions-per-learner by 19%. The learner
  list called its total "learners" too, so one screen said 180 and the other 185.
* **The learner list always read "0 sessions".** The client had drawn that
  number since it was written; the server's summary record never had the field.
  Absent key, default zero, no error anywhere.
* **"Last active" was last *login*.** Fifty-four accounts had done real work and
  never signed in a second time, so they showed no activity date at all and
  sorted to the bottom as though they had gone quiet. The overview had already
  moved to the activity log for exactly this reason (§34–§35); the list had not.
* **"Today" was a UTC day.** Observed on a live account: six words added after
  midnight local time, reported as **0 today**. For an audience three hours ahead
  of UTC, every morning until 3am belonged to yesterday.

**Decision.**

**Say the unit.** The per-skill line now reads *"24 sessions · words: 11 passed,
4 failed"*, and a learner row *"… 3 sessions done"*. The list counts **accounts**,
because it contains the Owner's own; the overview counts **learners**.

**Divide like by like.** `wordsDecided` — distinct words a skill has passed or
failed at least once — is sent alongside, and first-attempt accuracy is measured
over it.

**Every per-learner figure counts learners.** Owner accounts are excluded from
sessions, words, events, activity, levels and interests, so the dashboard
describes the audience and not the person watching it.

**The day belongs to the learner.** One product-wide offset, configuration and
not a constant (rule R3), defaulting to +3. Returned in UTC, because
PostgreSQL's `timestamptz` parameters accept offset zero and nothing else — a
value carrying +03:00 is refused at the driver, which is a 500 rather than a
wrong number, and was exactly the first thing this change did.

**Report the typical session, not the mean.** Sessions are resumable by design
(ADR-039), so one finished the next morning is a duration in hours: median 16
seconds against a mean of 45 minutes, longest 46 hours, eleven sessions out of
288 moving the headline by a factor of 170. The median is shown — formatted as
minutes and seconds rather than "2716s" — and the mean is kept in the payload,
because a widening gap between them is itself the signal that sessions are being
abandoned and resumed.

**Consequence.** Re-audited after the change against independent SQL, figure by
figure, and every one matches: learners 180, words 159, sessions 241,
sessions-per-learner **1.339** (was 1.6), per-skill sessions/passed/failed/decided
exact, median 14 507 ms exact. Fifteen cross-screen consistency checks pass —
first-attempt never exceeds words decided, words decided never exceeds attempts,
the two screens agree on what they count, and every row carries the field the
screen draws. Six new tests pin the properties, and one widget test asserts what
the numbers on screen actually say. 94 + 317 backend, 259 Flutter.

**Not changed.** `signInCount` reading 2 965 for one account is genuine — load
testing wrote every one of those sign-ins. The figure is correct; it simply
records what happened.

---

## ADR-053 — A learner needs a way to reach a person
**Date:** 2026-08-20 · **Status:** Accepted

**Context.** There was no way for a learner to report anything. No address in
the app, no support screen, no form — a learner who hit a bug could tell nobody.
The realistic outcome of that is that they stop using it and the reason is never
known, which for an experiment whose entire purpose is measurement is the worst
result available: silent, uninstrumented failure.

The second half of the same problem sat in the dashboard. The Owner could read
everything about what a learner *did* and had no way to contact them about it.

**Decision.**

**Learners write; only the Owner reads.** A "Message the team" card in Settings
opens one field and a Send button. There is deliberately no call anywhere that
returns feedback to a learner — not even their own — so no client change can
leak one learner's words to another. The Owner's inbox is a fourth tab in the
dashboard: unhandled first, newest first, which is the order the screen exists
for.

**The message carries what makes it useful.** Who wrote it, their email, their
phone, and the build they were running — sent by the client rather than asked
of the learner, because "it crashed" is twice as useful with a version beside
it and nobody reporting a crash should have to go looking for one.

**Handled is reversible.** An Owner reading a long list will mark the wrong one
eventually, and a message that cannot be un-handled is lost. Handled messages
fade rather than disappear, for the same reason.

**Contact details are on the learner's page, and copyable.** Email and phone,
Owner-only, tap to copy — because the next thing after reading a report is
reaching the person, and gathering numbers for a group is the same act. Copy
rather than a `tel:` link: the Owner is usually building a list, not placing a
call, and nothing is copied without a tap.

**The text is data, never instructions.** It is stored as written, rendered as
text, and interpreted by nothing — no markup, no links, no HTML on the path. It
is bounded at 4 000 characters at both the client and the server, and stripped
of the control characters PostgreSQL refuses (ADR-036). Sending is rate-limited
with the expensive endpoints: it costs nothing to serve, but an unbounded write
of free text is how a table fills up overnight.

**The event is logged; the words are not.** `FeedbackSent` joins the activity
log so "did anyone report anything the day it broke?" is answerable from the
same trail as every other question (ADR-025) — without copying free text into a
log that deliberately holds none.

**Consequence.** Verified live against the real stack, in Arabic:

> **سالم المتعلم** · fb-…@wordos.test · **+967770112233** · ios 1.2.0
> *"قسم التحدث يتوقف عندما أضغط على الميكروفون. جربت ثلاث مرات."*

The learner's own call to read the inbox returned **403**, an unauthenticated
send **401**, an empty body and a NUL byte **400**, and 5 000 characters **400**.
Marking handled moved the unread count 1 → 0 and back. Nine backend tests and a
widget test that walks the whole path — learner writes, signs out, Owner signs
in, reads it, marks it, un-marks it. 94 + 326 backend, 260 Flutter.

---

## ADR-054 — A phone number is required to create an account
**Date:** 2026-08-20 · **Status:** Accepted · **Completes ADR-053**

**Context.** Feedback gave learners a way to reach the Owner and the Owner a way
to reach them back — but only for the learners who had happened to fill in an
optional field. A bug report from an account with no number is a problem nobody
can follow up, which is most of the value of collecting it in the first place.

**Decision.** Registration requires a number. Existing accounts are untouched:
this is a rule about *creating* an account, not about having one, and 102
accounts on this database have no number and continue to sign in and work
exactly as before.

Enforced in three places, deliberately:

* **The form** refuses to submit and says why, in the learner's language.
* **The endpoint** validates shape and returns `INVALID_PHONE`.
* **The domain** refuses to construct a `User` without one, so a future caller
  cannot create an unreachable account by forgetting — the same two-layer rule
  the rest of this domain follows.

**Checked on the digits, not the input.** `()- ` is four characters the phone
field permits as separators and contains no number. It passed the shape check,
reached the domain, and the domain rightly threw — which arrived as a **500**.
Caught at the endpoint now, so an invalid number is a 400 that names the field.

**Consequence.** Verified live:

| sent | answer |
|---|---|
| no phone at all | 400 |
| empty number | 400 |
| no country code | 400 |
| `"()- "` | 400 `INVALID_PHONE` |
| `"12"` | 400 |
| `"77 011 2233"` | 200, stored as `967` / `770112233` |

Sixteen test fixtures across both suites had to start supplying a number, which
is itself the proof that the rule is enforced rather than merely written down.
Two widget tests cover the form — empty, and punctuation only. The mock backend
enforces the same rule, so development does not diverge from the server.
94 + 330 backend, 262 Flutter.

---

## ADR-055 — One tap to a person
**Date:** 2026-08-20 · **Status:** Accepted · **Beside ADR-053**

**Context.** Feedback (ADR-053) is a message left in a box: the learner writes,
the Owner reads it later. That is the right shape for "this word looks wrong"
and the wrong shape for someone stuck right now, who wants to talk to a person.

**Decision.** Settings carries **Contact support** beside the message box, and
it goes straight to WhatsApp — no dialog, no confirmation, no form. The learner
asked to talk to someone; a question in between is one they did not ask for.

The link is `https://wa.me/917558973719`, not the `whatsapp://` scheme:
`wa.me` opens the app when it is installed and falls back to the browser when it
is not, while the custom scheme needs an iOS entitlement and shows the learner
nothing at all when WhatsApp is missing.

**The number lives in the client.** It is not a secret — every learner who taps
the button sees it — and a chat link needs no server, no token and no round
trip. Routing it through configuration was considered and dropped: it would buy
the ability to change the number without a rebuild, at the cost of a button that
fails when the network does, for a number belonging to one person that changes
about never.

**A failed launch still gives them the number.** If nothing can handle the link,
a dialog shows `+91 7558973719`, selectable and copyable, forced left-to-right
so it does not read backwards inside the Arabic interface. A button that does
nothing is worse than no button.

**Consequence.** Three unit tests pin the link itself — that it is exactly
`https://wa.me/917558973719`, that the number carries no punctuation `wa.me`
rejects, and that the number a person reads matches the one the link dials,
since the two are written separately and can drift. A widget test taps the
button with the platform channel mocked and asserts the exact URL the phone is
handed. `url_launcher` added; no iOS entitlement needed, because the link is
plain HTTPS. 266 Flutter tests.

---

## ADR-056 — A word list has to say what each word is
**Date:** 2026-08-20 · **Status:** Accepted · **Completes ADR-045, ADR-046**

**Context.** Once every inflection became a vocabulary item of its own
(ADR-045), a learner's word list could hold `go`, `went`, `gone` and `going`
side by side — and showed nothing to tell them apart beyond the spelling. The
same for `book` the noun and `book` the verb (ADR-046): two entries, two
journeys, one word on screen.

Half the machinery was already there. `partOfSpeech` had been on the wire since
the beginning, and the form was derivable from the sense id. Neither reached the
learner.

Checking rather than assuming turned up a real defect underneath. The label
function covered the spelled-out names — `noun`, `preposition`, `auxiliary` —
but the importer writes short codes, and the live lexicon holds **`prep` 48,
`det` 37, `pron` 28, `conj` 20, `aux` 17, `modal` 10, `intj` 4, `part` 3**.
Every one of them fell through to the raw code, so a learner who added `can`
was shown the word **"modal"** in an Arabic interface. One account already
held such a word.

**Decision.**

**Every entry says what kind of word it is, and which form.** Under the word in
the list and as chips on its page: *فعل · الماضي* for `went`, *اسم* for `book`,
*فعل ناقص* for `can`.

**The form travels as a key, not a sentence.** `past`, `pastParticiple`, `ing`,
`plural` — the client says it in the learner's language (ADR-035), and the
server derives it from the sense id rather than storing the same fact twice
(ADR-045). A plain word carries no form label: nobody needs to be told that
`book` is `book`.

**Every code the lexicon actually stores has a label**, in both languages, short
form and long form agreeing — `prep` and `preposition` are the same thing to a
learner and must not read differently. An unknown code shows itself rather than
an empty chip, because a strange label is easier to report than a blank space.

**Consequence.** Verified live against the real dictionary:

| word | part of speech | form |
|---|---|---|
| went | v | past |
| played | v | pastParticiple |
| book | n | — |
| **can** | **modal** | — |
| under | prep | — |
| beautiful | a | — |
| quickly | r | — |

Eight new Flutter tests, three of which render the tile itself and assert what a
person reads — including that `modal` is drawn as *فعل ناقص* and never as
"modal". Seven backend tests pin the form key, including that an unrecognised
suffix produces no label rather than a wrong one. 94 + 337 backend, 274 Flutter.

---

## ADR-057 — The app's own mark, everywhere it appears
**Date:** 2026-08-20 · **Status:** Accepted

**Context.** The learner sees the WordOS mark — a white W on a violet-to-blue
gradient — the moment they open the app, and then sees Flutter's own logo on
their home screen, in the app switcher, and in every list of installed apps.
The identity ended at the sign-in screen.

**Decision.** The launcher icon is that mark, on every platform: iOS at fifteen
sizes, Android at five densities, the web icons and favicon, and the iOS launch
screen so the first frame is the brand rather than a white flash.

**Generated from the widget, not redrawn.** `tool/generate_app_icon.dart` is run
with `flutter test`, which is an odd thing to say about a generator — but it is
the only way to make Flutter's own renderer draw into a file, and any other
route means a second copy of the logo that drifts from the first. It reads the
same `AppColors.brand` → `AppColors.listening` gradient and the same
proportions, and writes every size in one pass. It lives in `tool/` rather than
`test/` precisely so an ordinary test run does not rewrite twenty-five files.

**The W is a path, not text.** The first render came out as a white rectangle on
a blue square: a test binding substitutes a placeholder font that draws every
glyph as a filled box. Drawn as a stroked polyline it needs no font at all, is
reproducible on any machine, and stays crisp at twenty pixels where a hinted
glyph would not.

Two more things the first attempt got wrong, both caught by looking at the
output rather than trusting it: every file came out **800×600**, the test
surface, because the capture is clipped to it — the surface has to be sized to
the icon; and `toImage` has to run inside `runAsync`, or the encode never
completes and the run dies on a ten-minute timeout.

**And the name beside it.** The icon said WordOS while the label under it said
`wordos` on Android and `Wordos` on iOS. Both now read **WordOS**, as does the
web manifest, whose theme colour is the brand rather than white.

**Consequence.** Verified by building the app and reading the icon back out of
`Runner.app` — not from the source folder, but from the bundle iOS actually
compiled. Android uses the classic launcher icon, so no adaptive foreground and
background pair is needed; the maskable web icons are padded, because a browser
crops them and an unpadded letter loses its corners.

---

## ADR-058 — Leaving a placement question has to end it
**Date:** 2026-08-20 · **Status:** Accepted

**Context.** Three reports from the device, which read as three bugs and were
one:

* answer a **listening** question, press Next, and the clip carries on talking
  over the question after it;
* answer a **speaking** question, press Next, and the microphone is still open
  on the next one;
* reach a **writing** question and the previous answer is still in the box.

The sessions had all of this fixed already — audio stops at every section
change, and the fields reset on advance. The placement test never received any
of it. Nothing on that screen was ever told a question had ended.

Underneath, one line: the answer widget was not keyed. Flutter matches widgets
by type and position, so moving to the next question handed the *same* `State`
to a new question — keeping its `TextEditingController`, its `_listening` flag,
and the open microphone behind it.

**Decision.**

**Every transition silences the question it leaves.** `_stopMedia()` — stop the
voice, cancel the microphone — on starting, on answering, on finishing, and on
leaving the screen. Disposal was missing entirely: the screen held no reference
to either service, so nothing could be asked to stop when it went. Both are held
as fields rather than read on demand, because `ref.read` during tree
finalisation throws and takes the frame down with it (the same fault that once
made signing out do nothing).

**Every question builds its own answer field**, keyed by its item id. That one
key resets the text, the microphone and the typing toggle together, because they
all live in the State the key replaces.

**And a late result belongs to the question it was spoken for.** Reported after
the first fix: the learner left the microphone open, pressed Next, and the
microphone had correctly stopped — but the box above the *next* question held
the previous answer. Cancelling the recogniser does not recall a result already
in flight; it arrives a moment later, and with nothing to say otherwise it
landed in whichever question was on screen by then. The answer handler now
captures the item it was built for and drops anything that arrives for a
question the learner has left.

That one only shows on a spoken question, because the transcript box renders
what the *screen* holds rather than a controller of its own — which is also why
the first attempt at a test for it passed without the fix: it happened to land
on a written question, where a stale answer is invisible.

**Consequence.** Four tests, each of which fails when its own fix is removed —
checked by removing them. The third asserts on the mechanism rather than the
symptom: it finds the keyed element and requires a different one after
advancing, so it holds whichever question type the adaptive engine happens to
serve.

Two things surfaced while writing them. The walk got stuck on listening
questions because the first `InkWell` on screen is the play button, not an
option — a test tapping "the first tappable thing" answers nothing. And an
earlier test typed the phone number into `TextFormField` index 3, which is the
password box: the country code is a picker, not a field. That test asserted a
missing-phone error and passed for the wrong reason — the phone was empty
because it was never filled in. Corrected to index 2.

---

## ADR-059 — A turn the learner can take back, and a result written to them
**Date:** 2026-08-20 · **Status:** Accepted · **Extends ADR-028, ADR-048**

**Context.** Two things, both about who the conversation belongs to.

The microphone was push-to-talk with one exit: tap to start, tap to send. A
learner who fumbled a sentence — started over, lost the word, said the wrong
thing — had no way out except to send the fumble and let the tutor answer it.
The one control had two jobs and no undo.

And the end-of-conversation summary was written *about* them. The per-word
feedback had been second person since ADR-048 — *"لقد استخدمت كلمة research
بشكل ممتاز"* — but the summary beside it read **"The learner successfully used
both target words in their responses."** Third person, and in English. It was
never described in the prompt at all, so the model wrote a report.

**Decision.**

**A bin beside the microphone, while there is something in it.** Tapping it
cancels the recording, clears what was heard, and offers the microphone again.
Nothing is read off the recogniser and nothing is sent, so no AI call is spent
on a sentence the learner had already decided against. It appears only while
listening: a bin beside an idle microphone offers to delete nothing, and a
learner reading it wonders what they are about to lose.

`cancel`, not `stopAndRead` — stopping asks the recogniser for its result, and
the point is that there is not going to be one.

**Everything the learner reads is written to them.** The summary is now
specified: two or three sentences addressed to the learner, in their language,
saying what went well and the one thing to work on. And the rule is stated once
for the whole prompt — *"You used `went` well"*, never *"the learner used"* —
because a result written in the third person reads like a file somebody keeps
on them.

**Consequence.** Verified live against real Gemini, in Arabic:

> *"لقد كان حديثنا ممتعاً اليوم يا خالد، وأنا سعيد جداً بمدى وضوح إجاباتك. لقد
> استخدمت كلمة research بشكل ممتاز، استمر في التركيز على اختيار حروف الجر
> المناسبة."*

Two tests: the bin cancels rather than reads, sends nothing, leaves the tutor
silent, clears the words from the screen and lets the learner record again; and
no bin is offered before they have spoken. 94 + 337 backend, 280 Flutter.

---

## ADR-060 — The backend banner comes off the sign-in screen
**Date:** 2026-08-20 · **Status:** Accepted · **Reverses part of ADR-039's fix**

**Context.** Debug builds printed the API address on the sign-in screen. It was
added for a real reason: an Xcode build silently lost its `--dart-define`
values, fell back to the in-app mock, and looked like a working app — the demo
account signed in and a real one was refused as "wrong email or password". The
cause was invisible from the screen, so it was made visible.

**Decision.** Removed, at the product owner's request. It is the first thing
anyone sees when they open the app, and a URL sitting above the password field
is developer furniture in a learner's way.

The problem it solved has a better answer that costs the learner nothing:
`./wordos status` reports the configured address and warns when it is no longer
this Mac's, which is the check that actually matters and the one place a person
looks before pressing Run.

**Consequence.** Release builds never showed it, so nothing about a shipped app
changes. `kDebugMode` and the widgets library are no longer imported by the
sign-in screen.

---

## ADR-061 — The database is reset for the MVP, and the Owner is re-made
**Date:** 2026-08-20 · **Status:** Accepted

**Context.** Everything in the development database was test data: 193 accounts,
of which five were Owners, produced by feature work, load tests and end-to-end
runs. None of it belonged to a real learner, and all of it was distorting the
dashboard — the figures that led to ADR-052 were mostly a developer's afternoon.
The Owner account also carried a password chosen for convenience.

**Decision.** Every account deleted, and one new Owner created.

**Deleted by deleting the accounts, not the tables.** A single
`delete from users` — everything a learner owns hangs off it by a cascading
foreign key. That is also a test of the cascades: seventeen tables emptied
themselves, and one that had not would have been a missing constraint worth
knowing about. None was missing.

**The lexicon is not anybody's data** and was left alone: 234,359 entries,
reference material the importer builds and no learner owns. It was excluded from
the backup for the same reason.

**A backup first.** `pg_dump` of everything except the lexicon, kept outside the
repository. The instruction was explicit and the data was disposable, but a
delete with no way back is worth ten seconds of insurance.

**The new Owner is registered through the API, then promoted in SQL.** Not
inserted directly: registration is what hashes the password with Argon2id, seeds
the five skill-level rows, and writes the `Registered` activity event. Promotion
has to be SQL because there is deliberately no client-reachable path to Owner
(docs/07-SECURITY.md §3) — which the run confirmed by returning `role: USER`.

**The password is not in this repository.** It was supplied by the product
owner, sent once to the API in a file outside the repo, and that file deleted.
What is stored is an Argon2id hash. `CLAUDE.md` names the account and explicitly
does not name its password.

**Consequence.** One account, role Owner, signing in and reaching every admin
endpoint (200), refused with the wrong password (401). The dashboard reads zero
across every figure, which is now true. The lexicon still answers — `went`
resolves to its two Arabic senses.

---

## ADR-062 — One image, two processes, and no schema rights
**Date:** 2026-08-20 · **Status:** Accepted

**Context.** The MVP goes out on free hosting: Neon for PostgreSQL, Render for
the service, for about a hundred learners. Nothing in the repository described
how to deploy anything — no Dockerfile, no compose, no document.

Sizing was measured rather than argued about. The published API is **160 MB**
idle and **192 MB** under a hundred-concurrent burst, serving 5,166 requests a
second with no errors; the AI service with one worker adds about **40 MB**. That
is **~232 MB against Render's 512 MB**. The database is **187 MB**, of which 175
is the lexicon.

**Decision.**

**One container runs both services.** They are separate everywhere else in this
architecture, and here they are not, for three reasons that all point the same
way:

* Render's free plan is measured in instance-hours. Two always-on services spend
  the month's allowance in half a month.
* The AI service holds the Gemini key. As a second Render service it would have
  a public URL guarded only by a shared token; in this container it binds to
  loopback and cannot be reached from outside at all — a **stronger** boundary
  than the one it replaces.
* One cold start rather than two.

Splitting them again is a deployment change, not a code change: the API finds
the AI service at `AiService__BaseUrl`.

**The container dies when either process does.** A container still running with
half its services up passes the platform's health check while every lesson
fails, and nothing says why.

**Migrations are not applied at startup.** This was tried, and PostgreSQL
refused it — correctly. The service connects as `wordos_app`, which holds no DDL
rights; owning the schema is `wordos_migrator`'s job (docs/07-SECURITY.md §10).
Making startup migration work would mean giving the internet-facing process a
credential that can drop every table, to save a command run once per release.
Migrations are applied from a developer's machine with the owner role before the
deploy, and the deployment guide says so at the point where it matters.

**Two more things caught by checking rather than assuming.** The configuration
section is `AiService`, not `Ai` — the first draft of the Dockerfile named the
wrong one, which binds to nothing, leaves the service token empty, and has every
AI call refused with nothing at startup to explain it. And `curl` is not in the
ASP.NET runtime image, so the entrypoint waits for the AI service with bash's own
`/dev/tcp` rather than installing an HTTP client to check that a port is open.

**Consequence.** `Dockerfile`, `docker/entrypoint.sh`, `.dockerignore` and
`docs/09-DEPLOYMENT.md` — the last written for someone who has used neither
platform, with the click path for both and the two-role SQL for Neon.

**Not verified.** There is no container runtime on this machine, so the image has
never been built. What *was* verified is everything the image assembles: the
published API runs and serves under load at the memory quoted, the AI service
runs from a virtual environment, the entrypoint parses, every configuration key
matches the code that reads it, and `wordos_app` is refused when it attempts
`CREATE TABLE`. The first `docker build` is Render's, and its log is the place a
mistake will show.

---

## ADR-063 — Starting a session is a claim, not a check

**Date.** 2026-08-21 · **Status.** Accepted · **Supersedes nothing.**

**Context.** An audit of the whole service found three defects that share one
shape: a rule the code believed it enforced, enforced by a look rather than a
lock.

`StartAsync` opened with "is there an open session for this skill?" and, if not,
generated a passage and saved one. Between the look and the save sits a gap
wide enough for a second tap. Measured with four simultaneous starts: **four
sessions, four Gemini calls, 14,366 tokens where 3,679 was the whole
requirement.** The invariant the codebase already had a test for — *starting a
second session while one is open resumes rather than forks* — held only because
the test was sequential. The trigger is not an attack; it is a double-tap on a
slow connection.

The same shape, elsewhere: `POST /auth/register` and `POST /words` both checked
for a duplicate and then inserted. Six concurrent registrations of one email
produced one `200` and five `500 INTERNAL_ERROR`. The database was never wrong —
the unique index held every time — but the *answer* was, and a learner who
double-tapped Register saw "something went wrong", retried, and got
`EMAIL_TAKEN`, which reads like a broken account.

**Decision.** The database settles these races, because it is the only
participant that sees every request.

1. A **filtered unique index**, `ix_skill_sessions_one_open_per_skill`, on
   `(UserId, Skill) WHERE "IsComplete" = false`.
2. The session row is written **before** the content is generated, not after.
   This is the part that saves the money: the losers are turned away in a
   millisecond having spent nothing, and are handed the winner's session — which
   is what they were asking for. Saving afterwards would let all four generate a
   passage and only then discover that three of them lose.
3. A uniqueness violation is caught and answered as the pre-check would have
   answered it — `SESSION_RACE`, `EMAIL_TAKEN`, `WORD_ALREADY_ADDED`. Matched on
   the index *by name*, so an unrelated violation still surfaces as the fault it
   is. The pre-checks stay: they are the fast, common path and give the better
   message.

**Cost.** Claiming first means a row can exist for a session whose content never
arrived — the AI gate refusing at capacity, or a restart in between. Such a row
is recognisable (no passage, no conversation, no items) and is cleared by the
next start; the hub does not offer it as `activeSessionId`, so nobody is sent to
an empty screen in the meantime. That is a better failure than the one it
replaces, and unlike a `try`/`catch` around the generation it also survives the
process being killed.

**The cleanup needed an age, and finding out cost a test.** A session being
built *right now* is indistinguishable by inspection from one whose build died —
both have no passage, no conversation and no items. The first version cleaned up
on that shape alone, so a second request arriving mid-generation deleted the
first request's row, claimed the skill, and the first then failed saving content
into a row that was gone: **one start in six answering 500**, found by the very
test written to prove the claim works, and only under full-suite load. Cleanup
now also requires the row to be older than `SessionBuildGraceSeconds` (120),
comfortably beyond the 25-second AI budget. A request landing inside that window
is answered `409 SESSION_STARTING` rather than handed the empty row.

The lesson is the ADR's own: this was a check-then-act race introduced while
fixing check-then-act races, and it was invisible in isolation — the test passed
eight times out of eight on its own.

**Migration.** Any database written before this may already hold duplicates, and
PostgreSQL will not build a unique index over them. The migration resolves them
first, keeping the session with the most *attempted* items — the one holding
answers somebody actually gave — and only falling back to the oldest when none
of them was answered.

**Also settled here, from the same audit.**

- **`abandon` on a finished session is refused.** It was the one route that did
  not go through `LoadSessionAsync`, so it alone skipped the `IsComplete` check —
  and obeying it *deleted the row and its items*. The word outcomes had already
  been applied, so no learner's progress changed; what vanished was the
  comprehension score, the token cost, the prompt version and the level used.
  This service exists to measure its own algorithm, and a client firing abandon
  on dispose was quietly deleting the measurement.
- **`complete` no longer fails words it never asked about.** Completing with the
  queue still full ran every untouched word through the state machine as a
  failure: eight words came back `FAILED` with `attemptsInSession: 0` and a
  two-day wait, having been shown to nobody. They are now returned `untouched`
  and left exactly as they were. Speaking is included: a conversation the learner
  never spoke into tested nothing.
- **Practice gives way, and expires.** A practice session is resumed rather than
  replaced, so yesterday's half-finished practice was what a learner received
  when they came back and asked for today's real words — with no way past it but
  to finish or abandon it. It now stands aside when real words are due, and is
  dropped after `PracticeSessionExpiryHours` (24). Deliberately *not* dropped
  when nothing is due: being told there is nothing to do must not also cost the
  learner their place. Real sessions never expire on age — they hold answers.

**Two settings that were one setting in two places.** The backend's AI timeout
and the Flutter client's receive timeout were both 90 seconds. With the AI
service hung, the backend was measured answering at 90.08s while the client gave
up at 90.00s: the learner saw "the server took too long" and the whole
`ResilientAiContentService` fallback — which had worked perfectly — was never
seen by anybody. The backend budget is now 25 seconds, leaving the client over a
minute of headroom. A healthy generation takes eight to nine.
