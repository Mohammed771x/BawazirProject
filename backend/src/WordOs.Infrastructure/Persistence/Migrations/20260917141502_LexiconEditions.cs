using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WordOs.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class LexiconEditions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Edition",
                table: "lexicon_entries",
                type: "character varying(32)",
                maxLength: 32,
                nullable: false,
                defaultValue: "oewn-awn");

            migrationBuilder.CreateIndex(
                name: "IX_lexicon_Edition_TextNormalized",
                table: "lexicon_entries",
                columns: new[] { "Edition", "TextNormalized" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_lexicon_Edition_TextNormalized",
                table: "lexicon_entries");

            migrationBuilder.DropColumn(
                name: "Edition",
                table: "lexicon_entries");
        }
    }
}
