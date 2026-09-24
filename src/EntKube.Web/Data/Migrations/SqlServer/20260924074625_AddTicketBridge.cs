using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EntKube.Web.Data.Migrations.SqlServer
{
    /// <inheritdoc />
    public partial class AddTicketBridge : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "TicketBridgeConnections",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CustomerId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    System = table.Column<int>(type: "int", nullable: false),
                    Instance = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    SecretHash = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    IsEnabled = table.Column<bool>(type: "bit", nullable: false),
                    DefaultAppId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    PriorityMap = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    LastDeliveryAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    LastError = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    ConsecutiveFailures = table.Column<int>(type: "int", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TicketBridgeConnections", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TicketBridgeConnections_Apps_DefaultAppId",
                        column: x => x.DefaultAppId,
                        principalTable: "Apps",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_TicketBridgeConnections_Customers_CustomerId",
                        column: x => x.CustomerId,
                        principalTable: "Customers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_TicketBridgeConnections_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ExternalTicketLinks",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TicketId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ConnectionId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    System = table.Column<int>(type: "int", nullable: false),
                    Instance = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    ExternalId = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    ExternalKey = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    Url = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    LastSeenAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ExternalTicketLinks", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ExternalTicketLinks_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ExternalTicketLinks_TicketBridgeConnections_ConnectionId",
                        column: x => x.ConnectionId,
                        principalTable: "TicketBridgeConnections",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ExternalTicketLinks_Tickets_TicketId",
                        column: x => x.TicketId,
                        principalTable: "Tickets",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ExternalTicketLinks_ConnectionId",
                table: "ExternalTicketLinks",
                column: "ConnectionId");

            migrationBuilder.CreateIndex(
                name: "IX_ExternalTicketLinks_TenantId_ExternalKey",
                table: "ExternalTicketLinks",
                columns: new[] { "TenantId", "ExternalKey" });

            migrationBuilder.CreateIndex(
                name: "IX_ExternalTicketLinks_TenantId_System_Instance_ExternalId",
                table: "ExternalTicketLinks",
                columns: new[] { "TenantId", "System", "Instance", "ExternalId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ExternalTicketLinks_TicketId",
                table: "ExternalTicketLinks",
                column: "TicketId");

            migrationBuilder.CreateIndex(
                name: "IX_TicketBridgeConnections_CustomerId",
                table: "TicketBridgeConnections",
                column: "CustomerId");

            migrationBuilder.CreateIndex(
                name: "IX_TicketBridgeConnections_DefaultAppId",
                table: "TicketBridgeConnections",
                column: "DefaultAppId");

            migrationBuilder.CreateIndex(
                name: "IX_TicketBridgeConnections_TenantId_CustomerId",
                table: "TicketBridgeConnections",
                columns: new[] { "TenantId", "CustomerId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ExternalTicketLinks");

            migrationBuilder.DropTable(
                name: "TicketBridgeConnections");
        }
    }
}
