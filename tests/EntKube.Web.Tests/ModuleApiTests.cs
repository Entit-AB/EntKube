using EntKube.Contracts.Catalog;
using EntKube.Contracts.DataServices;
using EntKube.Contracts.Delivery;
using EntKube.Contracts.Telemetry;
using EntKube.Contracts.Fleet;
using EntKube.Web.Data;
using EntKube.Web.Modules.Api;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace EntKube.Web.Tests;

/// <summary>
/// The module contracts answer for one tenant and no other.
///
/// <para><b>Why this is the test worth writing.</b> Before the contracts, every caller wrote
/// its own tenant filter — <c>c.Cluster.TenantId == tenantId</c>, twenty-one times over, by
/// hand. One of those being forgotten would leak another customer's components, and nothing
/// would say so. Moving the filter into the contract only helps if the contract is the thing
/// that is checked, so each one is asked for a neighbour's data here and must refuse.</para>
/// </summary>
public class ModuleApiTests : IDisposable
{
    private readonly SqliteConnection connection;
    private readonly TestDbContextFactory factory;
    private readonly ApplicationDbContext db;

    private readonly Guid ourTenant = Guid.NewGuid();
    private readonly Guid theirTenant = Guid.NewGuid();
    private Guid ourCluster, theirCluster, ourComponent, theirComponent;
    private Guid ourApp, theirApp, ourDeployment, theirDeployment;
    private Guid ourStorageLink, theirStorageLink;

    public ModuleApiTests()
    {
        connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();
        factory = new TestDbContextFactory(connection);
        db = factory.CreateDbContext();
        db.Database.EnsureCreated();

        Seed(ourTenant, "ours", out ourCluster, out ourComponent, out ourApp, out ourDeployment,
            out ourStorageLink);
        Seed(theirTenant, "theirs", out theirCluster, out theirComponent, out theirApp,
            out theirDeployment, out theirStorageLink);
        db.SaveChanges();
    }

    private void Seed(Guid tenantId, string label, out Guid clusterId, out Guid componentId,
        out Guid appId, out Guid deploymentId, out Guid storageLinkId)
    {
        storageLinkId = Guid.NewGuid();
        Guid environmentId = Guid.NewGuid();
        clusterId = Guid.NewGuid();
        componentId = Guid.NewGuid();
        appId = Guid.NewGuid();
        deploymentId = Guid.NewGuid();
        Guid customerId = Guid.NewGuid();

        db.Tenants.Add(new Tenant { Id = tenantId, Name = label, Slug = label });
        db.Environments.Add(new Data.Environment { Id = environmentId, TenantId = tenantId, Name = $"{label}-env" });
        db.KubernetesClusters.Add(new KubernetesCluster
        {
            Id = clusterId, TenantId = tenantId, EnvironmentId = environmentId,
            Name = $"{label}-cluster", ApiServerUrl = $"https://{label}.example",
        });
        db.ClusterComponents.Add(new ClusterComponent
        {
            Id = componentId, ClusterId = clusterId, Name = "harbor",
            ComponentType = "Helm", Status = Data.ComponentStatus.Installed,
        });
        db.Customers.Add(new Customer { Id = customerId, TenantId = tenantId, Name = $"{label}-customer" });
        db.Apps.Add(new App { Id = appId, CustomerId = customerId, Name = $"{label}-app" });
        db.AppDeployments.Add(new AppDeployment
        {
            Id = deploymentId, AppId = appId, Name = $"{label}-deploy",
            EnvironmentId = environmentId, ClusterId = clusterId, Namespace = label,
            // Deliberately NOT the zero member of any of the three. These are persisted as text,
            // and a projection that casts them to int reads 0 on SQLite and throws on PostgreSQL —
            // so seeding the default would hide both failures. See Delivery_carries_the_enums.
            Type = Data.DeploymentType.HelmChart,
            SyncStatus = Data.SyncStatus.OutOfSync,
            HealthStatus = Data.HealthStatus.Degraded,
        });
        db.StorageLinks.Add(new StorageLink
        {
            Id = storageLinkId, TenantId = tenantId, EnvironmentId = environmentId,
            Provider = StorageProvider.CleuraS3, Name = $"{label}-bucket",
            Endpoint = $"https://s3.{label}.example", BucketName = $"{label}-chunks", Region = "Kna1",
        });
    }

    public void Dispose()
    {
        db.Dispose();
        connection.Dispose();
        GC.SuppressFinalize(this);
    }

    private ICatalogApi Catalog => new CatalogApi(factory);
    private IFleetApi Fleet => new FleetApi(factory);
    private IDeliveryApi Delivery => new DeliveryApi(factory);
    private IDataServicesApi DataServices => new DataServicesApi(factory);
    private ITelemetryApi Telemetry => new TelemetryApi(factory);

    [Fact]
    public async Task Catalog_answers_for_the_asking_tenant_and_refuses_the_neighbour()
    {
        (await Catalog.GetComponentAsync(ourTenant, ourComponent)).Should().NotBeNull();
        (await Catalog.GetComponentAsync(ourTenant, theirComponent)).Should().BeNull(
            "asking for another tenant's component by id must not return it");

        (await Catalog.GetComponentsForTenantAsync(ourTenant)).Should().ContainSingle()
            .Which.ClusterName.Should().Be("ours-cluster");
        (await Catalog.GetComponentsForClusterAsync(ourTenant, theirCluster)).Should().BeEmpty();
        (await Catalog.FindComponentsAsync(ourTenant, "harbor")).Should().ContainSingle();
    }

    // ════════════════════════════════════════════════════════════════
    //  Configuring a component through the contract
    // ════════════════════════════════════════════════════════════════

    [Fact]
    public async Task Catalog_merges_form_values_into_what_is_already_stored()
    {
        await SetHelmValuesDirectlyAsync(ourComponent, "existing:\n  keep: yes\n");

        InstalledComponent? after = await Catalog.MergeHelmValuesAsync(
            ourTenant, ourComponent, new Dictionary<string, string> { ["added.field"] = "new" });

        // Merging, not replacing: the point of the method is that a caller patching one field
        // does not have to load, edit and write back the whole document itself.
        after.Should().NotBeNull();
        after!.HelmValues.Should().Contain("keep").And.Contain("new");
    }

    [Fact]
    public async Task Catalog_refuses_to_configure_another_tenants_component_and_writes_nothing()
    {
        await SetHelmValuesDirectlyAsync(theirComponent, "theirs: untouched\n");

        InstalledComponent? after = await Catalog.MergeHelmValuesAsync(
            ourTenant, theirComponent, new Dictionary<string, string> { ["injected"] = "yes" });

        after.Should().BeNull("a component that is not ours must read as absent");

        // The null is only half the guarantee. A refusal that still wrote would be worse than
        // no contract at all, so the row is checked too.
        using ApplicationDbContext check = factory.CreateDbContext();
        ClusterComponent theirs = check.ClusterComponents.Single(c => c.Id == theirComponent);
        theirs.HelmValues.Should().Be("theirs: untouched\n");
    }

    [Fact]
    public async Task Catalog_set_replaces_the_values_outright()
    {
        await SetHelmValuesDirectlyAsync(ourComponent, "old: gone\n");

        InstalledComponent? after = await Catalog.SetHelmValuesAsync(
            ourTenant, ourComponent, "fresh: document\n");

        after!.HelmValues.Should().Be("fresh: document\n");
        after.HelmValues.Should().NotContain("old");
    }

    [Fact]
    public async Task Catalog_leaves_the_configuration_blob_alone_when_none_is_given()
    {
        using (ApplicationDbContext seed = factory.CreateDbContext())
        {
            ClusterComponent c = seed.ClusterComponents.Single(x => x.Id == ourComponent);
            c.Configuration = "{\"StorageLinkId\":\"keep-me\"}";
            seed.SaveChanges();
        }

        InstalledComponent? after = await Catalog.MergeHelmValuesAsync(
            ourTenant, ourComponent, new Dictionary<string, string> { ["a"] = "b" });

        // Null means "leave it", not "clear it" — the one ambiguity the contract documents away.
        after!.Configuration.Should().Be("{\"StorageLinkId\":\"keep-me\"}");
    }

    [Fact]
    public async Task Catalog_carries_the_configuration_blob_that_callers_deserialize()
    {
        InstalledComponent? after = await Catalog.MergeHelmValuesAsync(
            ourTenant, ourComponent, new Dictionary<string, string> { ["a"] = "b" },
            "{\"StorageLinkId\":\"written\"}");

        // Nine foreign call sites read this field. The first version of the record omitted it,
        // which would have stopped every one of them adopting the contract.
        after!.Configuration.Should().Be("{\"StorageLinkId\":\"written\"}");
    }

    /// <summary>Sets a component's Helm values behind the contract's back, to arrange a test.</summary>
    private async Task SetHelmValuesDirectlyAsync(Guid componentId, string helmValues)
    {
        using ApplicationDbContext seed = factory.CreateDbContext();
        ClusterComponent c = seed.ClusterComponents.Single(x => x.Id == componentId);
        c.HelmValues = helmValues;
        await seed.SaveChangesAsync();
    }

    [Fact]
    public async Task Fleet_answers_for_the_asking_tenant_and_refuses_the_neighbour()
    {
        (await Fleet.GetClusterAsync(ourTenant, ourCluster)).Should().NotBeNull();
        (await Fleet.GetClusterAsync(ourTenant, theirCluster)).Should().BeNull();

        (await Fleet.GetClustersAsync(ourTenant)).Should().ContainSingle()
            .Which.EnvironmentName.Should().Be("ours-env");

        // Even handed the other tenant's id explicitly, the filter still applies.
        (await Fleet.GetClustersAsync(ourTenant, [ourCluster, theirCluster]))
            .Should().ContainSingle().Which.Id.Should().Be(ourCluster);
    }

    [Fact]
    public async Task Delivery_answers_for_the_asking_tenant_and_refuses_the_neighbour()
    {
        (await Delivery.GetAppAsync(ourTenant, ourApp)).Should().NotBeNull();
        (await Delivery.GetAppAsync(ourTenant, theirApp)).Should().BeNull();

        (await Delivery.GetDeploymentAsync(ourTenant, ourDeployment)).Should().NotBeNull();
        (await Delivery.GetDeploymentAsync(ourTenant, theirDeployment)).Should().BeNull(
            "tenancy reaches a deployment through its app's customer — the long way round is "
            + "exactly where a hand-written filter goes wrong");

        DeploymentSummary ours = (await Delivery.GetDeploymentsForTenantAsync(ourTenant)).Should()
            .ContainSingle().Subject;
        ours.AppName.Should().Be("ours-app");

        // The inline environment name is why CostReportService can group a report by environment
        // without reaching for Delivery's Environments table. A contract field nothing asserts is a
        // contract field that silently stops being projected.
        ours.EnvironmentName.Should().Be("ours-env");

        // Same bargain, same reason to assert it: ClusterName is here so the four callers that
        // display it — three service tabs and the public API's /deployments route — do not each
        // make a Fleet call for one string.
        ours.ClusterName.Should().Be("ours-cluster");
        (await Delivery.GetDeploymentsForClusterAsync(ourTenant, theirCluster)).Should().BeEmpty();
    }

    [Fact]
    public async Task DataServices_answers_for_the_asking_tenant_and_refuses_the_neighbour()
    {
        StorageLinkSummary ours = (await DataServices.GetStorageLinkAsync(ourTenant, ourStorageLink))
            .Should().NotBeNull().And.Subject.As<StorageLinkSummary>();

        (await DataServices.GetStorageLinkAsync(ourTenant, theirStorageLink)).Should().BeNull(
            "the four telemetry services this contract was written for were each handed a "
            + "storageLinkId and a tenantId as independent parameters");

        // The three fields every caller reads. The enum is checked by ordinal across every member
        // in its own test below, since one value round-tripping here would not notice a member
        // appended to only one of the two.
        ours.Endpoint.Should().Be("https://s3.ours.example");
        ours.BucketName.Should().Be("ours-chunks");
        ours.Region.Should().Be("Kna1");
        ours.Provider.Should().Be(StorageProviderKind.CleuraS3);
    }

    /// <summary>
    /// <see cref="StorageProviderKind"/> is mapped from <see cref="StorageProvider"/> by a cast
    /// through <c>int</c>, which is the cheapest mapping and the one that fails silently: a member
    /// added to one enum and not the other configures a chart for the wrong provider, with nothing
    /// to say so. Compared by name at every ordinal, so either edit fails here.
    ///
    /// <para>Names are compared case-insensitively on purpose — the entity spells them
    /// <c>MinIO</c> and <c>CubeFS</c>, the contract <c>Minio</c> and <c>CubeFs</c>, and the
    /// contract follows .NET naming rather than copying the entity's.</para>
    /// </summary>
    [Fact]
    public void The_storage_provider_enums_agree_member_for_member()
    {
        string[] entity = Enum.GetNames<StorageProvider>();
        string[] contract = Enum.GetNames<StorageProviderKind>();

        contract.Should().BeEquivalentTo(entity, o => o.WithStrictOrdering().Using<string>(
            c => c.Subject.Should().BeEquivalentTo(c.Expectation)).WhenTypeIs<string>());

        foreach (StorageProvider p in Enum.GetValues<StorageProvider>())
        {
            ((int)(StorageProviderKind)(int)p).Should().Be((int)p,
                $"{p} must keep its ordinal, because the projection casts rather than switches");
        }
    }

    /// <summary>
    /// The three enums a deployment carries survive the trip through the contract.
    ///
    /// <para><b>This is the test that was missing, and the bug it would have caught was live.</b>
    /// <c>AppDeployment.Type</c>, <c>SyncStatus</c> and <c>HealthStatus</c> are persisted as text
    /// (<c>HasConversion&lt;string&gt;()</c>), and <c>DeliveryApi</c>'s projection cast them with
    /// <c>(ContractDeploymentType)(int)d.Type</c> <em>inside an <c>Expression</c></em> — so the cast
    /// ran in SQL as <c>CAST("Type" AS integer)</c>. PostgreSQL refuses outright
    /// (<c>22P02: invalid input syntax for type integer: "HelmChart"</c>, which is how the cost
    /// report failed); <b>SQLite returns 0</b>, so the old tests saw <c>Manual</c> with status 0 and
    /// passed.</para>
    ///
    /// <para>So asserting the <em>values</em> is what matters here, not that the call succeeds. A
    /// test that only checked for an exception would still pass on SQLite against the bug.</para>
    /// </summary>
    [Fact]
    public async Task Delivery_carries_the_enums_rather_than_casting_them_in_sql()
    {
        DeploymentSummary ours = (await Delivery.GetDeploymentAsync(ourTenant, ourDeployment))
            .Should().NotBeNull().And.Subject.As<DeploymentSummary>();

        ours.Type.Should().Be(EntKube.Contracts.Delivery.DeploymentType.HelmChart);
        ours.SyncStatus.Should().Be(EntKube.Contracts.Delivery.SyncStatus.OutOfSync);
        ours.HealthStatus.Should().Be(EntKube.Contracts.Delivery.HealthStatus.Degraded);

        // Every query path shares one projection, so a regression in any of them shows up here.
        (await Delivery.GetDeploymentsForTenantAsync(ourTenant)).Should().ContainSingle()
            .Which.Type.Should().Be(EntKube.Contracts.Delivery.DeploymentType.HelmChart);
        (await Delivery.GetDeploymentsForClusterAsync(ourTenant, ourCluster)).Should().ContainSingle()
            .Which.HealthStatus.Should().Be(EntKube.Contracts.Delivery.HealthStatus.Degraded);
    }

    /// <summary>
    /// The ordinal mapping above is only sound while the entity and contract enums agree member for
    /// member. Three pairs, one test — a member added to one side alone silently re-labels a
    /// deployment, and a mis-labelled health status is the kind of thing an operator acts on.
    /// </summary>
    [Fact]
    public void The_delivery_enums_agree_member_for_member()
    {
        Enum.GetNames<EntKube.Contracts.Delivery.DeploymentType>()
            .Should().Equal(Enum.GetNames<Data.DeploymentType>());
        Enum.GetNames<EntKube.Contracts.Delivery.SyncStatus>()
            .Should().Equal(Enum.GetNames<Data.SyncStatus>());
        Enum.GetNames<EntKube.Contracts.Delivery.HealthStatus>()
            .Should().Equal(Enum.GetNames<Data.HealthStatus>());
    }

    /// <summary>
    /// Telemetry answers for the asking tenant — and an incident carries no tenant of its own, so
    /// this is the long way round again: the filter goes through the incident's cluster.
    /// </summary>
    [Fact]
    public async Task Telemetry_answers_for_the_asking_tenant_and_refuses_the_neighbour()
    {
        Guid ourIncident = await SeedIncidentAsync(ourCluster, "DiskFull", Data.IncidentStatus.Active);
        await SeedIncidentAsync(theirCluster, "TheirAlert", Data.IncidentStatus.Active);

        IncidentSummary ours = (await Telemetry.GetIncidentsAsync(ourTenant)).Should()
            .ContainSingle("an incident belongs to the tenant that owns its cluster").Subject;

        ours.Id.Should().Be(ourIncident);
        ours.AlertName.Should().Be("DiskFull");
        ours.ClusterName.Should().Be("ours-cluster", "the name is inline to save a second lookup");
    }

    /// <summary>
    /// <c>openOnly</c> is the filter the public API exposes as <c>?open=true</c>, and "resolved" is
    /// the state it excludes — not "acknowledged", which is still open as far as an operator is
    /// concerned.
    /// </summary>
    [Fact]
    public async Task Telemetry_open_only_excludes_resolved_but_keeps_acknowledged()
    {
        await SeedIncidentAsync(ourCluster, "Acked", Data.IncidentStatus.Acknowledged);
        await SeedIncidentAsync(ourCluster, "Gone", Data.IncidentStatus.Resolved);

        (await Telemetry.GetIncidentsAsync(ourTenant, openOnly: true))
            .Select(i => i.AlertName).Should().Equal(["Acked"]);

        (await Telemetry.GetIncidentsAsync(ourTenant)).Should().HaveCount(2);
    }

    /// <summary>
    /// <c>AlertIncident.Status</c> is persisted as TEXT, so a cast inside the projection would
    /// become <c>CAST("Status" AS integer)</c> — which PostgreSQL rejects and SQLite answers with
    /// 0. That is the bug that took the cost report down (#223), and it passed its unit tests the
    /// whole time because nothing asserted the value. This asserts the value.
    /// </summary>
    [Fact]
    public async Task Telemetry_carries_the_status_rather_than_casting_it_in_sql()
    {
        await SeedIncidentAsync(ourCluster, "Acked", Data.IncidentStatus.Acknowledged);

        (await Telemetry.GetIncidentsAsync(ourTenant)).Should().ContainSingle()
            .Which.Status.Should().Be(EntKube.Contracts.Telemetry.IncidentStatus.Acknowledged);
    }

    /// <summary>The ordinal mapping above is sound only while the two declarations agree.</summary>
    [Fact]
    public void The_incident_status_enums_agree_member_for_member()
        => Enum.GetNames<EntKube.Contracts.Telemetry.IncidentStatus>()
            .Should().Equal(Enum.GetNames<Data.IncidentStatus>(),
                "the public API publishes these names, so the two drifting apart changes a "
                + "published JSON contract");

    private async Task<Guid> SeedIncidentAsync(Guid clusterId, string alert, Data.IncidentStatus status)
    {
        AlertIncident incident = new()
        {
            Id = Guid.NewGuid(),
            ClusterId = clusterId,
            Fingerprint = Guid.NewGuid().ToString("N"),
            AlertName = alert,
            Severity = "critical",
            Summary = $"{alert} fired",
            Status = status,
            StartsAt = DateTime.UtcNow.AddMinutes(-5),
            ResolvedAt = status == Data.IncidentStatus.Resolved ? DateTime.UtcNow : null,
        };
        db.Set<AlertIncident>().Add(incident);
        await db.SaveChangesAsync();
        return incident.Id;
    }

    /// <summary>
    /// A storage link's access keys live in the vault, not on the row — which is why
    /// <see cref="StorageLinkSummary"/> can carry the whole row where
    /// <see cref="ClusterSummary"/> cannot. That is a fact about the schema and not about
    /// contracts, so it is asserted rather than assumed: if a key ever lands on the entity,
    /// this fails instead of the contract quietly handing it out.
    /// </summary>
    [Fact]
    public void A_storage_link_summary_carries_no_credential()
    {
        typeof(StorageLink).GetProperties().Select(p => p.Name)
            .Should().NotContain(n => n.Contains("AccessKey", StringComparison.OrdinalIgnoreCase)
                                   || n.Contains("SecretKey", StringComparison.OrdinalIgnoreCase)
                                   || n.Contains("Password", StringComparison.OrdinalIgnoreCase),
                "the contract projects every field of this entity, so a credential added to it "
                + "would be published to every module without anyone choosing to");
    }

    /// <summary>
    /// A cluster's kubeconfig is resolved from the vault on materialisation. Selecting it
    /// for a caller that only wanted a name would decrypt a credential for no reason, so the
    /// contract does not carry one at all.
    /// </summary>
    [Fact]
    public void A_cluster_summary_carries_no_credential()
    {
        typeof(ClusterSummary).GetProperties().Select(p => p.Name)
            .Should().NotContain(n => n.Contains("Kubeconfig", StringComparison.OrdinalIgnoreCase)
                                   || n.Contains("Secret", StringComparison.OrdinalIgnoreCase)
                                   || n.Contains("Token", StringComparison.OrdinalIgnoreCase));
    }
}
