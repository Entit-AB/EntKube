using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EntKube.Web.Data.Migrations.Postgres
{
    /// <inheritdoc />
    public partial class AddClusterServers : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // All pre-existing schema cleanup is done with idempotent SQL because the live DB
            // may already have had some of these applied outside of EF migrations.

            migrationBuilder.Sql("DROP INDEX IF EXISTS \"IX_OnCallSchedules_TenantId\";");

            migrationBuilder.Sql(@"ALTER TABLE ""VaultSecrets"" ADD COLUMN IF NOT EXISTS ""UpdatedBy"" text;");

            // AlterColumn: removes stale DEFAULT values — DROP DEFAULT is a no-op if no default exists
            migrationBuilder.AlterColumn<bool>(
                name: "IsEnabled",
                table: "OnCallSchedules",
                type: "boolean",
                nullable: false,
                oldClrType: typeof(bool),
                oldType: "boolean",
                oldDefaultValue: true);

            migrationBuilder.AlterColumn<string>(
                name: "GitUrl",
                table: "AppDeployments",
                type: "text",
                nullable: true,
                oldClrType: typeof(string),
                oldType: "character varying(2000)",
                oldMaxLength: 2000,
                oldNullable: true);

            migrationBuilder.AlterColumn<bool>(
                name: "SuppressIncident",
                table: "AlertRoutingRules",
                type: "boolean",
                nullable: false,
                oldClrType: typeof(bool),
                oldType: "boolean",
                oldDefaultValue: false);

            migrationBuilder.AlterColumn<int>(
                name: "Priority",
                table: "AlertRoutingRules",
                type: "integer",
                nullable: false,
                oldClrType: typeof(int),
                oldType: "integer",
                oldDefaultValue: 0);

            migrationBuilder.AlterColumn<bool>(
                name: "IsEnabled",
                table: "AlertRoutingRules",
                type: "boolean",
                nullable: false,
                oldClrType: typeof(bool),
                oldType: "boolean",
                oldDefaultValue: true);

            migrationBuilder.AlterColumn<string>(
                name: "RunbookUrl",
                table: "AlertIncidents",
                type: "character varying(500)",
                maxLength: 500,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(500)",
                oldMaxLength: 500,
                oldDefaultValue: "");

            // ClusterServers is a brand-new table — always safe to create
            migrationBuilder.CreateTable(
                name: "ClusterServers",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ClusterId = table.Column<Guid>(type: "uuid", nullable: false),
                    NodeName = table.Column<string>(type: "character varying(253)", maxLength: 253, nullable: true),
                    DisplayName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    IpAddress = table.Column<string>(type: "character varying(45)", maxLength: 45, nullable: true),
                    ManagementIpAddress = table.Column<string>(type: "character varying(45)", maxLength: 45, nullable: true),
                    Provider = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    OsDistribution = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    CpuCores = table.Column<int>(type: "integer", nullable: true),
                    RamGb = table.Column<int>(type: "integer", nullable: true),
                    DiskGb = table.Column<int>(type: "integer", nullable: true),
                    Location = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    SshUser = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    SshPort = table.Column<int>(type: "integer", nullable: false),
                    JumpHost = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    Notes = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
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

            // VaultSecretVersions may already exist — use IF NOT EXISTS throughout
            migrationBuilder.Sql(@"
CREATE TABLE IF NOT EXISTS ""VaultSecretVersions"" (
    ""Id"" uuid NOT NULL,
    ""SecretId"" uuid NOT NULL,
    ""VersionNumber"" integer NOT NULL,
    ""EncryptedValue"" bytea NOT NULL,
    ""Nonce"" bytea NOT NULL,
    ""CreatedBy"" character varying(254),
    ""CreatedAt"" timestamp with time zone NOT NULL,
    CONSTRAINT ""PK_VaultSecretVersions"" PRIMARY KEY (""Id""),
    CONSTRAINT ""FK_VaultSecretVersions_VaultSecrets_SecretId""
        FOREIGN KEY (""SecretId"") REFERENCES ""VaultSecrets"" (""Id"") ON DELETE CASCADE
);");

            // Unique index on OnCallSchedules — may already exist
            migrationBuilder.Sql(@"CREATE UNIQUE INDEX IF NOT EXISTS ""IX_OnCallSchedules_TenantId_Name"" ON ""OnCallSchedules"" (""TenantId"", ""Name"");");

            // Index on ClusterServers — new table, always safe
            migrationBuilder.CreateIndex(
                name: "IX_ClusterServers_ClusterId_DisplayName",
                table: "ClusterServers",
                columns: new[] { "ClusterId", "DisplayName" },
                unique: true);

            // Index on VaultSecretVersions — may already exist if table pre-existed
            migrationBuilder.Sql(@"CREATE INDEX IF NOT EXISTS ""IX_VaultSecretVersions_SecretId_VersionNumber"" ON ""VaultSecretVersions"" (""SecretId"", ""VersionNumber"");");
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
                type: "boolean",
                nullable: false,
                defaultValue: true,
                oldClrType: typeof(bool),
                oldType: "boolean");

            migrationBuilder.AlterColumn<string>(
                name: "GitUrl",
                table: "AppDeployments",
                type: "character varying(2000)",
                maxLength: 2000,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "text",
                oldNullable: true);

            migrationBuilder.AlterColumn<bool>(
                name: "SuppressIncident",
                table: "AlertRoutingRules",
                type: "boolean",
                nullable: false,
                defaultValue: false,
                oldClrType: typeof(bool),
                oldType: "boolean");

            migrationBuilder.AlterColumn<int>(
                name: "Priority",
                table: "AlertRoutingRules",
                type: "integer",
                nullable: false,
                defaultValue: 0,
                oldClrType: typeof(int),
                oldType: "integer");

            migrationBuilder.AlterColumn<bool>(
                name: "IsEnabled",
                table: "AlertRoutingRules",
                type: "boolean",
                nullable: false,
                defaultValue: true,
                oldClrType: typeof(bool),
                oldType: "boolean");

            migrationBuilder.AlterColumn<string>(
                name: "RunbookUrl",
                table: "AlertIncidents",
                type: "character varying(500)",
                maxLength: 500,
                nullable: false,
                defaultValue: "",
                oldClrType: typeof(string),
                oldType: "character varying(500)",
                oldMaxLength: 500);

            migrationBuilder.CreateIndex(
                name: "IX_OnCallSchedules_TenantId",
                table: "OnCallSchedules",
                column: "TenantId");
        }
    }
}
