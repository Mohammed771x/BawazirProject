using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WordOs.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class SpellingHintLadder : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Hint",
                table: "session_items");

            migrationBuilder.AddColumn<string>(
                name: "HintsJson",
                table: "session_items",
                type: "jsonb",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "HintsJson",
                table: "session_items");

            migrationBuilder.AddColumn<string>(
                name: "Hint",
                table: "session_items",
                type: "character varying(256)",
                maxLength: 256,
                nullable: true);
        }
    }
}
