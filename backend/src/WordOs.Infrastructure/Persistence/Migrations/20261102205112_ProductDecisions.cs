using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WordOs.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ProductDecisions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "FilterJson",
                table: "admin_inquiries",
                type: "jsonb",
                nullable: true);

            migrationBuilder.AddColumn<DateOnly>(
                name: "FollowUpOn",
                table: "admin_inquiries",
                type: "date",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "ParentId",
                table: "admin_inquiries",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "product_decisions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    AuthorId = table.Column<Guid>(type: "uuid", nullable: false),
                    Title = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Area = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Problem = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: false),
                    Evidence = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    Hypothesis = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    Action = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: false),
                    Assignee = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    ExpectedOutcome = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    InquiryId = table.Column<Guid>(type: "uuid", nullable: true),
                    Segment = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    BaselineFrom = table.Column<DateOnly>(type: "date", nullable: true),
                    BaselineTo = table.Column<DateOnly>(type: "date", nullable: true),
                    Metrics = table.Column<string>(type: "jsonb", nullable: false),
                    Status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    ApprovedById = table.Column<Guid>(type: "uuid", nullable: true),
                    ApprovedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ImplementedOn = table.Column<DateOnly>(type: "date", nullable: true),
                    ReviewOn = table.Column<DateOnly>(type: "date", nullable: true),
                    ActualOutcome = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    Verdict = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                    EvaluationJson = table.Column<string>(type: "jsonb", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_product_decisions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_product_decisions_admin_inquiries_InquiryId",
                        column: x => x.InquiryId,
                        principalTable: "admin_inquiries",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_product_decisions_users_AuthorId",
                        column: x => x.AuthorId,
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "product_changes",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    AuthorId = table.Column<Guid>(type: "uuid", nullable: false),
                    Title = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Kind = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    IsExperiment = table.Column<bool>(type: "boolean", nullable: false),
                    Description = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: false),
                    Problem = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    Hypothesis = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    Version = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    ShippedOn = table.Column<DateOnly>(type: "date", nullable: false),
                    Segment = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    ComparisonSegment = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    DecisionId = table.Column<Guid>(type: "uuid", nullable: true),
                    Metrics = table.Column<string>(type: "jsonb", nullable: false),
                    WindowDays = table.Column<int>(type: "integer", nullable: false),
                    SideEffects = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    Result = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    Recommendation = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    Verdict = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                    EvaluationJson = table.Column<string>(type: "jsonb", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_product_changes", x => x.Id);
                    table.ForeignKey(
                        name: "FK_product_changes_product_decisions_DecisionId",
                        column: x => x.DecisionId,
                        principalTable: "product_decisions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_product_changes_users_AuthorId",
                        column: x => x.AuthorId,
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_admin_inquiries_ParentId",
                table: "admin_inquiries",
                column: "ParentId");

            migrationBuilder.CreateIndex(
                name: "IX_product_changes_AuthorId",
                table: "product_changes",
                column: "AuthorId");

            migrationBuilder.CreateIndex(
                name: "IX_product_changes_DecisionId",
                table: "product_changes",
                column: "DecisionId");

            migrationBuilder.CreateIndex(
                name: "IX_product_changes_ShippedOn",
                table: "product_changes",
                column: "ShippedOn");

            migrationBuilder.CreateIndex(
                name: "IX_product_decisions_AuthorId",
                table: "product_decisions",
                column: "AuthorId");

            migrationBuilder.CreateIndex(
                name: "IX_product_decisions_InquiryId",
                table: "product_decisions",
                column: "InquiryId");

            migrationBuilder.CreateIndex(
                name: "IX_product_decisions_Status_ReviewOn",
                table: "product_decisions",
                columns: new[] { "Status", "ReviewOn" });

            // Tables created by wordos_migrator reach the service's role with no
            // privileges on Neon, where default privileges belong to another
            // role (docs/09-DEPLOYMENT.md) — the deploy then succeeds and the
            // first request answers 500. Granted here so it cannot be forgotten;
            // guarded, because a test or fresh database may have no such role.
            migrationBuilder.Sql("""
                DO $$
                BEGIN
                    IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'wordos_app') THEN
                        GRANT SELECT, INSERT, UPDATE, DELETE ON product_decisions, product_changes TO wordos_app;
                    END IF;
                END
                $$;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "product_changes");

            migrationBuilder.DropTable(
                name: "product_decisions");

            migrationBuilder.DropIndex(
                name: "IX_admin_inquiries_ParentId",
                table: "admin_inquiries");

            migrationBuilder.DropColumn(
                name: "FilterJson",
                table: "admin_inquiries");

            migrationBuilder.DropColumn(
                name: "FollowUpOn",
                table: "admin_inquiries");

            migrationBuilder.DropColumn(
                name: "ParentId",
                table: "admin_inquiries");
        }
    }
}
