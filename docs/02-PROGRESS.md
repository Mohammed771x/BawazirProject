# WordOS — Progress Ledger

> **The live state of the project.** Update this at the end of every working session.
> A new session should read `CLAUDE.md` → this file → then start at "Next up".

**Last updated:** 2026-08-17

---

## Phase status

| Phase | Name | Status |
|-------|------|--------|
| 0 | Foundations & planning | ✅ Done |
| 1 | Flutter foundation (design system, contract layer, mock backend) | ✅ Done |
| 2 | Identity & onboarding | ✅ Done |
| 3 | Core surfaces (Hub, Add Word, Vocabulary, Settings) | ✅ Done |
| 4 | Five skills + Weekly Review | ✅ Done (against the mock) |
| 4.5 | Demo review round 1 (product owner feedback) | ✅ Done |
| 5 | C# backend (ASP.NET Core + PostgreSQL) | ✅ Contract complete on real PostgreSQL |
| 6 | Python AI service | ✅ FastAPI + real Gemini, token-authenticated |
| 7 | Integration (swap mock → real, voice capture) | ✅ All five skills on the real API; voice conversation built |
| 8 | Analytics & Owner dashboard | ✅ Real PostgreSQL-backed analytics |
| 9 | Hardening & pilot | ⬜ Not started |

## Demo review round 1 — status

Product-owner feedback of 2026-08-15, worked in order of dependency.

| # | Item | Status |
|---|------|--------|
| 1 | Welcome screen composition | ✅ Brand mark lifted; form block optically centred below it |
| 2 | **Arabic by default**, switchable in Settings | ✅ `AppPreferences`, persisted; device locale deliberately ignored (ADR-010) |
| 3 | Interests: encourage many + "Other" free text | ✅ Shared `InterestsEditor`; custom entries de-duplicated |
| 12 | Interests editable later from Settings | ✅ Same editor, add/remove, keeps ≥1 |
| 4–6 | Placement: real algorithm + documentation | ✅ Adaptive Rasch/EAP (ADR-009), [`06-PLACEMENT-ALGORITHM.md`](06-PLACEMENT-ALGORITHM.md) |
| 46 | Spelling must not carry a CEFR level | ✅ Nullable levels (ADR-008); measured via accuracy + input mode |
| 8–11 | Developer Dashboard: overview, users, drill-down | ✅ Owner-only area, charts, per-user journey |
| 13 | Developer tools out of learner Settings | ✅ Moved into the Owner area |
| 14–22 | Vocabulary: validation, meanings, duplicates | ✅ Client + rules done; real lexicon is a Phase 5 data task (ADR-012) |
| 23–48 | Rework the five skill sessions | ✅ Reading context, Listening audio-first, Speaking conversation, Writing "use the word", Spelling per docs |
| 29–31, 47–48 | In-session learning loop + repeated errors | ✅ Requeue, attempt cap, first-attempt-only pass |
| — | Level progression + archiving (closed a known gap) | ✅ `LevelEngine`, ADR-013, level history in the dashboard |
| 51–52 | Exception/resilience pass | ✅ Typed error taxonomy, session recovery, audio fallback |
| 55–59 | Test expansion | ✅ 99 tests incl. failure injection and security |

**Decisions taken 2026-08-15 (all confirmed by the product owner):**
ADR-011 — backend stays **ASP.NET Core + PostgreSQL**; Firebase rejected as the
system of record. ADR-012 — vocabulary comes from a **server-side lexicon**
joined on WordNet synset id from CEFR-J + Open English WordNet + Arabic WordNet;
**no AI-generated meanings**, and Arabic WordNet is approved as a lexical
resource. ADR-001 — skill order **confirmed** as
`Reading → Listening → Speaking → Writing → Spelling`; no longer open.

**Security:** [`07-SECURITY.md`](07-SECURITY.md) is the binding requirement set
for the backend, with an implemented/specified split. The client audit found no
secrets; two client gaps were found and fixed (cleartext transport allowed in
release, no 401 recovery).

## Backend status (Phases 5–6)

`backend/` and `ai-service/` are built and running against real infrastructure.

| Area | State |
|------|-------|
| Solution | `WordOs.Api → Infrastructure → Application → Domain`, .NET 10 |
| Database | PostgreSQL 17, schema created **only** through EF Core migrations; two roles (`wordos_migrator` owns DDL, `wordos_app` has no DDL rights — verified by attempting `CREATE TABLE` as the app role) |
| Lexicon | CEFR-J + Open English WordNet + Arabic WordNet joined on synset id; raw datasets gitignored, import is transactional |
| Auth | Argon2id, JWT + rotating refresh tokens with family revocation, `/admin/*` behind `OwnerOnly` |
| Placement | Rasch 1PL + EAP, adaptive selection by Fisher information, 12–22 items |
| Sessions | All five skills, in-session requeue loop, resume endpoint |
| Weekly review | Measurement only (R9) — no path from it to any pipeline write |
| AI | FastAPI → Gemini `gemini-3.1-flash-lite`, JSON-schema-constrained output, `X-Service-Token` required, prompt versions recorded per session |
| Exposure | Credited when generated content actually reuses an Active word, once per session, enforced by a unique index (ADR-018) |
| Tests | **189 green** (69 domain + 120 API over real PostgreSQL), plus 118 Flutter widget tests and 3 real-stack integration journeys |

Verified end to end with real Gemini, not mocks: register → interests → add two
dictionary words → Reading session (generated passage using the learner's
interests, five comprehension questions, per-word context questions) → answer →
complete → Listening → Speaking → Writing (Gemini flags "a research" as a
grammar slip; the backend still passes it, per ADR-015) → weekly review, with
each step's rows confirmed by SQL against `wordos_dev`.

**Rules held under test:** the answer key never appears in any client response;
a session belonging to another learner returns 404, not 403; a wrong answer
requeues and costs the pass; failing Writing leaves Reading/Listening/Speaking
`Passed`; an AI outage produces a complete session flagged `usedAiFallback`.

**Not yet done:** voice capture for Speaking, and analytics endpoints.

## What exists right now

A complete, runnable **Flutter app** in `mobile/` covering the whole learner journey against a
contract-accurate mock backend.

```
Login / Register → Interests → Placement test → per-skill levels
   → Skills Hub → Add Word → Reading · Listening · Speaking · Writing · Spelling
   → Session results → Weekly Review → Vocabulary (Learning/Active/Archive) → Settings
```

**Verification:** `flutter analyze` clean · `flutter test` **118/118 green**.

Test suites:
- `test/word_pipeline_test.dart` — pins the learning rules (written as a spec for the C#
  backend, not as mock-implementation details).
- `test/placement_algorithm_test.dart` — the placement algorithm as a specification: all-correct,
  all-wrong, uneven skills, erratic answers, termination, no repeated items, option shuffling,
  protocol errors. Port these to xUnit in Phase 5.
- `test/developer_dashboard_test.dart` — **authorization first**: a normal learner is refused
  every admin endpoint with 403; registration can never produce an OWNER.
- `test/localization_test.dart` — Arabic default, persistence, RTL layout.
- `test/app_boot_test.dart` — boot → sign-in → Skills Hub.
- `test/learner_journey_test.dart` — driver-level walkthroughs: add a word, run a full Reading
  session, complete the Weekly Review, inspect Settings.

- `test/vocabulary_test.dart` — lookup and add-word rules: prefix autocomplete, multi-meaning,
  non-word, near-miss, duplicate `word + meaning`, same word different meaning, forged
  candidates and forged sense ids.
- `test/learning_loop_test.dart` — session shape (exactly five comprehension items, context
  sentences, listening-is-audio-only) and the loop itself: requeue, first-attempt-only pass,
  the retry cap, writing evaluation, speaking conversation.
- `test/resilience_test.dart` — failure injection: a dropped connection on session start and on
  answer submission, a dead speech engine, foreign session ids, abandoned sessions.
- `test/security_test.dart` — no key-shaped literal anywhere in `lib/`, cleartext transport
  refused in release, device storage limited to presentation state.
- `test/level_progression_test.dart` — the level policy and archiving rule (ADR-013): promotion
  by one step at ≥85%, demotion below 70%, the evidence window, C2/A1 ceilings, the
  weakest-skill proven level, and archiving that never fires on a manual level change.

Six real bugs the new suites caught during development, all fixed:

1. The offline free-text scorer gave a one-word answer ~33 % credit — a single word trivially has
   100 % lexical "variety", and variety was an additive term rather than a discount on length.
2. The placement confidence mapping demanded a posterior SE unreachable at realistic item counts,
   so *every* placement reported low confidence.
3. `MetricTile` overflowed its fixed-ratio grid cell whenever a caption wrapped.
4. `ColumnChart` computed its bar height by subtracting a guessed label height, overflowing by a
   couple of pixels; it now uses `Expanded` + a height factor.
5. **`ref` was used inside `SessionScreen.dispose()`** — Riverpod throws once teardown starts, so
   backing out of a session mid-way crashed. The API is now captured in `initState`.
6. `TtsService` scheduled a 5-second timeout timer during configuration and disposal, which
   outlived the widget tree on any platform without a speech engine.

### File map (`mobile/lib/`)

| Area | Files |
|------|-------|
| App shell | `app/wordos_app.dart`, `app/router.dart` (guards follow server `onboardingStage`) |
| Design system | `core/theme/app_tokens.dart`, `app_theme.dart`, `skill_visuals.dart`, `core/widgets/app_widgets.dart` |
| Localization | `core/l10n/app_strings.dart` (en + ar, RTL) |
| Models | `core/models/*.dart` — mirror `docs/05-API-CONTRACT.md` exactly |
| API | `core/api/wordos_api.dart` (contract), `http_wordos_api.dart` (real, ready), `api_providers.dart` (swap point) |
| Mock backend ⚠️ | `mock_backend/` — disposable rule simulation, deleted in Phase 7 |
| Auth | `features/auth/` — session controller, login, register |
| Onboarding | `features/onboarding/` — interests, placement (+ inline result) |
| Hub | `features/hub/` — bottom-nav shell, Skills Hub |
| Words | `features/words/` — add word, vocabulary tabs, word detail with event history |
| Sessions | `features/session/` — all five skills in one payload-driven screen + result view |
| Review | `features/review/weekly_review_screen.dart` |
| Settings | `features/settings/settings_screen.dart` — levels, targets, interests, theme, language |
| Developer ⚠️ | `features/developer/` — Owner-only overview, users, drill-down, mock time-travel |
| Placement ⚠️ | `mock_backend/engine/placement/` — adaptive engine, item bank, scorer (disposable) |
| Preferences | `core/storage/app_preferences.dart` — the *only* device-owned state (ADR-010) |
| Audio | `core/audio/tts_service.dart` — normal/slow TTS for Listening |

### Rules already encoded (and covered by tests in `mobile/test/word_pipeline_test.dart`)

- A new word opens **only** the first skill; the rest stay `PENDING`.
- Passing a skill schedules the next one `skill_interval_days` later; it is genuinely not
  startable before then, and stays waiting (not lost) if the user disappears for a week.
- Failing a skill keeps every already-passed skill and retries only the failed one.
- Five passes → `ACTIVE`, `currentSkill = null`.
- Session size is capped by the per-skill daily target; targets clamp to 5–15.
- A manual level change moves only the *user-selected* level.
- Weekly Review requeues wrong answers, scores first-pass accuracy, and leaves every skill
  status untouched.

## Known gaps / deliberate deferrals

1. **Mock state is in-memory** — restarting the app resets it (a seeded demo user with words
   spread across the lifecycle is recreated each launch). Real persistence arrives with Phase 5.
2. **Speaking is a hands-free voice conversation** (ADR-020): the tutor speaks, the microphone
   opens by itself, silence ends the turn. Verified with faked platform services; a physical
   device is still needed to confirm the real recogniser, since the simulator cannot listen.
3. **Exposure counting is only half wired.** The backend increments it when a word is met in a
   weekly review; the other source — the AI reusing an Active word in generated content — is
   still open (Phase 7). Archiving already reads it as a priority signal, never a limit (R8).
4. **Speech recognition** for Speaking, and pronunciation assessment with it, land in Phase 7.
5. **Archiving and level progression are implemented and tested** (ADR-013), but only against
   the mock. They are the rules the C# `Levels` module must reproduce.

## Phase 7 — where it stands

**All five skills are now verified through the real Flutter UI** against
Flutter → ASP.NET Core → AI service → Gemini → PostgreSQL, on a booted
simulator. Three integration journeys, all green:

| Journey | What it drives |
|---|---|
| `real_backend_journey_test.dart` | register → interests → 22 adaptive placement questions → Hub → autocomplete → add a sense → Reading → weekly review → relaunch |
| `five_skills_test.dart` | one word carried Reading → Listening → Speaking → Writing → Spelling → **Active**, each screen on a word that genuinely arrived there |
| `session_resume_test.dart` | leave and relaunch mid-session on **every** skill |

Confirmed by SQL afterwards: `research` reached `Active` with all five
`word_skill_states` at `Passed`, Reading and Listening carrying real
`gemini-3.1-flash-lite` token counts and `UsedAiFallback = false`.

What each skill proved, on its own terms:

- **Listening** — the script is never printed, only spoken; a resumed session
  does not leak it either.
- **Speaking** — a real conversation with state across turns; there is no finish
  button because the **server** ends it once every target word has been used;
  a resumed conversation comes back with its transcript.
- **Writing** — Gemini flagged "a research" as an uncountable-noun slip and the
  backend passed the sentence anyway (§32 / ADR-015), with real feedback shown.
- **Spelling** — `LETTER_TILES` with a hint that reveals the opening letters and
  length; unlevelled in Settings, asserted against what the UI renders
  (ADR-008).

The two-day gaps are collapsed for these runs via `WordOs__SkillIntervalDays=0`
— a harness setting, not a committed default. The gap itself stays pinned by
backend tests that assert both the schedule and the 409 for starting early.

**Bugs this found, all in the app rather than the tests:**

1. **The Listening screen crashed on layout.** The theme gives every
   `FilledButton`/`OutlinedButton` `Size.fromHeight(54)` — an *infinite* minimum
   width — so an unflexed one inside a `Row` fails layout outright. The
   slow-speed button was unflexed; so was the Spelling "Check" button after a
   `Spacer`. Both fixed, and the codebase re-scanned for the pattern.
2. **A resumed conversation lost its history** — the server sent only the
   opening line, so a returning learner saw an empty chat. It now returns the
   whole transcript.
3. **A wrong first answer sent the learner back to the passage.** Resume keyed
   off `answered`, which counts *cleared* items — and a wrong answer requeues
   without clearing. The server now reports `attempted`, which is the honest
   signal.
4. The login screen prefilled the mock demo account against the real API,
   handing learners credentials that cannot work. Now mock-only.
5. `WordOsConfiguration` was constructed with its defaults and bound to nothing,
   so none of the "tunables" were tunable — squarely against rule R3. It is
   configuration now, with the documented production values as defaults.

**Known gap, not fixed:** Speaking and Writing sessions record no `AiModel` or
`PromptVersion`, because only content generation calls `SetContent`. Reading and
Listening are attributable; the other two are not, which weakens the
prompt-change attribution `MVP Core.txt` §62 asks for.


The Flutter app runs against the real backend. Verified by
`mobile/integration_test/real_backend_journey_test.dart`, which drives the
**real UI** on a simulator against the running stack — no mock anywhere in the
chain:

```
register → interests → 22 adaptive placement questions → Skills Hub
  → autocomplete "research" → pick a sense → add
  → Reading session (Gemini passage, 5 comprehension + context questions)
  → answer → result → weekly review
  → relaunch → session resumes where it was
```

Placement assigned real per-skill levels from those answers
(Reading A2+, Speaking B2, Spelling null — ADR-008 holding end to end), and every
row is in PostgreSQL.

**Client changes, all connection rather than redesign** — no screen was
rebuilt:

| Area | What changed |
|---|---|
| Auth | Refresh tokens stored beside the access token; a 401 refreshes once and replays the original request, so an expired token never interrupts a session. Concurrent 401s share one refresh. |
| Restore | Being offline at launch no longer signs the learner out — only a rejected session does. |
| Words | `POST /words` sends the **sense id**, not a meaning the client picked. |
| Sessions | Start returns the open session if there is one; the screen resumes from server progress instead of assuming item one; backing out no longer abandons. |
| Errors | 401/403/404/409/429/5xx each map to a typed `ApiException`; ASP.NET validation problems are flattened to per-field messages. |

**Server changes found by connecting the client** — each one a real gap:

1. `/me` and the auth responses omitted `skillLevels`, so Settings would have
   silently shown A1 for every learner.
2. `GET /words/{id}` did not exist, and word responses carried no `skills`, so
   the pipeline view had nothing to draw.
3. `POST /sessions/{skill}/start` replaced an unfinished session instead of
   resuming it — losing answers and spending a second Gemini call.
4. **The rate limiter ran before authentication**, so every "per-user" budget
   was really per-IP: one learner could exhaust the AI allowance for everyone
   behind the same NAT. Fixed, and pinned by a test.
5. Rate-limit budgets were hard-coded, against rule R3. They are configuration
   now, with the production values as defaults.

**Since done:** the hands-free voice conversation (ADR-020), end-of-conversation
Speaking evaluation (ADR-019), AI attribution on Speaking and Writing, and real
PostgreSQL-backed Owner analytics. `mobile/lib/mock_backend/` survives only
because the widget suite runs on it.

## Next up

The backend now implements every contract the client consumes, so the mock has
served its purpose.

1. **Try the voice conversation on a physical device.** The loop is built and
   unit-tested with faked platform services, and the app falls back to typing
   where listening is impossible — which is what the iOS Simulator does, so the
   simulator cannot prove the microphone half. A device run is the only way to
   confirm the real recogniser ends turns where a learner expects.
2. **Delete `mobile/lib/mock_backend/`** and the mock-only "skip 2 days" control.
   It is kept **only** for the widget suite, which runs entirely on it; it is
   never evidence that the real API works — the four integration journeys are.
3. Pronunciation assessment remains deliberately out of scope (ADR-020).

### Running the stack locally

```bash
```bash
./wordos start     # both services, with a health check and the address to use
./wordos stop      # both of them; PostgreSQL is left alone
./wordos status    # what is up, and whether the app is pointed at this Mac
./wordos ip        # after the Mac's address changes, repoint the Xcode build
```

Or by hand:

```bash
# AI service (holds the Gemini key; never reachable from Flutter)
cd ai-service && uvicorn app.main:app --host 127.0.0.1 --port 8099

# API — Development is required, user-secrets only load there
cd backend && ASPNETCORE_ENVIRONMENT=Development \
  dotnet run --project src/WordOs.Api --urls http://127.0.0.1:5199
```

`dotnet` lives at `/usr/local/share/dotnet` and is not on the default PATH;
`dotnet ef` needs `~/.dotnet/tools` too. Secrets (`Jwt:SigningKey`,
both connection strings, `AiService:Token`) are in user-secrets, not in any file
under version control. Production must use real secret management and a
migration role **without** `CREATEDB`.

## Session log

| Date | Work |
|------|------|
| 2026-08-20 | Deployment, for Neon + Render on their free plans (ADR-062). Sizing measured first: the published API is 160 MB idle, 192 MB under a 100-concurrent burst at 5,166 req/s, and the AI service adds ~40 MB — ~232 MB of Render's 512. Both services share one container, which saves half the free instance-hours and puts the Gemini key on loopback instead of behind a public URL. Startup migration was tried and PostgreSQL refused it, correctly: the service connects as a role with no DDL rights, so migrations stay a command run from a developer's machine before each deploy. Two mistakes caught by checking — the config section is `AiService` not `Ai` (binds to nothing, empties the service token, no startup error), and `curl` is absent from the runtime image. The image itself is unbuilt: there is no container runtime on this Mac, and the guide says so. |
| 2026-08-20 | Database reset for the MVP (ADR-061). All 193 accounts deleted — every one was test data from feature work, load tests and end-to-end runs, and all of it was distorting the dashboard. Deleted by deleting the accounts rather than the tables, which also proved the cascades: seventeen tables emptied themselves and none was left behind. The lexicon was untouched (234,359 entries — reference data nobody owns) and a pg_dump taken first, outside the repository. One new Owner, registered through the API so the password is Argon2id-hashed and the skill rows are seeded, then promoted with SQL because there is deliberately no client path to Owner — the registration returned `role: USER`, which is the guard working. Its password is in no file in this repository. Verified: signs in, reaches every admin endpoint, refused on a wrong password, dashboard reads zero. |
| 2026-08-20 | Removed the debug backend banner from the sign-in screen (ADR-060). It was added when an Xcode build silently fell back to the mock and looked like a working app, but it is the first thing anyone sees and a URL above the password field is developer furniture in a learner's way. `./wordos status` answers the same question better, and warns when the configured address is no longer this Mac's. Release builds never showed it. 280 Flutter tests. |
| 2026-08-20 | Two small things in Speaking (ADR-059). The microphone had one exit — tap to start, tap to send — so a learner who fumbled a sentence had to send the fumble and let the tutor answer it. A bin now sits beside it while there is something to throw away: it cancels rather than reads, sends nothing, spends no AI call, and offers the microphone again. And the end-of-conversation summary was written *about* the learner — "The learner successfully used both target words" — because the prompt never described that field at all, while the per-word feedback beside it had been second person since ADR-048. Now specified, and the rule stated once for the whole prompt: everything the learner reads is written to them. Verified live in Arabic. 94 + 337 backend, 280 Flutter. |
| 2026-08-20 | Three placement-test reports from the device turned out to be one bug (ADR-058): the listening clip played over the next question, the microphone stayed open into it, and the previous written answer was still in the box. The sessions had all of this fixed long ago; placement had never received any of it, and nothing on that screen was ever told a question had ended. Now every transition — start, answer, finish, leave — stops the voice and cancels the microphone, and each question is keyed by its item so it builds its own field: one key resets the text, the microphone and the typing toggle together, because they share the State it replaces. Disposal was missing altogether — the screen held no reference to either service. A fourth report after the first fix closed the last of it: a transcript already in flight when the microphone was cancelled still arrived, and landed in the next question — visible only on a spoken one, because that box renders what the screen holds rather than a controller of its own. The answer handler now knows which question it was built for. Four tests, each verified to fail when its own fix is removed. Two test faults found while writing them: a walk that tapped "the first tappable thing" was answering the play button on listening questions, and an earlier registration test typed the phone number into the password box, so it passed for the wrong reason. 277 Flutter tests. |
| 2026-08-20 | The app wears its own mark now (ADR-057). The W on the sign-in screen is the launcher icon on every platform — fifteen iOS sizes, five Android densities, the web icons and favicon — and the iOS launch screen, so the first frame is the brand instead of a white flash. Generated from the widget itself by a tool run under `flutter test`, because any other route means a second copy of the logo that drifts from the first; kept in `tool/` so an ordinary test run does not rewrite twenty-five files. Three things the first attempt got wrong, all caught by looking at the output: the glyph rendered as a white rectangle (a test binding has no real font, so the W is a stroked path now), every file came out 800×600 (the capture is clipped to the test surface), and the encode hung until it was wrapped in `runAsync`. The label beside the icon read `wordos`/`Wordos`; it reads WordOS. Verified by building and reading the icon back out of `Runner.app`. |
| 2026-08-20 | My Words now says what each word *is* (ADR-056). Since every inflection became an entry of its own, a list could hold `go`, `went` and `going` with nothing to tell them apart — and `book` the noun beside `book` the verb. Each entry now shows its part of speech and, when it is an inflection, which form: *فعل · الماضي* for `went`, *اسم* for `book`. The form travels as a key and is said in the learner's language; a plain word carries no form label. Checking rather than assuming found a real defect: the label table covered the spelled-out names but the importer writes short codes — `prep`, `det`, `aux`, `modal` and four more, 167 rows in all — so a learner who added `can` was shown the raw word "modal" inside an Arabic interface. Every stored code is now translated, with the short and long spellings agreeing. Eight Flutter tests, three rendering the tile and asserting what a person reads. 94 + 337 backend, 274 Flutter. |
| 2026-08-20 | Settings gained **Contact support**: one tap into WhatsApp at +91 7558973719 (ADR-055). It sits beside the message box rather than replacing it — feedback is for something that can wait and be read later, this is for a learner who is stuck now. Uses the `wa.me` https link rather than the `whatsapp://` scheme, so it opens the app when installed and the browser when not, and needs no iOS entitlement. The number is a client constant: a chat link needs no server, and routing it through configuration would only mean the button fails when the network does. If the launch fails the number is shown, selectable and forced left-to-right so it does not read backwards in Arabic. Four tests, including one that mocks the platform channel and asserts the exact URL handed to the phone. 266 Flutter tests. |
| 2026-08-20 | A phone number is now required to create an account (ADR-054). Feedback is only as useful as the Owner's ability to answer it, and an optional field meant a bug report could arrive from someone nobody could reach. Enforced at the form, the endpoint and the domain — a future caller cannot create an unreachable account by forgetting. Checked on the digits rather than the input: "()- " is punctuation the field permits, contains no number, passed the shape check and reached the domain, which threw and produced a 500; it is a 400 naming the field now. Existing accounts are untouched — 102 of them have no number and sign in exactly as before. Sixteen fixtures across both suites had to start supplying one, which is the proof the rule is enforced. 94 + 330 backend, 262 Flutter. |
| 2026-08-20 | Learners can now reach a person, and the Owner can reach them back (ADR-053). There had been no way to report anything at all — no address, no support screen — so a learner who hit a bug could only stop using the app, and the reason would never be known. Settings gained a "Message the team" card; the dashboard gained a Feedback tab showing what was written, who wrote it, their email and phone, and the build it came from, unhandled first. Marking a message handled is reversible, because an Owner reading a long list will mark the wrong one eventually. Learners write and can never read — there is no call that returns feedback to a learner, so no client change can leak one learner's words to another. The text is stored as written, rendered as text and interpreted by nothing, bounded at 4 000 characters on both sides and stripped of the NUL PostgreSQL refuses. Contact details also went onto the learner's own page, tap-to-copy, for gathering numbers for a group. Verified live in Arabic, with 403/401/400 on every path that should refuse. 94 + 326 backend, 260 Flutter. |
| 2026-08-20 | Audited every figure in the Developer Dashboard against independent SQL, after "24 sessions · 11 passed · 4 failed — those don't add up" (ADR-052). They never could: sessions are sessions and the other two are words. Most of the dashboard was exact to the row; five things were not. First-attempt accuracy divided words by attempts, reading Speaking as 67% where the answer is 86%. Owner accounts sat in every numerator while the denominators counted learners — 114 of 392 skill decisions were a developer's own testing reported as how the audience is doing, and the two screens disagreed about the word "learners" (180 against 185). The learner list read "0 sessions" for everyone because the server never sent the field the client drew. "Last active" was last *login*, so 54 accounts that had done real work showed nothing. And "today" was a UTC day: six words added after local midnight reported as zero. All five fixed, units now stated on screen, the day boundary configurable (+3 default), and the typical session reported instead of a mean that 11 resumed sessions had dragged from 16 seconds to 45 minutes. Re-audited afterwards: every figure matches SQL and fifteen cross-screen consistency checks pass. 94 + 317 backend, 259 Flutter. |
| 2026-08-20 | Capacity, measured rather than assumed (ADR-051). Asked whether the service survives a real audience, `ab` answered no: at 200 concurrent readers PostgreSQL ran out of connections — one instance may claim all 100 by default — and learners got 500s. Behind it, three more: Argon2id at 19 MiB a time made sign-in a way to exhaust the machine, a slow Gemini minute would have taken down endpoints that never call it, and the AI service kept its token counts in a module global that two concurrent requests overwrite, quietly corrupting the experiment's own measurement. Now every ceiling is explicit and configurable: a bounded pool with transient retries, one hash per core with an asynchronous queue, a shared cap on AI calls in flight answered by a fast 503 with the session untouched, bounded Kestrel, and free capacity reported on the readiness probe. After: 1 000 concurrent readers, 0 errors, 5 490 req/s, slowest request 291 ms; sign-in tail latency halved. Six tests pin the properties — including that waiting for a hash does not hold a thread, which the first attempt got wrong and which halved throughput. 94 domain + 312 API tests, 258 Flutter. |
| 2026-08-20 | A Speaking session announced two words and then asked about twelve (ADR-050). It looked like a corrupted developer account; the account was fine and the session record was correct — the *turn* re-read the whole Speaking queue instead of the words the session was opened for, so every word parked at that skill joined the conversation, including ones already passed and ones added while it was open, which ADR-048 says must wait. The turn now reads the session's own record like every other caller. Beside it, a second gap: the tutor was handed bare words, so it could not ask a question that needs a past tense, and when a learner reached for `went` and said `go` all it could say was "try to use the word went". It is now told what each remaining word is (ADR-047) and names the step that was missed — "Almost — I need the past tense: 'went'" — staying on that word instead of correcting at length. Verified end to end on a new account against real Gemini: warm-up, greeting, the form repair, both words passing, a closing with no further question, and per-word Arabic feedback. 94 domain + 306 API tests, 258 Flutter. |
| 2026-08-20 | Speaking now hears the word the way a teacher would (ADR-049). A turn counted a word if the transcript *started with* it anywhere, which said yes to `booking a flight` and `the bookshop` and moved the learner on from a word they had not used — and still asked again for `book` after they answered "three books", which to the learner is the app not listening. A word now counts as a whole word, and a plain noun's regular plural counts as the word, decided from the same shape fact the passage checks before it writes one (ADR-047). Everything else stays a different word: `mice` is not `mouse`, `researched` is not `research`. The fix surfaced a crash underneath — a learner holding `book` twice, the object and the verb (ADR-046), broke the turn with a duplicate key, because a transcript cannot tell two senses apart; one spelling is now one thing to say, and both senses move on together. Verified live against real Gemini: `booking` ignored, `books` accepted, `go` did not pass `went`, `went` did. A shared-database assumption in an unrelated lexicon test was corrected while proving it. 94 domain + 303 API tests. |
| 2026-08-20 | Speaking now ends in coaching rather than a verdict (ADR-048). The end-of-conversation evaluation always produced, per word, what the learner said and what they should do about it — and all of it was used for pass/fail and then thrown away, so what reached the learner was a tick or a cross. Each word now returns with what happened, why it was wrong when it was, that it comes back another day, their own words quoted, and one English sentence to copy — required, because a learner told what was wrong and not shown what right looks like has been marked, not taught. Underneath it, the turn count stopped depending on the model: the transcript settles whether a word appears, and the model is asked only whether it was *named* rather than used. Session rules pinned by test: adding words mid-conversation does not fork a second session, and a completed one gives way to the next. 386 backend tests, 258 Flutter. |
| 2026-08-21 | Full-service audit, then the ten defects it found (ADR-063). The headline three shared one shape — a rule enforced by a look rather than a lock. **Starting a session forked under concurrency:** four simultaneous starts made four sessions and four Gemini calls, 14,366 tokens where 3,679 was the requirement, and the existing test for "resumes rather than forks" passed only because it was sequential. The row is now claimed by a filtered unique index *before* the content is generated, so the losers spend nothing and receive the winner's session; measured after, six concurrent starts give one session and one call. **`abandon` on a finished session deleted it** — the one route that skipped `LoadSessionAsync`, so it alone skipped the `IsComplete` check; word outcomes survived but the comprehension score, token cost, prompt version and level used did not, which is the measurement this experiment runs on. Now 409. **The AI fallback never reached anyone:** backend timeout and Flutter receive timeout were both 90s, and with the AI hung the server was measured answering at 90.08s against a client that gave up at 90.00s — the learner saw a timeout instead of the degraded lesson the server had carefully built. Backend budget is now 25s (measured: 25.03s, fallback delivered). Also: `complete` no longer fails words it never asked about (eight came back `FAILED` with zero attempts), practice sessions stand aside for real words and expire after 24h but are kept when nothing is due, unique-violation races answer 409 instead of 500 (six concurrent registrations: one 200, five `EMAIL_TAKEN`), `?page=2147483647` no longer overflows to a 500, and EF's per-statement SQL logging is off in production (744 KB of log from a handful of requests). The Python service gained its first tests — 82 of them, with an autouse fixture that fails any test reaching the provider for real, added after the first draft sent a live request to googleapis.com. `AI_SERVICE_TOKEN` is now required at startup: unset, it did not weaken the check, it removed it. Two of the fixes bit back and were caught by their own tests: the mid-build cleanup deleted sessions that were still being generated (one start in six answering 500, invisible except under full-suite load), and the conflict path handed the losers the winner's row before it had any content — five of six simultaneous starts received an empty session. Both now answer `409 SESSION_STARTING`. Final live check: six simultaneous starts, one session, one Gemini call, five honest retries, and a retry a second later returns the finished passage. 345 backend tests, 280 Flutter, 82 Python. |
| 2026-08-20 | Reading and Listening are now told what *shape* each word may take (ADR-047). Once a form is a vocabulary item of its own, "use this word" stopped being a complete instruction: a learner practising `played` as the participle is learning "I have played", and one who added `mouse` has not been taught `mice`. Each target word arrives with its shape — use the form exactly, or use singular-or-plural when the plural is just an `s`. Whether a plural is regular is looked up in the lexicon rather than guessed, and the form is read back from the sense id rather than stored twice. Active words reused in a passage follow the same rule. Spelling is deliberately untouched: it asks for the word exactly as added, because there the spelling is the question. Verified against real Gemini — "have played", "a physical book", "a small plastic mouse", no `mice`; across three runs `books` appeared once and `children` never. 383 backend tests. |
| 2026-08-20 | One spelling, two things to learn (ADR-046). For most verbs the past and the participle are the same word — `played`, `said` — and a single entry let a learner practise one without choosing which. Both roles are now offered, each labelled in both languages and carrying that label into the search list, the vocabulary, the spelling clue and the definition the AI is given: a writing answer of "I have played football since I was a child" comes back judged as correct use of the perfect tense. The 35 verbs where only one role is real are enumerated by hand — `beaten` is never the past of `beat`, `ran` never the participle of `run` — because the missing form is the base and the data cannot say so. The importer also learned to remove what it no longer produces: it upserted by sense id and never deleted, so the first, wrong labelling of `beaten` survived its own correction. Words a learner has added are never removed. 216,208 → 234,359 rows. 373 backend tests. |
| 2026-08-20 | The dictionary held `go` and not `went` (ADR-045). WordNet is a lexicon of lemmas, so every form a learner meets in a real sentence was unreachable. Each form is now an entry of its own — addable, schedulable, practisable — labelled with what it is in both languages. The rule the product asked for is drawn precisely: forms that look different are words (`went`, `gone`, `going`, `mice`, `children`, `women`, `lunged`), a plural that is the word plus `s` is not (`books`, `cities`). WordNet's own `form` lists supply the irregulars, rules supply the regulars, and two short authored lists cover the ten verbs whose past is the base — reading the dataset's silence as "regular" would have invented `readed` and `costed` — and the three plurals it misses. Past and participle are told apart by two patterns that settle 100 of 132 cases, and nothing is guessed for the rest: the first cut shipped `went` labelled as the third form. 175,778 → 216,208 rows. 367 backend tests. |
| 2026-08-20 | Checked that the level control on a conversation does what it claims, and found the low end did not (ADR-044). The register moved with the band, but an A1 learner was still hearing "run the same lines of code many times until a condition is met" — B2 vocabulary in a short sentence. Each band now carries its own description into the prompt instead of being named. Measured on the same moment: A1 went from 38 words to 16 ("Do you need to repeat the code in a loop?"), C2 from 48 to 62 with the vocabulary to match. Live mid-conversation, dropping C2 → A1 changed the very next line. Also confirmed the spelling space tile: it appears for `alarm clock` and not for `project` or `hook`. |
| 2026-08-20 | Spelling stuck on "Loading…" for ever, while the server answered that same request in five milliseconds (ADR-043). The session had been answered through but never closed, so it was resumed with no next item and the screen waited for a question that did not exist — the state any learner reaches if the app dies between the last answer and the result screen. It could not recover either, because completing it returned 500: a word had moved on to Writing, and the domain rightly refuses to apply a Spelling result to it — but throwing left the session open to be resumed and fail again for ever. Now: a session with nothing left to ask finishes itself, a missing item offers Finish instead of a spinner, a superseded word is skipped and marked rather than thrown, and `_start` catches everything rather than only `ApiException`. Verified on the stuck account. 324 backend tests. |
| 2026-08-19 | Two from the device (ADR-042). A conversation ended on "try to use the word loop" one line after the learner used it — the reply is written in the same call that judges the turn, so it always reflects the list as it stood before they spoke. When the last word lands the closing turn is now asked for properly: one extra call, once, with an empty list, and the learner hears what they did well instead of another question. And a two-word entry could not be spelled: the letter pool stripped spaces, so `alarm clock` offered every letter and no way to put the gap in. Spaces are tiles now — drawn as a space bar, excluded from the decoy count — and spelling is judged on the letters rather than the spacing. 229 API tests. |
| 2026-08-19 | Speaking, two more from the device (ADR-041). A learner who said "I want to become a software engineering" was asked for `become` again: the turn-level rule wanted words used "naturally and correctly", so a grammar slip stopped the conversation advancing — reproduced three times out of three. Moving on is now about *use* alone; an attempt counts even when it is wrong, and how well it was used is still judged once at the end, where that same sentence passes. And the tutor names the word again at the end of every practising turn, which ADR-040 had made conditional — a learner cannot see the word list. The closing turn became its own prompt after a model with an empty list invented a word and asked the learner to use `obstacle`. 317 backend tests, 258 Flutter. |
| 2026-08-19 | Speaking flow checked against the specified behaviour, end to end on the real stack. Confirmed as specified: per-word progression during the conversation, and a verdict of used + right meaning + understandable + no grammar breakdown → pass, wrong meaning or major grammar → that word alone returns to Speaking after the gap, with ordinary slips ignored (§32). Two defects found while checking. A learner answering in their own words was not counted when the tutor had used the word first — the anti-echo rule was too strict, and "I put a hook on the line to catch fish" scored nothing. And a word the session had recorded as used could still be failed at the end as *never used*; whether it was used is a fact recorded turn by turn with the sentence in hand, so the evidence now answers that question and the judge answers only how well. Judge stability measured over five runs on one transcript: four good words passed every time, the nonsense sentence failed every time. 317 backend tests. |
| 2026-08-19 | Speaking, from a real conversation on the device (ADR-040). The tutor asked "What do you want to become when you finish your English studies? Try to use 'hook' in your answer" — a career question with a piece of metal bolted on, and no honest answer. The tutor now works backwards from the word to a situation where a person would say it, and changes the subject when the current one cannot carry it; interests choose between the situations a word could live in rather than forcing one. The second failure was underneath: the learner's request to *change the topic so I can use hook* contained the word, and the turn-level check was a substring search, so the word counted as used and the tutor never asked again. Turn verdicts are now the AI's observation verified against what was actually said, kept on the session instead of recomputed. Verified on the same exchange against real Gemini: it now moves to fishing, and `hook` stays in the remaining list. 316 backend tests. |
| 2026-08-19 | Device build was silently running on the mock backend. `--dart-define` values reach Xcode through `Generated.xcconfig`, which Flutter rewrites whenever it builds — including when Xcode runs the build — so pressing Run dropped them and `WORDOS_MOCK` fell back to its default of true. The failure was invisible: the seeded demo account signed in and a real account was refused as "wrong email or password". The device defines now live in `ios/Flutter/Debug.xcconfig`, which is checked in and never regenerated (verified: a bare `flutter build ios --debug` with no defines at all still resolves them), and debug builds show which backend they are pointed at on the sign-in screen. Release is deliberately untouched — it sends tokens over TLS or not at all. |
| 2026-08-19 | Two device reports, same shape — something the learner had was quietly gone (ADR-039). Tapping a word gave a dictionary meaning instead of its meaning in the sentence, but only after re-levelling: the generation prompt demands a gloss for every word a learner might tap, while the re-telling prompt *paraphrased* that as "a full glossary" and returned 4 entries for a 27-word text. The rule is now written once and shared — measured after: 94 of 120 words glossed on generation, 68 of 105 on the A2 re-telling. And Speaking said five words were due but opened with none: a session's words were recovered from its items, and a conversation has no items, so leaving and returning gave an empty screen for ever. A session now records its own words, every caller reads that one record, and a session that can only come back empty is replaced rather than resumed — which healed the account that already had one. 220 backend tests. |
| 2026-08-19 | Level control, finished (ADR-038). Two complaints about one thing: a level changed inside a session did not show in Settings — the server had written it through all along, but the client renders levels from the cached profile and nothing re-read it — and Writing had no control at all. The client now refreshes the profile after any in-session change, so Settings and the Hub update the moment the learner looks. Writing gained the control, and its level is what the *rewrite* follows: the learner's own sentence comes back as a writer at their band would put it, verified against real Gemini at A2/B2/C1 on one sentence, and titled "Your sentence at B2" rather than anything resembling a correction. Writing re-levels like Speaking — no passage to re-tell, so nothing already done is regenerated. 255 Flutter tests, 311 backend tests. |
| 2026-08-19 | Developer Tools regained the time skip, this time against the real backend (ADR-037). Two days between skills is what the product measures and what makes it undemonstrable — five skills took over a week — so the Owner can now bring a learner's waiting skills forward from that learner's page. It moves scheduled dates only: passes, failures, attempts and sessions are untouched, because history is the evidence the experiment runs on. Written to the activity log as `ScheduleAdvanced`, Owner-only twice over (refused even for a learner's own schedule), and clamped to 1–30 days. Verified against the real stack: Writing answered `NO_WORDS_DUE`, one skip, and the same account started a four-word Writing session. |
| 2026-08-19 | Documentation consolidated into a final version. New `08-FINAL-SPECIFICATION.md` describes the finished application end to end — every section, flow, rule, level, AI behaviour and setting as it actually behaves — and the index now names it as authoritative for behaviour, with the plan and phase documents kept as the record of intent and build order. Stale claims removed: `CLAUDE.md` still called the backend and AI service "not started", the plan still marked the phases as in progress. The data model gained the columns added since it was written (`prompt_key`, the spelling hint ladder, `meaning_ar_normalized`, the second lexicon provenance), and the security document gained the four hostile-input properties the adversarial pass produced. |
| 2026-08-19 | Adversarial pass (ADR-036). The reported dashboard crash turned out to be two faults meeting: the custom-range dialog disposed its text controller while the dialog was still animating away — the next frame read a dead controller and closed the app — and a large enough number overflowed `AddDays` on the server, answering 500 and emptying the dashboard. Both fixed, plus two more found by sweeping every endpoint with hostile values: an unreadable query value was a 500 rather than a 400, and a pasted control character crashed any search because PostgreSQL refuses a NUL byte in text. Two mock/real divergences closed as well (the mock forked a second session instead of resuming, and accepted a search term the server caps). Three new suites whose assertion is that nothing was thrown; the hostile sweep now ends with zero unhandled exceptions in the server log. 260 Flutter tests, 303 backend tests. |
| 2026-08-18 | Language sweep (ADR-035). The line now falls at what the text *is*, not where it came from: the app talking to the learner follows the app language — instructions, evaluation feedback, every error message — while the material being learned stays English, passages, comprehension questions, options, the spoken conversation and the whole placement bank, whose difficulties are calibrated on the English wording. Three mechanisms: fixed instructions travel as a key rather than a sentence (`promptKey`), so the client says them in the learner's language while generated questions carry no key and are shown exactly as they arrived; the client sends `Accept-Language` and the API asks the AI for its feedback in that language (verified against real Gemini — the Arabic feedback quotes the English words in English rather than transliterating them); and failures are localised by their stable error code, falling back to the server's sentence for anything newer than the app. 193 Flutter tests, 293 backend tests. |
| 2026-08-18 | Settings and dictionary round. Signing out now always lands on the login form: the product tour is marked seen when a session ends, because a device that has held an account is not a first-time visitor and a learner signing out is usually trying to come back as somebody else. The dictionary was opened up on three fronts (ADR-033, ADR-034): the 167 closed-class words WordNet does not carry — pronouns, auxiliaries, articles, prepositions, conjunctions, question words — are authored and imported with them, ranked ahead of homographs so `are` is the verb and not a unit of area; irregular forms resolve (`went` → `go`, `children` → `child`); one-letter words are matched exactly; and a query typed in Arabic now searches the meanings and returns the English words, folding diacritics off both sides against a stored, trigram-indexed column. Verified on the simulator against the real stack. 188 Flutter tests, 278 backend tests. |
| 2026-08-18 | Spelling section review. The single clue plus a masked-letters hint became one ladder of five rungs — dictionary definition → simplified definition → synonym → translation → number of letters — entered at the rung that suits the learner (C1 at the top, B2 one down, B1 at the synonym, A1/A2 at the translation) and stepped down one press at a time (ADR-032). Built server-side, because it depends on the level and on what the lexicon holds; empty rungs are skipped and a duplicate simplified definition is dropped, so every press changes something. The masked-letters hint is gone: it spelled out part of the word the task was asking for. `hint` left the wire for `hints`, the `Hint` column was dropped, and `SpellingClueKind` is now ordered hardest-first so the enum *is* the ladder. Verified on the simulator against the real stack: a B1 learner walks synonym → Arabic meaning → letter count in two presses. 186 Flutter tests, 267 backend tests. |
| 2026-08-18 | Speaking section review. The passive briefing (Part 2 §26) became an active warm-up: each word of the session with four meanings, shuffled server-side, marked server-side, and recorded nowhere — a miss returns to the back of the queue and the loop ends only when every word has been recalled once. It measures nothing: no word passes or fails on it and no level moves, pinned by a test that snapshots the word, its events and the level across a miss. No words means no warm-up, straight into the conversation. Speaking's level became changeable *during* the conversation — there is no passage to replace, so the new level is simply what the next turn is generated at — and, like Reading's, it writes through to Settings but never to the validated level. The header was already shared with Reading and Listening. 181 Flutter tests, 263 backend tests. |
| 2026-08-18 | Listening section review. Four of the seven points were already true because Reading and Listening share one screen — the enlarged header, the level control, the write-through to Settings and the LTR passage all applied to Listening the moment they applied to Reading; pinned with tests rather than assumed. Three were real: audio now stops at every section change (leaving the passage, moving between questions, completing, re-levelling) — for Listening a script still playing over the questions was handing out answers; the recording is handed back in the result area as a play/stop/slow control, ordered answers → audio → transcript, and deliberately never autoplaying while the learner reads their score; and the fallback transcript shown when a device cannot speak was the last English string still inheriting the Arabic interface's direction. 181 Flutter tests. |
| 2026-08-18 | Reading section review. Header and level badge enlarged; English content pinned LTR so it stops inheriting the Arabic interface's direction (measured before the fix: `TextDirection.rtl`, right-aligned) — fixed centrally via `EnglishText`, since the same defect ran through the dictionary, review and word detail. The generator now glosses its own passage (ADR-029): 81 words in a typical text, each with the meaning it carries *in that sentence* and its part of speech, so a tap is instant and unambiguous. A passage can be re-told at another level before the questions begin (ADR-030) — same story, new language, questions regenerated, choice written through to Settings but never to the validated level. Comprehension questions are asked once; only words come back (ADR-031). Verified end to end against real Gemini. 175 Flutter tests, 259 backend tests. |
| 2026-08-18 | Device-testing round. Four reported bugs fixed: placement never sent the `SPOKEN` wire type so Speaking was answered in a text box (ADR-022's sibling — the client had the microphone all along); the "Sign in" link on register used `pop()` and did nothing when arrived at from the tour; sign out was buried below four sections of Settings; and voice selection ranked *unknown* voices above the platform default, so iOS novelty voices (Zarvox, Trinoids) could become the tutor. Two crashes found while fixing them: `ref.read` in `SpokenAnswerField.dispose` threw during tree finalisation and killed any navigation happening at that moment — which is what actually made sign-out do nothing — and sign-out awaited the server before clearing the session, so a slow network froze the button. Then three product changes: the AI now places Speaking and Writing (ADR-026), the ladder opens easy and never runs uphill (ADR-027), and the microphone became push-to-talk (ADR-028). Placement results reworded as estimates, said before the bands are shown. 169 Flutter tests, 252 backend tests. |
| 2026-08-17 | Part 3 of the specification. Append-only activity log (ADR-025) written at registration, sign-in, word added, session start/complete, review and placement completion — `activeToday`, the daily series and the sign-in count now come from it rather than from `LastLoginAt`, which could only ever record one moment. Admin learner list gained search, pagination and a Today/5/10/custom window; the overview honours the same window, scoping words and sessions but deliberately *not* pipeline completion. New Owner views: a learner's vocabulary filtered by pipeline state, one word's whole journey from the event log, and the placement evidence behind a level with initial-vs-current bands. Final QA sweep as a test: every learner screen in both languages, both themes and two device sizes, asserting on layout exceptions rather than eyeballing. 149 → 158 Flutter tests, 233 → 245 backend tests, 8 integration journeys green. |
| 2026-08-17 | Part 2 of the three-part specification. Weekly review auto-advances on selection (§9–§12). Reading rebuilt: every word tappable, server-resolved definitions with add-to-pipeline (`/api/words/define`, ADR-022), target words underlined at full strength and never defined mid-session, passage typography. **Bug found:** the real backend never sent `content.targetSpans`, so no target word had ever been underlined outside the mock. Listening: the clip plays on its own and the same control stops it, state read from `SpeechService`; passage length now scales with level and is shorter for listening. Speaking opens on a briefing of its words (§26). Spelling: letter pool padded with decoys, three-tier clue by level with synonyms from shared glosses (ADR-024), per-letter undo. Vocabulary became **My Words** — one searchable list, no pipeline-state tabs, server-side search and real pagination (§42–§46, Part 3 §37). Practice sessions for an empty pipeline (§5, ADR-023). 135 → 149 Flutter tests, 204 → 233 backend tests, 7 integration journeys green on the simulator. **Second bug found:** `/api/words` returned 400 without explicit `page`/`pageSize` once paging was added — non-nullable minimal-API query parameters are required, not defaulted. |
| 2026-08-12 | Note: the in-app iOS Simulator control tool refuses to attach on this machine (it reports Xcode as "not selected" even though `xcode-select -p` is correct), so on-device verification used `flutter run` + `xcrun simctl io … screenshot`, and flow verification used driver-level widget tests instead of tapping through the simulator. |
| 2026-08-15 | Sessions and weekly review on the real backend. `SkillSession`/`SessionItem` with the requeue loop, `SessionEndpoints` (start/answer/writing/speaking/complete/abandon/resume), `WeeklyReview` as measurement-only, two EF migrations applied to PostgreSQL. ADR-015 (the AI observes, the backend decides), ADR-016 (speaking passes on substantial use), ADR-017 (spelling borrows the Reading level). 144 → 161 backend tests. Three bugs found: EF discovered `Queue`/`CurrentItem` as navigations and added a shadow FK; the resilience decorator depended on the concrete HTTP class so its fallback could not be tested; context JSON was serialized PascalCase into an otherwise camelCase response. |
| 2026-08-15 | Closed the last two lifecycle gaps: the system-validated level engine (promote/demote/hold over an evidence window) and archiving on proven level growth, both per ADR-013. Level history — manual vs system-validated — now shows in the Owner drill-down. 99 → 118 tests. |
| 2026-08-15 | Demo review round 1, part 2. Rebuilt the five skill sessions: Reading with exactly five comprehension items then context-inference questions, Listening audio-only, Speaking as a reactive conversation, Writing as "use this word", Spelling per `MVP Core.txt` §33–34 with synonym clues and hints. Added the in-session learning loop (requeue, retry cap, first-attempt-only pass). Vocabulary pipeline keyed on WordNet sense id with prefix autocomplete and server-side re-resolution. Security audit + `07-SECURITY.md`. Resilience pass. 61 → 99 tests. |
| 2026-08-15 | Demo review round 1, part 1. Arabic default (ADR-010), welcome screen, interests editor, adaptive placement (ADR-009 + `06-PLACEMENT-ALGORITHM.md`), Spelling de-levelled (ADR-008), Owner dashboard, vocabulary validation and duplicates (ADR-012). Backend confirmed as C# + PostgreSQL (ADR-011). |
| 2026-08-12 | Read all 7 requirement documents. Created `docs/` (plan, phases, ADRs, data model, API contract) and `CLAUDE.md`. Built Phases 1–4: full Flutter app with design system, bilingual RTL UI, contract layer, mock backend engine, all five skill sessions, weekly review, settings. Analyzer clean, 15 tests green, runs on iOS Simulator. |
