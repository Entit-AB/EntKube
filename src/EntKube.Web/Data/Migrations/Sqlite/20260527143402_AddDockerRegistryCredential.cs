using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EntKube.Web.Data.Migrations.Sqlite
{
    /// <inheritdoc />
    public partial class AddDockerRegistryCredential : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "DockerRegistryCredentials",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    VaultId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    RegistryType = table.Column<int>(type: "INTEGER", nullable: false),
                    Server = table.Column<string>(type: "TEXT", maxLength: 500, nullable: false),
                    Username = table.Column<string>(type: "TEXT", maxLength: 300, nullable: false),
                    EncryptedPassword = table.Column<byte[]>(type: "BLOB", nullable: false),
                    PasswordNonce = table.Column<byte[]>(type: "BLOB", nullable: false),
                    Email = table.Column<string>(type: "TEXT", maxLength: 254, nullable: true),
                    AppId = table.Column<Guid>(type: "TEXT", nullable: true),
                    KubernetesClusterId = table.Column<Guid>(type: "TEXT", nullable: true),
                    KubernetesSecretName = table.Column<string>(type: "TEXT", maxLength: 253, nullable: true),
                    KubernetesNamespace = table.Column<string>(type: "TEXT", maxLength: 63, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DockerRegistryCredentials", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DockerRegistryCredentials_Apps_AppId",
                        column: x => x.AppId,
                        principalTable: "Apps",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_DockerRegistryCredentials_KubernetesClusters_KubernetesClusterId",
                        column: x => x.KubernetesClusterId,
                        principalTable: "KubernetesClusters",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_DockerRegistryCredentials_SecretVaults_VaultId",
                        column: x => x.VaultId,
                        principalTable: "SecretVaults",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_DockerRegistryCredentials_AppId",
                table: "DockerRegistryCredentials",
                column: "AppId");

            migrationBuilder.CreateIndex(
                name: "IX_DockerRegistryCredentials_KubernetesClusterId",
                table: "DockerRegistryCredentials",
                column: "KubernetesClusterId");

            migrationBuilder.CreateIndex(
                name: "IX_DockerRegistryCredentials_VaultId",
                table: "DockerRegistryCredentials",
                column: "VaultId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "DockerRegistryCredentials");
        }
    }
}
