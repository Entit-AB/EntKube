using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EntKube.Web.Data.Migrations.Postgres
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
                type: "character varying(63)",
                maxLength: 63,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "AppNetworkPolicies",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    AppId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(63)", maxLength: 63, nullable: false),
                    PolicyType = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    AllowFromNamespace = table.Column<string>(type: "character varying(63)", maxLength: 63, nullable: true),
                    CustomYaml = table.Column<string>(type: "text", nullable: true)
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
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    AppId = table.Column<Guid>(type: "uuid", nullable: false),
                    CpuRequest = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    CpuLimit = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    MemoryRequest = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    MemoryLimit = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    MaxPods = table.Column<int>(type: "integer", nullable: true),
                    MaxPvcs = table.Column<int>(type: "integer", nullable: true),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
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
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    AppId = table.Column<Guid>(type: "uuid", nullable: false),
                    ServiceAccountName = table.Column<string>(type: "character varying(63)", maxLength: 63, nullable: false),
                    AutoMountToken = table.Column<bool>(type: "boolean", nullable: false)
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
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    AppRbacPolicyId = table.Column<Guid>(type: "uuid", nullable: false),
                    ApiGroups = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Resources = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    Verbs = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false)
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
