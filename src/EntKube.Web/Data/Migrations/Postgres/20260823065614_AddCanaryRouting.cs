using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EntKube.Web.Data.Migrations.Postgres
{
    /// <inheritdoc />
    public partial class AddCanaryRouting : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "CanaryServiceName",
                table: "AppDeploymentRoutes",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "CanaryServicePort",
                table: "AppDeploymentRoutes",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "CanaryWeight",
                table: "AppDeploymentRoutes",
                type: "integer",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CanaryServiceName",
                table: "AppDeploymentRoutes");

            migrationBuilder.DropColumn(
                name: "CanaryServicePort",
                table: "AppDeploymentRoutes");

            migrationBuilder.DropColumn(
                name: "CanaryWeight",
                table: "AppDeploymentRoutes");
        }
    }
}
