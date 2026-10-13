using System.Text.RegularExpressions;
using FluentAssertions;

namespace EntKube.Web.Tests;

/// <summary>
/// How many services still reach into Catalog's table directly instead of asking
/// <c>ICatalogApi</c> — a number that may only come down.
///
/// <para><b>Why this test exists at all.</b> Four module contracts were written, registered in
/// DI and unit-tested, and <em>nothing in production called any of them</em>. An interface with
/// no consumers does not decompose anything; it only looks like progress. This counts the thing
/// the contract was supposed to replace, so "we have a contract" and "the contract is used" stop
/// being the same claim.</para>
///
/// <para><b>What measuring it found.</b> 87 direct queries in 34 foreign services, and — missed
/// by the first attempt — <b>22 property writes in 9 of them</b>. The earlier count looked for
/// <c>.ClusterComponents.Add/Remove/Update</c>, found none, and concluded the table was read-only
/// outside Catalog. EF writes through change tracking, so <c>component.HelmValues = x</c> followed
/// by <c>SaveChangesAsync</c> is a write with no <c>Update()</c> anywhere near it. That false
/// conclusion was written into <c>ICatalogApi</c>'s own documentation as the reason it had no
/// write methods, which is how a measurement mistake becomes an API.</para>
///
/// <para><b>What is left is sequenced behind the credential work.</b> Of the direct queries, the
/// ones whose surrounding code also takes <c>cluster.Kubeconfig</c> cannot move: the contract
/// deliberately carries no credential, so those callers have to go through
/// <c>IClusterClient</c> first (docs/decomposition.md §4.0.1). That is seven services, and it
/// makes Phase 1 and the custody work the same dependency chain rather than two separate efforts.</para>
/// </summary>
public class CatalogContractAdoptionTests
{
    private const string Web = "../../../../../src/EntKube.Web";

    /// <summary>
    /// Direct <c>.ClusterComponents</c> uses in services Catalog does not own, measured
    /// 2026-10-09. Was 87 before Tempo, Mimir and Loki moved to the contract, then 66, now 63
    /// with CnpgService, MongoService and KyvernoPolicyService.
    ///
    /// <para><b>39 now</b>, after <c>HarborService</c> and <c>KeycloakService</c> reached zero
    /// and <c>SupportMailboxService</c>'s server listing moved. Three of those were the same
    /// correlated subquery — <c>db.ClusterComponents.Any(comp =&gt; comp.Id == c.ClusterComponentId
    /// &amp;&amp; comp.ClusterId == clusterId)</c> — which never checked the cluster against the
    /// tenant at all: a cluster id from elsewhere matched whatever configs pointed at its
    /// components. <c>GetComponentsForClusterAsync</c> is tenant-first, so asking Catalog closed
    /// that as a side effect.</para>
    ///
    /// <para>One of the two <c>.Include(c =&gt; c.ExternalRoutes)</c> on the detection queries was
    /// <b>dead</b> — nothing read a detected Harbor's routes — so it stopped loading a collection
    /// per component for nothing. Keycloak's was live (the suggested admin URL is built from the
    /// hostnames) and became one query keyed by component id.</para>
    ///
    /// <para><b>Before that, 45</b>, after <c>StalwartService</c>'s nine and <c>OpenLdapService</c>'s
    /// write. Stalwart's were all the same shape — "fetch this component, scoped to the tenant",
    /// which is <c>GetComponentAsync</c> exactly — and converting them let its
    /// <c>StalwartInstance</c> DTO stop handing a tracked EF entity to a Razor page. MailTab
    /// reached through it for <c>Component.Id</c> and <c>Component.Cluster.Name</c> and nothing
    /// else, which is two fields the contract already returns.</para>
    ///
    /// <para><b>Before that, 55.</b> Eight more services stopped touching the table, each of which had
    /// exactly one query: <c>DatabaseService</c>, <c>ElasticsearchService</c>, <c>KafkaService</c>,
    /// <c>KedaScalerService</c>, <c>RabbitMQService</c>, <c>RedisService</c>, <c>TenantService</c>
    /// and <c>VpnService</c>. Every one asked a variant of "is this operator installed", and every
    /// one keeps its own predicate in memory rather than getting a contract method — the alias
    /// lists are not symmetrical across <c>Name</c>, <c>ReleaseName</c> and <c>HelmChartName</c>,
    /// so a uniform match would answer about installations they have always rejected.</para>
    ///
    /// <para>⚠️ <c>TailscaleService</c> looks like a ninth and is not. Its result record hands a
    /// <c>ClusterComponent</c> to a Razor page that reaches through it for
    /// <c>Component.Cluster.Kubeconfig</c> — five times. Converting the query means converting the
    /// page's cluster access, which is credential-custody work rather than contract work, so it is
    /// left whole rather than half-moved.</para>
    ///
    /// <para><b>What the 63 were, which says what is actually left to do.</b> 14 are
    /// credential-bound — their surrounding method also takes <c>cluster.Kubeconfig</c>, and the
    /// contract deliberately carries no credential, so those wait on <c>IClusterClient</c>.
    /// <b>13 have no tenant at all</b>: background sweeps like <c>AlertSyncService</c> and
    /// <c>ResourceUsageCollectorService</c> ask "every cluster in the installation running
    /// kube-prometheus-stack", and every method on this contract is tenant-scoped. That is the
    /// same blocker as the credential work's remaining 206 — not a missing method but an unanswered
    /// question about who a background sweep acts as. The other 36 are movable now.</para>
    /// </summary>
    /// <para><b>27, and the split is now the same one the credential work reached.</b> Of the 39
    /// before this batch, <b>17 were in a method with a tenant in scope and 22 were not</b> —
    /// measured, because "movable now" had been an estimate. Twelve of the 17 moved here:
    /// <c>StalwartService</c> 7→2, <c>OpenLdapService</c> 4→0, <c>ExternalRouteService</c> 2→0,
    /// <c>StalwartDnsService</c> 1→0.</para>
    ///
    /// <para><b>The gateway resolvers were the structural blocker, not a missing method.</b>
    /// <c>ResolveGateway</c>, <c>ResolveGatewayClass</c>, <c>ResolveIngressClass</c>,
    /// <c>ResolveL4Gateway</c> and the two private spellings took
    /// <c>IEnumerable&lt;ClusterComponent&gt;</c> and read exactly four fields, so any caller
    /// needing a gateway had to hold Catalog's entity — which is why the entity still flows
    /// through seven services in five modules. They now take
    /// <c>EntKube.Contracts.Catalog.IComponentNaming</c>, which both the entity and
    /// <c>InstalledComponent</c> satisfy, so all twenty-odd existing callers compile untouched.
    /// An overload would have duplicated logic that decides a GatewayClass, and a controller
    /// handed the wrong class <em>silently ignores</em> the Gateway.</para>
    ///
    /// <para>⚠️ <b>Decoupling the resolvers was necessary and not sufficient.</b> The four sites
    /// that most obviously wanted it — <c>HeadscaleService</c> and
    /// <c>KubernetesOperationsService</c>, all four the same
    /// <c>Where(c =&gt; c.ClusterId == clusterId)</c> feeding a resolver — have <b>no tenant in
    /// scope</b>, so the contract still cannot serve them. I nearly built this batch around them
    /// before measuring. Only <c>StalwartService.ApplyConfigurationAsync</c>'s list had a tenant
    /// and so actually moved.</para>
    ///
    /// <para><b>23, and this is where the contract work stops being mechanical.</b>
    /// <c>StorageService</c> 6→2 took the last four sites that had a tenant in scope. Of the 23
    /// left, <b>exactly one</b> has a tenant — <c>TailscaleService.GetOperatorsAsync</c>, blocked
    /// for a different reason (see above: its DTO hands the entity to a page that reads
    /// <c>Component.Cluster.Kubeconfig</c> off it). The other 22 have no tenant in scope, so
    /// <c>ICatalogApi</c> cannot serve them whatever is added to it.</para>
    ///
    /// <para>87 → 23 is as far as this ratchet goes without answering who a background sweep acts
    /// as. That is the same wall <see cref="CustodyTriageTests"/> reached at three, and it is one
    /// question, not two: <c>AlertSyncService</c>, <c>ResourceUsageCollectorService</c> and
    /// <c>HeadscaleCertSyncService</c> appear in both counts because a sweep that reaches every
    /// cluster in the installation needs a tenant for the contract <em>and</em> for the
    /// credential.</para>
    ///
    /// <para><c>RegisterMinioTenantAsync</c> matched <c>c.ClusterId == clusterId</c> with no
    /// tenant predicate, and its one caller is a page offering the tenant's own clusters — so,
    /// like <c>StalwartDnsService</c> before it, this was scoping that lived in the caller rather
    /// than a reachable hole. The method itself had no guarantee, since <c>tenantId</c> and
    /// <c>clusterId</c> are independent parameters of a public method; it has one now. Worth
    /// having, not a vulnerability fixed.</para>
    ///
    /// <para><c>IsMinioAvailableAsync</c> and <c>GetCubeFSComponentsAsync</c> keep their predicates
    /// in memory, because the contract's find-by-key takes <em>one</em> key and "minio" and
    /// "minio-operator" are two — the same asymmetry that left the other seven
    /// "is this operator installed" callers alone.</para>
    ///
    /// <para><b>21.</b> <c>HeadscaleCertSyncService</c> 1 → 0 and <c>HeadscaleService</c> 3 → 2,
    /// because the sweep decision (§4.0.1.2) gives a sweep a tenant to ask with — a lookup that
    /// selected cluster ids across the whole installation became the tenant's own installs from
    /// the contract.</para>
    ///
    /// <para><b>⚠️ This constant has now been wrong on main TWICE, from the same cause.</b> Two
    /// open PRs each lowered it, their conflict was resolved by taking one side, and the result
    /// claimed a number the tree did not have — 202 the first time (#171), 23 the second. Both
    /// times the slack half of this ratchet was the only thing that noticed, and both times the
    /// measured combination had been written in the PR body beforehand.</para>
    ///
    /// <para>So the remedy is not better prose on the PR. <b>Do not leave two branches open that
    /// both lower the same baseline.</b> A ratchet constant is a claim about the whole tree, so
    /// two independent decrements have no correct merge — they have to be re-measured, which is
    /// work someone has to notice is needed. Serialise instead: land one, rebase the other. The
    /// author controls that; the merger cannot be expected to.</para>
    ///
    /// <para><b>19.</b> <c>AlertSyncService</c> and <c>ResourceUsageCollectorService</c> — the two
    /// sweeps that were in <em>both</em> this count and the credential one — now loop tenants, so
    /// each asks Catalog for its own clusters running kube-prometheus-stack instead of selecting
    /// every installation that runs it.</para>
    ///
    /// <para><c>AlertSyncService</c> is the clearer win of the two: it loaded the cluster row
    /// <em>in order to read a tenant off it</em>, then filtered routing rules, notification
    /// channels and maintenance windows by that. The row is still loaded — an incident carries the
    /// cluster as a navigation — but scoped by tenant now, so it no longer supplies the thing it
    /// is checked against.</para>
    ///
    /// <para>⚠️ <c>ResourceUsageCollectorService</c> is <b>half converted, deliberately.</b> The
    /// tenant bounds which clusters are collected, but <c>PrometheusService.GetMetricRangeAsync</c>
    /// still takes a cluster id and no tenant — as do all fifteen of its public methods, which is
    /// why its 20 credential sites are the largest remaining block. The sweep is fixed; the call
    /// it makes would still answer about any cluster. <em>(Both halves are done as of #184.)</em></para>
    ///
    /// <para><b>17.</b> The log viewers' backend routing and the headscale Istio fix.</para>
    ///
    /// <para><c>HeadscaleService.EnsureIstioPermissiveAsync</c> was the free one: it already took
    /// a tenant, so its component read was a foreign query with nothing to gain. What made it a
    /// one-line change is #172 — the gateway resolvers take <c>IComponentNaming</c>, so the
    /// contract's DTO goes straight into them with no shape conversion at all.</para>
    ///
    /// <para><c>LogQueryService</c> cost more and is worth the detail. Its seven public methods
    /// now take the asking tenant, which reached four pages, because the <em>routing</em> decision
    /// — native store or Loki — turns on whether EntKube's collector is installed, and that is a
    /// Catalog question. The predicate is kept exactly as it was, asymmetric on purpose: the chart
    /// name is upstream's, the release name and the name are EntKube's own, and matching all three
    /// uniformly is how this kind of lookup goes wrong.</para>
    ///
    /// <para>⚠️ <b>The probe is memoized, and the memo had to be re-keyed.</b> It ran before every
    /// facade call (~5 round-trips per log-panel load), cached on the cluster id — correct while
    /// the probe took nothing else. Asked as a tenant it is a different question: a tenant who may
    /// not see the cluster gets an empty component list and therefore "no collector", and under a
    /// cluster-only key that answer would be handed to the <em>owner</em> next, routing their logs
    /// to a Loki that is not there. <c>LogRoutingTenancyTests</c> asks the same pair of questions
    /// in both orders, which is the only way round that is sensitive to it.</para>
    ///
    /// <para><c>EntKubeTelemetryService</c>'s two reads were attempted and reverted. Both of
    /// <c>GetInClusterIndexerUrlAsync</c>'s own callers have a tenant — but it is also reached
    /// through <c>GetInClusterIngestUrlAsync</c> and <c>FixQuerierIndexerUrlAsync</c>, whose
    /// callers in <c>ComponentLifecycleService</c> take a component id and nothing else. The
    /// cascade ends in a file that holds credential sites, so it belongs with that file's
    /// conversion rather than here. The comment above that query, which already said it could not
    /// be the contract's, now says which caller is the reason.</para>
    ///
    /// <para><b>15.</b> The install orchestrator's post-install hook and the connectivity policy
    /// generator — both picked because the methods holding them read no credential, so neither
    /// could spill into the custody count while that work was in flight elsewhere.</para>
    ///
    /// <para><c>HeadscaleService.EnsureExternalRouteAfterInstallAsync</c> is the better story. It
    /// took a component id and nothing else, read the component <em>with its cluster attached</em>,
    /// and used <c>comp.Cluster.TenantId</c> for the vault lookups and both downstream calls — and
    /// its own comment explained that the resulting check was tautological "because this runs from
    /// the install orchestrator, which addresses components by id and has no caller to speak for".
    /// That was wrong: <c>ComponentInstallOrchestrator.ApplyAsync</c> takes a tenantId and always
    /// had one. The limitation was in the signature, not in the caller — worth remembering the
    /// next time a comment explains why something cannot be fixed.</para>
    ///
    /// <para><c>ConnectivityGraphService.GenerateNetworkPoliciesAsync</c> asks Catalog per target
    /// cluster now. Its <c>ApplyNetworkPoliciesAsync</c> caller <b>derives</b> the app's tenant
    /// rather than taking one, which is deliberate and stated at the call: that method reads
    /// kubeconfigs further down, so a tenant parameter there makes those reads reachable in
    /// <see cref="CustodyTriageTests"/>'s sense and they have to move in the same change. A
    /// cross-row derivation is weaker than what the page above supplies, and saying so beats
    /// quietly implying the whole path is authorized.</para>
    ///
    /// <para><b>14.</b> <c>EntKubeTelemetryService.GetInClusterIndexerUrlAsync</c> — the read
    /// #186 attempted and reverted. It was blocked by its own cascade:
    /// <c>GetInClusterIngestUrlAsync</c> and <c>FixQuerierIndexerUrlAsync</c> are reached from
    /// <c>ComponentLifecycleService</c>'s install path, which took a component id and nothing
    /// else. #193 gave that path a tenant, so the cascade has one now and this read is the
    /// contract's.</para>
    ///
    /// <para>Its sibling <c>ManagementPlaneStillReceivesAsync</c> stays: it is reached from
    /// <c>ClusterRoutedBackends</c>, the telemetry routing layer, which addresses clusters by id
    /// and has no tenant of its own — a different cascade, and the next one.</para>
    ///
    private const int BaselineQueries = 14;

    /// <summary>
    /// Tracked writes to a <c>ClusterComponent</c> from services Catalog does not own. Was 22,
    /// then 3, and now <b>zero</b>.
    ///
    /// <para>This is the number that mattered most, because it is the one the first measurement
    /// got wrong. It looked for <c>.ClusterComponents.Add/Remove/Update</c>, found none, and
    /// concluded the table was read-only outside Catalog — a conclusion that was written into
    /// <c>ICatalogApi</c>'s own documentation as the reason it had no write methods. EF writes
    /// through change tracking, so <c>component.HelmValues = x</c> followed by
    /// <c>SaveChangesAsync</c> is a write with no <c>Update()</c> anywhere near it.</para>
    ///
    /// <para>The last three were <c>StalwartService</c> twice and <c>OpenLdapService</c> once, and
    /// all three are now <see cref="EntKube.Contracts.Catalog.ICatalogApi.SetHelmValuesAsync"/> —
    /// which until this point had no callers at all, so the contract's write half existed only on
    /// paper.</para>
    /// </summary>
    private const int BaselineWrites = 0;

    [Fact]
    public void No_new_service_reaches_past_the_catalog_contract()
    {
        (int queries, int writes) = Measure();

        queries.Should().BeLessThanOrEqualTo(BaselineQueries,
            "a new direct query on Catalog's table is one more caller that cannot be moved off "
            + "the shared database later. Ask ICatalogApi instead");

        writes.Should().BeLessThanOrEqualTo(BaselineWrites,
            "a foreign module mutating a component through change tracking is the write this "
            + "contract exists to express — use MergeHelmValuesAsync or SetHelmValuesAsync");
    }

    [Fact]
    public void The_adoption_baseline_does_not_carry_slack()
    {
        (int queries, int writes) = Measure();

        queries.Should().Be(BaselineQueries,
            $"now {queries} — bank it by lowering the baseline, or the slack lets direct access "
            + "creep back for free");

        writes.Should().Be(BaselineWrites, $"now {writes} writes; lower the baseline to match");
    }

    /// <summary>
    /// Mutable scalar properties on <c>ClusterComponent</c>, read out of the entity itself so a
    /// new property cannot be written by a foreign module without this test noticing.
    /// </summary>
    private static IReadOnlySet<string> MutableProperties(string root)
    {
        string entity = File.ReadAllText(Path.Combine(root, "Data", "ClusterComponent.cs"));

        return Regex.Matches(entity, @"public\s+[\w<>?\[\]]+\s+(\w+)\s*\{\s*get;\s*set;")
            .Select(m => m.Groups[1].Value)
            .ToHashSet(StringComparer.Ordinal);
    }

    /// <summary>Which module owns each service, straight from <c>ModuleServiceMap</c>.</summary>
    private static IReadOnlyDictionary<string, string> ServiceModules(string root)
    {
        string map = File.ReadAllText(Path.Combine(root, "Modules", "ModuleServiceMap.cs"));

        return Regex.Matches(map, @"\[typeof\(([\w\.<>]+)\)\]\s*=\s*Module\.(\w+)")
            .GroupBy(m => m.Groups[1].Value.Split('.')[^1])
            .ToDictionary(g => g.Key, g => g.First().Groups[2].Value, StringComparer.Ordinal);
    }

    private static (int Queries, int Writes) Measure()
    {
        string root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, Web));

        Directory.Exists(root).Should().BeTrue(
            "this test reads the web project's source; if it moved, the test has to follow "
            + "rather than quietly stop checking anything");

        IReadOnlyDictionary<string, string> modules = ServiceModules(root);
        IReadOnlySet<string> properties = MutableProperties(root);

        modules.Should().NotBeEmpty("ModuleServiceMap is how this test knows whose code it is reading");
        properties.Should().NotBeEmpty("ClusterComponent's settable properties are what a write writes");

        int queries = 0, writes = 0;

        foreach (string path in Directory.EnumerateFiles(
                     Path.Combine(root, "Services"), "*.cs", SearchOption.AllDirectories))
        {
            string service = Path.GetFileNameWithoutExtension(path);

            // Services Catalog owns are supposed to use the table; so is anything the map does
            // not place in a module, which is not a service at all.
            if (!modules.TryGetValue(service, out string? owner) || owner == "Catalog") continue;

            string[] lines = File.ReadAllLines(path);
            string body = string.Join('\n', lines);

            // Anything named as a ClusterComponent — what a tracked write would be written
            // through. Deliberately any declaration position, not just a local assigned where it
            // is declared: the first version of this matched `ClusterComponent x = …` and
            // `foreach`, which silently missed every method parameter, and a check that cannot
            // see a write is worse than no check. `List<ClusterComponent> xs` does not match,
            // because the `>` sits where the name would be.
            HashSet<string> held = Regex.Matches(body, @"ClusterComponent[?!]?\s+(\w+)\b")
                .Select(m => m.Groups[1].Value)
                .ToHashSet(StringComparer.Ordinal);

            foreach (string line in lines)
            {
                string trimmed = line.TrimStart();

                // Prose about the credential inflated the custody metric until it was fixed to
                // read code only; the same mistake is not worth making twice.
                if (trimmed.StartsWith("//", StringComparison.Ordinal)
                    || trimmed.StartsWith("*", StringComparison.Ordinal)) continue;

                queries += Regex.Matches(line, @"\.ClusterComponents\b").Count;

                foreach (string name in held)
                {
                    foreach (Match m in Regex.Matches(line, $@"\b{Regex.Escape(name)}\.(\w+)\s*=(?!=)"))
                    {
                        if (properties.Contains(m.Groups[1].Value)) writes++;
                    }
                }
            }
        }

        return (queries, writes);
    }
}
