using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EntKube.Web.Data.Migrations.Postgres
{
    /// <inheritdoc />
    public partial class KeycloakBackupStorageLinkNullable : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // The migration that originally created KeycloakBackups was removed from history.
            // This migration now creates the table directly with the final schema (nullable StorageLinkId).
            migrationBuilder.CreateTable(
                name: "KeycloakBackups",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CompletedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    KeycloakRealmId = table.Column<Guid>(type: "uuid", nullable: false),
                    LastError = table.Column<string>(type: "text", nullable: true),
                    ObjectKey = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: false),
                    RealmName = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    SizeBytes = table.Column<long>(type: "bigint", nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    StorageLinkId = table.Column<Guid>(type: "uuid", nullable: true),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_KeycloakBackups", x => x.Id);
                    table.ForeignKey(
                        name: "FK_KeycloakBackups_KeycloakRealms_KeycloakRealmId",
                        column: x => x.KeycloakRealmId,
                        principalTable: "KeycloakRealms",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_KeycloakBackups_StorageLinks_StorageLinkId",
                        column: x => x.StorageLinkId,
                        principalTable: "StorageLinks",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateIndex(
                name: "IX_KeycloakBackups_KeycloakRealmId",
                table: "KeycloakBackups",
                column: "KeycloakRealmId");

            migrationBuilder.CreateIndex(
                name: "IX_KeycloakBackups_StorageLinkId",
                table: "KeycloakBackups",
                column: "StorageLinkId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(name: "KeycloakBackups");
        }
    }
}
