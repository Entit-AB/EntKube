using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EntKube.Web.Data.Migrations.SqlServer
{
    /// <inheritdoc />
    public partial class AddErrorTracking : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ErrorIssues",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Source = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    ScopeId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ScopeName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Fingerprint = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    Title = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: false),
                    Namespace = table.Column<string>(type: "nvarchar(253)", maxLength: 253, nullable: true),
                    Workload = table.Column<string>(type: "nvarchar(253)", maxLength: 253, nullable: true),
                    SampleMessage = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: false),
                    SampleTraceId = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    FirstSeenAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    LastSeenAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    Occurrences = table.Column<long>(type: "bigint", nullable: false),
                    CountIsPartial = table.Column<bool>(type: "bit", nullable: false),
                    Status = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    ResolvedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ResolvedBy = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    RegressedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    Regressions = table.Column<int>(type: "int", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ErrorIssues", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ErrorScanCursors",
                columns: table => new
                {
                    ScopeId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ScannedThrough = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ErrorScanCursors", x => x.ScopeId);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ErrorIssues_LastSeenAt",
                table: "ErrorIssues",
                column: "LastSeenAt");

            migrationBuilder.CreateIndex(
                name: "IX_ErrorIssues_TenantId_ScopeId_Fingerprint",
                table: "ErrorIssues",
                columns: new[] { "TenantId", "ScopeId", "Fingerprint" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ErrorIssues_TenantId_Status_LastSeenAt",
                table: "ErrorIssues",
                columns: new[] { "TenantId", "Status", "LastSeenAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ErrorIssues");

            migrationBuilder.DropTable(
                name: "ErrorScanCursors");
        }
    }
}
