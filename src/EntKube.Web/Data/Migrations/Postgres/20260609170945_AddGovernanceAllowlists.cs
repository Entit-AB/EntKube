using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EntKube.Web.Data.Migrations.Postgres
{
    /// <inheritdoc />
    public partial class AddGovernanceAllowlists : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AppAllowedCaches",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    AppId = table.Column<Guid>(type: "uuid", nullable: false),
                    EnvironmentId = table.Column<Guid>(type: "uuid", nullable: false),
                    RedisClusterId = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AppAllowedCaches", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AppAllowedCaches_Apps_AppId",
                        column: x => x.AppId,
                        principalTable: "Apps",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_AppAllowedCaches_Environments_EnvironmentId",
                        column: x => x.EnvironmentId,
                        principalTable: "Environments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_AppAllowedCaches_RedisClusters_RedisClusterId",
                        column: x => x.RedisClusterId,
                        principalTable: "RedisClusters",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "AppAllowedDatabases",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    AppId = table.Column<Guid>(type: "uuid", nullable: false),
                    EnvironmentId = table.Column<Guid>(type: "uuid", nullable: false),
                    CnpgDatabaseId = table.Column<Guid>(type: "uuid", nullable: true),
                    MongoDatabaseId = table.Column<Guid>(type: "uuid", nullable: true),
                    RegisteredPostgresDatabaseId = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AppAllowedDatabases", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AppAllowedDatabases_Apps_AppId",
                        column: x => x.AppId,
                        principalTable: "Apps",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_AppAllowedDatabases_CnpgDatabases_CnpgDatabaseId",
                        column: x => x.CnpgDatabaseId,
                        principalTable: "CnpgDatabases",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_AppAllowedDatabases_Environments_EnvironmentId",
                        column: x => x.EnvironmentId,
                        principalTable: "Environments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_AppAllowedDatabases_MongoDatabases_MongoDatabaseId",
                        column: x => x.MongoDatabaseId,
                        principalTable: "MongoDatabases",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_AppAllowedDatabases_RegisteredPostgresDatabases_RegisteredP~",
                        column: x => x.RegisteredPostgresDatabaseId,
                        principalTable: "RegisteredPostgresDatabases",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "AppAllowedStorages",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    AppId = table.Column<Guid>(type: "uuid", nullable: false),
                    EnvironmentId = table.Column<Guid>(type: "uuid", nullable: false),
                    StorageLinkId = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AppAllowedStorages", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AppAllowedStorages_Apps_AppId",
                        column: x => x.AppId,
                        principalTable: "Apps",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_AppAllowedStorages_Environments_EnvironmentId",
                        column: x => x.EnvironmentId,
                        principalTable: "Environments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_AppAllowedStorages_StorageLinks_StorageLinkId",
                        column: x => x.StorageLinkId,
                        principalTable: "StorageLinks",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AppAllowedCaches_AppId",
                table: "AppAllowedCaches",
                column: "AppId");

            migrationBuilder.CreateIndex(
                name: "IX_AppAllowedCaches_EnvironmentId",
                table: "AppAllowedCaches",
                column: "EnvironmentId");

            migrationBuilder.CreateIndex(
                name: "IX_AppAllowedCaches_RedisClusterId",
                table: "AppAllowedCaches",
                column: "RedisClusterId");

            migrationBuilder.CreateIndex(
                name: "IX_AppAllowedDatabases_AppId",
                table: "AppAllowedDatabases",
                column: "AppId");

            migrationBuilder.CreateIndex(
                name: "IX_AppAllowedDatabases_CnpgDatabaseId",
                table: "AppAllowedDatabases",
                column: "CnpgDatabaseId");

            migrationBuilder.CreateIndex(
                name: "IX_AppAllowedDatabases_EnvironmentId",
                table: "AppAllowedDatabases",
                column: "EnvironmentId");

            migrationBuilder.CreateIndex(
                name: "IX_AppAllowedDatabases_MongoDatabaseId",
                table: "AppAllowedDatabases",
                column: "MongoDatabaseId");

            migrationBuilder.CreateIndex(
                name: "IX_AppAllowedDatabases_RegisteredPostgresDatabaseId",
                table: "AppAllowedDatabases",
                column: "RegisteredPostgresDatabaseId");

            migrationBuilder.CreateIndex(
                name: "IX_AppAllowedStorages_AppId",
                table: "AppAllowedStorages",
                column: "AppId");

            migrationBuilder.CreateIndex(
                name: "IX_AppAllowedStorages_EnvironmentId",
                table: "AppAllowedStorages",
                column: "EnvironmentId");

            migrationBuilder.CreateIndex(
                name: "IX_AppAllowedStorages_StorageLinkId",
                table: "AppAllowedStorages",
                column: "StorageLinkId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AppAllowedCaches");

            migrationBuilder.DropTable(
                name: "AppAllowedDatabases");

            migrationBuilder.DropTable(
                name: "AppAllowedStorages");
        }
    }
}
