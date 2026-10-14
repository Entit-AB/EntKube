using EntKube.Web.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EntKube.Web.Data.Migrations.Sqlite
{
    // ⚠️ Deliberately invisible to EF: no Designer file and no [Migration] attribute, so the
    // migrations assembly does not find it and this never runs. That is not an oversight to
    // repair — docs/migrations-defect.md explains why. The column it adds is created at
    // startup instead, by an idempotent shim in Program.cs, which means an existing database
    // already HAS the column while carrying no history row for this migration. Give it the
    // attributes and the next startup runs it against a column that exists: on SQLite that is
    // a duplicate-column error inside a migration, where no try/catch protects it, and the
    // application does not start.
    public partial class AddDeploymentGitUrl : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "GitUrl",
                table: "AppDeployments",
                type: "TEXT",
                nullable: true);
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "GitUrl",
                table: "AppDeployments");
        }
    }
}
