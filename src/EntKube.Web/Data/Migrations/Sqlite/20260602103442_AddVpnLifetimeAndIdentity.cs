using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EntKube.Web.Data.Migrations.Sqlite
{
    /// <inheritdoc />
    public partial class AddVpnLifetimeAndIdentity : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "ChildLifetime",
                table: "VpnTunnels",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "IkeLifetime",
                table: "VpnTunnels",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "LocalId",
                table: "VpnRemoteEndpoints",
                type: "TEXT",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RemoteId",
                table: "VpnRemoteEndpoints",
                type: "TEXT",
                maxLength: 500,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ChildLifetime",
                table: "VpnTunnels");

            migrationBuilder.DropColumn(
                name: "IkeLifetime",
                table: "VpnTunnels");

            migrationBuilder.DropColumn(
                name: "LocalId",
                table: "VpnRemoteEndpoints");

            migrationBuilder.DropColumn(
                name: "RemoteId",
                table: "VpnRemoteEndpoints");
        }
    }
}
