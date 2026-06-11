using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EntKube.Web.Data.Migrations.Postgres
{
    /// <inheritdoc />
    public partial class AddMessagingBinding : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "MessagingBindings",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    RabbitMQClusterId = table.Column<Guid>(type: "uuid", nullable: false),
                    AppDeploymentId = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    Vhost = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    QueueName = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    ExchangeName = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    KubernetesSecretName = table.Column<string>(type: "character varying(253)", maxLength: 253, nullable: false),
                    SyncEnabled = table.Column<bool>(type: "boolean", nullable: false),
                    LastSyncedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MessagingBindings", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MessagingBindings_AppDeployments_AppDeploymentId",
                        column: x => x.AppDeploymentId,
                        principalTable: "AppDeployments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_MessagingBindings_RabbitMQClusters_RabbitMQClusterId",
                        column: x => x.RabbitMQClusterId,
                        principalTable: "RabbitMQClusters",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_MessagingBindings_AppDeploymentId",
                table: "MessagingBindings",
                column: "AppDeploymentId");

            migrationBuilder.CreateIndex(
                name: "IX_MessagingBindings_RabbitMQClusterId",
                table: "MessagingBindings",
                column: "RabbitMQClusterId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "MessagingBindings");
        }
    }
}
