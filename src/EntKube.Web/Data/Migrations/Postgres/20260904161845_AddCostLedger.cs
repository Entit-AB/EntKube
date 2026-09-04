using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EntKube.Web.Data.Migrations.Postgres
{
    /// <inheritdoc />
    public partial class AddCostLedger : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "CostLedgerCoverages",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    ClusterId = table.Column<Guid>(type: "uuid", nullable: false),
                    ClusterName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    Day = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CoveredHours = table.Column<decimal>(type: "numeric(18,6)", precision: 18, scale: 6, nullable: false),
                    GapHours = table.Column<decimal>(type: "numeric(18,6)", precision: 18, scale: 6, nullable: false),
                    SampleCount = table.Column<int>(type: "integer", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CostLedgerCoverages", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CostLedgerCoverages_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "CostLedgerCursors",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ClusterId = table.Column<Guid>(type: "uuid", nullable: false),
                    LastSampleAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CostLedgerCursors", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CostLedgerCursors_KubernetesClusters_ClusterId",
                        column: x => x.ClusterId,
                        principalTable: "KubernetesClusters",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "CostLedgerEntries",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    ClusterId = table.Column<Guid>(type: "uuid", nullable: false),
                    ClusterName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    Namespace = table.Column<string>(type: "character varying(253)", maxLength: 253, nullable: false),
                    Day = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CustomerId = table.Column<Guid>(type: "uuid", nullable: true),
                    CustomerName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    AppId = table.Column<Guid>(type: "uuid", nullable: true),
                    AppName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    EnvironmentName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    IsMultiApp = table.Column<bool>(type: "boolean", nullable: false),
                    IsRedistributed = table.Column<bool>(type: "boolean", nullable: false),
                    Hours = table.Column<decimal>(type: "numeric(18,6)", precision: 18, scale: 6, nullable: false),
                    CpuCoreHours = table.Column<double>(type: "double precision", nullable: false),
                    MemoryGiBHours = table.Column<double>(type: "double precision", nullable: false),
                    StorageGiBHours = table.Column<double>(type: "double precision", nullable: false),
                    LoadBalancerHours = table.Column<double>(type: "double precision", nullable: false),
                    PublicIpHours = table.Column<double>(type: "double precision", nullable: false),
                    CpuCost = table.Column<decimal>(type: "numeric(18,6)", precision: 18, scale: 6, nullable: false),
                    MemoryCost = table.Column<decimal>(type: "numeric(18,6)", precision: 18, scale: 6, nullable: false),
                    StorageCost = table.Column<decimal>(type: "numeric(18,6)", precision: 18, scale: 6, nullable: false),
                    NetworkCost = table.Column<decimal>(type: "numeric(18,6)", precision: 18, scale: 6, nullable: false),
                    SharedCost = table.Column<decimal>(type: "numeric(18,6)", precision: 18, scale: 6, nullable: false),
                    Currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    ChargedOnRequests = table.Column<bool>(type: "boolean", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CostLedgerEntries", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CostLedgerEntries_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_CostLedgerCoverages_ClusterId_Day",
                table: "CostLedgerCoverages",
                columns: new[] { "ClusterId", "Day" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CostLedgerCoverages_TenantId_Day",
                table: "CostLedgerCoverages",
                columns: new[] { "TenantId", "Day" });

            migrationBuilder.CreateIndex(
                name: "IX_CostLedgerCursors_ClusterId",
                table: "CostLedgerCursors",
                column: "ClusterId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CostLedgerEntries_ClusterId_Namespace_Day",
                table: "CostLedgerEntries",
                columns: new[] { "ClusterId", "Namespace", "Day" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CostLedgerEntries_TenantId_Day",
                table: "CostLedgerEntries",
                columns: new[] { "TenantId", "Day" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CostLedgerCoverages");

            migrationBuilder.DropTable(
                name: "CostLedgerCursors");

            migrationBuilder.DropTable(
                name: "CostLedgerEntries");
        }
    }
}
