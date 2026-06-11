using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EntKube.Web.Data.Migrations.Sqlite
{
    /// <inheritdoc />
    public partial class AddVaultSecretVersions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "UpdatedBy",
                table: "VaultSecrets",
                type: "TEXT",
                maxLength: 254,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "VaultSecretVersions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    SecretId = table.Column<Guid>(type: "TEXT", nullable: false),
                    VersionNumber = table.Column<int>(type: "INTEGER", nullable: false),
                    EncryptedValue = table.Column<byte[]>(type: "BLOB", nullable: false),
                    Nonce = table.Column<byte[]>(type: "BLOB", nullable: false),
                    CreatedBy = table.Column<string>(type: "TEXT", maxLength: 254, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_VaultSecretVersions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_VaultSecretVersions_VaultSecrets_SecretId",
                        column: x => x.SecretId,
                        principalTable: "VaultSecrets",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_VaultSecretVersions_SecretId_VersionNumber",
                table: "VaultSecretVersions",
                columns: new[] { "SecretId", "VersionNumber" });
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
