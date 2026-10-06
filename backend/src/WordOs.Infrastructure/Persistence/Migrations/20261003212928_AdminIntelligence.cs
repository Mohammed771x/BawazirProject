using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace WordOs.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AdminIntelligence : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Category",
                table: "feedback_messages",
                type: "character varying(16)",
                maxLength: 16,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "admin_audit_events",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ActorId = table.Column<Guid>(type: "uuid", nullable: false),
                    Action = table.Column<string>(type: "character varying(48)", maxLength: 48, nullable: false),
                    TargetId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    Detail = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    ClientAddress = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_admin_audit_events", x => x.Id);
                    table.ForeignKey(
                        name: "FK_admin_audit_events_users_ActorId",
                        column: x => x.ActorId,
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "admin_inquiries",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    AuthorId = table.Column<Guid>(type: "uuid", nullable: false),
                    Section = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Question = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    Summary = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    InterpretedBy = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: false),
                    ResultJson = table.Column<string>(type: "jsonb", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_admin_inquiries", x => x.Id);
                    table.ForeignKey(
                        name: "FK_admin_inquiries_users_AuthorId",
                        column: x => x.AuthorId,
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "admin_notes",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    FeedbackId = table.Column<Guid>(type: "uuid", nullable: false),
                    AuthorId = table.Column<Guid>(type: "uuid", nullable: false),
                    Body = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_admin_notes", x => x.Id);
                    table.ForeignKey(
                        name: "FK_admin_notes_feedback_messages_FeedbackId",
                        column: x => x.FeedbackId,
                        principalTable: "feedback_messages",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_admin_notes_users_AuthorId",
                        column: x => x.AuthorId,
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "analytics_events",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(48)", maxLength: 48, nullable: false),
                    Source = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: false),
                    OccurredAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ReceivedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    AppSessionId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    SessionId = table.Column<Guid>(type: "uuid", nullable: true),
                    WordId = table.Column<Guid>(type: "uuid", nullable: true),
                    Skill = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                    Attempt = table.Column<int>(type: "integer", nullable: true),
                    Result = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                    DurationMs = table.Column<int>(type: "integer", nullable: true),
                    ContentLevel = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: true),
                    Screen = table.Column<string>(type: "character varying(48)", maxLength: 48, nullable: true),
                    AppVersion = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    Platform = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                    PropsJson = table.Column<string>(type: "jsonb", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_analytics_events", x => x.Id);
                    table.ForeignKey(
                        name: "FK_analytics_events_users_UserId",
                        column: x => x.UserId,
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_admin_audit_events_ActorId_CreatedAt",
                table: "admin_audit_events",
                columns: new[] { "ActorId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_admin_audit_events_CreatedAt",
                table: "admin_audit_events",
                column: "CreatedAt");

            migrationBuilder.CreateIndex(
                name: "IX_admin_inquiries_AuthorId",
                table: "admin_inquiries",
                column: "AuthorId");

            migrationBuilder.CreateIndex(
                name: "IX_admin_inquiries_CreatedAt",
                table: "admin_inquiries",
                column: "CreatedAt");

            migrationBuilder.CreateIndex(
                name: "IX_admin_notes_AuthorId",
                table: "admin_notes",
                column: "AuthorId");

            migrationBuilder.CreateIndex(
                name: "IX_admin_notes_FeedbackId_CreatedAt",
                table: "admin_notes",
                columns: new[] { "FeedbackId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_analytics_events_Name_OccurredAt",
                table: "analytics_events",
                columns: new[] { "Name", "OccurredAt" });

            migrationBuilder.CreateIndex(
                name: "IX_analytics_events_SessionId",
                table: "analytics_events",
                column: "SessionId");

            migrationBuilder.CreateIndex(
                name: "IX_analytics_events_UserId_OccurredAt",
                table: "analytics_events",
                columns: new[] { "UserId", "OccurredAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "admin_audit_events");

            migrationBuilder.DropTable(
                name: "admin_inquiries");

            migrationBuilder.DropTable(
                name: "admin_notes");

            migrationBuilder.DropTable(
                name: "analytics_events");

            migrationBuilder.DropColumn(
                name: "Category",
                table: "feedback_messages");
        }
    }
}
