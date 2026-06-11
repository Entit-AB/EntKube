using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EntKube.Web.Data.Migrations.Sqlite
{
    /// <inheritdoc />
    public partial class GovernancePerEnvironment : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_AppRbacPolicies_AppId",
                table: "AppRbacPolicies");

            migrationBuilder.DropIndex(
                name: "IX_AppQuotas_AppId",
                table: "AppQuotas");

            migrationBuilder.DropIndex(
                name: "IX_AppNetworkPolicies_AppId_Name",
                table: "AppNetworkPolicies");

            migrationBuilder.AddColumn<Guid>(
                name: "EnvironmentId",
                table: "AppRbacPolicies",
                type: "TEXT",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<Guid>(
                name: "EnvironmentId",
                table: "AppQuotas",
                type: "TEXT",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<Guid>(
                name: "EnvironmentId",
                table: "AppNetworkPolicies",
                type: "TEXT",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.CreateIndex(
                name: "IX_AppRbacPolicies_AppId_EnvironmentId",
                table: "AppRbacPolicies",
                columns: new[] { "AppId", "EnvironmentId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AppRbacPolicies_EnvironmentId",
                table: "AppRbacPolicies",
                column: "EnvironmentId");

            migrationBuilder.CreateIndex(
                name: "IX_AppQuotas_AppId_EnvironmentId",
                table: "AppQuotas",
                columns: new[] { "AppId", "EnvironmentId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AppQuotas_EnvironmentId",
                table: "AppQuotas",
                column: "EnvironmentId");

            migrationBuilder.CreateIndex(
                name: "IX_AppNetworkPolicies_AppId_EnvironmentId_Name",
                table: "AppNetworkPolicies",
                columns: new[] { "AppId", "EnvironmentId", "Name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AppNetworkPolicies_EnvironmentId",
                table: "AppNetworkPolicies",
                column: "EnvironmentId");

            migrationBuilder.Sql("DELETE FROM AppQuotas WHERE EnvironmentId = '00000000-0000-0000-0000-000000000000'");
            migrationBuilder.Sql("DELETE FROM AppNetworkPolicies WHERE EnvironmentId = '00000000-0000-0000-0000-000000000000'");
            migrationBuilder.Sql("DELETE FROM AppRbacPolicies WHERE EnvironmentId = '00000000-0000-0000-0000-000000000000'");

            migrationBuilder.AddForeignKey(
                name: "FK_AppNetworkPolicies_Environments_EnvironmentId",
                table: "AppNetworkPolicies",
                column: "EnvironmentId",
                principalTable: "Environments",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_AppQuotas_Environments_EnvironmentId",
                table: "AppQuotas",
                column: "EnvironmentId",
                principalTable: "Environments",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_AppRbacPolicies_Environments_EnvironmentId",
                table: "AppRbacPolicies",
                column: "EnvironmentId",
                principalTable: "Environments",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_AppNetworkPolicies_Environments_EnvironmentId",
                table: "AppNetworkPolicies");

            migrationBuilder.DropForeignKey(
                name: "FK_AppQuotas_Environments_EnvironmentId",
                table: "AppQuotas");

            migrationBuilder.DropForeignKey(
                name: "FK_AppRbacPolicies_Environments_EnvironmentId",
                table: "AppRbacPolicies");

            migrationBuilder.DropIndex(
                name: "IX_AppRbacPolicies_AppId_EnvironmentId",
                table: "AppRbacPolicies");

            migrationBuilder.DropIndex(
                name: "IX_AppRbacPolicies_EnvironmentId",
                table: "AppRbacPolicies");

            migrationBuilder.DropIndex(
                name: "IX_AppQuotas_AppId_EnvironmentId",
                table: "AppQuotas");

            migrationBuilder.DropIndex(
                name: "IX_AppQuotas_EnvironmentId",
                table: "AppQuotas");

            migrationBuilder.DropIndex(
                name: "IX_AppNetworkPolicies_AppId_EnvironmentId_Name",
                table: "AppNetworkPolicies");

            migrationBuilder.DropIndex(
                name: "IX_AppNetworkPolicies_EnvironmentId",
                table: "AppNetworkPolicies");

            migrationBuilder.DropColumn(
                name: "EnvironmentId",
                table: "AppRbacPolicies");

            migrationBuilder.DropColumn(
                name: "EnvironmentId",
                table: "AppQuotas");

            migrationBuilder.DropColumn(
                name: "EnvironmentId",
                table: "AppNetworkPolicies");

            migrationBuilder.CreateIndex(
                name: "IX_AppRbacPolicies_AppId",
                table: "AppRbacPolicies",
                column: "AppId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AppQuotas_AppId",
                table: "AppQuotas",
                column: "AppId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AppNetworkPolicies_AppId_Name",
                table: "AppNetworkPolicies",
                columns: new[] { "AppId", "Name" },
                unique: true);
        }
    }
}
