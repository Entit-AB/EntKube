using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EntKube.Web.Data.Migrations.Sqlite
{
    /// <summary>
    /// Makes IX_VaultSecretVersions_SecretId_VersionNumber unique, so one secret can never
    /// again hold two rows numbered 4 — and renumbers any it already holds, first.
    ///
    /// <para>⭐ <b>The renumber is the whole point of this file.</b> Adding the constraint is
    /// two scaffolded lines; what needed deciding is what happens on an installation that
    /// already has duplicates, because <c>CREATE UNIQUE INDEX</c> fails outright on one. This
    /// application applies its own migrations at boot (Program.cs) and there is no database
    /// console behind it in production (docs/decomposition.md §9.3), so a migration that
    /// throws is not an inconvenience to fix at leisure — it is an installation that does not
    /// start, with no way in to repair it. Discovering the duplicates at that moment is the
    /// one outcome that must not happen.</para>
    ///
    /// <para><b>Renumber, not refuse.</b> Refusing with a message naming the secret reads as
    /// the careful option and is the wrong one here: the message would arrive in a crash loop
    /// on a box whose operator has no SQL prompt to act on it. Renumbering always succeeds —
    /// ROW_NUMBER is distinct within its partition by construction — so the unique index that
    /// follows cannot fail, and the repair is deterministic rather than a judgement call made
    /// under outage pressure.</para>
    ///
    /// <para><b>What the renumber does.</b> For each secret that has at least one duplicated
    /// number, every one of that secret's version rows is renumbered 1..N in creation order
    /// (CreatedAt, then Id to break a tie deterministically). Secrets with no duplicate are
    /// not touched at all, so a healthy database — which is nearly all of them — writes zero
    /// rows here.</para>
    ///
    /// <para>⚠️ <b>This rewrites recorded version numbers, and that is a real loss.</b> Dense
    /// renumbering is chosen because the alternative — pushing the loser to MAX+1 — leaves the
    /// history out of chronological order, which is worse for the one thing a version list is
    /// for. The numbers are display and ordering only: nothing keys off VersionNumber
    /// (VaultService orders by it, two Razor pages render "v@ver.VersionNumber", BackupService
    /// round-trips it), so no reference breaks. What is genuinely gone is the old numbering of
    /// an already-ambiguous history — which is the state being repaired.</para>
    ///
    /// <para>⚠️ The renumber and the index are one migration, so EF runs them in one
    /// transaction: either the database comes out with contiguous numbers and the constraint,
    /// or it comes out untouched.</para>
    /// </summary>
    public partial class UniqueSecretVersionNumber : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_VaultSecretVersions_SecretId_VersionNumber",
                table: "VaultSecretVersions");

            // SQLite: WITH may precede UPDATE, but UPDATE..FROM only arrived in 3.33, so the
            // new number is pulled back with a correlated subquery against the CTE rather than
            // joined. Equivalent, and does not depend on the bundled engine's age.
            migrationBuilder.Sql(@"
WITH renumbered AS (
    SELECT v.""Id"" AS RowId,
           ROW_NUMBER() OVER (PARTITION BY v.""SecretId""
                              ORDER BY v.""CreatedAt"", v.""Id"") AS NewNumber
    FROM ""VaultSecretVersions"" v
    WHERE v.""SecretId"" IN (
        SELECT ""SecretId""
        FROM ""VaultSecretVersions""
        GROUP BY ""SecretId"", ""VersionNumber""
        HAVING COUNT(*) > 1)
)
UPDATE ""VaultSecretVersions""
SET ""VersionNumber"" = (SELECT NewNumber FROM renumbered
                         WHERE renumbered.RowId = ""VaultSecretVersions"".""Id"")
WHERE ""Id"" IN (SELECT RowId FROM renumbered);");

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
