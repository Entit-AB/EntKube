using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EntKube.Web.Data.Migrations.Postgres
{
    /// <inheritdoc />
    public partial class AddElasticsearchStack : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ElasticsearchClusters",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    KubernetesClusterId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(36)", maxLength: 36, nullable: false),
                    Namespace = table.Column<string>(type: "character varying(63)", maxLength: 63, nullable: false),
                    Version = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    MasterCount = table.Column<int>(type: "integer", nullable: false),
                    MasterCpuRequest = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    MasterMemory = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    MasterStorageSize = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    HotCount = table.Column<int>(type: "integer", nullable: false),
                    HotCpuRequest = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    HotMemory = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    HotStorageSize = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    WarmCount = table.Column<int>(type: "integer", nullable: false),
                    WarmCpuRequest = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    WarmMemory = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    WarmStorageSize = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    ColdCount = table.Column<int>(type: "integer", nullable: false),
                    ColdCpuRequest = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    ColdMemory = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    ColdStorageSize = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    IngestCount = table.Column<int>(type: "integer", nullable: false),
                    IngestCpuRequest = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    IngestMemory = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    IngestStorageSize = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    StorageClass = table.Column<string>(type: "character varying(63)", maxLength: 63, nullable: true),
                    AllowMmap = table.Column<bool>(type: "boolean", nullable: false),
                    ZoneAware = table.Column<bool>(type: "boolean", nullable: false),
                    KibanaEnabled = table.Column<bool>(type: "boolean", nullable: false),
                    KibanaCount = table.Column<int>(type: "integer", nullable: false),
                    KibanaCpuRequest = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    KibanaMemory = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    Health = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    LastError = table.Column<string>(type: "text", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ElasticsearchClusters", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ElasticsearchClusters_KubernetesClusters_KubernetesClusterId",
                        column: x => x.KubernetesClusterId,
                        principalTable: "KubernetesClusters",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ElasticsearchClusters_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ElasticsearchIlmPolicies",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    ElasticsearchClusterId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    IndexPattern = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    UseDataStream = table.Column<bool>(type: "boolean", nullable: false),
                    Shards = table.Column<int>(type: "integer", nullable: false),
                    Replicas = table.Column<int>(type: "integer", nullable: false),
                    RolloverMaxPrimaryShardGb = table.Column<int>(type: "integer", nullable: false),
                    RolloverMaxAgeDays = table.Column<int>(type: "integer", nullable: false),
                    WarmAfterDays = table.Column<int>(type: "integer", nullable: true),
                    ColdAfterDays = table.Column<int>(type: "integer", nullable: true),
                    DeleteAfterDays = table.Column<int>(type: "integer", nullable: true),
                    TemplatePriority = table.Column<int>(type: "integer", nullable: false),
                    LastAppliedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    LastError = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ElasticsearchIlmPolicies", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ElasticsearchIlmPolicies_ElasticsearchClusters_Elasticsearc~",
                        column: x => x.ElasticsearchClusterId,
                        principalTable: "ElasticsearchClusters",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ElasticsearchClusters_KubernetesClusterId_Name_Namespace",
                table: "ElasticsearchClusters",
                columns: new[] { "KubernetesClusterId", "Name", "Namespace" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ElasticsearchClusters_TenantId",
                table: "ElasticsearchClusters",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_ElasticsearchIlmPolicies_ElasticsearchClusterId_Name",
                table: "ElasticsearchIlmPolicies",
                columns: new[] { "ElasticsearchClusterId", "Name" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ElasticsearchIlmPolicies");

            migrationBuilder.DropTable(
                name: "ElasticsearchClusters");
        }
    }
}
