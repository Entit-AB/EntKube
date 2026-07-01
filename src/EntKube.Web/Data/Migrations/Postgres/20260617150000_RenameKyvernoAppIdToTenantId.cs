using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EntKube.Web.Data.Migrations.Postgres
{
    /// <inheritdoc />
    public partial class RenameKyvernoAppIdToTenantId : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Guarded: only rename when a legacy "AppId" column is actually present.
            // AddKyvernoPolicies now creates the table with "TenantId" directly, so on a
            // fresh database there is nothing to rename and the original strongly-typed
            // operations would fail. This block fixes legacy AppId-based databases and is a
            // no-op everywhere else.
            migrationBuilder.Sql("""
                DO $$
                BEGIN
                    IF EXISTS (
                        SELECT 1 FROM information_schema.columns
                        WHERE table_name = 'KyvernoPolicies' AND column_name = 'AppId'
                    ) THEN
                        ALTER TABLE "KyvernoPolicies" DROP CONSTRAINT IF EXISTS "FK_KyvernoPolicies_Apps_AppId";
                        ALTER TABLE "KyvernoPolicies" RENAME COLUMN "AppId" TO "TenantId";
                        ALTER INDEX IF EXISTS "IX_KyvernoPolicies_AppId_EnvironmentId_PolicyType"
                            RENAME TO "IX_KyvernoPolicies_TenantId_EnvironmentId_PolicyType";
                        ALTER TABLE "KyvernoPolicies" ADD CONSTRAINT "FK_KyvernoPolicies_Tenants_TenantId"
                            FOREIGN KEY ("TenantId") REFERENCES "Tenants" ("Id") ON DELETE CASCADE;
                    END IF;
                END $$;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_KyvernoPolicies_Tenants_TenantId",
                table: "KyvernoPolicies");

            migrationBuilder.RenameColumn(
                name: "TenantId",
                table: "KyvernoPolicies",
                newName: "AppId");

            migrationBuilder.RenameIndex(
                name: "IX_KyvernoPolicies_TenantId_EnvironmentId_PolicyType",
                table: "KyvernoPolicies",
                newName: "IX_KyvernoPolicies_AppId_EnvironmentId_PolicyType");

            migrationBuilder.AddForeignKey(
                name: "FK_KyvernoPolicies_Apps_AppId",
                table: "KyvernoPolicies",
                column: "AppId",
                principalTable: "Apps",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }
    }
}
