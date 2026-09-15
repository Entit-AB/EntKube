using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EntKube.Web.Data.Migrations.Postgres
{
    /// <inheritdoc />
    public partial class AddJitGrants : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "JitGrants",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    CustomerId = table.Column<Guid>(type: "uuid", nullable: false),
                    AppId = table.Column<Guid>(type: "uuid", nullable: false),
                    EnvironmentId = table.Column<Guid>(type: "uuid", nullable: false),
                    KubernetesClusterId = table.Column<Guid>(type: "uuid", nullable: false),
                    Namespace = table.Column<string>(type: "character varying(63)", maxLength: 63, nullable: false),
                    UserId = table.Column<string>(type: "character varying(450)", maxLength: 450, nullable: false),
                    Level = table.Column<int>(type: "integer", nullable: false),
                    Reason = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    TicketRef = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    RequestedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    RequestedBy = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    ApprovedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ApprovedBy = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    DeniedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    DeniedBy = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    ExpiresAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    RevokedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    RevokedBy = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    RevokeReason = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    ServiceAccountName = table.Column<string>(type: "character varying(63)", maxLength: 63, nullable: true),
                    TokenHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    DisplayPrefix = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    TokenExpiresAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    LastUsedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    EncryptedClusterToken = table.Column<byte[]>(type: "bytea", nullable: true),
                    ClusterTokenNonce = table.Column<byte[]>(type: "bytea", nullable: true),
                    ClusterTokenKey = table.Column<byte[]>(type: "bytea", nullable: true),
                    ClusterTokenKeyNonce = table.Column<byte[]>(type: "bytea", nullable: true),
                    TornDownAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_JitGrants", x => x.Id);
                    table.ForeignKey(
                        name: "FK_JitGrants_Apps_AppId",
                        column: x => x.AppId,
                        principalTable: "Apps",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_JitGrants_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_JitGrants_Customers_CustomerId",
                        column: x => x.CustomerId,
                        principalTable: "Customers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_JitGrants_Environments_EnvironmentId",
                        column: x => x.EnvironmentId,
                        principalTable: "Environments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_JitGrants_KubernetesClusters_KubernetesClusterId",
                        column: x => x.KubernetesClusterId,
                        principalTable: "KubernetesClusters",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_JitGrants_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_JitGrants_AppId",
                table: "JitGrants",
                column: "AppId");

            migrationBuilder.CreateIndex(
                name: "IX_JitGrants_CustomerId",
                table: "JitGrants",
                column: "CustomerId");

            migrationBuilder.CreateIndex(
                name: "IX_JitGrants_EnvironmentId",
                table: "JitGrants",
                column: "EnvironmentId");

            migrationBuilder.CreateIndex(
                name: "IX_JitGrants_ExpiresAt_TornDownAt",
                table: "JitGrants",
                columns: new[] { "ExpiresAt", "TornDownAt" });

            migrationBuilder.CreateIndex(
                name: "IX_JitGrants_KubernetesClusterId",
                table: "JitGrants",
                column: "KubernetesClusterId");

            migrationBuilder.CreateIndex(
                name: "IX_JitGrants_TenantId_RequestedAt",
                table: "JitGrants",
                columns: new[] { "TenantId", "RequestedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_JitGrants_TokenHash",
                table: "JitGrants",
                column: "TokenHash");

            migrationBuilder.CreateIndex(
                name: "IX_JitGrants_UserId",
                table: "JitGrants",
                column: "UserId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "JitGrants");
        }
    }
}
