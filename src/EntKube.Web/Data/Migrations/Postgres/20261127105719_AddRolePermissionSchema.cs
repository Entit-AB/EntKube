using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EntKube.Web.Data.Migrations.Postgres
{
    /// <inheritdoc />
    public partial class AddRolePermissionSchema : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // 0 is deliberate, unlike the entity's initializer (the current version). Every role
            // that exists now was written before permissions were enforced, and 0 is what tells
            // the startup carry-across to grant it what its members could already do
            // (TenantRoleService.CarryRolesAcrossAsync).
            migrationBuilder.AddColumn<int>(
                name: "PermissionSchema",
                table: "TenantRoles",
                type: "integer",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "PermissionSchema",
                table: "TenantRoles");
        }
    }
}
