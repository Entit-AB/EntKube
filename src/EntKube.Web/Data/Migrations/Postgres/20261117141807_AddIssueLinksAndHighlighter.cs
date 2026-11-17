using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EntKube.Web.Data.Migrations.Postgres
{
    /// <inheritdoc />
    public partial class AddIssueLinksAndHighlighter : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "IssueTrackerId",
                table: "VaultSecrets",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "IssueTrackers",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    Kind = table.Column<int>(type: "integer", nullable: false),
                    Host = table.Column<string>(type: "character varying(253)", maxLength: 253, nullable: false),
                    IsEnabled = table.Column<bool>(type: "boolean", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedBy = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_IssueTrackers", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "TicketHighlights",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    EventId = table.Column<Guid>(type: "uuid", nullable: false),
                    Text = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    Color = table.Column<int>(type: "integer", nullable: false),
                    By = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    At = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TicketHighlights", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TicketHighlights_TicketEvents_EventId",
                        column: x => x.EventId,
                        principalTable: "TicketEvents",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "TicketIssueLinks",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    TicketId = table.Column<Guid>(type: "uuid", nullable: false),
                    Kind = table.Column<int>(type: "integer", nullable: false),
                    Url = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    Reference = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    AddedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    AddedBy = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TicketIssueLinks", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TicketIssueLinks_Tickets_TicketId",
                        column: x => x.TicketId,
                        principalTable: "Tickets",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_VaultSecrets_IssueTrackerId",
                table: "VaultSecrets",
                column: "IssueTrackerId");

            migrationBuilder.CreateIndex(
                name: "IX_IssueTrackers_TenantId_Host",
                table: "IssueTrackers",
                columns: new[] { "TenantId", "Host" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TicketHighlights_EventId",
                table: "TicketHighlights",
                column: "EventId");

            migrationBuilder.CreateIndex(
                name: "IX_TicketHighlights_TenantId",
                table: "TicketHighlights",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_TicketIssueLinks_TenantId",
                table: "TicketIssueLinks",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_TicketIssueLinks_TicketId_Url",
                table: "TicketIssueLinks",
                columns: new[] { "TicketId", "Url" },
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_VaultSecrets_IssueTrackers_IssueTrackerId",
                table: "VaultSecrets",
                column: "IssueTrackerId",
                principalTable: "IssueTrackers",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_VaultSecrets_IssueTrackers_IssueTrackerId",
                table: "VaultSecrets");

            migrationBuilder.DropTable(
                name: "IssueTrackers");

            migrationBuilder.DropTable(
                name: "TicketHighlights");

            migrationBuilder.DropTable(
                name: "TicketIssueLinks");

            migrationBuilder.DropIndex(
                name: "IX_VaultSecrets_IssueTrackerId",
                table: "VaultSecrets");

            migrationBuilder.DropColumn(
                name: "IssueTrackerId",
                table: "VaultSecrets");
        }
    }
}
