using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EntKube.Web.Data.Migrations.Sqlite
{
    /// <inheritdoc />
    public partial class AddProvisionedClusters : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ProvisionedClusters",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    TenantId = table.Column<Guid>(type: "TEXT", nullable: false),
                    KubernetesClusterId = table.Column<Guid>(type: "TEXT", nullable: true),
                    OpenStackConnectionId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 63, nullable: false),
                    EnvironmentId = table.Column<Guid>(type: "TEXT", nullable: false),
                    KubernetesVersion = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    NodeImageName = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    BaseImageName = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    ControlPlaneCount = table.Column<int>(type: "INTEGER", nullable: false),
                    ControlPlaneFlavor = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    ControlPlaneDiskGb = table.Column<int>(type: "INTEGER", nullable: false),
                    ApiEndpoint = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    NetworkMode = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    NodeNetworkId = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    ExternalNetworkId = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    PodCidr = table.Column<string>(type: "TEXT", maxLength: 50, nullable: false),
                    ServiceCidr = table.Column<string>(type: "TEXT", maxLength: 50, nullable: false),
                    DnsNameservers = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    FailureDomain = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    BootstrapFlavor = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    BootstrapNetworkId = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    BootstrapSshUser = table.Column<string>(type: "TEXT", maxLength: 50, nullable: false),
                    DesiredState = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    Generation = table.Column<int>(type: "INTEGER", nullable: false),
                    ObservedGeneration = table.Column<int>(type: "INTEGER", nullable: false),
                    LastReconciledAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    ObservedStateJson = table.Column<string>(type: "TEXT", nullable: true),
                    LastError = table.Column<string>(type: "TEXT", maxLength: 4000, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProvisionedClusters", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ProvisionedClusters_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ProvisionedWorkerPools",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    ProvisionedClusterId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 63, nullable: false),
                    Flavor = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    DiskGb = table.Column<int>(type: "INTEGER", nullable: false),
                    FailureDomain = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    Count = table.Column<int>(type: "INTEGER", nullable: false),
                    MinCount = table.Column<int>(type: "INTEGER", nullable: true),
                    MaxCount = table.Column<int>(type: "INTEGER", nullable: true),
                    KubernetesVersion = table.Column<string>(type: "TEXT", maxLength: 20, nullable: true),
                    LabelsJson = table.Column<string>(type: "TEXT", maxLength: 4000, nullable: true),
                    TaintsJson = table.Column<string>(type: "TEXT", maxLength: 4000, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProvisionedWorkerPools", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ProvisionedWorkerPools_ProvisionedClusters_ProvisionedClusterId",
                        column: x => x.ProvisionedClusterId,
                        principalTable: "ProvisionedClusters",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ProvisionedClusters_KubernetesClusterId",
                table: "ProvisionedClusters",
                column: "KubernetesClusterId");

            migrationBuilder.CreateIndex(
                name: "IX_ProvisionedClusters_TenantId_Name",
                table: "ProvisionedClusters",
                columns: new[] { "TenantId", "Name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ProvisionedWorkerPools_ProvisionedClusterId_Name",
                table: "ProvisionedWorkerPools",
                columns: new[] { "ProvisionedClusterId", "Name" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ProvisionedWorkerPools");

            migrationBuilder.DropTable(
                name: "ProvisionedClusters");
        }
    }
}
