using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WordOs.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class PlacementSessions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "placement_sessions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    CurrentItemId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    IsComplete = table.Column<bool>(type: "boolean", nullable: false),
                    StartedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CompletedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    FallbackScoredCount = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_placement_sessions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_placement_sessions_users_UserId",
                        column: x => x.UserId,
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "placement_answers",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    SessionId = table.Column<Guid>(type: "uuid", nullable: false),
                    ItemId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Skill = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Difficulty = table.Column<double>(type: "double precision", nullable: false),
                    Score = table.Column<double>(type: "double precision", nullable: false),
                    AnsweredAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_placement_answers", x => x.Id);
                    table.ForeignKey(
                        name: "FK_placement_answers_placement_sessions_SessionId",
                        column: x => x.SessionId,
                        principalTable: "placement_sessions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_placement_answers_SessionId_ItemId",
                table: "placement_answers",
                columns: new[] { "SessionId", "ItemId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_placement_sessions_UserId_IsComplete",
                table: "placement_sessions",
                columns: new[] { "UserId", "IsComplete" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "placement_answers");

            migrationBuilder.DropTable(
                name: "placement_sessions");
        }
    }
}
