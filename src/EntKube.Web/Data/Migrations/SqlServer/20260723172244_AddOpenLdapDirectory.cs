using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EntKube.Web.Data.Migrations.SqlServer
{
    /// <inheritdoc />
    public partial class AddOpenLdapDirectory : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "OpenLdapComponentConfigs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ClusterComponentId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    DisplayName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    BaseDn = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    Organization = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    AdminUsername = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    LdapPort = table.Column<int>(type: "int", nullable: false),
                    LdapsPort = table.Column<int>(type: "int", nullable: false),
                    TlsMode = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    ClusterIssuer = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    StartTlsEnabled = table.Column<bool>(type: "bit", nullable: false),
                    ReplicationEnabled = table.Column<bool>(type: "bit", nullable: false),
                    ReplicaCount = table.Column<int>(type: "int", nullable: false),
                    MemberOfEnabled = table.Column<bool>(type: "bit", nullable: false),
                    RefIntEnabled = table.Column<bool>(type: "bit", nullable: false),
                    PasswordPolicyEnabled = table.Column<bool>(type: "bit", nullable: false),
                    PpolicyMinLength = table.Column<int>(type: "int", nullable: false),
                    PpolicyMaxFailure = table.Column<int>(type: "int", nullable: false),
                    PpolicyLockoutDurationSeconds = table.Column<int>(type: "int", nullable: false),
                    PpolicyMaxAgeDays = table.Column<int>(type: "int", nullable: false),
                    PpolicyInHistory = table.Column<int>(type: "int", nullable: false),
                    StorageSize = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    StorageClass = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OpenLdapComponentConfigs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_OpenLdapComponentConfigs_ClusterComponents_ClusterComponentId",
                        column: x => x.ClusterComponentId,
                        principalTable: "ClusterComponents",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_OpenLdapComponentConfigs_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "OpenLdapOrganizationalUnits",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ConfigId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    Description = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OpenLdapOrganizationalUnits", x => x.Id);
                    table.ForeignKey(
                        name: "FK_OpenLdapOrganizationalUnits_OpenLdapComponentConfigs_ConfigId",
                        column: x => x.ConfigId,
                        principalTable: "OpenLdapComponentConfigs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "OpenLdapGroups",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ConfigId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OrganizationalUnitId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Cn = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Description = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    GroupType = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    GidNumber = table.Column<int>(type: "int", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OpenLdapGroups", x => x.Id);
                    table.ForeignKey(
                        name: "FK_OpenLdapGroups_OpenLdapComponentConfigs_ConfigId",
                        column: x => x.ConfigId,
                        principalTable: "OpenLdapComponentConfigs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_OpenLdapGroups_OpenLdapOrganizationalUnits_OrganizationalUnitId",
                        column: x => x.OrganizationalUnitId,
                        principalTable: "OpenLdapOrganizationalUnits",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "OpenLdapUsers",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ConfigId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OrganizationalUnitId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Uid = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    Cn = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Sn = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    GivenName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    Email = table.Column<string>(type: "nvarchar(320)", maxLength: 320, nullable: true),
                    DisplayName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    UidNumber = table.Column<int>(type: "int", nullable: true),
                    GidNumber = table.Column<int>(type: "int", nullable: true),
                    HomeDirectory = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: true),
                    LoginShell = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    IsServiceAccount = table.Column<bool>(type: "bit", nullable: false),
                    PasswordSsha = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    Enabled = table.Column<bool>(type: "bit", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OpenLdapUsers", x => x.Id);
                    table.ForeignKey(
                        name: "FK_OpenLdapUsers_OpenLdapComponentConfigs_ConfigId",
                        column: x => x.ConfigId,
                        principalTable: "OpenLdapComponentConfigs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_OpenLdapUsers_OpenLdapOrganizationalUnits_OrganizationalUnitId",
                        column: x => x.OrganizationalUnitId,
                        principalTable: "OpenLdapOrganizationalUnits",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "OpenLdapGroupMembers",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    GroupId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OpenLdapGroupMembers", x => x.Id);
                    table.ForeignKey(
                        name: "FK_OpenLdapGroupMembers_OpenLdapGroups_GroupId",
                        column: x => x.GroupId,
                        principalTable: "OpenLdapGroups",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_OpenLdapGroupMembers_OpenLdapUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "OpenLdapUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_OpenLdapComponentConfigs_ClusterComponentId",
                table: "OpenLdapComponentConfigs",
                column: "ClusterComponentId");

            migrationBuilder.CreateIndex(
                name: "IX_OpenLdapComponentConfigs_TenantId",
                table: "OpenLdapComponentConfigs",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_OpenLdapGroupMembers_GroupId_UserId",
                table: "OpenLdapGroupMembers",
                columns: new[] { "GroupId", "UserId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_OpenLdapGroupMembers_UserId",
                table: "OpenLdapGroupMembers",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_OpenLdapGroups_ConfigId_Cn",
                table: "OpenLdapGroups",
                columns: new[] { "ConfigId", "Cn" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_OpenLdapGroups_OrganizationalUnitId",
                table: "OpenLdapGroups",
                column: "OrganizationalUnitId");

            migrationBuilder.CreateIndex(
                name: "IX_OpenLdapOrganizationalUnits_ConfigId_Name",
                table: "OpenLdapOrganizationalUnits",
                columns: new[] { "ConfigId", "Name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_OpenLdapUsers_ConfigId_Uid",
                table: "OpenLdapUsers",
                columns: new[] { "ConfigId", "Uid" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_OpenLdapUsers_OrganizationalUnitId",
                table: "OpenLdapUsers",
                column: "OrganizationalUnitId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "OpenLdapGroupMembers");

            migrationBuilder.DropTable(
                name: "OpenLdapGroups");

            migrationBuilder.DropTable(
                name: "OpenLdapUsers");

            migrationBuilder.DropTable(
                name: "OpenLdapOrganizationalUnits");

            migrationBuilder.DropTable(
                name: "OpenLdapComponentConfigs");
        }
    }
}
