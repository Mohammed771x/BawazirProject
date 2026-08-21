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
* **Every question ends when the learner leaves it** (ADR-058). Answering, restarting,
  finishing or leaving the screen stops the clip and closes the microphone, and each
  question builds its own answer field — so nothing typed, heard or recorded on one
  question follows the learner to the next. A result the recogniser was still preparing
  when the microphone stopped is recognised as belonging to the question it was spoken
  for, and dropped.
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
* **The forms of a word are words.** `went`, `gone`, `going`, `taken`, `walked`,
  `mice`, `children` and `women` are entries in their own right, each saying what it
  is — *past tense of "go"* / *(الماضي)* — so a learner can add and practise a form
  as its own vocabulary item (ADR-045). Where the past and the participle share a
  spelling — `played`, `said` — both are offered, because "I played" and "I have
  played" are two things to learn (ADR-046); where only one role is real, only that
  one appears (`beaten` is never the past of `beat`). English has no future form to
  add: "will play" is `will` plus the base. A plural that is just the word plus `s`
  is not a word: no `books`, no `cities`. One-letter words (`a`, `I`) are matched exactly;
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
than to a different passage. A session **records the words it is for** when it starts,
so resuming one returns the same words even for Speaking, which is a conversation and
has no items to recover them from (ADR-039).

### Shared behaviour

* **The learning loop.** A wrong answer sends *the word* to the back of the queue, up
  to three attempts; a first-attempt miss still fails that word for that skill. A
  comprehension question is asked once — the answer has already been shown, so asking
  again teaches nothing (ADR-031).
* **Level control.** The learner can change the level of the session from inside it.
  Reading and Listening **re-tell the same passage** at the new level rather than
  generating a different one, and their questions are regenerated with it — but only
  before the questions begin (ADR-030). Speaking and Writing have no passage to
  re-tell, so their level changes at any point and applies to what happens next: the
  tutor's reply, or the rewrite. In a conversation the band is spelled out for the
  model rather than named — A1 is short sentences of the commonest words with one
  clause, C2 is fully idiomatic — so dropping the level makes the *very next line*
  easier (ADR-044). Spelling has no level of its own (ADR-008).

  The choice **writes through to the learner's profile immediately**, so Settings and
  the Hub show it the moment they look (ADR-038) — and it moves the *user-selected*
  level only. The validated level is earned from performance, and a tap is not
  performance (R6).
* **Typography and direction.** English content is pinned left-to-right whatever the
  interface language is; Arabic meanings render right-to-left. The header carries the
  skill and its level badge at reading size.
* **Audio stops at every section change** — leaving the passage, moving between
  questions, completing, re-levelling. A clip still playing over the questions hands
  out answers.

### Reading

A generated passage on one of the learner's interests, at their level, containing the
session's target words — each in the shape the learner is practising (ADR-047). A word
added as the past participle appears as the past participle; a noun whose plural is
just an `s` may appear either way, because `book` and `books` are one word; a noun whose
plural is a different word (`mouse` → `mice`) appears exactly as given, because the
plural is a separate vocabulary item they have not been taught. Active words reused in
the passage follow the same rule. Then five comprehension questions, then one question per target
word about *that use of it*, not the dictionary entry.

Every word of the passage is tappable and answers instantly with the meaning it carries
**in that sentence** plus its part of speech, because the generator glosses its own
passage as it writes it (ADR-029) — and a **re-told** passage is glossed exactly the
same way, so changing the level does not turn taps back into dictionary lookups
(ADR-039). Any tapped word can be added to the pipeline on the spot.

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
and steers toward the remaining words — by working backwards from each word to a
situation where a person would really say it, changing the subject when the current one
cannot carry it, and never bolting "try to use this word" onto an unrelated question
(ADR-040). The learner's interests choose between the situations a word could live in;
they never force a word somewhere it does not belong. Answering without the word is a
good answer, and the word gets another opening later. The microphone is **push-to-talk** — the tutor
greets, the mic stays off, the learner presses to speak, speaks for as long as they
like with no silence cut-off, and presses again when done (ADR-028). A **bin** sits
beside it while they are talking: it throws the recording away and offers the
microphone again, so a fumbled sentence never has to be sent (ADR-059).

During the conversation the tutor tracks what has actually been said: once a word has
been used in a sentence of the learner's own, it drops off the list and the tutor moves
to the next one — **even if the sentence is wrong**, because moving on is about *use* and
how well it was used is judged at the end (ADR-041). Naming a word — "let me use *hook*
in a sentence" — is not using it, and does not (ADR-040).

The conversation is about **the words the session was opened for**, and only those. A
word that reaches Speaking while it is open — added by the learner, or finished
elsewhere — waits for the next conversation (ADR-039, ADR-050).

The tutor knows what each of those words *is*: the past tense, a plural, or the plain
word (ADR-047), so the question it asks can actually be answered with the form being
practised. And when the learner reaches for a word and gets the form wrong — "I **go**
to my village every year" where the word is `went` — it names the step they missed
rather than repeating the word at them: *"Almost — I need the past tense: 'went'."* It
stays on that word and lets them try the same idea again (ADR-050).

Every practising turn ends by saying which word to use: *Try to use the word "…" in your
answer.* The learner cannot see the list, and guessing which word is wanted is not the
exercise. When the last word lands the tutor closes instead: what they did well, and
goodbye — never another question about a word they have just finished (ADR-042).

Judgement happens **once, at the end, on the whole conversation** (ADR-019), one verdict
per word:

* **used, meant correctly, understandable, no grammar breakdown → passes.** Ordinary
  grammar slips are recorded and ignored: "I research about AI yesterday" has the wrong
  tense, the right meaning, and passes (§32).
* **wrong meaning, or grammar broken enough to obscure it → fails**, and that word alone
  comes back in Speaking after the usual gap. Nothing else about the word is disturbed
  (rule R5).

Whether a word was used at all is read from the transcript, which cannot be wrong about
it; the model is asked only whether a word that appears was being *named* rather than
used. Only the quality of the use is the judge's call.

**What counts as saying it** (ADR-049). The word has to be there as a whole word —
`booking` and `bookshop` are not `book`, and the tutor asks again. But a plain noun's
regular plural *is* the word: answer "I read three **books**" and `book` is done, because
`book` and `books` are one word to a learner and asking again reads as not listening.
Anything else is a different word with a pipeline of its own — `mice` is not `mouse`
(ADR-045), and `researched` is not `research`. Whether the plural counts is the same
fact the passage checks before writing one (ADR-047). A spelling the learner holds
twice — `book` the object and `book` the verb (ADR-046) — is one thing to say: said
once, both move on.

**The result is a lesson, not a scoreboard** (ADR-048), and it is written **to** the
learner rather than about them — "you used it well", never "the learner used" (ADR-059).
Every word comes back with what
the learner did with it, why it was wrong when it was, that it returns another day, their
own words quoted, and one English sentence to copy — in their language, at their level. A word passes on substantial use, not on mention
(ADR-016). **Pronunciation is never scored**: the transcript comes from speech
recognition, so a "mispronunciation" is indistinguishable from a recogniser error
(ADR-020).

### Writing

One sentence per word, alternating between a plain instruction and one that asks for a
sentence about the learner's own life. The AI reports observations — was the word used,
with the intended meaning, in a grammatically appropriate position, understandably —
and **the domain decides**: a small grammar slip never fails correct usage (§32).

The learner is then shown their own sentence **as a writer at their level would put
it** — same idea, same content, raised or simplified to match the band, with the target
word kept. This is what the level control on this screen is for, and it is not a
grammar correction: the same sentence comes back plainer at A2 and more elaborate at
C1, and the card says "Your sentence at B2" rather than anything resembling a red pen
(ADR-038).

### Spelling

The word is written from a clue, exactly as the learner added it — Spelling never
varies the form, because here the spelling *is* the question (ADR-047). Letter tiles
below B2, free typing above. The
tile pool holds decoys, so finishing the pool is not the same as spelling the word — and
it holds a **space** tile for a two-word entry like *alarm clock*, which otherwise could
not be spelled at all. Spelling is judged on the letters, not the spacing: `alarmclock`
and `alarm clock` both count (ADR-042).

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

Every entry says **what kind of word it is** — noun, verb, adjective, adverb, and the
closed classes down to *فعل مساعد* and *فعل ناقص* — and, when the entry is an inflection
rather than the plain word, **which form**: *فعل · الماضي* for `went`, *فعل · اسم
المفعول* for `played` (ADR-056). Both are said in the learner's language, never as the
lexicon's own codes. A plain word carries no form label, because nobody needs telling
that `book` is `book`.

This exists because a list can hold `go`, `went`, `gone` and `going` at once (ADR-045),
and `book` the noun beside `book` the verb (ADR-046) — four rows and two rows that the
spelling alone cannot tell apart.

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
**What the numbers mean** (ADR-052). Every figure here is about *learners*: an Owner
trying the product is excluded from all of them, because the denominators count
learners and mixing the two reported a developer's afternoon as how the audience is
doing. The learner list is the one exception, and says so — it counts **accounts**,
since it contains the Owner's own.

Units are stated rather than implied. A per-skill line reads *"24 sessions · words: 11
passed, 4 failed"*: sessions are sessions, the other two are attempts on words, and the
three never sum because one session covers several words. First-attempt accuracy is
measured over **words the skill has decided** — not over attempts, which every retry
inflates while the numerator cannot move.

"Today" is the learner's day, at a configured product-wide offset (default +3), not
the server's and not UTC: under a UTC boundary every morning before 3am belonged to
yesterday, and a learner who had added six words was told they had added none.

The session-length tile reports the **typical** session rather than the mean. A session
is resumable by design (ADR-039), so one finished the next morning is a duration in
hours — measured: median 16 seconds, mean 45 minutes, longest 46 hours.

* **Feedback** — what learners have written, unhandled first (ADR-053). Each message
  shows their words unedited, who wrote them, their email and phone, and the build it
  was sent from. Marking one handled is reversible, and handled messages fade rather
  than vanish — a list that empties as it is read gives no way back to one marked by
  mistake. Learners write and can never read: no call anywhere returns feedback to a
  learner, their own included.
* **Contact** — on each learner's page, their email and phone, tap to copy. Owner-only,
  and there because the next thing after reading a report is reaching the person who
  sent it.
* **Skip 2 days** — brings that learner's waiting skills forward so the spaced
  gaps can be tested without waiting a week (ADR-037). It moves scheduled dates
  only, never anything that already happened, and the skip is written to the
  activity log so a pipeline finished in an afternoon cannot be mistaken for an
  extraordinary learner. Owner-only, and refused for an ordinary learner even on
  their own schedule.

---

## 12b. Reaching a person

Registration requires a **phone number** (ADR-054) — feedback is only as useful as the
Owner's ability to answer it, and a bug report from someone nobody can reach is a
problem that cannot be followed up. The rule applies to creating an account, never to
having one: accounts made before it continue to work untouched.

Settings carries **Message the team**: one field, one button, and a confirmation that
it arrived. It exists because there was previously no way for a learner to report
anything at all, and a learner who hits a bug and can tell nobody simply stops — which
is the one failure this experiment cannot afford, because it leaves no measurement
behind (ADR-053).

Beside it sits **Contact support**, which opens WhatsApp on the developer's number in
one tap — no dialog, no form (ADR-055). The two are deliberately different things: the
message box is for something that can wait and be read later, the button is for a
learner who is stuck now. If WhatsApp cannot be opened, the number is shown so they can
copy it.

The message is stored as written and shown to the Owner as text. It is never
interpreted — no markup, no links, no HTML anywhere on the path — bounded at 4 000
characters at both ends, and stripped of the control characters PostgreSQL refuses.
The build and platform travel with it so a crash report arrives with a version
attached. The activity log records *that* they wrote, never what they wrote.

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
