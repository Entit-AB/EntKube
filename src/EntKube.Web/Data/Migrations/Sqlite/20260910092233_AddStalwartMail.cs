using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EntKube.Web.Data.Migrations.Sqlite
{
    /// <inheritdoc />
    public partial class AddStalwartMail : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "StalwartComponentConfigs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    TenantId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ClusterComponentId = table.Column<Guid>(type: "TEXT", nullable: true),
                    DisplayName = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    Hostname = table.Column<string>(type: "TEXT", maxLength: 253, nullable: false),
                    AdminHostname = table.Column<string>(type: "TEXT", maxLength: 253, nullable: true),
                    AdminUsername = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    StorageSize = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    StorageClass = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    AuthMode = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    OpenLdapConfigId = table.Column<Guid>(type: "TEXT", nullable: true),
                    LdapUrl = table.Column<string>(type: "TEXT", maxLength: 400, nullable: true),
                    LdapBaseDn = table.Column<string>(type: "TEXT", maxLength: 400, nullable: true),
                    LdapBindDn = table.Column<string>(type: "TEXT", maxLength: 400, nullable: true),
                    LdapLoginFilter = table.Column<string>(type: "TEXT", maxLength: 600, nullable: false),
                    LdapMailboxFilter = table.Column<string>(type: "TEXT", maxLength: 600, nullable: false),
                    LdapMemberOfFilter = table.Column<string>(type: "TEXT", maxLength: 600, nullable: false),
                    LdapUseTls = table.Column<bool>(type: "INTEGER", nullable: false),
                    LdapAllowInvalidCerts = table.Column<bool>(type: "INTEGER", nullable: false),
                    OidcIssuerUrl = table.Column<string>(type: "TEXT", maxLength: 400, nullable: true),
                    OidcClaimUsername = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    OidcUsernameDomain = table.Column<string>(type: "TEXT", maxLength: 253, nullable: true),
                    OidcClaimGroups = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    OidcRequireAudience = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    OidcRequireScopes = table.Column<string>(type: "TEXT", maxLength: 400, nullable: false),
                    OidcAppRegistrationSecretId = table.Column<Guid>(type: "TEXT", nullable: true),
                    TlsMode = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    ClusterIssuer = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    AcmeContact = table.Column<string>(type: "TEXT", maxLength: 320, nullable: true),
                    AcmeChallenge = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    WebClusterIssuer = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    ExposeMode = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    LoadBalancerIp = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    LoadBalancerAnnotations = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true),
                    SmtpEnabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    SubmissionEnabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    ImapEnabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    Pop3Enabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    ManageSieveEnabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    RspamdEnabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    RspamdHost = table.Column<string>(type: "TEXT", maxLength: 253, nullable: true),
                    RspamdPort = table.Column<int>(type: "INTEGER", nullable: false),
                    RspamdTempFailOnError = table.Column<bool>(type: "INTEGER", nullable: false),
                    HighAvailability = table.Column<bool>(type: "INTEGER", nullable: false),
                    Replicas = table.Column<int>(type: "INTEGER", nullable: false),
                    CnpgDatabaseId = table.Column<Guid>(type: "TEXT", nullable: true),
                    BlobStorageLinkId = table.Column<Guid>(type: "TEXT", nullable: true),
                    CoordinatorRedisHost = table.Column<string>(type: "TEXT", maxLength: 253, nullable: true),
                    CoordinatorRedisPort = table.Column<int>(type: "INTEGER", nullable: false),
                    LastAppliedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StalwartComponentConfigs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_StalwartComponentConfigs_ClusterComponents_ClusterComponentId",
                        column: x => x.ClusterComponentId,
                        principalTable: "ClusterComponents",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_StalwartComponentConfigs_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "StalwartMailDomains",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    ConfigId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 253, nullable: false),
                    IsPrimary = table.Column<bool>(type: "INTEGER", nullable: false),
                    Description = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    CatchAllLocalPart = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    AllowRelaying = table.Column<bool>(type: "INTEGER", nullable: false),
                    AutomaticDkim = table.Column<bool>(type: "INTEGER", nullable: false),
                    SubAddressing = table.Column<bool>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StalwartMailDomains", x => x.Id);
                    table.ForeignKey(
                        name: "FK_StalwartMailDomains_StalwartComponentConfigs_ConfigId",
                        column: x => x.ConfigId,
                        principalTable: "StalwartComponentConfigs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "StalwartMailAccounts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    ConfigId = table.Column<Guid>(type: "TEXT", nullable: false),
                    DomainId = table.Column<Guid>(type: "TEXT", nullable: false),
                    LocalPart = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    DisplayName = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    Description = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    Aliases = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StalwartMailAccounts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_StalwartMailAccounts_StalwartComponentConfigs_ConfigId",
                        column: x => x.ConfigId,
                        principalTable: "StalwartComponentConfigs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_StalwartMailAccounts_StalwartMailDomains_DomainId",
                        column: x => x.DomainId,
                        principalTable: "StalwartMailDomains",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_StalwartComponentConfigs_ClusterComponentId",
                table: "StalwartComponentConfigs",
                column: "ClusterComponentId");

            migrationBuilder.CreateIndex(
                name: "IX_StalwartComponentConfigs_TenantId",
                table: "StalwartComponentConfigs",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_StalwartMailAccounts_ConfigId",
                table: "StalwartMailAccounts",
                column: "ConfigId");

            migrationBuilder.CreateIndex(
                name: "IX_StalwartMailAccounts_DomainId_LocalPart",
                table: "StalwartMailAccounts",
                columns: new[] { "DomainId", "LocalPart" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_StalwartMailDomains_ConfigId_Name",
                table: "StalwartMailDomains",
                columns: new[] { "ConfigId", "Name" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "StalwartMailAccounts");

            migrationBuilder.DropTable(
                name: "StalwartMailDomains");

            migrationBuilder.DropTable(
                name: "StalwartComponentConfigs");
        }
    }
}
