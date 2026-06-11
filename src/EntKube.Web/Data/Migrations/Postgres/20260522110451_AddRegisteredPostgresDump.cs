using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EntKube.Web.Data.Migrations.Postgres
{
    /// <inheritdoc />
    public partial class AddRegisteredPostgresDump : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "RegisteredPostgresDumps",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    RegisteredPostgresDatabaseId = table.Column<Guid>(type: "uuid", nullable: false),
                    StorageLinkId = table.Column<Guid>(type: "uuid", nullable: false),
                    S3Key = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: false),
                    SizeBytes = table.Column<long>(type: "bigint", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RegisteredPostgresDumps", x => x.Id);
                    table.ForeignKey(
                        name: "FK_RegisteredPostgresDumps_RegisteredPostgresDatabases_Registe~",
                        column: x => x.RegisteredPostgresDatabaseId,
                        principalTable: "RegisteredPostgresDatabases",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_RegisteredPostgresDumps_StorageLinks_StorageLinkId",
                        column: x => x.StorageLinkId,
                        principalTable: "StorageLinks",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_RegisteredPostgresDumps_RegisteredPostgresDatabaseId",
                table: "RegisteredPostgresDumps",
                column: "RegisteredPostgresDatabaseId");

            migrationBuilder.CreateIndex(
                name: "IX_RegisteredPostgresDumps_StorageLinkId",
                table: "RegisteredPostgresDumps",
                column: "StorageLinkId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "RegisteredPostgresDumps");
        }
    }
}
