using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EntKube.Web.Data.Migrations.SqlServer
{
    /// <inheritdoc />
    public partial class AddOpenLdapWebUis : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "LtbPasswdEnabled",
                table: "OpenLdapComponentConfigs",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "LtbPasswdHostname",
                table: "OpenLdapComponentConfigs",
                type: "nvarchar(253)",
                maxLength: 253,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "PhpLdapAdminEnabled",
                table: "OpenLdapComponentConfigs",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "PhpLdapAdminHostname",
                table: "OpenLdapComponentConfigs",
                type: "nvarchar(253)",
                maxLength: 253,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "LtbPasswdEnabled",
                table: "OpenLdapComponentConfigs");

            migrationBuilder.DropColumn(
                name: "LtbPasswdHostname",
                table: "OpenLdapComponentConfigs");

            migrationBuilder.DropColumn(
                name: "PhpLdapAdminEnabled",
                table: "OpenLdapComponentConfigs");

            migrationBuilder.DropColumn(
                name: "PhpLdapAdminHostname",
                table: "OpenLdapComponentConfigs");
        }
    }
}
