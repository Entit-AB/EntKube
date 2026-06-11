using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EntKube.Web.Data.Migrations.Postgres
{
    /// <inheritdoc />
    public partial class AddKubeconfigToCluster : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ContextName",
                table: "KubernetesClusters",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Kubeconfig",
                table: "KubernetesClusters",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ContextName",
                table: "KubernetesClusters");

            migrationBuilder.DropColumn(
                name: "Kubeconfig",
                table: "KubernetesClusters");
        }
    }
}
