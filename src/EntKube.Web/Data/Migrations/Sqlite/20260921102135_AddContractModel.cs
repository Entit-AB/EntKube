using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EntKube.Web.Data.Migrations.Sqlite
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
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    TenantId = table.Column<Guid>(type: "TEXT", nullable: false),
                    AppId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Origin = table.Column<int>(type: "INTEGER", nullable: false),
                    DevelopedBy = table.Column<string>(type: "TEXT", nullable: true),
                    OnboardedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    GuaranteeEndsAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    ParentAppId = table.Column<Guid>(type: "TEXT", nullable: true),
                    SlaStartsAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    ManagementEndedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    MonthlyWorkCapHours = table.Column<decimal>(type: "TEXT", precision: 18, scale: 2, nullable: true),
                    OnboardingFee = table.Column<decimal>(type: "TEXT", precision: 18, scale: 2, nullable: true),
                    Criticality = table.Column<int>(type: "INTEGER", nullable: true),
                    Notes = table.Column<string>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
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
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    TenantId = table.Column<Guid>(type: "TEXT", nullable: false),
                    CustomerId = table.Column<Guid>(type: "TEXT", nullable: false),
                    AppId = table.Column<Guid>(type: "TEXT", nullable: true),
                    Party = table.Column<int>(type: "INTEGER", nullable: false),
                    Role = table.Column<int>(type: "INTEGER", nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    Email = table.Column<string>(type: "TEXT", nullable: true),
                    Phone = table.Column<string>(type: "TEXT", nullable: true),
                    TeamsHandle = table.Column<string>(type: "TEXT", nullable: true),
                    IsActive = table.Column<bool>(type: "INTEGER", nullable: false),
                    Notes = table.Column<string>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
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
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    TenantId = table.Column<Guid>(type: "TEXT", nullable: false),
                    CustomerId = table.Column<Guid>(type: "TEXT", nullable: false),
                    EffectiveFrom = table.Column<DateTime>(type: "TEXT", nullable: false),
                    PricingModel = table.Column<int>(type: "INTEGER", nullable: false),
                    HourBankHoursPerMonth = table.Column<decimal>(type: "TEXT", precision: 18, scale: 2, nullable: true),
                    Notes = table.Column<string>(type: "TEXT", nullable: true),
                    RecordedBy = table.Column<string>(type: "TEXT", nullable: true),
                    RecordedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
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
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    TenantId = table.Column<Guid>(type: "TEXT", nullable: false),
                    CustomerId = table.Column<Guid>(type: "TEXT", nullable: true),
                    EffectiveFrom = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Currency = table.Column<string>(type: "TEXT", nullable: false),
                    Notes = table.Column<string>(type: "TEXT", nullable: true),
                    CreatedBy = table.Column<string>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
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
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    ApplicationContractId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Level = table.Column<int>(type: "INTEGER", nullable: false),
                    SupportWindow = table.Column<int>(type: "INTEGER", nullable: true),
                    EffectiveFrom = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Reason = table.Column<string>(type: "TEXT", nullable: false),
                    RecordedBy = table.Column<string>(type: "TEXT", nullable: true),
                    RecordedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ApplicationServiceLevels", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ApplicationServiceLevels_ApplicationContracts_ApplicationContractId",
                        column: x => x.ApplicationContractId,
                        principalTable: "ApplicationContracts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "PriceListEntries",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    PriceListId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Kind = table.Column<int>(type: "INTEGER", nullable: false),
                    Key = table.Column<string>(type: "TEXT", nullable: false),
                    Amount = table.Column<decimal>(type: "TEXT", precision: 18, scale: 2, nullable: false),
                    Hours = table.Column<decimal>(type: "TEXT", precision: 18, scale: 2, nullable: true),
                    SortOrder = table.Column<int>(type: "INTEGER", nullable: false)
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
