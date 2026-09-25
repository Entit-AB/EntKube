using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EntKube.Web.Data.Migrations.Postgres
{
    /// <inheritdoc />
    public partial class AddContractModel : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ApplicationContracts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    AppId = table.Column<Guid>(type: "uuid", nullable: false),
                    Origin = table.Column<int>(type: "integer", nullable: false),
                    DevelopedBy = table.Column<string>(type: "text", nullable: true),
                    OnboardedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    GuaranteeEndsAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ParentAppId = table.Column<Guid>(type: "uuid", nullable: true),
                    SlaStartsAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ManagementEndedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    MonthlyWorkCapHours = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: true),
                    OnboardingFee = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: true),
                    Criticality = table.Column<int>(type: "integer", nullable: true),
                    Notes = table.Column<string>(type: "text", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ApplicationContracts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ApplicationContracts_Apps_AppId",
                        column: x => x.AppId,
                        principalTable: "Apps",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ApplicationContracts_Apps_ParentAppId",
                        column: x => x.ParentAppId,
                        principalTable: "Apps",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ApplicationContracts_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ContractContacts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    CustomerId = table.Column<Guid>(type: "uuid", nullable: false),
                    AppId = table.Column<Guid>(type: "uuid", nullable: true),
                    Party = table.Column<int>(type: "integer", nullable: false),
                    Role = table.Column<int>(type: "integer", nullable: false),
                    Name = table.Column<string>(type: "text", nullable: false),
                    Email = table.Column<string>(type: "text", nullable: true),
                    Phone = table.Column<string>(type: "text", nullable: true),
                    TeamsHandle = table.Column<string>(type: "text", nullable: true),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    Notes = table.Column<string>(type: "text", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ContractContacts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ContractContacts_Apps_AppId",
                        column: x => x.AppId,
                        principalTable: "Apps",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ContractContacts_Customers_CustomerId",
                        column: x => x.CustomerId,
                        principalTable: "Customers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ContractContacts_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "PortfolioAgreements",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    CustomerId = table.Column<Guid>(type: "uuid", nullable: false),
                    EffectiveFrom = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    PricingModel = table.Column<int>(type: "integer", nullable: false),
                    HourBankHoursPerMonth = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: true),
                    Notes = table.Column<string>(type: "text", nullable: true),
                    RecordedBy = table.Column<string>(type: "text", nullable: true),
                    RecordedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PortfolioAgreements", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PortfolioAgreements_Customers_CustomerId",
                        column: x => x.CustomerId,
                        principalTable: "Customers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_PortfolioAgreements_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "PriceLists",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    CustomerId = table.Column<Guid>(type: "uuid", nullable: true),
                    EffectiveFrom = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Currency = table.Column<string>(type: "text", nullable: false),
                    Notes = table.Column<string>(type: "text", nullable: true),
                    CreatedBy = table.Column<string>(type: "text", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PriceLists", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PriceLists_Customers_CustomerId",
                        column: x => x.CustomerId,
                        principalTable: "Customers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_PriceLists_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ApplicationServiceLevels",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ApplicationContractId = table.Column<Guid>(type: "uuid", nullable: false),
                    Level = table.Column<int>(type: "integer", nullable: false),
                    SupportWindow = table.Column<int>(type: "integer", nullable: true),
                    EffectiveFrom = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Reason = table.Column<string>(type: "text", nullable: false),
                    RecordedBy = table.Column<string>(type: "text", nullable: true),
                    RecordedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ApplicationServiceLevels", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ApplicationServiceLevels_ApplicationContracts_ApplicationCo~",
                        column: x => x.ApplicationContractId,
                        principalTable: "ApplicationContracts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "PriceListEntries",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    PriceListId = table.Column<Guid>(type: "uuid", nullable: false),
                    Kind = table.Column<int>(type: "integer", nullable: false),
                    Key = table.Column<string>(type: "text", nullable: false),
                    Amount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    Hours = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: true),
                    SortOrder = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PriceListEntries", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PriceListEntries_PriceLists_PriceListId",
                        column: x => x.PriceListId,
                        principalTable: "PriceLists",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ApplicationContracts_AppId",
                table: "ApplicationContracts",
                column: "AppId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ApplicationContracts_ParentAppId",
                table: "ApplicationContracts",
                column: "ParentAppId");

            migrationBuilder.CreateIndex(
                name: "IX_ApplicationContracts_TenantId",
                table: "ApplicationContracts",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_ApplicationServiceLevels_ApplicationContractId_EffectiveFrom",
                table: "ApplicationServiceLevels",
                columns: new[] { "ApplicationContractId", "EffectiveFrom" });

            migrationBuilder.CreateIndex(
                name: "IX_ContractContacts_AppId",
                table: "ContractContacts",
                column: "AppId");

            migrationBuilder.CreateIndex(
                name: "IX_ContractContacts_CustomerId_Party_Role",
                table: "ContractContacts",
                columns: new[] { "CustomerId", "Party", "Role" });

            migrationBuilder.CreateIndex(
                name: "IX_ContractContacts_TenantId",
                table: "ContractContacts",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_PortfolioAgreements_CustomerId_EffectiveFrom",
                table: "PortfolioAgreements",
                columns: new[] { "CustomerId", "EffectiveFrom" });

            migrationBuilder.CreateIndex(
                name: "IX_PortfolioAgreements_TenantId",
                table: "PortfolioAgreements",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_PriceListEntries_PriceListId_Kind_Key",
                table: "PriceListEntries",
                columns: new[] { "PriceListId", "Kind", "Key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PriceLists_CustomerId",
                table: "PriceLists",
                column: "CustomerId");

            migrationBuilder.CreateIndex(
                name: "IX_PriceLists_TenantId_CustomerId_EffectiveFrom",
                table: "PriceLists",
                columns: new[] { "TenantId", "CustomerId", "EffectiveFrom" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ApplicationServiceLevels");

            migrationBuilder.DropTable(
                name: "ContractContacts");

            migrationBuilder.DropTable(
                name: "PortfolioAgreements");

            migrationBuilder.DropTable(
                name: "PriceListEntries");

            migrationBuilder.DropTable(
                name: "ApplicationContracts");

            migrationBuilder.DropTable(
                name: "PriceLists");
        }
    }
}
