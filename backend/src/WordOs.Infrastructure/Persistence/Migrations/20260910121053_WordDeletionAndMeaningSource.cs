using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WordOs.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class WordDeletionAndMeaningSource : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_words_UserId_SenseId",
                table: "words");

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "DeletedAt",
                table: "words",
                type: "timestamp with time zone",
                nullable: true);

            // "Lexicon", not the scaffolder's "": EF persists this enum by its
            // C# name, and an empty string is not one of them. Every word that
            // existed before ADR-072 got its meaning from the lexicon, because
            // there was no other way to get one — so backfilling them as
            // `Lexicon` is not a convenient default, it is the fact.
            migrationBuilder.AddColumn<string>(
                name: "MeaningSource",
                table: "words",
                type: "character varying(16)",
                maxLength: 16,
                nullable: false,
                defaultValue: "Lexicon");

            migrationBuilder.CreateIndex(
                name: "IX_words_UserId_SenseId",
                table: "words",
                columns: new[] { "UserId", "SenseId" },
                unique: true,
                filter: "\"State\" <> 'Deleted'");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_words_UserId_SenseId",
                table: "words");

            migrationBuilder.DropColumn(
                name: "DeletedAt",
                table: "words");

            migrationBuilder.DropColumn(
                name: "MeaningSource",
                table: "words");

            migrationBuilder.CreateIndex(
                name: "IX_words_UserId_SenseId",
                table: "words",
                columns: new[] { "UserId", "SenseId" },
                unique: true);
        }
    }
}
