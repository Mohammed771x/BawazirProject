using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WordOs.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class OneOpenSessionPerSkill : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Any database written before this migration may already hold
            // several open sessions for one learner and one skill — that is the
            // defect the index exists to prevent, and PostgreSQL will not build
            // a unique index over data that violates it. So the duplicates are
            // resolved first.
            //
            // Which one survives is chosen to protect the learner's work, not
            // to be simple: the session with the most *attempted* items wins,
            // because that is the one holding answers somebody has already
            // given. Only if none of them was answered does it fall back to the
            // oldest, which is the one "resume rather than fork" always meant
            // to return. The losers are deleted; their items go with them
            // through the existing cascade, and the words they were about never
            // left the queue.
            migrationBuilder.Sql(
                """
                WITH ranked AS (
                    SELECT s."Id",
                           row_number() OVER (
                               PARTITION BY s."UserId", s."Skill"
                               ORDER BY (
                                   SELECT count(*)
                                   FROM session_items i
                                   WHERE i."SessionId" = s."Id"
                                     AND i."Attempts" > 0
                               ) DESC,
                               s."StartedAt" ASC,
                               s."Id" ASC
                           ) AS position
                    FROM skill_sessions s
                    WHERE s."IsComplete" = false
                )
                DELETE FROM skill_sessions
                WHERE "Id" IN (SELECT "Id" FROM ranked WHERE position > 1);
                """);

            migrationBuilder.CreateIndex(
                name: "ix_skill_sessions_one_open_per_skill",
                table: "skill_sessions",
                columns: new[] { "UserId", "Skill" },
                unique: true,
                filter: "\"IsComplete\" = false");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_skill_sessions_one_open_per_skill",
                table: "skill_sessions");
        }
    }
}
