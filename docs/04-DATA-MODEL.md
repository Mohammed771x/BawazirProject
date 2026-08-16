# WordOS — Data Model (MVP)

Target store: **PostgreSQL**. Naming below is logical; EF Core entities in Phase 5 use the same
names. The Dart models in `mobile/lib/core/models/` mirror the API projection of these entities.

---

## Enumerations

```
CefrLevel      : A1, A1_PLUS, A2, A2_PLUS, B1, B1_PLUS, B2, B2_PLUS, C1, C1_PLUS, C2
SkillType      : READING, LISTENING, SPEAKING, WRITING, SPELLING
SkillStatus    : PENDING, AVAILABLE, PASSED, FAILED
WordState      : LEARNING, MATURE, ACTIVE, ARCHIVED
LevelChangeType: PLACEMENT, USER_MANUAL_CHANGE, SYSTEM_VALIDATED_CHANGE
SessionKind    : READING, LISTENING, SPEAKING, WRITING, SPELLING, WEEKLY_REVIEW, PLACEMENT
```

`WordState` is the coarse lifecycle. Fine-grained progress lives in the five `word_skill_state`
rows — the doc-level states such as `READING_PENDING` are a *projection* of
(`word.state`, `word.current_skill`, `word_skill_state.status`), never a separate stored field.

---

## Core tables

### `users`
`id`, `email` (unique), `password_hash`, `display_name`, `role` (`USER` | `OWNER`),
`onboarding_stage` (`INTERESTS` | `PLACEMENT` | `COMPLETE`), `created_at`, `last_login_at`,
`timezone`.

### `user_skill_levels` — one row per (user, skill)
`user_id`, `skill`, `user_selected_level` (**nullable**), `system_assessed_level`
(**nullable**), `confidence` (0–1), `evaluation_sessions_count`, `rolling_accuracy`,
`daily_target_words` (5–15), `spelling_support_mode` (nullable), `updated_at`.

> R6: `system_assessed_level` drives archiving and progression; `user_selected_level` drives
> only the difficulty of generated content.
>
> Both level columns are **NULL for `SPELLING`** — it is measured but carries no CEFR band
> (ADR-008). Its row holds `rolling_accuracy` and `spelling_support_mode`
> (`LETTER_TILES` | `FREE_TYPING`) instead. Spelling *content* difficulty is derived from the
> learner's Reading level.

### `user_interests`
`user_id`, `interest` (slug or free text), `is_custom`, `created_at`.

### `lexicon_entries` — the vocabulary source (ADR-012)

Global, not per-user. Built offline by joining three sources on the WordNet
synset id, then loaded into PostgreSQL and served through our own API:

`sense_id` (PK, WordNet synset id), `text`, `lemma`, `part_of_speech`,
`definition_en` (Open English WordNet), `meaning_ar` (Arabic WordNet),
`cefr_level` (CEFR-J / Octanove, nullable), `frequency_rank`, `source_flags`,
`updated_at`.

```
English word ─┐
              ├─ synset (sense_id) ─┬─ definition_en   (Open English WordNet)
part of speech┘                     ├─ meaning_ar      (Arabic WordNet)
                                    └─ cefr_level      (CEFR-J / Octanove)
```

Indexes: `text text_pattern_ops` for prefix autocomplete (`bo` → `book`),
`(text, part_of_speech)`, and a trigram index on `text` for spelling
suggestions.

> **`sense_id` is the identity.** `book` alone is not: `book = كتاب` and
> `book = يحجز` are different synsets and therefore different vocabulary items.
> `source_flags` records which source supplied each field, so an entry whose
> Arabic gloss came from the machine-translated portion of Arabic WordNet stays
> auditable and replaceable.
>
> Rows are never written from a client request. `POST /words` treats the body as
> a **lookup key** and copies the stored row.

### `words` — one row per (user, word, intended meaning)
`id`, `user_id`, `sense_id` (→ `lexicon_entries`), `text`, `meaning` (Arabic, user-chosen
sense), `definition_en`, `part_of_speech`, `cefr_level`, `state`, `current_skill` (nullable),
`added_at`, `matured_at`, `activated_at`, `archived_at`, `exposure_count`, `last_reviewed_at`,
`next_eligible_at`, `updated_at`.

Unique constraint: `(user_id, sense_id)` — that is the duplicate rule in the
schema rather than only in code.

*A word is only meaningful together with its intended meaning — `book = كتاب` and `book = يحجز`
are two independent rows with independent journeys.*

### `word_skill_states` — exactly five rows per word
`word_id`, `skill`, `status`, `available_at` (nullable), `attempts`, `passed_at`, `failed_at`,
`last_attempt_at`.

Eligibility for a session = `state = LEARNING` **and** `skill = word.current_skill` **and**
`status ∈ {AVAILABLE, FAILED}` **and** `available_at ≤ now`.

### `word_exposures` — why `words.exposure_count` cannot drift
`id`, `word_id`, `source` (`AI_CONTENT_REUSE`, `WEEKLY_REVIEW`), `source_id` (the session or
review that caused it), `occurred_at`.

Unique: `(word_id, source, source_id)`. Every increment of `exposure_count` writes exactly one
row here, so one generating event credits a word **once** however many times it appears in the
content or across the turns of a conversation. A later session is a new encounter and counts
again. No endpoint accepts an exposure value; the server derives it by reading content it
generated itself (ADR-018, rule R8).

### `word_events` — append-only word history
`id`, `word_id`, `type` (`ADDED`, `SKILL_STARTED`, `SKILL_PASSED`, `SKILL_FAILED`,
`BECAME_MATURE`, `ENTERED_ACTIVE`, `EXPOSURE_INCREMENTED`, `ARCHIVED`), `skill`, `payload` (jsonb),
`created_at`.

---

## Session tables

### `sessions`
`id`, `user_id`, `kind`, `skill_level_used`, `started_at`, `completed_at`, `word_count`,
`passed_count`, `failed_count`, `duration_ms`, `ai_content_id`.

### `session_items` — one per question / task
`id`, `session_id`, `word_id` (nullable for comprehension items), `item_type`
(`COMPREHENSION`, `TARGET_WORD`, `WRITING_TASK`, `SPEAKING_TURN`, `SPELLING_TASK`,
`REVIEW_ITEM`), `prompt`, `options` (jsonb, **already shuffled by the backend**),
`correct_answer`, `user_answer`, `is_correct`, `attempt_number`, `answered_at`, `time_ms`.

### `ai_contents`
`id`, `session_id`, `skill`, `prompt_version`, `model`, `request` (jsonb), `response` (jsonb),
`latency_ms`, `tokens`, `parse_ok`, `created_at`. Historical rows are never mutated — that is
how prompt versions are compared later.

### `ai_evaluations`
`id`, `session_item_id`, `word_id`, `used_word`, `meaning_correct`, `usage_correct`,
`understandable`, `pronunciation_acceptable`, `grammar_note`, `result` (`PASS`/`FAIL`),
`feedback`, `raw` (jsonb), `created_at`.

*The backend reads these fields and applies its own rule to set skill status — it never takes an
AI "passed" verdict as the state change itself (R2).*

---

## Review, levels, configuration, analytics

### `weekly_reviews`
`id`, `user_id`, `period_start`, `period_end`, `total_words`, `first_pass_correct`,
`weekly_score`, `completed_at`.

### `weekly_review_items`
`review_id`, `word_id`, `attempt_number`, `is_correct`, `answered_at`.
Wrong answers re-enter the queue; only `attempt_number = 1` feeds `weekly_score` (R9).

### `level_changes`
`id`, `user_id`, `skill`, `previous_level`, `new_level`, `change_type`, `reason`,
`sessions_considered`, `accuracy`, `created_at`.

Written on **every** level movement, manual or system-validated. Only
`SYSTEM_VALIDATED_CHANGE` may drive archiving (R6); a manual change is recorded
and otherwise inert. See ADR-013 for the promotion, demotion and archiving
policy, and `mobile/test/level_progression_test.dart` for the pinned rules.

### `configurations`
`key`, `value` (jsonb), `scope` (`GLOBAL` | `USER`), `user_id` (nullable), `updated_at`,
`updated_by`. Seeded with the defaults in `00-PROJECT-PLAN.md` §5 (R3).

### `analytics_events`
`id`, `user_id`, `type`, `payload` (jsonb), `session_id`, `occurred_at`.
Emitted for: login/logout, word added, meaning selected, skill started/completed, answer
submitted, word passed/failed, word active/archived, level changed, setting changed, weekly
review started/completed, AI request/response/parse failure, errors.

---

## Relationship overview

```
users ─┬─ user_skill_levels (5)
       ├─ user_interests (n)
       ├─ words (n) ─┬─ word_skill_states (5)
       │             └─ word_events (n)
       ├─ sessions (n) ─┬─ session_items (n) ── ai_evaluations (0..1)
       │                └─ ai_contents (0..1)
       ├─ weekly_reviews (n) ── weekly_review_items (n)
       ├─ level_changes (n)
       └─ analytics_events (n)
```

## Invariants

1. Every `words` row has exactly five `word_skill_states`.
2. `word.state = ACTIVE` ⟺ all five statuses are `PASSED` (and `current_skill IS NULL`).
3. A `PASSED` skill status is never reverted by a failure in another skill (R5).
4. `next_eligible_at = passed_at + skill_interval_days` at each transition (R3 value).
5. Words are never deleted; archiving is a state change only (R8 / lifecycle §31).
7. A word may be archived only from `ACTIVE`, only when the **system-validated**
   level exceeds the word's level by `archive_level_gap_steps`, and only when
   `exposure_count >= archive_min_exposure` (ADR-013).
6. Weekly review writes only to `weekly_review*` and `analytics_events` (R9).
