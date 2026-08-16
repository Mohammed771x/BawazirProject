using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WordOs.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class WordExposures : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "word_exposures",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    WordId = table.Column<Guid>(type: "uuid", nullable: false),
                    Source = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    SourceId = table.Column<Guid>(type: "uuid", nullable: false),
                    OccurredAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_word_exposures", x => x.Id);
                    table.ForeignKey(
                        name: "FK_word_exposures_words_WordId",
                        column: x => x.WordId,
                        principalTable: "words",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_word_exposures_SourceId",
                table: "word_exposures",
                column: "SourceId");

            migrationBuilder.CreateIndex(
                name: "IX_word_exposures_WordId_Source_SourceId",
                table: "word_exposures",
                columns: new[] { "WordId", "Source", "SourceId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "word_exposures");
        }
    }
}
