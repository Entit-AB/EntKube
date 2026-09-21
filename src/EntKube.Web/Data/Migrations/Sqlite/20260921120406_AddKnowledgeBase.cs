using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EntKube.Web.Data.Migrations.Sqlite
{
    /// <inheritdoc />
    public partial class AddKnowledgeBase : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AppKnowledgeProfiles",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    TenantId = table.Column<Guid>(type: "TEXT", nullable: false),
                    AppId = table.Column<Guid>(type: "TEXT", nullable: false),
                    DataClassification = table.Column<int>(type: "INTEGER", nullable: false),
                    HandlesPatientData = table.Column<bool>(type: "INTEGER", nullable: false),
                    RegulatoryScope = table.Column<string>(type: "TEXT", nullable: true),
                    BackupResponsibility = table.Column<int>(type: "INTEGER", nullable: false),
                    BusinessPurpose = table.Column<string>(type: "TEXT", nullable: true),
                    ImpactWhenDown = table.Column<string>(type: "TEXT", nullable: true),
                    Notes = table.Column<string>(type: "TEXT", nullable: true),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedBy = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AppKnowledgeProfiles", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AppKnowledgeProfiles_Apps_AppId",
                        column: x => x.AppId,
                        principalTable: "Apps",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_AppKnowledgeProfiles_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "AppServiceDependencies",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    TenantId = table.Column<Guid>(type: "TEXT", nullable: false),
                    AppId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    Kind = table.Column<int>(type: "INTEGER", nullable: false),
                    Supplier = table.Column<string>(type: "TEXT", nullable: true),
                    SupportHours = table.Column<string>(type: "TEXT", nullable: true),
                    OfficeHoursOnly = table.Column<bool>(type: "INTEGER", nullable: false),
                    ContactRoute = table.Column<string>(type: "TEXT", nullable: true),
                    CriticalPath = table.Column<bool>(type: "INTEGER", nullable: false),
                    Notes = table.Column<string>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AppServiceDependencies", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AppServiceDependencies_Apps_AppId",
                        column: x => x.AppId,
                        principalTable: "Apps",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_AppServiceDependencies_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "EndOfLifeNotices",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    TenantId = table.Column<Guid>(type: "TEXT", nullable: false),
                    AppId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Component = table.Column<string>(type: "TEXT", nullable: false),
                    CurrentVersion = table.Column<string>(type: "TEXT", nullable: true),
                    EndOfLifeOn = table.Column<DateTime>(type: "TEXT", nullable: true),
                    ProposedUpgrade = table.Column<string>(type: "TEXT", nullable: true),
                    NoticeGivenAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    NoticeGivenBy = table.Column<string>(type: "TEXT", nullable: true),
                    UpgradeOrderedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    UpgradedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    Notes = table.Column<string>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EndOfLifeNotices", x => x.Id);
                    table.ForeignKey(
                        name: "FK_EndOfLifeNotices_Apps_AppId",
                        column: x => x.AppId,
                        principalTable: "Apps",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_EndOfLifeNotices_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "KnowledgeSections",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    TenantId = table.Column<Guid>(type: "TEXT", nullable: false),
                    AppId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Kind = table.Column<int>(type: "INTEGER", nullable: false),
                    Title = table.Column<string>(type: "TEXT", nullable: false),
                    Body = table.Column<string>(type: "TEXT", nullable: false),
                    ReviewedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    ReviewedBy = table.Column<string>(type: "TEXT", nullable: true),
                    ReviewIntervalDays = table.Column<int>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedBy = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_KnowledgeSections", x => x.Id);
                    table.ForeignKey(
                        name: "FK_KnowledgeSections_Apps_AppId",
                        column: x => x.AppId,
                        principalTable: "Apps",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_KnowledgeSections_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "KnowledgeRevisions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    SectionId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Body = table.Column<string>(type: "TEXT", nullable: false),
                    SavedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    SavedBy = table.Column<string>(type: "TEXT", nullable: true),
                    Summary = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_KnowledgeRevisions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_KnowledgeRevisions_KnowledgeSections_SectionId",
                        column: x => x.SectionId,
                        principalTable: "KnowledgeSections",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AppKnowledgeProfiles_AppId",
                table: "AppKnowledgeProfiles",
                column: "AppId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AppKnowledgeProfiles_TenantId",
                table: "AppKnowledgeProfiles",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_AppServiceDependencies_AppId",
                table: "AppServiceDependencies",
                column: "AppId");

            migrationBuilder.CreateIndex(
                name: "IX_AppServiceDependencies_TenantId",
                table: "AppServiceDependencies",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_EndOfLifeNotices_AppId_UpgradedAt",
                table: "EndOfLifeNotices",
                columns: new[] { "AppId", "UpgradedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_EndOfLifeNotices_TenantId",
                table: "EndOfLifeNotices",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_KnowledgeRevisions_SectionId_SavedAt",
                table: "KnowledgeRevisions",
                columns: new[] { "SectionId", "SavedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_KnowledgeSections_AppId_Kind",
                table: "KnowledgeSections",
                columns: new[] { "AppId", "Kind" });

            migrationBuilder.CreateIndex(
                name: "IX_KnowledgeSections_TenantId",
                table: "KnowledgeSections",
                column: "TenantId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AppKnowledgeProfiles");

            migrationBuilder.DropTable(
                name: "AppServiceDependencies");

            migrationBuilder.DropTable(
                name: "EndOfLifeNotices");

            migrationBuilder.DropTable(
                name: "KnowledgeRevisions");

            migrationBuilder.DropTable(
                name: "KnowledgeSections");
        }
    }
}
