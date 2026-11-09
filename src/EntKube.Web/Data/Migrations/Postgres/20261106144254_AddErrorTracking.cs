using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EntKube.Web.Data.Migrations.Postgres
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
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    Source = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    ScopeId = table.Column<Guid>(type: "uuid", nullable: false),
                    ScopeName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Fingerprint = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Title = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    Namespace = table.Column<string>(type: "character varying(253)", maxLength: 253, nullable: true),
                    Workload = table.Column<string>(type: "character varying(253)", maxLength: 253, nullable: true),
                    SampleMessage = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    SampleTraceId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    FirstSeenAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    LastSeenAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Occurrences = table.Column<long>(type: "bigint", nullable: false),
                    CountIsPartial = table.Column<bool>(type: "boolean", nullable: false),
                    Status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    ResolvedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ResolvedBy = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    RegressedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    Regressions = table.Column<int>(type: "integer", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ErrorIssues", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ErrorScanCursors",
                columns: table => new
                {
                    ScopeId = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    ScannedThrough = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
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
