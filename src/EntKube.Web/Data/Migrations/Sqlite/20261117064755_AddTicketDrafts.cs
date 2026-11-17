using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EntKube.Web.Data.Migrations.Sqlite
{
    /// <inheritdoc />
    public partial class AddTicketDrafts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "TicketDrafts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    TenantId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Kind = table.Column<int>(type: "INTEGER", nullable: false),
                    TicketId = table.Column<Guid>(type: "TEXT", nullable: true),
                    OwnerUserId = table.Column<string>(type: "TEXT", maxLength: 450, nullable: true),
                    Name = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    Text = table.Column<string>(type: "TEXT", nullable: false),
                    CustomerVisible = table.Column<bool>(type: "INTEGER", nullable: false),
                    Title = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    CustomerId = table.Column<Guid>(type: "TEXT", nullable: true),
                    AppId = table.Column<Guid>(type: "TEXT", nullable: true),
                    Priority = table.Column<int>(type: "INTEGER", nullable: true),
                    Tags = table.Column<string>(type: "TEXT", maxLength: 500, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    CreatedBy = table.Column<string>(type: "TEXT", nullable: true),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedBy = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TicketDrafts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TicketDrafts_Tickets_TicketId",
                        column: x => x.TicketId,
                        principalTable: "Tickets",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_TicketDrafts_TenantId_Kind",
                table: "TicketDrafts",
                columns: new[] { "TenantId", "Kind" });

            migrationBuilder.CreateIndex(
                name: "IX_TicketDrafts_TicketId_Kind_OwnerUserId",
                table: "TicketDrafts",
                columns: new[] { "TicketId", "Kind", "OwnerUserId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "TicketDrafts");
        }
    }
}
