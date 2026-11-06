using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EntKube.Web.Data.Migrations.Postgres
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
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "FeedbackItems",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    RumSiteId = table.Column<Guid>(type: "uuid", nullable: false),
                    SiteName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    AppId = table.Column<Guid>(type: "uuid", nullable: true),
                    Kind = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Rating = table.Column<int>(type: "integer", nullable: true),
                    Message = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: false),
                    ContactEmail = table.Column<string>(type: "character varying(254)", maxLength: 254, nullable: true),
                    PagePath = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: false),
                    Browser = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true),
                    Os = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true),
                    Device = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true),
                    ViewportWidth = table.Column<int>(type: "integer", nullable: true),
                    ViewportHeight = table.Column<int>(type: "integer", nullable: true),
                    RumSessionId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    RecentErrors = table.Column<string>(type: "character varying(1505)", maxLength: 1505, nullable: true),
                    TicketId = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    TriagedBy = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FeedbackItems", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "FeedbackScreenshots",
                columns: table => new
                {
                    FeedbackItemId = table.Column<Guid>(type: "uuid", nullable: false),
                    ContentType = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Bytes = table.Column<byte[]>(type: "bytea", nullable: false)
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
