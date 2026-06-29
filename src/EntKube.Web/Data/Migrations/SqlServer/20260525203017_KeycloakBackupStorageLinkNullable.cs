using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EntKube.Web.Data.Migrations.SqlServer
{
    /// <inheritdoc />
    public partial class KeycloakBackupStorageLinkNullable : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // KeycloakBackups was originally created by a migration removed from history.
            // Recreate it in its final form (nullable StorageLinkId) using an IF NOT EXISTS guard.

            migrationBuilder.Sql("""
                IF OBJECT_ID('[KeycloakBackups]', 'U') IS NULL
                BEGIN
                    CREATE TABLE [KeycloakBackups] (
                        [Id] uniqueidentifier NOT NULL,
                        [CompletedAt] datetime2 NULL,
                        [CreatedAt] datetime2 NOT NULL,
                        [KeycloakRealmId] uniqueidentifier NOT NULL,
                        [LastError] nvarchar(max) NULL,
                        [ObjectKey] nvarchar(1024) NOT NULL DEFAULT N'',
                        [RealmName] nvarchar(100) NOT NULL DEFAULT N'',
                        [SizeBytes] bigint NOT NULL DEFAULT 0,
                        [Status] int NOT NULL DEFAULT 0,
                        [StorageLinkId] uniqueidentifier NULL,
                        [TenantId] uniqueidentifier NOT NULL,
                        CONSTRAINT [PK_KeycloakBackups] PRIMARY KEY ([Id]),
                        CONSTRAINT [FK_KeycloakBackups_KeycloakRealms_KeycloakRealmId]
                            FOREIGN KEY ([KeycloakRealmId]) REFERENCES [KeycloakRealms] ([Id]) ON DELETE CASCADE,
                        CONSTRAINT [FK_KeycloakBackups_StorageLinks_StorageLinkId]
                            FOREIGN KEY ([StorageLinkId]) REFERENCES [StorageLinks] ([Id]) ON DELETE SET NULL
                    );
                END
                """);

            migrationBuilder.Sql("""
                IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_KeycloakBackups_KeycloakRealmId' AND object_id = OBJECT_ID(N'[KeycloakBackups]'))
                    CREATE INDEX [IX_KeycloakBackups_KeycloakRealmId] ON [KeycloakBackups] ([KeycloakRealmId]);
                """);

            migrationBuilder.Sql("""
                IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_KeycloakBackups_StorageLinkId' AND object_id = OBJECT_ID(N'[KeycloakBackups]'))
                    CREATE INDEX [IX_KeycloakBackups_StorageLinkId] ON [KeycloakBackups] ([StorageLinkId]);
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(name: "KeycloakBackups");
        }
    }
}
