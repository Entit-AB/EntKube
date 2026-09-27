using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EntKube.Web.Data.Migrations.SqlServer
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
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "ProxyTrustedNetworks",
                table: "StalwartComponentConfigs",
                type: "nvarchar(2000)",
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
