using System.Diagnostics;
using System.Globalization;
using Microsoft.Extensions.Configuration;
using Npgsql;
using WordOs.Domain.Common;
using WordOs.LexiconImporter;

// ─────────────────────────────────────────────────────────────────────────────
// WordOS lexicon importer
//
//   English word → synset → Arabic meaning (AWN) → CEFR level → PostgreSQL
//
// Reproducible: `./download.sh && dotnet run --project importer`.
// Idempotent:   re-running updates rows in place and never duplicates them.
//
// The connection string comes from user-secrets or the environment. Migrations
// credentials are used because this writes reference data owned by the schema
// owner, not learner data.
// ─────────────────────────────────────────────────────────────────────────────

// Numbers in the report are for humans reading a console, not for a locale.
CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;

var dataDir = args.FirstOrDefault(a => !a.StartsWith("--"))
              ?? Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "data");
dataDir = Path.GetFullPath(dataDir);

var dryRun = args.Contains("--dry-run");

// The closed-class words are authored, not downloaded, so they can be applied
// on their own — no 166 MB corpus, no parse, seconds instead of minutes. That
// matters because they are the half of the lexicon most likely to be edited.
var closedClassOnly = args.Contains("--closed-class-only");

Console.WriteLine($"WordOS lexicon importer");
Console.WriteLine($"  data: {dataDir}");
Console.WriteLine($"  mode: {(dryRun ? "dry run (no database writes)" : "import")}"
                  + (closedClassOnly ? "  (closed-class words only)" : ""));
Console.WriteLine();

var cefrjCsv = Path.Combine(dataDir, "cefrj.csv");
var octanoveCsv = Path.Combine(dataDir, "octanove-c1c2.csv");
var oewnDir = Path.Combine(dataDir, "oewn");
var awnXml = Path.Combine(dataDir, "awn4.xml");

foreach (var required in closedClassOnly ? [] : new[] { cefrjCsv, awnXml })
{
    if (!File.Exists(required))
    {
        Console.Error.WriteLine($"Missing {required}. Run ./download.sh first.");
        return 1;
    }
}

if (!closedClassOnly && !Directory.Exists(oewnDir))
{
    Console.Error.WriteLine($"Missing {oewnDir}. Run ./download.sh first.");
    return 1;
}

var sw = Stopwatch.StartNew();

List<LexiconRow> rows;
BuildStats stats;

if (closedClassOnly)
{
    rows = FunctionWords.Build();
    stats = new BuildStats(0, 0, rows.Count, 0, 0, 0, rows.Count);
    Console.WriteLine($"closed-class words … {rows.Count:N0} rows");
}
else
{
    Console.Write("reading CEFR-J + Octanove … ");
    var cefr = LexiconSources.ReadCefr(cefrjCsv, octanoveCsv);
    Console.WriteLine($"{cefr.Count:N0} levelled (word, pos) pairs");

    Console.Write("reading Open English WordNet synsets … ");
    var synsets = LexiconSources.ReadOewnSynsets(oewnDir);
    Console.WriteLine($"{synsets.Count:N0} synsets");

    Console.Write("reading Open English WordNet senses … ");
    var senses = LexiconSources.ReadOewnSenses(oewnDir);
    Console.WriteLine($"{senses.Count:N0} senses");

    Console.Write("reading inflected forms … ");
    var forms = LexiconSources.ReadOewnForms(oewnDir);
    Console.WriteLine($"{forms.Count:N0} words with an irregular form");

    Console.Write("reading Arabic WordNet … ");
    var arabic = LexiconSources.ReadArabicBySynset(awnXml);
    Console.WriteLine($"{arabic.Count:N0} synsets with Arabic");

    Console.Write("joining … ");
    (rows, stats) = LexiconBuilder.Build(senses, synsets, arabic, cefr, forms: forms);
    Console.WriteLine($"{rows.Count:N0} rows");

    // Pronouns, auxiliaries, articles, prepositions and question words are not in
    // WordNet at all — it is a lexicon of content words — so a learner searching
    // for "is", "are" or "what" found nothing. They are added here rather than
    // waited for: the classes are closed, so the list is finite (ADR-033).
    var closedClass = FunctionWords.Build();
    rows.AddRange(closedClass);
    Console.WriteLine($"closed-class words … {closedClass.Count:N0} rows");
    Console.WriteLine();
}

Console.WriteLine("Join report");
Console.WriteLine($"  emitted                {stats.Emitted,10:N0}");
Console.WriteLine($"  with a CEFR level      {stats.WithCefr,10:N0}  " +
                  $"({100.0 * stats.WithCefr / Math.Max(1, stats.Emitted):F1}%)");
Console.WriteLine($"  skipped: no Arabic     {stats.SkippedNoArabic,10:N0}");
Console.WriteLine($"  skipped: no synset     {stats.SkippedNoSynset,10:N0}");
Console.WriteLine($"  skipped: long phrase   {stats.SkippedMultiword,10:N0}");
Console.WriteLine($"  collapsed synonyms     {stats.CollapsedSynonymousSenses,10:N0}  " +
                  "(same word, POS and Arabic gloss)");
Console.WriteLine();

if (rows.Count == 0)
{
    Console.Error.WriteLine("Nothing to import — refusing to continue.");
    return 1;
}

if (dryRun)
{
    foreach (var sample in rows.Where(r => r.TextNormalized == "book").Take(4))
        Console.WriteLine($"  sample  {sample.Text,-12} {sample.PartOfSpeech}  " +
                          $"{sample.CefrLevel?.ToWire() ?? "—",-6} {sample.MeaningAr}");

    // The forms, which are the point of the run: what a rule got wrong once is
    // worth reading before 216,000 rows are written.
    Console.WriteLine();
    foreach (var lemma in new[]
             {
                 "go", "take", "walk", "lunge", "read", "cost", "put", "stop",
                 "study", "mouse", "child", "woman", "book", "city",
                 // The three shapes of a single listed form: both roles, the
                 // participle alone, and the past alone.
                 "say", "beat", "run", "win",
             })
    {
        var forms = rows
            .Where(r => string.Equals(r.Lemma, lemma, StringComparison.OrdinalIgnoreCase)
                        && r.SourceFlags.Contains("form="))
            .Select(r => $"{r.Text.ToLowerInvariant()} [{r.SourceFlags.Split("form=")[1]}]")
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(t => t)
            .ToList();

        Console.WriteLine($"  {lemma,-8} → {(forms.Count == 0 ? "(no forms added)" : string.Join(", ", forms))}");
    }
    Console.WriteLine($"\nDry run complete in {sw.Elapsed.TotalSeconds:F1}s.");
    return 0;
}

// ── Database ─────────────────────────────────────────────────────────────────

var configuration = new ConfigurationBuilder()
    .AddUserSecrets<LexiconImporterMarker>(optional: true)
    .AddEnvironmentVariables()
    .Build();

var connectionString =
    configuration.GetConnectionString("WordOsMigrations")
    ?? Environment.GetEnvironmentVariable("WORDOS_MIGRATIONS_CONNECTION");

if (string.IsNullOrWhiteSpace(connectionString))
{
    Console.Error.WriteLine(
        """
        No connection string. The importer writes reference data owned by the
        schema owner, so it uses ConnectionStrings:WordOsMigrations.

            dotnet user-secrets set "ConnectionStrings:WordOsMigrations" "..." \
              --project tools/lexicon/importer

        It is never read from source or committed.
        """);
    return 1;
}

await using var connection = new NpgsqlConnection(connectionString);
await connection.OpenAsync();

Console.WriteLine("importing …");

// One transaction for the whole import: the staging table is scoped to it
// (ON COMMIT DROP needs an explicit transaction), and the merge is
// all-or-nothing, so a failure can never leave the lexicon half-replaced.
await using var transaction = await connection.BeginTransactionAsync();

await using (var create = connection.CreateCommand())
{
    create.CommandText =
        """
        CREATE TEMP TABLE lexicon_staging (
            "SenseId"        varchar(64)  PRIMARY KEY,
            "Text"           varchar(128) NOT NULL,
            "TextNormalized" varchar(128) NOT NULL,
            "Lemma"          varchar(128) NOT NULL,
            "PartOfSpeech"   varchar(32)  NOT NULL,
            "DefinitionEn"   varchar(2048) NOT NULL,
            "MeaningAr"      varchar(512) NOT NULL,
            "MeaningArNormalized" varchar(512) NOT NULL,
            "CefrLevel"      varchar(8),
            "FrequencyRank"  integer,
            "SourceFlags"    varchar(128) NOT NULL,
            "UpdatedAt"      timestamptz  NOT NULL
        ) ON COMMIT DROP;
        """;
    await create.ExecuteNonQueryAsync();
}

var now = DateTimeOffset.UtcNow;

await using (var writer = await connection.BeginBinaryImportAsync(
                 """
                 COPY lexicon_staging (
                     "SenseId","Text","TextNormalized","Lemma","PartOfSpeech",
                     "DefinitionEn","MeaningAr","MeaningArNormalized",
                     "CefrLevel","FrequencyRank",
                     "SourceFlags","UpdatedAt"
                 ) FROM STDIN (FORMAT BINARY)
                 """))
{
    foreach (var row in rows)
    {
        await writer.StartRowAsync();
        await writer.WriteAsync(row.SenseId);
        await writer.WriteAsync(row.Text);
        await writer.WriteAsync(row.TextNormalized);
        await writer.WriteAsync(row.Lemma);
        await writer.WriteAsync(row.PartOfSpeech);
        await writer.WriteAsync(row.DefinitionEn);
        await writer.WriteAsync(row.MeaningAr);
        // Folded here rather than in SQL so the importer and the API agree on
        // one definition of "the same Arabic word" (ADR-034).
        await writer.WriteAsync(ArabicText.Normalize(row.MeaningAr));
        if (row.CefrLevel is null) await writer.WriteNullAsync();
        else await writer.WriteAsync(row.CefrLevel.Value.ToWire());
        if (row.FrequencyRank is null) await writer.WriteNullAsync();
        else await writer.WriteAsync(row.FrequencyRank.Value);
        await writer.WriteAsync(row.SourceFlags);
        await writer.WriteAsync(now);
    }

    await writer.CompleteAsync();
}

int affected;
await using (var merge = connection.CreateCommand())
{
    merge.CommandText =
        """
        INSERT INTO lexicon_entries AS t (
            "SenseId","Text","TextNormalized","Lemma","PartOfSpeech",
            "DefinitionEn","MeaningAr","MeaningArNormalized",
            "CefrLevel","FrequencyRank",
            "SourceFlags","UpdatedAt")
        SELECT "SenseId","Text","TextNormalized","Lemma","PartOfSpeech",
               "DefinitionEn","MeaningAr","MeaningArNormalized",
               "CefrLevel","FrequencyRank",
               "SourceFlags","UpdatedAt"
        FROM lexicon_staging
        ON CONFLICT ("SenseId") DO UPDATE SET
            "Text"           = EXCLUDED."Text",
            "TextNormalized" = EXCLUDED."TextNormalized",
            "Lemma"          = EXCLUDED."Lemma",
            "PartOfSpeech"   = EXCLUDED."PartOfSpeech",
            "DefinitionEn"   = EXCLUDED."DefinitionEn",
            "MeaningAr"      = EXCLUDED."MeaningAr",
            "MeaningArNormalized" = EXCLUDED."MeaningArNormalized",
            "CefrLevel"      = EXCLUDED."CefrLevel",
            "FrequencyRank"  = EXCLUDED."FrequencyRank",
            "SourceFlags"    = EXCLUDED."SourceFlags",
            "UpdatedAt"      = EXCLUDED."UpdatedAt";
        """;
    merge.CommandTimeout = 600;
    affected = await merge.ExecuteNonQueryAsync();
}

// Rows the build no longer produces. Without this the table only ever grows:
// a rule that stops emitting `beaten` as a past tense leaves the old row behind,
// and the learner goes on seeing something the importer has already disowned.
//
// A row a learner has added is kept whatever the rules now say — their
// vocabulary is theirs, and it copied what it needed at the time.
int removed;
await using (var prune = connection.CreateCommand())
{
    prune.CommandText =
        """
        DELETE FROM lexicon_entries e
        WHERE NOT EXISTS (
                  SELECT 1 FROM lexicon_staging s WHERE s."SenseId" = e."SenseId")
          AND NOT EXISTS (
                  SELECT 1 FROM words w WHERE w."SenseId" = e."SenseId");
        """;
    prune.CommandTimeout = 600;
    removed = await prune.ExecuteNonQueryAsync();
}

await using (var count = connection.CreateCommand())
{
    count.CommandText = "SELECT count(*) FROM lexicon_entries;";
    var total = (long)(await count.ExecuteScalarAsync() ?? 0L);
    Console.WriteLine($"  merged {affected:N0} rows, removed {removed:N0} stale; table now holds {total:N0}");
}

await transaction.CommitAsync();

Console.WriteLine($"\nDone in {sw.Elapsed.TotalSeconds:F1}s.");
return 0;

/// <summary>Type anchor for user-secrets lookup.</summary>
internal sealed class LexiconImporterMarker;
