using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EntKube.Web.Data.Migrations.SqlServer
{
    /// <inheritdoc />
    public partial class AddOpenLdapWebUiExpose : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "LtbPasswdExposeMode",
                table: "OpenLdapComponentConfigs",
                type: "nvarchar(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "LtbPasswdIngressClass",
                table: "OpenLdapComponentConfigs",
                type: "nvarchar(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PhpLdapAdminExposeMode",
                table: "OpenLdapComponentConfigs",
                type: "nvarchar(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "PhpLdapAdminIngressClass",
                table: "OpenLdapComponentConfigs",
                type: "nvarchar(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "WebUiClusterIssuer",
                table: "OpenLdapComponentConfigs",
                type: "nvarchar(200)",
                maxLength: 200,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "LtbPasswdExposeMode",
                table: "OpenLdapComponentConfigs");

            migrationBuilder.DropColumn(
                name: "LtbPasswdIngressClass",
                table: "OpenLdapComponentConfigs");

            migrationBuilder.DropColumn(
                name: "PhpLdapAdminExposeMode",
                table: "OpenLdapComponentConfigs");

            migrationBuilder.DropColumn(
                name: "PhpLdapAdminIngressClass",
                table: "OpenLdapComponentConfigs");

            migrationBuilder.DropColumn(
                name: "WebUiClusterIssuer",
                table: "OpenLdapComponentConfigs");
        }
    }
}
