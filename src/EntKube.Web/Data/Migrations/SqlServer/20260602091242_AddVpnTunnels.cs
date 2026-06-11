using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EntKube.Web.Data.Migrations.SqlServer
{
    /// <inheritdoc />
    public partial class AddVpnTunnels : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "VpnRemoteEndpointId",
                table: "VaultSecrets",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "VpnTunnels",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Description = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    TunnelType = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    Status = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    IkeVersion = table.Column<int>(type: "int", nullable: false),
                    IkeProposal = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    EspProposal = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    DpdDelay = table.Column<int>(type: "int", nullable: false),
                    DpdTimeout = table.Column<int>(type: "int", nullable: false),
                    LastStatusCheckAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_VpnTunnels", x => x.Id);
                    table.ForeignKey(
                        name: "FK_VpnTunnels_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "VpnLocalEndpoints",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    VpnTunnelId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ClusterId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ComponentId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Subnets = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: false),
                    PublicIp = table.Column<string>(type: "nvarchar(45)", maxLength: 45, nullable: true),
                    Role = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    Status = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_VpnLocalEndpoints", x => x.Id);
                    table.ForeignKey(
                        name: "FK_VpnLocalEndpoints_ClusterComponents_ComponentId",
                        column: x => x.ComponentId,
                        principalTable: "ClusterComponents",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_VpnLocalEndpoints_KubernetesClusters_ClusterId",
                        column: x => x.ClusterId,
                        principalTable: "KubernetesClusters",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_VpnLocalEndpoints_VpnTunnels_VpnTunnelId",
                        column: x => x.VpnTunnelId,
                        principalTable: "VpnTunnels",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "VpnRemoteEndpoints",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    VpnTunnelId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    PublicIp = table.Column<string>(type: "nvarchar(45)", maxLength: 45, nullable: false),
                    Subnets = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: false),
                    AuthMode = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    Status = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    LastSeenAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_VpnRemoteEndpoints", x => x.Id);
                    table.ForeignKey(
                        name: "FK_VpnRemoteEndpoints_VpnTunnels_VpnTunnelId",
                        column: x => x.VpnTunnelId,
                        principalTable: "VpnTunnels",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_VaultSecrets_VpnRemoteEndpointId",
                table: "VaultSecrets",
                column: "VpnRemoteEndpointId");

            migrationBuilder.CreateIndex(
                name: "IX_VpnLocalEndpoints_ClusterId",
                table: "VpnLocalEndpoints",
                column: "ClusterId");

            migrationBuilder.CreateIndex(
                name: "IX_VpnLocalEndpoints_ComponentId",
                table: "VpnLocalEndpoints",
                column: "ComponentId");

            migrationBuilder.CreateIndex(
                name: "IX_VpnLocalEndpoints_VpnTunnelId",
                table: "VpnLocalEndpoints",
                column: "VpnTunnelId");

            migrationBuilder.CreateIndex(
                name: "IX_VpnRemoteEndpoints_VpnTunnelId",
                table: "VpnRemoteEndpoints",
                column: "VpnTunnelId");

            migrationBuilder.CreateIndex(
                name: "IX_VpnTunnels_TenantId_Name",
                table: "VpnTunnels",
                columns: new[] { "TenantId", "Name" },
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_VaultSecrets_VpnRemoteEndpoints_VpnRemoteEndpointId",
                table: "VaultSecrets",
                column: "VpnRemoteEndpointId",
                principalTable: "VpnRemoteEndpoints",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_VaultSecrets_VpnRemoteEndpoints_VpnRemoteEndpointId",
                table: "VaultSecrets");

            migrationBuilder.DropTable(
                name: "VpnLocalEndpoints");

            migrationBuilder.DropTable(
                name: "VpnRemoteEndpoints");

            migrationBuilder.DropTable(
                name: "VpnTunnels");

            migrationBuilder.DropIndex(
                name: "IX_VaultSecrets_VpnRemoteEndpointId",
                table: "VaultSecrets");

            migrationBuilder.DropColumn(
                name: "VpnRemoteEndpointId",
                table: "VaultSecrets");
        }
    }
}
