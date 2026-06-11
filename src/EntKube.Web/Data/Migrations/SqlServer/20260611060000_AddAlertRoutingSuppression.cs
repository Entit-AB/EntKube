using System;
using EntKube.Web.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EntKube.Web.Data.Migrations.SqlServer
{
    [DbContext(typeof(SqlServerApplicationDbContext))]
    [Migration("20260611060000_AddAlertRoutingSuppression")]
    public partial class AddAlertRoutingSuppression : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Drop existing FK, make ChannelId nullable, re-add FK
            migrationBuilder.DropForeignKey(
                name: "FK_AlertRoutingRules_NotificationChannels_ChannelId",
                table: "AlertRoutingRules");

            migrationBuilder.AlterColumn<Guid>(
                name: "ChannelId",
                table: "AlertRoutingRules",
                type: "uniqueidentifier",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "uniqueidentifier");

            migrationBuilder.AddForeignKey(
                name: "FK_AlertRoutingRules_NotificationChannels_ChannelId",
                table: "AlertRoutingRules",
                column: "ChannelId",
                principalTable: "NotificationChannels",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            // Add new columns
            migrationBuilder.AddColumn<bool>(
                name: "SuppressIncident",
                table: "AlertRoutingRules",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<Guid>(
                name: "MatchClusterId",
                table: "AlertRoutingRules",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_AlertRoutingRules_MatchClusterId",
                table: "AlertRoutingRules",
                column: "MatchClusterId");

            migrationBuilder.AddForeignKey(
                name: "FK_AlertRoutingRules_KubernetesClusters_MatchClusterId",
                table: "AlertRoutingRules",
                column: "MatchClusterId",
                principalTable: "KubernetesClusters",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_AlertRoutingRules_KubernetesClusters_MatchClusterId",
                table: "AlertRoutingRules");

            migrationBuilder.DropIndex(
                name: "IX_AlertRoutingRules_MatchClusterId",
                table: "AlertRoutingRules");

            migrationBuilder.DropColumn(name: "MatchClusterId", table: "AlertRoutingRules");
            migrationBuilder.DropColumn(name: "SuppressIncident", table: "AlertRoutingRules");

            migrationBuilder.DropForeignKey(
                name: "FK_AlertRoutingRules_NotificationChannels_ChannelId",
                table: "AlertRoutingRules");

            migrationBuilder.AlterColumn<Guid>(
                name: "ChannelId",
                table: "AlertRoutingRules",
                type: "uniqueidentifier",
                nullable: false,
                oldClrType: typeof(Guid),
                oldType: "uniqueidentifier",
                oldNullable: true);

            migrationBuilder.AddForeignKey(
                name: "FK_AlertRoutingRules_NotificationChannels_ChannelId",
                table: "AlertRoutingRules",
                column: "ChannelId",
                principalTable: "NotificationChannels",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }
    }
}
