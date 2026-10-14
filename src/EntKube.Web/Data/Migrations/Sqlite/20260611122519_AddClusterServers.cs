using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EntKube.Web.Data.Migrations.Sqlite
{
    /// <inheritdoc />
    public partial class AddClusterServers : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""DROP INDEX IF EXISTS "IX_OnCallSchedules_TenantId";""");

            migrationBuilder.AddColumn<string>(
                name: "UpdatedBy",
                table: "VaultSecrets",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AlterColumn<bool>(
                name: "IsEnabled",
                table: "OnCallSchedules",
                type: "INTEGER",
                nullable: false,
                oldClrType: typeof(bool),
                oldType: "INTEGER",
                oldDefaultValue: true);

            // ⚠️ Three AlterColumn calls on AlertRoutingRules were here, and all three did nothing
            // but drop a column default. On SQLite EF cannot alter a column in place: it rebuilds
            // the table from the MODEL SNAPSHOT and copies the old rows across. The snapshot
            // contains SuppressIncident and MatchClusterId — columns created only by
            // 20260611060000_AddAlertRoutingSuppression, which is one of the migrations EF cannot
            // see (docs/migrations-defect.md). So on a database built by running the migrations,
            // the rebuild selected two columns that do not exist.
            //
            // It did not fail, which is why nobody knew. SQLite before 3.50 accepted a
            // double-quoted identifier that resolved to nothing as a string literal, so
            // "MatchClusterId" became the text 'MatchClusterId' and the copy carried on. The
            // columns ended up present (the rebuild's CREATE TABLE came from the snapshot) and
            // empty, which is also why a schema comparison found nothing wrong. SQLite 3.54 —
            // which the CVE-2025-6965 fix requires — refuses it, and eleven from-scratch
            // migration tests fail.
            //
            // So the two columns are added here, where they were always needed, and the three
            // default-dropping alters are gone: a leftover DEFAULT on a column EF always writes
            // explicitly costs nothing, and it is not worth a table rebuild to remove.
            //
            // A database that already ran this migration is untouched — it has the columns, from
            // that same accidental rebuild, and carries the history row that stops it running
            // again.
            migrationBuilder.AddColumn<bool>(
                name: "SuppressIncident",
                table: "AlertRoutingRules",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<Guid>(
                name: "MatchClusterId",
                table: "AlertRoutingRules",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_AlertRoutingRules_MatchClusterId",
                table: "AlertRoutingRules",
                column: "MatchClusterId");

            migrationBuilder.AlterColumn<string>(
                name: "RunbookUrl",
                table: "AlertIncidents",
                type: "TEXT",
                maxLength: 500,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "TEXT",
                oldMaxLength: 500,
                oldDefaultValue: "");

            migrationBuilder.CreateTable(
                name: "ClusterServers",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    ClusterId = table.Column<Guid>(type: "TEXT", nullable: false),
                    NodeName = table.Column<string>(type: "TEXT", maxLength: 253, nullable: true),
                    DisplayName = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    IpAddress = table.Column<string>(type: "TEXT", maxLength: 45, nullable: true),
                    ManagementIpAddress = table.Column<string>(type: "TEXT", maxLength: 45, nullable: true),
                    Provider = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    OsDistribution = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    CpuCores = table.Column<int>(type: "INTEGER", nullable: true),
                    RamGb = table.Column<int>(type: "INTEGER", nullable: true),
                    DiskGb = table.Column<int>(type: "INTEGER", nullable: true),
                    Location = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    SshUser = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    SshPort = table.Column<int>(type: "INTEGER", nullable: false),
                    JumpHost = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    Notes = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ClusterServers", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ClusterServers_KubernetesClusters_ClusterId",
                        column: x => x.ClusterId,
                        principalTable: "KubernetesClusters",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "VaultSecretVersions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    SecretId = table.Column<Guid>(type: "TEXT", nullable: false),
                    VersionNumber = table.Column<int>(type: "INTEGER", nullable: false),
                    EncryptedValue = table.Column<byte[]>(type: "BLOB", nullable: false),
                    Nonce = table.Column<byte[]>(type: "BLOB", nullable: false),
                    CreatedBy = table.Column<string>(type: "TEXT", maxLength: 254, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_VaultSecretVersions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_VaultSecretVersions_VaultSecrets_SecretId",
                        column: x => x.SecretId,
                        principalTable: "VaultSecrets",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_OnCallSchedules_TenantId_Name",
                table: "OnCallSchedules",
                columns: new[] { "TenantId", "Name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ClusterServers_ClusterId_DisplayName",
                table: "ClusterServers",
                columns: new[] { "ClusterId", "DisplayName" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_VaultSecretVersions_SecretId_VersionNumber",
                table: "VaultSecretVersions",
                columns: new[] { "SecretId", "VersionNumber" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ClusterServers");

            migrationBuilder.DropTable(
                name: "VaultSecretVersions");

            migrationBuilder.DropIndex(
                name: "IX_OnCallSchedules_TenantId_Name",
                table: "OnCallSchedules");

            migrationBuilder.DropColumn(
                name: "UpdatedBy",
                table: "VaultSecrets");

            migrationBuilder.AlterColumn<bool>(
                name: "IsEnabled",
                table: "OnCallSchedules",
                type: "INTEGER",
                nullable: false,
                defaultValue: true,
                oldClrType: typeof(bool),
                oldType: "INTEGER");

            migrationBuilder.AlterColumn<string>(
                name: "RunbookUrl",
                table: "AlertIncidents",
                type: "TEXT",
                maxLength: 500,
                nullable: false,
                defaultValue: "",
                oldClrType: typeof(string),
                oldType: "TEXT",
                oldMaxLength: 500);

            migrationBuilder.CreateIndex(
                name: "IX_OnCallSchedules_TenantId",
                table: "OnCallSchedules",
                column: "TenantId");
        }
    }
}
