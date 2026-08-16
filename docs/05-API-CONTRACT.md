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
| POST | `/auth/register` | `{email, password, displayName}` → `AuthResponse` |
| POST | `/auth/login` | `{email, password}` → `AuthResponse` |
| POST | `/auth/refresh` | `{refreshToken}` → `AuthResponse` (rotates the token) |
| POST | `/auth/logout` | — → `204` |
| GET | `/me` | — → `UserProfile` |

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
  "progress": { "answered": 4, "estimatedTotal": 20,   // estimate: adaptive
                "currentSkill":"READING", "skillIndex":0, "skillCount":5 }
}

// PlacementResult — levels are computed server-side (ADR-007)
{
  "levels": [ /* SkillLevel — `confidence` is meaningful here */ ],
  "spelling": { "itemsAnswered": 4, "correct": 3,
                "supportMode": "LETTER_TILES" },       // LETTER_TILES | FREE_TYPING
  "summary": "…short human text…"
}
```

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
  "weeklyReview": { "available": true, "wordCount": 23,
                    "periodStart":"2026-08-05T00:00:00Z", "nextAvailableAt": null },
  "vocabulary": { "learning": 31, "active": 12, "archived": 0 }
}
```

> The client renders `availability` verbatim. It never computes it (R1).

## Words

| Method | Path | Body → Response |
|---|---|---|
| GET | `/words/lookup?q=bo` | → `[WordCandidate]` (prefix search, or spelling suggestions) |
| POST | `/words` | `{senseId, text?, meaning?}` → `Word` |
| GET | `/words?state=LEARNING\|ACTIVE\|ARCHIVED&page=` | → `{items:[Word], total}` |
| GET | `/words/{id}` | → `WordDetail` |

> **Lookup is a prefix search and never invents an entry.** `bo` returns every
> sense whose word starts with those letters, each row carrying the word, its
> CEFR level and the Arabic meaning of *that sense*. If nothing matches, the
> response contains only candidates with `isSpellingSuggestion: true`, or is
> empty. There is no "add it anyway" result, and the client offers no way to
> type a meaning (ADR-012).
>
> **`POST /words` re-resolves.** The body is treated as a **lookup key**; the
> row that gets stored is the lexicon's, so a forged level, definition or part
> of speech is discarded. Errors: `INVALID_WORD` (400) for a missing word or
> meaning; `WORD_NOT_FOUND` (404) when the sense is not in the lexicon;
> `WORD_ALREADY_ADDED` (409) when this learner already has that sense.
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
  "partOfSpeech":"noun", "cefrLevel":"B1", "state":"LEARNING",
  "currentSkill":"READING", "addedAt":"2026-08-12T09:10:00Z",
  "nextEligibleAt":"2026-08-12T09:10:00Z", "exposureCount":0,
  "skills":[ {"skill":"READING","status":"AVAILABLE","availableAt":"…","attempts":0},
             {"skill":"LISTENING","status":"PENDING","availableAt":null,"attempts":0} /* … */ ]
}

// WordDetail = Word + { "events": [{"type":"SKILL_PASSED","skill":"READING","createdAt":"…"}] }
```

## Skill sessions

| Method | Path | Body → Response |
|---|---|---|
| POST | `/sessions/{skill}/start` | — → `SkillSession` |
| GET | `/sessions/{id}` | — → `SkillSession` (resume where the learner was) |

| POST | `/sessions/{id}/answer` | `{itemId, answer}` → `AnswerResult` |
| POST | `/sessions/{id}/writing` | `{itemId, answer}` → `WritingEvaluation` |
| POST | `/sessions/{id}/speaking/turn` | `{transcript}` → `SpeakingTurn` |
| POST | `/sessions/{id}/complete` | — → `SessionResult` |
| POST | `/sessions/{id}/abandon` | — → `204` |

`GET /sessions/{id}` exists because the client holds no session state (R1):
after a crash or a backgrounded app it asks the server where it was. The stored
content is replayed, never regenerated — a second generation would produce a
different passage.

`start` returns `409 NO_WORDS_DUE` when the spaced gap has not elapsed. Starting
a session **abandons** any unfinished session for the same skill, so words are
never consumed twice. `abandon` deletes the session and leaves its words due.

Rate limits: `start`, `writing` and `speaking/turn` cost Gemini tokens and carry
the tight budget; `answer` and `complete` do not and are covered by the global
per-user limiter.

```jsonc
// SkillSession — one shape, skill-specific parts nullable
{
  "id":"s_1", "skill":"READING", "levelUsed":"B1",
  "content": {                                  // READING / LISTENING only
     "text":"Ahmed was studying computer science…",
     "revealTextAfterTest": false               // true for LISTENING
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
     "options":["نظام تشغيل","لوحة مفاتيح","شبكة الإنترنت","برنامج رسم"]},

    // LISTENING target word: the same three sentences, spoken and never shown.
    // `context` is null; the client must not render `audioText` as text.
    {"id":"it7","type":"TARGET_WORD","wordId":"w_1",
     "context": null,
     "audioText":"He had never used Linux before. The operating system is…",
     "prompt":"What does \"operating system\" mean here?",
     "options":["…","…","…","…"]},

    {"id":"it8","type":"SPELLING_TASK","wordId":"w_1",
     "clue":"software that manages a computer's resources",
     "clueKind":"DEFINITION_EN",                 // ARABIC_MEANING | DEFINITION_EN | SYNONYM
     "letters":["o","p","e","r","a","t","i","n","g"],   // lower levels only, shuffled
     "inputMode":"LETTER_TILES",                        // LETTER_TILES | FREE_TYPING
     "hint":"op______  (15 letters)"},                  // always offered
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
  "remaining":["interface"] }

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
| POST | `/weekly-review/{id}/answer` | `{itemId, answer}` → `ReviewAnswerResult` |
| POST | `/weekly-review/{id}/complete` | — → `WeeklyReviewResult` |

```jsonc
// WeeklyReviewSession
{ "id":"wr_1", "periodStart":"2026-08-05T00:00:00Z", "totalWords": 23,
  "queue":[ {"id":"ri1","wordId":"w_1","prompt":"operating system",
             "options":["نظام تشغيل","كرة","قاعدة بيانات","متصفح"]} ] }

// ReviewAnswerResult — a wrong answer returns the item to the END of the queue
{ "itemId":"ri1","isCorrect":false,"correctAnswer":"نظام تشغيل",
  "requeued": true, "remaining": 7,
  "nextItem": {"id":"ri2","wordId":"w_2","prompt":"interface","options":["…"]} }

// WeeklyReviewResult — measurement only, no pipeline change (R9)
{ "reviewId":"wr_1","totalWords":23,"firstPassCorrect":20,"weeklyScore":0.87,
  "totalAttempts":29 }
```

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
| PUT | `/me/interests` | `{interests:[…]}` → `UserProfile` |
| GET | `/config` | → `PublicConfig` |

```jsonc
// PublicConfig — client-visible tunables (never client-enforced business rules)
{ "skillIntervalDays":2, "minDailyTarget":5, "maxDailyTarget":15,
  "defaultDailyTarget":10, "cefrLevels":["A1","A1_PLUS", "…"],
  "skillsOrder":["READING","LISTENING","SPEAKING","WRITING","SPELLING"],
  "weeklyReviewPeriodDays":7 }
```

## Admin (`role = OWNER` only)

| Method | Path | Response |
|---|---|---|
| GET | `/admin/overview` | `AdminOverview` |
| GET | `/admin/users` | `[AdminUserSummary]` |
| GET | `/admin/users/{id}` | `AdminUserDetail` |

`AdminUserDetail.levelChanges` carries the level history, with `changeType`
separating `USER_MANUAL_CHANGE` from `SYSTEM_VALIDATED_CHANGE` — the gap between
what a learner claims and what the system proved is the metric
(`MVP Core.txt` §60).

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
  "averageSessionsPerUser": 11.2, "averageSessionDurationMs": 214000,
  "pipelineCompletionRate": 0.34,        // added → Active
  "aiFallbackRate": 0.02,                // AI calls scored by the fallback
  "skillStats": [
    { "skill":"READING", "sessionsCompleted":48,
      "wordsPassed":41, "wordsFailed":7, "firstAttemptPasses":38 }
  ],
  "levelDistributions": [                // SPELLING is absent — no CEFR band
    { "skill":"READING", "counts": { "A2": 3, "B1": 6, "B2": 3 } }
  ],
  "topInterests": [
    { "interest":"technology", "userCount":8, "isCustom":false },
    { "interest":"تصوير فوتوغرافي", "userCount":1, "isCustom":true }
  ]
}

// AdminUserSummary
{ "id":"u_2", "displayName":"Ahmed Bawazir", "email":"a@b.c", "role":"USER",
  "createdAt":"…", "lastActiveAt":"…",
  "wordsTotal":63, "wordsActive":12, "sessionsCompleted":31 }

// AdminUserDetail — the complete journey (MVP Core §58–59)
{
  "summary": { /* AdminUserSummary */ },
  "interests": ["technology","تصوير فوتوغرافي"],
  "levels": [ /* SkillLevel — spelling's are null */ ],
  "spelling": { "itemsAnswered":4, "correct":3, "supportMode":"LETTER_TILES" },
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
