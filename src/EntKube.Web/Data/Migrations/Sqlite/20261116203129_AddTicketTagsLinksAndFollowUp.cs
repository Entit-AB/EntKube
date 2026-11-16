using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EntKube.Web.Data.Migrations.Sqlite
{
    /// <inheritdoc />
    public partial class AddTicketTagsLinksAndFollowUp : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "AutoCloseAt",
                table: "Tickets",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "MergedIntoTicketId",
                table: "Tickets",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "ReminderAt",
                table: "Tickets",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ReminderNote",
                table: "Tickets",
                type: "TEXT",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "ReminderSentAt",
                table: "Tickets",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "RecordedAt",
                table: "TicketEvents",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "TicketLinks",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    TenantId = table.Column<Guid>(type: "TEXT", nullable: false),
                    FromTicketId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ToTicketId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Kind = table.Column<int>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    CreatedBy = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TicketLinks", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TicketLinks_Tickets_FromTicketId",
                        column: x => x.FromTicketId,
                        principalTable: "Tickets",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_TicketLinks_Tickets_ToTicketId",
                        column: x => x.ToTicketId,
                        principalTable: "Tickets",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "TicketSubscriptions",
                columns: table => new
                {
                    TicketId = table.Column<Guid>(type: "TEXT", nullable: false),
                    UserId = table.Column<string>(type: "TEXT", maxLength: 450, nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    Email = table.Column<string>(type: "TEXT", nullable: false),
                    Reason = table.Column<int>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    CreatedBy = table.Column<string>(type: "TEXT", nullable: true),
                    NotifiedThrough = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TicketSubscriptions", x => new { x.TicketId, x.UserId });
                    table.ForeignKey(
                        name: "FK_TicketSubscriptions_Tickets_TicketId",
                        column: x => x.TicketId,
                        principalTable: "Tickets",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "TicketTags",
                columns: table => new
                {
                    TicketId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 50, nullable: false),
                    AddedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    AddedBy = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TicketTags", x => new { x.TicketId, x.Name });
                    table.ForeignKey(
                        name: "FK_TicketTags_Tickets_TicketId",
                        column: x => x.TicketId,
                        principalTable: "Tickets",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Tickets_MergedIntoTicketId",
                table: "Tickets",
                column: "MergedIntoTicketId");

            migrationBuilder.CreateIndex(
                name: "IX_Tickets_TenantId_AutoCloseAt",
                table: "Tickets",
                columns: new[] { "TenantId", "AutoCloseAt" });

            migrationBuilder.CreateIndex(
                name: "IX_Tickets_TenantId_ReminderAt",
                table: "Tickets",
                columns: new[] { "TenantId", "ReminderAt" });

            migrationBuilder.CreateIndex(
                name: "IX_TicketLinks_FromTicketId_ToTicketId_Kind",
                table: "TicketLinks",
                columns: new[] { "FromTicketId", "ToTicketId", "Kind" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TicketLinks_TenantId",
                table: "TicketLinks",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_TicketLinks_ToTicketId",
                table: "TicketLinks",
                column: "ToTicketId");

            migrationBuilder.CreateIndex(
                name: "IX_TicketSubscriptions_UserId",
                table: "TicketSubscriptions",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_TicketTags_Name",
                table: "TicketTags",
                column: "Name");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "TicketLinks");

            migrationBuilder.DropTable(
                name: "TicketSubscriptions");

            migrationBuilder.DropTable(
                name: "TicketTags");

            migrationBuilder.DropIndex(
                name: "IX_Tickets_MergedIntoTicketId",
                table: "Tickets");

            migrationBuilder.DropIndex(
                name: "IX_Tickets_TenantId_AutoCloseAt",
                table: "Tickets");

            migrationBuilder.DropIndex(
                name: "IX_Tickets_TenantId_ReminderAt",
                table: "Tickets");

            migrationBuilder.DropColumn(
                name: "AutoCloseAt",
                table: "Tickets");

            migrationBuilder.DropColumn(
                name: "MergedIntoTicketId",
                table: "Tickets");

            migrationBuilder.DropColumn(
                name: "ReminderAt",
                table: "Tickets");

            migrationBuilder.DropColumn(
                name: "ReminderNote",
                table: "Tickets");

            migrationBuilder.DropColumn(
                name: "ReminderSentAt",
                table: "Tickets");

            migrationBuilder.DropColumn(
                name: "RecordedAt",
                table: "TicketEvents");
        }
    }
}
