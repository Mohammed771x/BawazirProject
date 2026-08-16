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

Console.WriteLine($"WordOS lexicon importer");
Console.WriteLine($"  data: {dataDir}");
Console.WriteLine($"  mode: {(dryRun ? "dry run (no database writes)" : "import")}");
Console.WriteLine();

var cefrjCsv = Path.Combine(dataDir, "cefrj.csv");
var octanoveCsv = Path.Combine(dataDir, "octanove-c1c2.csv");
var oewnDir = Path.Combine(dataDir, "oewn");
var awnXml = Path.Combine(dataDir, "awn4.xml");

foreach (var required in new[] { cefrjCsv, awnXml })
{
    if (!File.Exists(required))
    {
        Console.Error.WriteLine($"Missing {required}. Run ./download.sh first.");
        return 1;
    }
}

if (!Directory.Exists(oewnDir))
{
    Console.Error.WriteLine($"Missing {oewnDir}. Run ./download.sh first.");
    return 1;
}

var sw = Stopwatch.StartNew();

Console.Write("reading CEFR-J + Octanove … ");
var cefr = LexiconSources.ReadCefr(cefrjCsv, octanoveCsv);
Console.WriteLine($"{cefr.Count:N0} levelled (word, pos) pairs");

Console.Write("reading Open English WordNet synsets … ");
var synsets = LexiconSources.ReadOewnSynsets(oewnDir);
Console.WriteLine($"{synsets.Count:N0} synsets");

Console.Write("reading Open English WordNet senses … ");
var senses = LexiconSources.ReadOewnSenses(oewnDir);
Console.WriteLine($"{senses.Count:N0} senses");

Console.Write("reading Arabic WordNet … ");
var arabic = LexiconSources.ReadArabicBySynset(awnXml);
Console.WriteLine($"{arabic.Count:N0} synsets with Arabic");

Console.Write("joining … ");
var (rows, stats) = LexiconBuilder.Build(senses, synsets, arabic, cefr);
Console.WriteLine($"{rows.Count:N0} rows");
Console.WriteLine();

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
                     "DefinitionEn","MeaningAr","CefrLevel","FrequencyRank",
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
            "DefinitionEn","MeaningAr","CefrLevel","FrequencyRank",
            "SourceFlags","UpdatedAt")
        SELECT "SenseId","Text","TextNormalized","Lemma","PartOfSpeech",
               "DefinitionEn","MeaningAr","CefrLevel","FrequencyRank",
               "SourceFlags","UpdatedAt"
        FROM lexicon_staging
        ON CONFLICT ("SenseId") DO UPDATE SET
            "Text"           = EXCLUDED."Text",
            "TextNormalized" = EXCLUDED."TextNormalized",
            "Lemma"          = EXCLUDED."Lemma",
            "PartOfSpeech"   = EXCLUDED."PartOfSpeech",
            "DefinitionEn"   = EXCLUDED."DefinitionEn",
            "MeaningAr"      = EXCLUDED."MeaningAr",
            "CefrLevel"      = EXCLUDED."CefrLevel",
            "FrequencyRank"  = EXCLUDED."FrequencyRank",
            "SourceFlags"    = EXCLUDED."SourceFlags",
            "UpdatedAt"      = EXCLUDED."UpdatedAt";
        """;
    merge.CommandTimeout = 600;
    affected = await merge.ExecuteNonQueryAsync();
}

await using (var count = connection.CreateCommand())
{
    count.CommandText = "SELECT count(*) FROM lexicon_entries;";
    var total = (long)(await count.ExecuteScalarAsync() ?? 0L);
    Console.WriteLine($"  merged {affected:N0} rows; table now holds {total:N0}");
}

await transaction.CommitAsync();

Console.WriteLine($"\nDone in {sw.Elapsed.TotalSeconds:F1}s.");
return 0;

/// <summary>Type anchor for user-secrets lookup.</summary>
internal sealed class LexiconImporterMarker;
