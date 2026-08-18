# Lexicon pipeline

Builds the vocabulary source WordOS validates against.

```
English word ──▶ synset ──┬─▶ definition (Open English WordNet)
                          ├─▶ Arabic meaning (Arabic WordNet 4.0)
                          └─▶ CEFR level (CEFR-J + Octanove)
                                   │
                                   ▼
                          PostgreSQL lexicon_entries
                                   │
                                   ▼
                          GET /words/lookup  ──▶  Flutter
```

The Flutter client never reads these datasets. It sees only what the API
returns, and the API serves only what is in PostgreSQL.

## Running it

```bash
cd backend/tools/lexicon
./download.sh                                    # ~166 MB into ./data (gitignored)
cd ../..
dotnet run --project tools/lexicon/importer -- tools/lexicon/data --dry-run
dotnet run --project tools/lexicon/importer -- tools/lexicon/data

# Just the authored closed-class words — no corpus, no parse, ~0.2s
dotnet run --project tools/lexicon/importer -- --closed-class-only
```

WordNet carries only content words, so `is`, `are`, `what`, `the` and every
other pronoun, article, auxiliary, preposition and conjunction are written by
hand in `importer/FunctionWords.cs` and imported alongside the join (ADR-033).
Those classes are closed, so the list is finite; edit it and re-run with
`--closed-class-only`.

`--dry-run` parses and joins but writes nothing — use it to check the join
report before touching the database.

**Idempotent.** The import stages into a temp table inside one transaction, then
`INSERT … ON CONFLICT ("SenseId") DO UPDATE`. Re-running updates rows in place;
it never duplicates and never leaves the table half-replaced. Verified: two
consecutive runs both end at 175,611 rows.

## Sources

| Source | Version | Supplies | Licence |
|---|---|---|---|
| [CEFR-J Vocabulary Profile](https://github.com/openlanguageprofiles/olp-en-cefrj) | 1.5 | CEFR band per (word, POS) | Research + commercial with citation (Tono Lab, TUFS) |
| Octanove Vocabulary Profile | 1.0 | C1/C2 bands | CC BY-SA 4.0 |
| [Open English WordNet](https://en-word.net/) | 2025 | Senses, synsets, definitions | CC BY 4.0 |
| [Arabic WordNet](https://github.com/Salah-Sal/arabic-wordnet-v4) | 4.0/4.1 | Arabic meaning per synset | CC BY 4.0 |
| `importer/FunctionWords.cs` | — | The 167 closed-class words WordNet has no entries for | Written here |

Versions are **pinned**, not `latest`: a silent upstream change would alter
learners' vocabulary levels, and the lexicon must be rebuildable byte-for-byte.
`data/MANIFEST.json` records the URL, version and SHA-256 of every artefact
actually used.

Raw data is **never committed** — `data/.gitignore` excludes everything but
itself. Only the scripts are in Git.

## How the join works

The key is the WordNet synset id. AWN prefixes it with `awn4-`; stripping that
gives the OEWN id, and **99.9 %** of OEWN synsets (107,496 of 107,558) have an
Arabic counterpart.

The grain is the **sense**, not the word. `book` yields one row per synset it
belongs to, so `book = كتاب` and `book = يحجز` are separate rows with separate
ids — which is what makes them independent vocabulary items with independent
journeys downstream (ADR-012).

### Deliberate choices

**Senses a learner could not tell apart are collapsed.** English draws finer
distinctions than the Arabic gloss preserves, so several synsets of `book` all
return `كتاب`. Presenting them as separate options would ask the learner to
choose between identical-looking rows and then treat those choices as different
vocabulary items. One row is kept per `(word, POS, Arabic meaning)`, preferring
the sense that carries a CEFR level. 8,288 rows collapsed this way. Genuinely
different meanings keep their own rows.

**Rows without an Arabic meaning are dropped** (60 of them). A row the learner
could never be shown the meaning of is not useful.

**An unknown CEFR level stays NULL** — never defaulted to A1. Only 15.3 % of
senses are in the CEFR lists; silently labelling the rest A1 would tell learners
that rare words are beginner vocabulary.

**`FrequencyRank` is a proxy, not corpus frequency.** No source here ships a
frequency list, so the rank combines the CEFR band with WordNet's own
sense ordering. Without it, typing `bo` buries `book` under twenty senses of
`board`. Pass a real frequency list to `LexiconBuilder.Build` to replace it;
nothing else changes.

## Result

```
175,611 senses     126,124 distinct words     26,859 with a CEFR level
A1 4,846 · A2 4,696 · B1 6,991 · B2 6,758 · C1 2,068 · C2 1,500
```

Prefix search uses a `text_pattern_ops` index — confirmed by `EXPLAIN` to be a
`Bitmap Index Scan`, not a sequential scan.

## Provenance

Every row carries `SourceFlags`, e.g.
`en=oewn-2025;ar=awn-4.0;cefr=cefrj-1.5`.

This matters for Arabic WordNet specifically: parts of it were machine-
translated upstream. It is approved as a **lexical dataset** — the importer only
reads and joins, and no AI generates meanings at runtime — but recording the
source per row keeps those entries auditable and replaceable if a
human-curated gloss arrives later (ADR-012).

## Tests

| Suite | Verifies |
|---|---|
| `LexiconImportTests` | The rules, on a deterministic fixture: sense uniqueness, multiple meanings per word, Arabic integrity, CEFR mapping, prefix search, rejected invalid rows, re-import without duplication |
| `ImportedLexiconTests` | The **actual imported data**. Skips (never silently passes) when the lexicon has not been imported |
