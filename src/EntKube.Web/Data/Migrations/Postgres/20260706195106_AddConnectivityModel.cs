using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EntKube.Web.Data.Migrations.Postgres
{
    /// <inheritdoc />
    public partial class AddConnectivityModel : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AppServicePorts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    AppId = table.Column<Guid>(type: "uuid", nullable: false),
                    EnvironmentId = table.Column<Guid>(type: "uuid", nullable: false),
                    AppDeploymentId = table.Column<Guid>(type: "uuid", nullable: true),
                    Namespace = table.Column<string>(type: "character varying(63)", maxLength: 63, nullable: true),
                    ServiceName = table.Column<string>(type: "character varying(253)", maxLength: 253, nullable: false),
                    Port = table.Column<int>(type: "integer", nullable: false),
                    TargetPort = table.Column<int>(type: "integer", nullable: true),
                    Protocol = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    PortName = table.Column<string>(type: "character varying(63)", maxLength: 63, nullable: true),
                    AppProtocol = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: true),
                    Source = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Description = table.Column<string>(type: "text", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AppServicePorts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AppServicePorts_Apps_AppId",
                        column: x => x.AppId,
                        principalTable: "Apps",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_AppServicePorts_Environments_EnvironmentId",
                        column: x => x.EnvironmentId,
                        principalTable: "Environments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ConnectivityRules",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    AppId = table.Column<Guid>(type: "uuid", nullable: false),
                    EnvironmentId = table.Column<Guid>(type: "uuid", nullable: false),
                    Direction = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    PeerType = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    PeerAppId = table.Column<Guid>(type: "uuid", nullable: true),
                    PeerNamespace = table.Column<string>(type: "character varying(63)", maxLength: 63, nullable: true),
                    PeerSelector = table.Column<string>(type: "text", nullable: true),
                    PeerCidr = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    Port = table.Column<int>(type: "integer", nullable: true),
                    Protocol = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    AppProtocol = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: true),
                    IsEnabled = table.Column<bool>(type: "boolean", nullable: false),
                    Source = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Description = table.Column<string>(type: "text", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ConnectivityRules", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ConnectivityRules_Apps_AppId",
                        column: x => x.AppId,
                        principalTable: "Apps",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ConnectivityRules_Apps_PeerAppId",
                        column: x => x.PeerAppId,
                        principalTable: "Apps",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ConnectivityRules_Environments_EnvironmentId",
                        column: x => x.EnvironmentId,
                        principalTable: "Environments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ExternalDependencies",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    AppId = table.Column<Guid>(type: "uuid", nullable: false),
                    EnvironmentId = table.Column<Guid>(type: "uuid", nullable: false),
                    Host = table.Column<string>(type: "character varying(253)", maxLength: 253, nullable: false),
                    Port = table.Column<int>(type: "integer", nullable: false),
                    Protocol = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    Tls = table.Column<bool>(type: "boolean", nullable: false),
                    Source = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Description = table.Column<string>(type: "text", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ExternalDependencies", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ExternalDependencies_Apps_AppId",
                        column: x => x.AppId,
                        principalTable: "Apps",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ExternalDependencies_Environments_EnvironmentId",
                        column: x => x.EnvironmentId,
                        principalTable: "Environments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AppServicePorts_AppId_EnvironmentId",
                table: "AppServicePorts",
                columns: new[] { "AppId", "EnvironmentId" });

            migrationBuilder.CreateIndex(
                name: "IX_AppServicePorts_EnvironmentId",
                table: "AppServicePorts",
                column: "EnvironmentId");

            migrationBuilder.CreateIndex(
                name: "IX_ConnectivityRules_AppId_EnvironmentId",
                table: "ConnectivityRules",
                columns: new[] { "AppId", "EnvironmentId" });

            migrationBuilder.CreateIndex(
                name: "IX_ConnectivityRules_EnvironmentId",
                table: "ConnectivityRules",
                column: "EnvironmentId");

            migrationBuilder.CreateIndex(
                name: "IX_ConnectivityRules_PeerAppId",
                table: "ConnectivityRules",
                column: "PeerAppId");

            migrationBuilder.CreateIndex(
                name: "IX_ExternalDependencies_AppId_EnvironmentId",
                table: "ExternalDependencies",
                columns: new[] { "AppId", "EnvironmentId" });

            migrationBuilder.CreateIndex(
                name: "IX_ExternalDependencies_EnvironmentId",
                table: "ExternalDependencies",
                column: "EnvironmentId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AppServicePorts");

            migrationBuilder.DropTable(
                name: "ConnectivityRules");

            migrationBuilder.DropTable(
                name: "ExternalDependencies");
        }
    }
}
