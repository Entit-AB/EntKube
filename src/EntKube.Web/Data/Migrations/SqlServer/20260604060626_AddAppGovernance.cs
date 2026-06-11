using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EntKube.Web.Data.Migrations.SqlServer
{
    /// <inheritdoc />
    public partial class AddAppGovernance : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Namespace",
                table: "Apps",
                type: "nvarchar(63)",
                maxLength: 63,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "AppNetworkPolicies",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AppId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(63)", maxLength: 63, nullable: false),
                    PolicyType = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    AllowFromNamespace = table.Column<string>(type: "nvarchar(63)", maxLength: 63, nullable: true),
                    CustomYaml = table.Column<string>(type: "nvarchar(max)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AppNetworkPolicies", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AppNetworkPolicies_Apps_AppId",
                        column: x => x.AppId,
                        principalTable: "Apps",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "AppQuotas",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AppId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CpuRequest = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: true),
                    CpuLimit = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: true),
                    MemoryRequest = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: true),
                    MemoryLimit = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: true),
                    MaxPods = table.Column<int>(type: "int", nullable: true),
                    MaxPvcs = table.Column<int>(type: "int", nullable: true),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AppQuotas", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AppQuotas_Apps_AppId",
                        column: x => x.AppId,
                        principalTable: "Apps",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "AppRbacPolicies",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AppId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ServiceAccountName = table.Column<string>(type: "nvarchar(63)", maxLength: 63, nullable: false),
                    AutoMountToken = table.Column<bool>(type: "bit", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AppRbacPolicies", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AppRbacPolicies_Apps_AppId",
                        column: x => x.AppId,
                        principalTable: "Apps",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "AppRbacRules",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AppRbacPolicyId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ApiGroups = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Resources = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false),
                    Verbs = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AppRbacRules", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AppRbacRules_AppRbacPolicies_AppRbacPolicyId",
                        column: x => x.AppRbacPolicyId,
                        principalTable: "AppRbacPolicies",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AppNetworkPolicies_AppId_Name",
                table: "AppNetworkPolicies",
                columns: new[] { "AppId", "Name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AppQuotas_AppId",
                table: "AppQuotas",
                column: "AppId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AppRbacPolicies_AppId",
                table: "AppRbacPolicies",
                column: "AppId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AppRbacRules_AppRbacPolicyId",
                table: "AppRbacRules",
                column: "AppRbacPolicyId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AppNetworkPolicies");

            migrationBuilder.DropTable(
                name: "AppQuotas");

            migrationBuilder.DropTable(
                name: "AppRbacRules");

            migrationBuilder.DropTable(
                name: "AppRbacPolicies");

            migrationBuilder.DropColumn(
                name: "Namespace",
                table: "Apps");
        }
    }
}
