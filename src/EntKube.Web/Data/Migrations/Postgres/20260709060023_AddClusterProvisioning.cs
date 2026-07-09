using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EntKube.Web.Data.Migrations.Postgres
{
    /// <inheritdoc />
    public partial class AddClusterProvisioning : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ProvisioningStateJson",
                table: "KubernetesClusters",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ProvisioningStatus",
                table: "KubernetesClusters",
                type: "integer",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ProvisioningStateJson",
                table: "KubernetesClusters");

            migrationBuilder.DropColumn(
                name: "ProvisioningStatus",
                table: "KubernetesClusters");
        }
    }
}
