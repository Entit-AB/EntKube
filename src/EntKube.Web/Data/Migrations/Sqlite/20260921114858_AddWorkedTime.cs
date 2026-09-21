using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EntKube.Web.Data.Migrations.Sqlite
{
    /// <inheritdoc />
    public partial class AddWorkedTime : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "WorkAuthorisations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    TenantId = table.Column<Guid>(type: "TEXT", nullable: false),
                    CustomerId = table.Column<Guid>(type: "TEXT", nullable: false),
                    TicketId = table.Column<Guid>(type: "TEXT", nullable: true),
                    AppId = table.Column<Guid>(type: "TEXT", nullable: true),
                    Month = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Hours = table.Column<decimal>(type: "TEXT", precision: 18, scale: 2, nullable: true),
                    ApprovedBy = table.Column<string>(type: "TEXT", nullable: false),
                    ApprovedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Note = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WorkAuthorisations", x => x.Id);
                    table.ForeignKey(
                        name: "FK_WorkAuthorisations_Apps_AppId",
                        column: x => x.AppId,
                        principalTable: "Apps",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_WorkAuthorisations_Customers_CustomerId",
                        column: x => x.CustomerId,
                        principalTable: "Customers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_WorkAuthorisations_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_WorkAuthorisations_Tickets_TicketId",
                        column: x => x.TicketId,
                        principalTable: "Tickets",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "TimeEntries",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    TenantId = table.Column<Guid>(type: "TEXT", nullable: false),
                    CustomerId = table.Column<Guid>(type: "TEXT", nullable: false),
                    AppId = table.Column<Guid>(type: "TEXT", nullable: true),
                    TicketId = table.Column<Guid>(type: "TEXT", nullable: true),
                    StartedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    EndedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Kind = table.Column<int>(type: "INTEGER", nullable: false),
                    Description = table.Column<string>(type: "TEXT", nullable: false),
                    PerformedBy = table.Column<string>(type: "TEXT", nullable: true),
                    AuthorisationId = table.Column<Guid>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TimeEntries", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TimeEntries_Apps_AppId",
                        column: x => x.AppId,
                        principalTable: "Apps",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_TimeEntries_Customers_CustomerId",
                        column: x => x.CustomerId,
                        principalTable: "Customers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_TimeEntries_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_TimeEntries_Tickets_TicketId",
                        column: x => x.TicketId,
                        principalTable: "Tickets",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_TimeEntries_WorkAuthorisations_AuthorisationId",
                        column: x => x.AuthorisationId,
                        principalTable: "WorkAuthorisations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateIndex(
                name: "IX_TimeEntries_AppId",
                table: "TimeEntries",
                column: "AppId");

            migrationBuilder.CreateIndex(
                name: "IX_TimeEntries_AuthorisationId",
                table: "TimeEntries",
                column: "AuthorisationId");

            migrationBuilder.CreateIndex(
                name: "IX_TimeEntries_CustomerId_StartedAt",
                table: "TimeEntries",
                columns: new[] { "CustomerId", "StartedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_TimeEntries_TenantId",
                table: "TimeEntries",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_TimeEntries_TicketId",
                table: "TimeEntries",
                column: "TicketId");

            migrationBuilder.CreateIndex(
                name: "IX_WorkAuthorisations_AppId",
                table: "WorkAuthorisations",
                column: "AppId");

            migrationBuilder.CreateIndex(
                name: "IX_WorkAuthorisations_CustomerId_Month",
                table: "WorkAuthorisations",
                columns: new[] { "CustomerId", "Month" });

            migrationBuilder.CreateIndex(
                name: "IX_WorkAuthorisations_TenantId",
                table: "WorkAuthorisations",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_WorkAuthorisations_TicketId",
                table: "WorkAuthorisations",
                column: "TicketId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "TimeEntries");

            migrationBuilder.DropTable(
                name: "WorkAuthorisations");
        }
    }
}
