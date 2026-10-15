using System.Text.RegularExpressions;
using FluentAssertions;

namespace EntKube.Web.Tests;

/// <summary>
/// How much of the public API still reads a module's tables directly instead of asking its
/// contract — a number that may only come down.
///
/// <para><b>Why this file and not the general ratchet.</b>
/// <see cref="CatalogContractAdoptionTests"/> counts Catalog only, and it reached the public API by
/// accident: <c>PublicApiEndpoints</c> is not a service, so ownership resolution skipped it until
/// #220 stopped treating "unmapped" as "exempt". The public API deserves its own count for a
/// reason that has nothing to do with Catalog — <b>it is the product surface</b>. The CLI, the MCP
/// server and every future BFF or in-cluster agent consume these routes, so a route that reaches
/// into a module's tables is a module that cannot be deployed separately, whatever the contracts
/// say.</para>
///
/// <para><b>What the eight were.</b> `AppDeployments` 3, `KubernetesClusters` 2, `Apps` 1,
/// `AlertIncidents` 1, `DeploymentRollouts` 1. Six are now `IDeliveryApi` / `IFleetApi` /
/// `ICatalogApi`, including both deployment-ownership checks — the ones that decide whether a token
/// may sync or restart somebody else's deployment, written by hand as
/// <c>d.App.Customer.TenantId == principal.TenantId</c> at two sites.</para>
///
/// <para><b>What the two are, and why they are not conversions.</b> Both need contract surface that
/// does not exist, which is a different change from adopting surface that does:</para>
/// <list type="bullet">
///   <item><s><c>db.AlertIncidents</c></s> — <b>done.</b> Telemetry has a contract now, with
///     exactly one method, because this route is its only service-level consumer. Measuring first
///     is what kept it that small: thirty foreign reads touch Telemetry's tables, but twenty are
///     <c>BackupService</c>'s whole-database export and <c>TenantService</c>'s tenant purge —
///     neither of which a per-module <em>read</em> contract can serve — and eight more are in Razor
///     pages, which are not modules. Notification-provider config is deliberately left out: that
///     row carries an SMTP password, and a contract method would publish it to every module.</item>
///   <item><c>db.DeploymentRollouts</c> — Delivery owns rollouts but <c>IDeliveryApi</c> has no
///     method for them. One method with one consumer is exactly how <c>IDataServicesApi</c> was
///     added, so this is a small addition rather than a blocked one — just not the same idea as
///     this change.</item>
/// </list>
///
/// <para>⚠️ A route that needs a field no DTO carries is <em>not</em> a reason to keep the query.
/// Three came up here and none needed new surface: the cluster's component count came from
/// <c>ICatalogApi</c> grouped by cluster (it is Catalog's number, not Fleet's), the app's
/// deployment count from Delivery's own list, and the cluster NAME on a deployment from one Fleet
/// call. Adding a field to a DTO for one route's convenience is how a contract turns back into a
/// second name for the database.</para>
/// </summary>
public class PublicApiContractAdoptionTests
{
    private const string Web = "../../../../../src/EntKube.Web";

    /// <summary>
    /// Direct module-table reads left in the public API. Was 8.
    /// See this class's remarks for what the two are and why.
    /// </summary>
    private const int BaselineDirectReads = 1;

    [Fact]
    public void The_public_api_asks_the_contracts()
    {
        (int reads, IReadOnlyList<string> where) = Measure();

        reads.Should().BeLessThanOrEqualTo(BaselineDirectReads,
            "the public API is the product surface — the CLI, the MCP server and every future BFF "
            + "read these routes. A route that reaches into a module's tables is a module that "
            + $"cannot be deployed separately. Found: {string.Join(", ", where)}");
    }

    [Fact]
    public void The_public_api_baseline_does_not_carry_slack()
    {
        (int reads, IReadOnlyList<string> where) = Measure();

        reads.Should().Be(BaselineDirectReads,
            $"now {reads} ({string.Join(", ", where)}) — bank it by lowering the baseline, or the "
            + "slack lets direct access creep back for free");
    }

    /// <summary>
    /// Every <c>db.Xxx</c> in the public API whose entity <c>ModuleMap</c> places in a module.
    ///
    /// <para>Occurrences rather than matching lines, and comments stripped, for the same reasons the
    /// other ratchets do it: prose naming a table inflated one of these metrics once already, and a
    /// single line can read two tables.</para>
    /// </summary>
    private static (int Reads, IReadOnlyList<string> Where) Measure()
    {
        string root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, Web));

        string endpoints = Path.Combine(root, "Services", "PublicApi", "PublicApiEndpoints.cs");

        File.Exists(endpoints).Should().BeTrue(
            "this test reads the public API's source; if it moved, the test has to follow rather "
            + "than quietly stop checking anything");

        string map = File.ReadAllText(Path.Combine(root, "Modules", "ModuleMap.cs"));

        IReadOnlySet<string> owned = Regex.Matches(map, @"\[typeof\((\w+)\)\]\s*=\s*Module\.(\w+)")
            .Select(m => m.Groups[1].Value)
            .ToHashSet(StringComparer.Ordinal);

        owned.Should().NotBeEmpty("ModuleMap is how this test knows which tables belong to a module");

        List<string> found = [];

        foreach (string line in File.ReadAllLines(endpoints))
        {
            string trimmed = line.TrimStart();

            if (trimmed.StartsWith("//", StringComparison.Ordinal)
                || trimmed.StartsWith('*')) continue;

            foreach (Match m in Regex.Matches(line, @"\bdb\.(\w+)"))
            {
                string set = m.Groups[1].Value;

                if (set is "SaveChangesAsync" or "Set" or "Database") continue;

                // DbSet properties are plural; the map is keyed by the entity type.
                string entity = set.EndsWith("ies", StringComparison.Ordinal)
                    ? string.Concat(set.AsSpan(0, set.Length - 3), "y")
                    : set.TrimEnd('s');

                if (owned.Contains(entity)) found.Add($"db.{set}");
            }
        }

        return (found.Count, found);
    }
}
