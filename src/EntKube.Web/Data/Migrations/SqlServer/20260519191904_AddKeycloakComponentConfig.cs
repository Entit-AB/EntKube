using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EntKube.Web.Data.Migrations.SqlServer
{
    /// <inheritdoc />
    public partial class AddKeycloakComponentConfig : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // KeycloakConnections and KeycloakRealms were originally created by a migration
            // removed from history. This migration recreates them in their final form
            // (KeycloakComponentConfigId instead of KeycloakConnectionId) using IF NOT EXISTS
            // guards so it is a no-op on databases that were already migrated correctly.

            migrationBuilder.Sql("""
                IF OBJECT_ID('[KeycloakComponentConfigs]', 'U') IS NULL
                BEGIN
                    CREATE TABLE [KeycloakComponentConfigs] (
                        [Id] uniqueidentifier NOT NULL,
                        [TenantId] uniqueidentifier NOT NULL,
                        [ClusterComponentId] uniqueidentifier NOT NULL,
                        [CnpgDatabaseId] uniqueidentifier NULL,
                        [AdminUsername] nvarchar(100) NOT NULL DEFAULT N'',
                        [AdminUrl] nvarchar(500) NULL,
                        [CreatedAt] datetime2 NOT NULL,
                        CONSTRAINT [PK_KeycloakComponentConfigs] PRIMARY KEY ([Id]),
                        CONSTRAINT [FK_KeycloakComponentConfigs_ClusterComponents_ClusterComponentId]
                            FOREIGN KEY ([ClusterComponentId]) REFERENCES [ClusterComponents] ([Id]) ON DELETE CASCADE,
                        CONSTRAINT [FK_KeycloakComponentConfigs_CnpgDatabases_CnpgDatabaseId]
                            FOREIGN KEY ([CnpgDatabaseId]) REFERENCES [CnpgDatabases] ([Id]) ON DELETE SET NULL,
                        CONSTRAINT [FK_KeycloakComponentConfigs_Tenants_TenantId]
                            FOREIGN KEY ([TenantId]) REFERENCES [Tenants] ([Id]) ON DELETE CASCADE
                    );
                END
                """);

            migrationBuilder.Sql("""
                IF OBJECT_ID('[KeycloakRealms]', 'U') IS NULL
                BEGIN
                    CREATE TABLE [KeycloakRealms] (
                        [Id] uniqueidentifier NOT NULL,
                        [AccountTheme] nvarchar(max) NULL,
                        [CreatedAt] datetime2 NOT NULL,
                        [DisplayName] nvarchar(200) NOT NULL DEFAULT N'',
                        [Enabled] bit NOT NULL DEFAULT 0,
                        [KeycloakComponentConfigId] uniqueidentifier NOT NULL,
                        [LinkedAppId] uniqueidentifier NULL,
                        [LoginTheme] nvarchar(max) NULL,
                        [RealmName] nvarchar(100) NOT NULL DEFAULT N'',
                        [TenantId] uniqueidentifier NOT NULL,
                        CONSTRAINT [PK_KeycloakRealms] PRIMARY KEY ([Id]),
                        CONSTRAINT [FK_KeycloakRealms_Apps_LinkedAppId]
                            FOREIGN KEY ([LinkedAppId]) REFERENCES [Apps] ([Id]) ON DELETE SET NULL,
                        CONSTRAINT [FK_KeycloakRealms_KeycloakComponentConfigs_KeycloakComponentConfigId]
                            FOREIGN KEY ([KeycloakComponentConfigId]) REFERENCES [KeycloakComponentConfigs] ([Id]) ON DELETE CASCADE
                    );
                END
                """);

            migrationBuilder.Sql("""
                IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_KeycloakComponentConfigs_ClusterComponentId' AND object_id = OBJECT_ID(N'[KeycloakComponentConfigs]'))
                    CREATE INDEX [IX_KeycloakComponentConfigs_ClusterComponentId] ON [KeycloakComponentConfigs] ([ClusterComponentId]);
                """);

            migrationBuilder.Sql("""
                IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_KeycloakComponentConfigs_CnpgDatabaseId' AND object_id = OBJECT_ID(N'[KeycloakComponentConfigs]'))
                    CREATE INDEX [IX_KeycloakComponentConfigs_CnpgDatabaseId] ON [KeycloakComponentConfigs] ([CnpgDatabaseId]);
                """);

            migrationBuilder.Sql("""
                IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_KeycloakComponentConfigs_TenantId' AND object_id = OBJECT_ID(N'[KeycloakComponentConfigs]'))
                    CREATE INDEX [IX_KeycloakComponentConfigs_TenantId] ON [KeycloakComponentConfigs] ([TenantId]);
                """);

            migrationBuilder.Sql("""
                IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_KeycloakRealms_LinkedAppId' AND object_id = OBJECT_ID(N'[KeycloakRealms]'))
                    CREATE INDEX [IX_KeycloakRealms_LinkedAppId] ON [KeycloakRealms] ([LinkedAppId]);
                """);

            migrationBuilder.Sql("""
                IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_KeycloakRealms_KeycloakComponentConfigId_RealmName' AND object_id = OBJECT_ID(N'[KeycloakRealms]'))
                    CREATE UNIQUE INDEX [IX_KeycloakRealms_KeycloakComponentConfigId_RealmName] ON [KeycloakRealms] ([KeycloakComponentConfigId], [RealmName]);
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(name: "KeycloakRealms");
            migrationBuilder.DropTable(name: "KeycloakComponentConfigs");
        }
    }
}
