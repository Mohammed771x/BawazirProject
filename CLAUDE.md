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
   A rule about *the system*. A learner may delete their own word, and it becomes
   `DELETED` rather than a `DELETE` — invisible to them, kept for the Owner (ADR-071).
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
- **Widget tests pin the mock backend** in `testOverrides`. `WORDOS_MOCK` defaults
  to `false` for device builds, so a test that lets the real environment through
  points at an HTTP server that is not running — and fails with "Found 0 widgets
  with text 'Skills Hub'", which reads as a UI regression and is not one. Read the
  environment from `appEnvironmentProvider`, never from `AppEnvironment.current`.
- **`lib/core/api/wordos_api.dart`** is *the* contract. Two implementations:
  `HttpWordOsApi` (real backend, ready) and `MockWordOsApi` (development).
  Swap via `AppEnvironment` / `--dart-define=WORDOS_MOCK=false`.
- **`lib/mock_backend/`** is a disposable simulation of the C# rules. It is deleted in Phase 7.
  **Never** move logic from there into `lib/features/**` — that would break R1.
- **`lib/core/theme/app_tokens.dart`** holds every colour, spacing and radius. No inline values.
- **`lib/core/l10n/app_strings.dart`** holds all UI copy (English + Arabic, RTL supported).

- **`lib/core/storage/app_preferences.dart`** holds the *only* device-owned state: UI language,
  theme (ADR-010) and whether this phone raises daily reminders (ADR-076). Everything else is
  server state. Default language is **Arabic**.
- **`lib/core/notifications/`** schedules the daily reminders. Local notifications only — no
  Firebase, no push, no device token. The phone fires them offline with the app closed, so the
  *server* decides what each one says (`GET /api/notifications/daily`) and this layer only
  translates and schedules. Widget tests pin `notificationSchedulerProvider` to a fake; the real
  one is a platform channel a test binary does not have.

**The local stack.** `./wordos start` brings up the AI service and the API and
reports where they are; `./wordos stop` shuts both down; `./wordos status` says
what is running and whether the address the app was built to call is still this
Mac's. PostgreSQL is deliberately untouched — the script did not start it. When
the Mac's network address changes, `./wordos ip` writes the new one into the
Xcode config.

**Running on a device from Xcode.** `--dart-define` values reach an Xcode build
through `DART_DEFINES` in `ios/Flutter/Generated.xcconfig` — which Flutter
rewrites, including when Xcode runs the build itself. A build configured from the
command line and then started with Xcode's Run button therefore loses its defines
and silently falls back to the mock backend: the seeded demo account signs in and
a real one is refused as "wrong email or password". So the device defines live in
`ios/Flutter/Debug.xcconfig`, which is checked in and never regenerated. Change
the address there, not on the command line. `./wordos status` is what says
whether the address the app was built to call is still this Mac's — the sign-in
screen used to say so too, and no longer does (ADR-060).

Open **`ios/Runner.xcworkspace`**, never `Runner.xcodeproj` — and run
`flutter build ios --simulator --debug` once before pressing Run after any
`flutter pub get`, branch switch or plugin change. Xcode never runs `pod install`
itself, so the first build after the Pods sandbox drifts fails with *"The sandbox
is not in sync with the Podfile.lock"* — which reads like a project fault and is
not one. The Flutter CLI runs `pod install` as part of its build; that is the fix.

**Against the mock**, sign in as `owner@wordos.app / wordos123` to reach the
**Developer Dashboard** (Settings → Developer Dashboard): overview analytics, users,
per-user drill-down, and the "skip 2 days" control for demonstrating the spaced-gap
behaviour without waiting. Those two accounts are seeded inside `lib/mock_backend/` and
exist nowhere else.

**Against the real backend** the Owner is `ahmed@gmail.com`. Its password is not written
down here or anywhere else in this repository, and must not be — ask the product owner.
There is deliberately no client-reachable way to create an Owner: a new one is registered
like any learner and promoted with SQL (ADR-061).

A normal account cannot reach the dashboard — the route is guarded *and* the API returns
403.

## Conventions

- Match the existing comment density: explain *why* (and cite the rule/document) rather than *what*.
- Models mirror the API contract exactly; update `docs/05-API-CONTRACT.md` in the same change.
- Every judgement call or documentation conflict gets a new ADR in `docs/03-DECISIONS.md` — append, never rewrite.
- Update `docs/02-PROGRESS.md` at the end of a working session so the next one can resume.

## Building an Android release

```bash
cd mobile
flutter build apk --release --split-per-abi   --dart-define=WORDOS_MOCK=false   --dart-define=WORDOS_API_BASE_URL=https://wordos-api.onrender.com/api
```

Three APKs land in `build/app/outputs/flutter-apk/`; `app-arm64-v8a-release.apk`
is the one for any modern phone. The URL must be **https** — a release build
refuses cleartext.

**Run it on its own.** A debug-mode Flutter command — `flutter test`, `flutter
analyze`, an iOS build — regenerates
`android/app/src/main/java/io/flutter/plugins/GeneratedPluginRegistrant.java`
for *every* platform the project has, in debug mode, which includes the
`integration_test` plugin. The release build then skips regenerating it when pub
is already up to date, and Gradle fails with *"package
dev.flutter.plugins.integration_test does not exist"* — a compile error about a
file nobody wrote, naming a plugin nobody asked for. Delete that file and build
again, with nothing else running.

Release is currently signed with the **debug key**. It installs and runs, and it
is not a store-ready build: a real upload key is still to be created, and
switching to one later means anyone who installed this must uninstall first.

## The app icon

The launcher icon, the web icons and the iOS launch screen are **generated**
from the same mark as `WordOsBrand`, never hand-drawn:

```bash
cd mobile && flutter test tool/generate_app_icon.dart
```

Run it after any change to the brand colours or shape. It lives in `tool/` and
not `test/` so a normal `flutter test` does not rewrite the files (ADR-057).

## Capacity

Every ceiling this service imposes on itself lives in `Capacity` configuration
(`backend/src/WordOs.Api/Endpoints/CapacityOptions.cs`) — database connections,
password hashes in flight, AI calls in flight, connections, body size. They are
deliberately not constants: the right number depends on the machine and on how
many instances share one PostgreSQL (ADR-051).

Two rules when touching them:

- **Never queue by blocking.** A synchronous wait on the request path holds a
  thread, so a burst in one place starves everything else. Measured: sign-in
  throughput halved. Use `await`.
- **Overload is not failure.** A request refused for capacity gets 503 with the
  learner's session untouched — not a wrong-password answer, and not the AI
  fallback, which exists for a model that answered badly rather than a queue
  that clears in seconds.

Load is measured, not assumed: `ab -n 5000 -c 1000` against a local instance
with rate limits raised (a real crowd arrives from a thousand addresses, so the
per-caller limiter would otherwise mask the test).
