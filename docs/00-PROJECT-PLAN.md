# WordOS — Master Project Plan

> Source of truth for **what we set out to build and in which order**. It was built, and the
> finished behaviour is described in [`08-FINAL-SPECIFICATION.md`](08-FINAL-SPECIFICATION.md) —
> read that for what the application does today.
> Derived exclusively from `WordOS Decumentation/` (7 documents, read in full).
> Companion files: [`01-PHASES.md`](01-PHASES.md) · [`02-PROGRESS.md`](02-PROGRESS.md) · [`03-DECISIONS.md`](03-DECISIONS.md) · [`04-DATA-MODEL.md`](04-DATA-MODEL.md) · [`05-API-CONTRACT.md`](05-API-CONTRACT.md)

---

## 1. What WordOS is

A **Word Operating System**: vocabulary is not a list, it is a set of entities each with a
lifecycle, a state, five independent skill states, schedules, priority and history.

```
Add Word → Select Meaning → AI Analysis (CEFR level)
        → LEARNING PIPELINE
            Reading → (gap) → Listening → (gap) → Speaking → (gap) → Writing → (gap) → Spelling
        → MATURE → ACTIVE VOCABULARY (exposure-priority AI reuse)
        → ARCHIVE (never deleted, only on System-Validated level growth)
```

The MVP is not only an app — it is an **algorithm validation experiment**. Every meaningful
action must be logged and measurable (`MVP Core.txt` §53–68).

## 2. Non-negotiable architectural rules (from the docs)

| # | Rule | Source |
|---|------|--------|
| R1 | **No business rules in Flutter.** The client renders state (`Listening: Locked/Available`) it is *given*. It never computes eligibility, pass/fail, maturity or archiving. | Core Components §27, System Architecture §3.1 |
| R2 | **AI never mutates state.** AI returns *structured* output; the C# backend validates it and owns every state transition. | System Architecture §12, MVP Core §48 |
| R3 | **Nothing tunable is hard-coded.** Skill gap, daily targets, thresholds, evaluation windows, exposure limits live in a Configuration store. | User Flow §59, Core Components §21 |
| R4 | **Everything is persistent & server-side.** Device storage is never the source of truth. | Word Life Cycle §35 |
| R5 | **Failing one skill never resets the others.** Only the failed skill is rescheduled. | Word Life Cycle §34 |
| R6 | **User-Selected Level ≠ System-Validated Level.** Only the system-validated level may drive archiving and progression. | User Flow §8, Word Life Cycle §28–29 |
| R7 | **Answer options are shuffled by the backend**, never trusted in AI order. | MVP Core §21 |
| R8 | **Exposure Count is a priority signal, not a limit or a deletion mechanism.** | Word Life Cycle §26 |
| R9 | **Weekly Review never changes pipeline state.** Measurement only. | MVP Core §45 |

## 3. Target architecture

```
┌───────────────────────────┐
│  Flutter Mobile App       │  presentation only
└─────────────┬─────────────┘
              │ REST (JSON, JWT)
┌─────────────▼─────────────┐
│  ASP.NET Core (C#)        │  Auth · WordOS Algorithm · Pipeline · Scheduling
│  Modular Monolith         │  Priority Engine · Levels · Config · Analytics
└──────┬──────────────┬─────┘
       │              │ REST
┌──────▼──────┐  ┌────▼─────────────┐
│ PostgreSQL  │  │ Python AI Service│  prompts · structured output · evaluation
└─────────────┘  └────┬─────────────┘
                      │
                 ┌────▼─────┐
                 │ AI APIs  │
                 └──────────┘
```

**Repository layout**

```
bawazirapp/
├── WordOS Decumentation/   # original requirement documents (read-only)
├── docs/                   # planning, decisions, contracts, progress ledger
├── mobile/                 # Flutter app (built)
├── backend/                # ASP.NET Core 10 + PostgreSQL 17 (built), incl. tools/lexicon
└── ai-service/             # Python FastAPI, holds the Gemini key (built)
```

## 4. Build order and why

The client is built **first, against a mock that speaks the exact future backend contract**
(`docs/05-API-CONTRACT.md`). That gives us a runnable product to validate UX and the word
journey immediately, and makes the backend a *drop-in replacement* — one line switches
`MockWordOsApi` for `HttpWordOsApi`. The mock's simulated rules live in a clearly isolated
`mock_backend/` folder that is deleted when Phase 5 lands, so rule R1 is never violated in
production code.

See [`01-PHASES.md`](01-PHASES.md) for the phase breakdown and acceptance criteria, and
[`02-PROGRESS.md`](02-PROGRESS.md) for exactly where work stopped.

## 5. MVP configuration defaults

| Key | Default | Range |
|-----|---------|-------|
| `skill_interval_days` | 2 | configurable |
| `daily_target_words` | 10 | 5–15 |
| `per_skill_targets` | 10 each | 5–15 each |
| `min_level_evaluation_sessions` | 14 | configurable |
| `level_upgrade_threshold` | 85% | configurable |
| `level_downgrade_threshold` | 70% | configurable |
| `weekly_review_period_days` | 7 | configurable |
| `archive_level_gap_steps` | 4 | configurable (ADR-013) |
| `archive_min_exposure` | 3 | configurable (ADR-013) |
| `skills_order` | Reading → Listening → Speaking → Writing → Spelling | configurable (see ADR-001) |
| CEFR ladder | A1, A1+, A2, A2+, B1, B1+, B2, B2+, C1, C1+, C2 | fixed for MVP |

## 6. Explicitly out of MVP scope

Social features, leaderboards, advanced gamification, friends, public profiles, payments,
subscriptions, multi-language learning, teacher/classroom dashboards, marketplace, offline AI,
advanced notifications (`MVP Core.txt` §66).

## 7. Definition of success

Not "the app runs". Success is: the collected data can answer — do words stick? which skill
causes drop-off? is a 2-day gap right? is 10 words/day right? does exposure priority work?
are system-assigned levels realistic? (`MVP Core.txt` §67)
