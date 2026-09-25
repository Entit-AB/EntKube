using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EntKube.Web.Data.Migrations.Sqlite
{
    /// <inheritdoc />
    public partial class AddOnCallReachability : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "Affiliation",
                table: "OnCallShifts",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "AssigneePhone",
                table: "OnCallShifts",
                type: "TEXT",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "AssigneeTeamsHandle",
                table: "OnCallShifts",
                type: "TEXT",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "HandoverNotes",
                table: "OnCallShifts",
                type: "TEXT",
                maxLength: 2000,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "SubconsultantId",
                table: "OnCallShifts",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "Covers",
                table: "OnCallSchedules",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "Subconsultants",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    TenantId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    Company = table.Column<string>(type: "TEXT", nullable: true),
                    OrganisationNumber = table.Column<string>(type: "TEXT", nullable: true),
                    Email = table.Column<string>(type: "TEXT", nullable: true),
                    Phone = table.Column<string>(type: "TEXT", nullable: true),
                    CustomerId = table.Column<Guid>(type: "TEXT", nullable: true),
                    NotifiedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    ApprovedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    ObjectedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    ObjectionReason = table.Column<string>(type: "TEXT", nullable: true),
                    ConfidentialitySignedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    DataProcessingBoundAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    IsActive = table.Column<bool>(type: "INTEGER", nullable: false),
                    Notes = table.Column<string>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Subconsultants", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Subconsultants_Customers_CustomerId",
                        column: x => x.CustomerId,
                        principalTable: "Customers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_Subconsultants_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_OnCallShifts_SubconsultantId",
                table: "OnCallShifts",
                column: "SubconsultantId");

            migrationBuilder.CreateIndex(
                name: "IX_Subconsultants_CustomerId",
                table: "Subconsultants",
                column: "CustomerId");

            migrationBuilder.CreateIndex(
                name: "IX_Subconsultants_TenantId_IsActive",
                table: "Subconsultants",
                columns: new[] { "TenantId", "IsActive" });

            migrationBuilder.AddForeignKey(
                name: "FK_OnCallShifts_Subconsultants_SubconsultantId",
                table: "OnCallShifts",
                column: "SubconsultantId",
                principalTable: "Subconsultants",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_OnCallShifts_Subconsultants_SubconsultantId",
                table: "OnCallShifts");

            migrationBuilder.DropTable(
                name: "Subconsultants");

            migrationBuilder.DropIndex(
                name: "IX_OnCallShifts_SubconsultantId",
                table: "OnCallShifts");

            migrationBuilder.DropColumn(
                name: "Affiliation",
                table: "OnCallShifts");

            migrationBuilder.DropColumn(
                name: "AssigneePhone",
                table: "OnCallShifts");

            migrationBuilder.DropColumn(
                name: "AssigneeTeamsHandle",
                table: "OnCallShifts");

            migrationBuilder.DropColumn(
                name: "HandoverNotes",
                table: "OnCallShifts");

            migrationBuilder.DropColumn(
                name: "SubconsultantId",
                table: "OnCallShifts");

            migrationBuilder.DropColumn(
                name: "Covers",
                table: "OnCallSchedules");
        }
    }
}
