using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EntKube.Web.Data.Migrations.Postgres
{
    /// <inheritdoc />
    public partial class AddSupportMailboxStalwartLink : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "OAuthClientId",
                table: "SupportMailboxes",
                type: "character varying(256)",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "OAuthScopes",
                table: "SupportMailboxes",
                type: "character varying(256)",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "OAuthTokenEndpoint",
                table: "SupportMailboxes",
                type: "character varying(512)",
                maxLength: 512,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "StalwartAccountId",
                table: "SupportMailboxes",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "StalwartComponentId",
                table: "SupportMailboxes",
                type: "uuid",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "OAuthClientId",
                table: "SupportMailboxes");

            migrationBuilder.DropColumn(
                name: "OAuthScopes",
                table: "SupportMailboxes");

            migrationBuilder.DropColumn(
                name: "OAuthTokenEndpoint",
                table: "SupportMailboxes");

            migrationBuilder.DropColumn(
                name: "StalwartAccountId",
                table: "SupportMailboxes");

            migrationBuilder.DropColumn(
                name: "StalwartComponentId",
                table: "SupportMailboxes");
        }
    }
}
