using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WordOs.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialSchema : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "lexicon_entries",
                columns: table => new
                {
                    SenseId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Text = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    TextNormalized = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    Lemma = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    PartOfSpeech = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    DefinitionEn = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: false),
                    MeaningAr = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    CefrLevel = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: true),
                    FrequencyRank = table.Column<int>(type: "integer", nullable: true),
                    SourceFlags = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_lexicon_entries", x => x.SenseId);
                });

            migrationBuilder.CreateTable(
                name: "users",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Email = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false),
                    PasswordHash = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    DisplayName = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    Role = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    OnboardingStage = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    LastLoginAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    TimeZone = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_users", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "level_changes",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    Skill = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    PreviousLevel = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: true),
                    NewLevel = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: true),
                    ChangeType = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Reason = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    SessionsConsidered = table.Column<int>(type: "integer", nullable: false),
                    Accuracy = table.Column<double>(type: "double precision", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_level_changes", x => x.Id);
                    table.ForeignKey(
                        name: "FK_level_changes_users_UserId",
                        column: x => x.UserId,
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "user_interests",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    Interest = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    IsCustom = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_user_interests", x => x.Id);
                    table.ForeignKey(
                        name: "FK_user_interests_users_UserId",
                        column: x => x.UserId,
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "user_skill_levels",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    Skill = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    UserSelectedLevel = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: true),
                    SystemAssessedLevel = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: true),
                    Confidence = table.Column<double>(type: "double precision", nullable: false),
                    EvaluationSessions = table.Column<int>(type: "integer", nullable: false),
                    AccuracySum = table.Column<double>(type: "double precision", nullable: false),
                    DailyTargetWords = table.Column<int>(type: "integer", nullable: false),
                    SpellingSupportMode = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_user_skill_levels", x => x.Id);
                    table.CheckConstraint("ck_skill_levels_daily_target", "\"DailyTargetWords\" BETWEEN 5 AND 15");
                    table.ForeignKey(
                        name: "FK_user_skill_levels_users_UserId",
                        column: x => x.UserId,
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "words",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    SenseId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Text = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    Meaning = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    DefinitionEn = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: false),
                    PartOfSpeech = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    CefrLevel = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: false),
                    State = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    CurrentSkill = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                    AddedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    MaturedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ActivatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ArchivedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ExposureCount = table.Column<int>(type: "integer", nullable: false),
                    LastReviewedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_words", x => x.Id);
                    table.ForeignKey(
                        name: "FK_words_users_UserId",
                        column: x => x.UserId,
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "word_events",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    WordId = table.Column<Guid>(type: "uuid", nullable: false),
                    Type = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Skill = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_word_events", x => x.Id);
                    table.ForeignKey(
                        name: "FK_word_events_words_WordId",
                        column: x => x.WordId,
                        principalTable: "words",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "word_skill_states",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    WordId = table.Column<Guid>(type: "uuid", nullable: false),
                    Skill = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    AvailableAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    Attempts = table.Column<int>(type: "integer", nullable: false),
                    PassedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    FailedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    LastAttemptAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_word_skill_states", x => x.Id);
                    table.ForeignKey(
                        name: "FK_word_skill_states_words_WordId",
                        column: x => x.WordId,
                        principalTable: "words",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_level_changes_UserId_CreatedAt",
                table: "level_changes",
                columns: new[] { "UserId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_lexicon_entries_TextNormalized_PartOfSpeech",
                table: "lexicon_entries",
                columns: new[] { "TextNormalized", "PartOfSpeech" });

            migrationBuilder.CreateIndex(
                name: "ix_lexicon_text_prefix",
                table: "lexicon_entries",
                column: "TextNormalized")
                .Annotation("Npgsql:IndexOperators", new[] { "text_pattern_ops" });

            migrationBuilder.CreateIndex(
                name: "IX_user_interests_UserId_Interest",
                table: "user_interests",
                columns: new[] { "UserId", "Interest" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_user_skill_levels_UserId_Skill",
                table: "user_skill_levels",
                columns: new[] { "UserId", "Skill" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_users_Email",
                table: "users",
                column: "Email",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_word_events_WordId_CreatedAt",
                table: "word_events",
                columns: new[] { "WordId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_word_skill_states_Skill_Status_AvailableAt",
                table: "word_skill_states",
                columns: new[] { "Skill", "Status", "AvailableAt" });

            migrationBuilder.CreateIndex(
                name: "IX_word_skill_states_WordId_Skill",
                table: "word_skill_states",
                columns: new[] { "WordId", "Skill" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_words_UserId_SenseId",
                table: "words",
                columns: new[] { "UserId", "SenseId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_words_UserId_State_CurrentSkill",
                table: "words",
                columns: new[] { "UserId", "State", "CurrentSkill" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "level_changes");

            migrationBuilder.DropTable(
                name: "lexicon_entries");

            migrationBuilder.DropTable(
                name: "user_interests");

            migrationBuilder.DropTable(
                name: "user_skill_levels");

            migrationBuilder.DropTable(
                name: "word_events");

            migrationBuilder.DropTable(
                name: "word_skill_states");

            migrationBuilder.DropTable(
                name: "words");

            migrationBuilder.DropTable(
                name: "users");
        }
    }
}
