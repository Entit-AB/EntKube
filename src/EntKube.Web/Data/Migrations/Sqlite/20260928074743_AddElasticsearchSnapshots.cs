using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EntKube.Web.Data.Migrations.Sqlite
{
    /// <inheritdoc />
    public partial class AddElasticsearchSnapshots : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "SnapshotBasePath",
                table: "ElasticsearchClusters",
                type: "TEXT",
                maxLength: 255,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "SnapshotExpireAfterDays",
                table: "ElasticsearchClusters",
                type: "INTEGER",
                nullable: false,
                defaultValue: 30);

            migrationBuilder.AddColumn<DateTime>(
                name: "SnapshotLastCheckedAt",
                table: "ElasticsearchClusters",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SnapshotLastFailure",
                table: "ElasticsearchClusters",
                type: "TEXT",
                maxLength: 2000,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "SnapshotLastSuccessAt",
                table: "ElasticsearchClusters",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SnapshotLastSuccessName",
                table: "ElasticsearchClusters",
                type: "TEXT",
                maxLength: 255,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "SnapshotMaxCount",
                table: "ElasticsearchClusters",
                type: "INTEGER",
                nullable: false,
                defaultValue: 50);

            migrationBuilder.AddColumn<int>(
                name: "SnapshotMinCount",
                table: "ElasticsearchClusters",
                type: "INTEGER",
                nullable: false,
                defaultValue: 5);

            migrationBuilder.AddColumn<string>(
                name: "SnapshotScheduleCron",
                table: "ElasticsearchClusters",
                type: "TEXT",
                maxLength: 64,
                nullable: false,
                defaultValue: "0 30 1 * * ?");

            migrationBuilder.AddColumn<Guid>(
                name: "SnapshotStorageLinkId",
                table: "ElasticsearchClusters",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "SnapshotsEnabled",
                table: "ElasticsearchClusters",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "SnapshotBasePath",
                table: "ElasticsearchClusters");

            migrationBuilder.DropColumn(
                name: "SnapshotExpireAfterDays",
                table: "ElasticsearchClusters");

            migrationBuilder.DropColumn(
                name: "SnapshotLastCheckedAt",
                table: "ElasticsearchClusters");

            migrationBuilder.DropColumn(
                name: "SnapshotLastFailure",
                table: "ElasticsearchClusters");

            migrationBuilder.DropColumn(
                name: "SnapshotLastSuccessAt",
                table: "ElasticsearchClusters");

            migrationBuilder.DropColumn(
                name: "SnapshotLastSuccessName",
                table: "ElasticsearchClusters");

            migrationBuilder.DropColumn(
                name: "SnapshotMaxCount",
                table: "ElasticsearchClusters");

            migrationBuilder.DropColumn(
                name: "SnapshotMinCount",
                table: "ElasticsearchClusters");

            migrationBuilder.DropColumn(
                name: "SnapshotScheduleCron",
                table: "ElasticsearchClusters");

            migrationBuilder.DropColumn(
                name: "SnapshotStorageLinkId",
                table: "ElasticsearchClusters");

            migrationBuilder.DropColumn(
                name: "SnapshotsEnabled",
                table: "ElasticsearchClusters");
        }
    }
}
