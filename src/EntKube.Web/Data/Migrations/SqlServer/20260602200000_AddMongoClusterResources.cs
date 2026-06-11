using EntKube.Web.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EntKube.Web.Data.Migrations.SqlServer
{
    [DbContext(typeof(SqlServerApplicationDbContext))]
    [Migration("20260602200000_AddMongoClusterResources")]
    public partial class AddMongoClusterResources : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "CpuRequest",
                table: "MongoClusters",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CpuLimit",
                table: "MongoClusters",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "MemoryRequest",
                table: "MongoClusters",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "MemoryLimit",
                table: "MongoClusters",
                type: "nvarchar(max)",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(name: "CpuRequest",    table: "MongoClusters");
            migrationBuilder.DropColumn(name: "CpuLimit",      table: "MongoClusters");
            migrationBuilder.DropColumn(name: "MemoryRequest", table: "MongoClusters");
            migrationBuilder.DropColumn(name: "MemoryLimit",   table: "MongoClusters");
        }
    }
}
