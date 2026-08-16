# WordOS — Placement Algorithm

> **Purpose of this file.** The placement method is a product decision we expect to
> revise. It is documented here, in one place, so it can be replaced without
> reverse-engineering it out of the code.
>
> Reference implementation (disposable, Phase 5 ports it to C#):
> `mobile/lib/mock_backend/engine/placement/`
> Specification tests: `mobile/test/placement_algorithm_test.dart`

---

## 1. What method we chose

**Rasch (1PL) ability estimation with expert-assigned item difficulties, driven
adaptively, with Expected A Posteriori (EAP) scoring — run independently per
skill.**

In plain terms:

1. Every item in the bank is written for a CEFR band; that band gives it a
   *difficulty* on a logit scale.
2. The learner's *ability* is a number on that same scale.
3. After each answer we recompute the probability distribution over the
   learner's ability and take its mean (EAP) as the current estimate.
4. The next question asked is the unused one whose difficulty sits closest to
   that estimate — which, under this model, is exactly the most informative one.
5. We stop as soon as the estimate is precise enough, or when the item cap for
   that skill is reached.
6. The final ability is mapped to the nearest CEFR band.

Reading, Listening, Speaking and Writing each run this loop separately and
produce their own level. **Spelling does not** — see §6.

## 2. Why this method and not another

| Option | Why not |
|---|---|
| **Fixed-form test** (what the demo had: 8 questions, count the right ones) | Wastes the learner's time on items far from their level, and a fixed 8-item form cannot separate A1 from C1 with any precision. It also cannot express uncertainty. |
| **2PL / 3PL IRT** (adds discrimination and guessing parameters) | These parameters must be *calibrated from real response data*. We have none — the pilot has not run. Fitting them to zero learners produces confident-looking numbers with nothing behind them. |
| **Full CAT with a large calibrated bank** (the SIMTEST / CEFR-CAT literature) | The right long-term destination, and this design is a strict subset of it. It needs a few hundred learners' responses first. |
| **Ask an LLM to guess the level from a writing sample** | Not reproducible, not auditable, cannot be regression-tested, and costs a network round trip per item. We use AI for *scoring free-text answers* (§5) but never for assigning the level itself (rule R2). |

Rasch with a-priori difficulties is the standard way to operate a **new** item
bank before calibration data exists. It is the smallest model that is still
adaptive, still expresses uncertainty, and upgrades to a fully calibrated CAT by
**changing data, not code** (§9).

Background reading that informed the choice:
[CEFR-based computerized adaptive testing (ERIC EJ989251)](https://files.eric.ed.gov/fulltext/EJ989251.pdf) ·
[SIMTEST, a CEFR-classifying adaptive test](https://en.wikipedia.org/wiki/Simtest) ·
[EAP ability estimation in web-based CAT](https://www.ncbi.nlm.nih.gov/pmc/articles/PMC2631187/)

## 3. The scale

The CEFR ladder is anchored onto logits with even spacing:

```
A1    A1+   A2    A2+   B1    B1+   B2    B2+   C1    C1+   C2
-2.5  -2.0  -1.5  -1.0  -0.5   0.0   0.5   1.0   1.5   2.0   2.5
```

`stepLogits = 0.5`, centred on B1+. Population prior: `N(-0.25, 1.2²)` — slightly
below centre because the pilot audience skews A2–B1. The prior only matters
before the first few answers.

**Response model.** For ability θ and item difficulty b:

```
P(correct) = 1 / (1 + e^-(θ - b))
```

**Ability update (EAP).** Over a discrete grid of θ from −3.5 to +3.5 in steps of
0.05, the posterior weight of each grid point is the prior density times the
likelihood of every answer so far. The estimate is the posterior mean; the
reported precision is the posterior standard deviation (the *standard error*).
The computation is done in log space, because a long response vector otherwise
underflows to zero and collapses the posterior to `NaN` — there is a regression
test for exactly this.

## 4. How questions are chosen, and when we stop

**Next item.** Fisher information for a Rasch item is `P·(1−P)`, which is
maximised when the item's difficulty equals the learner's ability. So "pick the
unused item whose difficulty is nearest the current estimate" *is* the optimal
rule under this model — it is not a heuristic. Exact ties are broken at random,
which gives basic exposure control so two learners of the same level do not
always see an identical test.

**Stopping.** Per skill, stop when **either**:

- the posterior standard error has dropped to the target, **and** the minimum
  item count has been met; or
- the item cap for that skill has been reached.

| Skill group | Min items | Max items | Target SE |
|---|---|---|---|
| Reading, Listening | 3 | 6 | 0.40 |
| Speaking, Writing | 2 | 3 | 0.55 |
| Spelling | 4 | 4 | n/a (fixed ladder) |

Receptive items are cheap, so we buy precision. Each productive item costs the
learner a written or spoken answer *and* an AI evaluation, so the caps are
tighter and the SE target is looser; the level engine refines those two skills
from real sessions afterwards (rule R6).

A whole test is therefore roughly **12–22 questions** and never more than 24.
The UI shows this as "about N", not a countdown, because an adaptive test has no
fixed length.

## 5. How each skill is evaluated

**Reading** — a short passage plus a comprehension question, or a
gap-completion item testing lexical/grammatical range. Scored 0 or 1.

**Listening** — the same shape, but the stem is spoken by TTS and **never shown
as text**. The learner may replay it, including at reduced speed. Scored 0 or 1.

**Speaking** — open prompts of increasing demand ("introduce yourself" → "explain
which part of a plan you accept and which you do not"). The response is scored
against a rubric and contributes **partial credit in [0, 1]**, not a pass/fail.

**Writing** — the same, with written prompts.

Partial credit is folded into the same model by using the likelihood
`P^s · (1−P)^(1−s)`, which reduces exactly to the Bernoulli likelihood when the
score is 0 or 1. Multiple-choice and rubric-scored items therefore live on one
scale without a second model.

**Who scores the free text.** The `FreeResponseScorer` interface. In production
this is the Python AI service (Phase 6), returning a schema-validated rubric
score. There is a deliberate offline fallback, `HeuristicFreeResponseScorer`,
which measures response length relative to the band's expectation, lexical
variety and clause connectives. It is crude, and the code says so: it exists so
that an AI outage **degrades** placement rather than breaking it, and the backend
records that the fallback was used so analytics can show how many placements ran
without AI.

## 6. Spelling — measured, never levelled

**A CEFR band is not a meaningful description of orthographic accuracy**, so
Spelling is never assigned one. `Spelling A1` / `Spelling B2` do not exist
anywhere in the model, the API or the UI.

What Spelling placement produces instead:

- a short fixed ladder of four items (fixed, not adaptive, so the accuracy
  figure is comparable between learners);
- an **accuracy** figure, stored as `rollingAccuracy` on the spelling row;
- a **starting input mode**: `LETTER_TILES` below 75 % accuracy, `FREE_TYPING`
  at or above it.

`SkillLevel.userSelectedLevel` and `systemAssessedLevel` are **null** for
Spelling. `SkillLevel.carriesCefrLevel` is the check call sites use; attempting
to set a spelling level through `PATCH /settings/skill-level` is rejected with
`SKILL_NOT_LEVELLED` rather than silently ignored.

Where spelling *content* needs a difficulty — Arabic meaning vs. English
definition as the clue, tiles vs. free typing — it is derived from the learner's
**Reading** level. Whether an English definition is a usable clue is a
reading-comprehension question, not a spelling one. See ADR-008.

## 7. How the final level is assigned

The final ability estimate is mapped to the **nearest anchor** on the CEFR
ladder. This is deliberately a nearest-anchor rule rather than a table of cut
scores: with evenly spaced anchors the two are equivalent, and the nearest-anchor
form cannot develop gaps or overlaps if the spacing is retuned later.

Placement writes the result to **both** level fields (ADR-007):

- `systemAssessedLevel` — the measurement. Only this may drive progression and
  archiving (rule R6).
- `userSelectedLevel` — starts as a copy, and is the only one the learner can
  change in Settings.

## 8. How uncertainty is handled

The posterior standard error is converted to a **confidence** in [0, 1]:

```
SE ≤ 0.40  → confidence 1.0     (the test reached its precision target)
SE ≥ 1.10  → confidence 0.0     (we learned nothing beyond the prior)
between    → linear
```

`confidence` is a field on `SkillLevel` and travels to the client. When any
levelled skill lands below 0.5, the result screen tells the learner the level is
**provisional** and that WordOS will confirm it from their first sessions,
instead of presenting a precision the test did not reach.

**We never refuse to place a learner.** A level is always assigned — a learner
cannot be left without a starting point. Low confidence is a label, not a
failure mode, and it is safe because the level engine (14 sessions / 85 %
thresholds) corrects the estimate from real performance within the first two
weeks.

Two cases produce low confidence, and both are honest:

- *inconsistent answers* — the posterior genuinely stays wide;
- *topping out or bottoming out the bank* — a learner who answers everything
  correctly has only established "at least C1"; the estimate runs past the
  hardest item we own, so the posterior is wide by construction. Adding C1+/C2
  items to the bank is the fix, and it needs no code change (§9).

## 9. How to replace or retune this

Ordered from cheapest to most invasive. **None of the first three touch the
algorithm.**

1. **Retune the numbers.** Everything tunable lives in `PlacementConfig` and
   `AbilityScale`: prior, step size, per-skill min/max items, SE targets,
   confidence thresholds, the free-typing threshold. In Phase 5 these are seeded
   into the `configurations` table (rule R3), so retuning is a config write.
2. **Change the item bank.** `PlacementItemBank` is pure data. Add items, add
   bands (C1+/C2), retire weak ones — the algorithm is unchanged.
3. **Upgrade to calibrated difficulties.** Once the pilot has produced response
   data, fit per-item difficulties and have `BankItem` carry a measured
   `difficulty` instead of deriving it from `level`. This is the single highest-
   value upgrade and it is a data change.
4. **Upgrade the response model** to 2PL/3PL. Add `discrimination` (and
   `guessing`) to `BankItem`, extend `probabilityCorrect` and `information`.
   `AbilityEstimator` is the only file that changes.
5. **Replace the method entirely.** The seam is narrow by design: the rest of the
   system only consumes `PlacementStep` and `PlacementResult`. Anything that
   produces those two shapes can be dropped in, and
   `test/placement_algorithm_test.dart` is written against behaviour, not
   internals, so it keeps working as an acceptance suite.

## 10. Known limitations

Stated plainly so nobody is misled by the machinery:

- **Item difficulties are expert-assigned, not calibrated.** The bands are our
  judgement. Until the pilot data lands, the precision the SE figure implies is
  better than the accuracy actually is.
- **The item bank is small** (~10 receptive items per skill, 5 productive). It
  cannot resolve the `+` half-bands reliably, and it tops out at C1.
- **The offline free-text scorer does not measure grammar, coherence or task
  achievement.** It is a length-and-variety proxy. Placements that run without
  AI should be treated as provisional regardless of their reported confidence.
- **Speaking placement is currently typed, not spoken.** Pronunciation is not
  assessed at all until voice capture lands in Phase 7; the Speaking level is
  really a "spoken-register production" level today.
- **No exposure control across sessions.** A learner who retakes placement may
  see overlapping items.

## 11. API surface

Adaptive testing cannot hand the client every item up front, so placement is a
three-call protocol (`docs/05-API-CONTRACT.md`):

```
POST /placement/start              → PlacementStep (first item)
POST /placement/{id}/answer        → PlacementStep (next item, or isComplete)
POST /placement/{id}/complete      → PlacementResult
```

The client renders one item at a time and computes nothing (rule R1). Options
arrive already shuffled and the correct answer is never sent (rule R7).
Answering an item that is not the current one is rejected with
`ITEM_NOT_CURRENT`, so a retry after a dropped connection cannot corrupt the
estimate.
