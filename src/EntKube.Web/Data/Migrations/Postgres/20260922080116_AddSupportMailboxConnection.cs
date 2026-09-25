using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EntKube.Web.Data.Migrations.Postgres
{
    /// <inheritdoc />
    public partial class AddSupportMailboxConnection : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "SupportMailboxId",
                table: "VaultSecrets",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "SupportMailboxes",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    Host = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    Port = table.Column<int>(type: "integer", nullable: false),
                    UseSsl = table.Column<bool>(type: "boolean", nullable: false),
                    Username = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    Address = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    Folder = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    IsEnabled = table.Column<bool>(type: "boolean", nullable: false),
                    PollIntervalSeconds = table.Column<int>(type: "integer", nullable: false),
                    Disposition = table.Column<int>(type: "integer", nullable: false),
                    MoveToFolder = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    LastSeenUid = table.Column<long>(type: "bigint", nullable: true),
                    LastUidValidity = table.Column<long>(type: "bigint", nullable: true),
                    LastPolledAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    LastMessageAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    LastError = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    ConsecutiveFailures = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SupportMailboxes", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SupportMailboxes_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_VaultSecrets_SupportMailboxId",
                table: "VaultSecrets",
                column: "SupportMailboxId");

            migrationBuilder.CreateIndex(
                name: "IX_SupportMailboxes_TenantId",
                table: "SupportMailboxes",
                column: "TenantId",
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_VaultSecrets_SupportMailboxes_SupportMailboxId",
                table: "VaultSecrets",
                column: "SupportMailboxId",
                principalTable: "SupportMailboxes",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_VaultSecrets_SupportMailboxes_SupportMailboxId",
                table: "VaultSecrets");

            migrationBuilder.DropTable(
                name: "SupportMailboxes");

            migrationBuilder.DropIndex(
                name: "IX_VaultSecrets_SupportMailboxId",
                table: "VaultSecrets");

            migrationBuilder.DropColumn(
                name: "SupportMailboxId",
                table: "VaultSecrets");
        }
    }
}
