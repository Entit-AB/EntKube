using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EntKube.Web.Data.Migrations.SqlServer
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
                IF EXISTS (
                    SELECT 1 FROM sys.columns
                    WHERE Name = N'AppId' AND Object_ID = Object_ID(N'[KyvernoPolicies]')
                )
                BEGIN
                    IF EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name = N'FK_KyvernoPolicies_Apps_AppId')
                        ALTER TABLE [KyvernoPolicies] DROP CONSTRAINT [FK_KyvernoPolicies_Apps_AppId];
                    EXEC sp_rename N'[KyvernoPolicies].[AppId]', N'TenantId', N'COLUMN';
                    IF EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_KyvernoPolicies_AppId_EnvironmentId_PolicyType')
                        EXEC sp_rename N'[KyvernoPolicies].[IX_KyvernoPolicies_AppId_EnvironmentId_PolicyType]', N'IX_KyvernoPolicies_TenantId_EnvironmentId_PolicyType', N'INDEX';
                    ALTER TABLE [KyvernoPolicies] ADD CONSTRAINT [FK_KyvernoPolicies_Tenants_TenantId]
                        FOREIGN KEY ([TenantId]) REFERENCES [Tenants] ([Id]) ON DELETE CASCADE;
                END
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
