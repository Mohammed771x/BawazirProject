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
