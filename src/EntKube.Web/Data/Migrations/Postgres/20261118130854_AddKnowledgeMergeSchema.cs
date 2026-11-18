using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EntKube.Web.Data.Migrations.Postgres
{
    /// <inheritdoc />
    public partial class AddKnowledgeMergeSchema : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "AppId",
                table: "KbAnswers",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "ReviewedAt",
                table: "KbAnswers",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ReviewedBy",
                table: "KbAnswers",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "Section",
                table: "KbAnswers",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "KbAnswerRevisions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    AnswerId = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    Title = table.Column<string>(type: "text", nullable: true),
                    Body = table.Column<string>(type: "text", nullable: false),
                    BodyFormat = table.Column<int>(type: "integer", nullable: false),
                    SavedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    SavedBy = table.Column<string>(type: "text", nullable: true),
                    Summary = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_KbAnswerRevisions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_KbAnswerRevisions_KbAnswers_AnswerId",
                        column: x => x.AnswerId,
                        principalTable: "KbAnswers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "KnowledgeSectionMoves",
                columns: table => new
                {
                    SectionId = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    AppId = table.Column<Guid>(type: "uuid", nullable: false),
                    AnswerId = table.Column<Guid>(type: "uuid", nullable: false),
                    MovedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Verdict = table.Column<int>(type: "integer", nullable: false),
                    Notes = table.Column<string>(type: "text", nullable: true),
                    LegacyUpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    LegacyReviewedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    LegacyReviewedBy = table.Column<string>(type: "text", nullable: true),
                    LegacyRevisionCount = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_KnowledgeSectionMoves", x => x.SectionId);
                });

            migrationBuilder.CreateIndex(
                name: "IX_KbAnswers_AppId",
                table: "KbAnswers",
                column: "AppId");

            migrationBuilder.CreateIndex(
                name: "IX_KbAnswers_TenantId_AppId",
                table: "KbAnswers",
                columns: new[] { "TenantId", "AppId" });

            migrationBuilder.AddCheckConstraint(
                name: "CK_KbAnswers_AppShape",
                table: "KbAnswers",
                sql: "(\"AppId\" IS NULL AND \"Section\" IS NULL) OR (\"AppId\" IS NOT NULL AND \"Section\" IS NOT NULL AND \"Visibility\" IN (3, 4) AND \"PublishAt\" IS NULL AND \"CustomerId\" IS NOT NULL)");

            migrationBuilder.CreateIndex(
                name: "IX_KbAnswerRevisions_AnswerId_SavedAt",
                table: "KbAnswerRevisions",
                columns: new[] { "AnswerId", "SavedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_KbAnswerRevisions_TenantId",
                table: "KbAnswerRevisions",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_KnowledgeSectionMoves_TenantId",
                table: "KnowledgeSectionMoves",
                column: "TenantId");

            migrationBuilder.AddForeignKey(
                name: "FK_KbAnswers_Apps_AppId",
                table: "KbAnswers",
                column: "AppId",
                principalTable: "Apps",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_KbAnswers_Apps_AppId",
                table: "KbAnswers");

            migrationBuilder.DropTable(
                name: "KbAnswerRevisions");

            migrationBuilder.DropTable(
                name: "KnowledgeSectionMoves");

            migrationBuilder.DropIndex(
                name: "IX_KbAnswers_AppId",
                table: "KbAnswers");

            migrationBuilder.DropIndex(
                name: "IX_KbAnswers_TenantId_AppId",
                table: "KbAnswers");

            migrationBuilder.DropCheckConstraint(
                name: "CK_KbAnswers_AppShape",
                table: "KbAnswers");

            migrationBuilder.DropColumn(
                name: "AppId",
                table: "KbAnswers");

            migrationBuilder.DropColumn(
                name: "ReviewedAt",
                table: "KbAnswers");

            migrationBuilder.DropColumn(
                name: "ReviewedBy",
                table: "KbAnswers");

            migrationBuilder.DropColumn(
                name: "Section",
                table: "KbAnswers");
        }
    }
}
