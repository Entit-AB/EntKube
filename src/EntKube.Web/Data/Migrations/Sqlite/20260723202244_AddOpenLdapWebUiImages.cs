using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EntKube.Web.Data.Migrations.Sqlite
{
    /// <inheritdoc />
    public partial class AddOpenLdapWebUiImages : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "LtbPasswdImage",
                table: "OpenLdapComponentConfigs",
                type: "TEXT",
                maxLength: 300,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PhpLdapAdminImage",
                table: "OpenLdapComponentConfigs",
                type: "TEXT",
                maxLength: 300,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "LtbPasswdImage",
                table: "OpenLdapComponentConfigs");

            migrationBuilder.DropColumn(
                name: "PhpLdapAdminImage",
                table: "OpenLdapComponentConfigs");
        }
    }
}
