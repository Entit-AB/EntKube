using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EntKube.Web.Data.Migrations.Postgres
{
    /// <inheritdoc />
    public partial class AddAppDeploymentIsManaged : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "IsManaged",
                table: "AppDeployments",
                type: "boolean",
                nullable: false,
                defaultValue: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "IsManaged",
                table: "AppDeployments");
        }
    }
}
