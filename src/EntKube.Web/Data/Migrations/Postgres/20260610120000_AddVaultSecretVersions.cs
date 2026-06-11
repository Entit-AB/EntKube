using System;
using EntKube.Web.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EntKube.Web.Data.Migrations.Postgres
{
    [DbContext(typeof(PostgresApplicationDbContext))]
    [Migration("20260610120000_AddVaultSecretVersions")]
    /// <inheritdoc />
    public partial class AddVaultSecretVersions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """ALTER TABLE "VaultSecrets" ADD COLUMN IF NOT EXISTS "UpdatedBy" character varying(254);""");

            migrationBuilder.Sql("""
                CREATE TABLE IF NOT EXISTS "VaultSecretVersions" (
                    "Id" uuid NOT NULL,
                    "SecretId" uuid NOT NULL,
                    "VersionNumber" integer NOT NULL,
                    "EncryptedValue" bytea NOT NULL,
                    "Nonce" bytea NOT NULL,
                    "CreatedBy" character varying(254),
                    "CreatedAt" timestamp with time zone NOT NULL,
                    CONSTRAINT "PK_VaultSecretVersions" PRIMARY KEY ("Id"),
                    CONSTRAINT "FK_VaultSecretVersions_VaultSecrets_SecretId"
                        FOREIGN KEY ("SecretId") REFERENCES "VaultSecrets" ("Id") ON DELETE CASCADE
                );
                """);

            migrationBuilder.Sql("""
                CREATE INDEX IF NOT EXISTS "IX_VaultSecretVersions_SecretId_VersionNumber"
                    ON "VaultSecretVersions" ("SecretId", "VersionNumber");
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "VaultSecretVersions");

            migrationBuilder.DropColumn(
                name: "UpdatedBy",
                table: "VaultSecrets");
        }
    }
}
