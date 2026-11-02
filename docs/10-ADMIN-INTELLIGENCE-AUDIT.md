# WordOS Admin — Audit and Requirements Map

> The audit the second admin brief asks for before any change (§1, §18 phase 1),
> done 2026-11-03 against the code and the local `wordos_dev` database, and the
> map of what was reused, what was added (ADR-132) and what remains.
> Earlier work: ADR-125. Decisions taken here: ADR-132.

## 1. What already existed

**Code.** `admin-web/` (React + Vite, Arabic RTL, hand-built SVG charts, URL
filters, mock mode) served at `/admin` by the API. `backend/src/WordOs.Api/Admin/`
computes everything in memory per request from the real tables (`IntelDataset`),
with definitions in `IntelMetrics`, cohorts in `Segments`, the timeline in
`IntelTimeline`, users and attention rules in `IntelUsers`, and the investigator
in `IntelInquiry`. Roles Owner and Analyst (contact details masked), every
handler re-checks, audit trail in `admin_audit_events`. The AI service's
`/ai/admin/investigate` receives aggregates only.

**Database** (24 tables, read from the live schema, not assumed):

| Need | Where it already lives |
|---|---|
| Users, accounts, levels | `users`, `user_skill_levels`, `user_interests`, `placement_sessions/answers`, `level_changes` |
| Words and their stages | `words`, `word_skill_states`, `word_events` (Added, SkillStarted/Passed/Failed, BecameMature, EnteredActive, ExposureIncremented, Archived, Deleted, MeaningChanged), `word_exposures` |
| Sessions, questions, attempts | `skill_sessions` (skill, `LevelUsed`, content, prompt version, AI fallback), `session_items` (attempts, first-attempt result), `analytics_events: answer_submitted` (each attempt, elapsed ms) |
| AI judgements and calls | `analytics_events: writing_evaluated, speaking_turn, speaking_word_evaluated, ai_call` |
| Reviews | `weekly_reviews`, `weekly_review_items`, `review_answered` |
| Screen behaviour | `analytics_events` from the phone: app open/background, screens, translation, audio, hints, exits, notification opened, API errors |
| Feedback and notes | `feedback_messages`, `admin_notes` |
| Investigations, audit | `admin_inquiries`, `admin_audit_events` |

Volumes at audit time: 51 learners; 456 words added; 182 `ai_call`; phone
events only from a development build (17 translation opens, one each of
background/exit/screen) — the pages already say client-event charts are empty
until a tracking build is installed.

## 2. Requirements map

Status: **Done** (exists and works) · **Partial** · **Added** (ADR-132) ·
**Gap** (not built) · **N/A** (cannot be built honestly, reason given).

### Pages (brief §3)

| # | Page | Before | Action |
|---|---|---|---|
| 1 | Product Overview | Done — KPIs with previous period, engagement, funnel, lifecycle, skills, signals; today/7/30/90/custom | Reused |
| 2 | Users | Done — search, sort, segments, attention groups, cohort compare | Reused |
| 3 | User 360 | Done — periods, 90-day activity strip, skills, word matrix, timeline, levels, feedback | Reused. **Added:** clicking a day opens it — app opens and time, lessons by skill, results, words added, the day's events |
| 4 | Learning & Words | Done — lifecycle, overdue, failures, most-failed words, level flows | Reused |
| 5 | Skill Analytics | Done — per skill: sessions, accuracy, pass/fail, retry, abandonment, durations, by level, content | Reused |
| 6 | Time & UX | Done — App/learning time, per question, to first answer, AI latency p50/p95, friction | Reused |
| 7 | Retention & Review | Done — D1/D7/D30, recall curve, cohorts, recovery, weekly review | Reused |
| 8 | Notifications | Partial — only an investigator section | **Added** page: planned → opened → started → completed, by kind/hour/day, users who open and don't learn |
| 9 | Issues & Feedback | Done — Problems + Feedback with internal notes | Reused |
| 10 | AI Product Intelligence | Partial — one-shot investigations, saved whole | **Added** threads, discussion mode, follow-up dates, re-run, prior decisions as context, "turn into a decision" |
| 11 | Decision Log | Gap | **Added** |
| 12 | Experiments & Outcomes | Gap | **Added** (one log with changes) |
| 13 | Settings | Partial — config, thresholds, tracking health, roles, audit | **Added** metric and event dictionary with brief coverage |

### Cross-cutting requirements

| Brief § | Requirement | Status |
|---|---|---|
| §4 | User level vs content level vs level at event time | Done — `user_skill_levels` (selected / assessed), `skill_sessions.LevelUsed`, `level_changes` |
| §4 | Comparisons with counts shown | Done — cohort compare; evaluations show `n` on each side |
| §4 | Configurable regularity definitions | Partial — `heavy/light/frequent/rare/active-days:N` segments exist; thresholds are configuration |
| §5 | App / learning / skill / question / waiting time kept apart | Done (ADR-125); no double counting — each minute belongs to one session |
| §6 | Per-question vs whole-activity results | Done — per-attempt and per-skill results are recorded separately. The 80 % comprehension threshold was dropped by the product owner (2026-11-03) |
| §7 | Word journey timeline, delays per skill | Done — timeline, overdue by stage, days to Active (now a dictionary metric) |
| §8 | Content stored once with version | Each passage is generated for its own session and stored with it and its prompt version — no shared text to deduplicate. **Added:** each skill page compares results by content (prompt) version and model. Speaking keeps judgements, not raw audio |
| §9.1 | Agent knows sources and definitions | Added — the dictionary is the catalogue; sections fix what each investigation reads |
| §9.2 | Query only when needed | Added — `discussion` section reads nothing |
| §9.3 | Scoped, permissioned, backend-only queries | Done — fixed sections over `IntelScope`, no SQL from the model, aggregates only |
| §9.4 | Answer shape | Done (ADR-125 §7) |
| §9.5 | Keeps context | Added — `parentId` inherits section and filters; last four turns to the model |
| §10 | Saved conversations and follow-ups | Added — thread, follow-up date, re-run. Nothing watches continuously, and the page says so |
| §11 | Decision fields, approval, history, review dates, prior decisions before proposing | Added |
| §12 | Evaluate against baseline, guardrails, confounders, need for comparison | Added — arithmetic and cautions; verdict written by a person |
| §13 | Change log | Added |
| §14 | Event tracking | See Settings → dictionary → "coverage". `notification_sent` is planned, not sent (ADR-076). `question_presented` derives from elapsed time |
| §15 | Metric dictionary | Added |
| §16 | Arabic RTL, states, responsive | Done, kept |
| §17 | Server-side permissions, audit, AI limits | Done; new routes follow the same pattern; approval is Owner-only on the server |

## 3. What is still open

Closed on 2026-11-03: the User 360 day view, comparison by content version,
and review-date measurement (taken by the system the first time the decision
log is read after the date, kept, and flagged in the sidebar). The 80 %
threshold was dropped by the product owner.

One item remains, and it is an app change, so it was deliberately not made
here (the admin work touches no app code):

1. **The phone sending a reminder's kind** on `notification_opened`. Until then
   the kind is attributed from the last planned slot, and the page says so.

## 4. Verification

- API: 530 tests green, including `AdminProductTests` (approval rights, frozen
  evaluation, history, unknown metric refused, comparison group and overlaps,
  threads, discussion reads nothing, reminder plan recorded once, baseline
  clipping).
- AI service: insight tests green on `admin-insight-v2`.
- admin-web: `npm run build` clean; checked in the browser against the local
  API with the real model — a follow-up question was answered in its thread's
  context, turned into a decision, approved and implemented, and the
  evaluation showed its samples and cautions.
