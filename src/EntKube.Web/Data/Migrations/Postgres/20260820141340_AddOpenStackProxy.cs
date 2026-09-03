using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EntKube.Web.Data.Migrations.Postgres
{
    /// <inheritdoc />
    public partial class AddOpenStackProxy : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ProxyUrl",
                table: "OpenStackConnections",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ProxyUsername",
                table: "OpenStackConnections",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ProxyUrl",
                table: "OpenStackConnections");

            migrationBuilder.DropColumn(
                name: "ProxyUsername",
                table: "OpenStackConnections");
        }
    }
}
