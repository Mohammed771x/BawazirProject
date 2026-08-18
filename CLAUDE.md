# WordOS — Repository Guide

> **New session? Read this file, then [`docs/02-PROGRESS.md`](docs/02-PROGRESS.md).**
> Together they tell you what this is, what is built, and exactly what to do next.
> For how the finished application actually behaves — every section, flow and rule —
> read [`docs/08-FINAL-SPECIFICATION.md`](docs/08-FINAL-SPECIFICATION.md).
> You do **not** need to re-read the seven Arabic/English requirement documents to continue —
> they are distilled into `docs/`.

## What this is

**WordOS — a "Word Operating System".** A vocabulary app where every word is an entity with a
lifecycle: added with an explicit intended meaning → validated across five skills with spaced
gaps → Mature → Active Vocabulary (reused by AI, exposure-prioritised) → Archive (never deleted).

```
Reading → 2d → Listening → 2d → Speaking → 2d → Writing → 2d → Spelling → Active → Archive
```

The MVP is equally an **algorithm-validation experiment**: everything meaningful must be
measurable.

## Repository layout

| Path | What |
|------|------|
| `WordOS Decumentation/` | Original requirement documents. **Read-only source of truth.** |
| `docs/` | Distilled plan, phases, progress ledger, ADRs, data model, API contract, placement algorithm. |
| `mobile/` | Flutter app (built). |
| `backend/` | ASP.NET Core 10 + PostgreSQL 17 — built, with the lexicon importer in `tools/`. |
| `ai-service/` | Python FastAPI layer holding the Gemini key — built. |

## The nine rules that must never be broken

1. **R1 — No business logic in Flutter.** The client renders server-provided state.
2. **R2 — AI never mutates state.** It returns structured output; the backend decides.
3. **R3 — Nothing tunable is hard-coded.** Gaps, targets, thresholds live in configuration.
4. **R4 — Server-side persistence only.** Device storage is never the source of truth.
5. **R5 — Failing one skill never resets the others.**
6. **R6 — User-selected level ≠ system-validated level.** Only the latter drives progression/archiving.
7. **R7 — The backend shuffles answer options.**
8. **R8 — Exposure count is a priority signal, never a limit or a delete trigger.**
9. **R9 — Weekly Review measures only; it never changes pipeline state.**

Full detail and sources: [`docs/00-PROJECT-PLAN.md`](docs/00-PROJECT-PLAN.md) §2.

## Working in `mobile/`

```bash
cd mobile
flutter pub get
flutter analyze          # must stay clean
flutter test             # pipeline rules + boot smoke tests
flutter run              # demo account: demo@wordos.app / wordos123
```

Architecture:

- **State**: Riverpod (no codegen). **Routing**: go_router with onboarding guards.
- **`lib/core/api/wordos_api.dart`** is *the* contract. Two implementations:
  `HttpWordOsApi` (real backend, ready) and `MockWordOsApi` (development).
  Swap via `AppEnvironment` / `--dart-define=WORDOS_MOCK=false`.
- **`lib/mock_backend/`** is a disposable simulation of the C# rules. It is deleted in Phase 7.
  **Never** move logic from there into `lib/features/**` — that would break R1.
- **`lib/core/theme/app_tokens.dart`** holds every colour, spacing and radius. No inline values.
- **`lib/core/l10n/app_strings.dart`** holds all UI copy (English + Arabic, RTL supported).

- **`lib/core/storage/app_preferences.dart`** holds the *only* device-owned state: UI language
  and theme (ADR-010). Everything else is server state. Default language is **Arabic**.

Sign in as `owner@wordos.app / wordos123` to reach the **Developer Dashboard**
(Settings → Developer Dashboard): overview analytics, users, per-user drill-down, and the
mock-only "skip 2 days" control for demonstrating the spaced-gap behaviour without waiting.
A normal account cannot reach it — the route is guarded *and* the API returns 403 (`MockAdmin`).

## Conventions

- Match the existing comment density: explain *why* (and cite the rule/document) rather than *what*.
- Models mirror the API contract exactly; update `docs/05-API-CONTRACT.md` in the same change.
- Every judgement call or documentation conflict gets a new ADR in `docs/03-DECISIONS.md` — append, never rewrite.
- Update `docs/02-PROGRESS.md` at the end of a working session so the next one can resume.
