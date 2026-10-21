using System.Reflection;
using System.Text.RegularExpressions;
using EntKube.Contracts.Advisor;
using EntKube.Web.Data;
using EntKube.Web.Modules;
using FluentAssertions;
using Module = EntKube.Web.Modules.Module;

namespace EntKube.Web.Tests;

/// <summary>
/// The contracts stay contracts, and the god context keeps shrinking.
///
/// <para>Phase 0 gave every table and every service an owning module, and gave each module
/// a context that exposes only its own tables. None of that matters while the services
/// still take <see cref="ApplicationDbContext"/>, which can see everything — the boundary
/// exists but nothing is standing behind it yet. The ratchet below is how that gets walked
/// down deliberately instead of hopefully.</para>
/// </summary>
public class ModuleContractTests
{
    /// <summary>
    /// <see cref="EntKube.Contracts"/> must never be able to see the web application.
    ///
    /// <para>If it could, the contracts would start returning EF entities and quietly
    /// become a second name for the database — which is exactly the coupling the
    /// decomposition is trying to undo. It is also referenced by things that cannot take
    /// the whole web application with them: the CLI, the MCP server, a generated HTTP
    /// client, and eventually a separate deployable.</para>
    /// </summary>
    [Fact]
    public void The_contracts_project_cannot_see_the_web_application()
    {
        IEnumerable<string> referenced = typeof(IAdvisorApi).Assembly
            .GetReferencedAssemblies()
            .Select(a => a.Name ?? "")
            .Where(n => n.StartsWith("EntKube", StringComparison.Ordinal));

        string.Join(", ", referenced).Should().BeEmpty(
            "EntKube.Contracts must depend on no EntKube assembly at all — see its README");
    }

    /// <summary>
    /// Nothing a contract hands out may be a type from the web application, which in
    /// practice means an EF entity. An entity carries navigation properties and a change
    /// tracker; handing one across a boundary hands over a live route into the database.
    /// </summary>
    [Fact]
    public void No_contract_exposes_a_type_from_the_web_application()
    {
        List<string> leaks = [];

        foreach (Type contract in typeof(IAdvisorApi).Assembly.GetTypes().Where(t => t.IsInterface))
        {
            foreach (MethodInfo method in contract.GetMethods())
            {
                IEnumerable<Type> touched = method.GetParameters().Select(p => p.ParameterType)
                    .Append(method.ReturnType)
                    .SelectMany(t => t.IsGenericType ? t.GetGenericArguments().Append(t) : [t]);

                foreach (Type t in touched.Where(t => t.Namespace?.StartsWith("EntKube.Web", StringComparison.Ordinal) == true))
                {
                    leaks.Add($"{contract.Name}.{method.Name} -> {t.Name}");
                }
            }
        }

        string.Join("; ", leaks).Should().BeEmpty("a contract must carry its own types");
    }

    private const string Src = "../../../../../src";

    /// <summary>
    /// Mapped services whose source pulls the all-seeing context <em>out of a scope</em> rather
    /// than taking it as a constructor parameter.
    ///
    /// <para>⚠️ <b>This is the half of the metric reflection cannot see, and it was missing.</b>
    /// A singleton cannot take a scoped <see cref="ApplicationDbContext"/> in its constructor —
    /// that is a captive dependency and the container refuses it — so every
    /// <c>BackgroundService</c> in the product takes <c>IServiceScopeFactory</c> and resolves
    /// <c>IDbContextFactory&lt;ApplicationDbContext&gt;</c> inside its loop. A constructor-only
    /// count therefore does not under-report the sweeps a little: it excludes <em>all</em> of
    /// them, by construction. Twelve files resolve it that way and <b>not one</b> of them was
    /// counted.</para>
    ///
    /// <para>That is the broadest reach in the product going unmeasured. A sweep runs unattended,
    /// crosses every tenant, and is precisely the code §4.0 governs — so the one category the
    /// ratchet silently exempted is the one with the most to gain from a narrower context.</para>
    ///
    /// <para>The reason is the same one that has now bitten this programme six times, and it is
    /// worth naming rather than fixing quietly: <b>the metric listed one spelling of the thing it
    /// names.</b> DbSet versus navigation versus <c>Set&lt;T&gt;()</c>, a wrapper call, a
    /// filename-keyed map, a log string — and here, constructor injection versus scope
    /// resolution. Each time the fix was to make the measurement <em>derive</em> what it looks
    /// for. This scan reads the source for both spellings; it does not hold a list of sweeps.</para>
    /// </summary>
    private static IReadOnlySet<string> ScopeResolvers(IReadOnlySet<string> mapped)
    {
        // Two categories legitimately reach the context while belonging to no module, and both
        // are declared rather than inferred. PlatformServices is the product's own list of
        // cross-cutting wiring — presence, toasts, the backup bundle, kubeconfig materialisation
        // — and Program.cs is the composition root, which wires everything and owns nothing.
        // Anything else that reaches the context and has no module is a gap, not an exemption.
        HashSet<string> platform = [.. ModuleMap.PlatformServices.Select(t => t.Name)];

        string root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, Src));

        Directory.Exists(root).Should().BeTrue(
            "this test reads the source of every project under src/, not just the web project — "
            + "the segment services live in EntKube.Telemetry and are mapped services too");

        HashSet<string> found = [];

        foreach (string path in Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories))
        {
            if (path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                || path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            {
                continue;
            }

            string body = File.ReadAllText(path);

            // Comments first. A header paragraph saying a service NO LONGER resolves the context
            // out of a scope contains the call it is disclaiming, and LokiService's does almost
            // exactly that — the custody ratchet already shipped a count whose only match was a
            // log message, and that is the same mistake one layer down.
            string code = string.Join('\n', body.Split('\n')
                .Select(line => Regex.Replace(line, @"//.*$", string.Empty)));

            // Both ways a scope hands one over: the factory (what every sweep uses) and the
            // context itself. Derived from the resolution call, not from a list of service names.
            if (!code.Contains("Service<IDbContextFactory<ApplicationDbContext>>", StringComparison.Ordinal)
                && !code.Contains("Service<ApplicationDbContext>", StringComparison.Ordinal))
            {
                continue;
            }

            List<string> allDeclared =
            [
                .. Regex.Matches(code, @"\b(?:class|record)\s+(\w+)")
                    .Select(m => m.Groups[1].Value)
                    .Distinct()
            ];

            List<string> declared = [.. allDeclared.Where(mapped.Contains)];

            // ⚠️ A file that reaches the all-seeing context and owns NO mapped service is
            // invisible to every per-module count in this file — there is no module to count it
            // against, so it is not "zero", it is unmeasured. This used to be a silent skip, and
            // it hid two: KubeconfigResolver, registered in Program.cs where
            // ModuleCompositionTests cannot see it, and TelemetrySegmentBlobStore, which takes a
            // tenant id in its constructor and so is never DI-registered at all. Neither could be
            // caught by a registration check; both are caught by this one.
            if (Path.GetFileName(path) != "Program.cs" && !allDeclared.Any(platform.Contains))
            {
                declared.Should().NotBeEmpty(
                    $"{Path.GetFileName(path)} resolves the all-seeing context out of a scope and "
                    + $"declares no service in ModuleServiceMap (it declares: "
                    + $"{string.Join(", ", allDeclared)}). Give the type an owning module, or add "
                    + "it to ModuleMap.PlatformServices if it really is cross-cutting wiring — an "
                    + "unmapped service is counted by nothing, which reads as compliance");
            }

            declared.Count.Should().BeLessThanOrEqualTo(1,
                $"{Path.GetFileName(path)} resolves the all-seeing context out of a scope and "
                + "declares more than one mapped service, so this scan cannot say which of them "
                + "does it. Every such file declares exactly one today. Split the file, or teach "
                + "this test to attribute the call to its enclosing type — do not let it guess");

            found.UnionWith(declared);
        }

        return found;
    }

    /// <summary>Services still reaching the all-seeing context, by module — either spelling.</summary>
    private static Dictionary<Module, int> GodContextUsers()
    {
        HashSet<string> mapped = [.. ModuleMap.Services.Keys.Select(t => t.Name)];
        IReadOnlySet<string> fromScope = ScopeResolvers(mapped);

        fromScope.Should().NotBeEmpty(
            "the scope spelling is the half of this metric reflection cannot see; an empty "
            + "result means this scan stopped working, not that the product came clean");

        Dictionary<Module, int> counts = [];

        foreach ((Type service, Module module) in ModuleMap.Services)
        {
            bool byConstructor = service.GetConstructors().Any(c => c.GetParameters().Any(p =>
                p.ParameterType == typeof(ApplicationDbContext)
                || (p.ParameterType.IsGenericType
                    && p.ParameterType.GetGenericArguments().Length == 1
                    && p.ParameterType.GetGenericArguments()[0] == typeof(ApplicationDbContext))));

            if (byConstructor || fromScope.Contains(service.Name))
            {
                counts[module] = counts.GetValueOrDefault(module) + 1;
            }
        }

        return counts;
    }

    /// <summary>
    /// How many services each module still routes through <see cref="ApplicationDbContext"/>.
    /// <b>60 of 176</b>, down from 117 when the first module contract was written.
    ///
    /// <para>The 58 that came off needed no contract at all. Two kinds: services touching only
    /// their own module's tables, and services whose only reach outside it was a read of one of
    /// the four hubs — which every module context already exposes read-only. Neither is a
    /// behaviour change, because the model is identical whichever context you hold.</para>
    ///
    /// <para><b>It went up by one here, on purpose.</b> Reassigning the service bindings to
    /// DataServices showed that <c>PortalServiceScopeService</c> and
    /// <c>DeploymentImportService</c> read them — so they were never own-module-only, and the
    /// earlier conversion of those two had been wrong on a boundary that was itself wrong.
    /// They are back on the shared context until there is a bindings contract to read
    /// instead. A ratchet that only ever moves one way would have hidden that.</para>
    ///
    /// <para><b>Secrets is at zero</b> — the first module entirely off the all-seeing context.
    /// What remains elsewhere is the harder half: services that genuinely read another module's
    /// non-hub tables and so need its contract to exist first. <c>DataServices</c> (13) has not
    /// moved at all for exactly that reason — every one of its services reaches into Fleet,
    /// Catalog or Delivery.</para>
    ///
    /// <para>The measurement that names the next contracts: of the remaining cross-module
    /// reads, <c>ClusterComponent</c> (Catalog) is read by 36 services and <c>AppDeployment</c>
    /// (Delivery) by 22. Two contracts would unblock most of what is left.</para>
    ///
    /// <para><b>Cost is at zero</b>, the second module entirely off the all-seeing context — and
    /// the first consumer <c>IDeliveryApi</c> has ever had. That contract was written, implemented
    /// over Delivery's own context and registered in DI, and <em>nothing called it</em>, which is
    /// the exact failure this file exists to measure. <c>CostReportService</c> held the shared
    /// context for one method with three queries: clusters and rates are Cost's own (or a hub it
    /// already had read-only), and the third was "which app, in which environment, deployed into
    /// this namespace" — Delivery's question. Asked through the contract, nothing is left to hold a
    /// context for.</para>
    ///
    /// <para><b>Adopting it needed a field added to the DTO</b>, and that is worth recording as a
    /// pattern rather than an accident: <c>DeploymentSummary</c> carried <c>EnvironmentId</c> but
    /// not the environment's <em>name</em>, which the report groups by, so the only way to adopt
    /// the contract would have been to keep a context open for <c>Environments</c>. Same lesson as
    /// <c>InstalledComponent.Configuration</c>: a contract missing the one field its callers read
    /// cannot be adopted by them. <c>ModuleApiTests</c> now asserts the new field, because a
    /// projection nothing asserts is one that silently stops being projected.</para>
    ///
    /// <para><b>What the measurement says about the next ones</b>, having opened each rather than
    /// guessed: <c>PrometheusService</c> (Telemetry) reads exactly one foreign table, but two of
    /// its three reads also need a cluster's components and whether a cluster has a credential
    /// stored at all — so it needs <c>ICatalogApi</c> and a new <c>IFleetApi</c> field, not just
    /// this contract. <c>AppL4RouteService</c> (Connectivity) is one deployment read away, except
    /// that it resolves the Istio gateway from <c>deployment.Cluster.Components</c> and its method
    /// takes no tenant, so the cascade reaches its pages. <c>CertificateDistributionService</c>
    /// (Catalog) is blocked on one <c>VaultSecrets</c> read with no Secrets contract to ask, and
    /// <c>DriftAdoptionService</c> <em>writes</em> Delivery's tables, which a read-only contract
    /// cannot serve. None of those four is a missing method on <c>IDeliveryApi</c>.</para>
    ///
    /// <para><b>Telemetry 10 → 9</b>, and it gave <c>IFleetApi</c>'s sibling its second consumer
    /// rather than needing anything new. <c>PrometheusService</c> held the all-seeing context for
    /// three reads of <c>AppDeployment</c> — one deployment by id, and an app's or a customer's
    /// deployments grouped by cluster — all three now <c>IDeliveryApi</c>.</para>
    ///
    /// <para><b>The field I expected to have to add to <c>IFleetApi</c> was not needed</b>, and the
    /// reason is worth keeping. Two of those reads filtered on
    /// <c>d.Cluster.KubeconfigSecretId != null</c>, which looks like it needs the contract to admit
    /// whether a credential is stored. It does not: <c>IClusterClientFactory.ForAsync</c> already
    /// answers that question by refusing, and this service already had a private funnel —
    /// <c>ResolveReachableAsync</c> — that resolved the cluster through the seam and its Prometheus
    /// through Catalog. The two aggregate methods were each doing half of what that funnel does, so
    /// the conversion moved the lookup into one place instead of adding contract surface. The
    /// general form: before widening a contract, check whether the seam already refuses.</para>
    ///
    /// <para><b>Telemetry 11 → 10</b>, and it came for free: <c>LogQueryService</c> held the
    /// all-seeing context for one read — "is EntKube's collector installed on this cluster" —
    /// which is a Catalog question. Asking <c>ICatalogApi</c> instead left it with no reason to
    /// hold a context at all. That is the shape the paragraph above predicts: the Catalog
    /// contract is what takes services off this list, one read at a time.</para>
    ///
    /// <para><b>Telemetry 9 → 6, and it took the fifth module contract.</b> Three services came off
    /// at once — <c>MimirService</c>, <c>TempoService</c> and <c>EntKubeTelemetryService</c> — and
    /// they were held by the <em>same</em> query, spelled out by hand in each:
    /// <c>StorageLinks.FirstOrDefaultAsync(s =&gt; s.Id == storageLinkId &amp;&amp; s.TenantId ==
    /// tenantId)</c>, to turn a bucket into object-store chart values. Each read exactly three
    /// fields off it (region, bucket, endpoint). <c>StorageLink</c> belongs to DataServices, which
    /// had no contract, so <c>IDataServicesApi</c> is that one method and nothing else.</para>
    ///
    /// <para>It starts with <b>four</b> callers rather than none, which is the whole of what #138
    /// got wrong: the four earlier contracts were written as complete-looking surfaces first and
    /// adopted later, two of them not at all for months. This one was written from the callers
    /// backwards, and the method list is literally what they asked for — no list method, no
    /// find-by-name, because nothing outside DataServices does either.</para>
    ///
    /// <para><c>StorageLink</c> also makes the point that <c>ClusterSummary</c> had to make the
    /// hard way: the DTO carries the <em>whole</em> row, because a storage link's access keys live
    /// in the vault and not on the entity. That is a fact about the schema, not a property of
    /// contracts, so <c>ModuleApiTests</c> asserts it — if a key ever lands on the row, the test
    /// says so instead of the contract quietly handing it out.</para>
    ///
    /// <para>⚠️ <c>LokiService</c> is the fourth caller and did <b>not</b> come off the list. Its
    /// storage read converted like the others, but <c>ResolveLokiInfoAsync</c> loads the cluster
    /// <em>with its components</em> and reads <c>cluster.Kubeconfig</c> off the row — a credential
    /// site, so it belongs to the custody conversion. It is worth being explicit that converting
    /// one of a service's two reasons for holding the context buys nothing on this ratchet, and
    /// the comment now sits on its constructor so the next reader does not have to find out.</para>
    ///
    /// <para><c>EntKubeTelemetryService.ConfigureIdentityAsync</c> was also <b>creating a context
    /// it never used</b> — <c>await using</c>, then not one query. Nothing failed, because an EF
    /// context opens no connection until it is asked something; it is only visible as a count on
    /// this ratchet, which is a fair argument for counting.</para>
    /// <para><b>Telemetry 6 → 5.</b> <c>TelemetryNodeClient</c> came off without being aimed at: it
    /// held the shared context to read its component <em>and the cluster's kubeconfig in the same
    /// projection</em>, and once the component came from <c>ICatalogApi</c> and the request went
    /// through the seam's new <c>ProxyServiceAsync</c>, nothing was left to hold a context for. One
    /// file moved three ratchets — this one, custody and the Catalog contract — which is what "the
    /// two ratchets have converged" was predicting.</para>
    /// <para><b>Telemetry 5 → 4.</b> <c>LokiService</c>, and its own header comment had already
    /// worked out why it could not move alone: <c>ResolveLokiInfoAsync</c> "loads the cluster WITH
    /// its components and reads cluster.Kubeconfig off the row", so converting the storage read on
    /// its own "would otherwise look like this service had been dealt with". The component list
    /// came from <c>ICatalogApi</c> and the credential from the seam in one change, and the
    /// <c>IDbContextFactory</c> parameter went with them — the second service (after
    /// <c>TelemetryNodeClient</c>) whose whole claim on the shared context was one foreign read
    /// plus a credential.</para>
    ///
    /// <para>⚠️ It also <b>moved nothing on <see cref="CatalogContractAdoptionTests"/></b>, and
    /// that is a gap in <em>that</em> metric rather than a property of this file: it read Catalog's
    /// table through <c>Include(c =&gt; c.Components)</c> on the cluster row, and the foreign-read
    /// count matches <c>.ClusterComponents</c>. A navigation read is the same coupling by a
    /// different spelling.</para>
    ///
    /// <para><b>Telemetry 4 → 3</b>, and the question that moved it was being asked four times.
    /// <c>MaintenanceWindow</c> is <em>Fleet's</em> entity, and four services outside Fleet read it
    /// directly — Telemetry's alert sync, its incident dispatcher, its rule evaluator and its
    /// incident service — three of them with the identical predicate, including the
    /// <c>ClusterId == null</c> clause that makes a window tenant-wide. A table read in four places
    /// with one meaning is the shape that belongs behind a method, so <c>IFleetApi</c> gained
    /// <c>IsInMaintenanceAsync</c> and (for the sweep that tests many clusters against one instant)
    /// <c>GetActiveMaintenanceWindowsAsync</c>. <c>IncidentDispatcher</c>'s only other table is
    /// Telemetry's own <c>NotificationChannels</c>, so it came off the shared context with
    /// them.</para>
    ///
    /// <para>⚠️ The evaluator's version had to change shape, not just source. It loaded
    /// <em>every</em> tenant's active windows before its tenant loop and filtered afterwards —
    /// not a leak, but the flattening §4.0.1.2 warns about, where a sweep establishes a tenant and
    /// then consults a structure built across all of them. It asks per tenant inside the loop now,
    /// after the enabled-rules check so a tenant with nothing to evaluate costs no query. That is
    /// one query per active tenant instead of one in total, and worth saying out loud rather than
    /// hiding in a diff.</para>
    ///
    /// <para>⚠️ The first draft of <c>MaintenanceWindowSummary</c> carried a cluster name and a
    /// description <b>for nobody</b> — the three suppression callers need only the answer and the
    /// evaluator needs only the cluster id. It carries four fields because four are read; the page
    /// that renders windows still reads the entity and brings what it displays when it moves. Same
    /// rule that refused <c>DeploymentSummary.CustomerName</c>.</para>
    ///
    /// <para><b>Mail 2 → 1.</b> <c>StalwartDnsService</c> touches <c>StalwartMailDomains</c> and
    /// <c>StalwartComponentConfigs</c>, both Mail's own, and nothing else — the only service in
    /// four modules whose table census was already clean. The swap was the context type and one
    /// helper signature. <c>StalwartService</c> is the one left: six foreign read tables, six
    /// signatures, and a <c>Set&lt;VaultSecret&gt;()</c> that would compile under
    /// <c>MailDbContext</c> and throw at runtime, because <c>VaultSecret</c> is not in
    /// <c>MailModel</c> — the <c>Set&lt;T&gt;()</c> hole again, failing late instead of
    /// silently.</para>
    ///
    /// <para>⚠️ <b>The Delivery paragraph below was restored, not written: the merge of its PR kept
    /// the constant and dropped the prose.</b> That is the second time a merge has done this — the
    /// first dropped a whole section of <c>docs/decomposition.md</c> explaining an ungated delete.
    /// The failure is worse than a wrong number, because a number that is wrong fails a test and a
    /// paragraph that is missing fails nothing. When two branches both edit one doc-comment block,
    /// check the prose survived, not just the figure.</para>
    ///
    /// <para><b>Delivery 6 → 3</b>, and all three were already eligible — nobody had looked.
    /// <c>KedaScalerService</c> and <c>KyvernoPolicyService</c> touch only Delivery's own tables
    /// plus the read-only cluster hub, so the swap was the context type and six private helper
    /// signatures that named <c>ApplicationDbContext</c> explicitly. ⚠️ Those helpers are the part
    /// a table census misses: every table was eligible and the file still would not compile, which
    /// is worth knowing before trusting any measurement of "could swap today".</para>
    ///
    /// <para><c>GitOperationsService</c> took the context and <b>never used it</b> — the compiler
    /// had been saying so for some time (<c>CS9113: Parameter 'dbFactory' is unread</c>). Deleting
    /// the parameter moved this ratchet. A service counted as holding the god context while not
    /// holding a database at all is the cheapest possible entry on this list, and it sat there
    /// because the count was read and the warning was not.</para>
    ///
    /// <para>The three that remain each touch another module's table in a way no <em>read</em>
    /// contract removes: <c>AppGovernanceService</c> does CRUD on Connectivity's
    /// <c>AppNetworkPolicies</c> and reads five DataServices tables for its picker lists;
    /// <c>PortalServiceScopeService</c> reads three binding tables, all set-valued restatements of
    /// methods DataServices already owns per deployment; and <c>DeploymentImportService</c> inserts
    /// a <c>DatabaseBinding</c> with <c>SyncEnabled = false</c>, which the owner-side twin cannot
    /// express because it defaults that to true.</para>
    ///
    /// <para><b>Connectivity 7 → 4</b>, and all three went off the same way the earlier ones did:
    /// their Catalog reads moved to <c>ICatalogApi</c> and their credential to the seam first, and
    /// then there was nothing left to hold the shared context for. <c>VpnService</c> touches only
    /// <c>VpnTunnels</c>/<c>VpnLocalEndpoints</c>/<c>VpnRemoteEndpoints</c>;
    /// <c>ExternalRouteService</c> only <c>ExternalRoutes</c>, <c>AppRoutes</c> and
    /// <c>ExternalRouteHealthHistories</c> — all its module's own.</para>
    ///
    /// <para>⚠️ <c>HeadscaleService</c> is the one worth reading. What kept it on the all-seeing
    /// context was <b>five identical blocks that loaded the cluster row to read back
    /// <c>cluster.TenantId</c></b> — the tenant the caller had already passed in, and which the
    /// tenant-first <c>GetComponentsForClusterAsync</c> call two lines above has already proved the
    /// cluster belongs to. So the query was both redundant and a derived tenant where an input was
    /// in scope. Deleting the five removed the module's last foreign table, five round trips, and
    /// the pattern <c>VacuousTenantCheckTests</c> exists to stop — in a shape that test cannot see,
    /// because the derived tenant was passed to the <em>vault</em> rather than to
    /// <c>ForAsync</c>.</para>
    ///
    /// <para>The three that remain are blocked on contracts rather than on tidying:
    /// <c>ConnectivityGraphService</c> reads <c>AppDeployments</c>, <c>AppEnvironments</c>,
    /// <c>MessagingBindings</c> and <c>StorageBindings</c> — three other modules;
    /// <c>AppRouteService</c> and <c>AppL4RouteService</c> read <c>AppDeployments</c> and
    /// <c>AppDeploymentRoutes</c>, which <c>ConnectivityDbContext</c> does not expose.</para>
    ///
    /// <para><b>DataServices 12 → 8, and it needed no contract at all.</b> This module was recorded
    /// as "has not moved at all for exactly that reason — every one of its services reaches into
    /// Fleet, Catalog or Delivery". That was true of the module and <em>false of four of its
    /// services</em>: <c>DatabaseService</c>, <c>DockerRegistryService</c>, <c>OpenLdapService</c>
    /// and <c>HarborService</c> read only their own tables plus a hub, and
    /// <c>DataServicesDbContext</c> has exposed <c>App</c>, <c>Customer</c> and
    /// <c>KubernetesCluster</c> read-only all along. The blocker was a claim about the module, never
    /// checked per service.</para>
    ///
    /// <para><b>What the narrower context then refused is the useful part.</b> <c>MongoService</c>
    /// and <c>CnpgService</c> would not compile on it: they read and <em>delete</em>
    /// <c>db.VaultSecrets</c>, which belongs to Secrets. That is the boundary doing its job — and
    /// <c>VaultService</c> already owns the pattern for it
    /// (<c>DeleteRabbitMQClusterSecretAsync</c>), so these two simply never used the door.</para>
    ///
    /// <para>⚠️ Converting them is <b>not</b> a context swap, which is why they are not in this
    /// batch. <c>CnpgService.DeleteDatabaseSecretsAsync(Guid tenantId, Guid databaseId, …)</c> takes
    /// a tenant and <em>never uses it</em> — it deletes every vault secret matching the database id,
    /// so a database id from elsewhere deletes that tenant's secrets. Mongo's equivalent has no
    /// tenant in scope at all. Those want the three missing <c>VaultService</c> methods, scoped the
    /// way the RabbitMQ one is, and a tenant threaded to the Mongo callers — a separate change with
    /// a separate reason.</para>
    ///
    /// <para>The remaining 8 split: four read <c>AppDeployments</c> (Delivery's, and not a hub) so
    /// they want <c>IDeliveryApi</c>; two are the Mongo/Cnpg pair above; and
    /// <c>StorageService</c> with <c>StorageLinkClientFactory</c> hold credentials, so they move
    /// with the custody work.</para>
    /// <para><b>DataServices 8 → 2, and what is left is only what credentials keep.</b>
    /// <c>MongoService</c> and <c>CnpgService</c> moved the moment #230 took their
    /// <c>db.VaultSecrets</c> delete out — the narrower context had refused to compile them, which
    /// is what found the missing tenant predicate behind it. The other four
    /// (<c>Elasticsearch</c>, <c>Kafka</c>, <c>RabbitMQ</c>, <c>Redis</c>) each had the same query
    /// written out by hand — the tenant's deployments with app, customer and cluster included — and
    /// it was the only thing keeping them on the shared context.</para>
    ///
    /// <para><c>DeploymentSummary</c> gained <c>ClusterName</c> for it, and that is worth being
    /// explicit about because the rule cuts both ways: <b>four</b> callers wanted it (three service
    /// tabs and the public API's <c>/deployments</c> route, which had been resolving it through a
    /// separate Fleet call that is now deleted), so it earns a place beside <c>ClusterId</c> for
    /// the same reason <c>EnvironmentName</c> did. <c>CustomerName</c> had <b>one</b> caller and did
    /// not get one — <c>MessagingTab</c> resolves it from Delivery's own app list instead. A DTO
    /// that grows for a single consumer is how a contract turns back into a second name for the
    /// database.</para>
    ///
    /// <para>The two that remain, <c>StorageService</c> and <c>StorageLinkClientFactory</c>, hold
    /// cluster credentials, so they move with the custody work rather than before it. This module
    /// went from the largest block on this ratchet to the smallest in three changes, and none of
    /// them needed a new contract — only an existing one, a field with four readers, and a delete
    /// put back where it belonged.</para>
    ///
    /// <para>⚠️⚠️ <b>Correction: every number above was measured with half the metric. 34 was
    /// really 44.</b> <see cref="ScopeResolvers"/> explains the mechanism; this is what it costs.
    /// The count read constructor parameters by reflection, and a singleton cannot take a scoped
    /// context in its constructor, so <b>every background sweep in the product was exempt by
    /// construction</b> — twelve files, ten of them mapped services, none of them counted. The
    /// corrected baselines: <c>Telemetry 3 → 6</c> (<c>AlertSyncService</c>,
    /// <c>TelemetryAlertEvaluator</c>, <c>UptimeTrackingService</c>), <c>Delivery 3 → 6</c>
    /// (<c>DeploymentSyncService</c>, <c>GitSyncService</c>, <c>RolloutWatcherService</c>),
    /// <c>Connectivity 4 → 6</c> (<c>AppL4RouteHealthService</c>,
    /// <c>HeadscaleCertSyncService</c>), <c>Fleet 2 → 3</c> (<c>BootstrapRunnerService</c>) and
    /// <c>Cost 0 → 1</c> (<c>ResourceUsageCollectorService</c>).</para>
    ///
    /// <para><b>Cost is the one to read twice.</b> It was recorded as <c>0</c> — a finished module,
    /// quoted as such — while its usage collector swept every tenant's clusters on the all-seeing
    /// context the whole time. A zero that was never capable of being anything else is worse than
    /// a high number, because nobody goes back to a module that is done.</para>
    ///
    /// <para><b>Telemetry 6 → 5, and it took the sixth module contract.</b>
    /// <c>ErrorBudgetService</c> owns no table at all: its deployments and their health samples
    /// are Delivery's, its objectives are Support's, and holding the all-seeing context was the
    /// only way it had to read any of them. It takes <c>IDeliveryApi</c> and the new
    /// <c>ISupportApi</c> now, and the file is the decomposition's shape in miniature — a
    /// computation over three other modules' rows, with no rows of its own.</para>
    ///
    /// <para><c>DeploymentHealthSample</c> is the contract that was missing, and it was written
    /// from the callers backwards: three re-slice the rows per deployment and per window, so it
    /// returns rows, while the two that want only a ratio (<c>IncidentService.GetUptimeAsync</c>
    /// and <c>SlaTargetManager</c>) are deliberately <em>not</em> served by it — §14.1 samples
    /// every five minutes, about 8,600 rows per deployment per month, and pulling all of them to
    /// compute two integers is not a boundary, it is a bill.</para>
    ///
    /// <para>⚠️ <c>DeploymentSummary</c> did <b>not</b> grow a customer for this, though
    /// <c>ErrorBudgetStatus</c> exposes <c>CustomerId</c> and <c>CustomerName</c> and
    /// <c>OperationsAdvisor</c> reads both. <c>AppSummary</c> already carries them and a
    /// deployment already carries its <c>AppId</c>, so the service joins the two — one extra call
    /// against a small set, and the same route <c>MessagingTab</c> was given when
    /// <c>CustomerName</c> was refused a place on the DTO for having one reader.</para>
    ///
    /// <para><b>Telemetry 5 → 3, and the two that moved were the ones the ratchet had never
    /// counted.</b> <c>AlertSyncService</c> and <c>TelemetryAlertEvaluator</c> needed no contract
    /// and no tidying: between them they touch <c>AlertIncidents</c>,
    /// <c>AlertRoutingRules</c>, <c>NotificationChannels</c> and <c>TelemetryAlertRules</c> — all
    /// Telemetry's own — plus <c>Tenants</c> and <c>KubernetesClusters</c>, which every module
    /// context has exposed read-only from the start. They were a context swap the whole time, and
    /// the only reason nobody did it is that the metric said they were not there. Correcting a
    /// measurement is not bookkeeping; it is how work gets found.</para>
    ///
    /// <para>⚠️ The swap is not quite free, and the reason is worth keeping: a module context
    /// exposes the four hubs as <c>IQueryable</c> with <c>AsNoTracking</c>, so a cluster read
    /// through one comes back <b>detached</b> where <c>ApplicationDbContext</c>'s <c>DbSet</c>
    /// returned it tracked. <c>AlertSyncService</c> assigns that cluster onto each incident's
    /// navigation so <c>NotificationService</c> can render <c>incident.Cluster?.Name</c>, and
    /// attaching a detached principal to a <em>tracked</em> dependent is how EF is persuaded to
    /// insert a row that already exists. It is correct today only because every write in the
    /// method happens above that line; the comment there says so, in the imperative, because the
    /// next person to add a <c>SaveChanges</c> is who it is for.</para>
    ///
    /// <para><b>Then six more came off at once, and every one of them was a sweep the metric had
    /// been hiding: Delivery 6 → 3, Connectivity 6 → 4, Cost 1 → 0.</b>
    /// <c>DeploymentSyncService</c>, <c>GitSyncService</c> and <c>RolloutWatcherService</c> touch
    /// only <c>AppDeployments</c>, <c>DeploymentManifests</c>, <c>DeploymentRollouts</c>,
    /// <c>RolloutPolicies</c> and <c>GitRepositories</c>; <c>AppL4RouteHealthService</c> and
    /// <c>HeadscaleCertSyncService</c> only <c>AppL4Routes</c> and <c>ExternalRoutes</c>;
    /// <c>ResourceUsageCollectorService</c> only <c>ResourceUsageSnapshots</c>. All own tables,
    /// plus scalar reads of two hubs. No contract, no tidying, no behaviour change — six context
    /// swaps that were available from the day the module contexts were written.</para>
    ///
    /// <para><b>Cost is now 0 for real, and this is the second time this module's zero has meant
    /// two different things.</b> It read 0 while a collector swept every tenant on the all-seeing
    /// context; it reads 0 now because that collector takes <c>CostDbContext</c>. The number did
    /// not change. What it measures did.</para>
    ///
    /// <para>⚠️ The compiler is the adjudicator for a swap like this, not a grep. A scan for
    /// <c>db.Table</c> cannot see a read that arrives through a navigation — the mistake this
    /// file has recorded three times — whereas the narrower context simply fails to compile on a
    /// table it does not expose, which is how <c>BootstrapRunnerService</c> stayed behind: it
    /// writes <c>CnpgClusters</c>, <c>RabbitMQClusters</c> and <c>RedisClusters</c>, three
    /// DataServices tables, so it is a contract's worth of work rather than a swap.</para>
    ///
    /// <para>The six were checked for one thing beyond compiling, because the hub change is not
    /// type-visible: every hub read in them is a <em>scalar projection</em>
    /// (<c>Select(t =&gt; t.Id)</c>, <c>Select(a =&gt; a.Customer.TenantId)</c>), so no hub entity
    /// is materialised and the detached-principal trap above cannot arise. That had to be read
    /// rather than assumed — compiling says nothing about it either way.</para>
    ///
    /// <para><b>The fifth hub was missing, and finding it took Connectivity 4 → 3.</b> Four of
    /// the services still on the all-seeing context read <c>db.Environments</c> and nothing else
    /// foreign — and <c>Data.Environment</c> was reachable from <c>DeliveryDbContext</c> alone,
    /// because Delivery owns the table (<c>AppEnvironment</c> and <c>CustomerEnvironment</c> live
    /// beside it). That ownership is right. What was wrong is that <b>24 entities across 7
    /// modules carry an <c>EnvironmentId</c></b> — Fleet's own <c>KubernetesCluster</c> among them
    /// — so every other module had to hold the context that sees everything in order to ask which
    /// stage a row belonged to.</para>
    ///
    /// <para>That is the same concentration §4.0 used to pick the original four: 70 of 164
    /// cross-module foreign keys point at Tenant or Customer and another 40 at App or
    /// KubernetesCluster. Environment belongs in that set on the same evidence, and the entity's
    /// own doc comment has said so since it was written — "Clusters, services, and other
    /// resources are scoped to an environment within a tenant". It is a tenant-scoped dimension
    /// row carrying an id, a tenant and a name; nobody had read it as a hub.</para>
    ///
    /// <para><c>IngressDashboardService</c> came off immediately. The other three
    /// (<c>CertificateDistributionService</c>, <c>OperationsAdvisorService</c>,
    /// <c>TenantService</c>) still read other modules' tables, so the hub is necessary for them
    /// but not sufficient — which is the honest way to describe a structural fix: it removed one
    /// reason to hold the context, not every reason.</para>
    ///
    /// <para><b>Fleet 3 → 2.</b> <c>VeleroService</c>'s last two uses of the shared context were
    /// a Catalog read it had been exempted from (see <c>CatalogContractAdoptionTests</c> — the
    /// exemption was wrong about the schema) and a projection of <c>KubernetesClusters</c>, which
    /// is <em>Fleet's own table</em>. Converting the first left nothing foreign, so the second
    /// was a plain swap. The service held the all-seeing context for one query it was told it
    /// could not give up.</para>
    ///
    /// <para><b>Identity 4 → 3</b>, and the only thing in the way was a property name.
    /// <c>ScimUserService</c> reads <c>db.Users</c> and nothing else — ASP.NET Identity's own
    /// DbSet, which exists on <c>ApplicationDbContext</c> because that context derives from
    /// <c>IdentityDbContext&lt;ApplicationUser&gt;</c>. The module context does not, and spells the
    /// same table <c>ApplicationUsers</c>. One rename.</para>
    ///
    /// <para>⚠️ Checked rather than assumed, because the compiler cannot see it: both contexts
    /// resolve <c>ApplicationUser</c> to the <em>same table name</em>
    /// (<c>Model.FindEntityType(typeof(ApplicationUser)).GetTableName()</c> compared across the
    /// two). Had the module context fallen back to convention while the real table is
    /// <c>AspNetUsers</c>, the swap would have compiled and then queried a table that does not
    /// exist — the hazard <c>EfTableNameTests</c> was written for, in a context where the name
    /// comes from a base class rather than from a <c>DbSet</c> property.</para>
    ///
    /// <para><b>Support 7 → 6, and the eighth contract method was an interval.</b>
    /// <c>MonthlyReportService</c> read three of Delivery's and Fleet's tables; two of the three
    /// questions already had contract methods (deployments, and the health samples added for
    /// <c>ErrorBudgetService</c> — this was the second of the three row-shaped callers that method
    /// was written from). The third did not, and the reason is worth keeping:
    /// <c>IFleetApi</c>'s maintenance methods answer about an <b>instant</b>, which is what a
    /// suppression check asks. A month cannot be assembled from instants — a window that opened in
    /// August and closed in September is active at no instant the report names, and it still
    /// excludes the downtime it covered. <c>GetMaintenanceWindowsOverlappingAsync</c> states
    /// <c>StartsAt &lt; to &amp;&amp; EndsAt &gt; from</c> once.</para>
    ///
    /// <para><c>MaintenanceWindowSummary</c> went from four fields to eight, and <b>the four it
    /// gained are the four the first draft was refused for carrying "for nobody"</b> — the title,
    /// the creator, the announcement and the kind. That is the rule working in both directions
    /// rather than being bent: a field needs a reader, and these have one now. <c>Description</c>
    /// is still absent, because nothing reads it.</para>
    ///
    /// <para>⚠️ The announcement is resolved <em>at the boundary</em>: the entity holds
    /// <c>AnnouncedAt</c> and <c>CreatedAt</c> and the rule is "the recorded announcement, else
    /// the moment the window was created, which is the only announcement the system can vouch
    /// for". The contract carries one non-nullable <c>AnnouncedAt</c> rather than both columns,
    /// because two fields let a consumer read <c>CreatedAt</c> and call it the announcement — the
    /// same value by a different name only when nobody recorded one. A contract that hands over
    /// the columns hands over the rule as well.</para>
    ///
    /// <para><b>Catalog 6 → 5, by moving a rule rather than a query.</b>
    /// <c>TrustBundleService.ResolveTenantNamespacesAsync</c> was an <c>internal static</c> helper
    /// over the shared context, called by two Catalog services. What it implements — an app's
    /// namespace is the one governance pinned on its <c>AppEnvironment</c>, falling back to the
    /// deployment's own — reads <c>Apps</c>, <c>AppEnvironments</c> and <c>AppDeployments</c>:
    /// three of <em>Delivery's</em> tables and none of Catalog's. Correct code in the wrong
    /// module, which a count of foreign reads shows and a count of foreign <em>rules</em> would
    /// have shown sooner.</para>
    ///
    /// <para>It is <c>GetTenantNamespacesOnClusterAsync</c> now, and it returns namespace
    /// <em>names</em>: both callers want exactly a list for a namespace selector, so a row-shaped
    /// answer would hand back app and environment ids neither reads. It also queries less — the
    /// original loaded every <c>AppEnvironment</c> row for every app in the tenant and then looked
    /// up a handful, where this looks up only the pairs that deploy on the cluster, which cannot
    /// change the answer — and it <b>orders</b> the result, which the original did not. The list
    /// goes into a generated manifest, so an unstable order reads as drift in a resource nobody
    /// touched.</para>
    ///
    /// <para>⚠️ The rule had <b>no test</b>. The only ones naming <c>TenantNamespaces</c> hand an
    /// already-resolved list to <c>BuildBundleManifest</c>, so the resolution itself was never
    /// exercised in either of its two callers. <c>TenantNamespaceResolutionTests</c> covers it in
    /// seven cases, each verified by deleting the rule it names and watching that test — not
    /// merely some test — fail. What distributes into these namespaces is a CA trust store: one
    /// wrongly included receives another tenant's trust anchors, and one wrongly left out silently
    /// never gets the CA it needs. Moving a rule is the moment to cover it, not to assume it.</para>
    ///
    /// <para><b>The two unmapped files are mapped, and the hole they came through is closed.</b>
    /// <c>KubeconfigResolver</c> and <c>TelemetrySegmentBlobStore</c> reached the all-seeing
    /// context from a scope while belonging to no module — so no per-module count could see
    /// them, and that reads as compliance rather than as a gap. <see cref="ScopeResolvers"/> now
    /// <em>fails</em> on an unmapped declarer instead of skipping it.</para>
    ///
    /// <para>⚠️ Neither was reachable by a registration check, and for two different reasons.
    /// <c>KubeconfigResolver</c> was registered in <c>Program.cs</c>, which
    /// <c>ModuleCompositionTests</c> does not read — that test walks the <c>{Module}Services</c>
    /// classes, so the one registration no module owned was outside the ownership check written
    /// to enforce exactly that. <c>TelemetrySegmentBlobStore</c> takes a tenant id in its
    /// constructor, so it is <em>never</em> DI-registered: it is built by hand in
    /// <c>SegmentManagerRegistry</c>, and no test of the container could ever have found it.
    /// A check on registrations and a check on the source are not the same check.</para>
    ///
    /// <para><c>KubeconfigResolver</c> came off the context in the same change — it reads
    /// <c>VaultSecret</c> and <c>SecretVault</c>, both Secrets' own, so it takes
    /// <c>SecretsDbContext</c> and Secrets stays at 0. ⚠️ It reached them through
    /// <c>db.Set&lt;VaultSecret&gt;()</c> and <c>db.Set&lt;SecretVault&gt;()</c>, and
    /// <b>nothing counted those</b>: the <c>Set&lt;T&gt;()</c> rule in
    /// <c>CatalogContractAdoptionTests</c> is written for <c>ClusterComponent</c>, so the hole is
    /// measured for one entity and open for the other 164. They are real DbSet properties on the
    /// module context now, but the measurement gap is worth stating.</para>
    ///
    /// <para><c>TelemetrySegmentBlobStore</c> stays, and <b>Telemetry goes 3 → 4</b> because it is
    /// now counted rather than because anything regressed. It reads <c>StorageLinks</c> —
    /// DataServices' — which <c>IDataServicesApi.GetStorageLinkAsync</c> already serves, but
    /// <c>StorageLinkClientFactory.CreateClientAsync</c> takes the <em>entity</em>, and that
    /// factory is one of the two credential-holding services waiting on the custody work. ⚠️ Its
    /// tenant check is also the wrong way round: it loads the link by id and then compares
    /// <c>link.TenantId</c>, so it reads another tenant's row before refusing it. A predicate
    /// would not have to.</para>
    /// </summary>
    /// <summary>
    /// ⚠️ <b>A ratchet constant has no correct merge, and this one was merged anyway — for the
    /// third time.</b> #246 (Mail 2 → 1) and #247 (the corrected count, Fleet 2 → 3) touched this
    /// dictionary forty minutes apart. Both sides' <em>prose</em> survived: the correction
    /// paragraph below still said "Fleet 2 → 3" while the constant above it said 2. The merge had
    /// picked a side, main went red on <c>No_module_adds_another_service_on_the_all_seeing_context</c>
    /// with <c>Fleet: 3 (was 2)</c>, and the file documented the number it was not holding.
    ///
    /// <para>Re-measured rather than resolved: Fleet is <c>KubernetesOperationsService</c> and
    /// <c>VeleroService</c> by constructor plus <c>BootstrapRunnerService</c> by scope, so
    /// <b>3</b>. Neither side's value was right to keep — #246's 2 predated the metric fix and
    /// #247's 3 predated #246's conversions. Taking either on faith would have been luck.</para>
    ///
    /// <para>The rule, now stated where the constants are: when two branches both touch these
    /// numbers, <b>run the measurement on the merge result</b>. A number here is a fact about the
    /// tree, and a merge produces a tree neither author ever measured.</para>
    /// </summary>
    private static readonly Dictionary<Module, int> Baseline = new()
    {
        [Module.DataServices] = 2,
        [Module.Telemetry] = 4,
        [Module.Connectivity] = 3,
        [Module.Support] = 6,
        [Module.Catalog] = 5,
        [Module.Delivery] = 3,
        [Module.Identity] = 3,
        [Module.Fleet] = 2,
        [Module.Mail] = 1,
        [Module.Cost] = 0,
        [Module.Advisor] = 1,
        [Module.Secrets] = 0,
    };

    /// <summary>
    /// ⚠️ <b>A shape the count above cannot see: a STATIC helper that takes the god context as a
    /// parameter.</b>
    ///
    /// <para><see cref="GodContextUsers"/> asks each service's CONSTRUCTORS, and adds the scope
    /// spelling reflection cannot reach. Neither finds a static class with no constructor at all,
    /// which borrows the caller's context by parameter — so such a helper extends the all-seeing
    /// context's reach into a module while the metric reads unchanged. Converting
    /// <c>DeploymentPodScope</c> to <c>DeliveryDbContext</c> moved no number here, which is how
    /// the gap was noticed.</para>
    ///
    /// <para><b>Both spellings count.</b> The first version of this list looked for a bare
    /// <c>ApplicationDbContext</c> parameter and reported ZERO once <c>DeploymentPodScope</c> was
    /// converted — missing <c>JitClusterTokenStore</c>, which takes
    /// <c>IDbContextFactory&lt;ApplicationDbContext&gt;</c>. Same reach, different spelling.</para>
    ///
    /// <para><b>Now zero.</b> <c>JitClusterTokenStore</c> was the last one, and its conversion was
    /// the change this comment used to defer: the provisioner held the all-seeing factory for the
    /// single purpose of handing it to the store, so both moved to <c>IdentityDbContext</c>
    /// together and nothing else had to follow.</para>
    ///
    /// <para>⚠️ <b>Emptying it broke this test's own health check.</b> The first version proved the
    /// scan still worked by requiring the offender list to be NON-EMPTY — which is only ever true
    /// while an offender exists, so the last conversion would have failed the test that was
    /// guarding it. "Did the scan run" and "did it find anything" are two questions, and only the
    /// first can be answered by the scan's own output: it now counts the static classes it
    /// examined, and a zero THERE is the broken scan.</para>
    /// </summary>
    private static readonly string[] StaticHelpersOnTheGodContext = [];

    [Fact]
    public void No_static_helper_starts_borrowing_the_all_seeing_context()
    {
        string root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../src/EntKube.Web"));

        Directory.Exists(root).Should().BeTrue(
            "this test reads source; if the tree moved it has to follow rather than quietly stop "
            + "checking anything");

        Regex helper = new(
            @"public static[^\n(;]*\s(\w+)\((?:[^)]*?)"
            + @"(?:IDbContextFactory<ApplicationDbContext>|ApplicationDbContext)\s+\w+",
            RegexOptions.Compiled);

        List<string> offenders = [];
        int staticClassesExamined = 0;

        foreach (string path in Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories))
        {
            if (path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                || path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
            {
                continue;
            }

            string src = File.ReadAllText(path);

            foreach (Match cls in Regex.Matches(src, @"public static(?: partial)? class (\w+)"))
            {
                staticClassesExamined++;
                string name = cls.Groups[1].Value;
                if (helper.IsMatch(src[cls.Index..]) && !offenders.Contains(name))
                {
                    offenders.Add(name);
                }
            }
        }

        // The health check, separated from the finding: this says the scan READ something, which
        // is the only part its own output can vouch for. An empty offender list is now the goal,
        // so it can no longer double as evidence that the scan works.
        staticClassesExamined.Should().BeGreaterThan(20,
            "if this stops finding static classes the scan broke, and an empty offender list below "
            + "would mean nothing");

        offenders.Order().Should().BeEquivalentTo(StaticHelpersOnTheGodContext.Order(),
            "a static helper that takes ApplicationDbContext hands a module the whole product to "
            + "read, and does it where the constructor count cannot see. Take the module's own "
            + "context, as DeploymentPodScope now does");
    }

    [Fact]
    public void No_module_adds_another_service_on_the_all_seeing_context()
    {
        Dictionary<Module, int> actual = GodContextUsers();

        List<string> regressions =
        [
            .. actual
                .Where(a => a.Value > Baseline.GetValueOrDefault(a.Key))
                .Select(a => $"{a.Key}: {a.Value} (was {Baseline.GetValueOrDefault(a.Key)})")
                .Order()
        ];

        string.Join("; ", regressions).Should().BeEmpty(
            "a new service on ApplicationDbContext can read every table in the product, "
            + "which is the thing the module contexts exist to stop. Take the module's own "
            + "context instead — and if it genuinely needs another module's data, that is "
            + "what the contract in EntKube.Contracts is for.");
    }

    [Fact]
    public void The_ratchet_does_not_carry_slack()
    {
        Dictionary<Module, int> actual = GodContextUsers();

        List<string> slack =
        [
            .. Baseline
                .Where(b => actual.GetValueOrDefault(b.Key) < b.Value)
                .Select(b => $"{b.Key}: now {actual.GetValueOrDefault(b.Key)}, baseline still {b.Value}")
                .Order()
        ];

        string.Join("; ", slack).Should().BeEmpty(
            "this module has moved a service off the all-seeing context — bank it by "
            + "lowering the baseline, or the slack lets it come back for free");
    }
}
