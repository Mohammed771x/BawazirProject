using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WordOs.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class SkillSessions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "skill_sessions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    Skill = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    LevelUsed = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: false),
                    StartedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CompletedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    IsComplete = table.Column<bool>(type: "boolean", nullable: false),
                    ContentText = table.Column<string>(type: "text", nullable: true),
                    CurrentItemId = table.Column<Guid>(type: "uuid", nullable: true),
                    TranscriptJson = table.Column<string>(type: "text", nullable: true),
                    UsedAiFallback = table.Column<bool>(type: "boolean", nullable: false),
                    PromptVersion = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    AiModel = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    AiTokens = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_skill_sessions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_skill_sessions_users_UserId",
                        column: x => x.UserId,
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "session_items",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    SessionId = table.Column<Guid>(type: "uuid", nullable: false),
                    Position = table.Column<int>(type: "integer", nullable: false),
                    Type = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    WordId = table.Column<Guid>(type: "uuid", nullable: true),
                    Prompt = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: false),
                    OptionsJson = table.Column<string>(type: "text", nullable: false),
                    CorrectAnswer = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    ContextJson = table.Column<string>(type: "text", nullable: true),
                    AudioText = table.Column<string>(type: "character varying(4096)", maxLength: 4096, nullable: true),
                    Clue = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: true),
                    ClueKind = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: true),
                    LettersJson = table.Column<string>(type: "text", nullable: true),
                    InputMode = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                    Hint = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    Attempts = table.Column<int>(type: "integer", nullable: false),
                    FirstAttemptCorrect = table.Column<bool>(type: "boolean", nullable: true),
                    IsCleared = table.Column<bool>(type: "boolean", nullable: false),
                    RequeuedAt = table.Column<int>(type: "integer", nullable: true),
                    LastAnswer = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_session_items", x => x.Id);
                    table.ForeignKey(
                        name: "FK_session_items_skill_sessions_SessionId",
                        column: x => x.SessionId,
                        principalTable: "skill_sessions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_session_items_SessionId_Position",
                table: "session_items",
                columns: new[] { "SessionId", "Position" });

            migrationBuilder.CreateIndex(
                name: "IX_skill_sessions_UserId_Skill_IsComplete",
                table: "skill_sessions",
                columns: new[] { "UserId", "Skill", "IsComplete" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "session_items");

            migrationBuilder.DropTable(
                name: "skill_sessions");
        }
    }
}
