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
///   <item><s><c>db.DeploymentRollouts</c></s> — <b>done, and this file is at zero.</b> Delivery
///     owned rollouts and <c>IDeliveryApi</c> had no method for them;
///     <c>GetRolloutsAsync(tenantId, limit)</c> is that method, with this route as its one
///     consumer — the same shape as <c>IDataServicesApi</c>'s addition. The predicate it replaces
///     walked rollout → deployment → app → customer, four navigations of Delivery's own structure
///     written out by hand in the public API.</item>
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
    /// Direct module-table reads left in the public API: <b>zero</b>, from 8.
    ///
    /// <para>Every route the CLI, the MCP server and any future BFF go through now asks a module
    /// contract rather than the shared database. That is the row this programme exists to close
    /// first — see <c>docs/decomposition.md</c> §1, "the API is the product" — and it is the first
    /// of the ledger's rows to reach zero with nothing deliberately exempted.</para>
    ///
    /// <para>⚠️ Zero here does <b>not</b> mean the API is finished: it means no route reaches past
    /// a contract. The routes themselves are still 22 against ~250 services, which is the gap the
    /// agents need closed and a different measurement entirely.</para>
    ///
    /// <para>One hazard came with the last conversion, and it is the same one the Cost page shipped
    /// (§4.0.1, <c>project-enum-storage-casts</c>): the route publishes
    /// <c>Status.ToString()</c>, so the string clients parse now comes from the contract's copy of
    /// the enum. <c>ApiRolloutStatusTests</c> pins the two together, and pins the near-miss —
    /// <c>RolloutStatus</c> is a <em>different</em> enum belonging to a blueprint rollout, one word
    /// away from <c>DeploymentRolloutStatus</c>, and mapping one onto the other would publish a
    /// wrong verdict for every release.</para>
    /// </summary>
    private const int BaselineDirectReads = 0;

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
    /// Module-owned SERVICES still injected straight into a public API route: <b>9</b>, from 11.
    ///
    /// <para><b>A second axis, because the first one's zero was true and incomplete.</b>
    /// <see cref="BaselineDirectReads"/> counts <c>db.Xxx</c> — tables. A route that injects a
    /// module's <em>service</em> instead reads the same database through one more layer and the
    /// table count does not move. That is exactly how Advisor hid: <c>GET /advisor/findings</c>
    /// took <c>OperationsAdvisorService</c> and the acknowledge route took
    /// <c>AdvisorStateService</c>, so this file reported zero while <c>IAdvisorApi</c> had eight
    /// methods and not one caller anywhere in the product.</para>
    ///
    /// <para><b>What the nine are, and why each is still here.</b> None is an adoption that was
    /// skipped; all four need contract surface that does not exist, which is a different change
    /// from using surface that does:</para>
    /// <list type="bullet">
    ///   <item><c>CostRateService</c> ×4 and <c>CostLedgerService</c> ×2 — <b>Cost has no contract
    ///     at all.</b> Six of the nine are one module, and four of those six are writes (a price
    ///     sheet upsert and a delete), so this is not a read contract's shape either. It is the
    ///     largest single gap this measure shows.</item>
    ///   <item><c>KubernetesOperationsService</c> ×2 — Fleet has <c>IFleetApi</c>, which describes
    ///     clusters. These routes <em>act</em> on one. A contract that performs cluster operations
    ///     is the agent boundary (docs/decomposition.md §5), not a read method.</item>
    ///   <item><c>ComponentUpgradeService</c> ×1 — Catalog has <c>ICatalogApi</c>, whose 44
    ///     consumers make it the most adopted contract in the product, and it still has no upgrade
    ///     method. Upgrading installs a chart; see <c>project-component-upgrade-action</c> for why
    ///     that is not a read.</item>
    /// </list>
    ///
    /// <para>⚠️ The same hazard as the other ratchets: this counts a type NAME in a parameter
    /// position, so it cannot see a route that resolves a service from the provider by hand, and it
    /// would count a comment naming one. Comments are stripped; the provider shape is not currently
    /// present and would need its own measure if it appeared.</para>
    /// </summary>
    private const int BaselineModuleServices = 9;

    [Fact]
    public void The_public_api_does_not_reach_past_a_contract_into_a_modules_services()
    {
        (int count, IReadOnlyList<string> where) = MeasureModuleServices();

        count.Should().BeLessThanOrEqualTo(BaselineModuleServices,
            "injecting a module's own service into a product-surface route is the same boundary "
            + "break as reading its tables, one layer further in — and the table count cannot see "
            + $"it. Ask the module's contract. Found: {string.Join(", ", where)}");
    }

    [Fact]
    public void The_module_service_baseline_does_not_carry_slack()
    {
        (int count, IReadOnlyList<string> where) = MeasureModuleServices();

        count.Should().Be(BaselineModuleServices,
            $"now {count} ({string.Join(", ", where)}) — bank it by lowering the baseline");
    }

    /// <summary>
    /// Every parameter of a public API route handler whose type <c>ModuleServiceMap</c> places in a
    /// module. Comments stripped, occurrences counted rather than lines, as above.
    /// </summary>
    private static (int Count, IReadOnlyList<string> Where) MeasureModuleServices()
    {
        string root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, Web));
        string endpoints = Path.Combine(root, "Services", "PublicApi", "PublicApiEndpoints.cs");

        File.Exists(endpoints).Should().BeTrue();

        string map = File.ReadAllText(Path.Combine(root, "Modules", "ModuleServiceMap.cs"));

        IReadOnlySet<string> owned = Regex
            .Matches(map, @"\[typeof\(([\w.]+)\)\]\s*=\s*Module\.(\w+)")
            .Select(m => m.Groups[1].Value.Split('.')[^1])
            .ToHashSet(StringComparer.Ordinal);

        owned.Should().NotBeEmpty(
            "ModuleServiceMap is how this test knows which services belong to a module; if it "
            + "stops parsing, an empty set would make every route look clean");

        List<string> found = [];

        foreach (string line in File.ReadAllLines(endpoints))
        {
            string trimmed = line.TrimStart();

            if (trimmed.StartsWith("//", StringComparison.Ordinal)
                || trimmed.StartsWith('*')) continue;

            foreach (Match m in Regex.Matches(line, @"\b([A-Z]\w*(?:Service|Resolver|Store))\s+[a-z]\w*\s*[,)]"))
            {
                if (owned.Contains(m.Groups[1].Value))
                {
                    found.Add(m.Groups[1].Value);
                }
            }
        }

        return (found.Count, found);
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
