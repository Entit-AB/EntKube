using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EntKube.Web.Data.Migrations.SqlServer
{
    /// <summary>
    /// Makes IX_VaultSecretVersions_SecretId_VersionNumber unique, so one secret can never
    /// again hold two rows numbered 4 — and renumbers any it already holds, first.
    ///
    /// <para>⭐ The renumber, not the constraint, is why this file is hand-finished. See the
    /// SQLite copy of this migration for the full reasoning: this application migrates itself
    /// at boot with no database console behind it in production (docs/decomposition.md §9.3),
    /// so a <c>CREATE UNIQUE INDEX</c> that trips over pre-existing duplicates is an
    /// installation that will not start. Renumbering always succeeds, so the index that
    /// follows cannot fail.</para>
    ///
    /// <para>Only secrets that actually have a duplicated number are touched, and those are
    /// renumbered 1..N in creation order (CreatedAt, then Id as a deterministic tie-break).
    /// A healthy database writes zero rows here.</para>
    /// </summary>
    public partial class UniqueSecretVersionNumber : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_VaultSecretVersions_SecretId_VersionNumber",
                table: "VaultSecretVersions");

            // T-SQL permits UPDATE directly against a CTE, but the UPDATE..FROM..JOIN form is
            // written out instead: it leaves no doubt which table is being written, which is
            // worth more than brevity in a statement that rewrites audit history.
            migrationBuilder.Sql(@"
WITH renumbered AS (
    SELECT v.[Id] AS RowId,
           ROW_NUMBER() OVER (PARTITION BY v.[SecretId]
                              ORDER BY v.[CreatedAt], v.[Id]) AS NewNumber
    FROM [VaultSecretVersions] v
    WHERE v.[SecretId] IN (
        SELECT [SecretId]
        FROM [VaultSecretVersions]
        GROUP BY [SecretId], [VersionNumber]
        HAVING COUNT(*) > 1)
)
UPDATE v
SET v.[VersionNumber] = CAST(r.NewNumber AS int)
FROM [VaultSecretVersions] v
INNER JOIN renumbered r ON r.RowId = v.[Id]
WHERE v.[VersionNumber] <> r.NewNumber;");

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
