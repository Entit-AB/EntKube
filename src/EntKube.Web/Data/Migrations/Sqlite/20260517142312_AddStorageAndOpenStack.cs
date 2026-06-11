using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EntKube.Web.Data.Migrations.Sqlite
{
    /// <inheritdoc />
    public partial class AddStorageAndOpenStack : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "OpenStackConnectionId",
                table: "VaultSecrets",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "StorageLinkId",
                table: "VaultSecrets",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "OpenStackConnections",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    TenantId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    AuthUrl = table.Column<string>(type: "TEXT", nullable: false),
                    Region = table.Column<string>(type: "TEXT", nullable: true),
                    ProjectName = table.Column<string>(type: "TEXT", nullable: true),
                    ProjectId = table.Column<string>(type: "TEXT", nullable: true),
                    UserDomainName = table.Column<string>(type: "TEXT", nullable: true),
                    ProjectDomainName = table.Column<string>(type: "TEXT", nullable: true),
                    Username = table.Column<string>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OpenStackConnections", x => x.Id);
                    table.ForeignKey(
                        name: "FK_OpenStackConnections_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "StorageLinks",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    TenantId = table.Column<Guid>(type: "TEXT", nullable: false),
                    EnvironmentId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Provider = table.Column<int>(type: "INTEGER", nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    Endpoint = table.Column<string>(type: "TEXT", nullable: true),
                    BucketName = table.Column<string>(type: "TEXT", nullable: true),
                    Region = table.Column<string>(type: "TEXT", nullable: true),
                    ComponentId = table.Column<Guid>(type: "TEXT", nullable: true),
                    OpenStackConnectionId = table.Column<Guid>(type: "TEXT", nullable: true),
                    Notes = table.Column<string>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StorageLinks", x => x.Id);
                    table.ForeignKey(
                        name: "FK_StorageLinks_ClusterComponents_ComponentId",
                        column: x => x.ComponentId,
                        principalTable: "ClusterComponents",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_StorageLinks_Environments_EnvironmentId",
                        column: x => x.EnvironmentId,
                        principalTable: "Environments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_StorageLinks_OpenStackConnections_OpenStackConnectionId",
                        column: x => x.OpenStackConnectionId,
                        principalTable: "OpenStackConnections",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_StorageLinks_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_VaultSecrets_StorageLinkId",
                table: "VaultSecrets",
                column: "StorageLinkId");

            migrationBuilder.CreateIndex(
                name: "IX_OpenStackConnections_TenantId",
                table: "OpenStackConnections",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_StorageLinks_ComponentId",
                table: "StorageLinks",
                column: "ComponentId");

            migrationBuilder.CreateIndex(
                name: "IX_StorageLinks_EnvironmentId",
                table: "StorageLinks",
                column: "EnvironmentId");

            migrationBuilder.CreateIndex(
                name: "IX_StorageLinks_OpenStackConnectionId",
                table: "StorageLinks",
                column: "OpenStackConnectionId");

            migrationBuilder.CreateIndex(
                name: "IX_StorageLinks_TenantId",
                table: "StorageLinks",
                column: "TenantId");

            migrationBuilder.AddForeignKey(
                name: "FK_VaultSecrets_StorageLinks_StorageLinkId",
                table: "VaultSecrets",
                column: "StorageLinkId",
                principalTable: "StorageLinks",
                principalColumn: "Id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_VaultSecrets_StorageLinks_StorageLinkId",
                table: "VaultSecrets");

            migrationBuilder.DropTable(
                name: "StorageLinks");

            migrationBuilder.DropTable(
                name: "OpenStackConnections");

            migrationBuilder.DropIndex(
                name: "IX_VaultSecrets_StorageLinkId",
                table: "VaultSecrets");

            migrationBuilder.DropColumn(
                name: "OpenStackConnectionId",
                table: "VaultSecrets");

            migrationBuilder.DropColumn(
                name: "StorageLinkId",
                table: "VaultSecrets");
        }
    }
}
