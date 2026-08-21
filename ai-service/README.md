# WordOS AI Service

```
Flutter  ──►  C# Backend  ──►  this service  ──►  Gemini
                                     ▲
                              the API key lives
                              here and nowhere else
```

Owns prompts and provider communication, nothing else. It never decides whether
a learner passed: it returns **observations**, and the backend applies the rule
(rule R2, `System Archticture.txt` §11).

## The key

`ai-service/.env`, mode `600`, excluded by `.gitignore`.

```bash
cp .env.example .env
# then edit .env and paste the key
python3 verify_key.py     # one real call; prints a fingerprint, never the key
```

**Never** in Flutter — a mobile binary can be decompiled and the key extracted.
**Never** in C# — the backend talks to this service, not to Gemini.
**Never** in Git — a committed secret stays in history even after deletion.

## Model

Pinned to **`gemini-3.1-flash-lite`**.

The 2.5 family was requested but Google has closed it to new accounts — every
variant returns `404 no longer available to new users`, including
`gemini-2.5-flash-lite` and dated builds. `3.1-flash-lite` is the same
cheapest tier and was the fastest of the six models actually tested (1140 ms).

> Listing a model via `GET /models` does **not** mean it can be called. The
> listing returns `gemini-2.5-flash`, which then fails at `generateContent`.
> Probe with a real request before pinning anything.

A **cost guard** refuses to start on a higher tier:

```
✗ Refusing to use 'gemini-pro-latest': it is a higher-cost tier.
```

It blocks `-pro`, `ultra`, `deep-research`, `-max`. Overriding is deliberate:

```bash
GEMINI_ALLOW_EXPENSIVE=true
```

This exists so a stray edit or a copied example cannot quietly start billing at
several times the rate.

## Running

```bash
python3 -m venv .venv
./.venv/bin/pip install -r requirements.txt
./.venv/bin/uvicorn app.main:app --host 127.0.0.1 --port 8099
```

> Python **3.9** needs the `eval_type_backport` shim for `str | None`
> annotations. Python 3.12 does not — `brew install python@3.12` is worth doing
> before deployment.

## Tests

```bash
./.venv/bin/pip install -r requirements-dev.txt
./.venv/bin/python -m pytest
```

They need neither a Gemini key nor a network: `tests/conftest.py` sets fixture
credentials before the app is imported, and an autouse fixture replaces the
provider client with one that **fails the test** if it is called without a
stub. That guard is not decoration — the first draft of the suite sent a real
request to googleapis.com from a test that expected its input to be rejected,
and only an invalid key stopped it becoming a charge.

What is covered: the service token (including that a missing one now stops
startup), the shaping of a generation into a response, every refusal path
(unparseable JSON, provider failure, no sentences), request validation, the
admission gate and its slot accounting, the model-cost guard, and the prompt
builders — passage length by band, which form of a word the passage is told to
use, and the feedback language.

What is not: the prompts' *quality*. Whether a passage teaches well is a
judgement, and it is measured by the experiment, not by an assertion.

## Endpoints

| Endpoint | Purpose | Tokens (typical) |
|---|---|---|
| `GET /health` | Liveness. Does **not** call Gemini — a health check should not bill. | 0 |
| `POST /ai/content` | Reading/Listening passage + comprehension questions + per-word context | ~940 |
| `POST /ai/writing` | Observations about one learner sentence | ~470 |
| `POST /ai/speaking/turn` | One conversational turn + which words were used naturally | ~370 |

All are authenticated with `X-Service-Token`. `AI_SERVICE_TOKEN` is
**required** — the service refuses to start without it, because an unset token
does not weaken the check, it removes it, and anyone who can reach the port can
then spend the API budget. A throwaway local run with no token has to say so
out loud with `AI_ALLOW_UNAUTHENTICATED=true`. This service must never be
exposed directly either way.

### Verified output

`POST /ai/content` at B1 with targets `allocate`, `reliable`:

> Modern software development requires careful planning before a single line of
> code is written. **Developers must carefully allocate enough memory and
> processing power** to each part of their new program. …

and the context handed to the learner for `allocate`:

```
before  : Modern software development requires careful planning …
sentence: Developers must carefully allocate enough memory …
after   : This ensures that the application runs smoothly …
```

The word is never defined inside the passage — the learner infers it from the
neighbours, which is the whole point of the exercise (demo review §26).

## Prompts

All in `app/prompts.py`, each with a version string returned on every response.
When a prompt changes, bump its version: the backend logs it with every
generation, so a drop in pass rates can be traced to a prompt change rather
than blamed on learners (`MVP Core.txt` §61).

Note what the writing prompt does **not** ask for: a pass/fail verdict. The
model reports observations; the rule that a small grammar slip must not fail
correct usage (§32) lives in C#, where a prompt tweak cannot silently change it.

## Structured output

Every call constrains Gemini with a JSON schema and parses the result. Output
that does not parse is an error the caller handles — never patched up with a
regex. AI is not trusted to be well-formed.
