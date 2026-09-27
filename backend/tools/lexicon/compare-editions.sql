-- Which dictionary is better, as numbers rather than an impression (ADR-096).
--
--   psql -d wordos_dev -f tools/lexicon/compare-editions.sql
--
-- Every figure is about the row a learner actually SEES: the one the lookup
-- puts on top, which is `ORDER BY "FrequencyRank", "SenseId"` — the same order
-- WordEndpoints uses.

\set QUIET on
\pset footer off

WITH top_row AS (
    SELECT DISTINCT ON ("Edition", "TextNormalized")
           "Edition", "TextNormalized", "MeaningAr", "DefinitionEn",
           "PartOfSpeech", "CefrLevel"
    FROM lexicon_entries
    ORDER BY "Edition", "TextNormalized", "FrequencyRank" NULLS LAST, "SenseId"
)
SELECT
    t."TextNormalized"                             AS word,
    max(CASE WHEN "Edition" = 'oewn-awn'   THEN "MeaningAr" END) AS current_ar,
    max(CASE WHEN "Edition" = 'wiktionary' THEN "MeaningAr" END) AS new_ar,
    max(CASE WHEN "Edition" = 'oewn-awn'   THEN left("DefinitionEn", 38) END) AS current_def,
    max(CASE WHEN "Edition" = 'wiktionary' THEN left("DefinitionEn", 38) END) AS new_def
FROM top_row t
WHERE t."TextNormalized" IN (
    'sell','buy','eat','go','run','write','read','speak','listen','city',
    'river','book','house','water','food','school','teacher','student',
    'money','market','friend','family','happy','sad','beautiful')
GROUP BY t."TextNormalized"
ORDER BY t."TextNormalized";

\echo ''
\echo '── Size and coverage by edition ─────────────────────────────────────────'

SELECT "Edition",
       count(*)                                        AS rows,
       count(DISTINCT "TextNormalized")                AS words,
       count("CefrLevel")                              AS levelled,
       round(100.0 * count("CefrLevel") / count(*), 1) AS levelled_pct,
       count(*) FILTER (WHERE "MeaningAr" ~ '[ً-ْ]') AS with_harakat
FROM lexicon_entries
GROUP BY "Edition"
ORDER BY "Edition";

\echo ''
\echo '── Verbs still cited in the past (the sell=باع fault) ───────────────────'

SELECT "Edition",
       count(*) FILTER (WHERE "SourceFlags" LIKE '%arverb=nonpast%') AS non_past,
       count(*) FILTER (WHERE "SourceFlags" NOT LIKE '%arverb=nonpast%') AS past_or_unknown
FROM lexicon_entries
WHERE "PartOfSpeech" = 'v' AND "SourceFlags" NOT LIKE '%form=%'
GROUP BY "Edition"
ORDER BY "Edition";

\echo ''
\echo '── A word whose every sense is one band (the go=A1 fault) ───────────────'

SELECT "Edition",
       count(*) AS words_with_6plus_senses_all_one_band
FROM (
    SELECT "Edition", "TextNormalized"
    FROM lexicon_entries
    WHERE "CefrLevel" IS NOT NULL AND "SourceFlags" NOT LIKE '%form=%'
    GROUP BY "Edition", "TextNormalized"
    HAVING count(*) >= 6 AND count(DISTINCT "CefrLevel") = 1
) x
GROUP BY "Edition"
ORDER BY "Edition";
