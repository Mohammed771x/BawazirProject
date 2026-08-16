# WordOS — Development Phases

Each phase has a **goal**, a **deliverable list** and **acceptance criteria**. A phase is only
"done" when its acceptance criteria are verifiably met. Status is tracked in
[`02-PROGRESS.md`](02-PROGRESS.md) — that file, not this one, is the live ledger.

---

## Phase 0 — Foundations & Planning
**Goal:** shared understanding, repository skeleton, continuity for future sessions.

- Read all 7 requirement documents end to end.
- `docs/` planning set: plan, phases, progress ledger, decisions (ADRs), data model, API contract.
- Root `CLAUDE.md` so any new session bootstraps in one read.
- Repo layout: `mobile/`, (later) `backend/`, `ai-service/`.

**Accept:** a new engineer/agent can read `CLAUDE.md` + `docs/02-PROGRESS.md` and continue
without re-reading the Arabic source documents.

---

## Phase 1 — Flutter Foundation
**Goal:** a running, beautiful, empty-but-navigable app with the full contract layer.

- Flutter project, dependencies (Riverpod, go_router, Dio, secure storage, flutter_tts, intl).
- **Design system**: color tokens, typography scale, spacing, radii, elevation, light + dark
  Material 3 themes, reusable components (buttons, cards, chips, progress, empty/error states).
- **Localization**: English + Arabic, full RTL support (learner-facing meanings are Arabic).
- **Domain models** mirroring the backend contract exactly (`docs/04-DATA-MODEL.md`).
- **`WordOsApi` abstraction** + `HttpWordOsApi` (Dio, ready for Phase 5) +
  `MockWordOsApi` backed by `mock_backend/` (simulated pipeline engine, deleted in Phase 7).
- Routing with auth/onboarding guards, error + loading conventions, app shell.

**Accept:** `flutter analyze` clean; app boots to Login; theme switches light/dark; RTL works.

---

## Phase 2 — Identity & Onboarding
**Goal:** a user can get from install to a configured profile.

- Register / Login (email + password, JWT), session persistence via secure storage.
- Interests selection (preset grid + "Other" free entry).
- Placement Test: mixed Reading / Listening / Grammar / Vocabulary / Writing items.
- Result screen showing **independent per-skill CEFR levels**.
- Profile creation, onboarding completion guard in the router.

**Accept:** cold start → register → interests → placement → per-skill levels → Skills Hub;
restarting the app resumes at the right step; levels are server-issued, never client-computed.

---

## Phase 3 — Core Product Surfaces
**Goal:** the daily-use shell around the pipeline.

- **Skills Hub**: five skill cards with server-provided availability/counts + Weekly Review slot.
- **Add Word**: type → candidate meanings → user selects intended meaning → AI analysis →
  saved with CEFR level, one word at a time.
- **Vocabulary**: Learning / Active / Archive tabs, per-word skill state + schedule detail.
- **Settings**: per-skill level (user-selected, clearly separated from system-validated),
  per-skill daily targets (5–15), interests, theme/language, sign out.

**Accept:** adding a word puts it in the pipeline with `Reading` current and the other four
`Pending`; the hub reflects backend eligibility only; settings changes persist and are logged.

---

## Phase 4 — The Five Skills + Weekly Review
**Goal:** the complete word journey is playable end to end.

- **Reading**: AI passage with highlighted targets → comprehension MCQs → target-word-in-context
  MCQs → pass/fail per word.
- **Listening**: TTS playback (normal + slow support), no visible text during test → comprehension
  → target-word question → post-test transcript review with highlights.
- **Speaking**: guided AI conversation, target words injected, structured evaluation
  (used / meaning / usage / pronunciation / understandable).
- **Writing**: prompt → user sentence → AI evaluation + constructive feedback (small grammar
  slips do not fail a correct usage).
- **Spelling**: level-adaptive clue (Arabic meaning / simple definition / synonym / English
  definition), letter-tiles for lower levels, free typing for higher, optional hint.
- **Weekly Review**: queue loop over words added in the period; wrong answers re-enter the queue
  until cleared; first-pass accuracy recorded; **pipeline state untouched**.
- Session result screens; failure reschedules only the failed skill.

**Accept:** a word can traverse Reading→…→Spelling→Active in the mock (with time travel for the
gaps); failing one skill leaves the others `Passed`; weekly review changes no skill status.

---

## Phase 5 — C# Backend (ASP.NET Core + PostgreSQL)
**Goal:** the real brain; Flutter's mock becomes unnecessary.

Modules: `Auth`, `Users`, `Words`, `Skills`, `Learning` (pipeline + state machine),
`Scheduling` (eligibility/due dates), `Priority` (session assembly), `Levels`
(system-validated progression), `Review` (weekly), `Configuration`, `Analytics`, `Ai` (client
for the Python service). EF Core migrations, seed configuration, JWT auth, role for Owner/Admin.

**Accept:** full API contract implemented; pipeline unit tests (gap, eligibility, partial
failure, maturity, archive eligibility) green; every state transition emits an analytics event.

---

## Phase 6 — Python AI Service (FastAPI)
**Goal:** all prompt/AI concerns isolated behind a stable REST contract.

- Endpoints: word analysis, placement generation/scoring, reading/listening content, writing
  evaluation, speaking conversation + evaluation, spelling clue generation.
- Versioned prompt templates per skill, structured (schema-validated) outputs, retries,
  provider abstraction so the model vendor can change without touching WordOS logic.
- Logs prompt version, model, latency, tokens, parse success.

**Accept:** every endpoint returns schema-valid JSON or a typed error; the backend rejects
malformed AI output instead of trusting it.

---

## Phase 7 — Integration
**Goal:** the real system, end to end.

- Swap `MockWordOsApi` → `HttpWordOsApi`, environment config, error/retry/offline UX.
- Speech capture for Speaking, TTS tuning for Listening.
- Delete `mobile/lib/mock_backend/`.

**Accept:** the Phase 4 acceptance run passes against the real backend + AI service.

---

## Phase 8 — Analytics & Owner Dashboard
**Goal:** answer "is the algorithm working?".

- Event store + aggregation: global metrics, per-skill pass/fail, first-attempt accuracy,
  pipeline completion rate, failure distribution, level changes (manual vs system-validated),
  weekly scores, AI health.
- Owner-only dashboard, per-user drill-down day by day.

**Accept:** the questions in `MVP Core.txt` §67 can each be answered from the dashboard.

---

## Phase 9 — Hardening & Pilot
Tests (unit/widget/integration), structured logging, error taxonomy, rate limits, backups,
seed/demo data, store builds, pilot with 10–20 real users, then an algorithm-tuning pass driven
by the collected data.
