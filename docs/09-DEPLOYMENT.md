# 09 — Deployment

Getting WordOS onto the internet: **Neon** for PostgreSQL, **Render** for the
service, and a keep-alive so the free instance does not sleep.

Written for someone who has used neither before. Every step says what to click
and what you should see.

> **Sizing.** Measured, not guessed. The published API uses **160 MB** idle and
> **192 MB** under a 100-concurrent burst; the AI service with one worker adds
> about **40 MB**. Together **~232 MB of Render's 512 MB**. The database is
> **187 MB**, of which 175 MB is the lexicon, against Neon's free allowance.
> Both fit with room to spare (ADR-062).

---

## What goes where

```
        Learner's phone
              │  HTTPS
              ▼
   ┌──────────────────────────┐
   │  Render — one container  │
   │                          │
   │   WordOS API  :$PORT ────┼──────► Neon (PostgreSQL)
   │        │                 │
   │        ▼ 127.0.0.1:8099  │
   │   AI service ────────────┼──────► Gemini
   └──────────────────────────┘
```

Both services share one container **on purpose** (ADR-062):

* Render's free plan is measured in **instance-hours**. Two always-on services
  burn the month's allowance in half a month; one does not.
* The AI service holds the Gemini key. As its own Render service it would get a
  public URL guarded only by a token. Here it binds to loopback and is not
  reachable from outside the container at all.
* One cold start, not two.

---

## 1 · The database (Neon)

Neon is managed PostgreSQL. The free plan is enough for this: the whole database
is 187 MB.

1. Go to **neon.tech** and sign up (GitHub login is quickest).
2. **Create a project.** Name it `wordos`. Pick the region closest to your
   learners — for Yemen that is usually **Europe (Frankfurt)**; the AI call takes
   seconds regardless, so this is not worth agonising over.
3. PostgreSQL version: **17**.
4. When it finishes, Neon shows a **connection string**. It looks like:

   ```
   postgresql://wordos_owner:AbC123@ep-cool-name-123456.eu-central-1.aws.neon.tech/wordos?sslmode=require
   ```

   Copy it somewhere safe. **This is a password — it never goes in the
   repository.**

### Turn it into the form .NET wants

Npgsql does not read that URL. Convert it by hand:

```
Host=ep-cool-name-123456.eu-central-1.aws.neon.tech;Database=wordos;Username=wordos_owner;Password=AbC123;SSL Mode=Require;Trust Server Certificate=true
```

Keep this string. It is `ConnectionStrings__WordOs` in step 3.

### Two roles, not one

The connection string Neon gives you owns the database. **The running service
must not use it.** WordOS connects as a role with no power to change the schema,
so a flaw in the API cannot drop a table (`docs/07-SECURITY.md` §10).

Create the second role once, with the owner string:

```bash
psql "postgresql://wordos_owner:…@ep-….neon.tech/wordos?sslmode=require" <<'SQL'
create role wordos_app with login password 'pick-a-long-random-one';
grant connect on database wordos to wordos_app;
grant usage on schema public to wordos_app;

-- Data, not structure.
grant select, insert, update, delete on all tables in schema public to wordos_app;
grant usage, select on all sequences in schema public to wordos_app;

-- And the same for tables a future migration adds.
alter default privileges in schema public
  grant select, insert, update, delete on tables to wordos_app;
alter default privileges in schema public
  grant usage, select on sequences to wordos_app;
SQL
```

Now you have two strings. Keep them apart:

| | Used by | Can change the schema? |
|---|---|---|
| **owner** (`wordos_owner`) | you, from your Mac, for migrations | yes |
| **app** (`wordos_app`) | Render | **no** |

### Create the schema and load the dictionary

Both from your Mac, once — with the **owner** string. Nothing else sets up
234,359 lexicon rows.

```bash
cd backend
export NEON_OWNER="Host=ep-….neon.tech;Database=wordos;Username=wordos_owner;Password=…;SSL Mode=Require;Trust Server Certificate=true"
ConnectionStrings__WordOsMigrations="$NEON_OWNER" dotnet ef database update \
  --project src/WordOs.Infrastructure --startup-project src/WordOs.Api
```

> **`WordOsMigrations`, not `WordOs`.** This document said `WordOs` and was
> wrong in a way that succeeds. `WordOsDbContextFactory` reads
> `ConnectionStrings:WordOsMigrations` **first**, and on any machine that has
> ever run the backend locally that key is already in user-secrets, pointing at
> `wordos_dev`. The local secret wins, `dotnet ef` migrates the local database,
> and prints `Done.` Production never gets the table, and the first symptom is
> a `500` from an endpoint that had just deployed fine.
>
> `export NEON_OWNER=…` also lasts only as long as that terminal. A new window
> makes `"$NEON_OWNER"` an empty string, and then the local secret wins again
> for the same reason.

The first line of the output now names what it is about to change:

```
Migrating ep-cool-name-123456.eu-central-1.aws.neon.tech/wordos as wordos_owner
```

**Read it.** Three of the four ways this goes wrong are visible in that one
line (ADR-079):

| It says | Meaning |
|---|---|
| `127.0.0.1/wordos_dev` | the connection string never reached the command — the local user-secret won, and what follows is about your laptop |
| `…/neondb` | Neon's *default* database, not this one. It is empty, so the migration will replay `InitialSchema` and succeed, against nothing |
| `as neondb_owner` | the cloud owner role does not own these tables — see below |

Then each migration is named as it applies, ending in `Done.` **If a deployed
database replays `InitialSchema`, it is the wrong database** — stop.

### The simple way: connect as `wordos_migrator` itself (since 2026-09-28)

`wordos_migrator` owns every table, so a migration run **as** it needs no
borrowed role, no grant to hand back, and no `Options`. The only reason the
workaround below existed is that nobody knew its password. Now the owner does —
kept outside this repository. To set a new one, from Neon → SQL Editor →
database `wordos` (as `neondb_owner`, which holds admin over the role):

```bash
export MIGRATOR_PW=$(openssl rand -hex 24) && echo "$MIGRATOR_PW"   # save it
```

```sql
ALTER ROLE wordos_migrator WITH PASSWORD '<the value above>';
```

Hex, deliberately: a password with `@ : / # %` breaks the `postgresql://` URL
form, and one copied from the console with a trailing space is accepted by
Npgsql (which trims) and refused by `psql` (which does not) — both cost an
evening on 2026-09-28. Then:

```bash
cd backend && ConnectionStrings__WordOsMigrations="Host=ep-….neon.tech;Database=wordos;Username=wordos_migrator;Password=$MIGRATOR_PW;SSL Mode=Require" \
  dotnet ef database update --project src/WordOs.Infrastructure --startup-project src/WordOs.Api
```

Tables it creates are owned by the right role from the start. The grant to
`wordos_app` (below) is still needed for a **new table**.

Never change `wordos_app`'s password for this — Render serves learners with it.

### Three things the host and role must be (the older route)

**Drop `-pooler` from the hostname.** Neon's connect dialog hands out the pooled
host by default; PgBouncer refuses the startup parameter the next step needs
(`08P01: unsupported startup parameter in options: role`), and DDL belongs on a
direct connection regardless.

**Run as `wordos_migrator`, not `neondb_owner`.** It owns the tables, and a
foreign key to `users` needs `REFERENCES` on `users`, which only its owner has —
otherwise `42501: permission denied for table users`. If you do not have that
role's password, `neondb_owner` holds `admin_option` over it and can borrow it
for one command:

```bash
psql "$NEON_URL" -c "GRANT wordos_migrator TO neondb_owner WITH INHERIT TRUE, SET TRUE;"

# …then add  Options=-c role=wordos_migrator  to the connection string and
# run `dotnet ef database update`…

# Hand it straight back. GRANTED BY is not optional: without it this revokes
# the *original* grant and quietly widens what the next person inherits.
psql "$NEON_URL" -c "REVOKE wordos_migrator FROM neondb_owner GRANTED BY neondb_owner;"
```

**Grant the new tables to `wordos_app`.** This one fails at *runtime*, not here,
which is what makes it dangerous. `ALTER DEFAULT PRIVILEGES` above was run as
`neondb_owner`, and default privileges apply only to the role that creates the
object — so a table created by `wordos_migrator` reaches `wordos_app` with no
privileges at all. The migration succeeds, the deploy succeeds, and the endpoint
answers `500` the first time a learner touches it.

```bash
psql "$NEON_URL" -c "SET ROLE wordos_migrator; GRANT SELECT, INSERT, UPDATE, DELETE ON <new_table> TO wordos_app;"
```

Verify rather than assume — the ACL must match every other table:

```bash
psql "$NEON_URL" -tAc "select relacl from pg_class where relname='<new_table>';"
# {wordos_migrator=arwdDxtm/wordos_migrator,wordos_app=arwd/wordos_migrator}
```

### Then check it with input that touches what you added

A health check will not do it, and neither will any request that stops short of
the new table. After adding `password_reset_codes`, a reset request for an
address that *does not exist* returns `202` without ever reaching it — the same
answer as success. Use a **registered** email, then look for the row.

> **Every later release that adds a migration needs this command run again,
> before you deploy** — with `ConnectionStrings__WordOsMigrations`, and with the
> first output line checked. The service cannot do it for itself, deliberately: it
> connects as a role with no DDL rights, and giving it any would mean handing
> the internet-facing process a credential that can drop every table (ADR-062).
> Run the command, watch it succeed, then push.

Then the lexicon — the tables exist but are empty:

```bash
# From the local database, dictionary only.
PGPASSWORD=… pg_dump -h 127.0.0.1 -U wordos_migrator -d wordos_dev \
  --data-only -t lexicon_entries -f lexicon.sql

# Into Neon. Use the URL Neon gave you, not the .NET form.
psql "postgresql://wordos_owner:…@ep-….neon.tech/wordos?sslmode=require" -f lexicon.sql
```

Check it landed:

```bash
psql "postgresql://…" -c "select count(*) from lexicon_entries;"
#  234359
```

> A wrong number here means every word search returns nothing, and the app looks
> broken in a way that has nothing to do with the app.

---

## 2 · Secrets

Four values. Generate the two that are yours to invent:

```bash
openssl rand -base64 48   # Jwt__SigningKey
openssl rand -base64 32   # AI_SERVICE_TOKEN
```

| Name | What it is |
|---|---|
| `ConnectionStrings__WordOs` | the **app** role's string, .NET form, from step 1 |
| `Jwt__SigningKey` | the 48-byte value above — changing it signs everyone out |
| `GEMINI_API_KEY` | from Google AI Studio |
| `AI_SERVICE_TOKEN` | the 32-byte value above — the AI service checks this |
| `AiService__Token` | **the same value again** — the API sends it |
| `Email__ApiKey` | your Brevo key — password reset email (ADR-078) |
| `Email__FromAddress` | the sender address you **verified** in Brevo |

**None of these belong in Git.** They are typed into Render and nowhere else.

`AI_SERVICE_TOKEN` and `AiService__Token` are **the same secret seen from both
ends**: Python checks the header, .NET sends it. Set them to different values
and every AI call is refused.

Note the section name is `AiService`, not `Ai` — a key that binds to nothing
leaves the token empty, and nothing at startup says so; the first symptom is
every lesson arriving as fallback content.

It matters even on loopback: it stops anything else in the container from
spending your Gemini quota, and it is what protects you the day the two services
are split apart again.

---

### Email, for forgotten passwords

A learner who reinstalls the app and cannot remember their password has no way
back in without this (ADR-078). Brevo sends the code; its free plan is 300
messages a day, which is far more resets than a cohort will ever ask for.

1. Sign up at **brevo.com**.
2. **Senders, Domains & Dedicated IPs → Senders → Add a sender.** Use an
   address you can read email at — an ordinary Gmail account is fine. Brevo
   emails it a confirmation link; click it.

   > This is the step that makes Brevo the right choice here: most
   > transactional providers verify a whole **domain**, which means owning one.
   > Brevo will verify a single address.

3. **SMTP & API → API Keys → Generate a new API key.** Copy it once — it is
   shown once.

`Email__FromAddress` must be **exactly** the address you verified. An
unverified sender is accepted by the API and then quietly not delivered, which
looks identical to the feature not working.

Leave both unset and nothing breaks at startup — but every reset request fails,
and the log says why. Password reset is the only email WordOS sends.

---

## 3 · The service (Render)

1. Push this repository to GitHub if it is not there already.
2. Go to **render.com**, sign up, and connect your GitHub account.
3. **New → Web Service**, and pick this repository.
4. Fill in:

   | Field | Value |
   |---|---|
   | Name | `wordos-api` |
   | Language / Runtime | **Docker** |
   | Dockerfile path | `./Dockerfile` |
   | Instance type | **Free** |
   | Health check path | `/health/live` |

   `/health/live`, **not** `/health/ready`. Render probes this path on its own
   schedule, for ever. `/health/ready` opens a database connection, and a
   connection every few minutes is what stops Neon ever suspending itself —
   the single most expensive line in this document (ADR-077, and §4 below).

   Render finds the `Dockerfile` at the repository root. There is nothing to
   build by hand.

5. **Environment → Add Environment Variable**, once per row:

   | Key | Value |
   |---|---|
   | `ConnectionStrings__WordOs` | the **app** string — `wordos_app`, never the owner |
   | `Jwt__SigningKey` | your 48-byte value |
   | `Jwt__Issuer` | `wordos` |
   | `Jwt__Audience` | `wordos-app` |
   | `GEMINI_API_KEY` | your key |
   | `AI_SERVICE_TOKEN` | your 32-byte value |
   | `AiService__Token` | **the same 32-byte value** |
   | `Capacity__DatabaseConnections` | `10` |
   | `Email__ApiKey` | your Brevo key |
   | `Email__FromAddress` | the address verified in Brevo |
   | `Email__FromName` | `WordOS` |

   `Capacity__DatabaseConnections` is **10**, not the default 40: Neon's free
   plan allows far fewer connections than a server you own, and a pool that asks
   for more than exists fails at the far end instead of queueing politely
   (ADR-051).

6. **Create Web Service.** The first build takes 5–10 minutes — it restores
   NuGet packages, publishes the API, and installs Python.

7. When it says **Live**, check it:

   ```bash
   curl https://wordos-api.onrender.com/health/ready
   # {"status":"ok","database":"connected","aiSlotsFree":24}
   ```

   `"database":"connected"` is the part that matters. Anything else means the
   connection string is wrong — the logs tab will say how.

---

## 4 · Keeping it awake

Render's free plan stops a service after roughly **15 minutes** with no traffic,
and the next request waits **30–60 seconds** while it starts. For a learner
opening the app that is indistinguishable from it being broken.

A monitor that requests a cheap endpoint every few minutes prevents it:

```
https://wordos-api.onrender.com/health/live
```

Use **cron-job.org**, **UptimeRobot** or **Better Stack** — all have free plans:

1. Add an **HTTP(s) monitor**.
2. URL: the address above.
3. Interval: **10 minutes**.
4. Expect: **200**.

### `/health/live`, and why this paragraph used to say the opposite

An earlier version of this document recommended `/health/ready` at a
**5-minute** interval, on the reasoning that it touches the database and so the
one ping keeps Neon awake as well.

That reasoning was right about the mechanism and wrong about wanting it.

Neon's free plan bills **compute-hours** — the time the database is *awake* —
not queries. It suspends itself after **5 minutes** idle. A probe every five
minutes against a five-minute suspend arrives exactly when it was about to
sleep, every time, for ever. And two probes were doing it: this monitor, and
Render's own health check, which §3 also pointed at `/health/ready`.

Measured on the live instance, 1–14 September 2026:

```
0.25 CU (the floor) × 324 hours awake = 81 compute-hours
```

**80 of the month's 100 compute-hours, with no learner traffic in them at all.**
At that rate the allowance runs out around the 17th of every month, and Neon
then suspends the compute until the next billing cycle — the app simply stops.

`/health/live` returns without a connection, a query or a `DbContext`. It keeps
**Render** awake, which is all this monitor was ever for, and leaves Neon free
to sleep whenever no learner is using the app.

### Watching the database as well

Worth having — just not sixty times an hour. Add a **second** monitor:

| | URL | Interval | Keeps awake |
|---|---|---|---|
| Keep-alive | `/health/live` | 10 min | Render only |
| Database alarm | `/health/ready` | **60 min** | Neon, briefly |

One wake an hour against a five-minute suspend is about **15 compute-hours a
month** — 15% of the allowance, against 180% before.

`/health/ready` is also throttled at the service itself (ADR-077): it asks the
database at most once per `Capacity__ReadinessDatabaseCheckSeconds`, one hour by
default, and returns the cached verdict in between. So a probe misconfigured
back to 5 minutes can no longer produce this bill. Its answer carries
`databaseCheckedSecondsAgo` so nobody mistakes a cached *ok* for a fresh one:

```json
{"status":"ok","database":"connected","databaseCheckedSecondsAgo":1847,"aiSlotsFree":24}
```

Set `Capacity__ReadinessDatabaseCheckSeconds=0` to ask every time. That is the
right setting on a server you own, where uptime is already paid for.

### Three honest caveats

* **It costs instance-hours.** Render's free allowance is a fixed number of
  running hours per month per account. Kept awake continuously, one service uses
  about 720 of them. That fits — *one* service. It is the reason both processes
  share this container, and the reason not to add a second free service beside
  it.
* **Neon still sleeps, and should.** The first query after a suspend pays well
  under a second to wake it. A learner does not notice that. A learner does
  notice the app being down from the 17th.
* **Check for stray branches.** In the Neon console, every branch has its own
  compute endpoint burning its own hours. A forgotten test branch doubles this
  bill silently. Keep `main` and delete the rest.

---

## 4½ · Switching production to the Wiktionary dictionary

The rebuilt dictionary (ADR-096, ADR-097) lives in the **same table** as the
old one, told apart by `Edition`, and search reads only the edition named by
`WordOs:LexiconEdition`. That design makes the switch — and the rollback — one
setting. It also makes the **order** of the steps the whole difficulty, because
the code's default is `wiktionary` and production has none of those rows until
they are loaded:

| If this happens first… | …the learner sees |
|---|---|
| code pushed before the migrations | every lexicon and word query fails — the columns it reads do not exist yet |
| code pushed before the data, default setting | **every word search returns nothing** |
| data loaded before the code | the *old* code does not filter by edition, so both dictionaries appear mixed in one result list |

The order below is the one where every intermediate state is the old app
behaving exactly as it did.

**When:** after the monthly compute allowance resets. Loading 200 k rows and
their indexes lets Neon autoscale past 0.25 CU for a few minutes — affordable on
a fresh month, pointless to risk at the end of one (ADR-077).

**Size:** the dictionary adds about 160 MB; production goes from ~120 MB to
~280–300 MB of the 512 MB allowance.

### 1 · Migrate (additive — the live service is unaffected)

Two migrations, both **add a column** and create no table — so the grant step of
ADR-079 §5 does not apply this time. Run them as `wordos_migrator`, over the
**direct** host, exactly as §1 describes:

```bash
psql "$NEON_URL" -c "GRANT wordos_migrator TO neondb_owner WITH INHERIT TRUE, SET TRUE;"

cd backend && ConnectionStrings__WordOsMigrations="Host=ep-….neon.tech;Database=wordos;Username=neondb_owner;Password=…;SSL Mode=VerifyFull;Channel Binding=Require;Options=-c role=wordos_migrator" \
  dotnet ef database update --project src/WordOs.Infrastructure --startup-project src/WordOs.Api

psql "$NEON_URL" -c "REVOKE wordos_migrator FROM neondb_owner GRANTED BY neondb_owner;"
```

Expect `20260917141502_LexiconEditions` and `20260917184736_WeeklyReviewRetiresOnRecall`,
then `Done.` — two migrations, not twenty-three. The running (old) code never
reads these columns, so learners notice nothing.

```bash
psql "$NEON_URL" -tAc 'select "Edition", count(*) from lexicon_entries group by 1;'
# oewn-awn|234359        ← every existing row, labelled by the column default
```

### 2 · Deploy the code

Nothing to pin: since ADR-103 the code's default *is* `oewn-awn`, and only local
development asks for the new edition. So the new code, once pushed, searches
exactly the dictionary production already has. (If `WordOs__LexiconEdition` was
ever set in Render, make sure it is absent or `oewn-awn` before this step.)

### 3 · Load the Wiktionary rows

Export from the local database, which holds the built edition. An explicit
column list on both ends, so a column-order difference cannot shift data into
the wrong field:

```bash
COLS='"SenseId","Text","TextNormalized","Lemma","PartOfSpeech","DefinitionEn","MeaningAr","CefrLevel","FrequencyRank","SourceFlags","UpdatedAt","MeaningArNormalized","Edition"'

psql -h 127.0.0.1 -U wordos_migrator -d wordos_dev \
  -c "\copy (select $COLS from lexicon_entries where \"Edition\"='wiktionary') to 'wiktionary.csv' csv header"

psql "$NEON_URL" -c "SET ROLE wordos_migrator;" \
  -c "\copy lexicon_entries ($COLS) from 'wiktionary.csv' csv header"
```

(`SET ROLE` needs the temporary grant from step 1 — take it again and hand it
back afterwards the same way.)

Sense ids are prefixed `wikt:`, so they cannot collide with the old edition's.

```bash
psql "$NEON_URL" -tAc 'select "Edition", count(*) from lexicon_entries group by 1 order by 1;'
# oewn-awn|234359
# wiktionary|200369      ← must be exactly this
```

### 4 · Switch

Render → Environment → **add** `WordOs__LexiconEdition` = **`wiktionary`** → save. The service
restarts and search reads the new dictionary. Check it with a **registered**
account (ADR-079 — a probe that stops short of the thing you changed proves
nothing): search `sell`, expect **يَبِيعُ**, not أَقْنَعَ بِـ.

**Rollback** is deleting that variable (or setting it to `oewn-awn`). Nothing is deleted, and
words learners added under either edition keep resolving under both, because
reading a word they already own is deliberately edition-blind.

### 5 · Only then, publish the app

The backend must be fully switched first. The new app is compatible with the
new backend; the **old** app on learners' phones is compatible with it too — the
contract changes were additive or kept the old fields (`spelling` stays at 0 of
0, `kind` is still sent beside `message`), checked 2026-09-28 against the
committed client. Bump `version:` in `mobile/pubspec.yaml` before building, so a
bug report says which build it came from.

---

## 5 · Point the app at it

In `mobile/ios/Flutter/Debug.xcconfig`, the `DART_DEFINES` line carries the
address. For a release build, pass them on the command line:

```bash
cd mobile
flutter build ipa \
  --dart-define=WORDOS_MOCK=false \
  --dart-define=WORDOS_API_BASE_URL=https://wordos-api.onrender.com/api
```

The app **refuses plain HTTP** for anything but loopback, so the URL must be
`https://`. Render provides the certificate.

---

## 6 · The first Owner

There is deliberately no client-reachable path to creating an Owner
(`docs/07-SECURITY.md` §3). Register normally in the app, then promote:

```bash
psql "postgresql://…neon.tech/wordos?sslmode=require" \
  -c "update users set \"Role\"='Owner' where \"Email\"='you@example.com';"
```

Sign out and in again — the role is carried in the token.

---

## What will run out first

Not the server. In order:

1. **Gemini's quota.** 100 learners × ~3 calls = ~300 calls a day. Check the
   limit on your key and divide by three: that is your real capacity, whatever
   the hosting says.
2. **Neon's compute-hours**, if anything probes `/health/ready` on a short
   interval again. 100 a month is generous for a sleeping database and nothing
   at all for a waking one (§4).
3. **Render's instance-hours**, if a second always-on free service is added.
4. **Neon's storage**, eventually — 187 MB now, growing by a few MB per hundred
   learners.
5. **The container's 512 MB**, at roughly 232 MB in use. Not soon.

`/health/ready` reports `aiSlotsFree`. If it sits at zero, the ceiling is the
model's rate limit and no amount of hosting will move it (ADR-051).

---

## When this stops being enough

The container is the same on any host. Moving to a paid instance, a VPS, or
splitting the two services apart again is configuration, not code: the API finds
the AI service at `Ai__BaseUrl` and the database at `ConnectionStrings__WordOs`,
and neither knows or cares where they are.
