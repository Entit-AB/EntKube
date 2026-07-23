using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EntKube.Web.Data.Migrations.Sqlite
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
                type: "TEXT",
                maxLength: 20,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "LtbPasswdIngressClass",
                table: "OpenLdapComponentConfigs",
                type: "TEXT",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PhpLdapAdminExposeMode",
                table: "OpenLdapComponentConfigs",
                type: "TEXT",
                maxLength: 20,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "PhpLdapAdminIngressClass",
                table: "OpenLdapComponentConfigs",
                type: "TEXT",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "WebUiClusterIssuer",
                table: "OpenLdapComponentConfigs",
                type: "TEXT",
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
