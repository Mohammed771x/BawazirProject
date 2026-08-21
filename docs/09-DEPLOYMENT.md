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
ConnectionStrings__WordOs="$NEON_OWNER" dotnet ef database update \
  --project src/WordOs.Infrastructure --startup-project src/WordOs.Api
```

You should see each migration named as it applies, ending in `Done.`

> **Every later release that adds a migration needs this command run again,
> before you deploy.** The service cannot do it for itself, deliberately: it
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
   | Health check path | `/health/ready` |

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

A monitor that requests a cheap endpoint every few minutes prevents it. The
service already has the right endpoint for this:

```
https://wordos-api.onrender.com/health/ready
```

`/health/ready` rather than `/health/live`: it touches the database, so the same
ping keeps Neon awake as well.

Use **UptimeRobot**, **Better Stack**, or **cron-job.org** — all have free plans:

1. Add an **HTTP(s) monitor**.
2. URL: the address above.
3. Interval: **5 minutes**.
4. Expect: **200**.

That is the trick you already had in mind, and it works. Three honest caveats:

* **It costs instance-hours.** Render's free allowance is a fixed number of
  running hours per month per account. Kept awake continuously, one service uses
  about 720 of them. That fits — *one* service. It is the reason both processes
  share this container, and the reason not to add a second free service beside
  it.
* **It is not free of failure.** The monitor also tells you when the service is
  down, which is worth having anyway. Point it at `/health/ready` instead if you
  would rather be alerted when the *database* is unreachable, not just the
  process.
* **Neon sleeps too.** Its free plan suspends a database after a few minutes
  idle, and the first query afterwards pays a second or so to wake it. Pinging
  `/health/ready` rather than `/health/live` keeps both awake, because that
  endpoint asks the database whether it is there.

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
2. **Render's instance-hours**, if a second always-on free service is added.
3. **Neon's storage**, eventually — 187 MB now, growing by a few MB per hundred
   learners.
4. **The container's 512 MB**, at roughly 232 MB in use. Not soon.

`/health/ready` reports `aiSlotsFree`. If it sits at zero, the ceiling is the
model's rate limit and no amount of hosting will move it (ADR-051).

---

## When this stops being enough

The container is the same on any host. Moving to a paid instance, a VPS, or
splitting the two services apart again is configuration, not code: the API finds
the AI service at `Ai__BaseUrl` and the database at `ConnectionStrings__WordOs`,
and neither knows or cares where they are.
