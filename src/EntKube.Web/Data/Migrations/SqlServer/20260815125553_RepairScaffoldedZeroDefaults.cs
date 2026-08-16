using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EntKube.Web.Data.Migrations.SqlServer
{
    /// <summary>
    /// Repairs rows that earlier migrations backfilled with the CLR zero value instead of the
    /// entity's declared default.
    ///
    /// `dotnet ef migrations add` scaffolds AddColumn with the type's zero value and ignores the
    /// property initializer, so a column declared `= 30` was added to existing rows as 0. Editing
    /// those historical migrations fixes nothing — any database that already ran them keeps the
    /// wrong values — so the correction has to be a forward data migration.
    ///
    /// Six columns are affected, each in a narrow window between its table being created and
    /// the column being added:
    ///
    ///   CnpgClusters.RetentionDays  (0 → 30)     renders retentionPolicy: "0d" in the CNPG
    ///                                           ObjectStore manifest, which is not a usable
    ///                                           retention window.
    ///   VpnTunnels.IkeLifetime      (0 → 86400)  emits rekey_time = 0s in swanctl.conf, which
    ///   VpnTunnels.ChildLifetime    (0 → 3600)   strongSwan reads as "never rekey".
    ///   BootstrapRuns.Mode          ('' → Bootstrap)
    ///   OpenLdapComponentConfigs.LtbPasswdExposeMode     ('' → Gateway)
    ///   OpenLdapComponentConfigs.PhpLdapAdminExposeMode  ('' → Gateway)
    ///
    /// Scoping differs by column because the risk of clobbering a deliberate value differs:
    ///
    /// - Retention is bounded by min="1" in every UI that writes it, so 0 cannot be a user's
    ///   choice and needs no further qualification.
    /// - The VPN lifetime inputs have no minimum, and rekey_time = 0 is a legal (if unusual)
    ///   strongSwan setting, so those are limited to tunnels created before the migration that
    ///   introduced the columns (20260602103442). A tunnel created after that date keeps
    ///   whatever its operator entered.
    /// - The three enum columns are persisted via HasConversion&lt;string&gt;(), which can only
    ///   ever write a member name. An empty string is therefore unreachable through any code
    ///   path and is unambiguously the scaffolding artifact, so those need no qualification.
    ///
    /// Expected to affect zero rows on most databases; it only bites an instance that was live
    /// and using these features during the gaps.
    /// </summary>
    public partial class RepairScaffoldedZeroDefaults : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                UPDATE [CnpgClusters]
                SET [RetentionDays] = 30
                WHERE [RetentionDays] = 0;
                """);

            // CreatedAt is datetime2; the unambiguous ISO-8601 literal avoids any dependence
            // on the connection's DATEFORMAT.
            migrationBuilder.Sql("""
                UPDATE [VpnTunnels]
                SET [IkeLifetime] = 86400
                WHERE [IkeLifetime] = 0
                  AND [CreatedAt] < CONVERT(datetime2, '2026-06-02T10:34:42', 126);
                """);

            migrationBuilder.Sql("""
                UPDATE [VpnTunnels]
                SET [ChildLifetime] = 3600
                WHERE [ChildLifetime] = 0
                  AND [CreatedAt] < CONVERT(datetime2, '2026-06-02T10:34:42', 126);
                """);

            migrationBuilder.Sql("""
                UPDATE [BootstrapRuns]
                SET [Mode] = 'Bootstrap'
                WHERE [Mode] = '';
                """);

            migrationBuilder.Sql("""
                UPDATE [OpenLdapComponentConfigs]
                SET [LtbPasswdExposeMode] = 'Gateway'
                WHERE [LtbPasswdExposeMode] = '';
                """);

            migrationBuilder.Sql("""
                UPDATE [OpenLdapComponentConfigs]
                SET [PhpLdapAdminExposeMode] = 'Gateway'
                WHERE [PhpLdapAdminExposeMode] = '';
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Deliberately empty. Reverting would restore values that were never intended and
            // that break the configs they feed, and once repaired the original zeros are
            // indistinguishable from legitimately-set ones.
        }
    }
}
