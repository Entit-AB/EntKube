using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EntKube.Web.Data.Migrations.Postgres
{
    /// <summary>
    /// Makes IX_VaultSecretVersions_SecretId_VersionNumber unique, so one secret can never
    /// again hold two rows numbered 4 — and renumbers any it already holds, first.
    ///
    /// <para>⭐ The renumber, not the constraint, is why this file is hand-finished. See the
    /// SQLite copy of this migration for the full reasoning: in short, this application
    /// migrates itself at boot with no database console behind it in production
    /// (docs/decomposition.md §9.3), so a <c>CREATE UNIQUE INDEX</c> that trips over
    /// pre-existing duplicates is an installation that will not start rather than a problem
    /// someone can go and fix. Renumbering always succeeds, so the index that follows cannot
    /// fail.</para>
    ///
    /// <para>Only secrets that actually have a duplicated number are touched, and those are
    /// renumbered 1..N in creation order (CreatedAt, then Id as a deterministic tie-break).
    /// A healthy database writes zero rows here.</para>
    ///
    /// <para>⚠️ Postgres is the provider that matters most here: it is what production runs,
    /// so this is the copy that will meet real duplicates.</para>
    /// </summary>
    public partial class UniqueSecretVersionNumber : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_VaultSecretVersions_SecretId_VersionNumber",
                table: "VaultSecretVersions");

            // ⚠️ ROW_NUMBER() is bigint in Postgres and VersionNumber is integer; the CAST is
            // explicit rather than leaning on the assignment cast, so the statement says what
            // it means and does not depend on a coercion rule.
            migrationBuilder.Sql(@"
WITH renumbered AS (
    SELECT v.""Id"" AS row_id,
           CAST(ROW_NUMBER() OVER (PARTITION BY v.""SecretId""
                                   ORDER BY v.""CreatedAt"", v.""Id"") AS integer) AS new_number
    FROM ""VaultSecretVersions"" v
    WHERE v.""SecretId"" IN (
        SELECT ""SecretId""
        FROM ""VaultSecretVersions""
        GROUP BY ""SecretId"", ""VersionNumber""
        HAVING COUNT(*) > 1)
)
UPDATE ""VaultSecretVersions"" v
SET ""VersionNumber"" = r.new_number
FROM renumbered r
WHERE v.""Id"" = r.row_id AND v.""VersionNumber"" <> r.new_number;");

            migrationBuilder.CreateIndex(
                name: "IX_VaultSecretVersions_SecretId_VersionNumber",
                table: "VaultSecretVersions",
                columns: new[] { "SecretId", "VersionNumber" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // ⚠️ Down drops the constraint and nothing else. The renumber is not undone and
            // cannot be: the old numbering was ambiguous by definition, so there is no
            // "before" to restore to.
            migrationBuilder.DropIndex(
                name: "IX_VaultSecretVersions_SecretId_VersionNumber",
                table: "VaultSecretVersions");

            migrationBuilder.CreateIndex(
                name: "IX_VaultSecretVersions_SecretId_VersionNumber",
                table: "VaultSecretVersions",
                columns: new[] { "SecretId", "VersionNumber" });
        }
    }
}
