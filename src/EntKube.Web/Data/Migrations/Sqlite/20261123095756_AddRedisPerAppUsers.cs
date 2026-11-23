using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EntKube.Web.Data.Migrations.Sqlite
{
    /// <inheritdoc />
    public partial class AddRedisPerAppUsers : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "PerAppUsersSince",
                table: "RedisClusters",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "UsersLeaseUntil",
                table: "RedisClusters",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "KeyPrefix",
                table: "CacheBindings",
                type: "TEXT",
                maxLength: 48,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "SyncPending",
                table: "CacheBindings",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "PerAppUsersSince",
                table: "RedisClusters");

            migrationBuilder.DropColumn(
                name: "UsersLeaseUntil",
                table: "RedisClusters");

            migrationBuilder.DropColumn(
                name: "KeyPrefix",
                table: "CacheBindings");

            migrationBuilder.DropColumn(
                name: "SyncPending",
                table: "CacheBindings");
        }
    }
}
