# WordOS — REST API Contract (v1)

> **Implementation status (2026-08-16).** Every section below is implemented in
> the ASP.NET Core backend against real PostgreSQL, and the Flutter client is
> wired to all of it — verified by an integration test that drives the real UI
> against the running stack (`mobile/integration_test/`).
>
> | Area | Status |
> |---|---|
> | Auth (`register`/`login`/`refresh`/`logout`), `/me` | ✅ |
> | Onboarding: interests catalogue, `PUT /me/interests` | ✅ |
> | Placement: `start` / `answer` / `complete` (adaptive) | ✅ |
> | Skills Hub `GET /hub` | ✅ |
> | Words: `lookup` (prefix), `POST /words`, `GET /words` | ✅ |
> | Settings: skill level, daily target, `/config` | ✅ |
> | Admin: `overview` / `users` / `users/{id}` | ✅ |
> | Skill sessions (five skills), resume, abandon | ✅ |
> | Weekly review | ✅ |
> | Exposure from AI content reuse | ✅ (ADR-018) |
>
> All five skills, resume included, are verified through the **real Flutter UI**
> against this backend — see `mobile/integration_test/`.

The single interface between **Flutter** and the **C# backend**. The Flutter `WordOsApi`
abstraction and the Phase-1 mock both implement exactly this; Phase 5 makes it real.

- Base: `/api` · JSON · `Authorization: Bearer <jwt>` on everything except auth endpoints.
- Enum values are the SCREAMING_SNAKE strings from `04-DATA-MODEL.md`.
- Dates are ISO-8601 UTC.
- Errors: `{ "error": { "code": "STRING_CODE", "message": "...", "details": {...} } }` with
  conventional HTTP status codes.

---

## Auth & profile

| Method | Path | Body → Response |
|---|---|---|
| POST | `/auth/register` | `{email, password, displayName, phoneCountryCode, phoneNumber}` → `AuthResponse` — the number is **required** (ADR-054) |
| POST | `/auth/login` | `{email, password}` → `AuthResponse` |
| POST | `/auth/refresh` | `{refreshToken}` → `AuthResponse` (rotates the token) |
| POST | `/auth/logout` | — → `204` |
| POST | `/auth/password/forgot` | `{email}` → **always** `202 {message}` (ADR-078) |
| POST | `/auth/password/reset` | `{email, code, newPassword}` → `200 {message}` |
| GET | `/me` | — → `UserProfile` |

### Forgotten password

```jsonc
// POST /auth/password/forgot
{ "email": "learner@example.com" }

// 202 — identical for a registered address, an unknown one, and an email
// provider that is down. Anything that differed would turn this into a way to
// ask "does this person have an account here".
{ "message": "If that email is registered, a code is on its way." }
```

A six-digit code is emailed, valid for `PasswordResetCodeExpiryMinutes`
(15). Requesting another retires the previous one.

```jsonc
// POST /auth/password/reset
{ "email": "learner@example.com", "code": "048213",
  "newPassword": "at-least-eight" }

// 200 — and no tokens. The learner signs in afterwards with the password they
// just chose, so an intercepted code alone is not a session.
{ "message": "Password changed. Please sign in." }
```

Every failure is one answer, `400 INVALID_RESET_CODE`: wrong code, expired
code, already-used code, too many wrong guesses, and an email that was never
registered. Distinguishing them would say whether the account exists, or
confirm that a guessed code once did. Five wrong guesses burn the code —
which is what makes six digits defensible against a search space of a million.

Redeeming a code **revokes every refresh token** for that user. A reset exists
to answer "someone else knows my password", and leaving their session alive
would answer it halfway.

```jsonc
// AuthResponse
{ "token": "jwt", "expiresAt": "2026-08-16T10:00:00Z",
  "refreshToken": "opaque", "user": { /* UserProfile */ } }

// The access token is short-lived; the refresh token is long-lived, single-use
// and rotated. Presenting one twice revokes the whole family — a leaked token is
// worth one call. Clients refresh on a 401 and retry once, so an expired access
// token never signs a learner out mid-session.

// UserProfile
{
  "id": "u_1", "email": "a@b.c", "displayName": "Ahmed", "role": "USER",
  "onboardingStage": "COMPLETE",              // INTERESTS | PLACEMENT | COMPLETE
  "interests": ["technology", "football"],
  "skillLevels": [ /* SkillLevel */ ],
  "createdAt": "2026-08-12T09:00:00Z"
}

// SkillLevel  (one per skill — levels are independent)
{
  "skill": "READING",
  "userSelectedLevel": "B1",
  "systemAssessedLevel": "B1",
  "evaluationSessions": 6,
  "rollingAccuracy": 0.82,
  "dailyTargetWords": 10,
  "confidence": 0.78          // placement precision, 0–1
}

// Both level fields are NULL for SPELLING — it is measured but carries no CEFR
// band (ADR-008). Clients branch on the skill, never on a sentinel level.
{
  "skill": "SPELLING",
  "userSelectedLevel": null,
  "systemAssessedLevel": null,
  "evaluationSessions": 3,
  "rollingAccuracy": 0.75,
  "dailyTargetWords": 10,
  "confidence": 1.0
}
```

## Onboarding

| Method | Path | Body → Response |
|---|---|---|
| GET | `/onboarding/interests` | — → `[{slug, labelEn, labelAr, emoji}]` |
| PUT | `/me/interests` | `{interests: ["technology", "قراءة"]}` → `UserProfile` |
| POST | `/placement/start` | — → `PlacementStep` |
| POST | `/placement/{id}/answer` | `{itemId, answer}` → `PlacementStep` |
| POST | `/placement/{id}/complete` | — → `PlacementResult` |

Placement is **adaptive** (ADR-009, [`06-PLACEMENT-ALGORITHM.md`](06-PLACEMENT-ALGORITHM.md)):
the next question depends on how the previous one was answered, so the client is
handed one item at a time instead of a fixed list.

```jsonc
// PlacementStep — `item` is null exactly when `isComplete` is true
{
  "sessionId": "pt_1",
  "isComplete": false,
  "item": {
    "id":"rd_b1_1", "skill":"READING", "type":"MULTIPLE_CHOICE",
    "passage":"…optional…", "audioText":null,          // audioText = TTS stem
    "prompt":"What surprised the library staff?",
    "options":["…","…","…","…"]                       // already shuffled (R7)
  },
  "progress": { "answered": 4, "estimatedTotal": 8,    // exact since ADR-098
                "currentSkill":"READING", "skillCount":4 }
}
// `skillIndex` was listed here and has never been sent — not by this version and
// not by the first. Found by starting a placement on the live service and reading
// what came back (2026-09-28). Harmless: both the current app and every older one
// read it as `?? 0`, and no screen uses it. Recorded rather than silently removed
// so nobody adds a screen that trusts it.


// PlacementResult — levels are computed server-side (ADR-007)
{
  "levels": [ /* SkillLevel — `confidence` is meaningful here */ ],
  "spelling": { "itemsAnswered": 0, "correct": 0,
                "supportMode": "LETTER_TILES" },       // always (ADR-100)
  "summary": "…short human text…"
}
```

> **Eight questions**: three Reading, three Listening, one Speaking, one
> Writing (ADR-098). `estimatedTotal` keeps its name and is now the real total;
> it is still clamped upwards, so a run started under an older, longer version
> of the test cannot report more answered than there are questions.
>
> Spelling is no longer asked about. The `spelling` object stays in the payload
> — it is published contract, and a client that drew a "spelling measured" row
> reads 0 of 0 rather than crashing on a missing object — and `supportMode` is
> the mode every learner now starts on.
>
> Speaking and Writing are one question each, so their `confidence` is near
> zero by construction and the client shows those rows as provisional.

> The item's difficulty band is deliberately **not** in the projection, and the
> correct answer never leaves the server.
>
> Errors: `ITEM_NOT_CURRENT` (409) if the answered item is not the active one —
> a retry after a dropped connection must not corrupt the estimate;
> `PLACEMENT_INCOMPLETE` (409) on completing early; `PLACEMENT_NOT_FOUND` (404)
> for an expired run.

## Skills Hub

| Method | Path | Response |
|---|---|---|
| GET | `/hub` | `HubState` |

```jsonc
{
  "dailyProgress": { "wordsAddedToday": 4, "dailyTarget": 10 },
  "skills": [
    { "skill":"READING", "availability":"AVAILABLE",   // AVAILABLE | EMPTY | LOCKED
      "dueWordCount": 7, "sessionWordCount": 5,        // sessionWordCount = target-capped
      "level":"B1", "nextDueAt": null,
      // An unfinished session. This is how a session survives the app being
      // killed: the client stores nothing about sessions (R4), it asks the hub.
      // A skill with one is AVAILABLE even when no new words are due.
      "activeSessionId": "s_9" },
    { "skill":"LISTENING", "availability":"EMPTY", "dueWordCount":0,
      "sessionWordCount":0, "level":"B1", "nextDueAt":"2026-08-14T00:00:00Z",
      "activeSessionId": null }
  ],
  // `nextAvailableAt` is the day the learner's first words ripen and is set
  // whenever `available` is false — the card waits in plain sight with the date
  // on it rather than disappearing for a week (ADR-089).
  // `practiceAvailable` is true once a review is finished and no challenge is
  // open: the card then offers that review's words again as practice rather
  // than locking (ADR-120). `practiceWordCount` is how many.
  "weeklyReview": { "available": true, "wordCount": 23,
                    "wordsWaitingAfterThis": 13,
                    "periodStart":"2026-08-05T00:00:00Z", "nextAvailableAt": null,
                    "practiceAvailable": false, "practiceWordCount": 0 },
  "vocabulary": { "learning": 31, "active": 12, "archived": 0 }
}
```

> The client renders `availability` verbatim. It never computes it (R1).

## Words

> **`Accept-Language`** — every request may carry it (`ar`, `en`; default `ar`).
> It changes what the app *says to* the learner — generated feedback, and which
> instruction key it is expected to render — and never what it teaches them:
> passages, comprehension questions, options and the placement test are English
> whatever it says (ADR-035).

| Method | Path | Body → Response |
|---|---|---|
| GET | `/words/lookup?q=bo` | → `[WordCandidate]` (either language; spelling suggestions when nothing matches) |
| GET | `/words/define?w=researching` | → `{query, matchedText, senses:[WordCandidate]}` |
| POST | `/words` | `{senseId, text?, meaning?}` → `Word` — a lexicon sense |
| POST | `/words` | `{text, customMeaning}` → `Word` — a meaning the learner wrote, AI-checked; a rejected meaning is never saved (ADR-072, ADR-074, ADR-112) |
| POST | `/words` | `{text, fromSessionId}` → `Word` — the meaning that passage gave it (ADR-073) |
| GET | `/words?state=LEARNING\|ACTIVE\|ARCHIVED&q=&page=&pageSize=` | → `{items:[Word], total, page, pageSize, hasMore}` |
| GET | `/words/{id}` | → `WordDetail` |
| DELETE | `/words/{id}` | → `204` — the learner removes it (ADR-071) |
| PATCH | `/words/{id}/meaning` | `{meaning}` → `Word` — rewrite the Arabic meaning, keeping the journey (ADR-101, ADR-112) |
| PATCH | `/words/{id}/meaning` | `{meaning, replaceWithSenseId}` → `Word` — swap for the word that meaning belongs to, from the beginning |

> **The meaning may change; the word may not** (ADR-101). Nothing about the
> journey moves — state, current skill, every skill's status, attempts and
> schedule, the exposure count and `addedAt` are all left exactly as they were.
> A word that must start again does so by being replaced, not by being edited.
>
> Two authorities answer "is this still the same word", in this order:
>
> * **the dictionary**, when it recognises the wording — the only thing that can
>   *name* the English word a meaning belongs to. `409
>   MEANING_IS_ANOTHER_WORD` carries `candidates: [WordCandidate]`;
> * **the checker**, when the dictionary has never seen the wording: `409
>   MEANING_REJECTED` with the meanings it would accept. Final (ADR-112) — the
>   word keeps the meaning it had.
>
> The sense id travels with the meaning when the new wording is another sense of
> the same word, so the stored English definition still describes what the
> learner says it means. `409 WORD_ALREADY_ADDED` when they already own that
> sense. Sending the meaning it already has is a no-op: nothing is asked and
> nothing is written.
>
> The swap is one request because it is one decision — the old word is deleted
> and the new one added in the same transaction, so a learner who agreed to it
> cannot end up holding both, or neither.

> **Lookup searches from either side and never invents an entry.** `bo` returns
> every sense whose word starts with those letters, each row carrying the word,
> its CEFR level and the Arabic meaning of *that sense*. Beyond the prefix:
>
> * a query written in Arabic searches the **meanings** and returns the English
>   words that carry them — `يذهب` → `go` — with diacritics folded off both
>   sides (ADR-034);
> * when nothing starts with what was typed, irregular and inflected forms are
>   resolved, so `went` → `go` and `children` → `child` (ADR-033);
> * a single letter is matched **exactly**, so `a` and `I` are addable while one
>   letter still never returns the dictionary.
>
> If nothing matches, the response contains only candidates with
> `isSpellingSuggestion: true`, or is empty. That is no longer the end of the
> road: the word can still be added with a written meaning, and the checker
> answers for it instead of the lexicon (ADR-075).
>
> Closed-class words (`is`, `the`, `because`, `what`) carry a part of speech
> WordNet does not use — `pron`, `det`, `aux`, `modal`, `prep`, `conj`, `part`,
> `intj` — because they are authored rather than imported (ADR-033).
>
> **`define` answers one word, not a search.** It is what a tap inside a
> reading passage calls (ADR-022): the word arrives inflected and is resolved
> against the lexicon here, never on the device. `matchedText` is the spelling
> that answered — null, with an empty `senses` array and a `200`, when the
> lexicon has no entry. Proper nouns and numbers appear in generated prose and
> are not an error.
>
> **The list is searched and paged server-side.** `q` matches the word (case
> insensitive) or its Arabic meaning; `total` counts every match, not the page.
> `pageSize` is clamped to 100, and omitting the paging parameters is
> legitimate — they default rather than 400.
>
> **`POST /words` has three ways in, and they differ only in where the Arabic
> meaning comes from.** The word is resolved against the lexicon in all three —
> but on the `customMeaning` path a miss is no longer a refusal (ADR-075).
>
> * **`senseId`** — a lexicon sense. The body is treated as a **lookup key**;
>   the row that gets stored is the lexicon's, so a forged level, definition or
>   part of speech is discarded (ADR-012). A `custom:` id posted here is refused
>   with `BAD_SENSE`.
> * **`customMeaning`** — the Arabic meaning the learner typed (ADR-072). The
>   word is resolved for its level, part of speech and English definition; the
>   meaning is stored as written. Refused with `MEANING_NOT_ARABIC` (400) if it
>   is not Arabic — every skill marks answers against this string.
>
>   **The word need not be in the lexicon** (ADR-075). When it is not, the
>   checker is asked a second question — *is this English, and what kind of word
>   is it* — and supplies the level, part of speech and English definition the
>   lexicon could not. A string it does not recognise is `WORD_NOT_RECOGNIZED`
>   (409), carrying `correctedWord` when it can name the spelling that was meant:
>
>   ```json
>   { "error": { "code": "WORD_NOT_RECOGNIZED",
>                "message": "كلمة \"recieve\" مكتوبة بشكل خاطئ، والصحيح هو \"receive\".",
>                "correctedWord": "receive" } }
>   ```
>
>   Final: nothing downstream can teach a string that is not a word. A word the lexicon *does* hold is never
>   re-judged — the model does not get to refuse a dictionary entry.
>
>   **This path, and only this path, is checked by the AI** (ADR-074). A
>   disagreement is `MEANING_REJECTED` (409) carrying the checker's own sentence
>   plus `suggestions` (meanings it would accept) and `corrected` (their wording
>   with its spelling fixed, when that was the only problem):
>
>   ```json
>   { "error": { "code": "MEANING_REJECTED",
>                "message": "كلمة \"إنسان\" تعني human، بينما \"book\" تعني كتاباً…",
>                "suggestions": ["كتاب", "حجز", "سجل"],
>                "corrected": null } }
>   ```
>
>   Final (ADR-112): nothing is saved until the learner writes a wording the
>   checker accepts or picks a suggestion. There is no `acceptAnyway`; an older
>   client that sends it is ignored and refused the same way. If the checker is
>   unreachable, nothing is saved: `MEANING_CHECK_UNAVAILABLE` (503). There is
>   no fallback, deliberately.
>
>   The lexicon and passage paths are **not** checked: neither gloss is the
>   learner's guess.
> * **`fromSessionId`** — the meaning that session's passage gave the word
>   (ADR-073). **No meaning is sent.** The server reads it from the glossary it
>   stored when it generated the passage; a meaning in the body is ignored.
>   `NOT_IN_PASSAGE` (404) for a word the generator did not gloss, which the
>   client answers by offering the ordinary dictionary instead.
>
> Errors shared by all three: `BAD_WORD` (400) for a missing word;
> `WORD_NOT_FOUND` (404) when the word — or the sense — is not in the lexicon;
> `WORD_ALREADY_ADDED` (409) when this learner already has that word with that
> meaning. Deleted words are invisible to the duplicate check, so a word the
> learner removed can be added again (ADR-071).
>
> **`DELETE /words/{id}` is a state change, not a removal.** `204`, and the word
> leaves every learner-facing response — this list, sessions, the weekly review.
> The row and its whole history stay for the Owner's analytics, which is why
> rule R8 is not in tension with it: R8 forbids the *system* removing a word
> (ADR-071). `404` for a word belonging to another learner — never `403`, so the
> id confirms nothing. Deleting an already-deleted word answers `204`: a retry
> is not a failure. An open session that included the word finishes normally and
> applies nothing to it.
>
> **Identity is the sense, not the word.** `book = كتاب` and `book = يحجز` are
> different synsets and therefore independent vocabulary items with independent
> journeys; adding either one twice is a duplicate.

```jsonc
// WordCandidate — one sense
{ "senseId":"oewn-06410904-n",
  "text":"book", "meaning":"كتاب", "definitionEn":"a written or printed work…",
  "partOfSpeech":"noun", "suggestedLevel":"A1", "isSpellingSuggestion": false }

// Word
{
  "id":"w_1", "senseId":"oewn-03862676-n",
  "text":"operating system", "meaning":"نظام تشغيل",
  "definitionEn":"software that manages hardware and software resources",
  // `partOfSpeech` is the lexicon's own code — `n`, `v`, `a`, `r`, and the
  // closed-class `prep`/`det`/`pron`/`conj`/`aux`/`modal`/`intj`/`part`. The
  // client names it in the learner's language; it is never shown raw (ADR-056).
  //
  // `form` says which inflection this entry is — "past", "pastParticiple",
  // "ing", "plural" — or is absent for the plain word. A key, not a sentence,
  // for the same reason: the learner reads it in their own language (ADR-035).
  "partOfSpeech":"noun", "form":null,
  "cefrLevel":"B1", "state":"LEARNING",
  "currentSkill":"READING", "addedAt":"2026-08-12T09:10:00Z",
  "nextEligibleAt":"2026-08-12T09:10:00Z", "exposureCount":0,
  "skills":[ {"skill":"READING","status":"AVAILABLE","availableAt":"…","attempts":0},
             {"skill":"LISTENING","status":"PENDING","availableAt":null,"attempts":0} /* … */ ]
}

// WordDetail = Word + { "events": [{"type":"SKILL_PASSED","skill":"READING","createdAt":"…"}] }
```

## Notifications

| Method | Path | Body → Response |
|---|---|---|
| GET | `/notifications/daily` | → `{reminders:[Reminder]}` — what this learner's phone should schedule (ADR-076) |

```json
{ "reminders": [
    { "slot": "MORNING", "date": "2026-09-14", "hour": 8, "minute": 0,
      "kind": "WORDS_DUE", "message": "WORDS_DUE_STREAK", "count": 5 },
    { "slot": "EVENING", "date": "2026-09-14", "hour": 20, "minute": 0,
      "kind": "WORDS_DUE", "message": "WORDS_DUE_FIVE_MINUTES", "count": 0 } ] }
```

> `kind` is the coarse one — three values, unchanged since ADR-076. `message` is
> **which of the twenty lines** to say (ADR-090); a client that does not
> recognise it falls back on `kind`, which is why both are sent. `count` is the
> number *in that line* and is zero when it has none — what it counts depends on
> the message (words due, days of a streak, days until the next word ripens), so
> no client has to guess what it was handed.
>
> Every message is chosen from facts the server holds. Anything that looks
> backwards — a streak, yesterday, a level that rose — is only ever scheduled for
> **today's** slots, because these fire up to a week later with no network and a
> fact that has gone stale is a notification that lies.

> **There is no push.** These are local notifications: the phone sets alarms for
> itself, fires them offline with the app closed, and has nobody to ask what
> they should say at the time. So the server decides in advance and the phone
> only schedules — which is rule R1 applied to a sentence about the future.
>
> * **One entry per time of day for the next seven days**, each with the count
>   that will be true *at that moment*. A word waiting out a spaced gap is
>   `NOTHING_DUE` on Tuesday and `WORDS_DUE` on Thursday, and both are in the
>   same response.
> * **`hour`/`minute` are wall-clock**, scheduled in the device's own timezone;
>   a learner means the time on their own phone. The counts are computed against
>   the product's reporting offset.
> * **`kind`** is a stable key, never a sentence — the server does not know
>   which language the installation reads (ADR-035). `WORDS_DUE` (count = words
>   due), `NOTHING_DUE` (count = words in the pipeline), `NO_WORDS` (count 0,
>   and a different message from `NOTHING_DUE`: never having started is not the
>   same as having nothing due).
> * **Slots already past are omitted.** A phone handed a past time fires it
>   immediately or drops it silently; neither is a reminder.
> * Refetched on every app open and resume, and each refresh **replaces** what
>   is pending rather than adding to it.

## Skill sessions

| Method | Path | Body → Response |
|---|---|---|
| POST | `/sessions/{skill}/start?practice=` | — → `SkillSession` |
| GET | `/sessions/{id}` | — → `SkillSession` (resume where the learner was) |

| POST | `/sessions/{id}/answer` | `{itemId, answer}` → `AnswerResult` |
| POST | `/sessions/{id}/writing` | `{itemId, answer}` → `WritingEvaluation` |
| POST | `/sessions/{id}/speaking/turn` | `{transcript}` → `SpeakingTurn` — `isFinal` only once every word has been used (or the safety stop); a word counts only when used in an answer, never in a request for a question or an explanation (ADR-113) |
| POST | `/speech/transcribe` | raw audio (`Content-Type: audio/mp4`, …) → `{text}` — a draft for the learner to correct (ADR-107) |
| POST | `/speech/synthesize` | `{text}` → `{audio, mimeType, durationMs, words[{start, end, charStart, charEnd}], timing}` — Edge's Ava voice, Gemini behind it (ADR-108, ADR-110) |
| POST | `/sessions/{id}/level` | `{level}` → `SkillSession` — re-levels the session (ADR-030, ADR-038) |
| POST | `/sessions/{id}/complete` | — → `SessionResult` |
| POST | `/sessions/{id}/abandon` | — → `204` |

> **`POST /sessions/{id}/level`** behaves differently by skill, because the
> skills differ in what a level *is*:
>
> * **Reading, Listening** — the same passage is re-told at the new level and its
>   questions regenerated. Refused with `SESSION_STARTED` once the learner has
>   answered anything, because re-telling replaces the items their answers belong
>   to.
> * **Speaking, Writing** — nothing is regenerated. The level is an input to what
>   happens next: the tutor's next reply, or the rewrite the learner is shown
>   after they write. Allowed at any point.
> * **Spelling** — `LEVEL_NOT_ADJUSTABLE`. It carries no CEFR band of its own;
>   its content follows Reading (ADR-008).
>
> In every allowed case the chosen level is also written to the learner's
> **user-selected** level for that skill, so Settings and the Hub agree with the
> session. The **system-validated** level is untouched: it is earned from
> performance, and a tap is not performance (R6).

`GET /sessions/{id}` exists because the client holds no session state (R1):
after a crash or a backgrounded app it asks the server where it was. The stored
content is replayed, never regenerated — a second generation would produce a
different passage.

`start` returns `409 NO_WORDS_DUE` when the spaced gap has not elapsed. An
unfinished session for the same skill is **resumed**, so words are never
consumed twice, and that is now enforced by a unique index rather than by a
check — simultaneous starts yield one session and one AI call, and the losers
receive the winner's session (ADR-063). A request that arrives while the winner is
still generating gets `409 SESSION_STARTING` — the session exists but has
nothing in it yet, and handing back a half-built one would be an empty screen.
`409 SESSION_RACE` covers the vanishing case where the winner finishes in
between. Both mean the same thing to a client: ask again in a moment.

`abandon` deletes the session and leaves its words due. It refuses a session
that has already been completed — `409 SESSION_COMPLETE`, the same answer every
other session route gives — because deleting a finished session destroys the
record of what happened in it.

`?practice=true` asks instead for a session with no vocabulary attached — real
content and real comprehension questions, `isPractice: true`, no target words,
and no effect on any word or level (ADR-023). Reading and Listening only;
anything else still answers `409`. Never substituted silently: a plain `start`
with nothing due is still `NO_WORDS_DUE`.

An open practice session **stands aside** for a `start` that asks for real words
when any are due, and expires after `PracticeSessionExpiryHours` (24). It is
kept when nothing is due — being told there is nothing to do must not also cost
the learner the practice they were half-way through.

> **`POST /speech/transcribe`** takes the recording itself as the body — no
> multipart — typed `audio/mp4`, `audio/m4a`, `audio/aac`, `audio/wav`,
> `audio/ogg`, `audio/webm`, `audio/mpeg` or `audio/flac`. It needs a signed-in
> learner and no session, and changes nothing: the text is shown to the learner
> to read and correct, and only what they send through `speaking/turn` counts.
> `text` is empty when nobody spoke. Errors: `415 UNSUPPORTED_AUDIO`,
> `400 EMPTY_AUDIO`, `413 AUDIO_TOO_LARGE` (over `Capacity:MaxAudioBytes`, 8 MB),
> `503 SPEECH_UNAVAILABLE` (no engine answered — offer typing), `503 AI_BUSY`.

> **`POST /speech/synthesize`** speaks a tutor reply or a piece of a Listening
> passage (≤ `Capacity:MaxSpeechChars`, 1500). `audio` is base64 — MP3, or WAV
> when the server has no encoder; `words` holds every word of `text` in order,
> with its time in milliseconds and its character span **in the text exactly as
> sent** (it is not trimmed). `timing` is `aligned` (from the voice's own word
> marks, or measured from the audio) or `estimated`. Which voice spoke is
> the server's choice (`TTS_PROVIDERS`, ADR-110) and is not part of the contract.
> Errors: `400 EMPTY_TEXT`, `413 TEXT_TOO_LONG`, `503 VOICE_UNAVAILABLE` (the app
> speaks with the phone's voice), `503 AI_BUSY`.

Rate limits: `start`, `writing`, `speaking/turn`, `speech/transcribe` and
`speech/synthesize` cost
provider calls and carry the tight budget; `answer` and `complete` do not and are covered by the global
per-user limiter.

```jsonc
// SkillSession — one shape, skill-specific parts nullable
{
  "id":"s_1", "skill":"READING", "levelUsed":"B1",
  "isPractice": false,                          // true → owns no words (ADR-023)
  // The language this session's own task instructions are given in, or null for
  // the learner's (ADR-088). "EN" on a WRITING session at B1 and above; never
  // set on any other skill. The client renders what it is handed — the decision
  // is the level's, and the level lives here (R1). It moves the instruction
  // only: the buttons and headings around it stay in the interface language.
  "instructionLanguage": null,
  "content": {                                  // READING / LISTENING only
     "text":"Ahmed was studying computer science…",
     "revealTextAfterTest": false,              // true for LISTENING
     // Where the session's own words sit in the text, so the client marks them
     // without searching for them (R1). Ordered; `length`, not an end offset.
     "targetSpans":[{"wordId":"w_1","start":72,"length":16}]
  },
  "targetWords":[{"wordId":"w_1","text":"operating system","meaning":"نظام تشغيل"}],
  "items":[
    // Reading and Listening always carry EXACTLY five COMPREHENSION items,
    // before any TARGET_WORD item.
    {"id":"it1","type":"COMPREHENSION","wordId":null,
     "prompt":"What was Ahmed studying?","options":["…","…","…","…"]},

    // READING target word: the three sentences around it, so the learner
    // infers the meaning from context rather than recalling a translation.
    {"id":"it6","type":"TARGET_WORD","wordId":"w_1",
     "context":{"before":"He had never used Linux before.",
                "sentence":"The operating system is the interface between…",
                "after":"Everyone in the group wrote that down."},
     "prompt":"What does \"operating system\" mean here?",
     // The register follows the learner's band (ADR-088), not the skill:
     //   A1–A2+   the Arabic meaning, as below
     //   B1–B2    a plain-English definition, written for this sentence
     //   B2+ …C2  the dictionary's own definition, unchanged
     // All four options are always in one language — a single line in the other
     // script marks itself out and the question can be passed without knowing
     // the word.
     "options":["نظام تشغيل","لوحة مفاتيح","شبكة الإنترنت","برنامج رسم"]},

    // LISTENING target word: the same three sentences, spoken and never shown.
    // `context` is null; the client must not render `audioText` as text.
    {"id":"it7","type":"TARGET_WORD","wordId":"w_1",
     "context": null,
     "audioText":"He had never used Linux before. The operating system is…",
     "prompt":"What does \"operating system\" mean here?",
     "options":["…","…","…","…"]},

    {"id":"it8","type":"SPELLING_TASK","wordId":"w_1",
     "clue":"software that manages a computer's resources",   // = hints[0].text
     "clueKind":"DEFINITION_EN",                              // = hints[0].kind
     // The hint ladder, easiest last. The client shows hints[0] as the clue and
     // reveals one more rung per press; it never picks the help itself
     // (ADR-032). Rungs with nothing to say are absent, so the array is
     // between one and five entries long and always ends at LETTER_COUNT.
     "hints":[
       {"kind":"DEFINITION_EN","text":"software that manages a computer's resources"},
       {"kind":"SIMPLIFIED_DEFINITION","text":"software that manages a computer"},
       {"kind":"SYNONYM","text":"OS"},
       {"kind":"ARABIC_MEANING","text":"نظام تشغيل"},
       {"kind":"LETTER_COUNT","text":"15"}],
     "letters":["o","p","e","r","a","t","i","n","g"],   // shuffled, with decoys
     "inputMode":"LETTER_TILES"},                       // always, at every level
    {"id":"it9","type":"WRITING_TASK","wordId":"w_1",
     "clue":"نظام تشغيل",
     "prompt":"Write one sentence using \"operating system\"."}
  ],
  "conversation": {                              // SPEAKING only
     "opening":"Hi Ahmed! What are you studying this week?",
     // The whole exchange. Empty on a fresh session, populated on a resumed
     // one — the client keeps no transcript, so anything omitted here is lost
     // to a learner who comes back.
     "turns":[ {"fromAi":true,"text":"Hi Ahmed! …"},
               {"fromAi":false,"text":"I read a research paper…"} ]
  },
  "progress": { "nextItemId":"it1", "remaining":7, "answered":0, "total":7,
                // Whether anything has been attempted — NOT the same as
                // `answered`, because a wrong answer requeues and clears
                // nothing. Resuming uses this to know the passage/audio step is
                // already behind the learner.
                "attempted": false },
  "usedAiFallback": false      // true when the AI service was unreachable
}
// options are ALREADY SHUFFLED by the backend (R7); the client must not reorder them.
// A SPEAKING session has no `items` at all — it is a conversation, driven by
// `speaking/turn` until `isFinal`.

// AnswerResult — the server owns the queue, including retries
{ "itemId":"it2", "isCorrect": false, "correctAnswer":"نظام تشغيل",
  "wordId":"w_1",
  "explanation":"\"operating system\" means نظام تشغيل. Software that manages…",
  "requeued": true,            // comes back later in THIS session
  "attemptNumber": 1,
  "progress": { "nextItemId":"it3", "remaining": 7,
                "answered": 4, "total": 11, "attempted": true } }

// WritingEvaluation — the AI supplies the observations; `passed` is the
// backend's own decision (R2). See ADR-015.
{ "itemId":"it4", "passed": true, "usedWord": true, "meaningCorrect": true,
  "usageCorrect": true, "understandable": true, "grammarNote":"minor",
  "feedback":"Your sentence is correct. A more natural B1 version would be…",
  "suggestion":"The operating system manages the computer's hardware.",
  "requeued": false, "attemptNumber": 1,
  "progress": { "nextItemId":null, "remaining":0, "answered":1, "total":1 } }

// SpeakingTurn
{ "aiMessage":"Interesting — how does an operating system help you?",
  "isFinal": false,
  "wordsUsed":["operating system"],   // recognised across the whole transcript
  "remaining":["interface"] }         // this session's words only (ADR-050)

// SessionResult
{ "sessionId":"s_1", "skill":"READING",
  "comprehension": {"correct":4,"total":5},
  "words":[ {"wordId":"w_1","text":"operating system","meaning":"نظام تشغيل",
             "passed":true,
             "newStatus":"PASSED","nextSkill":"LISTENING",
             "nextEligibleAt":"2026-08-14T09:00:00Z","becameActive":false,
             "firstAttemptCorrect": true, "attemptsInSession": 1} ],
  "durationMs": 214000,
  "usedAiFallback": false }
```

Two flags mean **nothing was applied to this word**, and neither is a failure:

| Flag | Meaning |
|---|---|
| `superseded` | The word moved on elsewhere — finished at this skill in another session, or its schedule was brought forward. This session is no longer the one deciding (ADR-043). |
| `untouched` | This session never asked about the word: no item of its own was attempted, or — for Speaking — the learner never took a turn. Completing early no longer fails words nobody was shown (ADR-063). |

In both cases `newStatus` and `nextEligibleAt` describe the word as it stands,
`passed` is `false`, and the client shows neither a pass nor a fail.

### The in-session learning loop

A wrong answer is **recorded and repeated**, never discarded (demo review
§29–31, §47–48):

1. The item is pushed to the **end** of the queue and comes back before the
   session ends. `requeued: true` says so, and the UI tells the learner.
2. `explanation` is returned on **every** attempt, right or wrong, so an item is
   left understood rather than merely scored.
3. Only a **first-attempt** success passes the word for that skill
   (`firstAttemptCorrect`). A later success still counts as reinforcement, but
   the word is rescheduled — it is never dropped.
4. An item is retried at most **3 times** per session (configurable). The cap is
   what stops a struggling learner looping forever.
5. `POST /sessions/{id}/answer` and `/writing` reject anything that is not the
   current item with `ITEM_NOT_CURRENT` (409), so a retry after a dropped
   connection cannot corrupt the attempt counters.

Speaking has no queue: a word passes when the learner used it in a turn
substantial enough to judge (ADR-016).

## Weekly review

| Method | Path | Body → Response |
|---|---|---|
| POST | `/weekly-review/start` | — → `WeeklyReviewSession` |
| POST | `/weekly-review/practice/start` | — → `WeeklyReviewSession` (`isPractice: true`) |
| POST | `/weekly-review/{id}/answer` | `{itemId, answer}` → `ReviewAnswerResult` |
| POST | `/weekly-review/{id}/complete` | — → `WeeklyReviewResult` |

```jsonc
// WeeklyReviewSession
//
// Asks only about words that have had a week to settle and have not been
// recalled correctly yet, oldest first, at most 50 at a time (ADR-089,
// ADR-099). A word named right on the first attempt is retired for good; one
// that was missed ripens again a week after the challenge that missed it.
// `start` answers 409 REVIEW_NOT_READY while a learner's words are still
// ripening, and 409 REVIEW_NOTHING_TO_REVIEW when they have recalled
// everything — two situations, and only one of them has a date to wait for.
{ "id":"wr_1", "periodStart":"2026-08-05T00:00:00Z", "totalWords": 23,
  "wordsWaitingAfterThis": 13,     // ripe words behind the cap of fifty
  "isPractice": false,
  "queue":[ {"id":"ri1","wordId":"w_1","prompt":"operating system",
             "options":["نظام تشغيل","كرة","قاعدة بيانات","متصفح"]} ] }

// ReviewAnswerResult — a wrong answer returns the item to the END of the queue
{ "itemId":"ri1","isCorrect":false,"correctAnswer":"نظام تشغيل",
  "requeued": true, "remaining": 7,
  "nextItem": {"id":"ri2","wordId":"w_2","prompt":"interface","options":["…"]} }

// WeeklyReviewResult — measurement only, no pipeline change (R9)
{ "reviewId":"wr_1","totalWords":23,"firstPassCorrect":20,"weeklyScore":0.87,
  "totalAttempts":29, "isPractice": false }
```

**Practice (ADR-120).** `practice/start` goes over the words of the learner's
latest finished review (those not deleted since), and is answered and completed
through the same `answer` and `complete` calls. A practice records nothing:
no word is marked reviewed or retired, no exposure is counted, no completed
review is logged, and its score is returned for display only. Starting one
replaces an unfinished practice and never an unfinished review. `409
PRACTICE_NOTHING_TO_PRACTISE` before any review is finished.

Every word added in the period is included, whatever state it reached: the
question is what the learner remembers, not how far the word travelled. Wrong
answers requeue exactly as in a session, but `weeklyScore` counts **first
attempts only**.

Nothing here writes a skill status, a schedule, a level or a word state. The one
word-level write is the exposure counter, which is a priority signal and never a
limit (R8). `start` returns `409 NO_WORDS_IN_PERIOD` when the week was empty.

## Settings & configuration

| Method | Path | Body → Response |
|---|---|---|
| GET | `/settings` | → `UserSettings` |
| PATCH | `/settings/skill-level` | `{skill, level}` → `SkillLevel` *(user-selected only; `SKILL_NOT_LEVELLED` 400 for `SPELLING`)* |
| PATCH | `/settings/daily-target` | `{skill, target}` → `SkillLevel` |
| PATCH | `/settings/spelling-hints` | `{start: "AUTO" \| "DEFINITION_EN" \| "SIMPLIFIED_DEFINITION" \| "SYNONYM" \| "ARABIC_MEANING"}` → `{start, automaticStart}` — where every Spelling word's hints start; `LETTER_COUNT` is refused (ADR-115). `/me` carries the same object as `spellingHints`. Skill levels everywhere come in pipeline order (ADR-114). |
| PUT | `/me/interests` | `{interests:[…]}` → `UserProfile` |
| GET | `/config` | → `PublicConfig` |

```jsonc
// PublicConfig — client-visible tunables (never client-enforced business rules)
{ "skillIntervalDays":2, "minDailyTarget":5, "maxDailyTarget":15,
  "defaultDailyTarget":10, "cefrLevels":["A1","A1_PLUS", "…"],
  "skillsOrder":["READING","LISTENING","SPEAKING","SPELLING","WRITING"],
  "weeklyReviewPeriodDays":7 }
```

## Admin (`role = OWNER` only)

| Method | Path | Response |
|---|---|---|
| GET | `/admin/overview?days=` | `AdminOverview` |
| GET | `/admin/users?q=&days=&page=&pageSize=` | `{items:[AdminUserSummary], total, page, pageSize, hasMore}` |
| GET | `/admin/users/{id}` | `AdminUserDetail` |
| GET | `/admin/users/{id}/words?state=&q=&page=` | `{items:[AdminWord], total, hasMore}` |
| POST | `/admin/users/{id}/advance-schedule` | `{days}` (1–30, default 2) → `{days, wordsShifted, skillsDueNow}` — brings waiting skills forward for testing; moves scheduled dates only and is logged (ADR-037) |
| GET | `/admin/users/{id}/placement` | placement evidence + initial-vs-current levels |
| GET | `/admin/words/{wordId}` | one word's journey, for any learner |

`AdminUserDetail.levelChanges` carries the level history, with `changeType`
separating `USER_MANUAL_CHANGE` from `SYSTEM_VALIDATED_CHANGE` — the gap between
what a learner claims and what the system proved is the metric
(`MVP Core.txt` §60). `AdminUserDetail.activity` is the last fifty rows of the
activity log (ADR-025) — the raw trail behind every figure beside it.

> **`days` is a window, not a filter on one column.** 1 means today, and the
> window starts at midnight UTC so a count does not slide with the hour the
> Owner looks. Membership is answered from the activity log: "active in the
> last 5 days" means the learner did something, not that their `lastLoginAt`
> happens to fall inside it.
>
> Windowing scopes what it should — words added, sessions, per-skill outcomes —
> and deliberately not `pipelineCompletionRate`, which needs five skills and
> four two-day gaps to move and would read as a collapse over a short window.

> **The word views are the mirror of My Words.** The learner's own screen hides
> the pipeline states because they are internal machinery (Part 2 §42); these
> exist to inspect exactly those. `/admin/words/{wordId}` is read from the
> append-only word event log, so a word that failed Reading twice before passing
> shows all three events — its current row remembers only the ending.

> **Placement evidence is the audit trail for a level.** Each answer carries the
> item, its CEFR band, the domain it measured, the partial-credit score, and — for
> free-text and spoken items — the learner's own words. Multiple-choice items
> deliberately store no raw answer: the score already says which option was
> picked. `testVersion` stamps which item bank produced the result, because a
> result from an older bank is not comparable to a current one.

> **Authorization is server-side.** A caller whose role is not `OWNER` gets
> `403 FORBIDDEN` from every one of these, regardless of what the client renders.
> Hiding the UI is not the access control. There is no client-reachable path to
> becoming an `OWNER`; registration always creates a `USER`.

```jsonc
// AdminOverview — the metrics named in MVP Core §57 and §60
{
  "userCount": 12, "activeToday": 5, "activeThisWeek": 9,
  "wordsAddedTotal": 431,
  "averageWordsPerUserPerDay": 6.4,
  // Learners only, here and in every figure below: an Owner trying the product
  // is not part of how the audience is doing (ADR-052).
  "averageSessionsPerUser": 11.2, "averageSessionDurationMs": 214000,
  // The middle session. Shown in preference to the mean, which a handful of
  // sessions finished the next morning drag by two orders of magnitude.
  "medianSessionDurationMs": 16000,
  "pipelineCompletionRate": 0.34,        // added → Active
  "aiFallbackRate": 0.02,                // AI calls scored by the fallback
  "skillStats": [
    // `sessionsCompleted` counts sessions; `wordsPassed`/`wordsFailed` count
    // *attempts* on words. The three never sum — a session covers several
    // words — and `wordsDecided` is the count first-attempt accuracy belongs
    // over: distinct words this skill has passed or failed at least once
    // (ADR-052).
    { "skill":"READING", "sessionsCompleted":48,
      "wordsPassed":41, "wordsFailed":7,
      "firstAttemptPasses":38, "wordsDecided":44 }
  ],
  "levelDistributions": [                // SPELLING is absent — no CEFR band
    { "skill":"READING", "counts": { "A2": 3, "B1": 6, "B2": 3 } }
  ],
  "topInterests": [
    { "interest":"technology", "userCount":8, "isCustom":false },
    { "interest":"تصوير فوتوغرافي", "userCount":1, "isCustom":true }
  ]
}

// ── Feedback (ADR-053) ───────────────────────────────────────────────────────
//
// | POST  | `/feedback`                | `{body, appVersion?, platform?}` → `{id, sentAt}` |
// | GET   | `/admin/feedback?status=`  | → `{items, total, unread, page, hasMore}` |
// | PATCH | `/admin/feedback/{id}`     | `{handled}` → `{id, status, handledAt}` |
//
// Asymmetric on purpose: any signed-in learner may POST, and there is no call
// anywhere that returns feedback to a learner — their own included. Reading is
// Owner-only and refused with 403 for everyone else. `body` is capped at 4 000
// characters and stripped of control characters; it is stored as written and
// rendered as text, never interpreted.

// FeedbackMessage
{ "id":"f_1", "body":"The microphone does nothing.", "status":"NEW",
  "createdAt":"…", "handledAt":null,
  "appVersion":"1.2.0", "platform":"ios",
  "user": { "id":"u_2", "displayName":"سالم", "email":"a@b.c",
            "phoneCountryCode":"967", "phoneNumber":"770112233" } }

// AdminUserSummary
// `lastActiveAt` is the last thing the account *did*, from the activity log —
// not its last sign-in, which is one moment and misses a week of study after
// it (§34–§35, ADR-052). `sessionsCompleted` had been specified here from the
// start and simply never implemented, so the client read every row as zero.
{ "id":"u_2", "displayName":"Ahmed Bawazir", "email":"a@b.c", "role":"USER",
  "createdAt":"…", "lastActiveAt":"…",
  "wordsTotal":63, "wordsActive":12, "sessionsCompleted":31 }

// AdminUserDetail — the complete journey (MVP Core §58–59)
{
  "summary": { /* AdminUserSummary */ },
  "interests": ["technology","تصوير فوتوغرافي"],
  "levels": [ /* SkillLevel — spelling's are null */ ],
  "spelling": { "itemsAnswered":0, "correct":0, "supportMode":"LETTER_TILES" },
  "wordsLearning":31, "wordsActive":12, "wordsArchived":0,
  "wordsAddedToday":10, "wordsAddedThisWeek":63, "wordsAddedThisMonth":241,
  "skillStats": [ /* SkillStat, this user only */ ],
  "daily": [                              // one row per day, newest last
    { "date":"2026-08-14T00:00:00Z", "wordsAdded":10, "signedIn":true,
      "perSkillCompleted": {"READING":10,"LISTENING":0,"SPEAKING":0,
                            "WRITING":0,"SPELLING":0} }
  ],
  "mistakes": [                           // a wrong answer never deletes a word
    { "wordId":"w_4", "text":"software", "meaning":"برمجيات",
      "skill":"LISTENING", "attempts":2, "lastFailedAt":"…" }
  ],
  "masteredWords": ["operating system"],
  "signInCount": 17
}
```
