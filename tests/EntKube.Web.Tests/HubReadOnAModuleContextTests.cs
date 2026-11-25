using EntKube.Web.Data;
using EntKube.Web.Data.Modules;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace EntKube.Web.Tests;

/// <summary>
/// The capability every hub-only page conversion rests on: reading a hub table, with an
/// <c>Include</c> across a module boundary, from a module context.
///
/// <para><b>Why this needs its own test.</b> The four hub tables are exposed on every module
/// context as a read-only <c>IQueryable</c> rather than a <c>DbSet</c>, and
/// <see cref="RazorGodContextRatchetTests"/> only asks which context a component injects. Whether
/// that <c>IQueryable</c> actually <em>serves</em> the query a page makes is a runtime question
/// the ratchet cannot see: a page could be structurally converted, pass every structural check,
/// and throw on first render.</para>
///
/// <para>⚠️ The <c>Include</c> is the part worth pinning. <c>TenantLogsTab</c> reads
/// <c>KubernetesClusters</c> with <c>.Include(c =&gt; c.Components)</c> — a hub table pulling in a
/// <em>Catalog</em> entity — from Telemetry's context. (<c>TenantAlertsTab</c> made the same read
/// until it asked Catalog's <c>FindPrometheusAsync</c> instead; the logs tab still needs the
/// components for its Loki and collector checks.) That works because <c>ModuleDbContext</c>
/// builds the same full model for every module, so the navigation exists; but "it compiles" and
/// "EF can translate it" are different claims, and only the second one matters at render time.
/// </para>
///
/// <para>This is deliberately not a render test. Driving the component would need
/// <c>PrometheusService</c> and its whole dependency graph, and would test the page's wiring
/// rather than the property the conversion depends on. What can break is the query; so the query
/// is what this executes.</para>
/// </summary>
public class HubReadOnAModuleContextTests : IDisposable
{
    private readonly SqliteConnection connection;
    private readonly ApplicationDbContext seed;
    private readonly TestDbContextFactory factory;

    private readonly Guid tenantId = Guid.NewGuid();
    private readonly Guid otherTenantId = Guid.NewGuid();

    public HubReadOnAModuleContextTests()
    {
        connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();
        seed = new ApplicationDbContext(
            new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(connection).Options);
        seed.Database.EnsureCreated();
        factory = new TestDbContextFactory(connection);

        seed.Tenants.Add(new Tenant { Id = tenantId, Name = "ours", Slug = "ours" });
        seed.Tenants.Add(new Tenant { Id = otherTenantId, Name = "theirs", Slug = "theirs" });

        Data.Environment ours = new() { Id = Guid.NewGuid(), TenantId = tenantId, Name = "prod" };
        Data.Environment theirs = new() { Id = Guid.NewGuid(), TenantId = otherTenantId, Name = "prod" };
        seed.Environments.AddRange(ours, theirs);

        Guid clusterId = Guid.NewGuid();
        seed.KubernetesClusters.Add(new KubernetesCluster
        {
            Id = clusterId, TenantId = tenantId, EnvironmentId = ours.Id,
            Name = "with-prometheus", ApiServerUrl = "https://a.example",
        });
        seed.KubernetesClusters.Add(new KubernetesCluster
        {
            Id = Guid.NewGuid(), TenantId = otherTenantId, EnvironmentId = theirs.Id,
            Name = "someone-elses", ApiServerUrl = "https://b.example",
        });

        // A component for the Include to materialise.
        seed.ClusterComponents.Add(new ClusterComponent
        {
            Id = Guid.NewGuid(),
            ClusterId = clusterId,
            Name = "kube-prometheus-stack",
            ComponentType = "Helm",
            HelmChartName = "kube-prometheus-stack",
        });

        seed.SaveChanges();
    }

    public void Dispose()
    {
        seed.Dispose();
        connection.Dispose();
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// ⭐ The exact query <c>TenantLogsTab</c> makes, executed against Telemetry's context.
    /// </summary>
    [Fact]
    public async Task A_hub_table_serves_an_include_across_a_module_boundary()
    {
        using TelemetryDbContext db = ((IDbContextFactory<TelemetryDbContext>)factory).CreateDbContext();

        List<KubernetesCluster> clusters = await db.KubernetesClusters
            .Include(c => c.Components)
            .Where(c => c.TenantId == tenantId)
            .OrderBy(c => c.Name)
            .ToListAsync();

        clusters.Should().ContainSingle("the other tenant's cluster is not this tenant's")
            .Which.Name.Should().Be("with-prometheus");

        clusters[0].Components.Should().ContainSingle(
            "the Include has to materialise a Catalog entity from Telemetry's context — the model "
            + "is the same for every module, which is what makes the hub read usable at all")
            .Which.HelmChartName.Should().Be("kube-prometheus-stack");
    }

    /// <summary>
    /// The same read from Support's context, which is what <c>SlaTargetManager</c> relies on for
    /// <c>Customers</c>. ⚠️ Asserted so that "hubs are readable from any module context" is pinned
    /// as a general property rather than inferred from the one page that happens to do it.
    /// </summary>
    [Fact]
    public async Task The_same_read_works_from_another_modules_context()
    {
        using SupportDbContext db = ((IDbContextFactory<SupportDbContext>)factory).CreateDbContext();

        (await db.KubernetesClusters.CountAsync(c => c.TenantId == tenantId)).Should().Be(1);
    }
}
