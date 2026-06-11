using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EntKube.Web.Data.Migrations.SqlServer
{
    /// <inheritdoc />
    public partial class AddRegisteredPostgres : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "RegisteredPostgresDatabaseId",
                table: "VaultSecrets",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "RegisteredPostgresDatabaseId",
                table: "KeycloakComponentConfigs",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "RegisteredPostgresDatabaseId",
                table: "DatabaseBindings",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "RegisteredPostgresInstances",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    KubernetesClusterId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Namespace = table.Column<string>(type: "nvarchar(63)", maxLength: 63, nullable: false),
                    ServiceName = table.Column<string>(type: "nvarchar(253)", maxLength: 253, nullable: false),
                    Port = table.Column<int>(type: "int", nullable: false),
                    AdminPodName = table.Column<string>(type: "nvarchar(253)", maxLength: 253, nullable: false),
                    AdminUsername = table.Column<string>(type: "nvarchar(63)", maxLength: 63, nullable: false),
                    Notes = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RegisteredPostgresInstances", x => x.Id);
                    table.ForeignKey(
                        name: "FK_RegisteredPostgresInstances_KubernetesClusters_KubernetesClusterId",
                        column: x => x.KubernetesClusterId,
                        principalTable: "KubernetesClusters",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_RegisteredPostgresInstances_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "RegisteredPostgresDatabases",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RegisteredPostgresInstanceId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(63)", maxLength: 63, nullable: false),
                    Owner = table.Column<string>(type: "nvarchar(63)", maxLength: 63, nullable: false),
                    Status = table.Column<int>(type: "int", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RegisteredPostgresDatabases", x => x.Id);
                    table.ForeignKey(
                        name: "FK_RegisteredPostgresDatabases_RegisteredPostgresInstances_RegisteredPostgresInstanceId",
                        column: x => x.RegisteredPostgresInstanceId,
                        principalTable: "RegisteredPostgresInstances",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_VaultSecrets_RegisteredPostgresDatabaseId",
                table: "VaultSecrets",
                column: "RegisteredPostgresDatabaseId");

            migrationBuilder.CreateIndex(
                name: "IX_KeycloakComponentConfigs_RegisteredPostgresDatabaseId",
                table: "KeycloakComponentConfigs",
                column: "RegisteredPostgresDatabaseId");

            migrationBuilder.CreateIndex(
                name: "IX_DatabaseBindings_RegisteredPostgresDatabaseId",
                table: "DatabaseBindings",
                column: "RegisteredPostgresDatabaseId");

            migrationBuilder.CreateIndex(
                name: "IX_RegisteredPostgresDatabases_RegisteredPostgresInstanceId_Name",
                table: "RegisteredPostgresDatabases",
                columns: new[] { "RegisteredPostgresInstanceId", "Name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_RegisteredPostgresInstances_KubernetesClusterId",
                table: "RegisteredPostgresInstances",
                column: "KubernetesClusterId");

            migrationBuilder.CreateIndex(
                name: "IX_RegisteredPostgresInstances_TenantId_Name",
                table: "RegisteredPostgresInstances",
                columns: new[] { "TenantId", "Name" },
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_DatabaseBindings_RegisteredPostgresDatabases_RegisteredPostgresDatabaseId",
                table: "DatabaseBindings",
                column: "RegisteredPostgresDatabaseId",
                principalTable: "RegisteredPostgresDatabases",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_KeycloakComponentConfigs_RegisteredPostgresDatabases_RegisteredPostgresDatabaseId",
                table: "KeycloakComponentConfigs",
                column: "RegisteredPostgresDatabaseId",
                principalTable: "RegisteredPostgresDatabases",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.AddForeignKey(
                name: "FK_VaultSecrets_RegisteredPostgresDatabases_RegisteredPostgresDatabaseId",
                table: "VaultSecrets",
                column: "RegisteredPostgresDatabaseId",
                principalTable: "RegisteredPostgresDatabases",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_DatabaseBindings_RegisteredPostgresDatabases_RegisteredPostgresDatabaseId",
                table: "DatabaseBindings");

            migrationBuilder.DropForeignKey(
                name: "FK_KeycloakComponentConfigs_RegisteredPostgresDatabases_RegisteredPostgresDatabaseId",
                table: "KeycloakComponentConfigs");

            migrationBuilder.DropForeignKey(
                name: "FK_VaultSecrets_RegisteredPostgresDatabases_RegisteredPostgresDatabaseId",
                table: "VaultSecrets");

            migrationBuilder.DropTable(
                name: "RegisteredPostgresDatabases");

            migrationBuilder.DropTable(
                name: "RegisteredPostgresInstances");

            migrationBuilder.DropIndex(
                name: "IX_VaultSecrets_RegisteredPostgresDatabaseId",
                table: "VaultSecrets");

            migrationBuilder.DropIndex(
                name: "IX_KeycloakComponentConfigs_RegisteredPostgresDatabaseId",
                table: "KeycloakComponentConfigs");

            migrationBuilder.DropIndex(
                name: "IX_DatabaseBindings_RegisteredPostgresDatabaseId",
                table: "DatabaseBindings");

            migrationBuilder.DropColumn(
                name: "RegisteredPostgresDatabaseId",
                table: "VaultSecrets");

            migrationBuilder.DropColumn(
                name: "RegisteredPostgresDatabaseId",
                table: "KeycloakComponentConfigs");

            migrationBuilder.DropColumn(
                name: "RegisteredPostgresDatabaseId",
                table: "DatabaseBindings");
        }
    }
}
