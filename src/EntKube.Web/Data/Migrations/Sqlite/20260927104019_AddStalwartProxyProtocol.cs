using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EntKube.Web.Data.Migrations.Sqlite
{
    /// <inheritdoc />
    public partial class AddStalwartProxyProtocol : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "ProxyProtocol",
                table: "StalwartComponentConfigs",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "ProxyTrustedNetworks",
                table: "StalwartComponentConfigs",
                type: "TEXT",
                maxLength: 2000,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ProxyProtocol",
                table: "StalwartComponentConfigs");

            migrationBuilder.DropColumn(
                name: "ProxyTrustedNetworks",
                table: "StalwartComponentConfigs");
        }
    }
}
