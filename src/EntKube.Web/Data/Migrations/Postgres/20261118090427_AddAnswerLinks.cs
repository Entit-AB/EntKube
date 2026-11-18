using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EntKube.Web.Data.Migrations.Postgres
{
    /// <inheritdoc />
    public partial class AddAnswerLinks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "KbAnswerLinks",
                columns: table => new
                {
                    AnswerId = table.Column<Guid>(type: "uuid", nullable: false),
                    RelatedId = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_KbAnswerLinks", x => new { x.AnswerId, x.RelatedId });
                    table.ForeignKey(
                        name: "FK_KbAnswerLinks_KbAnswers_AnswerId",
                        column: x => x.AnswerId,
                        principalTable: "KbAnswers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_KbAnswerLinks_KbAnswers_RelatedId",
                        column: x => x.RelatedId,
                        principalTable: "KbAnswers",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "KbAnswerTickets",
                columns: table => new
                {
                    AnswerId = table.Column<Guid>(type: "uuid", nullable: false),
                    TicketId = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    LinkedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    LinkedBy = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_KbAnswerTickets", x => new { x.AnswerId, x.TicketId });
                    table.ForeignKey(
                        name: "FK_KbAnswerTickets_KbAnswers_AnswerId",
                        column: x => x.AnswerId,
                        principalTable: "KbAnswers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_KbAnswerTickets_Tickets_TicketId",
                        column: x => x.TicketId,
                        principalTable: "Tickets",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_KbAnswerLinks_RelatedId",
                table: "KbAnswerLinks",
                column: "RelatedId");

            migrationBuilder.CreateIndex(
                name: "IX_KbAnswerLinks_TenantId",
                table: "KbAnswerLinks",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_KbAnswerTickets_TenantId",
                table: "KbAnswerTickets",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_KbAnswerTickets_TicketId",
                table: "KbAnswerTickets",
                column: "TicketId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "KbAnswerLinks");

            migrationBuilder.DropTable(
                name: "KbAnswerTickets");
        }
    }
}
