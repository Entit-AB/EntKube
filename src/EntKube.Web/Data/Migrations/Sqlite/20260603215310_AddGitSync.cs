using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EntKube.Web.Data.Migrations.Sqlite
{
    /// <inheritdoc />
    public partial class AddGitSync : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "GitRepositoryId",
                table: "VaultSecrets",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SourceFile",
                table: "DeploymentManifests",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "GitAutoSync",
                table: "AppDeployments",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateTime>(
                name: "GitLastSyncedAt",
                table: "AppDeployments",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "GitLastSyncedCommit",
                table: "AppDeployments",
                type: "TEXT",
                maxLength: 40,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "GitPath",
                table: "AppDeployments",
                type: "TEXT",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "GitRepositoryId",
                table: "AppDeployments",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "GitRevision",
                table: "AppDeployments",
                type: "TEXT",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "ParentDeploymentId",
                table: "AppDeployments",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "GitRepositories",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    TenantId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    Url = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: false),
                    AuthType = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    Username = table.Column<string>(type: "TEXT", maxLength: 300, nullable: true),
                    DefaultBranch = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false, defaultValue: "main"),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GitRepositories", x => x.Id);
                    table.ForeignKey(
                        name: "FK_GitRepositories_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "GitKnownHosts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    TenantId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Hostname = table.Column<string>(type: "TEXT", maxLength: 253, nullable: false),
                    Fingerprint = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    KeyType = table.Column<string>(type: "TEXT", maxLength: 50, nullable: false),
                    TrustedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    GitRepositoryId = table.Column<Guid>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GitKnownHosts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_GitKnownHosts_GitRepositories_GitRepositoryId",
                        column: x => x.GitRepositoryId,
                        principalTable: "GitRepositories",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_GitKnownHosts_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_VaultSecrets_GitRepositoryId",
                table: "VaultSecrets",
                column: "GitRepositoryId");

            migrationBuilder.CreateIndex(
                name: "IX_AppDeployments_GitRepositoryId",
                table: "AppDeployments",
                column: "GitRepositoryId");

            migrationBuilder.CreateIndex(
                name: "IX_AppDeployments_ParentDeploymentId",
                table: "AppDeployments",
                column: "ParentDeploymentId");

            migrationBuilder.CreateIndex(
                name: "IX_GitKnownHosts_GitRepositoryId",
                table: "GitKnownHosts",
                column: "GitRepositoryId");

            migrationBuilder.CreateIndex(
                name: "IX_GitKnownHosts_TenantId_Hostname",
                table: "GitKnownHosts",
                columns: new[] { "TenantId", "Hostname" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_GitRepositories_TenantId_Name",
                table: "GitRepositories",
                columns: new[] { "TenantId", "Name" },
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_AppDeployments_AppDeployments_ParentDeploymentId",
                table: "AppDeployments",
                column: "ParentDeploymentId",
                principalTable: "AppDeployments",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_AppDeployments_GitRepositories_GitRepositoryId",
                table: "AppDeployments",
                column: "GitRepositoryId",
                principalTable: "GitRepositories",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.AddForeignKey(
                name: "FK_VaultSecrets_GitRepositories_GitRepositoryId",
                table: "VaultSecrets",
                column: "GitRepositoryId",
                principalTable: "GitRepositories",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_AppDeployments_AppDeployments_ParentDeploymentId",
                table: "AppDeployments");

            migrationBuilder.DropForeignKey(
                name: "FK_AppDeployments_GitRepositories_GitRepositoryId",
                table: "AppDeployments");

            migrationBuilder.DropForeignKey(
                name: "FK_VaultSecrets_GitRepositories_GitRepositoryId",
                table: "VaultSecrets");

            migrationBuilder.DropTable(
                name: "GitKnownHosts");

            migrationBuilder.DropTable(
                name: "GitRepositories");

            migrationBuilder.DropIndex(
                name: "IX_VaultSecrets_GitRepositoryId",
                table: "VaultSecrets");

            migrationBuilder.DropIndex(
                name: "IX_AppDeployments_GitRepositoryId",
                table: "AppDeployments");

            migrationBuilder.DropIndex(
                name: "IX_AppDeployments_ParentDeploymentId",
                table: "AppDeployments");

            migrationBuilder.DropColumn(
                name: "GitRepositoryId",
                table: "VaultSecrets");

            migrationBuilder.DropColumn(
                name: "SourceFile",
                table: "DeploymentManifests");

            migrationBuilder.DropColumn(
                name: "GitAutoSync",
                table: "AppDeployments");

            migrationBuilder.DropColumn(
                name: "GitLastSyncedAt",
                table: "AppDeployments");

            migrationBuilder.DropColumn(
                name: "GitLastSyncedCommit",
                table: "AppDeployments");

            migrationBuilder.DropColumn(
                name: "GitPath",
                table: "AppDeployments");

            migrationBuilder.DropColumn(
                name: "GitRepositoryId",
                table: "AppDeployments");

            migrationBuilder.DropColumn(
                name: "GitRevision",
                table: "AppDeployments");

            migrationBuilder.DropColumn(
                name: "ParentDeploymentId",
                table: "AppDeployments");
        }
    }
}
