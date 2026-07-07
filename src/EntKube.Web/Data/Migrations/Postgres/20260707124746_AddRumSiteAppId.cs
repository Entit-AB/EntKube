using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EntKube.Web.Data.Migrations.Postgres
{
    /// <inheritdoc />
    public partial class AddRumSiteAppId : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "AppId",
                table: "RumSites",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_RumSites_AppId",
                table: "RumSites",
                column: "AppId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_RumSites_AppId",
                table: "RumSites");

            migrationBuilder.DropColumn(
                name: "AppId",
                table: "RumSites");
        }
    }
}
