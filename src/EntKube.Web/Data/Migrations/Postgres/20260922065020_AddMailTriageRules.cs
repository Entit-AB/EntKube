using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EntKube.Web.Data.Migrations.Postgres
{
    /// <inheritdoc />
    public partial class AddMailTriageRules : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "MailTriageRules",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    Signal = table.Column<int>(type: "integer", nullable: false),
                    Phrase = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Priority = table.Column<int>(type: "integer", nullable: true),
                    Criterion = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    IsEnabled = table.Column<bool>(type: "boolean", nullable: false),
                    SortOrder = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MailTriageRules", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MailTriageRules_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_MailTriageRules_TenantId_Signal_Phrase",
                table: "MailTriageRules",
                columns: new[] { "TenantId", "Signal", "Phrase" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MailTriageRules_TenantId_Signal_SortOrder",
                table: "MailTriageRules",
                columns: new[] { "TenantId", "Signal", "SortOrder" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "MailTriageRules");
        }
    }
}
