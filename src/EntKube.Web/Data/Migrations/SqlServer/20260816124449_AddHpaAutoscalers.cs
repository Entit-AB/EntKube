using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EntKube.Web.Data.Migrations.SqlServer
{
    /// <inheritdoc />
    public partial class AddHpaAutoscalers : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "BehaviorYaml",
                table: "KedaScalers",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "TargetCpuUtilization",
                table: "KedaScalers",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "TargetMemoryUtilization",
                table: "KedaScalers",
                type: "int",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "BehaviorYaml",
                table: "KedaScalers");

            migrationBuilder.DropColumn(
                name: "TargetCpuUtilization",
                table: "KedaScalers");

            migrationBuilder.DropColumn(
                name: "TargetMemoryUtilization",
                table: "KedaScalers");
        }
    }
}
