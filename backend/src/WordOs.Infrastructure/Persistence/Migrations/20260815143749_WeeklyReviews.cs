using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WordOs.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class WeeklyReviews : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "weekly_reviews",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    PeriodStart = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    PeriodEnd = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    TotalWords = table.Column<int>(type: "integer", nullable: false),
                    FirstPassCorrect = table.Column<int>(type: "integer", nullable: false),
                    WeeklyScore = table.Column<double>(type: "double precision", nullable: false),
                    TotalAttempts = table.Column<int>(type: "integer", nullable: false),
                    StartedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CompletedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    IsComplete = table.Column<bool>(type: "boolean", nullable: false),
                    CurrentItemId = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_weekly_reviews", x => x.Id);
                    table.ForeignKey(
                        name: "FK_weekly_reviews_users_UserId",
                        column: x => x.UserId,
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "weekly_review_items",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ReviewId = table.Column<Guid>(type: "uuid", nullable: false),
                    Position = table.Column<int>(type: "integer", nullable: false),
                    WordId = table.Column<Guid>(type: "uuid", nullable: false),
                    Prompt = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    OptionsJson = table.Column<string>(type: "text", nullable: false),
                    CorrectAnswer = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    Attempts = table.Column<int>(type: "integer", nullable: false),
                    FirstAttemptCorrect = table.Column<bool>(type: "boolean", nullable: true),
                    IsCleared = table.Column<bool>(type: "boolean", nullable: false),
                    RequeuedAt = table.Column<int>(type: "integer", nullable: true),
                    AnsweredAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_weekly_review_items", x => x.Id);
                    table.ForeignKey(
                        name: "FK_weekly_review_items_weekly_reviews_ReviewId",
                        column: x => x.ReviewId,
                        principalTable: "weekly_reviews",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_weekly_review_items_words_WordId",
                        column: x => x.WordId,
                        principalTable: "words",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_weekly_review_items_ReviewId_Position",
                table: "weekly_review_items",
                columns: new[] { "ReviewId", "Position" });

            migrationBuilder.CreateIndex(
                name: "IX_weekly_review_items_WordId",
                table: "weekly_review_items",
                column: "WordId");

            migrationBuilder.CreateIndex(
                name: "IX_weekly_reviews_UserId_IsComplete",
                table: "weekly_reviews",
                columns: new[] { "UserId", "IsComplete" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "weekly_review_items");

            migrationBuilder.DropTable(
                name: "weekly_reviews");
        }
    }
}
