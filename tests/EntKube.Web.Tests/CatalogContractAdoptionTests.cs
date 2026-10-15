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
    /// <para><b>13.</b> That cascade, and it was not shaped the way it looked. The routing layer
    /// hands the viewers an <c>ILogBackend</c>/<c>ITraceQueryService</c>, so the obvious move was a
    /// <c>tenantId</c> on those interfaces — and it is the wrong one. <b>Both live in
    /// <c>EntKube.Telemetry</c> and are implemented inside the in-cluster node</b>
    /// (<c>HttpLogBackend</c>, <c>FederatedLogBackend</c>, <c>HttpTraceBackend</c>,
    /// <c>FederatedTraceService</c>) and mapped onto its HTTP routes. A node serves one cluster and
    /// authenticates with a token: a tenant means nothing there, so every implementation but the
    /// management plane's would ignore the parameter — and a management-plane authorization change
    /// would have become a change to the node's wire surface, which reaches no cluster until the
    /// chart's appVersion and both catalog pins are bumped and a new image ships.</para>
    ///
    /// <para>So the tenant arrives through <c>ITelemetryBackends.LogsFor(tenantId)</c> /
    /// <c>TracesFor(tenantId)</c> — the same shape as <c>IClusterClientFactory.ForAsync</c>, which
    /// is where every other authorization input in this codebase is supplied. <b>An interface that
    /// is part of a shipped artifact cannot take an authorization parameter</b>; the factory in
    /// front of it can. Worth remembering before the next cascade that ends at an interface.</para>
    ///
    /// <para>Five consumers had a tenant already (<c>LogQueryService</c>,
    /// <c>DashboardPanelCard</c>, <c>RolloutService</c>, and the two hosts of
    /// <c>TraceExplorer</c>), one page had none at all, and the sixth was
    /// <c>TelemetryAlertEvaluator</c> — a sweep, so it now loops tenants per §4.0.1.2 rather than
    /// reading <c>rule.TenantId</c> off the row it had just loaded. That is a tenant to label an
    /// incident with, not a tenant to ask as.</para>
    ///
    /// <summary>
    /// <b>10 — and the three that moved are the ones this file recorded as unservable.</b> The ⚠️
    /// above says of <c>HeadscaleService</c> and <c>KubernetesOperationsService</c>: "all four the
    /// same <c>Where(c =&gt; c.ClusterId == clusterId)</c> feeding a resolver — have <b>no tenant in
    /// scope</b>, so the contract still cannot serve them", and congratulates itself for measuring
    /// before building a batch around them. That was true when written and <b>stopped being true in
    /// #198</b>, which gave this file's methods their tenants:
    /// <c>ApplyDeploymentRouteAsync</c>, <c>ApplyL4RouteAsync</c> and
    /// <c>DeleteL4RouteFromClusterAsync</c> all take one now.
    ///
    /// <para>So this is the fourth expired exemption in this programme, and the first whose expiry
    /// was <em>caused</em> by other work in it rather than by never having been checked. The
    /// previous three were claims about a caller written without opening it; this one was correct
    /// and then rotted. A dated exemption would not have helped — nothing about #198 announced that
    /// it had unblocked a different ratchet. What catches this is re-measuring the blocked set when
    /// a related conversion lands, which is now the habit rather than a note.</para>
    ///
    /// <para>The third read was in a <c>private static</c> helper with no tenant to pass. It takes
    /// the component list as a parameter now instead of querying for it — both its callers have a
    /// tenant, and one of them had already asked Catalog for exactly that list.</para>
    ///
    /// <para><b>What the remaining 10 are</b>, each opened rather than assumed:
    /// <c>StalwartService</c> 2 and <c>StorageService</c> 1 are <c>static</c> or public methods
    /// taking <c>(db, config)</c> or an id with no tenant; <c>StorageLinkClientFactory</c> 2 and
    /// <c>StorageService</c> 1 sit on credential-resolution paths, so they belong with that file's
    /// custody conversion; <c>SupportMailboxService</c> 1 reads the tenant off the mailbox row;
    /// <c>TailscaleService</c> 1 is still blocked by the DTO its Razor page reaches through;
    /// <c>TelemetryNodeClient</c> 1 <em>selects the kubeconfig in the same projection</em>, so
    /// converting it moves the custody baseline and belongs there.</para>
    ///
    /// <para>⚠️ <c>VeleroService</c> 1 is the one blocked by the <em>contract</em> rather than by a
    /// caller, and its own comment is accurate — verified, not repeated: it filters on
    /// <c>Status</c> and takes the most recently <b>created</b> match, and
    /// <c>InstalledComponent</c> carries <c>InstalledAt</c>, not <c>CreatedAt</c>. Converting it
    /// would change which component is picked when a cluster has two. That is an argument for a
    /// field, not for a conversion, and nothing else asks for it yet.</para>
    ///
    /// <para><b>6 — and the four that moved were the four this file called "no tenant to pass".</b>
    /// All four had one; it was simply never asked for. <c>StalwartService</c>'s two were in a
    /// <c>private static</c> helper taking <c>(db, config)</c>, and the config row it was handed
    /// carries <c>TenantId</c> — so the helper stopped being static and asks Catalog as that tenant.
    /// <c>SupportMailboxService</c>'s read of a mail server was a hand-written predicate against the
    /// mailbox's own tenant, which is <c>GetComponentAsync</c> exactly. That is the third time in
    /// this file that "cannot be served" turned out to mean "nobody opened the caller".</para>
    ///
    /// <para>⚠️ <c>StorageService.BindStorageToComponentAsync</c> was the one that was not merely
    /// tidying. It read <c>FindAsync(componentId)</c> with <b>no tenant at all</b>, and the
    /// component's <c>Namespace</c> is what decides where the storage link's credentials are
    /// projected as a Kubernetes Secret — so a component id from any tenant in the installation
    /// resolved and chose the destination. The remedy is an ordering change as much as a
    /// conversion: the link is loaded <em>first</em>, and its tenant is who the component is then
    /// asked for. <c>StorageServiceTests</c> covers both halves, because a scoping that refused
    /// everything would pass the negative case on its own.</para>
    ///
    /// <para><b>What the remaining 6 are</b>, and four of them are one thing. <c>StorageService</c>
    /// 1, <c>StorageLinkClientFactory</c> 2 and <c>TelemetryNodeClient</c> 1 each resolve a
    /// <em>kubeconfig in the same expression as the component</em> — two of them project
    /// <c>c.Cluster</c> precisely so the materialization interceptor decrypts it, and the node
    /// client selects <c>c.Cluster.Kubeconfig</c> into its projection. The contract carries no
    /// credential by design, so converting any of them means moving the credential to
    /// <c>IClusterClient</c> in the same change, and that is custody work with its own baseline.
    /// <c>TailscaleService</c> 1 is still the DTO: its record hands the entity to a Razor page that
    /// reads <c>Component.Cluster.Kubeconfig</c> off it. <c>VeleroService</c> 1 remains the only
    /// one blocked by the contract itself, for the <c>CreatedAt</c> reason above.</para>
    ///
    /// <para>So 87 → 6, and every one of the six is now waiting on the credential seam rather than
    /// on a tenant. The two ratchets have converged: this one cannot move again until
    /// <see cref="ClusterCredentialCustodyTests"/> does.</para>
    ///
    /// <para>⚠️⚠️ <b>That paragraph was wrong, and the measurement was why. The real number was
    /// 10.</b> This test resolved a file's owning module by looking up
    /// <c>Path.GetFileNameWithoutExtension(path)</c> in a dictionary keyed by <em>type</em> names,
    /// and skipped anything it could not find — so every file whose name differs from the class
    /// inside it was silently exempt:</para>
    ///
    /// <list type="bullet">
    ///   <item><c>OperationsAdvisor.cs</c> declares <c>OperationsAdvisorService</c> — 2 queries,
    ///     one of which selected KEDA installs across the <b>whole installation</b> with no tenant
    ///     predicate at all.</item>
    ///   <item><c>Mail/SmtpSettings.cs</c> declares <c>SmtpSettingsResolver</c> — 1 query.</item>
    ///   <item><c>PublicApi/PublicApiEndpoints.cs</c> declares nothing the map holds, because it is
    ///     not a service — 1 query, in the <b>public REST API</b>.</item>
    ///   <item><c>BackupService.cs</c> — 3 queries, and these are a <em>real</em> exemption: the
    ///     backup bundle is a whole-installation export and <c>ModuleServiceMap.PlatformServices</c>
    ///     says so. It was being exempted by accident rather than by that rule.</item>
    /// </list>
    ///
    /// <para><b>Ownership now comes from the types a file declares</b> (<see cref="DeclaredTypes"/>),
    /// and <c>PlatformServices</c> is read for the one exemption that is deliberate. A file with no
    /// mapped type is now <em>counted</em> rather than skipped: "not a service" is no reason to let
    /// the public API reach past the contract, and that route is the one the CLI, the MCP server and
    /// every future BFF go through.</para>
    ///
    /// <para><b>This is the third time a name-shaped measurement in this programme has been wrong,
    /// and the most expensive.</b> The number 6 had already been copied into this file's own prose,
    /// into <c>docs/decomposition.md</c> and into the project notes as the reason the ratchet was
    /// blocked — "every one of the six is waiting on the credential seam" was an argument built on
    /// a count that could not see four of the ten. A measurement that feeds documentation has to be
    /// checked against something independent; this one was, with a throwaway script that walked the
    /// same files and disagreed.</para>
    ///
    /// <para><b>Back to 6, and now it means 6.</b> All four newly-visible sites converted, because
    /// all four had a tenant in scope: <c>OperationsAdvisorService</c> 2 → 0 (the KEDA sweep and the
    /// Kyverno correlated subquery, the latter collapsing entirely because the DTO already carries
    /// the cluster's <c>EnvironmentId</c>), <c>SmtpSettingsResolver</c> 1 → 0, and the public API's
    /// component listing 1 → 0.</para>
    ///
    /// <para>The public API's conversion carried one risk worth recording: it serialises
    /// <c>Status.ToString()</c>, so the published JSON now comes from the <em>contract's</em> copy
    /// of <c>ComponentStatus</c> rather than the entity's. The two agree member for member today;
    /// <c>ApiComponentStatusTests</c> is what keeps them agreeing, because a wire format that
    /// changes when someone edits an enum is noticed only by the caller. Its 404 check also stays:
    /// <c>GetComponentsForClusterAsync</c> is tenant-first and answers an empty list for a cluster
    /// that is not yours, and an empty list is a 200 — "does not exist" and "has no components" are
    /// different answers.</para>
    /// </summary>
    private const int BaselineQueries = 6;

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

    /// <summary>
    /// The cross-cutting wiring <c>ModuleServiceMap.PlatformServices</c> names — services that
    /// belong to no business module on purpose.
    ///
    /// <para>Read because one of them, <c>BackupService</c>, legitimately touches every table in
    /// the product: the backup bundle is a whole-installation export and a tenant-scoped contract
    /// cannot express it. That exemption was previously granted by accident — the file simply did
    /// not resolve to a module — and an exemption nobody states is indistinguishable from a
    /// measurement bug, which is what the rest of this method turned out to be.</para>
    /// </summary>
    private static IReadOnlySet<string> PlatformServices(string root)
    {
        string map = File.ReadAllText(Path.Combine(root, "Modules", "ModuleServiceMap.cs"));

        int start = map.IndexOf("PlatformServices", StringComparison.Ordinal);
        start.Should().BeGreaterThan(0, "the platform-wiring set is how this test knows which "
            + "services are deliberately module-less");

        return Regex.Matches(map[start..], @"typeof\(([\w\.<>]+)\)")
            .Select(m => m.Groups[1].Value.Split('.')[^1])
            .ToHashSet(StringComparer.Ordinal);
    }

    /// <summary>
    /// The type names a file declares, which is what <c>ModuleServiceMap</c> is keyed by.
    ///
    /// <para>⚠️ <b>This is the fix for the bug that made this whole ratchet under-count.</b> The
    /// previous version looked up <c>Path.GetFileNameWithoutExtension(path)</c> in a map keyed by
    /// <em>type</em> names, and silently skipped every file whose name differs from its class:
    /// <c>OperationsAdvisor.cs</c> declares <c>OperationsAdvisorService</c> and
    /// <c>Mail/SmtpSettings.cs</c> declares <c>SmtpSettingsResolver</c>, so three real foreign
    /// queries were exempted by a filename mismatch and the headline number was wrong by two
    /// thirds of its remaining distance. It had already been copied into this file's prose and
    /// into docs/decomposition.md as rationale.</para>
    /// </summary>
    private static IReadOnlyList<string> DeclaredTypes(string body) =>
        [.. Regex.Matches(body, @"\b(?:class|record|struct|interface)\s+(\w+)")
            .Select(m => m.Groups[1].Value)];

    private static (int Queries, int Writes) Measure()
    {
        string root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, Web));

        Directory.Exists(root).Should().BeTrue(
            "this test reads the web project's source; if it moved, the test has to follow "
            + "rather than quietly stop checking anything");

        IReadOnlyDictionary<string, string> modules = ServiceModules(root);
        IReadOnlySet<string> properties = MutableProperties(root);
        IReadOnlySet<string> platform = PlatformServices(root);

        modules.Should().NotBeEmpty("ModuleServiceMap is how this test knows whose code it is reading");
        properties.Should().NotBeEmpty("ClusterComponent's settable properties are what a write writes");

        int queries = 0, writes = 0;

        foreach (string path in Directory.EnumerateFiles(
                     Path.Combine(root, "Services"), "*.cs", SearchOption.AllDirectories))
        {
            string[] lines = File.ReadAllLines(path);
            string body = string.Join('\n', lines);

            // Ownership is resolved from the types the file DECLARES, not from its name — see
            // DeclaredTypes for the bug that caused. A file with no mapped type at all is still
            // read: PublicApi/PublicApiEndpoints.cs is not a service and never will be, but it is
            // the public REST surface the CLI, the MCP server and every future BFF go through, so
            // "not a service" is no reason to let it reach past the contract.
            List<string> declared = [.. DeclaredTypes(body)];

            if (declared.Any(platform.Contains)) continue;

            string? owner = declared
                .Select(d => modules.TryGetValue(d, out string? m) ? m : null)
                .FirstOrDefault(m => m is not null);

            // Catalog's own services are supposed to use the table.
            if (owner == "Catalog") continue;

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
