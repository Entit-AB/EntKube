using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EntKube.Web.Data.Migrations.Postgres
{
    /// <inheritdoc />
    public partial class AddRumSites : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "RumSites",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    ClusterId = table.Column<Guid>(type: "uuid", nullable: true),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    PublicKey = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    AllowedOrigins = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: false),
                    SampleRate = table.Column<double>(type: "double precision", nullable: false),
                    IsEnabled = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RumSites", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_RumSites_PublicKey",
                table: "RumSites",
                column: "PublicKey",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_RumSites_TenantId",
                table: "RumSites",
                column: "TenantId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "RumSites");
        }
    }
}
