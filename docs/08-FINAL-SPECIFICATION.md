# WordOS — Final Specification

> **What the finished application does**, section by section, as built and verified.
> This is the reference: where an older document describes an intention, this one
> describes the behaviour. Every rule here is enforced in code and pinned by a test.
>
> Companions: [`03-DECISIONS.md`](03-DECISIONS.md) for *why* each judgement went the
> way it did · [`04-DATA-MODEL.md`](04-DATA-MODEL.md) for the schema ·
> [`05-API-CONTRACT.md`](05-API-CONTRACT.md) for the wire · [`06-PLACEMENT-ALGORITHM.md`](06-PLACEMENT-ALGORITHM.md)
> for the placement mathematics · [`07-SECURITY.md`](07-SECURITY.md) for the threat model.

---

## 1. What the product is

A vocabulary application for Arabic speakers learning English, in which a word is not
a row in a list but an **entity with a lifecycle**. A learner adds a word *with the
meaning they intend*, and that exact sense is then validated across five skills with a
spaced gap between each:

```
Reading ─2d→ Listening ─2d→ Speaking ─2d→ Writing ─2d→ Spelling
        → MATURE → ACTIVE (reused in generated content, least-exposed first)
        → ARCHIVE (never deleted; only when the learner outgrows it)
```

Equally, the MVP is an **algorithm-validation experiment**: every meaningful action is
recorded, so the pipeline's effect can be measured rather than assumed.

### The nine rules

| # | Rule | Where it lives now |
|---|------|--------------------|
| R1 | No business logic in Flutter — the client renders server-provided state | Every eligibility, pass/fail and level decision is a C# decision; the client asks |
| R2 | AI never mutates state — it returns structured observations, the backend decides | No AI response carries a `passed` field for a prompt change to start filling in (ADR-015) |
| R3 | Nothing tunable is hard-coded | `WordOsConfiguration` — gaps, targets, thresholds, windows, counts |
| R4 | Server-side persistence only | The device owns exactly two things: UI language and theme (ADR-010) |
| R5 | Failing one skill never resets the others | Only the failed skill is rescheduled |
| R6 | User-selected level ≠ system-validated level | Only the validated level drives progression and archiving |
| R7 | The backend shuffles answer options | Options arrive shuffled and without the key |
| R8 | Exposure count is a priority signal, never a limit or a delete trigger | It orders reuse; it never withholds or retires a word (ADR-006, ADR-018) |
| R9 | Weekly Review measures only | It writes no pipeline state (ADR-023 draws the same line for Practice) |

---

## 2. The system

```
Flutter (iOS/Android)          ASP.NET Core 10                  Python FastAPI        Google
┌────────────────────┐  HTTPS  ┌──────────────────────┐  token  ┌──────────────┐     ┌────────┐
│ Riverpod, go_router├────────►│ Api → Infrastructure ├────────►│ prompts,     ├────►│ Gemini │
│ WordOsApi contract │  JWT +  │  → Application       │ X-Service│ JSON schemas │     └────────┘
│ no business rules  │ refresh │  → Domain            │  -Token  └──────────────┘
└────────────────────┘         └──────────┬───────────┘
                                          │ EF Core 10
                                    ┌─────▼──────┐
                                    │ PostgreSQL │  learners, words, sessions,
                                    │     17     │  events, 175,778-row lexicon
                                    └────────────┘
```

The Gemini key exists only in the Python service's environment. The Flutter client
cannot reach Gemini, and no client request can make the backend call it with
attacker-chosen text.

---

## 3. Getting in

**Register** — name, email, phone, password. Argon2id, never plain text. The response
carries an access token and a rotating refresh token; refresh reuse is detected and
revokes the family.

**Sign in** — the same pair. A learner whose access token expires mid-session is
refreshed transparently and never loses their place.

**Sign out** — clears the session on the device first and revokes on the server
afterwards, so a slow network cannot make the button look dead. It lands on the
**login form**, from which the learner can sign in as somebody else or create an
account — never back on the product tour, which is for a device that has never had an
account (ADR-035's sibling fix; the tour is marked seen on sign-out).

**Roles** — `User` and `Owner`. The Owner area is guarded in the router *and* refused
by the API; hiding a menu item is not access control.

---

## 4. Onboarding

1. **Product tour** — three slides, shown once per installation.
2. **Interests** — a preset grid plus free-text entry. They steer what generated
   passages are *about*, never how hard they are.
3. **Placement test** — adaptive, and the only thing that sets the starting levels.

### Placement (full detail in [`06-PLACEMENT-ALGORITHM.md`](06-PLACEMENT-ALGORITHM.md))

* **Rasch (1PL) with EAP ability estimation** over an item bank whose difficulties are
  expert-assigned on the CEFR ladder (`StepLogits = 0.5`, `PriorMean = -0.25`).
* **It opens easy and never runs uphill.** The first item is the easiest available and
  the ladder reaches at most one band above the current estimate, so a struggling
  learner is never walked upward until they fail (ADR-027).
* **Reading and Listening are key-matched** — no AI is involved in scoring them.
  **Speaking and Writing are evaluated by the AI**, because there is no fixed answer to
  match; a productive skill cannot be placed until the learner has actually produced
  language (ADR-026).
* Speaking items arrive as `SPOKEN` and are answered with the microphone.
* **The result is phrased as an estimate, not a verdict.** The learner is told where
  their practice will start, before any band is shown; nothing says "you are a
  beginner".
* Levels are per skill and independent. Spelling carries no level of its own — its
  content follows Reading (ADR-008, ADR-017).

---

## 5. Adding a word

The learner types into one field, **in English or in Arabic**:

* **English** — prefix search over the lexicon. Typing `bo` offers every sense whose
  word starts with those letters, each with its own Arabic meaning, definition, part of
  speech and CEFR band.
* **Arabic** — the *meanings* are searched and the English words that carry them come
  back: `يذهب` → `go`. Diacritics, tatweel and the alef/ya/ta-marbuta families are
  folded on both sides, because Arabic WordNet vocalises 45,000 of its glosses and
  nobody types the marks (ADR-034).
* **Any real English word resolves.** Irregular forms find their headword
  (`went` → `go`, `children` → `child`); one-letter words (`a`, `I`) are matched exactly;
  and the 167 closed-class words WordNet does not carry — pronouns, articles,
  auxiliaries, modals, prepositions, conjunctions, question words — are authored into
  the lexicon and ranked ahead of their homographs, so `are` is the verb and not a unit
  of area (ADR-033).

**The learner picks a sense, not a word.** `book = كتاب` and `book = يحجز` are two
different vocabulary items with two different pipelines, because they are two different
synsets (ADR-012). There is no path to typing your own meaning: the request body is a
lookup key, the stored lexicon row is what gets copied, and a forged level or
definition is discarded.

**Nothing is invented.** A string the lexicon does not carry produces spelling
suggestions and an explanation — never an "add it anyway" button.

---

## 6. The five skills

Every session is generated server-side, stored, and replayed on resume — so a crash, a
backgrounded app or a lost connection returns the learner to where they were rather
than to a different passage.

### Shared behaviour

* **The learning loop.** A wrong answer sends *the word* to the back of the queue, up
  to three attempts; a first-attempt miss still fails that word for that skill. A
  comprehension question is asked once — the answer has already been shown, so asking
  again teaches nothing (ADR-031).
* **Level control.** Before the questions begin, the learner can change the level of
  the session. Reading and Listening **re-tell the same passage** at the new level
  rather than generating a different one, and the questions are regenerated with it
  (ADR-030). Speaking changes level for the next turn. The choice writes through to
  Settings — and never to the validated level (R6).
* **Typography and direction.** English content is pinned left-to-right whatever the
  interface language is; Arabic meanings render right-to-left. The header carries the
  skill and its level badge at reading size.
* **Audio stops at every section change** — leaving the passage, moving between
  questions, completing, re-levelling. A clip still playing over the questions hands
  out answers.

### Reading

A generated passage on one of the learner's interests, at their level, containing the
session's target words. Then five comprehension questions, then one question per target
word about *that use of it*, not the dictionary entry.

Every word of the passage is tappable and answers instantly with the meaning it carries
**in that sentence** plus its part of speech, because the generator glosses its own
passage as it writes it (ADR-029). Any tapped word can be added to the pipeline on the
spot.

### Listening

The same generated material, heard and not seen. The clip plays on its own; the same
control stops it. Passage length scales with level and is shorter than Reading's. In
the result area the recording comes back as a play/stop/slow control, ordered
answers → audio → transcript, and never autoplays while the learner reads their score.
A device with no speech engine falls back to the transcript and says so.

### Speaking

Opens with a **warm-up**: each word of the session with four meanings, shuffled and
marked server-side. A miss returns to the end of the loop and the warm-up ends only
when every word has been recalled once. It measures nothing — no attempt, no event, no
level moves — because its only job is that nobody walks into a conversation about words
they cannot recall (§26). No words due means no warm-up.

Then a **conversation**, not a list of questions: a tutor that reacts to what was said
and steers toward the remaining words. The microphone is **push-to-talk** — the tutor
greets, the mic stays off, the learner presses to speak, speaks for as long as they
like with no silence cut-off, and presses again when done (ADR-028).

Judgement happens **once, at the end, on the whole conversation** (ADR-019). A word
passes on substantial use, not on mention (ADR-016). **Pronunciation is never scored**:
the transcript comes from speech recognition, so a "mispronunciation" is
indistinguishable from a recogniser error (ADR-020).

### Writing

One sentence per word, alternating between a plain instruction and one that asks for a
sentence about the learner's own life. The AI reports observations — was the word used,
with the intended meaning, in a grammatically appropriate position, understandably —
and **the domain decides**: a small grammar slip never fails correct usage (§32).

### Spelling

The word is written from a clue, with letter tiles below B2 and free typing above. The
tile pool holds decoys, so finishing the pool is not the same as spelling the word.

The clue is the first rung of a **hint ladder**, and every press of "hint" steps down
exactly one rung (ADR-032):

```
dictionary definition → simplified definition → synonym → translation → number of letters
```

Where a learner joins depends on their level — C1 at the top, B2 one down, B1 at the
synonym, A1/A2 at the translation — because a full WordNet definition is often harder
than the word it defines. Rungs with nothing to say are skipped; the translation is
always present, so the ladder can never come back empty; and nothing above the entry
rung is ever shown.

### When nothing is due

A **practice** session: real generated content, no vocabulary attached, and it writes
no pipeline state at all (ADR-023). The learner keeps the habit without the schedule
being falsified.

---

## 7. Levels

Two numbers per skill, and they never mean the same thing:

* **User-selected** — what the learner chose in Settings or in a session. It changes
  what the content *is*, and nothing else.
* **System-validated** — earned from performance over an evidence window: at least 14
  sessions, promotion above 85%, demotion below 70%. Only this may drive progression
  and archiving (R6).

**Archiving.** A word is retired when the learner has visibly outgrown it: its band sits
four ladder steps (two full CEFR bands) below the *weakest* validated skill, and it has
actually been met in content at least three times (ADR-013). Archived is not deleted —
the word, its history and its events remain, and the Owner can still read them.

---

## 8. Weekly Review

Every seven days: a sample of the words studied in the period, answered by selection
with auto-advance — the learner chooses and it moves on, with a pause long enough to
see the answer.

**It changes nothing.** No word passes or fails, no level moves, no schedule shifts. It
exists to measure retention *after* the pipeline has done its work (R9).

---

## 9. My Words

One searchable, paginated list. Pipeline state is deliberately not a set of tabs: the
learner sees their vocabulary, not the machinery. Search runs server-side over the word
and its meaning, so `بحث` and `research` find the same row. Each word opens on its own
history: which skills it has passed, when it is next due, every attempt and event.

---

## 10. Settings

| Setting | Owned by | Notes |
|---|---|---|
| Interface language (Arabic default) | the device | ADR-010 |
| Theme (system/light/dark) | the device | ADR-010 |
| Per-skill level | the server | user-selected, shown apart from the validated level |
| Per-skill daily target (5–15) | the server | |
| Interests | the server | editable at any time |
| Sign out | — | in the toolbar, one tap, with a confirmation |

### Which language the app uses (ADR-035)

The line falls at what the text **is**, not where it came from:

| Follows the app language | Stays English |
|---|---|
| Instructions ("Write the word") | The passage and the audio |
| Evaluation feedback | Comprehension questions and their options |
| Error messages | The spoken conversation |
| Every label, button and result | The whole placement test |

Fixed instructions travel as a **key**, not a sentence, so the client says them in the
learner's language; generated questions carry no key and are shown exactly as they
arrived. The client sends `Accept-Language`, and the API asks the AI for its feedback in
that language — with English words quoted in English inside Arabic feedback rather than
transliterated. Failures are localised by their stable error code, falling back to the
server's sentence for anything newer than the app.

---

## 11. AI behaviour

| Call | Model | What it returns | Who decides |
|---|---|---|---|
| Passage generation | `gemini-flash-lite` | text, sentences, 5 questions, contexts, glossary, target spans | — |
| Re-telling at another level | same | the same story in new language | — |
| Writing evaluation | same | observations + feedback in the learner's language | the domain |
| Speaking turn | same | the tutor's next reply | — |
| Speaking evaluation | same | per-word observations + feedback | the domain |
| Placement (Speaking/Writing) | same | a CEFR estimate for produced language | the placement engine |

Every response is JSON-schema-constrained. Every call records its prompt version, model
and token count, so a change in behaviour is attributable. **No prompt is ever asked for
a verdict** — there is no `passed` field anywhere for a prompt tweak to start filling in
(R2, ADR-015).

**When the AI is unavailable**, content falls back to stored material and the session is
marked as having used a fallback; a re-telling is refused outright rather than replacing
the passage the learner asked to improve with something worse.

**Active vocabulary reuse.** Up to three Active words, least-exposed first, are offered
to the generator each session. Exposure is then *derived by the server* from the text it
received — never reported by a client (ADR-018) — and it only ever orders the queue
(R8).

---

## 12. The Owner dashboard

Reachable only by an Owner account, and refused by the API for everyone else.

* **Overview** — learners, active today/this week, words added, averages, pipeline
  completion, per-skill pass rates, level distributions, top interests, AI fallback
  rate. All of it scoped by a reporting window: all time, today, 5 days, 10 days, or a
  hand-typed number of days (clamped to ten years, on both sides of the wire).
* **Learners** — searchable, paginated, filtered by the same window, where "active"
  comes from the activity log and not from a `LastLoginAt` column that can only record
  one moment (ADR-025).
* **One learner** — their journey, their mistakes, their daily activity, their
  vocabulary by pipeline state, and the placement evidence behind each level with
  initial-versus-current bands.
* **One word** — its entire history from the event log.

---

## 13. Data and privacy

* Passwords: Argon2id. Never logged, never returned, never stored in plain text.
* Tokens: short-lived JWT access + rotating refresh with reuse detection.
* Authorization: every learner-owned row is queried by the caller's own id; another
  learner's id returns 404, not 403, so the id itself reveals nothing.
* Secrets: user-secrets in development, real secret management in production. Nothing
  sensitive is in the client, in the repository or in a log.
* The lexicon is reference data. It is never written from a client request.

Full threat model and the tests that pin it: [`07-SECURITY.md`](07-SECURITY.md).

---

## 14. Robustness

The application is tested against the way people actually use it, not only the happy
path (ADR-036). Specifically pinned:

* Every reporting window a keyboard can produce — including the value that used to
  overflow the server's date arithmetic and empty the dashboard.
* Every kind of rubbish in a search box: wildcards, quotes, script tags, emoji, control
  characters, a pasted NUL byte, 5,000 characters.
* Paging walked off both ends.
* The same answer submitted twice; a session abandoned twice; a skill opened six times
  in the time one session takes to load.
* Leaving a screen while it is still loading, and leaving a listening session with the
  clip still playing.
* Switching language and theme repeatedly with the app already built behind them.
* Signing out from inside a session.

An unreadable query value is a **400**, never a 500. The server log ending a hostile
sweep with zero unhandled exceptions is itself the acceptance criterion.

---

## 15. Verification

| Layer | Count | What it covers |
|---|---|---|
| `WordOs.Domain.Tests` | 94 | the pipeline, the level engine, placement mathematics |
| `WordOs.Api.Tests` | 209 | every endpoint against a real PostgreSQL, plus security and hostile input |
| `mobile/test` | 260 | models, the mock engine's rules, every screen, localisation, stress |
| `mobile/integration_test` | 12 journeys | the real UI against the real stack on a simulator |

The integration journeys are the only tests that can prove the wire contract, the
models and the screens agree with each other, because nothing in them is stubbed:
Flutter → ASP.NET Core → Python → Gemini → PostgreSQL and back.
