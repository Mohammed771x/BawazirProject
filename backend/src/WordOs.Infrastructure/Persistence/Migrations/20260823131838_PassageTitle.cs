using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WordOs.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class PassageTitle : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<string>(
                name: "GlossaryJson",
                table: "skill_sessions",
                type: "character varying(120000)",
                maxLength: 120000,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "character varying(20000)",
                oldMaxLength: 20000,
                oldNullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ContentTitle",
                table: "skill_sessions",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ContentTitle",
                table: "skill_sessions");

            migrationBuilder.AlterColumn<string>(
                name: "GlossaryJson",
                table: "skill_sessions",
                type: "character varying(20000)",
                maxLength: 20000,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "character varying(120000)",
                oldMaxLength: 120000,
                oldNullable: true);
        }
    }
}
