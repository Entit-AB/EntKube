using System.Reflection;
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

    /// <summary>Services still taking the all-seeing context, by module.</summary>
    private static Dictionary<Module, int> GodContextUsers()
    {
        Dictionary<Module, int> counts = [];

        foreach ((Type service, Module module) in ModuleMap.Services)
        {
            bool takesIt = service.GetConstructors().Any(c => c.GetParameters().Any(p =>
                p.ParameterType == typeof(ApplicationDbContext)
                || (p.ParameterType.IsGenericType
                    && p.ParameterType.GetGenericArguments().Length == 1
                    && p.ParameterType.GetGenericArguments()[0] == typeof(ApplicationDbContext))));

            if (takesIt)
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
    /// </summary>
    private static readonly Dictionary<Module, int> Baseline = new()
    {
        [Module.DataServices] = 2,
        [Module.Telemetry] = 3,
        [Module.Connectivity] = 4,
        [Module.Support] = 7,
        [Module.Catalog] = 6,
        [Module.Delivery] = 3,
        [Module.Identity] = 4,
        [Module.Fleet] = 2,
        [Module.Mail] = 2,
        [Module.Cost] = 0,
        [Module.Advisor] = 1,
        [Module.Secrets] = 0,
    };

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
