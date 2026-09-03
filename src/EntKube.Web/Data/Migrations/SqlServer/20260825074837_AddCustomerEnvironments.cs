using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EntKube.Web.Data.Migrations.SqlServer
{
    /// <inheritdoc />
    public partial class AddCustomerEnvironments : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "CustomerEnvironments",
                columns: table => new
                {
                    CustomerId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    EnvironmentId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    LinkedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CustomerEnvironments", x => new { x.CustomerId, x.EnvironmentId });
                    table.ForeignKey(
                        name: "FK_CustomerEnvironments_Customers_CustomerId",
                        column: x => x.CustomerId,
                        principalTable: "Customers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_CustomerEnvironments_Environments_EnvironmentId",
                        column: x => x.EnvironmentId,
                        principalTable: "Environments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_CustomerEnvironments_EnvironmentId",
                table: "CustomerEnvironments",
                column: "EnvironmentId");

            // Backfill: until now every customer implicitly belonged to every environment
            // in its tenant, because the tenant tree listed them all under each one. Make
            // that explicit only where it was real — a customer is a member of the
            // environments it actually has apps in. A customer with no app in any
            // environment has no discoverable home, so it keeps the old behaviour and is
            // added to every environment in its tenant rather than vanishing from the tree.

            migrationBuilder.Sql("""
                INSERT INTO [CustomerEnvironments] ([CustomerId], [EnvironmentId], [LinkedAt])
                SELECT DISTINCT a.[CustomerId], ae.[EnvironmentId], SYSUTCDATETIME()
                FROM [AppEnvironments] ae
                JOIN [Apps] a ON a.[Id] = ae.[AppId];
                """);

            migrationBuilder.Sql("""
                INSERT INTO [CustomerEnvironments] ([CustomerId], [EnvironmentId], [LinkedAt])
                SELECT c.[Id], e.[Id], SYSUTCDATETIME()
                FROM [Customers] c
                JOIN [Environments] e ON e.[TenantId] = c.[TenantId]
                WHERE NOT EXISTS (
                    SELECT 1 FROM [Apps] a
                    JOIN [AppEnvironments] ae ON ae.[AppId] = a.[Id]
                    WHERE a.[CustomerId] = c.[Id]);
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CustomerEnvironments");
        }
    }
}
