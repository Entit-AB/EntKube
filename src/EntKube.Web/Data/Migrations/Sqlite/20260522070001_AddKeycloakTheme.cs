using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EntKube.Web.Data.Migrations.Sqlite
{
    /// <inheritdoc />
    public partial class AddKeycloakTheme : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "KeycloakThemeId",
                table: "KeycloakRealms",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "KeycloakThemes",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    TenantId = table.Column<Guid>(type: "TEXT", nullable: false),
                    KeycloakComponentConfigId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    LoginTheme = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    AccountTheme = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_KeycloakThemes", x => x.Id);
                    table.ForeignKey(
                        name: "FK_KeycloakThemes_KeycloakComponentConfigs_KeycloakComponentConfigId",
                        column: x => x.KeycloakComponentConfigId,
                        principalTable: "KeycloakComponentConfigs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_KeycloakRealms_KeycloakThemeId",
                table: "KeycloakRealms",
                column: "KeycloakThemeId");

            migrationBuilder.CreateIndex(
                name: "IX_KeycloakThemes_KeycloakComponentConfigId_Name",
                table: "KeycloakThemes",
                columns: new[] { "KeycloakComponentConfigId", "Name" },
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_KeycloakRealms_KeycloakThemes_KeycloakThemeId",
                table: "KeycloakRealms",
                column: "KeycloakThemeId",
                principalTable: "KeycloakThemes",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_KeycloakRealms_KeycloakThemes_KeycloakThemeId",
                table: "KeycloakRealms");

            migrationBuilder.DropTable(
                name: "KeycloakThemes");

            migrationBuilder.DropIndex(
                name: "IX_KeycloakRealms_KeycloakThemeId",
                table: "KeycloakRealms");

            migrationBuilder.DropColumn(
                name: "KeycloakThemeId",
                table: "KeycloakRealms");
        }
    }
}
