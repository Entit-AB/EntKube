using System.Reflection;
using EntKube.Web.Data;
using EntKube.Web.Data.Backup;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;

namespace EntKube.Web.Tests;

/// <summary>
/// Every table is either in the backup bundle or named here as deliberately left out.
///
/// <para><b>Why a test and not a review.</b> The bundle is a hand-written list, and a
/// table added to the model does not appear in it by itself. That is exactly how the
/// whole of application management and support — the agreement's annexes, the tickets,
/// the worked hours, the knowledge base — came to be missing from it: nothing was wrong
/// with any one commit, and nobody re-read a three-hundred-line list. The failure only
/// shows up during a server migration, when what was agreed with a customer is gone.</para>
///
/// <para>Adding a table therefore fails this test until somebody decides. Bundling it is
/// usually right; excluding it is fine when there is a reason, and the reason goes next to
/// the name below.</para>
///
/// <para><b>This test does not claim the backup is complete.</b> Writing it turned up
/// thirty tables already outside the bundle, listed in <see cref="KnownGaps"/>. They have
/// not been analysed one by one and several of them plainly matter — the whole Stalwart
/// mail configuration, the mTLS bundles and policies, the rollout policies. What the test
/// guarantees is that the list stops growing by accident, which is how it got to thirty.</para>
/// </summary>
public class BackupCoverageTests
{
    /// <summary>
    /// Tables that are deliberately not backed up, with why.
    ///
    /// <para>The theme is that these are all either derived from something else that <em>is</em>
    /// backed up, or measurements of one particular installation that mean nothing on
    /// another — a restore recreates them or they are rightly lost.</para>
    /// </summary>
    private static readonly Dictionary<string, string> DeliberatelyExcluded = new()
    {
        // Observed state. A restored server talks to the same clusters and re-observes.
        ["DeploymentHealthSnapshot"] = "re-observed from the cluster within minutes",
        ["DeploymentResource"] = "read back from the live cluster",
        ["DeploymentAppliedResource"] = "read back from the live cluster",
        ["AlertIncident"] = "re-raised by the evaluator from live state",
        ["NotificationDelivery"] = "the log of sends from the old server",

        ["ExternalRouteHealthHistory"] = "probes the new server repeats",
        ["ResourceUsageSnapshot"] = "measurements of the old installation's own clusters",

        // Telemetry and cost: volume, and meaningless off the installation that measured it.
        ["CostLedgerEntry"] = "measurements of the old installation's own clusters",
        ["CostLedgerCoverage"] = "measurements of the old installation's own clusters",
        ["CostLedgerCursor"] = "a position in a sweep the new server has not run",
        ["TelemetrySegment"] = "an index of segments in object storage, rebuilt by the node",
    };

    /// <summary>
    /// Tables outside the bundle that nobody has decided about — a backlog, not a policy.
    ///
    /// <para>These were already missing when this test was written. Each is a real loss on
    /// a server migration until somebody works out whether it should be carried; the
    /// obvious ones are the Stalwart mail configuration, the client-CA bundles and mTLS
    /// policies, and the rollout policies, none of which any live system recreates.</para>
    ///
    /// <para>The list is here so that it is visible and so that it cannot quietly grow.
    /// Names come off it as they are dealt with; nothing new should ever go on it.</para>
    /// </summary>
    private static readonly HashSet<string> KnownGaps =
    [
        "ApiToken",
        "ApplicationUser",
        "AuditEvent",
        "BlueprintRollout",
        "BlueprintRolloutTarget",
        "BootstrapRun",
        "BootstrapStepRun",
        "ClientCaBundle",
        "ClientCaCertificate",
        "ClusterCostRate",
        "CnpgBackup",
        "DeploymentRollout",
        "EgressAgent",
        "ExternalGroupMapping",
        "IncidentNote",
        "JitGrant",
        "KeycloakBackup",
        "MeshMtlsPolicy",
        "MongoBackup",
        "OutboundMtlsCredential",
        "RabbitMQBackup",
        "RegisteredPostgresDump",
        "RolloutPolicy",
        "SecretExpiryNotification",
        "StalwartComponentConfig",
        "StalwartMailAccount",
        "StalwartMailDomain",
    ];

    private static IModel Model()
    {
        using ApplicationDbContext db = new(
            new DbContextOptionsBuilder<ApplicationDbContext>()
                .UseSqlite("DataSource=:memory:").Options);

        return db.Model;
    }

    /// <summary>The entity types the bundle carries, by CLR type name.</summary>
    private static HashSet<string> Bundled()
    {
        HashSet<string> carried = [];

        foreach (PropertyInfo property in typeof(BackupBundle).GetProperties())
        {
            if (!property.PropertyType.IsGenericType
                || property.PropertyType.GetGenericTypeDefinition() != typeof(List<>))
            {
                continue;
            }

            Type item = property.PropertyType.GetGenericArguments()[0];

            carried.Add(item.Name);

            // Secrets and identity travel as records rather than entities, because their
            // values are re-encrypted on the way through. "VaultSecretRecord" stands for
            // "VaultSecret".
            if (item.Name.EndsWith("Record", StringComparison.Ordinal))
            {
                carried.Add(item.Name[..^"Record".Length]);
            }
        }

        // The records whose names do not simply drop a suffix.
        carried.Add("SecretVault");                 // VaultRecord
        carried.Add("DockerRegistryCredential");    // DockerCredentialRecord

        return carried;
    }

    [Fact]
    public void Every_table_is_either_backed_up_or_deliberately_left_out()
    {
        HashSet<string> bundled = Bundled();

        List<string> missing =
        [
            .. Model().GetEntityTypes()
                .Where(e => !e.IsOwned())
                .Select(e => e.ClrType.Name)
                .Distinct()
                .Where(name => !bundled.Contains(name))
                .Where(name => !DeliberatelyExcluded.ContainsKey(name))
                .Where(name => !KnownGaps.Contains(name))
                // ASP.NET Identity's own tables are handled by the Users/Roles records.
                .Where(name => !name.StartsWith("Identity", StringComparison.Ordinal))
                .Order()
        ];

        // Joined rather than asserted as a collection, so a failure names every table at
        // once instead of one per run.
        string.Join(", ", missing).Should().BeEmpty(
            "a table that is neither in the backup bundle nor listed as deliberately "
            + "excluded is lost on a server migration, and nobody finds out until then. "
            + "Add it to BackupBundle and to both halves of BackupService, or name it in "
            + "DeliberatelyExcluded with the reason.");
    }

    /// <summary>
    /// A table on either list has to still exist, or the list quietly grants a permanent
    /// exemption to a name that was renamed years ago.
    /// </summary>
    [Fact]
    public void Nothing_is_listed_that_no_longer_exists()
    {
        HashSet<string> entities = [.. Model().GetEntityTypes().Select(e => e.ClrType.Name)];

        DeliberatelyExcluded.Keys.Concat(KnownGaps)
            .Where(name => !entities.Contains(name))
            .Should().BeEmpty("a listing for a table that is gone is dead weight");
    }

    /// <summary>
    /// A name on the backlog that is now in the bundle should come off it, so the list
    /// shrinks as the work is done rather than staying thirty long forever.
    /// </summary>
    [Fact]
    public void Nothing_on_the_backlog_is_already_backed_up()
    {
        HashSet<string> bundled = Bundled();

        string.Join(", ", KnownGaps.Where(bundled.Contains).Order())
            .Should().BeEmpty("this is now carried — take it off KnownGaps");
    }
}
