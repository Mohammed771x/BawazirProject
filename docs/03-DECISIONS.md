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
---

## ADR-064 — Reading answers in two steps, and the passage stays reachable
**Date:** 2026-08-23 · **Status:** Accepted · **Reading only**

**Context.** Two complaints from the device, both about the Reading section.

A tap on an option *was* the answer: it went straight to the server and came
back marked. On a phone that makes a mis-tap indistinguishable from a decision —
the learner's thumb lands on the wrong line and the attempt is spent, with the
right answer already on screen. Nothing in the app measures intent, so the
client cannot tell the two apart after the fact.

And "I finished reading" was a one-way door. A learner who pressed it early, or
who reached a question about a detail they wanted to check, had no way back to
the text except to leave the session.

**Decision.**

- **Choose, then check.** A tap selects and sends nothing; the footer button is
  *Check* until the answer has been submitted, and *Next* / *Finish* after. The
  selection can be changed as often as the learner likes before it is checked,
  and it survives a failed submission, so a dropped connection is retried by
  pressing Check again rather than re-answering.
- **The passage can be reopened.** A book icon in the app bar puts the text back
  on screen; its button reads *Back to the questions* and returns to exactly the
  question that was open.

**Confined to Reading.** Listening shares this screen, and it does not get
either change: replaying the clip during the questions would be handing over the
answers, which is the same reason the audio stops when the questions start. The
other three skills are typed or spoken rather than tapped, and already have a
submit step of their own. The Weekly Review keeps auto-advancing — it is
measurement, deliberately quick, and ADR §9–§12 settled that.

**What did not change.** Nothing on the server, and nothing about the rules. The
selection is UI state held for the length of one question — R1 is untouched,
because the client still decides nothing about the answer; it only decides when
to ask. Progress, requeueing and the retry budget are read from the server's
reply exactly as before. Revisiting the passage is a view, not a phase: the
session's content phase stays finished, so the level stays locked and no answer
is thrown away.
---

## ADR-065 — The passage glossary is completed, not requested
**Date:** 2026-08-23 · **Status:** Accepted · **Extends ADR-029, ADR-039**

**Context.** A tap on a word in a Reading passage answers from the glossary the
generator wrote as it composed the sentence, and from nothing else. Where there
is no entry the client falls back to the lexicon — which returns every sense the
word has ever had. "bank" has six; five of them are wrong in the sentence in
front of the learner. The feature is either complete or it is not there.

ADR-029 introduced the glossary and ADR-039 fixed re-told passages by *sharing
the rule* between the two prompts rather than paraphrasing it. Both left the
same assumption in place: that asking clearly is enough. It is not. The model is
`gemini-3.1-flash-lite`, and on an A1 passage it glosses the interesting words
and skips `was`, `before`, `quiet` — which are precisely the words a beginner
taps. Nothing anywhere measured this. `ResilientAiContentService` validates that
a passage has sentences and questions; it has never looked at the glossary, so a
passage with one entry shipped exactly like a passage with eighty.

**Decision.** The AI service measures its own output and repairs it. After
shaping a passage it tokenises the text with the client's own word rule, lists
the words the glossary does not answer for, and — only if there are any — makes
one further call asking for those words by name, in their sentences. The entries
come back and are merged.

- **Named words, not the whole glossary again.** The words are known; only their
  meanings are missing. Re-generating everything costs more and can come back
  just as short.
- **One pass.** Not a loop: two calls is a bounded cost, and whatever a second
  attempt would still miss is a word the model cannot gloss.
- **Never fatal.** A failed repair returns the partial glossary. Refusing the
  passage would drop the learner into `FallbackContent`, which carries no
  glossary at all — strictly worse than the partial one already in hand.
- **The repair tokens are added to the passage's total**, because they were
  spent on this passage. A passage that needed no repair costs one call, as
  before.

**Measurement.** `ai-service/tools/glossary_coverage.py` asks for a real passage
at each of the eleven levels, tokenises it exactly as the client does, and
reports every word a learner could tap without getting an answer. It exits
non-zero when any level leaves one, so it is a check and not only a report.
Coverage varies with the text as well as the level, so it takes `--repeats`.

**Also found while testing the levels.** `_sentence_count` knew only the six
whole bands. The level arrives on the wire as `A1_PLUS`, so all five half-steps
missed the table and took the default of nine sentences — an A1+ learner was
being handed a B1-length passage, and a C1+ one a passage shorter than C1's.
Every band is now in the table.

**The outage path was the whole of what was reported.** Three separate faults
seen on a device — a four-sentence "B1" passage, "the passage could not be
rewritten", and a tap on `all` answering with every meaning it has — were one
fact: the AI service was not running, so every generation took
`FallbackContent`. That fallback ignored the level entirely and carried no
glossary at all, and re-levelling has no fallback by design. Two of those are
now fixed rather than explained:

- **The fallback is glossed.** Its sentences are fixed, so its glossary is
  written by hand and covers every word of them; the learner's own target words
  bring their Arabic with the request. A test asserts the two sets are equal, so
  a sentence added to the fallback without its words fails the build.
- **The fallback answers to the level.** Filler sentences scale from two at A1
  to six at C1+, from the same fixed vocabulary. A B1 learner handed four lines
  read that as a broken app and was right to.
- **The definition is no longer spliced into the passage.** It was arbitrary
  English from the lexicon, so every word of it was an unanswerable tap. The
  learner still meets it in the lookup sheet and in the question about the word.

Re-levelling keeps its refusal: a fallback re-telling would be worse than the
passage the learner asked to improve.

**Pinned across every band.** `Every_word_of_the_passage_can_be_tapped_at_every_level`
starts a Reading session over real HTTP against real PostgreSQL, then walks all
eleven levels by re-telling — which is how a learner reaches any band but their
own — and asserts after each that the passage's words and the glossary's words
are the same set. The test double now glosses whatever it wrote, for the same
reason: a stub that glossed three words let the suite pass on a passage whose
taps mostly could not be answered.

**Measured, against the real model.** 33 passages — every one of the eleven
levels, three topics each — through `gemini-3.1-flash-lite`:

```
level    words  glossed  missed
A1       43–53    same        0
A2       62–75    same        0
B1       80–95    same        0
B2       99–117   same        0
C1      121–148   same        0
C2      124–136   same        0        (all eleven bands, 3 runs each)
```

**Not one unanswerable tap in any of them** — and the repair pass is what did
it: it fired on **17 of 34** generations, each time short by one to four words,
and closed every one of them (`0 words still unglossed`, every run). So the
model alone leaves a hole in roughly half of all passages, which is exactly
often enough to look like "it works at some levels and not others" from a
device. `all` in an A1 passage now answers **كل** and nothing else. Re-telling
B1 → A1 comes back three sentences, 24 entries, zero missed.

**Consequence.** The client is unchanged: it was already correct, showing the
one contextual meaning when it has one and the lexicon when it does not. What
changes is how often it has one. The lexicon fallback stays as the last resort —
a word the model still cannot gloss is better served by an honest list of senses
than by a confidently wrong single meaning.
---

## ADR-066 — A passage is the length of the exam text it stands in for, and it has a title
**Date:** 2026-08-23 · **Status:** Accepted · **Reading first; Listening follows**

**Context.** Three faults in the same screen, reported from a device.

A C2 passage came back as a dozen sentences. Length was set in *sentences* from
a table nobody had ever checked against anything: six at A1, thirteen at C2. A
C2 reader given thirteen sentences is not doing C2 reading, whatever the
vocabulary in them.

The prompt opened with the learner's interests — *"Write a short passage at CEFR
level B1 about technology"* — so the topic was the first instruction the model
obeyed and the target words were bent to fit it. A learner whose word was `can`
(the tin) and whose interest was technology got the word forced into a
paragraph about programming: a sentence no writer would produce, teaching a use
of the word that does not exist. Some passages also announced the topic back at
the learner — *"Since you are interested in technology…"*.

And the passage began with no heading. Every reading task anyone has met in a
classroom or an exam is titled; prose that starts cold reads as an extract torn
out of something else.

**Decision — length is measured against real examinations.** Sources, and what
they gave:

| Source | Finding |
|---|---|
| A2 Key | no reading text exceeds **230 words** |
| B1 Preliminary | texts from ~150 (the gapped one) to ~300 |
| B2 First, Part 5 | a single text of **500–600 words** |
| C1 Advanced | **3,000–3,500 words** across the whole paper; the long Part 5 text ~700–800 |
| C2 Proficiency | longer and denser again than C1 |
| Sentence length | <10 words is A1–A2, 10–16 B1–B2, 16–23 B2–C1, >23 C1–C2 |

The ladder runs 80 words at A1 to 720 at C2, with every half-step in between,
and a sentence length that climbs with it. Listening takes 60% of its band's
length: heard once, with nothing to go back to (Part 2 §24).

**Asked for as a sentence count, not a word count.** A word count is not
something a model can hold itself to — asked for 720 words at C2 it returned
361. Asked for 29 sentences it returns 29. Counting is the instruction it can
follow, so the prompt names the count and gives the word total as the
consequence.

**Decision — the words come first and the interests yield.** The target words
and the natural-use rule now open the prompt; the interests appear last, marked
*lowest priority*, and the model is told in as many words that a passage on the
wrong subject is fine and a passage that misuses a word is not. It is also told
never to name the interests or explain its choice of subject. Verified on the
exact case reported: interest `technology`, word `can` (a tin) — the generator
dropped technology entirely and wrote *"I spot a metal can sitting on the shelf
behind the large jar."*

**Decision — the generator writes the title.** Three to eight words, the kind a
magazine prints, never naming a target word. It travels the whole way:
`skill_sessions.ContentTitle`, `content.title` in the API, and an `EnglishText`
heading above the passage. Null is legal and renders nothing — a session from
before this, or a model that omitted it.

**Three ceilings had to move with it, and each was measured first.**

- **`GlossaryJson` 20,000 → 120,000 characters.** A C2 passage has ~430 distinct
  words at ~70 characters an entry: 30,000. The old ceiling would have started
  truncating exactly at the top of the ladder.
- **The glossary repair works in batches.** One capped call was enough for a
  dozen sentences. At 750 words the model glossed 40 of 430 words, and a single
  repair capped at 200 left **227** unanswerable taps — the original bug back
  again at C2. Up to four batches of 150, stopping early when the passage is
  covered or when a pass adds nothing.
- **The AI budget 25s → 80s, and the client's receive timeout 90s → 120s.** A
  measured C1 passage takes 35 seconds to write and a C2 one 45. At 25 every
  band above B2 would have timed out into the fallback — which is the opposite
  of what that budget is for. They remain one setting in two places and they
  moved together.

**Measured, all eleven bands, two topics each:**

```
A1    84 words / 12 sentences /  8s      B2+  446 / 27 / 29s
A1+  108 / 12 /  9s                      C1   569 / 28 / 35s
A2   150 / 15 / 13s                      C1+  641 / 29 / 41s
A2+  187 / 17 / 17s                      C2   765 / 29 / 45s
B1   225 / 18 / 17s
B1+  283 / 21 / 20s                      unanswerable taps: 0, everywhere
B2   387 / 24 / 26s
```

**Consequence.** A session costs more: a C2 passage is ~15,000 tokens against
~5,000 before, plus its repair batches, and the learner waits 45 seconds rather
than 14. That is the price of the passage being the length it should always
have been. Listening keeps the same ladder at 60% and is otherwise untouched
until its own review.
---

## ADR-067 — The glossary is built in parallel, not inline and not in a queue
**Date:** 2026-08-23 · **Status:** Accepted · **Fixes a regression from ADR-066**

**Context.** Reading became slow the moment passages were sized like exam texts,
and changing level — the one thing a learner sits and waits for — began failing
outright with *"the passage could not be rewritten"*. Measured, from the
service's own log:

```
relevel B1 → C2   generation  8.6s   +  3 sequential repair batches  34s   = 43s
relevel B1 → C2   generation 43.2s   +  1 repair batch                3s   = 46s
                              ↑ the model wrote 430 glossary entries inline
```

Two separate costs, both introduced by lengthening the passage:

1. **Asking one call for the whole glossary.** At 750 words that is 430 entries;
   the model spent 43 seconds and 18,000 tokens producing them — or, on another
   run, gave up and returned 40, which is what made the repair pass necessary in
   the first place.
2. **Repairing in sequence.** Three batches at ~12 seconds each, waiting on one
   another for no reason: each batch asks about a different set of words.

Worst case those compose — 43 + 34 = 77 seconds — which crossed the backend's AI
budget and returned `RELEVEL_UNAVAILABLE`. The learner saw the app hang and then
refuse.

**Decision.**

- **Long passages are written without a glossary.** Above 260 words the prompt
  says so explicitly and the schema drops the field, so the model spends
  everything it has on the passage. Short ones keep the single call, which is
  still cheaper: one round trip.
- **The batches are issued together.** They never depended on each other. Up to
  six of 150 words, dispatched on a thread pool, merged when they return.
- **The budgets come back down**: the AI budget 80s → 60s and the client's
  receive timeout 120s → 90s, about three times the new worst case.

**Measured after, all of it end to end:**

```
fresh passage          before   after        re-telling from B1   before  after
A1                        8s      8s         → B2                   ~30s   16s
B1                       17s     19s         → C1                   ~40s   17s
B2                       26s     18s         → C2                 43–77s   22s
C1                       35s     20s
C2                       45s     21s         unanswerable taps: 0 in every one
```

Generation time is now roughly flat across the ladder — a C2 passage costs what
a B1 one does in wall-clock terms, because the part that grows with length is
the part that runs in parallel.

**Consequence.** Tokens are unchanged in kind and slightly higher in total: the
batches ask for the same entries, and a passage written without an inline
glossary re-sends its sentences with each batch. That is the trade — a little
more spent, less than half the time waited. The in-flight admission limit
(`WORDOS_AI_MAX_IN_FLIGHT`, 16) now covers repair batches too, so a burst of
long passages queues at the semaphore rather than at the provider.
---

## ADR-068 — A passage is built, not merely written; and a clip can be scrubbed
**Date:** 2026-08-23 · **Status:** Accepted · **Reading, then Listening**

### The passage has a shape

**Context.** Sized like an exam text (ADR-066), the passages read as one
undifferentiated block. Every sentence connected to the next, no sentence
announcing what a paragraph was for, and no ending — the text simply stopped.
That is what a model produces when it is asked for N sentences and told nothing
about their arrangement.

**Decision.** From **B1 upward** the prompt asks for the shape a TOEFL, IELTS or
Cambridge passage actually has: an opening sentence that says what the whole
text is about; two to four paragraphs, each opening with its own topic sentence
and supporting it; a close that lands the passage rather than stopping
mid-thought; and visible joins — *However*, *As a result*, *By contrast*.

Below B1 there is nothing to structure: eighty words of short declarative
sentences given a thesis and a conclusion is a parody of academic writing, not a
beginner's text. Those bands get a plainer rule — one connected little text with
a first sentence and a last one.

**Paragraphs travel as indexes, not as blank sentences.** The model reports
`paragraph_breaks` beside the array. Blank elements *inside* the array would
have shifted every `sentence_index` the model reports for its target words by
the number of breaks above it.

**Measured:** A1 stays one paragraph; B1 comes back with four; C1 and C2 with
six; coverage still zero unanswerable taps and timing unchanged.

### Listening gets what Reading got, and a scrubber

Title above the clip, choose → check → next, and the recording reachable from
the questions — the same three changes as Reading (ADR-064, ADR-066).

**Re-listening was refused before and is allowed now.** The earlier argument was
that replaying the clip during the questions hands over the answers. The product
owner asked for it anyway and is right about which is worse: a comprehension
question you cannot re-listen to is a memory test, and this section does not
measure memory.

**Decision — the clip is spoken sentence by sentence.** Text-to-speech has no
playhead: it is handed a string and talks until it runs out, so there is nothing
to seek. The clip is therefore cut into sentences and spoken one at a time, and
those boundaries *are* the seek points:

- **Dragging** the bar picks the sentence containing that point and starts
  there. The bar is drawn against characters, not sentence numbers, so it
  travels smoothly like a media scrubber; only where it lands is granular.
- **Start and end** buttons either side of play, for the whole clip at once.
- **The slow voice keeps the learner's place** — it re-speaks the current
  sentence at the slower rate rather than restarting the clip. A learner
  switches to slow *because of* the line they are on; sending them back to the
  beginning answers the wrong request.

**A run token, not a flag.** The playback loop awaits one sentence at a time, so
a seek cannot set a flag and trust the next iteration to notice: it has to be
able to tell "I am the current loop" from "I was replaced while I was waiting".
Each play increments the token and every await checks it.

**Consequence.** Position is reported in sentences — *Sentence 3 of 18* — because
that is what the voice can be positioned at; there are no seconds to show. A
sentence already in the speaker's mouth cannot be interrupted mid-word, so a
seek is heard from the start of the sentence it lands in.
---

## ADR-069 — Speaking: the tutor is told what the word means, and dictation stops losing words
**Date:** 2026-08-23 · **Status:** Accepted

### The tutor could not see the sense it was asking about

**Context.** The tutor was handed the remaining words as **bare strings**. Shown
`can`, it had no way to tell the modal from a tin from preserving fruit — so it
asked whatever the learner's interests suggested, and the word could not
honestly answer. A learner practising `can` (علبة, a tin) with `technology` in
their profile was asked what a computer *can* do.

The prompt already said the right things — *work backwards from the word*, *if
it does not fit, change the subject*, *interests choose between situations, they
are never a reason to bolt a word onto a topic*. None of it could work: the
model was reasoning about a spelling, not a meaning.

**Decision.** The English definition and part of speech travel with every
remaining word, and the prompt is told to read the sense before writing the
question — *"a question that suits a different sense of the same spelling is a
question they cannot answer"*. The interests are moved to the foot of the
prompt and labelled the lowest priority, with the same ban on naming them back
at the learner as Reading has (ADR-066).

**Verified on the reported case.** Learner interested in technology and
programming, having just said they worked on their computer all morning, with
`can` = *a metal container in which food or drink is sealed*:

> *"Working on the computer all morning sounds tiring. I usually need a snack to
> keep going when I work like that. **Do you ever buy a cold drink or food in a
> can when you are at the grocery store?** Try to use the word can in your
> answer."*

It reacted to what was said, then walked the conversation to where that sense of
the word actually lives.

### Dictation lost what the learner had already said

**Context.** Two failures, one cause. A pause of a few seconds and then speaking
again started the transcript over; and a long answer — four or five sentences —
began overwriting itself part way through.

Every recogniser closes its session on its own: after a silence, and after a
minute or so of continuous speech. It then opens the next one with an empty
transcript. This service kept only the latest result:

```dart
_heard = result.recognizedWords;   // ← replaces
```

The words were never lost by the microphone. They were overwritten here.

**Decision.** The turn is now assembled from segments: a `final` result is
appended to the transcript and the session is reopened, so a pause or a
platform-imposed cut is invisible to the learner and the words already heard
survive it.

- **`_wantsToListen` is separate from `_listening`** — the learner's intent
  versus the platform's session. The two come apart constantly.
- **A closed session is reopened**, from the status callback and from the error
  callback both, after a short beat so a platform mid-teardown is not asked to
  start again while it is stopping.
- **`cancelOnError` is now false.** Cancelling discards the segment in progress;
  a transient error should cost the last few words at most, not the turn.
- **Reopening is bounded.** Forty consecutive restarts with nothing heard and
  the service gives up rather than showing a microphone that is no longer
  listening.

### Nothing is sent before the learner has read it

The only two exits from a recording were "send it exactly as heard" and "throw
it away" (ADR-059). A recogniser mishears — a name, a number, the one word the
sentence turned on — and neither exit helps.

Closing the microphone now opens a **review**: the transcript in an ordinary
text field, with *Record again* and *Send*. The learner can fix a word, add the
sentence they forgot, or start over. Nothing reaches the tutor, and no AI call
is spent, until they press Send.

**Consequence.** A turn takes one more tap. That is the point: the tap is the
learner saying "this is what I meant", which is the only place that judgement
can come from.
---

## ADR-070 — A conversation is not over until someone has said goodbye
**Date:** 2026-08-23 · **Status:** Accepted · **Completes ADR-042**

**Context.** A Speaking session ends for one of two reasons: every target word
has been used, or it has run long enough (`words.Count + 3` learner turns).
ADR-042 gave the first a proper closing turn. The second had none — `isFinal`
simply became true, and the tutor's last words were whatever it had already
said, which at that point is almost always *"Try to use the word …"*. The
result screen then appeared over the top of it.

Which learner hits that path? The one who **never managed a word**. So the
conversation a learner got wrong was also the only one that ended mid-sentence.

**Decision — a conversation always closes.** Both endings now ask for the
closing turn. The rule, stated plainly: it opens with a greeting and it closes
with a goodbye, whatever happened in between.

**The goodbye is told what was missed, and must not mention it.** A closing turn
that congratulates a learner on practising every word when they never said
`several` is a lie they can detect. So the unused words travel with the request
— and the prompt is told not to list them, not to apologise, and not to say
anything went wrong. It names something they *did* do, and closes warmly.

Where the mistake is explained is the end-of-session evaluation: in detail, in
Arabic, after the conversation. A goodbye is not the place to mark somebody.

**Also settled here, from the same review.**

- **The interests rank last in every prompt that has them**, and both now say so
  in those words. They appear in exactly two: the passage generator (ADR-066)
  and the speaking tutor (ADR-069). Both open with *natural use comes first*,
  and a test asserts that neither can lose it.
- **Signing in is remembered, and already was.** Reported as "it asks me to sign
  in again next time"; the tokens are in the platform keystore, restored before
  the first frame, and only a rejected session clears them. Pinned rather than
  argued: a test signs in, throws the whole widget tree away, boots the app
  again against the same keystore, and asserts the learner lands on the hub with
  no sign-in form in sight.

---

## ADR-071 — A learner may delete a word; the system still may not
**Date:** 2026-09-10 · **Status:** Accepted · **Relates to** rule R8, ADR-012

**Reported:** "in My Words, I want to be able to delete the word."

Nothing in the app could remove a word. That was deliberate, and the reasoning
is still in the code: rule **R8** says exposure count is never a delete trigger,
and the lifecycle says an Archived word keeps its row and its whole history
(§31). My Words even lists Archived words on purpose, because disappearing from
that screen would be indistinguishable from deletion.

But R8 is a rule about **the system**. It exists so that a word cannot be quietly
retired by an algorithm counting exposures. It never spoke to the learner
removing a word they added by mistake, or added under a meaning they have since
decided is wrong — which, given the state of the Arabic glosses (ADR-072), is
not a rare event.

**Decision — deletion is a state, not a `DELETE`.**

`WordState.Deleted`, set by `Word.Delete`. To the learner it is gone: it leaves
My Words, no session will ever ask about it again, the weekly review does not
draw it. The row, its five skill states, its event log and its exposures all
stay.

**Why not a real delete.** This service exists to measure whether the pipeline
works (`00-PROJECT-PLAN.md` §1). A word that ran through Reading and Listening
and then vanished takes its evidence with it, and the Owner's dashboard would
read the deletion as the word never having existed — the one reading of the data
that is certainly false. The deletion itself is also a measurement: a learner
giving up on a word at Writing is worth knowing about.

**How it is enforced.** An EF Core **global query filter** on `Word`, not a
`Where` clause at each call site. There are two dozen places that read words —
five skill sessions, the hub, eligibility scans, the weekly review — and
"remember to exclude deleted" would be wrong in one of them within a month. That
one is a learner being tested on a word they deleted. The Owner's analytics opt
back in with `IgnoreQueryFilters()`, deliberately and visibly, in the eight
places that should see the whole history. The mock backend mirrors this exactly:
`MockUser.allWords` is the store, `MockUser.words` is the filtered view.

**Deleting and adding again gives a new journey.** The unique index on
`(UserId, SenseId)` is now filtered on `"State" <> 'Deleted'`. Without that, a
learner who removed a word could never add it back — refused for ever on the
strength of a row they believe is gone. The new row starts at Reading with no
history, which is what "I deleted it" means.

**An open session is left alone.** A word deleted mid-session does not tear the
session down: it finishes normally and applies nothing to that word. Completion
already handled a word that had moved on since the session opened (ADR-043), and
this is that case. Ending the session instead would throw away the answers the
learner had already given on the *other* words in it. Two `First` calls that
would have thrown on a filtered-out word — the writing evaluator, the mock's
comprehension builder — were the real hazard here, and both are fixed.

**Deleting twice answers 204.** A retried request is not a failure.

## ADR-072 — The meaning is the learner's to write
**Date:** 2026-09-10 · **Status:** Accepted · **Supersedes part of** ADR-012

**Reported:** "the dictionary that is there now is very, very bad — the meanings
on it do not match. Let them write the meaning by hand, or pick one of the ones
that are there."

This is measurable, not a matter of taste. From the live lexicon:

```
sell → أَقْنَعَ بِـ · باع · بِيعَ · بَاعَ · قُبِلَ · إقناع بالشراء
```

The first meaning offered for `sell` is "persuade to". Three of the six are the
same verb in different vocalisations, one is passive, and the one a learner
wants is second. That is what a machine join of CEFR-J, Open English WordNet and
Arabic WordNet produces (ADR-012), and no amount of re-ranking fixes the glosses
themselves.

ADR-012 said the learner may only ever pick a meaning the lexicon provided,
because a hand-written meaning can be wrong and the pipeline is built on the
meaning being trustworthy. The reasoning is sound and the conclusion no longer
follows: the alternative to a hand-written meaning is not a correct meaning, it
is *the lexicon's*, and the lexicon is demonstrably wrong at the top of the list.
Between a gloss the learner knows to be wrong and one they wrote themselves,
theirs is the better bet — they are the only party in this system who knows what
they meant.

**Decision — the meaning may be written; the word may not.**

`POST /api/words` accepts `customMeaning`. The word itself is still resolved
against the lexicon, and a spelling it does not know is refused. That boundary
is where it is for concrete reasons: the CEFR level decides which passages the
word appears in, the part of speech shapes the generated sentences, and the
English definition is the top rung of the Spelling hint ladder. None of the
three can be invented for a string nobody recognises, and `asdfgh` must not
enter a pipeline that will spend five sessions on it.

**The meaning must be Arabic.** Every skill marks answers against this string.
An English meaning makes its own comprehension questions unanswerable — refused
at the door rather than discovered two days later inside a session.

**Identity.** A written meaning has no synset to point at, so it gets a derived
sense id: `custom:` + the first 128 bits of `SHA-256(text|meaning)`. Derived, not
generated, so that the same word and the same meaning produce the same id and
the unique index goes on doing its job — including for two taps arriving at
once. A client may not post a `custom:` id down the lexicon path; that is the
one way a forged meaning could get in, and it is refused.

**`MeaningSource` is recorded** — `Lexicon`, `Learner` or `Passage`. The three
are not equally trustworthy and the experiment has to be able to tell them
apart. If words with learner-written meanings turn out to fail Spelling twice as
often, that is a finding, and it is unreadable if every meaning looks alike in
the data. The learner is never shown it: to them a meaning is a meaning.

**What is *not* changed.** The lexicon's meanings are still offered first, and
still the recommended path. Writing one is the way out of a list that does not
contain the right answer, not a replacement for the list.

## ADR-073 — A word added from a passage keeps that passage's meaning
**Date:** 2026-09-10 · **Status:** Accepted · **Fixes** a rule R1 violation

**Reported:** "in Reading I press a word to add it. The word has a meaning in
the context; when I add it, it goes into my words with a different meaning."

Exactly right, and the cause was on the client. `word_lookup_sheet.dart` showed
the passage's own gloss — correct, written by the generator as it composed the
sentence — and then, on "add", threw it away: it fetched the word's dictionary
senses and picked whichever *read* closest, by exact string match, then by any
sense sharing a word with it, then `senses.first`. For `bank` in a passage about
a river that is a coin flip. The learner tapped "ضفة النهر" and got "مصرف".

It was also a quiet breach of **rule R1**. Deciding which sense a word is, is not
a decision the client gets to make.

**Decision — the client says which word and which passage; the server says what
it meant.**

`POST /api/words` accepts `fromSessionId`. The server loads that session — the
caller's own, or 404 — reads the glossary **it stored when it generated the
passage**, and takes the meaning from there. No meaning crosses the wire. A
client that sends one anyway gets the server's answer, and there is a test that
says so.

The part of speech comes from the glossary too, not from the lexicon's commonest
sense: the glossary knows the word's role *in this sentence*, and "will" is an
auxiliary here and a noun three entries up. The level and the English definition
still come from the lexicon, as in ADR-072.

**A word the passage never glossed is refused**, with `NOT_IN_PASSAGE` — names
and numbers appear in generated text and carry no gloss. The sheet answers that
by falling back to the ordinary dictionary view, which is the right answer for a
word this passage never explained, rather than by reporting a failure.

**Verified end to end against real Gemini.** A generated passage — *Natural
Waterways and Their Benefits*, 156 glossary entries — was searched for a word
whose passage gloss disagrees with the lexicon's first sense, which is the exact
shape of the reported bug. It found `Learning`:

| | |
|---|---|
| the passage means | تعلم |
| the lexicon's first sense | اكتسب (صيغة الاستمرار) |
| what the old client stored | اكتسب (صيغة الاستمرار) |
| what is stored now | تعلم |

Confirmed by SQL, with `MeaningSource = Passage` and a `custom:` sense id.

## ADR-074 — A meaning the learner writes is checked before it is stored
**Date:** 2026-09-10 · **Status:** Accepted · **Completes** ADR-072

**Reported:** "when the student writes the meaning in Arabic, it should go to
the AI and check it — is the Arabic good, are there spelling mistakes? Because
it travels with him afterwards. If he writes `book` and puts `إنسان`, maybe he
doesn't know. It should tell him: no, the meaning isn't right — did you mean
this? Only when he writes the meaning himself; the other ways are fine."

Exactly right, and the reason is downstream. ADR-072 let the learner write the
Arabic because the lexicon's glosses are bad. But whatever they write is what
**all five skills mark answers against** for the next eight days. A wrong
meaning is not a cosmetic problem — it is five sessions asking the wrong
question, and the learner has no way to discover it, because the app agrees
with them by construction.

**Decision — `POST /ai/meaning/check`, on the written path only.**

The lexicon path and the passage path are untouched. A curated gloss and a
gloss this service wrote itself are not the learner's guesses, and checking
them would be spending the learner's money to ask a model whether the
dictionary is right. A test asserts the checker is called zero times for both.

**The verdict is advisory; the check is not.** Two decisions from the product
owner, and they only look contradictory:

* A rejected meaning comes back with the checker's sentence and up to three
  meanings it would accept. The learner may tap one, edit, or press **"save it
  as I wrote it"** — placed below the suggestions and quieter than them. The
  checker is sometimes wrong, and this whole feature exists because an
  automated source of meanings was (ADR-072). Replacing a bad dictionary with a
  confident model the learner cannot get past is the same mistake wearing a
  different hat, and rule R2 says the AI reports rather than decides.
* If the checker **cannot be reached**, nothing is saved — 503, try again in a
  moment. Alone in this service, this call has no fallback, because there is
  nothing to fall back *to*: the only answers available without a model are
  "yes", which lets an unchecked meaning in wearing the same badge as a checked
  one, and "no", which refuses a learner who is probably right.

So the check always happens, and its answer is the learner's to overrule.

**A spelling slip is offered, never applied.** `طاولةةة` comes back as a
refusal carrying `corrected: طاولة`. Storing the correction silently puts words
in the learner's mouth; storing the misspelling teaches it.

**`MeaningCheckResult` is recorded** — `Approved` or `Overridden`, and null for
the two paths that are not checked. "The model said no and the learner said yes
anyway" is the fact that explains a word failing Spelling four times a
fortnight later, and it is unrecoverable if nobody wrote it down.

### Two bugs this found, both mine, both only visible by running it

**Sending one sense rejected correct meanings.** The checker was handed
`facts.DefinitionEn` — the *commonest* sense — so `book` arrived as "a set of
printed pages" and a learner writing `يحجز` was told they were wrong. That is
the precise meaning ADR-072 exists to let them write, refused by the feature
meant to help them. It now receives every sense.

**"Every sense" was still nine nouns.** `book` has nine noun senses and three
verb senses, and the nouns rank higher — so `Take(8)` cut every verb. The model
never learned `book` is a verb at all and rejected `يحجز` a second time, for a
different reason. Senses are now spread across parts of speech: a few each,
commonest first within each.

A standalone `part_of_speech` line went with them. It named the commonest
sense's part of speech and sat above a list that already carries one per sense
— telling the model "book is a noun" while the list said otherwise, and the
model believed the headline.

**Verified against real Gemini**, end to end through the API:

| written | result |
|---|---|
| `book` = `يحجز` | ✅ saved, `Approved` |
| `book` = `إنسان` | ❌ *"إنسان تعني human، بينما book تعني كتاباً أو عملية حجز"* — كتاب · حجز · سجل |
| `book` = `إنسان`, insisting | ✅ saved, `Overridden` |
| `table` = `طاولةةة` | ❌ spelling — `corrected: طاولة` |

---

## ADR-075 — A word the dictionary has never heard of can still be added

**Date:** 2026-09-13 · **Status:** Accepted · **Supersedes the lexicon gate in ADR-072**

The product owner, plainly: *"القاموس حقنا فيه كلمات مش موجودة… عادي لو إنه يدخل
الكلمة من عنده ويدخل المعنى ويسوي إضافة."*

ADR-072 let a learner write the meaning and kept the *word* behind the lexicon,
on the reasoning that a CEFR level, a part of speech and an English definition
are not a learner's to invent — they decide which passages a word appears in and
how Spelling clues it. That reasoning is still right. The conclusion was wrong,
because the lexicon is a machine join of WordNet and two CEFR lists and it has
holes, and **the word a learner most wants to add is disproportionately the one
that fell down a hole**: `deepfake`, `microservice`, `doomscroll` — every word
newer than the datasets, and every word too specialised for them.

So the lexicon is no longer a gate. It is a *preference*.

### What answers the question instead

The three facts still have to come from somewhere, and there is exactly one
thing in this system that knows them for a word no dataset has: the meaning
checker, which is already being asked about this word on this code path
(ADR-074). It now answers two questions instead of one, and only when the
lexicon came back empty:

| | lexicon has the word | lexicon does not |
|---|---|---|
| is it English? | not asked — it is in the dictionary | asked |
| level, part of speech, definition | the lexicon's | the checker's |
| does the Arabic match? | asked | asked |

`known_word: false` in the request is what turns the second question on. It is
sent explicitly rather than inferred from an empty definition list, because a
word the lexicon *has* and holds no English gloss for is a real case, and
inferring "unknown word" from it would ask the model to invent facts this
service already holds.

**A word the lexicon has is never re-judged as a word.** The stub AI in the test
suite is deliberately configured to deny everything, and `crucible` still goes
in: a model saying "that is not a word" must not be able to refuse a dictionary
entry.

### The refusal is not overridable, and that is the interesting part

ADR-074's whole argument is that the learner may overrule the checker: the
feature exists *because* an automated source of meanings was wrong often enough
to be unusable, and replacing it with a confident model the learner cannot get
past would be the same mistake wearing a different hat.

That argument does not transfer. It is about *meaning*, where the learner may
genuinely know better — a dialect gloss, a sense the list omits. "Is this a
word" is not a matter of opinion, and nothing downstream can teach `asdfghjkl`:
the generator cannot build a passage around it, Spelling cannot clue it, the
level engine cannot read a failure on it as evidence. Five sessions would be
spent on a typo.

So `acceptAnyway` does not reach this refusal. What the learner gets instead is
smaller and more useful: the spelling the checker thinks they meant, one tap
away — `recieve` comes back with `receive`, and tapping it retries with the
corrected word, not merely a corrected label.

### R2 holds: the model reports, the backend decides

The part of speech is filtered through a fixed list (`KnownPartOfSpeech`) and
anything outside it becomes empty — which every reader already handles, since a
lexicon row can lack one too. The CEFR band goes through `TryFromWire` and falls
back to B1, the neutral default the level engine corrects from real performance.
A model answering `gerundive` / `Z9` does not get to write either into the
database.

**Verified against real Gemini**, end to end through the API:

| typed | result |
|---|---|
| `deepfake` = `تزييف عميق` | ✅ saved — noun, C1, *"a piece of media that has been digitally manipulated…"* |
| `asdfghjkl` = `كلمة` | ❌ *"ليست كلمة إنجليزية حقيقية، بل هي مجرد سلسلة من الحروف المتجاورة على لوحة المفاتيح"* |
| `recieve` = `يستلم` | ❌ spelling — `correctedWord: receive` |
| `microservice` = `طاولة` | ❌ meaning — *"تعني في مجال البرمجيات جزءاً صغيراً ومستقلاً من نظام برمجي كبير، ولا علاقة لها بكلمة طاولة"* |
| `crucible` = `بوتقة كبيرة`, checker denying everything | ✅ saved — the lexicon has it, so it was never asked |

### One thing this gives up

`Word.DefinitionEn` and `Word.PartOfSpeech` for these rows are a model's
recollection rather than WordNet's. They are good enough for what reads them and
they are marked: `MeaningSource.Learner` plus a `custom:` sense id identifies
every such row, so a later audit can find them all.

---

## ADR-076 — Daily reminders, scheduled by the phone and written by the server

**Date:** 2026-09-13 · **Status:** Accepted

The product owner: *"أبغى يومياً يوصل إشعار لليوزر… من دون فايربيس ستور… خله
إشعارين، واحد الصباح واحد المساء… يقول له لديك كذا كذا كلمة جاهزة… عشان ما ينسى
البرنامج."*

Two reminders a day, morning and evening. **No Firebase, no push.** Every
notification here is an alarm the phone sets for itself.

### The problem that shapes everything else

A local notification fires with no network and usually with the app not running.
There is nobody to ask what it should say at the moment it goes off — so
whatever it says has to be decided days in advance, and stored on the device.

That collides with rule R1 (the client renders server state, it never computes
it), and the collision has to be resolved rather than ignored: the phone cannot
count how many words are due, because the pipeline lives in PostgreSQL and the
phone is offline and asleep.

**The resolution:** the server computes the plan, the phone schedules it.

`GET /api/notifications/daily` returns one entry per time of day for the next
seven days, each carrying a wall-clock time, a stable `kind` and a `count`. The
client turns that into sentences in the learner's language (ADR-035) and hands
them to the OS. Neither half can do the other's job — the server does not know
the language, and the phone does not know the pipeline.

### Why per-day and not one repeating notification

A repeating daily notification can only carry one sentence, so it would say the
same number for ever. But a word's due date is *known*: a word that passed
Reading on Monday is due for Listening on Wednesday, and the server can say so.
Each day therefore gets its own reminder with its own count, computed against
that day's instant:

```
Tue 08:00  NOTHING_DUE  (word is waiting out the gap)
Thu 08:00  WORDS_DUE 1  (the same word, now due)
```

Seven days × two slots = fourteen, comfortably inside iOS's limit of 64 pending
notifications. The plan is refetched on every app open and on every resume, so a
learner who uses the app never reaches the end of it — and one who does not,
stops being reminded, which is honest: by then every count would be a guess.

### Three kinds, because they are three different things to say

`NO_WORDS`, `NOTHING_DUE`, `WORDS_DUE`. "You have 0 words ready" is a true
sentence for both a learner who has never added a word and one who finished
everything yesterday, and it is the wrong thing to say to either.

### Details that are decisions

**Wall-clock times, not instants.** The server sends `date`, `hour`, `minute`;
the phone schedules in its own timezone. A learner means the time on their own
phone when they say "morning". The *counts* are still computed against
`ReportingUtcOffsetHours`, the one product-wide offset ADR-0xx already settled.

**A past slot is dropped server-side, and again client-side.** A phone handed a
past time either fires it the instant it is set — an alert the learner never
asked for, at the moment they opened the app — or drops it silently. The server
drops against its own idea of the day, so a phone in another timezone can still
be handed one; both ends check.

**Inexact alarms.** `SCHEDULE_EXACT_ALARM` is a special Android permission the
learner must grant in system settings. A practice reminder does not care whether
it arrives at 08:00 or 08:09, so the permission is not requested and
`inexactAllowWhileIdle` is used.

**Every refresh replaces, never appends.** Each reminder names a specific day and
carries a count that was true when it was written. Two passes leaving two of
each would have the phone say a number the server no longer believes.

**The switch is device-local** (`AppPreferences.remindersEnabled`), and that is
not a breach of rule R4. What is stored is not learning state: it is whether
*this phone* sets alarms for itself. A learner with the app on a tablet at home
and a phone in their pocket has a real reason to want one and not the other, and
the OS permission it sits on is per-device too. Defaults to on — the OS asks
before a single notification is shown, so a learner who does not want them says
no once; defaulting to off would mean the feature never ran for anybody who did
not go looking for a switch.

**Reminders are cancelled on sign-out.** A pending "you have 4 words ready" for
an account nobody is signed into is a notification about somebody else's
vocabulary.

### Two platform lines that are silent if missed

The receivers in `AndroidManifest.xml` — `ScheduledNotificationReceiver` and
`ScheduledNotificationBootReceiver` — are declared by the app, not by the
plugin. Without them scheduling succeeds and nothing is ever shown. Likewise
`UNUserNotificationCenter.current().delegate` in `AppDelegate.swift`: without it
a notification arriving while the app is open is swallowed, which is exactly the
case anyone testing the feature hits first.

---

## ADR-077 — The readiness probe is throttled, because it was the whole hosting bill

**Date:** 2026-09-14 · **Status:** Accepted · **Corrects `docs/09-DEPLOYMENT.md` §3–§4**

`/health/ready` opened a database connection on every request, by design: that
is what distinguishes it from `/health/live`, and the deployment document told
the operator to point a five-minute uptime monitor at it *specifically* because
it touches the database — "so the same ping keeps Neon awake as well."

Every part of that sentence is true. It was still the wrong thing to want.

### What it actually cost

Neon's free plan bills **compute-hours** — the time the database is awake — not
queries, and suspends the compute after **5 minutes** idle. A probe every five
minutes against a five-minute suspend arrives exactly when it was about to
sleep. Every time. And two probes were doing it: cron-job.org on the interval
above, and Render's own health check, which §3 had also pointed at
`/health/ready`.

Measured on the live instance, 1–14 September 2026:

```
0.25 CU (the plan's floor) × 324 hours = 81 compute-hours
```

Against an allowance of 100. **Eighty per cent of the month spent in thirteen
days, with no learner traffic in it at all** — the database was awake 24 hours a
day and idle for nearly all of them. At that rate the allowance runs out on the
17th of each month, Neon suspends the compute until the next cycle, and the app
stops for everybody.

This is worth stating as a general shape, because it will recur: **on a
serverless database, the probe that proves the database is alive is also the
thing that forbids it to sleep.** A health check is not free the way it is on a
server you rent by the month. It is the most frequent query the service makes,
and it runs hardest exactly when nobody is using the app.

### The fix, in two halves

**The operator's half**, in `docs/09-DEPLOYMENT.md` §4: the keep-alive points at
`/health/live` — no connection, no query, no `DbContext` — every 10 minutes.
That is all it was ever for; Render sleeps at 15 minutes, Neon is not its
business. Watching the database is a *second* monitor on `/health/ready` at 60
minutes, about 15 compute-hours a month.

**The service's half**, here: `/health/ready` asks the database at most once per
`Capacity__ReadinessDatabaseCheckSeconds` and returns the cached verdict in
between. Configuration, not a constant, because the right answer differs by host
(rule R3) — one hour by default, `0` to ask every time, which is correct on a
server you own.

Documentation alone would not have been enough. The deployment guide already
*had* a `/health/live` endpoint and recommended against it; the next operator,
or the same one on a bad day, re-points a monitor and the bill comes back with
no symptom until the month dies. The throttle makes the expensive configuration
impossible to express rather than merely discouraged.

### Two details that are decisions

**Only successes are cached.** A failure is re-checked on the very next request.
A database that is down accrues no compute time, so polling it costs nothing,
and a database coming back must be visible immediately rather than up to an hour
later.

**The staleness is published, not hidden.** The response carries
`databaseCheckedSecondsAgo`, so an operator reading `"database":"connected"`
knows whether this request proved it or whether it is a 47-minute-old claim.
A cache that silently answers for something it did not check is how a monitor
becomes a comfort rather than a measurement.

### What this gives up

Up to an hour between a database failing and `/health/ready` saying so. That is
real, and acceptable here: the learner-facing symptom of a dead database is
immediate and loud anyway — every request 500s — and `/health/live` still tells
the host to restart a process that has genuinely died. What is lost is *early*
warning of a database failure that has not yet been noticed by anybody. What is
bought is an app that is still running on the 25th.

---

## ADR-078 — A forgotten password is recovered with a six-digit code by email

**Date:** 2026-09-15 · **Status:** Accepted

There was no way back into an account. A learner who reinstalled the app and
could not remember their password was simply locked out for good — and with a
cohort of students installing a build for the first time, that is not an edge
case, it is Tuesday.

### Email, and the choice of provider

The project had **no** way to send anything: no SMTP, no provider, no key. So
this adds an external dependency, and which one is a real decision.

SMS was rejected despite the phone numbers already being in the database
(ADR-054). There is no free tier at any provider worth the name, delivery to
Yemen is unreliable, and every failed attempt still costs.

Brevo was chosen over Resend and the rest for one practical reason: it verifies
a single **sender address** — an ordinary Gmail account — where most
transactional providers verify a whole **domain**, which means owning one. Its
free allowance is 300 messages a day against a cohort that will ask for a
handful of resets a week.

`IEmailSender` keeps that a detail. `BrevoEmailSender` is one HTTPS POST;
swapping providers is a class, not a refactor.

### A code, not a link

A link means deep-link plumbing on two platforms, and it breaks the moment a
learner opens their mail on a different device from the one holding the app —
which, for a phone-only cohort reading Gmail in a browser, is common. Six digits
typed into a screen work everywhere and need no platform configuration at all.

### Six digits is a million, so three limits bound the guessing

Not one, and not two. Each closes a hole the others leave open:

| | |
|---|---|
| **15 minutes** | a code glimpsed over a shoulder is worthless by the time it is tried |
| **5 attempts per code** | rate limiting alone does **not** do this — a permitted request budget, spent patiently, walks a million-wide space eventually. This caps the search at five |
| **one live code at a time** | otherwise three taps of "send another" mean fifteen guesses, and the attempt cap stops meaning anything |

Stored **Argon2id**-hashed, not SHA-256 like a refresh token. A million SHA-256s
is an eye-blink, so a fast hash would mean a leaked database hands over every
outstanding reset code. This is affordable only because of a design choice that
looks incidental and is not: nothing ever looks a code up *by its hash*.
Redemption finds the row by user id and verifies a single candidate — one
verification per attempt, the same cost as a sign-in, under the same concurrency
cap (ADR-051).

### The part that shaped everything: it must not say who has an account

`/forgot` answers `202` with the same body for a registered address, an
unregistered one, **and a provider outage**. The third is the one that is easy
to get wrong, and it is the sharpest: only a registered address causes a send at
all, so if a failed send became a `500` while a successful one stayed `202`, an
attacker could separate real accounts from fake ones by watching which requests
error. That is why `IEmailSender.SendAsync` returns a bool that the endpoint
ignores rather than throwing.

`/reset` answers `400 INVALID_RESET_CODE` for every failure there is: wrong
code, expired code, spent code, exhausted attempts, and an email that was never
registered. A message saying "expired" rather than "wrong" would confirm to a
stranger that the code they guessed had once existed. An unknown address still
pays for a hash verification against a dummy, so the response time does not give
it away either — the same defence login already uses.

The client carries the other half of this promise: the screen says *"**If** that
email is registered, a code is on its way."* An honest "we sent you a code"
would undo the whole design.

### Two things the reset deliberately does not do

**It returns no tokens.** The learner signs in afterwards with the password they
just chose. Handing back a session would make one intercepted email a complete
account takeover, with nothing else in the way.

**It does not leave other sessions alive.** Redeeming a code revokes every
refresh token for that user. A reset exists to answer "someone else knows my
password"; stopping future sign-ins while leaving the intruder's current session
running answers it halfway. The learner is told this happened — being silently
signed out on another device is alarming in a way the explanation fixes.

### The development hole, closed deliberately

With no key configured, `UnconfiguredEmailSender` writes the code to the log so
the flow can be walked without a provider account. That is a live credential in
a log, which §9 forbids — tolerable in a developer's own terminal, and *not*
tolerable on Render, whose log is readable by anyone with dashboard access. So
the same class refuses to log it outside Development and fails the request
loudly instead. A missing key is a misconfiguration, and it should look like
one rather than like a learner who never checked their inbox.

### What this gives up

Delivery is now someone else's uptime. If Brevo is down, or the message lands in
spam, the learner is locked out exactly as before — the screen says to check the
spam folder for that reason. The Owner can still reset a password with SQL,
which remains the backstop.

---

## ADR-079 — Migrating the deployed database: four ways it silently goes elsewhere

**Date:** 2026-09-15 · **Status:** Accepted · **Corrects `docs/09-DEPLOYMENT.md` §1**

Applying one migration to production took two hours and four separate failures.
Not one of them was a hard problem; every one of them was a command that looked
like it had worked, or an error that named the wrong cause. They are written
down because the next migration meets all four again.

### 1 · The documented command migrated the wrong machine, and said `Done.`

The guide said:

```bash
ConnectionStrings__WordOs="$NEON_OWNER" dotnet ef database update …
```

`WordOsDbContextFactory` reads `ConnectionStrings:WordOsMigrations` **first**,
falling back to `WordOs` only if it is absent. On any machine that has ever run
the backend locally, `WordOsMigrations` is already in user-secrets — pointing at
`wordos_dev`. So the documented variable was shadowed, `dotnet ef` migrated the
laptop, and reported:

> `No migrations were applied. The database is already up to date.`

Which was true. Of the local database. Production never got the table, and the
first symptom was a `500` from an endpoint that had just deployed cleanly.

`export NEON_OWNER=…` compounds it: it lasts only as long as that terminal, so a
new window makes the variable an empty string and the local secret wins again,
for the same reason, with the same reassuring output.

**Fix:** the guide now says `ConnectionStrings__WordOsMigrations`, and the
factory prints what it is about to change before it changes it:

```
Migrating ep-….neon.tech/wordos as wordos_migrator
```

One line. It caught the next three failures on sight, which is the entire
argument for it — a destructive-capable command should not be silent about its
target.

### 2 · Neon's default database is not the application's database

The connection string Neon hands out names **`neondb`**. The deployment guide
creates a database called **`wordos`**. Both exist, on the same branch:

```
wordos    → 119 MB   ← 46 users, 583 words, 234,359 lexicon rows
neondb    →   8 MB   ← empty
postgres  →   8 MB
```

Running the migration against `neondb` applied all twenty-three migrations from
`InitialSchema` and reported success, because an empty database is a perfectly
valid thing to migrate. The giveaway is in the output and nowhere else: **if a
deployed database replays `InitialSchema`, it is the wrong database.**

A probe then appeared to confirm the fix, and did not. `POST /auth/password/forgot`
with an address that does not exist queries `users` and returns `202` without
ever reaching `password_reset_codes` — so the missing table stayed invisible.
**Verify a migration with input that exercises the thing it added**, which here
means a registered email, not a made-up one.

### 3 · The pooler refuses startup parameters

`-pooler` in the hostname is PgBouncer. It rejects `options` in the startup
packet outright:

```
08P01: unsupported startup parameter in options: role.
Please use unpooled connection…
```

Migrations should use the **direct** endpoint anyway — drop `-pooler`. Neon's
connect dialog hands out the pooled host by default, so this has to be done by
hand every time.

### 4 · The cloud owner role cannot alter the application's schema

`neondb_owner` — the role Neon's connect dialog gives you — is not the owner of
these tables. `wordos_migrator` is, and creating a foreign key to `users`
requires `REFERENCES` on `users`, which only its owner holds:

```
42501: permission denied for table users
```

The way through needs no extra password, and is worth knowing because it will be
needed again: `neondb_owner` holds **`admin_option`** on `wordos_migrator`
(`pg_auth_members`), so it can grant itself the membership it lacks —

```sql
GRANT wordos_migrator TO neondb_owner WITH INHERIT TRUE, SET TRUE;
-- … run the migration with Options=-c role=wordos_migrator …
REVOKE wordos_migrator FROM neondb_owner GRANTED BY neondb_owner;
```

The `GRANTED BY` clause matters: without it the revoke removes the *original*
grant instead of the temporary one, and quietly widens what the next person
inherits. Restore the membership to exactly `admin|inherit|set = t|f|f`.

This is not a privilege escalation so much as an unused one — anyone holding
`neondb_owner` could already do it. But it should be taken for one command and
handed back, not left open.

### 5 · Every new table needs an explicit grant to `wordos_app`

The consequence of §4, and the one that will bite hardest, because it fails at
**runtime** rather than at migration time.

`ALTER DEFAULT PRIVILEGES` in the deployment guide was run as `neondb_owner`, so
the defaults are attached to that role. Tables are created by `wordos_migrator`.
Default privileges apply only to the role that creates the object, so a new table
gets **no grant to `wordos_app` at all** — the migration succeeds, the deploy
succeeds, and the endpoint answers `500 permission denied` the first time a
learner touches it.

So, after every migration that adds a table:

```sql
SET ROLE wordos_migrator;
GRANT SELECT, INSERT, UPDATE, DELETE ON <new_table> TO wordos_app;
```

Check it rather than assume it — the ACL should match every other table:

```
{wordos_migrator=arwdDxtm/wordos_migrator,wordos_app=arwd/wordos_migrator}
```

### What would have prevented all of this

Naming the target. Four failures, and the single line added in §1 would have
made three of them obvious within a second of running the command. The fourth
— the silent absence of a grant — is why §5 is written as a checklist item
rather than a caution: nothing announces it until a learner hits it.

---

## ADR-080 — The Listening clip waits to be asked, and the control says what the next tap does

**Context.** Three complaints about the Listening player, from a learner's walk-through,
that turned out to be one complaint and one genuine bug underneath it.

### 1. It started talking before anyone asked

The clip auto-played from a post-frame callback the moment the screen appeared
(the old §22 reading: "a listening exercise whose first action is *press play*
spends the learner's first interaction on something the screen already knew it had
to do").

That argument is right about the tap and wrong about the moment. The learner does not
arrive on a finished screen — they arrive on a spinner, wait for the passage to be
written, and are then talked at. The passage **title**, added in ADR-066 precisely so a
listening clip is named before it is heard, goes past unread; the first thing they
actually do is hunt for the control that makes it stop. An exam prints the title above
the listening section and then waits for the invigilator. So does this.

**Decided:** no auto-play. The title is the pause, and the clip begins when the learner
says so.

Note what this changes downstream: a device with no speech engine is no longer
discovered on arrival but on the first press. That is the honest moment to discover it —
but it means the "audio is unavailable, here is the transcript" fallback has to survive
the press rather than be waiting on arrival, which is now pinned by a test.

### 2. Pausing offered to replay

`_played` — "has this clip ever been started" — drove the button's face, so the moment
the learner stopped it the control showed a **replay** icon. The behaviour underneath
was already right: the next tap continued from the sentence that was interrupted. The
icon promised the opposite. A learner who paused to think about a line was being told
the only way back was from the top.

**Decided:** three faces, each stating what the *next* tap does.

| State | Icon | Word | Next tap |
|-------|------|------|----------|
| playing | `pause_rounded` | Pause | suspends it where it stands |
| paused mid-clip | `play_arrow_rounded` | Continue | resumes from the interrupted sentence |
| finished | `replay_rounded` | Play again | starts the clip from the top |

Replay survives only in the third row, where it is the honest promise: there is nothing
left to continue.

"Continue" is sentence-granular, not sample-granular. Text-to-speech has no playhead
inside an utterance (ADR-068) — it is handed a string and it talks. The interrupted
sentence therefore starts again from its beginning. That is the closest thing to
resuming this engine can do, and it is the same granularity the scrubber and the
normal/slow switch already work at, so the player is consistent with itself.

### 3. The bug under the icons: a stopped clip read on

Found while fixing the above, and worse than either of them.

`SpeechService` reports a **cancelled** utterance and a **finished** one through the
same callback — `flutter_tts` wires `setCancelHandler` and `setCompletionHandler` to
the same place, and nothing downstream could tell them apart. The Listening clip is a
loop that awaits one sentence and then starts the next. So when the learner pressed
*I finished listening*, the screen called `stop()` — and the loop heard "that sentence
ended", advanced, and **started reading the next line out over the questions**.

The window is a single frame, which is why it was never seen in a test: `stop()`
suspends on a platform call, the loop's continuation runs as a microtask, and the
widget is not disposed until the next build. The microtask wins. For Listening this is
not untidiness — the transcript is the answer key.

**Decided:** `SpeechService.interruptions` — a count of the times playback was cut short
rather than allowed to end. A sequence reads it once the voice is its own and compares
after every sentence; a change means the clip is no longer its to continue. The one
place stopping *is* the end of an utterance — the completion timeout, which exists to
paper over a platform that never reports back — passes `interrupting: false`, so a
broken report does not halt a clip that should carry on.

And, separately, `dispose` now actually silences the voice instead of only abandoning
the loop. The old comment there claimed it did.

### 4. What that exposed at teardown

A screen that silences the voice on its way out is doing the right thing, and its
`stop()` lands *after* the provider scope holding the service has gone. `ChangeNotifier`
asserts on a notify after disposal, so the correct behaviour surfaced as a crash in the
screen that performed it.

**Decided:** `SpeechService` refuses work once disposed rather than asserting — it
cannot speak, and it notifies nobody. One exception: anything **awaiting** the voice is
completed on the way down rather than left hanging, because a stranded
`speakToCompletion` is a conversation waiting for a microphone that will never open.

Two smaller notes, for whoever holds a service reference in a widget:

* capture it in `initState`, not with a `late final` initialiser. A lazy initialiser
  runs on first use, and the first use is inside `dispose` — where `ref.read` throws.
* this applies to the Listening player specifically; every other caller reaches the
  service through `ref.watch` during build, which is safe.

### 5. One engine, because there were three

The rules above are not properties of the Listening screen; they are properties of
*playing English aloud with a place in it*. There were three copies of that — the
Listening clip, the sentence beside a question, the recording handed back with the
result — each with its own flags, and they had already drifted: the clip walked
sentence by sentence and could be scrubbed, the sentence card spoke its three lines as
one utterance with a second button for "slow" that always restarted it, and only one of
the three carried the bug in §3.

**Decided:** `ClipPlayback` (`mobile/lib/features/session/clip_playback.dart`) owns the
sentence splitting, the place, the speed and the loop, and has **no opinion about how it
is drawn** — each player keeps its own layout. It also has no auto-play option at all,
so that decision cannot be re-opened one call site at a time.

The sentence beside a Listening question therefore gained everything the clip has: it
waits to be asked, it pauses and continues, it reports which of its three lines it is
on, it can be sent back to the first, and **speed is a mode rather than a second play
button** — previously "slow" was its own button, so there was no way to be playing
slowly *and* pause.

`ReplayPlayer`, the recording handed back on the result screen, is deliberately left on
its own implementation for now: it already does not auto-play, and it is study material
rather than part of the test. It is the obvious next thing to fold in.

### Consequences

* §22 of the requirement documents is superseded on the auto-play point, deliberately,
  and now for **both** players. The requirement behind it — *the learner should not have
  to work to hear the audio* — is met by a single obvious control, not by talking first.
* The tests that pinned the old behaviour were rewritten, not deleted; the files now
  state the replaced readings so the next change does not restore them by accident.

---

## ADR-081 — A Listening word question offers the word aloud, at both speeds

**Context.** Every session ends with one question per target word — *what does this word
mean here?* Reading shows the word spelled out in its three neighbouring sentences.
Listening deliberately shows nothing: the same three sentences are spoken and never
written, because showing them would turn a listening task into a reading task
(demo review §34).

That is right about the **sentences** and wrong about the **word**. Listening is the
only skill where a target word never arrives on its own: it is buried mid-sentence, at
speaking speed, once. A learner who did not catch it is not being tested on meaning at
that point — they are being tested on whether they heard it, which the comprehension
questions already measure. The word is printed in the question either way; only its
sound is missing.

**Decided.** On target-word questions in Listening, the word can be heard **on its own,
at normal speed and slowly**, as a separate control under the question.

Scope, narrowly:

* **Listening only.** Reading already spells the word out in front of the learner.
* **Target-word questions only.** Comprehension questions carry no word of their own —
  and a control there would be answering the question it sits under.
* **The word only, never the sentence around it.** That sentence is the test. The
  sentence player above it is unchanged and keeps its own speed control; the two are
  separate cards with separate labels for that reason.

Nothing here can leak an answer: these questions ask what the word *means*, and the
options are meanings.

The control is `WordPronunciation` in `session_widgets.dart` rather than logic in the
screen, so enabling it elsewhere later is a call-site decision — one condition, not a
second implementation.

---

## ADR-082 — A real playhead: the clip is positioned at the word, not the sentence

**Context.** ADR-068 cut the clip into sentences and made those the seek points, because
text-to-speech has no playhead: it is handed a string and it talks. ADR-080 then made
pause continue rather than restart — but only from the start of the interrupted
sentence, and the position was printed as *"Sentence 2 of 11"*.

Neither is how anyone listens to a recording. A learner pauses eleven words into a long
sentence and wants those eleven words to stay behind them; and *"Sentence 2 of 11"* is
not a position, it is a diagnostic.

### What was rejected first

**Cutting the clip into words and speaking them one at a time.** It gives an exact
playhead in one line of code, and it destroys the thing being practised: every word
becomes its own utterance with the engine's inter-utterance gap after it, so the clip
stops being English spoken and becomes English dictated. A listening exercise whose
audio does not sound like speech is not a listening exercise. This was the obvious
implementation of what was asked for, and it is the wrong one.

### What is done instead

`flutter_tts` reports progress as each word begins — iOS through
`willSpeakRangeOfSpeechString`, Android through `onRangeStart` — with the character
offset into the text it was handed. That is a real playhead, and it costs the audio
nothing.

* **Sentences stay whole while playing.** Natural audio, unchanged.
* **The engine reports where it is**, word by word, through
  `SpeechProvider.onWordBoundary` → `SpeechService.spokenOffset`.
* **Resuming enters a sentence part-way.** `ClipPlayback.playAt(chars)` speaks
  `sentence.substring(wordStart)` and then the following sentences whole. So the audio
  is a contiguous phrase, not a stitched-together list of words, and the learner picks
  up at the word they stopped on.
* **A resumed word is never cut in half.** The offset is snapped back to a word start,
  because a dragged scrubber lands anywhere and half a word is worse than the sentence
  jump this replaced.

Reported progress is treated as an *at least*, never as the whole truth. A platform that
reports nothing leaves the offset where playback started — still true, just as coarse as
before — so nothing depends on the callback arriving.

### The clock

Text-to-speech has no file and no duration to ask for, so **the clock is the playhead in
another unit**: `elapsed = position / rate`, `total = totalChars / rate`. Because both
are the same number read two ways, the clock and the bar cannot disagree with each other
— which is the failure a separately-run stopwatch would have produced the moment anyone
scrubbed.

The rate is **measured while the clip plays** rather than assumed: a constant to begin
with, corrected by each finished sentence, smoothed so the total does not visibly jump,
and guarded — a measurement outside half to twice the current estimate is evidence about
the platform, not about the voice, and is discarded. The total is therefore an estimate
that improves; it is honest about being one, and it is bounded.

The sentence-counter is gone. `clipPosition` remains in the strings for now but nothing
renders it.

### Two smaller things in the same player

**The words under the control name the state, not the next tap.** They used to name the
action, so a learner who had just pressed pause read *"Continue"* and understood the
clip to be running. The icon is the verb — pause, play, replay — and the line beneath it
says *Playing*, *Paused* or *Finished*.

**The track is drawn in a new token, `trackRest`.** At the theme's default strength the
unplayed part of the bar disappeared into the card, so the clip appeared to have no end
and there was no reading of how far through it the learner was. Near-white on the dark
theme, as asked; a mid grey on the light one, because white on a white card is the same
invisibility with the opposite colour.

### Found by running it, not by reading it

Two things only the simulator showed, on the screen this ADR is about:

* **The total fell from 1:59 to 1:40 in the first fifteen seconds.** The opening rate
  constant was a guess, 19% below what an iOS voice actually produces, so the first
  measured sentence corrected it in one visible step. The constant is now the measured
  16 chars/sec and the first correction is smoothed like every later one. A clock that
  rewrites itself that far is worse than a slightly wrong one.
* **`What does "fan" mean here?` rendered as `?What does "fan" mean here`.** An English
  sentence inheriting the Arabic paragraph direction loses its trailing punctuation to
  the front, because a neutral character at the end of an RTL paragraph belongs to the
  paragraph rather than to the words. It has presumably always done this; it is visible
  the moment anyone looks at the question screen.

  Fixed with `AutoDirectionText`, which pins direction from the first strongly-directional
  character. It is used where the content **may be either language and the call site does
  not know which** — a comprehension option is English, a word-question option is Arabic,
  and `OptionTile` renders both. `EnglishText` remains correct wherever the content is
  known to be English, because it states that fact rather than inferring it. Other
  screens have not been swept for the same fault.

### Consequences

* The sentence beside a question gained a track of its own. Without one a learner can
  pause and continue but cannot go *back* a few words, which is what a sentence heard
  once is most often paused for.
* `SpeechService` gained one field and no new ownership: word boundaries arrive as a
  notification rather than a callback, because a single callback field on an app-wide
  service belongs to whichever player registered last — which is not necessarily the one
  speaking.

---

## ADR-083 — Both speeds, everywhere a word can be heard

**Context.** The slow voice existed on the placement screen and, after ADR-081, beside a
Listening word question. Everywhere else — the learner's own word list, a word's detail
page, the weekly review, the tap-a-word lookup sheet, the Speaking warm-up — a word could
be heard at one speed only.

The slow voice is not a secondary feature. It is the one a learner reaches for when they
did not catch the word, which is the whole reason they tapped the speaker. A learner who
finds it on one screen and not on the next has not learned that the app has two speeds;
they have learned that this screen is missing something.

**Decided.** `WordSpeakerButtons` — the pair — replaces the lone `SpeakerButton` at every
site where the thing being spoken is a **word**. Two ids, one derived from the other, so
the two controls can never light up together and a caller cannot forget to make them
differ. The slow one draws a distinct face and names itself, so it is not a second
identical button.

Left alone deliberately: `SpeechPlayButton` and the passage players, which speak
*sentences* and already carry their own speed control.

---

## ADR-084 — A word's wrong answers come from its own sentence, not from the other words

**Context.** Every Reading and Listening session ends with one question per target word:
*what does "sell" mean here?*, four options, one correct. The wrong options were built
in `SessionContentBuilder.BuildMeaningOptions` from `words.Select(w => w.Meaning)` — the
**other target words' meanings** of the same session.

The reasoning was sound as far as it went: a learner's own words make plausible
distractors, better than absurd ones. What it missed is that the five questions then
share one set of five answers between them.

Walk a session as a learner does. Question one offers يبيع / يأكل / يشرب / يحمل; they
answer يبيع and are told it is right. Question two offers four of the same five, and
يبيع is now known to belong elsewhere. By the last word there is one option left that has
not already been claimed, and it can be chosen **without reading the question at all**.
The back half of every session was solvable by bookkeeping rather than by knowing the
words — which is the one thing this question exists to measure.

**Decided.** The generator writes each word's wrong meanings itself, from the sentence
that word appears in.

* **`prompts.py`** — `READING_SCHEMA.targets[].wrong_meanings_ar`, three per word, with
  `DISTRACTOR_RULE` saying what makes one good: drawn from *that sentence*, rulable-out
  by a learner who understood it and worth considering by one who did not; another
  reading of the same sentence, the word's own other senses, a near-neighbour, something
  that fits the grammar of the slot but not its sense. Never absurd, never arguably also
  correct, and never shared between two words. Applied to re-telling as well, which
  regenerates the questions. `reading-v4` → `v5`, `relevel-v2` → `v3`.
* **The correct Arabic meaning is sent with the word**, and the rule requires the wrong
  ones to **match its shape** — same register, roughly the same length, the same kind of
  phrase. Without this the model writes its own style of gloss and the learner's own
  stored meaning stands out by *looking different*, which is passing the question
  without knowing the word by a different route.
* **`main.py`** — `_clean_distractors` is all-or-nothing: three usable wrong meanings or
  none. A word that came back with one would otherwise be asked with two options, and
  "some" is the one answer the backend cannot act on.
* **`SessionContentBuilder`** — uses what was written, filters out anything equal to the
  correct answer (a model that repeats it would create a question with two right answers
  and one of them marked), and tops up from the old pool only to reach four. A question
  with two options is worse than a question with a weak distractor.

### What is deliberately unchanged

* **The AI fallback** leaves the field empty and gets the old pool. Its content is
  already announced as degraded (`usedAiFallback`), and the alternative is inventing
  Arabic meanings in C#.
* **The Speaking warm-up** keeps the old scheme. It measures nothing — no attempt, no
  event, no level moves (rule R9) — so elimination costs the learner nothing there, and
  it runs before any passage exists to write context-aware options from.
* **Comprehension questions** were already per-question, from the model. Untouched.

### Verified, not assumed

Pinned on both sides — `backend/tests/WordOs.Domain.Tests/SessionOptionsTests.cs` and the
`the options on a word question` group in `mobile/test/learning_loop_test.dart` — and
both were run against the old implementation first to confirm they fail on it. The
central test walks a session striking off every meaning already shown to be an answer
and requires all four options to survive at every question.

---

## ADR-085 — In Listening the word is heard and never seen

**Context.** Every Reading and Listening session ends with one question per target word.
The question was written once, for both:

```csharp
prompt: $"What does \"{word.Text}\" mean here?"
```

For Reading that is correct. The learner is looking at the word, underlined, inside its
three sentences; naming it in the question is how they know which word is being asked
about.

For Listening it destroys the exercise. The whole task is that the learner **never sees
the word**: it arrives as sound, buried in a spoken sentence, and they say what it meant
in that sentence. Printing it hands over the one thing a listener is not supposed to
have — how the word is spelled — and what is left is reading with audio attached.

This had been true since the questions were first built, and ADR-081 made it worse
without noticing: the "hear the word" control added there used the word itself as its
button label, so the screen said it twice.

**Decided.** In Listening, the word appears nowhere on the question screen.

* **The question is a key, not text.** `SessionPromptKey.ListeningWordMeaning` →
  *"What does the word you just heard mean here?"* / *«ما معنى الكلمة التي سمعتها في هذه
  الجملة؟»*, rendered client-side in the learner's own language (ADR-035). The English
  that travels beside the key is **written the same way**, so a client that does not
  recognise the key still cannot show the word. Belt and braces, because this is content
  the learner must not see rather than a formatting preference.
* **`WordPronunciation` does not draw the word.** The button reads *Play audio*, not
  `fan`. The word is still passed in — the device has to be given something to say — and
  is simply never rendered.
* **Nor does a tooltip.** A tooltip is part of the screen: a screen reader speaks it and
  a long press shows it. `revealSpelling` gates both the label and the tooltips together,
  so they cannot drift apart.
* **`revealSpelling` defaults to false.** Listening is the only caller today. The flag
  exists so a future Reading use — where the word is on screen anyway — has to *say* it
  is showing the word, rather than the rule quietly lapsing.

Reading is unchanged, deliberately. A per-skill rule, not a global one: refusing to name
the word in Reading would answer a problem Reading does not have.

### Where the word still appears in a Listening session, and why

The rule is *never seen **while it is being tested***, not *never seen*.

* **The explanation after an answer** — `"fan" means مُحِبّ. a person who admires…` — is
  shown once the item has been answered and the correct meaning already revealed
  (demo review §28). The measurement is over, and it is the moment the learner finally
  gets to see the word they have been hearing. Left as it is.
* **The result screen** lists every word of the session, as does the transcript revealed
  with it. The test is finished.

Both are judgement calls rather than consequences of the rule, and either could be gated
behind the same flag if the product owner wants the spelling withheld until Reading or
Spelling introduces it.

### Verified

`A_listening_question_never_names_the_word` and `A_reading_question_still_names_it` in
`SessionOptionsTests.cs`; `the word behind a question can be heard but never seen` and
`Reading still names the word it is asking about` in `listening_section_test.dart`. The
client test checks the button label, every tooltip, and the whole screen's text — and
was confirmed to fail against the old prompt.

---

## ADR-086 — A player belongs to the question it is on, not to the screen

**Status:** accepted · **Date:** 2026-09-17

Reported: on a Listening word question, the sentence player spoke the *previous*
question's sentence. Reproduced, and it is not an audio bug at all.

### What actually happened

Moving to the next question does not build a new player. `SentencePlayer` sits at the
same position in the tree, has the same type and carries no key, so Flutter keeps the
existing `State` and hands it a new `text`. The controller was built once, in
`initState`, from the `text` that happened to be there first:

```dart
late final ClipPlayback _clip;   // built in initState, never again
```

So the card drew question two's sentence while the voice read question one's. On a
listening test that is worse than a stale label: the audio *is* the evidence, and the
learner was answering about a sentence they could not hear.

Confirmed by the failing test before the fix:

```
Expected: '…is the programs that run on a computer. Ahmed asked a question about it…'
  Which: does not contain 'Last week Ahmed joined a small study group at her university.'
```

The backend was never at fault. `SessionContentBuilder` gives every target-word item its
own `audioText`, joined from that word's own context sentences.

### Decision

Both players — `SentencePlayer` and the passage's `_ListeningPlayer` — rebuild their
`ClipPlayback` in `didUpdateWidget` when the text changes. Disposing the old controller
silences it on the way out, so nothing follows the learner forward.

**Not** fixed with a `ValueKey(text)` at the call site, which would also work. A key is a
promise every future caller has to remember to keep, and it is invisible at the place
where it matters — the widget that owns the text is the one that can be sure. `_clip`
stops being `final`, and that is the whole cost.

`_ListeningPlayer` gets the same treatment although its text changes far more rarely:
only when the learner changes the level and the passage is written again. Rarely is not
never, and the failure mode there is the same one.

### Verified

`each question speaks its own sentence, not the last one's` in
`listening_section_test.dart` — it reads the sentence the question is carrying, plays it,
and asserts the voice spoke text from *that* sentence. Confirmed failing before the fix
and passing after. Full suite: 432 client tests.

---

## ADR-087 — Writing moves behind Spelling, and the pipeline stops advancing by position

**Status:** accepted · **Date:** 2026-09-17 · **Supersedes the order in ADR-001**

```
Reading ─2d→ Listening ─2d→ Speaking ─2d→ Spelling ─2d→ Writing
```

### Why

Writing is the only skill that asks the learner to **produce the written word
unaided**. Everything before it either shows the word (Reading), speaks it
(Listening), or accepts it spoken (Speaking); Spelling asks for the letters with
a clue and a hint ladder behind them. So a learner who cannot yet spell a word is
marked on two things at once in Writing, and the one they fail is not the one it
measures.

Put Spelling first and by the time a sentence is asked for the spelling is no
longer in question. What Writing then measures is *use*, which is what it is for.

The product owner asked for this on 2026-09-17. ADR-001 recorded that the order
is configuration precisely so it could be changed on evidence; this is that
happening, and `SkillsOrder` is the only place the new order is written.

### The part that is not a configuration change

Every word already in the pipeline was standing somewhere in the **old** order
when it changed. Advancement was positional:

```csharp
var next = config.NextSkillAfter(skill);   // index + 1
if (next is null) { /* mature */ }
```

A word sitting at Writing — the old last skill — would pass it, find nothing
after Writing in the new order, and **mature having never been asked to spell
it**. Silently, for every such word, with no error and nothing in the data to
say it had happened.

So advancement now asks what the word still owes:

```csharp
public SkillType? NextPendingSkill(WordOsConfiguration config) =>
    config.SkillsOrder.FirstOrDefault(s => SkillState(s)?.Status != Passed);
```

Correct under any order, needs no migration, and takes nothing from anyone: a
word mid-flight finishes in the order it started, and the skill it has not done
yet is still waiting afterwards. It is also the honest reading of what the
pipeline means — five skills, each demonstrated once, in a preferred order
(rule R5). `NextSkillAfter` survives for callers asking about the *order* rather
than about a word.

`ApplySessionResult` also seeds a missing `WordSkillState` rather than
`Single`-ing on it, so an order that gains an entry later cannot throw at the
moment a learner passes something.

### Verified

`A_word_in_flight_when_the_order_changes_still_owes_every_skill` builds a word
under the old order, changes the order underneath it, and asserts it goes to
Spelling rather than to Active — **confirmed failing** against the positional
version. Mirrored in the mock as `a word that is already in flight does not skip
the skill it has left`. The API suite's `AdvanceTo…` helpers were rewritten
around the new order, and `Passing_spelling_matures_the_word` became
`Passing_the_last_skill_matures_the_word`, which is what it was always testing.

The mock now seeds a word standing at Writing, because with Writing last nothing
in the demo data reached it.

---

## ADR-088 — The options, and the instruction, are written at the learner's level

**Status:** accepted · **Date:** 2026-09-17

Two rules from the product owner, one ladder.

### The options on a word question

*What does this word mean here?* had exactly one kind of answer — the Arabic
meaning — at every level from A1 to C2. That is the right answer at one end of
the ladder and the wrong one at the other:

* An **A2** learner shown four English definitions is being tested on the
  definitions. The word is not what stands between them and the answer.
* A **C1** learner shown four Arabic words is being asked to translate, which is
  easier than the word is, and is not what the pipeline claims to measure.

So the register follows the band:

| Band | Options are |
|---|---|
| A1, A1+, A2, A2+ | the Arabic meaning |
| B1, B1+, B2 | a **plain-English** definition, written for this sentence |
| B2+, C1, C1+, C2 | the **dictionary's** definition, as written |

Reading and Listening only — the two skills that ask this question.

**Where the correct answer comes from matters more than where the wrong ones
do.** At two of the three bands it comes from this service: the learner's own
Arabic meaning, or the lexicon's gloss. Only the middle band has no other source
for a plain-English line, so the generator writes it — and the prompt asks it to
*simplify the definition it was handed*, not to state the meaning itself. That is
the narrowest place to let a model write an answer key, and it is still a model
writing one; it is flagged here rather than buried.

The wrong answers are the generator's at every band, written from the sentence
(ADR-084), and now written in the band's register: at B2+ the correct option is a
real dictionary line, so three conversational explanations beside it would make
the answer identifiable by its style alone. The prompt says so in those words.

**One language per question, always.** A single Arabic option among three English
ones gives itself away by script. So the fallbacks are paired: the top-up pool is
the learner's other words' *definitions* on an English band and their *meanings*
on the Arabic one, and there are two filler pools rather than one. A word with no
English definition at all falls back to Arabic for that word — a guard, not a
path, since a word added outside the lexicon still gets a definition from the
meaning checker (ADR-075), but an empty correct option is a question with no
right answer.

Re-telling a passage at another level moves the options with it. A learner who
dropped to A2 because the text was too hard did not ask to keep answering in
English.

### The instruction on a Writing task

Same ladder, one cut: **B1 and above is set in English.**

Below it the instruction is scaffolding — it has to be understood instantly or
the task becomes a reading test with a writing task attached. At B1 a learner
about to write English has already started in it, and an instruction in the
language of the work is one less translation between them and the task.

The decision is the **server's**: the session carries `instructionLanguage`,
derived from two facts already stored, so there is nothing to migrate and nothing
that can fall out of step with the level the session was built at. The client
renders what it is handed (rule R1) and applies it to the instruction **only** —
the buttons, headings and errors around it are the app talking, and the app keeps
speaking the learner's language. A value this build does not recognise leaves the
learner's language alone, because an instruction in the wrong language is worse
than a plain one.

### Which level

The **session's own** level — for Reading and Listening the learner's standing in
that skill, for Writing theirs in Writing. The product owner said "the person's
level" and then, for Writing, "their level in writing"; the per-skill level is the
narrower reading and the one already driving content difficulty, so a learner
strong at reading and weak at writing is met correctly in both.

### Verified

* `Every_rung_of_the_ladder_lands_in_a_band` names all eleven levels
  individually — a ladder read with `>` instead of `>=` moves exactly one band
  and nothing else would catch it.
* `The_easier_bands_answer_in_Arabic`, `…_the_generators_plain_English`,
  `…_the_dictionarys_own_words`, plus the two fallbacks:
  `A_word_with_no_English_definition_falls_back_to_Arabic` and
  `An_English_band_with_no_generated_options_still_asks_four`.
* AI service: the schema carries only the fields its band will use, each prompt
  asks for its own register, and the English options are all-or-nothing exactly
  as the Arabic ones are.
* Client: `the easier bands answer in Arabic` / `from B1 up the options are
  English, and all four of them are`; `the writing task is set in English in the
  Arabic app` — **confirmed failing** with the wiring removed — and `everything
  around the task still speaks Arabic`, which is the half that would be easy to
  get wrong.
* Live against Gemini, all three registers: `river` at B1 came back as
  *"a large natural stream of water"* against *a paved road / a mountain range /
  a thick forest*; at B2+ the wrong answers were full dictionary lines
  (*"a paved path used by pedestrians in a city center"*) and no plain-English
  answer was written at all, because the lexicon supplies it.

---

## ADR-089 — A word ripens before the challenge may ask about it

**Status:** accepted · **Date:** 2026-09-17

The weekly review asked about *everything added in the last seven days*. A word
added this morning was tested this evening.

That is not a review. The learner still has the word in mind, answers correctly,
and the score — the only thing this feature produces, because rule R9 forbids it
from touching anything else — says nothing about retention. It was measuring
short-term memory and reporting it as learning.

### The rule

* A word becomes reviewable **`WeeklyReviewMaturityDays` (7) after it was
  added**, not before. A learner's first week therefore has no challenge in it,
  and the first one opens on a date that can be named in advance.
* A word **leaves** the pool once the challenge has asked about it — right or
  wrong. A word the learner got wrong is not owed another challenge; it is owed
  the skill it is still standing on.
* Unreviewed words **carry over** instead of expiring. Somebody who skipped last
  week finds last week's words waiting beside this week's, because the
  alternative is a feature that silently forgets the words of anyone who was
  busy — and those are exactly the words worth asking about.
* Carrying over needs a ceiling or it becomes a punishment for missing a week.
  **`WeeklyReviewMaxWords` (50)** caps one sitting; the rest stay ripe and are
  offered again. Fifty is the product owner's number and lives in configuration,
  because the right ceiling is a judgement about people rather than about
  software (rule R3).

The pool is ordered **oldest first**. A word that has been waiting a fortnight is
the one most likely to have been forgotten, and it is the one the cap must not
keep pushing to the back week after week.

Ripeness is deliberately not a question about the word's *pipeline* state. A word
still on Reading counts exactly as much as one that matured: what is being
measured is what the learner remembers, not how far the word travelled.

### What the learner sees

The hub card is shown **while the challenge is still coming**, muted, with the
day on it — not hidden until it opens. A card that simply is not there teaches a
learner in their first week that the feature does not exist.

When more than one sitting is ripe, the card says so *before* they start, rather
than letting a second group arrive as a surprise after they finish what they
thought was everything.

`nextAvailableAt` on the hub stopped being always-null and became the date the
first word ripens. `NO_WORDS_IN_PERIOD` became two codes, because "not yet" and
"you have reviewed everything" are different situations and only one of them has
a date to wait for.

### Verified

`WeeklyReviewPolicyTests` — twelve cases covering ripening on the exact day,
leaving the pool for good, deleted words, carry-over across a skipped fortnight,
oldest-first ordering, the ceiling at fifty, the ceiling as configuration, and
the three answers to "when does it open". Mirrored in the mock and covered from
the client in `weekly_challenge_test.dart`.

---

## ADR-090 — Twenty notifications, and a rule about what may be said in one

**Status:** accepted · **Date:** 2026-09-17 · **Extends ADR-076**

There were three reminders, each a single fixed sentence, each stating a count:
*"4 words are ready to practise."* Twice a day, for ever.

A notification is the whole decision about whether the app is opened today. The
same sentence twice a day for a fortnight stops being read — and it stops being
*read* before it stops being *noticed*, which is worse, because the learner keeps
receiving it and has learned that it never says anything.

So there are now twenty lines, each gated on a fact.

### The rule that shapes everything

These are **local** notifications (ADR-076). The phone fires them with no
network, usually with the app closed, up to a week after the server handed them
over. Nothing can be corrected once scheduled.

**So a line may only rest on a fact that will still be true when it fires.**

"You practised yesterday" is knowable for this evening and a guess by Thursday. A
streak of four is a streak of four today and unknown after that. Every message
that looks backwards is therefore restricted to **today's slots**, and the days
beyond are filled only from facts the schedule itself projects: what will be due,
what will have ripened, what the learner owns. A phone re-fetches on every app
open, so the learner who opens the app keeps getting the good ones, and the one
who does not is never lied to.

A reminder that lies is worse than a dull one. Somebody told they are on a
five-day streak on their third day away has learned that the app does not know
them, and no better sentence next week recovers that.

### The catalogue

Eleven for words being due — the plain count, the single word, the five-minute
framing, morning and evening, a streak, a streak about to end tonight, the day
after a good one, a welcome back, a level that rose, words one skill from
finishing. Four for a quiet day, two for an empty vocabulary, two for the weekly
challenge, and one placeholder that is never reached.

How they are written:

* **Never a reproach.** "You haven't practised in 4 days" is accurate and it
  makes the app a thing to avoid. The same fact says *"your words are exactly
  where you left them."*
* **Name the size of the ask, not the size of the backlog.** "Five minutes" is a
  decision somebody can make at a bus stop; "23 words waiting" is a decision to
  postpone.
* **The number is a guest, not the host.** Most lines carry none. The count is
  still there, in the one line that is about it.
* **The two challenge lines carry no number at all**, by instruction: how many
  words ripened is the product's bookkeeping, and a count would make a quiet week
  look like a failure.

### Choosing

Candidates are ranked by what the learner most needs to hear at that moment; the
best one not used in the last four picks wins; ties break on a hash of the
learner and the date, so two people do not read the same script. Deterministic
throughout — the same facts compose the same week, so a refresh does not
reshuffle what the phone already holds.

**Two exceptions are pinned and never rotated**, because rotation must not
swallow a message whose whole value is the moment it arrives: the day the
challenge opens, and the evening a run of days is about to end. A test caught the
second of those being traded for *"you are on a five-day streak"* — the same fact
with the urgency removed.

The challenge is announced **twice at most**: once when it opens, and once the
next day if more than one sitting is waiting. Not daily. A test caught it saying
"your challenge is ready" four times in a week, which is how a learner learns to
swipe a notification away without reading it — and they do not learn that for one
message only.

`kind` survives beside the new `message` on purpose. A client that has never
heard of a key still says something true from the kind alone, and the server
ships far more often than the phones do.

### Verified

`ReminderComposerTests` — fifteen cases, including the staleness rule across a
whole week, the two pinned messages, the announcement cap, determinism under
refresh, and that a week contains more than two distinct lines. Client side:
every line renders non-empty in both languages with no unsubstituted
placeholders, the challenge lines never contain a number, and an unknown key
falls back to the kind. One defect was found by these tests rather than by
reading: with one word due the no-repeat window exhausted the list and an
ordinary Saturday morning said *"open the app"*.

---

## ADR-091 — The challenge shows a bar, not a position

**Status:** accepted · **Date:** 2026-09-17

The weekly challenge showed `Remaining: 47` beside a linear progress bar.

Both are honest and both are discouraging. "3 of 50" tells a learner who has just
started that they have forty-seven to go, at the exact moment they are deciding
whether to continue — and a linear bar answers their first question by moving two
per cent, which reads as *nothing happened*.

### The decision

The count is gone. The bar stays, it animates, and it is **front-loaded**: the
first five words are worth the opening 35% of it, and the remainder is shared
evenly across everything after them.

```dart
if (done <= 5)  return 0.35 * (done / 5);
return 0.35 + 0.65 * ((done - 5) / (total - 5));
```

The learner most likely to give up is the one who has answered three questions
and cannot see that they have. That is the moment the curve is for.

### The limits on it

* It **never overstates the end**: at `done == total` it is exactly 1, so the bar
  arrives full at the same moment the challenge does. The flattery is all in the
  middle, where it costs nothing.
* A **short** challenge stays linear. With five words the "opening stretch" would
  be the whole thing and the second answer would nearly fill the bar — the same
  lie in the other direction.
* It is **presentation only**. The score, the queue, the requeue and the result
  are counted honestly and are not touched by it (rule R9). What is bent here is
  the width of a rectangle.

### Verified

`weekly_challenge_test.dart` — starts at zero, arrives at exactly one, moves
monotonically for every length, gives the opening five more than twice the middle
five, stays linear when short, and divides by nothing. The screen is pinned by
`learner_journey_test.dart`, which now asserts the bar is present *and* that no
"Remaining" count is.

---

## ADR-092 — Pipeline order is asked for, never assumed from the enum

**Status:** accepted · **Date:** 2026-09-17

ADR-087 moved Writing behind Spelling. The hub obeyed immediately, because it
builds its cards from `config.SkillsOrder`. Every *word* went on being drawn in
the old sequence, because its skills were sent like this:

```csharp
w.Skills.OrderBy(s => s.Skill)      // the enum's declaration order
```

`SkillType` still declares `… Writing, Spelling`, so My Words, the word detail
journey and the Owner's dashboard all showed a pipeline the app no longer ran.
The hub said one thing and the word said another, and nothing failed.

### The decision

Order by `config.PipelinePosition(skill)`. The enum keeps its declaration —
renumbering it would hard-code into a language construct the very thing ADR-001
made configurable, and it would be wrong again next time.

The three sites were `WordEndpoints` (a word's skills) and two in
`AdminEndpoints`. A skill missing from the order sorts last rather than
throwing: a misconfigured list should mis-sort a row, not fail a request.

### Why it was invisible

The failure mode of an ordering bug is a screen that looks fine. Both tests
added here compare against `SkillsOrder` itself rather than against a literal,
so they cannot drift with it — and the word one was confirmed failing before the
fix.

---

## ADR-093 — Nobody is signed out except by signing out

**Status:** accepted · **Date:** 2026-09-17

Android learners were being asked to sign in again "after a while", with nothing
to explain it. There were **two** causes, and both are about a network rather
than about a session.

### 1. A failed refresh is not a refused refresh

Access tokens last fifteen minutes, so the refresh exchange runs several times a
day for anybody who uses the app. It was written like this:

```dart
} on DioException {
  return false;          // → onUnauthorized() → tokens deleted
}
```

`DioException` is *every* failure: a timeout, a dead socket, DNS, a 502, a server
still cold-starting. So opening the app on a weak connection more than fifteen
minutes after last using it **deleted the learner's credentials**. Nobody had
refused anything.

It now answers in three, not two — `renewed`, `rejected`, `unavailable` — and
only `rejected` (a 401 or 403 from the server, or no refresh token at all) ends
the session. Everything else leaves the tokens exactly where they are and
reports a network error. The access token is still expired, so the next request
tries again — which is right, because by then the connection may be back.

### 2. A lost reply is not a leak

Refresh tokens rotate, and presenting a used one means it leaked, so the whole
family is revoked (`docs/07-SECURITY.md` §2). There is exactly one honest way a
learner's own app does that: **the exchange succeeded here and the reply never
arrived.** The app still holds the old token and retries with it.

On a phone that is not rare, and the consequence was a *permanent* sign-out —
the family is revoked, so waiting does not help either.

The server now honours that retry when, and only when, the replacement token has
**never been used**: nobody received it, which is the tell. It retires the orphan
and issues a pair the client will actually get. A replay after the real client
has used the replacement, or later than `RefreshReplayGraceSeconds` (60), still
revokes the family.

> **This is a deliberate loosening of a security property and the product owner
> should know it.** Inside that minute, a refresh token stolen *in transit*
> could be redeemed without tripping the revocation. Against that: the token
> travels over TLS, the realistic theft vector is a compromised device or backup
> — where the attacker redeems at an arbitrary later time and is still caught —
> and the strict rule was permanently signing out real learners who did nothing
> wrong. Setting the window to `0` restores the old behaviour exactly, with no
> deploy.

The existing test was rewritten rather than deleted: it now replays *after* the
successor has been used, which is the real leak signature, and still asserts the
whole family dies.

### What was not the cause

Checked and cleared: the refresh token's expiry **slides** — each exchange issues
thirty fresh days — so an active learner never ages out. The keystore write is
non-fatal by design, which is right: a session that works for this run is better
than one refused because storage hiccuped.

---

## ADR-094 — The screen keeps up with the server, and nobody has to remember

**Status:** accepted · **Date:** 2026-09-17

Two complaints with one shape: *the app knows, and the screen does not.*

### Screens were refreshed by whoever remembered to

Each write invalidated the providers its caller happened to know about. Adding a
word refreshed the hub and not the word list — so a learner added a word, opened
My Words, and it was not there. Fourteen call sites, each a thing a future change
has to remember, and the failure is silent because a stale screen looks exactly
like a correct one.

Now: **every write announces itself, and every screen that reads server state
listens.**

```dart
Future<Map<String, dynamic>> _post(...) async {
  final res = await _guard(...);
  onChanged?.call();          // → serverRevisionProvider.bump()
  return _asMap(res);
}
```

The announcement lives at the bottom of the stack, where the HTTP verb *is* the
classification — a method added tomorrow that goes through `_post` is announced
without anyone deciding that it should be. The mock carries the same list by
hand, because every widget test runs against it and a screen that failed to
refresh would pass its tests and fail on a phone.

One counter, not one per resource: a finer signal means deciding at each write
which screens it could affect, which is the same guesswork this replaces.
Refetching a screen nobody is looking at costs nothing, because `autoDispose`
means nobody is listening to it. Only on success — announcing a refused write
turns one error into a burst of requests.

The fourteen hand-written invalidations are gone. What remains under `onRetry` is
a different thing: retrying a read that failed.

### Errors waited to be pressed

A learner who walked into a tunnel got "something went wrong" — and it stayed,
over a working network, until they found the retry button.

`ErrorView.from(error, strings)` now picks the sentence from the failure, so
somebody with no signal is told they have no signal; and while it is on screen it
**retries on its own**, at 2s doubling to 20s, and immediately when the app is
resumed. Only for failures that can heal (`isRetryable`) — a word that does not
exist will refuse identically for ever, and retrying it is a request every few
seconds that can never do anything. The button stays for anyone who wants to
press it: waiting for a timer you cannot see is its own kind of stuck.

### The test harness had to change with it

Widget tests ran against the mock's artificial latency, which exists so loading
states are visible while developing. With every write now refetching, one of
those timers started in the last frame outlives the widget tree and fails the
test with "pending timers" — a message about the harness, not about the app. The
shared harness now runs at zero latency, which is what `latencyScale` was added
for, and `stress_navigation` asks for it back, because a test about a learner
mashing a tile *while a session loads* has no subject without it. Two hand-copied
override lists in `qa_sweep` were replaced by the shared one, which is how they
came to drift in the first place.

## ADR-095 — One question, one button

**Context.** A Writing question put a "check" button inside the scrolling body,
under the text field, while the foot of the screen carried a "next" that stayed
disabled until the answer came back. Two buttons for one decision — and the dead
one was the one under the thumb.

Reading and Listening had already solved this (ADR-064): a tap on an option is
only a choice, and the footer reads "check" until the verdict is on screen, then
"next". Writing was left out because its answer is typed rather than tapped,
which is a difference in how the answer is *given*, not in what the learner is
deciding.

**Decision.** The footer is the only control on a question, for every skill that
ends in an explicit check. It reads "check" until there is a verdict and
"next" — or "finish" on the last one — afterwards. `_hasCheckStep(item)` says
which questions have that first step and `_checkAction(item)` says what it does,
so the branch is over the *item*, not over the skill.

The button is disabled while there is nothing to check: an unchosen option, or
an empty box. `_submitWriting` already refused an empty sentence, so pressing it
did nothing — refusing it in the button is what stops it looking pressable.
It is rebuilt as the learner types, from the text field's own notifier rather
than a `setState` per keystroke, because `_next()` clears that controller inside
a `setState` and a listener would re-enter it.

**Spelling keeps its own button.** With letter tiles it lives in the same row as
undo and clear, beside the tiles it commits; moving it to the foot would separate
it from the thing it acts on. Free typing shares that layout. Left as it is, and
named here so the next reader knows it was a decision rather than an oversight.

**Consequences.** One control to look at, at the bottom of the screen, doing one
thing at a time. `test/writing_answer_flow_test.dart` holds it: three of its
assertions were confirmed failing against the two-button version.

## ADR-096 — A second dictionary, beside the first

**Context.** The product owner called the lexicon "سيئ جداً"، and the example
was `sell = باع` — "مين باع sell؟". Querying the table found three separate
faults, not one.

**1. The sense order was invented here.** `FrequencyRank` combined the CEFR band
with WordNet's own sense ordering, because no source in the first build shipped
a frequency list. WordNet's order is not a frequency order, so the top row — the
one the learner sees — was:

| typed | shown first | its definition |
|---|---|---|
| `go` | تُوُفِّيَ | *pass from physical life* |
| `eat` | أَكَلَ | *cause to deteriorate due to the action of water, air* |
| `sell` | أَقْنَعَ بِـ | *persuade somebody to accept something* |
| `house` | لعبة بيت بيوت | *play in which children take the roles…* |

**2. Verbs were cited in the past.** Arabic WordNet gives the past tense, which
is the citation form of every Arabic dictionary — and against the English
infinitive it reads as a tense that is not there.

**3. CEFR was applied per (word, POS) to every sense.** All thirty-odd senses of
`go` were A1, including "be abolished or discarded"; and 84.7 % of senses had no
band at all.

**What was looked for.** No free API supplies all four of word, learner
definition, Arabic gloss and CEFR. **Cambridge** is the only source with CEFR
**per sense** (English Vocabulary Profile) alongside an official English–Arabic
dictionary, and it is commercial: a licensing conversation, a 30-day evaluation
key, and explicitly no free access for research or prototyping. The product
owner is approaching them in parallel; this decision is what ships meanwhile.

**Decision.** A second edition, built from Wiktionary via `wiktextract`, living
in `lexicon_entries` **beside** the first. Three rules, each answering one of
the faults:

1. **Order comes from the dictionary**, and rank comes from a real frequency
   list (`hermitdave/FrequencyWords`, top 50k). Wiktionary's senses are ordered
   by people, so `sell` leads with *"To transfer goods or provide services in
   exchange for money"*.
2. **The Arabic form follows the English inflection** — the owner's own
   formulation, sharper than "use the present tense":
   `sell → يبيع`, `sold → باع`, `selling → بَيْع` (the مصدر, which is what an
   -ing form is). The non-past is not generated: it is read from the Arabic
   Wiktionary entry's own paradigm (`non-past`, `noun-from-verb`). A verb with
   no paradigm recorded keeps the form it came with — conjugating by rule would
   invent Arabic, and an invented word is worse than a dictionary one.
3. **A band reaches the senses it was measured on** — the first two — and no
   further.

Dialect glosses are dropped: a learner studying for a CEFR band is studying
فصحى. Wiktionary tags most of them, and not all — one arrives as the bare
string `"Tunisian Arabic كلِا"` — so the name is matched in the tag *and* in the
word.

**Both editions stay.** `WordOsConfiguration.LexiconEdition` says which one is
served, so going back is a setting, not a re-import (rule R3). It stays on
`oewn-awn` until the new build has been imported and measured: a default naming
an edition that is not in the table is an app with no dictionary at all, which
is a worse failure than the one being fixed.

**Search and resolution are separated, and only search is edition-scoped.**
`db.ActiveLexicon` reads the active edition and is what decides *what a learner
may add* — lookup, search by Arabic meaning, defining a tapped word.
`db.LexiconEntries` stays edition-blind and is what reads the row behind a word
a learner **already owns**. Filtering that too would mean switching dictionary
quietly broke every vocabulary built under the other one. The distinction has a
name on the context rather than a rule to remember at each of the twelve call
sites.

**Consequences.** Measured on 60 common words before any of this was built: 98 %
carry an Arabic gloss and the leading sense was right in every one. It is not
uniformly better — `polite → أَدِيب` is worse than the old `مؤدب`, `deliver`'s
leading sense is archaic, `expand → يَوْسُعُ` is the wrong verb pattern — which is
why the comparison is a report (`tools/lexicon/compare-editions.sql`) and the
switch is a setting. Eleven tests pin the rules.

## ADR-097 — The rebuild keeps the old dictionary underneath it

**Context.** ADR-096 decided the second edition. Measuring it against the 50k
frequency list found the thing no sample of sixty words could have shown:

| frequency band | `oewn-awn` | `wiktionary` alone |
|---|---|---|
| top 1,000 | 85.4 % | 84.5 % |
| 1k–3k | 80.9 % | 69.9 % |
| 3k–5k | 73.9 % | 58.0 % |
| 5k–10k | 66.5 % | 46.3 % |
| 10k–50k | 48.8 % | **17.7 %** |

Wiktionary's English→Arabic translations are excellent and **concentrated in
common vocabulary**. Shipping it alone would have traded "the wrong meaning" for
"no meaning", which is not obviously a trade a learner wants: a word they met in
a passage and cannot add is as much of a dead end as a word glossed `تُوُفِّيَ`.

**Decision.** Do not choose. The import fills its own gaps from the first
edition — that row, its provenance, its Arabic — marked `fallback=oewn-awn` and
ranked in a band above every genuine row, so a real entry always wins and the
fallback only answers where there would otherwise be silence.

| frequency band | `oewn-awn` | **now** |
|---|---|---|
| top 1,000 | 85.4 % | **92.2 %** |
| 1k–3k | 80.9 % | **81.9 %** |
| 3k–5k | 73.9 % | **74.2 %** |
| 5k–10k | 66.5 % | **67.1 %** |
| 10k–50k | 48.8 % | **49.4 %** |

Better in every band, on quality and on coverage, which is what let the setting
move to the second edition rather than waiting.

**Two ordering rules came out of the same measurements.**

*The part of speech the dictionary leads with wins.* `go` came back as `غُو`,
the board game: two entries, equally common word, both a first sense, and the
tie fell to the sense id where `n` sorts before `v`. Wiktionary's page is
ordered verb, noun, adjective, and only fourth the game — the same human
ordering the senses already had — so the rank now carries it:
`word × 10,000 + entry × 1,000 + sense × 10`. Nothing in it is invented, which
is the whole difference from the first edition's rank.

*What the learner typed beats what merely starts with it.* Typing `go` answered
`goodbye`: the authored closed-class words carry a rank of −1 so that `is` and
`the` stay findable, and −1 beats every measured frequency there is. A prefix
search now puts an exact match first, whatever the ranking says.

**Consequences.** The dry run earned its keep: reading the wrong translation
field (1,951 senses instead of 20,613), `taked`/`runed`/`wining` from blind
spelling rules, and archaic `readen`/`putten` were all caught before a row was
written. Two more — `غُو` and `goodbye` — were caught by querying the imported
data and by calling the live API, which is why both are now tests.

The test host pins `LexiconEdition` rather than following the shipped value:
every dictionary fixture in the suite is written with `LexiconEntry.Create`,
whose rows carry the first edition, and they all went invisible the day the
setting moved. `LexiconEditionTests` covers the setting itself instead — that a
search reads only the named edition, that moving it back restores the first
dictionary, and that a word a learner owns resolves whichever edition it came
from.

## ADR-098 — Eight questions, and the two that carry one answer each

**Context.** The product owner's words were "تحديد المستوى كثير أسئلة" — and
"نوصل في النهاية بنخليه بس تقريبي". Twenty-odd questions to produce a number
that the level engine starts overwriting from the first real session, on a
screen a learner meets before they have used the app once. The ask was exact:
three Reading, three Listening, one Writing, one Speaking, no Spelling.

**Decision.** The test is eight questions.

| skill | questions | how they are chosen |
|---|---|---|
| Reading | 3 | easiest first, then climbing with the estimate |
| Listening | 3 | the same ladder |
| Speaking | 1 | pitched at what Reading and Listening showed |
| Writing | 1 | likewise |

`PlacementConfig` carries all of it (rule R3): `SkillOrder` without Spelling,
`CefrLimits = (3, 3)`, `ProductionLimits = (1, 1)`, `EstimatedTotalItems = 8`.
There is no stopping rule left — at three items it could only ever cost a
question, and a test whose length varies between learners is harder to describe
honestly before they start it. `PlacementVersion` is 3, because a v3 band rests
on much less evidence than a v2 one and the two must not be compared.

**Spelling leaves the test.** Its four questions never produced a level. They
chose between letter tiles and free typing — a starting affordance the first
real spelling session settles anyway. Everyone now starts on tiles, which is
the supported mode, and is promoted from there. The six items stay in the bank
and the diagnostic stays in the API payload reading 0 of 0: putting the ladder
back is `SkillOrder` plus its limits, not re-authoring six questions.

The nine grammar items go with it. They were extra *Writing* questions that also
counted as evidence for Speaking, and Writing now has room for one question,
which has to be a written one.

**Three things had to change for eight questions to be honest.**

*A wrong answer may hold the level or lower it — never raise it.* "Closest
remaining difficulty" pulls upwards from the floor: one missed A1 item leaves
the estimate well above A1, so the nearest item left is A2 and the second
question is harder than the first. Over six questions that corrected itself.
Over three it is most of the test. The reach is now capped by the difficulty of
a question just missed as well as by the band rule.

*Three floor items per receptive skill, not two.* With that cap, a learner who
missed both A1 Reading items had nothing left to be asked and their test quietly
ended two questions short — the learner who most needs it not to give up on
them. `rd_a1_3` and `ls_a1_3` are new.

*A one-question skill borrows the location of the estimate, not its certainty.*
Measured, the old rule placed every learner alive between A2+ and B2 on Writing
whatever they wrote, including one who answered the entire test wrongly: a
single response cannot outweigh the population prior. So Speaking and Writing
are asked last, their one item is pitched at what the rest of the test showed
rather than at the floor, and their band is estimated against a prior re-centred
on that — keeping its usual width, so the produced answer still moves the band
about as far as one answer should. The reported **confidence** comes from that
one answer alone and stays near zero, which is what puts "مبدئي" on the row.

This is the one place skills are not measured separately, and it is named here
rather than left to be discovered. Reading and Listening have three answers each
and are untouched.

**Consequences.** All wrong now places A1 across the board; all right places
B2+/C1+; answering the receptive half well and the written half badly moves
Writing down about a band. The learner is told, on the result screen and in the
API summary, that this is a first reading from eight questions and that they can
change any of it in Settings — which they can, per skill, from the level card
there.

## ADR-099 — The weekly challenge asks again until you get it

**Context.** "الكلمة لما نجاوبها صح خلاص ما ترجع، إلا الكلمات اللي جاوبتها غلط."
Being *asked* was what retired a word from the challenge — `LastReviewedAt`
set, ripeness gone, whatever the answer had been. So the one word a learner had
just proved they did not remember was the one word the challenge never mentioned
again, and the feature quietly tested everything except what was forgotten.

**Decision.** Recalling a word is what retires it; being asked is not.

- `Word.ReviewPassedAt` records the first correct recall, and only a word with
  one is out of the pool. It is set once and never withdrawn.
- Ripeness anchors on `LastReviewedAt ?? AddedAt`, so a missed word gets its own
  week to be forgotten in before it is asked again — not tomorrow, which would
  make the challenge the same lesson again (the fault ADR-089 exists to fix).
- **First attempt only.** A word rescued on the retry was not remembered. That
  is already the standard `WeeklyScore` is computed to (rule R9); it would be
  strange for the same answer to fail the score and pass the retirement.

Rule R9 is untouched. Neither field moves a word through the pipeline, and
nothing here changes a skill, a schedule or a level.

**The migration backfills.** Without it the change resurrects every word every
learner has ever answered — ripeness would restart from their last review a week
later. The evidence was already stored: `weekly_review_items.FirstAttemptCorrect`
says which ones were named right, and those get a `ReviewPassedAt`. Words
answered wrongly are deliberately left null. They are what this ADR is for.

**Consequences.** A learner's backlog is now the words they have not yet
recalled, which is also what the hub card counts and what the daily reminder
projects. "Nothing to review" means they have recalled everything they added,
and the message says that rather than "already reviewed". Two API tests hold the
behaviour end to end: one right and one missed-then-rescued word, a week later
exactly the missed one comes back; and a word recalled first time is not asked
again ten weeks on.

## ADR-100 — Spelling is spelled with letters, never with a keyboard

**Context.** From the product owner, on seeing a text field in a Spelling
session: "من قال لأهلك إنك تخليها بالكيبورد نكتب؟ … نحنا حذفنا الكيبورد."

They were right twice over. It was a decision nobody had taken — B2 and above
had been typing since `MVP Core §33–34`, on the reasoning that an advanced
learner does not need letter support. And it does not work: a phone keyboard
autocorrects, predicts and completes. The learner taps a suggestion and the
exercise has measured the keyboard.

**Decision.** Every spelling item is assembled from letter tiles, at every
level. `SessionContentBuilder.BuildSpellingItems` no longer takes a preferred
mode and always issues `LETTER_TILES` with a padded pool; the client's text
field is deleted, so there is no path left that raises a keyboard.

Difficulty was never the input method's job anyway — it is the **hint ladder**,
which already starts at the rung that suits the learner (ADR-032): C1 at the
dictionary definition, A1 at the Arabic translation. That is where an advanced
learner is stretched, and it cannot be autocompleted.

**`SpellingInputMode.FreeTyping` stays in the enum** and nothing produces it. It
is a stored string in `user_skill_levels` and `session_items`, and removing the
name would make existing rows unreadable — a retired value, not a live one.
`SpellingSupportMode` likewise remains on the level row and is now always
`LETTER_TILES`, which is what the shortened placement already wrote for
everybody (ADR-098).

**Consequences.** One way to answer a spelling question, and it is the way the
learner already knows. `An_advanced_learner_still_spells_with_letters_not_a_keyboard`
walks A1, B2 and C2 through a session and was confirmed failing against the old
builder.

## ADR-101 — The meaning is the learner's; the word is not

**Context.** "نقدر نعدل معنى الكلمة… الكلمة create كانت أنشأ، نقدر نعدلها إلى
يصنع." And the limit, in the same breath: "ضروري تكون بنفس المرادف… لكن مثلاً
create، قمت سويتها يحجز، لا — النظام يقول له لا، هذه كلمة ثانية."

A learner could already write their own meaning when **adding** a word
(ADR-072), and delete a word they regretted (ADR-071). What they could not do
was change their mind later — and the thing they most want to change their mind
about is a machine-joined Arabic gloss they have since understood better. The
only way out was delete and re-add, which throws away every day of the journey.

**Decision.** `PATCH /api/words/{id}/meaning`. The meaning may change; the word
may not.

**Nothing about the journey moves.** Not the state, not the current skill, not a
skill's status, attempts or schedule, not the exposure count, not `AddedAt`.
That is the entire value of the feature — `Word.ChangeMeaning` touches the
meaning fields and appends a `MeaningChanged` event, and a test snapshots the
whole journey across a change.

**Two authorities answer "is this still the same word", in this order.**

1. **The dictionary, when it recognises the wording.** It is the only thing that
   can *name* the English word a meaning belongs to, which is the difference
   between "that is wrong" and "that is `book`". A wording that belongs to
   another word is refused with the candidates attached, and **there is no
   override** — insisting is precisely how one word would silently become
   another.
2. **The checker, when the dictionary has never seen the wording.** It can only
   judge the pairing, not name an owner, so its refusal is the softer one the
   learner may overrule with `acceptAnyway` — the same bargain ADR-074 struck on
   the way in, and the disagreement is recorded the same way.

**The sense travels with the meaning** when the new wording is another sense of
the same English word: leaving the old sense id behind would keep an English
definition describing the meaning the learner has just rejected. The learner may
already own that sense, so the identity `(UserId, SenseId)` is checked first.

**Swapping is one request, because it is one decision.** With
`replaceWithSenseId`, the old word is deleted (softly, ADR-071) and the new one
added in the same transaction, starting at Reading with nothing passed. A
learner who agreed to a swap must not end up holding both words, or neither.
Starting from the beginning is not a penalty — it is the truthful statement that
they have never been tested on this word.

**Consequences.** Nine API tests and six in the app. One crash avoided on the
way: the dialog's `TextEditingController` was first created and disposed per
edit, which disposes it while the dialog is still animating away and takes the
app down on the next frame — the same fault ADR-036 fixed in the dashboard. It
belongs to the screen and is disposed with it.

---

## ADR-102 — English definitions keep their direction, and the dictionary switch has an order

**Date:** 2026-09-28 · **Status:** Accepted

Three findings from a full check before migrating production, recorded together
because each one passed every test until something other than a test looked.

### An English definition inside the Arabic interface

The add-word results, the word detail and the in-session lookup sheet drew the
dictionary's English definition with a plain `Text`. It inherited the Arabic
interface's right-to-left direction, and a trailing neutral character in an RTL
paragraph belongs to the paragraph rather than the words — so every definition
was drawn with its punctuation at the front: `.money`, `:To move`. Seen on the
simulator the day the rebuilt dictionary, with its full-sentence definitions,
reached the screen.

`EnglishText` already existed for exactly this, and its own documentation
describes this exact symptom. It was simply not used on these three cards. It is
now — with one deliberate difference: `EnglishText` left-aligns, which would tear
the definition away from the Arabic meaning above it in a card whose every other
line hangs from the right. So the *direction* is English and the *alignment*
follows the interface (`EnglishText.interfaceStart`). Direction is what moves the
punctuation; alignment is only where the block sits.

Pinned by a widget test confirmed failing against the old code
(`TextDirection.rtl` where `ltr` was expected), and checked on the simulator
after a clean rebuild — a signalled hot restart had not loaded the change, and
the screen went on showing the bug while the tests were green.

### A contract field that was never sent

`docs/05-API-CONTRACT.md` listed `skillIndex` in placement progress. Starting a
placement on the running service showed it has never been sent — not by this
version, not by the first. Harmless: every client reads it as `?? 0` and no
screen uses it. The contract now says so rather than silently dropping it, so
nobody builds a screen on it.

### The dictionary switch has one safe order

Search reads only the configured edition, whose default is `wiktionary`, and
production holds none of those rows. Three plausible orders each break
something a learner would see — failing queries, empty search, or both
dictionaries mixed into one list (the old code does not filter by edition).
`docs/09-DEPLOYMENT.md` §4½ now gives the one order in which every intermediate
state is the old app behaving as it did: migrate, pin `oewn-awn`, deploy, load,
switch. Rollback is the setting.

The old app on learners' phones was checked against the new contract rather
than assumed compatible: every change is additive or keeps the old field
(`spelling` stays at 0 of 0, `kind` beside `message`).

---

## ADR-103 — Ship on the old dictionary; the default must be safe to deploy

**Date:** 2026-09-28 · **Status:** Accepted · **Amends ADR-096**

The product owner's decision: publish the app update now, on the dictionary
production already has, and load the rebuilt one after the Neon compute
allowance resets on 1 October (ADR-077).

That made the default of `WordOs:LexiconEdition` a hazard. It was `wiktionary`,
production holds none of those rows, and search reads only the configured
edition — so pushing this code would have given every learner an empty search.
Pinning `oewn-awn` in Render before the push would work, as a step somebody
must remember, on the deploy where forgetting is invisible until a learner
searches.

So the default is `oewn-awn` again, and `appsettings.Development.json` asks for
`wiktionary` — which only the local stack loads (`ASPNETCORE_ENVIRONMENT` is
`Production` in the Dockerfile). Production switches with
`WordOs__LexiconEdition=wiktionary` after the load, and rolls back by removing it.

---

## ADR-104 — A pause on Android no longer erases the answer

**Date:** 2026-09-28 · **Status:** Accepted

Reported from students' Android phones: in Speaking, pause for a moment and
everything said before the pause disappears.

The service already kept *final* segments and reopened the microphone when the
platform closed a session. What it assumed is that a session ends with a final
result. On Android it frequently does not: a silence closes the session with
`error_speech_timeout` or `error_no_match` and nothing final, and sometimes
with a final result carrying **no words**. Either way the words were still a
partial, nothing had moved them into the transcript, and the reopened session's
first — empty — result replaced them. iOS almost always finalises before
closing, which is why it was only ever seen on Android.

Three changes, each closing one way the words could be lost:

- **Opening a session commits whatever the last one left unfinished.** The
  platform's silence is not the learner's decision to throw words away.
- **An empty result never overwrites words.** A final with no words keeps what
  was being heard; an empty partial carries no information and is ignored.
- **Results are scoped to their session.** A late result from a session already
  closed and saved is dropped, so committing on close cannot say a sentence
  twice.

Pinned by seven tests replaying the platform's sequences through a fake
recogniser, four of which fail against the previous code with the exact
symptom: `"my name is Ahmed"` came back as `"Ahmed"`, and three sentences
separated by pauses came back as only the last. Not reproducible on a
simulator — neither simulator has a working recogniser — so the device check is
the release APK on an Android phone.

---

## ADR-105 — A learner's own meaning is stored beside its own definition

**Date:** 2026-09-28 · **Status:** Accepted

Reported by a student: they added `habit`, wrote the meaning themselves —
عادة — and a later skill taught it as رداء.

It was not the model. The word had been stored with the learner's Arabic
beside the English definition of the lexicon's **commonest sense**: "a
distinctive attire worn by a member of a religious order". The add path took
`facts.DefinitionEn` — `facts` being the top-ranked row for the word — on the
reasoning that the lexicon's facts beat a model's. That holds for the part of
speech and the band, which belong to the word. It does not hold for the
definition, which belongs to a *sense*, and the learner had just said which
sense they meant. Every generator reads the definition, so every skill after
Reading taught the robe. Editing the meaning (ADR-101) had the same fault the
other way round: new Arabic, old definition.

On production: 32 of 36 learner-written words carried the commonest sense's
definition, and at least twelve were a different sense — `sausage = نقانق`
beside "a small nonrigid airship", `sell = يبيع` beside "persuade somebody",
`bowl = صحن` beside "the act of rolling". The old edition's invented sense
order is what made the commonest sense so often the wrong one (ADR-096).

**The fix.** The meaning checker already sees every listed sense; it now also
reports *which* — a 1-based `sense` into the list it was shown — and a
one-clause definition of the learner's meaning. It reports; this service
decides (rule R2):

1. the learner's Arabic is a sense's own Arabic → that sense, decided here;
2. the checker names a sense, validated against the list → that sense;
3. the word has exactly one sense and the meaning was accepted → that sense;
4. otherwise the checker's definition of the learner's meaning;
5. never the commonest sense's definition for a different meaning. Empty is
   better than wrong: the generators handle a missing definition, and nobody
   shows a wrong one to the learner to catch.

Verified against Gemini on the old edition: `habit = عادة` → "an established
custom", `sausage = نقانق` → "highly seasoned minced meat stuffed in casings",
`sell = يبيع` → "exchange or deliver for money". Two imperfect: `quarter = ربع`
got the verb sense ("divide into quarters"), and `nuts = مكسرات` got none —
the old edition has no food sense for `nuts`. Neither is wrong the way the robe
was.

Six API tests, all six confirmed failing against the previous code; nine in the
AI service. The twelve production words were repaired from the lexicon's own
rows — never typed — after a rolled-back dry run showed exactly the twelve
changes.

## ADR-106 — The rate limiter sees the learner, not Render's proxy

**Date:** 2026-09-28 · **Status:** Accepted

**What was wrong.** Every request on Render arrives through Render's own proxy
(Cloudflare in front), so `Connection.RemoteIpAddress` is the proxy's address —
the same one for every learner in the world. The limiter partitions anonymous
traffic by that address, so:

* **sign-in, registration, refresh and password reset shared one budget of 10
  per 15 minutes across the whole user base.** `/auth/refresh` is in that
  budget, and the access token lives 15 minutes, so every opening of the app
  after a quarter of an hour spends one. Eleven students opening the app in the
  same quarter of an hour — exactly what an announcement in the group produces —
  and the eleventh is refused. The client correctly does not sign them out on a
  429 (the session is not dead), so what they see is a screen that does not load;
* every anonymous request, including the keep-alive, shared one 300-per-minute
  global budget.

Measured, not inferred: 320 requests from one Mac exhausted the budget, and at
that moment a request from an unrelated machine (a different country) got 429
too; a minute later, when the window rolled, it got 200.

**The fix.** Partitions use `ClientAddress.Of`, which reads the address from a
configured header (`RateLimits:ClientAddressHeader`, default `True-Client-IP`,
which Render's edge sets) and falls back to the socket. A value that is not a
valid IP address is ignored, so a made-up header cannot buy a fresh budget per
request.

**Why a setting.** Which header a platform *overwrites* — rather than passes
through from the caller — is a fact about the platform, and a header the caller
controls would disable the limiter entirely. If a test on production ever shows
the header can be spoofed, the fix is an environment variable, not a release
(rule R3). Verify after each hosting change: exhaust the budget from one
machine while sending a forged `True-Client-IP`; the forged value must still be
refused.

Not changed: the budgets themselves, and the authenticated partitions, which
were already per user.

Two tests: callers behind one proxy with different addresses keep separate
budgets (confirmed failing on the old code), and non-address values fall back
to the socket.

**Addendum, same day — the first deploy did not fix it.** Re-running the
two-machine probe against `8ba3ed7` live on Render: still one shared budget.
`True-Client-IP` does not reach the service (a forum answer said it would; the
probe says otherwise), so every request fell back to the socket — no worse than
before, and no better. Render's own guidance is X-Forwarded-For, and it also says
the proxy *appends* to that header rather than replacing it, so its first entry
is whatever the caller sent. Taking it would let anyone reset their sign-in
budget per request.

So the header is not guessed a second time. Two changes:

* `RateLimits:ClientAddressEntry` picks an entry from a list — 0 the first,
  -1 the last — because the trustworthy entry of an appended list is counted
  from the right;
* every 429 logs the address it keyed on and the candidate headers exactly as
  they arrived (`X-Forwarded-For`, `True-Client-IP`, `CF-Connecting-IP`,
  `X-Real-IP`, the socket).

One refused request on Render then shows which header carries the caller and
where. The fix is two environment variables, chosen from that log line and
verified with a forged prefix — not a third release. One more test: with
`X-Forwarded-For` at entry -1, a forged left-hand prefix buys nothing.

**Second addendum — the first deploy did fix it; the probe was wrong.** The
"second machine" in both probes was Claude Code's WebFetch, which fetches from
the same Mac. It shared the budget because it *was* the same caller, before the
fix and after it. The addendum above drew its conclusion from that probe and is
superseded by what the new log line showed on Render:

```
Rate limit refused GET /health/live for 111.92.56.251 (X-Forwarded-For=198.51.100.77,111.92.56.251,
172.69.131.179, 10.27.71.204; True-Client-IP=111.92.56.251; CF-Connecting-IP=111.92.56.251; X-Real-IP=-;
socket=127.0.0.1)
```

* **The bug was real:** the socket is `127.0.0.1` for every request, so the old
  key was one value for the whole user base.
* **`True-Client-IP` arrives and is right**, and the key is the caller's address.
* **It cannot be forged:** with the budget spent, a request carrying a made-up
  `True-Client-IP` was still refused — the edge overwrites it. A made-up
  `CF-Connecting-IP` is refused by Cloudflare outright (403).
* **X-Forwarded-For's first entry is forgeable**, exactly as Render says: the
  made-up `198.51.100.77` sits at the front. Were it ever needed, the real
  caller is entry `-3` (client, Cloudflare, Render's balancer).

Configuration stays at the default (`True-Client-IP`, entry 0). The log line and
`ClientAddressEntry` stay: they are what settled this, and what will settle it
on the next platform. Lesson for any future probe of a per-caller limit: the
second caller must be a different network — a phone on mobile data — never a
tool running on the same machine.

---

## ADR-107 — The server writes down what the learner said

**Date:** 2026-09-29 · **Status:** Accepted (local; not yet deployed)

Reported from students, Android above all: in Speaking the phone's recogniser
wrote down something other than what they said. ADR-104 stopped it *losing*
words; it could not make it *hear* better. Speaking is judged on the
transcript, so a wrong transcript is a wrong verdict. Editing the draft
(ADR-069) was the stopgap; the product owner reported it was still wrong too
often to rely on.

**Measured before building.** A bench in `labs/stt_lab/bench/` ran the same 64
clips — four accents including an Arabic voice speaking English, clean and with
noise — through four engines:

| Engine | Answered | Word error | Median time |
|---|---|---|---|
| Groq `whisper-large-v3-turbo` | 64/64 | 6.8% | 0.5 s |
| Groq `whisper-large-v3` | 64/64 | 5.7% | 0.5 s |
| Gemini 3.1 Flash-Lite (free tier) | 47/53 (6 × 503) | 2.9% | 5.7 s |
| Gemini 3 Flash preview | 8/21 | 0.9% | 7.8 s |

Gemini was the most accurate and the least available; Groq never refused.
Both kept learners' grammar mistakes almost always — Gemini "corrected" `he
don't` → `he doesn't` once in sixteen, Whisper never did (its misses were
mishearings). The noisy Arabic voice defeated both.

**Decided.** The phone records (16 kHz mono AAC, `record` package) and the
server writes it down: `POST /api/speech/transcribe` → AI service
`/ai/transcribe` → **Gemini** (one retry on 429/5xx) → **Groq
`whisper-large-v3`** when Gemini fails, times out, or is not configured. The
text comes back as the same editable draft as before; nothing about the turn,
the tutor or the verdict changed.

* **A separate, free Gemini key** (`STT_GEMINI_API_KEY`) — the product owner's
  instruction, so audio never spends on the paid key that writes lessons. It is
  never defaulted to `GEMINI_API_KEY`, and a test pins that.
* **The prompt forbids correcting the learner** — verbatim, mistakes kept.
* **Silence is not failure.** Gemini is asked for `[NO_SPEECH]`; Whisper
  segments it rates as probably silence (`no_speech_prob ≥ 0.6`) are dropped,
  because Whisper hallucinates polite phrases over an empty clip. Either way
  the learner is offered the microphone again.
* **Failure is said.** Both engines down → 503 `SPEECH_UNAVAILABLE`, and the
  app says *"We couldn't listen just now. Try again, or type your answer."*
  rather than looking as though the learner said nothing.
* **Behind the same AI gate** (ADR-051) and the `Expensive` rate limit; a far
  side at capacity is `AI_BUSY`, not a failure.
* **Only this endpoint** may exceed the 256 KB body limit, up to
  `Capacity:MaxAudioBytes` (8 MB — over half an hour at 4 KB/s).
* **Nothing is stored.** The recording is a temporary file on the phone,
  deleted after the upload, and lives on the server for one request.
* **Against the mock** the phone's recogniser still listens, which is what the
  widget suite exercises.

**What it costs the learner.** The words no longer appear while they speak,
and the draft takes 4–10 s with Gemini (under 1 s when Groq answers). The
product owner chose right-and-later over live-and-wrong. Every number —
timeouts, retries, models, thresholds — is configuration (rule R3).

**Verified locally:** the demo account in the iOS Simulator recorded a
sentence spoken by the Mac, Gemini returned it word for word in 4.6 s, and the
tutor answered it. Tests: 21 AI-service, 13 API, 12 service + 1 widget in
Flutter. Not yet deployed: production needs `STT_GEMINI_API_KEY` and
`GROQ_API_KEY` on the AI service.

---

## ADR-108 — The tutor and Listening speak in Gemini's voice

**Date:** 2026-09-29 · **Status:** Accepted (local; not yet deployed)

The phone's voice sounded like a screen reader. The product owner heard
Gemini's TTS on the free key, chose the **Kore** voice, and decided: the
**tutor** and **Listening** (passage, sentence beside a question, replay after
the result) speak in Gemini's voice; **single words and the placement test keep
the phone's**. Storing a recording per word was considered and rejected by the
product owner.

**The hard part is what the phone gave for free.** Listening highlights the
word being spoken and draws a playhead (ADR-068, ADR-082); Gemini returns audio
and nothing else. So:

1. **Gemini speaks** (`gemini-3.8-flash-tts` → `-lite-tts` → `2.5-flash-preview-tts`;
   the free tier's limits are per model, so a refusal from one is not one from
   the next). The text is sent **alone** — measured: a style prefix ("Say
   warmly: …") was read aloud as part of the speech.
2. **Groq's Whisper times every word** of that audio (`timestamp_granularities[]=word`,
   ~0.5 s, free).
3. **The words are aligned to the text** (`difflib`), giving each word of the
   page a start time and a character span; a word Whisper missed is placed
   between its neighbours by length.
4. **The alignment is also the check.** Under 85 % of the text heard, or over
   15 % extra words heard, and the audio is refused and the next model tried —
   a passage that says what the page does not show is worse than the phone.
   Groq down → the audio is kept with timings estimated by word length.
5. **MP3 at 48 kbit/s** (`lameenc`): 29 KB where WAV was 221 KB.
6. **A cache of minutes**, in memory, spares regenerating what the learner
   replays or slows down. Nothing is stored.

**In the app nothing above the voice changed.** A new `HybridSpeechProvider`
sits behind the existing `SpeechProvider` seam: it is told who is speaking
(`tutor:`, `listening:`, `sentence:`, `replay:` → Gemini; anything else → the
phone) and keeps the phone's contract — `speak` one sentence, report each word
start, report the end. A passage is fetched in a few pieces (the first short),
ahead of time, and each sentence is played as its slice of the piece that holds
it (`ClippingAudioSource`, from its first word's time to its last). Long tutor
replies are split the same way — measured, the voice started after 5.9 s
instead of 8.5 s, with the second piece ready before the first ended. Slow is
the same audio at 0.65× (the phone's own slow ratio). Any fetch that fails or
exceeds 20 s is spoken by the phone; three failures in a row leave the cloud
alone for three minutes.

**One real bug found on the way:** a Listening clip's clock counted the fetch
as speaking time (0:27 shown for 15.5 s of audio). Sentences are now timed
from the first word the voice reports (`SpeechService.wordEvents`).

**And one in the local stack:** `./wordos` started the AI service with
`.venv/bin/uvicorn`, whose first line named the interpreter of the checkout the
venv was copied from — so it had been running on another project's packages.
It now runs `.venv/bin/python -m uvicorn`.

Verified in the iOS Simulator against the local stack: a Listening sentence
clip played in Gemini's voice with the playhead following it to the end, and
the tutor's reply was spoken by Gemini. Tests: 19 AI service, 9 API, 21 Flutter.
Not yet deployed: production needs the voice keys (the free
`STT_GEMINI_API_KEY` doubles as the voice key) and `lameenc` in the image.

## ADR-109 — Silence noise bursts at a voice clip's edges; the free voice quota is ten a day

**Date:** 2026-09-29 · **Status:** Accepted (local only, not deployed)

**What the product owner heard.** A radio-like "tsh" in the tutor's voice
after "running well" and again at the end of the reply. Listening sounded fine.

**Cause, measured.** `gemini-3.8-flash-tts` had run out of free requests, so
the tutor fell to `gemini-3.8-flash-lite-tts`. That model ends every clip with
~125 ms of near-full-scale static (10 ms RMS over 15,000, peaks at 32,767)
after 250 ms of silence, and opens some clips with a 5 ms click. A long tutor
reply is fetched in pieces, so the burst sounded at the end of each piece.
Listening plays each sentence as a slice between its first and last word, so
it never reached the burst.

**Decision.** `clean_edges` in `ai-service/app/tts.py` runs on every clip
before it is timed or encoded, whatever the model. An edge run of sound is
silenced when a silence (≥ 100 ms) separates it from the speech and it is
either a click (≤ 30 ms) or short and loud throughout (≤ 300 ms, mean ≥ 12,000
RMS). Real speech measured at most ~10,500 mean over 50 ms, and a final word
after a pause is longer or quieter than that. The clip keeps its length, so
the timings still hold, and both ends fade over 8 ms so the cut never clicks.
Every threshold is configuration (`TTS_BURST_*`, `TTS_CLICK_MAX_MS`).
Checked against the 64 recorded speech clips from the STT bench, including the
noisy ones: none changed.

**Also found — a blocker for deploying ADR-108 on the free key.** Google's
429 names the quota: `GenerateRequestsPerDayPerProjectPerModel-FreeTier`,
value **10**. Ten requests a day per voice model, three models: about thirty
tutor pieces or Listening pieces a day for the whole app. Past that, every
line falls back to the phone's voice. This works as designed, but it is not
a Gemini voice for learners. Serving learners needs a billed key for the voice,
chosen by the product owner.

## ADR-110 — Edge's "Ava Multilingual" voice first, Gemini behind it, as a list

**Date:** 2026-09-29 · **Status:** Accepted (local only, not deployed)

**Context.** Gemini's free voice allows ten requests a day per model
(ADR-109), not enough for learners. The product owner compared voices by ear
in `labs/tts_lab` (Edge, Azure, Google Cloud, Gemini, Groq Orpheus) and chose
Edge's `en-US-AvaMultilingualNeural`. They accepted that Edge is unofficial
(the read-aloud service behind Microsoft Edge, reached through the `edge-tts`
package) and may stop working any day. They asked that replacing it then be
easy.

**Decision.**

1. **Providers are a list in configuration**: `TTS_PROVIDERS=edge,gemini`.
   Each is tried in order for every request, so an Edge failure falls to
   Gemini, and if that fails too the app uses the phone's voice (ADR-108,
   unchanged). Replacing a provider means changing a setting, not shipping
   a release. An unknown name is skipped, never fatal.
2. **Edge reports its own word marks**, so Groq is not asked. The marks go
   through the same `align` as Whisper's (100% matched on every measured
   passage). If they ever fit the text poorly (numbers, symbols), the audio is
   kept and only the timings are estimated. Edge reads exactly what it is
   given, so a poor match is a problem with the timings, not with the audio.
3. **A sentence at a time, four at once, joined.** Edge generates speech at
   about the pace it is spoken. Measured as one request: 180 characters in
   7.2 s, 450 in 14.2 s, 1,400 in 56.8 s, which is past the app's 20 s wait.
   Split into sentences and fetched four at a time: 450 in 2–4.5 s and
   1,400 in 10.7–15.7 s. Eight at a time was slower, so Edge seems to throttle.
   The pieces are bare MP3 frames and are simply joined. Each piece's word
   marks are shifted by the length of the pieces before it, counted frame by
   frame (`mp3_duration_ms`) rather than guessed from the size.
4. **Nothing else changes.** The API contract, the app, the phone-voice
   fallback, Gemini's burst cleaning (ADR-109) and the cache are the same.
   Single words and placement still use the phone's voice.

**Verified.** 22 new AI-service tests (184 pass). Through the real API: a
tutor reply in 2.9 s and a 176-character Listening piece in 0.9 s, both Edge,
all words aligned. In the iOS Simulator: the tutor's reply (the same "…running well" reply
that had the static) was spoken by Edge in two pieces, ready in 4.1 s, and the
microphone opened when it ended. A Listening sentence played with the clock
reading 0:23 of 0:23 (audio 22.7 s) and the bar running to the end. Slow mode
showed 0:35 and fetched nothing new.

## ADR-111 — One voice for every word, and a session's audio fetched before it is asked for

**Date:** 2026-09-29 · **Status:** Accepted (local only, not deployed)

**Context.** Now that the voice is free (ADR-110), the product owner asked
for every word button to use it too, at both speeds. That reverses ADR-108's
"single words keep the phone's voice". They also asked that play in Listening
answer at once: no wait on each question, even on a weak connection.

**Decision.**

1. **Every utterance with an id is the server's voice** (`cloudSpeaks`). This
   covers the tutor, Listening, word buttons (list, detail, lookup, weekly
   review, warm-up, the word under a Listening question) and placement. It
   is a rule for everything rather than a list of callers, so a button added
   later cannot quietly keep the phone's voice. A call with no id still goes
   to the phone.
2. **A word is never cut out of a longer clip.** Only Listening's sentence
   ids (`listening:`, `sentence:`, `replay:`) may be played as a slice of a
   passage (`slicedFromLonger`). Looking a word up by substring would have
   played "tea" out of "green tea. It" — with the sentence's rhythm and the
   start of the next word. A word is fetched and played as its own clip. The
   slow button replays that same clip at 0.65×, with no second request.
3. **A word waits at most 6 s** (`shortTextTimeout`, texts ≤ 40 characters)
   before the phone says it. A passage still waits 20 s.
4. **A session fetches its audio when it opens**, in the order the learner
   will hear it: the passage the screen opens on (which asks for itself as
   it is built), then from the current question onwards each question's
   sentence and word, and Speaking's warm-up words. Two downloads run at
   once and the rest queue. Nothing is *played* early; ADR-080 stands.
5. **What the learner presses jumps the queue.** A line asked for now is
   moved to the front of the waiting fetches, so a tap never waits behind
   the background ones.
6. **The voice has its own rate limit** (`RateLimits:VoicePermitsPerMinute`,
   120). Opening a Listening session is about twenty requests in its first
   minute. On the AI budget (30) that would have left the tutor and the
   microphone refused.

The app keeps up to 80 clips (was 40) in the temporary folder; a word is a
few kilobytes.

**Verified.** Flutter +9 tests (537 pass): routing, words fetched whole and
never sliced, slow reusing the clip, the short timeout, fetch order, a press
jumping the queue, a failed prefetch retried on play, and a widget test that
opening Listening prepares the passage and then every question in order
without playing any. API: a test that the voice has its own budget and does
not spend the microphone's.

## ADR-112 — A meaning the checker rejects is never saved

**Date:** 2026-09-29 · **Status:** Accepted (local only, not deployed). Reverses the override in ADR-074.

**Context.** The product owner's review: a learner could add a word with a
wrong or random Arabic meaning by tapping "save it as I wrote it" under the
checker's objection. The word list is the foundation of the app, and
Reading, Listening, Speaking, Spelling and Writing all mark answers against
that meaning. A wrong one is five sessions teaching the wrong thing.

**Decision.** `acceptAnyway` is gone from `POST /api/words` and from
`PATCH /api/words/{id}/meaning`. A `MEANING_REJECTED` refusal is final. It
carries the checker's note and the meanings it would accept, and nothing is
stored until the learner writes a wording the checker accepts or picks one of
its suggestions. An older app that still sends `acceptAnyway: true` is
refused the same way, because the server no longer reads the field. The app
shows the suggestions as one-tap choices, with a line under them saying the
word is saved once its meaning is right. On the word page, the suggestions
are chips in the dialog. `MeaningCheckResult.Overridden` stays in the enum so
words saved that way before still read back.

## ADR-113 — Speaking ends when every word is used, and a request is not an answer

**Date:** 2026-09-29 · **Status:** Accepted (local only, not deployed)

**Context.** The product owner's review of Speaking:
1. The session ended before every word had been discussed. It ended at the
   word count plus three learner turns.
2. "Can you give me another question using football?", "Can you explain what
   football means?" and "How can I use football in a sentence?" were counted
   as using *football*. They are requests.
3. Questions must suit the learner's level. A scientific or technical word
   ("tissue") must be asked about simply, not with questions that need the
   field, especially when it is outside the learner's interests.
4. "Give me another question" must get a new, easy question.

**Decision.**
1. **The conversation ends when every word has been used.** The only other
   stop is a safety limit on model calls,
   `SpeakingMaxLearnerTurnsPerWord` × words (10 each), far past any real
   conversation. Two limits that a longer conversation would have hit are
   handled: the tutor is sent only the latest `SpeakingTranscriptWindow` (20)
   lines, and the evaluator the latest `SpeakingEvaluationTranscriptMax`
   (120). The AI service now accepts 160.
2. **Two independent checks that a word was used rather than asked about.**
   - `LearnerRequests` (domain, C#, no model) splits the turn into sentences
     and drops the ones that ask the tutor for something: another, new or
     easier question; what a word means; how to use or say it; "in a
     sentence"; "the word X"; "explain X"; "What is X?" said on its own;
     "I don't understand the word". A word counts only if it appears in a
     sentence that answers. The patterns name the act of asking, never a
     topic, so "Do you like football?" and "I can explain why football is
     popular" still count. 38 domain tests cover this, including the product
     owner's own three examples.
   - The model reports `learner_intent` (`answer`, `new_question`,
     `explain`, `other`) and `words_only_named`. A word it names is not
     counted. A single-sentence turn it reads as `new_question` or `explain`
     counts nothing, which catches phrasings the patterns don't know.
3. **The prompt (speaking-v7)** classifies the message first and acts on it.
   `new_question`: a new question about the same word, in a different
   everyday situation, easier than the last. `explain`: one simple sentence
   of explanation at the learner's level, one tiny example, then an easy
   question. Every question is pitched at the learner's level and everyday
   life. A scientific, medical or technical word is met where an ordinary
   person meets it, never through specialist knowledge. After a question the
   learner could not answer, the next one is easier.
4. **Fixed on the way:** the fallback tutor (model down) passed the words the
   learner *used* as the words only *named*, so with the model down no word
   could ever count.

## ADR-114 — Skill lists are always in the pipeline's order

**Date:** 2026-09-29 · **Status:** Accepted (local only, not deployed)

**Context.** Changing a daily target in Settings moved skills around. The
levels were returned in the database's row order, and PostgreSQL returns an
updated row after the others, so the skill just edited jumped to the bottom.
The cards also had no keys, so Flutter matched each slider's state to a card
by position, and a value could appear on another skill.

**Decision.** Every endpoint that returns skill levels (`/api/me`,
`/api/settings`, the Owner's user view) sorts them by the pipeline order
(`WordOsConfiguration.PipelineRank`). The Settings cards are keyed by skill.
An API test changes three daily targets and checks the order after each. It
fails without the fix and passes with it.

## ADR-115 — The learner chooses where Spelling's hints start

**Date:** 2026-09-29 · **Status:** Accepted (local only, not deployed)

**Context.** The ladder already existed (dictionary definition → simplified
definition → synonyms → Arabic meaning → letter count), entered by level. The
product owner asked for a setting that decides where each word starts.

**Decision.** `users.SpellingHintStart` (nullable; migration
`SpellingHintStart`). Null is **automatic**: A1–A2 start at the Arabic
meaning, B1 at synonyms, B2 at the simplified definition, and C1–C2 at the
dictionary definition, taken from the Reading level that Spelling follows.
`PATCH /api/settings/spelling-hints {start}` takes `AUTO` or one of the four
rungs. The letter count is refused, because it is the last hint and a word
that opened on it would have nothing left to give. `/api/me` returns
`spellingHints {start, automaticStart}`, so Settings can say what automatic
means for this learner without working it out (R1). Every word's ladder
starts at the same rung, so the next word starts at the top again. In the
app, each new word resets the hint count. A change takes effect from the next
Spelling session.

## ADR-116 — The tutor's line is shown with its voice, and the chat behaves like a messenger

**Date:** 2026-09-29 · **Status:** Accepted (local only, not deployed)

**Context.** The product owner's report: in Speaking, the tutor's text
appeared and its voice followed seconds later, so the learner read the line
in silence and then heard it again. They asked that text and voice always
arrive together, even when the voice is slow. The same review asked for the
chat to behave like WhatsApp: a sent line becomes its own message and the
box clears.

**Decision.**
1. **`SpeechService.ready(id, text)`** completes when the line can start
   speaking at once: its first piece has been fetched. It also completes when
   the line never will, because the fetch failed or ran past the line's own
   time limit and the phone will say it instead. It never throws, and never
   waits longer than speaking the line would. The tutor's replies, and the
   opening greeting, are added to the conversation only after `ready`, and
   speaking starts in the same moment. The greeting's voice is fetched while
   the learner does the warm-up words. In typing mode a reply is not spoken,
   so it is shown at once.
2. **Messenger behaviour.** The learner's line is added and the box cleared
   the moment they send. The list scrolls to the newest line whenever one
   arrives. While the tutor's line is on its way, three dots stand where it
   will appear. If a send fails, the line comes out of the conversation and
   back into the box, so it can be sent again and is never lost. English
   messages and the input box are laid out left to right in the Arabic
   interface, so the question mark is at the end. The box grows to four lines
   instead of scrolling one line sideways out of sight.
3. **Edge connections are capped for the whole process** at four
   (`TTS_EDGE_PARALLEL`), not per request. Measured: the app's two pieces of
   one reply, each split into sentences, put eight in flight, and a 146-character
   piece took 14 s. With the cap, both pieces of the same reply took about 1 s.
   A request that times out while waiting for a slot frees nothing it never
   took.

**On "the input box piles up".** With the phone's recogniser (still used in
production), Android builds a turn by joining segments, and some Samsung
recognisers repeat earlier words in every result, so text piled up in the
box. The server recogniser (ADR-107) records each turn fresh, so this cannot
happen there. The single-line box, which scrolled long text sideways, is the
other half, and is fixed above.

**Verified in the simulator** against the local stack. A spoken answer
appeared as its own message at once, with the dots in the tutor's place. The
reply's text appeared at the moment its voice began (reply written at 41.8 s,
voice ready at 44.5 s, both shown then). A request ("another question using
environment") was not counted and got an easier question about the same
word. The next real answer counted it.

## ADR-117 — How long the tutor speaks and how long a passage runs follow the learner's band

**Date:** 2026-09-30 · **Status:** Accepted (local only, not deployed)

**Context.** The product owner's report: at A2 the tutor "brings long
speech", and length should follow the level everywhere: A1–A2 simple, B1–B2
intermediate, C1–C2 advanced. Measured against Gemini before changing
anything, with the same conversation at every band:

- **Speaking.** Replies ran 28–46 words at A1–A2 over three to five
  sentences, against 37–71 at C1–C2. `_REGISTER` said how hard each sentence
  is, and nothing said how many. Explaining a word was three fixed parts
  (definition, example, question) whatever the band. A new question about
  a hard word set a scene first. And a B1 "new question" about body
  *tissue* drifted to a paper tissue for a cold, because the everyday rule
  allowed "its everyday meaning".
- **Reading and Listening.** The per-band table was right, but the floor for
  the target words was a flat 30 words a word, which is a B2 sentence pair.
  With five words, an A1 passage came back at 168 words and 24 sentences,
  twice its band, A1 listening was as long as A1 reading, and B1 listening was
  the same length as A2.

**Decision.**
1. **`_SPEAKING_LENGTH`**: sentences and words for a turn, by band. A1 2/16,
   A2 2/20, B1 3/30, B2 3/38, C1 3/45, C2 4/55. Neither count includes the
   closing "Try to use the word …" line, which the learner needs at any band.
   The greeting, every turn and the goodbye each state it. An explanation and
   a new question must fit inside it. The example is dropped when there is no
   room, and "easier" now also means shorter. `speaking-v10`.
2. **A rewrite past 1.5× the band** (`SPEAKING_SHORTEN_OVER`). The prompt
   alone did not hold a new question about a hard word to A2's length. So a
   turn that far over gets one short rewrite call at the learner's band, and
   the "Try to use" line is set aside and put back word for word. A rewrite
   that is no shorter, or a failed one, keeps the original. A long turn is
   worse, but it is not broken. The log records `reply_words` and
   `first_words` for every turn.
3. **A specialist word keeps its sense** when made simple. It is met where
   an ordinary person meets it *in this sense*, never through a commoner
   sense of the same spelling.
4. **The passage floor is two of the band's own sentences per target word**
   (`20 + words × 2 × average sentence length`), not 30 words each.
5. **A stalled Edge sentence is asked for again.** While measuring, the same
   reply's voice took 0.85 s, 2.2 s and 10.9 s. The tutor's text waits for its
   voice (ADR-116), so a stall held the whole reply. A sentence unfinished
   after `TTS_EDGE_HEDGE_SECONDS` (2.0) plus `TTS_EDGE_HEDGE_PER_CHAR` (0.02)
   per character is fetched a second time, and whichever copy finishes first
   is used. The clock starts when the sentence has a connection, not while it
   queues behind a long passage. 0 turns it off.

**Measured after**, on the local stack against Gemini:
- **Speaking**, two runs:
  - A1–A2 replies are 24–34 words including the ~8-word "Try to use" line,
    so about 17–25 words of speech.
  - B1–B2 run 31–48, and C1–C2 37–56.
  - Every *tissue* question stayed with the body.
  - A reply takes about 1.4 s; with the rewrite (seen once, A2, 40→26 words),
    2.7 s.
- **Reading/Listening** with five words:

  | | A1 | A2 | B1 | B2 |
  |---|---|---|---|---|
  | Reading (words) | 95 | 150 | 235 | 433 |
  | Listening (words) | 84 | 120 | 167 | 233 |

  Sentences averaged 7–8 words at A1, 10 at A2, 13–14 at B1 and 17–18 at B2.
- **Voice:** with the backup request, twelve three-sentence replies took
  2.4–3.8 s with no stall. Raw Edge was the same speed without our code, so
  the rest of the time is Edge's own network. The app waits only for a line's
  first piece.

## ADR-118 — A profile build may call the API on a private network over http

**Date:** 2026-09-30 · **Status:** Accepted (local only)

**Context.** The product owner asked for an Android APK that a friend could
test against the API on the Mac before release. A debug build is the wrong
thing to hand a tester who is judging speed, so the APK was built in profile
mode, which is compiled like a release build. Every sign-in then showed "حدث
خطأ ما" and never reached the server. `assertTransportIsSafe` allowed http to
a private address only when `kDebugMode` was true, so a profile build was
refused inside the app before any request left the phone.

**Decision.** The exception now applies to any build that is not a release
build (`!kReleaseMode`), and still only for a private-network host. A release
build sends tokens over TLS or not at all, as before (`docs/07-SECURITY.md`
§2). `android/app/src/profile/` has the same network security config as
`src/debug`, which allows cleartext only to the listed private addresses, so
Android also lets these requests through. Verified on the Pixel 8 emulator
(Android 16): the same APK signed in, showed all five skills, and opened a
Spelling session from the Mac's API.

## ADR-119 — Reading's word questions can be heard, at both speeds

**Date:** 2026-09-30 · **Status:** Accepted (local only)

**Context.** The product owner asked that Reading, when it asks about a word,
offer a button to hear that word, normally and slowly. Listening already had
this (`WordPronunciation`, ADR-081). ADR-085 had deliberately kept Reading
without it: the word is spelled on screen there, and the control carries a
`revealSpelling` switch for exactly this future use.

**Decision.** Reading's word questions put the speaker pair
(`WordSpeakerButtons`, normal and slow, `dense`) at the end of the question's
own line, inline in its text, level with the words. They do not use
Listening's card: on a phone the card pushed the answer options below the
fold. A separate column beside the question also failed on a real
screen: it took the width the question needed, so the question broke onto
two lines. `dense` works through the button's style, because Material 3 pads
every icon button to a 48-point tap target otherwise. Checked on the iPhone
17 Pro simulator and the Pixel 8 emulator: the question and both speakers
sit on one line, and all four options stay visible. The word is on screen already,
so the control only has to sound it. The comprehension questions still carry
none, since they have no word of their own. The word's voice is fetched ahead
with the session, as Listening's is. Listening is unchanged and still never
shows the word.

## ADR-120 — A finished weekly review can be practised, and the practice records nothing

**Date:** 2026-09-30 · **Status:** Accepted (local only, not deployed)

**Context.** After this week's challenge, the hub card locked until words
ripened again. When every word had been recalled, it disappeared. The product
owner asked that a learner who comes back later in the week can go over the
words again. It should not be the weekly review, which still comes once a
week, and it should not lock.

**Decision.**
1. **`POST /api/weekly-review/practice/start`** builds a round over the words
   of the learner's latest finished review, leaving out any deleted since.
   It is played through the same `answer` and `complete` calls. The row is a
   `WeeklyReview` with `IsPractice` (migration `WeeklyReviewPractice`).
2. **A practice records nothing (R9).**
   - It marks no word reviewed or passed, so the next challenge opens on the
     same day and asks about the same words.
   - It counts no exposure.
   - It logs no `ReviewCompleted`, so the reminders do not think the week's
     review was done.
   - Its score is shown as "practice score" and never kept as the weekly
     score.
   - Starting one replaces an unfinished practice, never an unfinished
     review.
3. **The hub** sends `practiceAvailable` and `practiceWordCount`, but only
   while no challenge is open. Then the card says the week is done, offers
   its words again, and still names the next challenge's date. When a
   challenge is open, the card is the challenge.

Tests pin all three on both sides. On the server, the ripening fields, the
exposures and the logged reviews are identical before and after a practice
that includes a wrong answer, and the next challenge still opens on its day.
The app has the same check on the mock, plus a walk from the hub card
through a practice to its result.

## ADR-121 — Listening and Reading are sized in seconds at the bottom of the ladder, and the Friday reminder is gone

**Date:** 2026-10-03 · **Status:** Accepted (local only, not deployed)

**Context.** Two reports from the product owner.

- **An A1 listening clip ran 58 seconds.** The band table was fine. The cause
  was the floor of two band sentences per target word (ADR-117). With the
  default daily target of ten words, that floor alone came to
  `20 + 10 × 2 × 8 = 180` words for A1, and listening was only ever 60% of
  reading. The prompt also told the model that "a short one is the single most
  common way this task is got wrong", so nothing pushed back on length. The
  product owner wants A1 at about 20–25 seconds, growing a step at a time up
  the ladder, and Reading kept short at the bottom in the same way.
- **The reminder "خمس دقائق الآن خير من ساعة يوم الجمعة"** ("five minutes now
  beats an hour on Friday") read in Arabic as a saying about Jumu'ah. It had to
  go. Its text lives in the app, so changing the app alone would leave it on
  every phone that has not updated.

**Decision.**
1. **`_PASSAGE_SHAPE` has its own listening column**, sized in seconds at
   Edge's ~2.5 words a second: A1 50 words (~20 s), A1+ 70, A2 90, A2+ 110,
   B1 135, B1+ 160, B2 200, B2+ 230, C1 260, C1+ 290, C2 320. Reading is cut
   at the bottom: A1 60, A1+ 85, A2 120, A2+ 160, B1 210. B2 and above are
   unchanged in spirit (B2 360, C1 520, C2 680). A1 sentences are now "5 to 7
   words".
2. **The floor is one band sentence per target word** (`words × average`).
   The neighbours that hint at a word are the other words' sentences.
3. **The prompt caps length both ways**: "not fewer, and NOT MORE", and
   "never more than" the length plus 10%. `reading-v7`.
4. **`WORDS_DUE_FIVE_MINUTES` is never chosen** by `ReminderComposer`. A new
   key, `WORDS_DUE_SHORT_SESSION`, takes its place in the rotation with a plain
   line: "خمس دقائق اليوم تكفي لتتقدّم كلماتك خطوة." A build that does not know
   the new key falls back on the kind, so the Friday line stops on every
   phone as soon as the server ships, with no app update. The old key stays
   in both enums so the value still parses, and the app now says the count
   line for it.

**Measured after**, on the local stack against Gemini (seconds at 2.5 words/s):

| Listening | A1 | A1+ | A2 | B1 | B2 | C1 |
|---|---|---|---|---|---|---|
| 5 words | 48 (19 s) | 70 (28 s) | 99 (40 s) | 143 (57 s) | 206 (82 s) | 278 (111 s) |
| 10 words | 60 (24 s) | 70 (28 s) | 99 (40 s) | 131 (52 s) | 192 (77 s) | 271 (108 s) |

Reading with ten words: A1 60, A2 136, B1 218.

**Trade-off.** One sentence per word is a thinner context than two. At A1 with
fifteen words due, the floor (90 words, ~36 s) still wins over the band. If
that turns out to matter, the remedy is fewer target words per passage at the
low bands, which is a backend decision and not made here.

## ADR-122 — A listening clip is sized by the clock the product owner set, and a re-told one stays a listening clip

**Date:** 2026-10-03 · **Status:** Accepted (local only, not deployed). Supersedes the listening column of ADR-121.

**Context.** Trying ADR-121 on the simulator, the product owner moved a
Listening session from B2+ to C1 and heard a clip of **4 minutes 57 seconds**.
The table was not at fault. The level control re-tells the passage through
`/ai/content/relevel`, and that path was hard-coded to `listening=False`. So any
listening passage moved to another level came back at **reading** length. The
log shows it: 24 and 27 sentences at C1/C2. The product owner then set the
lengths themself:

- A level and its "+" are **the same length**. What differs inside a pair is
  the language: sentence length and vocabulary by the CEFR band, as the
  prompts already say.
- Each pair is **ten seconds** longer than the one below: A1/A1+ 24 s, A2/A2+
  34 s, B1/B1+ 44 s, B2/B2+ 54 s, C1/C1+ 64 s, C2 74 s.

**Decision.**
1. **Listening is a column of seconds** in `_PASSAGE_SHAPE`, converted to words
   at the voice's **measured** pace per band (`_WORDS_PER_SECOND`: A1 2.8, A2
   3.2, B1 3.15, B2 2.9, C1 2.8, C2 2.7). That pace was measured by
   synthesising generated passages one sentence at a time, as the app plays
   them, over three runs.
2. **Listening has no target-word floor.** The clock is the rule. When there
   are more target words than sentences, a sentence may carry two of them, and
   both prompts say so.
3. **Reading follows the same pairing** in words: A1/A1+ 60, A2/A2+ 120,
   B1/B1+ 210, B2/B2+ 360, C1/C1+ 520, C2 680. It keeps its one-sentence-per-word
   floor.
4. **The re-telling knows what it is re-telling.** `RelevelRequest.Listening`
   (C# and Python) is set from `session.Skill == Listening`, and the relevel
   prompt has the same "never more than" ceiling as the first passage.
   `reading-v8`, `relevel-v5`.
5. Sentence averages A2 8→9 and C2 25→23, so the sentence count the model is
   given matches what it actually writes.

**Measured after** (real audio from Edge, ten target words, seconds):

| A1 | A1+ | A2 | A2+ | B1 | B1+ | B2 | B2+ | C1 | C1+ | C2 | C1 re-told from B2+ |
|---|---|---|---|---|---|---|---|---|---|---|---|
| 22–24 | 21 | 34–35 | 33 | 40–43 | 48 | 47 | 48 | 63 | 60 | 72–77 | 61–74 |

All of the first-passage rows were taken before the last pace adjustment
except A2, C2 and the re-telling, which were measured again after it. A clip
lands within a few seconds of its target, not on it. The model's sentences
vary, and so does the voice's pace from one text to the next. A re-telling
runs a little long, because its words are the story's and not the band's
shortest.

## ADR-123 — The clock is the preferred length, with ten seconds of room; the passage prompt is otherwise the original

**Date:** 2026-10-04 · **Status:** Accepted (local only, not deployed). Amends ADR-121/122.

**Context.** With fifteen words at A1, ADR-122 held the clip to 22–25 s, but
the text read as separate sentences. A "one connected story" rule fixed that,
and the product owner rejected it. A passage need not be a story. The texts
the original prompt wrote, a short article on management or on textiles, were
what they wanted. Their instruction: restore the original prompts and change
only the length. The seconds are the preferred size, the passage may run up
to ten seconds over when it needs to, and accuracy matters more than the
clock.

Restoring the original wording (and the original sentence lengths per band)
exposed one more thing. With fifteen words, A1 was given eight sentences and
**dropped a target word** in two of four runs, and C1 dropped one too. In
Listening, a word missing from the clip is still asked about: "what did the
word you heard mean?" for a word never said.

**Decision.**
1. **The passage and re-telling prompts are the committed ones** (`reading-v6`,
   `relevel-v4`) with only the length lines changed. The ADR-121/122 "NOT MORE"
   wording, the coherence rule and the two-words-a-sentence default are gone.
   So are the shortened sentence ranges. Each band's sentence length is the
   original CEFR one again (A1 "6 to 9 words", and so on).
2. **The length is preferred, with room**: "That is the preferred length for
   this level; if the passage needs a little more to read well, it may run to
   {room} words." `_passage_room` is the clock plus **ten seconds** for
   listening (`_LISTENING_ROOM_SECONDS`) and a **fifth** for reading
   (`_READING_ROOM`).
3. **Many words stretch a clip, only within that room.** Listening's length is
   `min(max(clock, words × band sentence), clock + 10 s)`. It also carries one
   line: leave no target word out, and when there are more words than
   sentences a sentence may carry two.
4. `reading-v9`, `relevel-v6`.

**Measured after**, real audio:

| | A1 | A2 | B1 | B2 | C1 | C2 |
|---|---|---|---|---|---|---|
| 5 words (s) | 18.5 | 38.5 | 54.1 | 54.6 | 64.4 | 83.1 |
| 15 words (s) | 25.8 / 27.6 / 27.2 | | 57.0 | | 75.8 / 65.1 | |

All fifteen words were present in all six fifteen-word runs. Five-word clips
land within the ten seconds of room, and several sit on their target.

## ADR-124 — A passage's length follows its number of target words; the band's figure is what ten words get

**Date:** 2026-10-04 · **Status:** Accepted (local only, not deployed). Amends ADR-122/123.

**Context.** The product owner pointed out that the band's seconds had become a
size for every session. A clip with three words still ran 24 s at A1. Their
rule: the length follows the words. Fewer words get a shorter clip, and ten
words get the band's 24 s.

**Decision.**
1. **The band's figure is the length for `_FULL_LENGTH_WORDS` = 10**, the
   default daily target. Up to ten words, the length is proportional to the
   word count. Past ten it grows linearly, reaching the room (ten seconds for
   listening, a fifth for reading) at fifteen, the most a session carries. A
   practice passage, with no words, gets the full length.
2. **The floor is four of the band's sentences** (`_MIN_SENTENCES`). Fewer is
   not a passage.
3. **Room is a quarter of the passage's own length**, never past the full
   length plus the band's room, so three words cannot grow into a whole clip.
4. Reading moves the same way, from its own full length. `reading-v10`,
   `relevel-v7`.

**Measured after**, real audio, listening:

| | 3 words | 5 words | 10 words | 15 words |
|---|---|---|---|---|
| A1 | 9.6 s | 9.9 s | 18.6 / 20.6 s | 31.8 s |
| B1 | | 20.6 s | 46.6 s | |
| C1 | | 29.8 s | 62.4 s | |

Every target word was present in all nine runs.

## ADR-126 — Every passage starts from a band minimum, and a beginner's session carries at most ten words

**Date:** 2026-10-05 · **Status:** Accepted (local only, not deployed). Amends ADR-124.

**Context.** Under ADR-124, three words at A1 came back as about five short
lines. That is too little to read as a paragraph, and too little to write
comprehension questions about. The product owner set the rule:

- **Words per session:** a learner from A1 to A2+ practises at most **ten**
  words per session. From B1 up, the daily target may run to fifteen.
- **Minimum length:** every passage starts from a minimum, **fifty words** at
  A1 to A2+, so even one word gets a proper paragraph.
- **Growth:** the passage grows with each word, up to the band's full length
  at the most words that band can carry.
- Listening follows the same rule.

**Decision.**
1. **Backend: `SessionWordCap`.** A session takes
   `min(daily target, LowerBandMaxSessionWords = 10)` at or below
   `LowerBandCeiling = A2Plus`, and the daily target above it. Both values are
   configuration (R3). The learner's own target is stored as they set it.
   Only the session is capped, so a learner who moves up a band gets their
   chosen target back. The hub's `sessionWordCount` applies the same cap from
   the same level.
2. **AI service: the length runs from a minimum to the full length.** The
   minimum (`_MIN_WORDS`) is 50 words at A1 to A2+, 70 at B1/B1+, 90 at
   B2/B2+, 110 at C1/C1+ and 130 at C2. The length grows evenly to the band's
   full length (its seconds or reading words) at ten words up to A2+ and
   fifteen from B1. A practice passage, with no words, gets the full length.
   `reading-v11`, `relevel-v8`.

**Planned length**, listening, in seconds at the measured pace:

| | 1 word | 3 | 5 | 10 | 15 |
|---|---|---|---|---|---|
| A1/A1+ | 18 | 19 | 21 | 24 | (capped at 10) |
| A2/A2+ | 16 | 20 | 24 | 34 | (capped at 10) |
| B1 | 22 | 25 | 29 | 36 | 44 |
| C1 | 39 | 43 | 46 | 55 | 64 |

**Measured** with real audio: A1 1 word 13.7 s (six sentences), 3 words
17.5 s, 5 words 16.3 s, 10 words 18.7 s; A2 3 words 20.5 s, 10 words 32.4 s;
B1 5 words 32.8 s, 15 words 43.2 s; C1 15 words 63.8 s. Every target word was
present in every run.
