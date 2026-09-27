using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WordOs.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class WeeklyReviewRetiresOnRecall : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "ReviewPassedAt",
                table: "words",
                type: "timestamp with time zone",
                nullable: true);

            // Backfill, or the change resurrects every word already reviewed.
            //
            // Ripeness now anchors on the last review rather than the date
            // added, and only ReviewPassedAt retires a word — so without this,
            // every word a learner has ever answered correctly would come back
            // one week after the migration ran. The evidence to set it
            // properly is already stored: the challenge records whether each
            // word was named right on the first attempt.
            //
            // Words answered wrongly are deliberately left null. They are
            // exactly the ones ADR-099 exists to bring back.
            migrationBuilder.Sql(
                """
                UPDATE words AS w
                SET "ReviewPassedAt" = passed.at
                FROM (
                    SELECT i."WordId",
                           MIN(COALESCE(i."AnsweredAt", r."StartedAt")) AS at
                    FROM weekly_review_items AS i
                    JOIN weekly_reviews AS r ON r."Id" = i."ReviewId"
                    WHERE i."FirstAttemptCorrect" IS TRUE
                    GROUP BY i."WordId"
                ) AS passed
                WHERE w."Id" = passed."WordId";
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ReviewPassedAt",
                table: "words");
        }
    }
}
