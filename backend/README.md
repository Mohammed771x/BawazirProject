# WordOS — Backend (ASP.NET Core)

The brain of WordOS. Owns every state transition, every rule, and the security
boundary. See [`../docs/07-SECURITY.md`](../docs/07-SECURITY.md) — the backend
enforces everything there; the Flutter client is never the control.

## Environment

Verified on 2026-08-15:

```
.NET SDK 10.0.400 (arm64, /usr/local/share/dotnet)
```

`dotnet` is **not on the default PATH** on this machine. Either add it to your
shell profile or prefix commands:

```bash
export PATH="/usr/local/share/dotnet:$PATH"
```

## Layout

A modular monolith with a strict dependency direction — the domain depends on
nothing, and infrastructure is replaceable.

```
Api  →  Infrastructure  →  Application  →  Domain
```

| Project | Contains |
|---|---|
| `src/WordOs.Domain` | Entities and the pure rules: pipeline state machine, level engine, archiving. No framework dependencies at all. |
| `src/WordOs.Application` | Use cases and the interfaces infrastructure implements. |
| `src/WordOs.Infrastructure` | EF Core / Npgsql, repositories, external clients. |
| `src/WordOs.Api` | Controllers, authentication, authorization, rate limiting. |
| `tests/WordOs.Domain.Tests` | The behavioural specification (see below). |
| `tests/WordOs.Api.Tests` | Endpoint and authorization integration tests. |

## Database

PostgreSQL **17.11**, database `wordos_dev`, with **two roles** — the split is a
security requirement, not a convention (`docs/07-SECURITY.md` §10):

| Role | Used by | Privileges |
|---|---|---|
| `wordos_migrator` | `dotnet ef` | Owns the schema. DDL. |
| `wordos_app` | the running API | `SELECT/INSERT/UPDATE/DELETE` only — **no DDL** |

That means a SQL-injection bug in the API could not drop a table even if one
existed. Verified: `wordos_app` gets `permission denied for schema public` on
`CREATE TABLE`, and `must be owner of table` on `DROP TABLE`.

> The local `wordos_migrator` additionally has `CREATEDB` so the integration
> test fixture can create and drop its own throwaway database. **The production
> migration role must not have it** — it applies migrations to a database that
> already exists.

### Migrations

The schema is created **only** by EF Core migrations, never by hand:

```bash
export PATH="/usr/local/share/dotnet:$HOME/.dotnet/tools:$PATH"
dotnet ef migrations add <Name> \
  --project src/WordOs.Infrastructure --startup-project src/WordOs.Api \
  --output-dir Persistence/Migrations
dotnet ef database update \
  --project src/WordOs.Infrastructure --startup-project src/WordOs.Api
```

Applied so far: `20260815100443_InitialSchema` — 8 tables, the
`(UserId, SenseId)` unique index that *is* the duplicate rule, the
`DailyTargetWords BETWEEN 5 AND 15` check constraint, and a `text_pattern_ops`
index on the lexicon so `bo%` autocomplete is an index scan.

## Running

```bash
export PATH="/usr/local/share/dotnet:$PATH"
dotnet build
dotnet test
dotnet run --project src/WordOs.Api
```

Health probes — deliberately separate, so "the app is up" and "the app can
serve" stay distinguishable:

```bash
curl localhost:5099/health/live    # {"status":"ok"}
curl localhost:5099/health/ready   # {"status":"ok","database":"connected"}
```

## The tests are a specification, not a safety net

`tests/WordOs.Domain.Tests` is a direct port of the behavioural specs written
against the Flutter mock (`mobile/test/*.dart`). They describe the **product**,
not this implementation. If one fails, the backend is not doing what the source
documents require — check `docs/03-DECISIONS.md` before changing a test.

Ported so far:

| xUnit | From | Covers |
|---|---|---|
| `WordPipelineTests` | `word_pipeline_test.dart` | The nine rules: first-skill-only entry, spaced gaps, partial failure retention (R5), maturity, archiving as a state change (R8) |
| `LevelProgressionTests` | `level_progression_test.dart` | System-validated levels (R6), promote/demote/hold, the evidence window, the weakest-skill proven level and archiving (ADR-013) |

## Security — what is implemented

`../docs/07-SECURITY.md` is the requirement set. Implemented and **tested
against the running API** so far:

| Requirement | Implementation | Test |
|---|---|---|
| Password hashing | Argon2id, 19 MiB / t=2 / p=1, parameters embedded in the hash | plaintext never stored or returned |
| Tokens | JWT 15 min + rotating refresh, stored SHA-256 hashed | forged token → 401 |
| Refresh reuse | Replaying a spent token revokes the whole rotation family | family revoked, not just the token |
| Logout | Revokes every outstanding refresh token | reuse after logout → 401 |
| Account enumeration | Byte-identical response for unknown email and wrong password; dummy hash keeps the timing flat | responses compared |
| `/admin/*` | Policy on the group **plus** an explicit check in every handler | learner → 403, owner → 200 |
| Privilege escalation | `role` in the request body is ignored; no client path to Owner | still `USER`, admin → 403 |
| IDOR | Every query scoped by the id in the signed token, never from the request | two learners cannot see each other's words |
| Trusting the client | `POST /words` re-resolves the sense; the lexicon row is stored, not the request | forged level/meaning discarded |
| SQL injection | EF Core parameterised throughout, no `FromSqlRaw` | `'; DROP TABLE words;--` is inert |
| Input validation | DataAnnotations on every DTO + schema-level caps | invalid email/short password → 400 |
| Rate limiting | Auth by IP, everything else by user id | policies applied per endpoint |
| Error responses | `{ error: { code, message } }` | no stack trace, SQL or connection string |
| Database privileges | `wordos_app` has DML only, no DDL | verified directly against PostgreSQL |
| CORS | Closed unless `Cors:AllowedOrigins` is set; no `AllowAnyOrigin` | — |

> **A bug worth remembering.** On a positional C# record, `[Required]` binds to
> the constructor *parameter*, not the property, and
> `Validator.TryValidateObject` never sees it. Every annotation on every request
> DTO was silently inert until they were rewritten as `[property: Required]`.
> Validation that does not run looks exactly like validation that passes — only
> a test that sends bad input can tell the difference.

Still specified but not yet built: correlation ids in error responses, a
daily AI-spend cap, and dependency-vulnerability scanning in CI.

## Secrets

**Never** commit a secret, and never send one to the client. Local development
uses user-secrets; deployment uses environment variables:

```bash
dotnet user-secrets init --project src/WordOs.Api
dotnet user-secrets set "ConnectionStrings:WordOs" "..." --project src/WordOs.Api
```

`.gitignore` excludes `appsettings.Development.json`, `.env` and `secrets.json`.
