using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WordOs.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ArabicSearchableGlosses : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "MeaningArNormalized",
                table: "lexicon_entries",
                type: "character varying(512)",
                maxLength: 512,
                nullable: false,
                defaultValue: "");

            // Backfilled in SQL rather than in C#: the same fold as
            // `ArabicText.Normalize`, applied to 175k rows in one statement
            // instead of loading them all into memory to write them back.
            //
            // Order matters — the marks are stripped before the letters are
            // folded, because a hamza carrier can sit under a fatha.
            migrationBuilder.Sql(
                """
                UPDATE lexicon_entries
                SET "MeaningArNormalized" = lower(btrim(regexp_replace(
                    translate(
                        regexp_replace("MeaningAr", '[\u064B-\u0655\u0670\u0640]', '', 'g'),
                        'آأإٱىة', 'اااايه'),
                    '\s+', ' ', 'g')));
                """);

            // Searching by meaning is a substring match, which no btree can
            // serve. Trigrams can, and 175k short glosses make a small index.
            migrationBuilder.Sql(
                "CREATE EXTENSION IF NOT EXISTS pg_trgm;");
            migrationBuilder.Sql(
                """
                CREATE INDEX ix_lexicon_meaning_ar_trgm
                ON lexicon_entries USING gin ("MeaningArNormalized" gin_trgm_ops);
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP INDEX IF EXISTS ix_lexicon_meaning_ar_trgm;");
            migrationBuilder.DropColumn(
                name: "MeaningArNormalized",
                table: "lexicon_entries");
        }
    }
}
