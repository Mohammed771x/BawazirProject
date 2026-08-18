using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WordOs.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class PlacementEvidence : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "TestVersion",
                table: "placement_sessions",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "AlsoEvidenceFor",
                table: "placement_answers",
                type: "character varying(16)",
                maxLength: 16,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Domain",
                table: "placement_answers",
                type: "character varying(16)",
                maxLength: 16,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "EvaluationJson",
                table: "placement_answers",
                type: "character varying(4000)",
                maxLength: 4000,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Level",
                table: "placement_answers",
                type: "character varying(8)",
                maxLength: 8,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "RawAnswer",
                table: "placement_answers",
                type: "character varying(4000)",
                maxLength: 4000,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "TestVersion",
                table: "placement_sessions");

            migrationBuilder.DropColumn(
                name: "AlsoEvidenceFor",
                table: "placement_answers");

            migrationBuilder.DropColumn(
                name: "Domain",
                table: "placement_answers");

            migrationBuilder.DropColumn(
                name: "EvaluationJson",
                table: "placement_answers");

            migrationBuilder.DropColumn(
                name: "Level",
                table: "placement_answers");

            migrationBuilder.DropColumn(
                name: "RawAnswer",
                table: "placement_answers");
        }
    }
}
