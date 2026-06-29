using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EntKube.Web.Data.Migrations.Sqlite
{
    /// <inheritdoc />
    public partial class AddKedaScalers : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "KedaScalers",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    TenantId = table.Column<Guid>(type: "TEXT", nullable: false),
                    AppId = table.Column<Guid>(type: "TEXT", nullable: false),
                    EnvironmentId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 63, nullable: false),
                    Kind = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    ScaleTargetName = table.Column<string>(type: "TEXT", nullable: true),
                    ScaleTargetKind = table.Column<string>(type: "TEXT", maxLength: 63, nullable: false),
                    MinReplicaCount = table.Column<int>(type: "INTEGER", nullable: true),
                    MaxReplicaCount = table.Column<int>(type: "INTEGER", nullable: true),
                    PollingInterval = table.Column<int>(type: "INTEGER", nullable: true),
                    CooldownPeriod = table.Column<int>(type: "INTEGER", nullable: true),
                    TriggersYaml = table.Column<string>(type: "TEXT", nullable: true),
                    CustomYaml = table.Column<string>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_KedaScalers", x => x.Id);
                    table.ForeignKey(
                        name: "FK_KedaScalers_Apps_AppId",
                        column: x => x.AppId,
                        principalTable: "Apps",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_KedaScalers_Environments_EnvironmentId",
                        column: x => x.EnvironmentId,
                        principalTable: "Environments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_KedaScalers_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_KedaScalers_AppId_EnvironmentId_Name",
                table: "KedaScalers",
                columns: new[] { "AppId", "EnvironmentId", "Name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_KedaScalers_EnvironmentId",
                table: "KedaScalers",
                column: "EnvironmentId");

            migrationBuilder.CreateIndex(
                name: "IX_KedaScalers_TenantId_EnvironmentId",
                table: "KedaScalers",
                columns: new[] { "TenantId", "EnvironmentId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "KedaScalers");
        }
    }
}
