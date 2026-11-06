using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EntKube.Web.Data.Migrations.Sqlite
{
    /// <inheritdoc />
    public partial class AddFeedback : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "FeedbackEnabled",
                table: "RumSites",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "FeedbackItems",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    TenantId = table.Column<Guid>(type: "TEXT", nullable: false),
                    RumSiteId = table.Column<Guid>(type: "TEXT", nullable: false),
                    SiteName = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    AppId = table.Column<Guid>(type: "TEXT", nullable: true),
                    Kind = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    Status = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    Rating = table.Column<int>(type: "INTEGER", nullable: true),
                    Message = table.Column<string>(type: "TEXT", maxLength: 4000, nullable: false),
                    ContactEmail = table.Column<string>(type: "TEXT", maxLength: 254, nullable: true),
                    PagePath = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: false),
                    Browser = table.Column<string>(type: "TEXT", maxLength: 40, nullable: true),
                    Os = table.Column<string>(type: "TEXT", maxLength: 40, nullable: true),
                    Device = table.Column<string>(type: "TEXT", maxLength: 40, nullable: true),
                    ViewportWidth = table.Column<int>(type: "INTEGER", nullable: true),
                    ViewportHeight = table.Column<int>(type: "INTEGER", nullable: true),
                    RumSessionId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    RecentErrors = table.Column<string>(type: "TEXT", maxLength: 1505, nullable: true),
                    TicketId = table.Column<Guid>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    TriagedBy = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FeedbackItems", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "FeedbackScreenshots",
                columns: table => new
                {
                    FeedbackItemId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ContentType = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    Bytes = table.Column<byte[]>(type: "BLOB", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FeedbackScreenshots", x => x.FeedbackItemId);
                    table.ForeignKey(
                        name: "FK_FeedbackScreenshots_FeedbackItems_FeedbackItemId",
                        column: x => x.FeedbackItemId,
                        principalTable: "FeedbackItems",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_FeedbackItems_CreatedAt",
                table: "FeedbackItems",
                column: "CreatedAt");

            migrationBuilder.CreateIndex(
                name: "IX_FeedbackItems_RumSiteId_CreatedAt",
                table: "FeedbackItems",
                columns: new[] { "RumSiteId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_FeedbackItems_TenantId_Status_CreatedAt",
                table: "FeedbackItems",
                columns: new[] { "TenantId", "Status", "CreatedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "FeedbackScreenshots");

            migrationBuilder.DropTable(
                name: "FeedbackItems");

            migrationBuilder.DropColumn(
                name: "FeedbackEnabled",
                table: "RumSites");
        }
    }
}
