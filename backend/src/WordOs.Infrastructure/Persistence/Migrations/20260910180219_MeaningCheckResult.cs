using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WordOs.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class MeaningCheckResult : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "MeaningCheck",
                table: "words",
                type: "character varying(16)",
                maxLength: 16,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "MeaningCheck",
                table: "words");
        }
    }
}
