using EntKube.Web.Data;
using EntKube.Web.Services;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace EntKube.Web.Tests;

/// <summary>
/// Who may link a Harbor project to an application.
///
/// <para>⚠️ <c>LinkProjectToAppAsync</c> took a <c>tenantId</c> and used it only to <b>stamp</b> a
/// new row. Neither of the two ids it was handed was ever checked against it, and both decide
/// something:</para>
///
/// <para><b>The Harbor config id</b> says which registry the project belongs to.
/// <c>GetTrackedProjectsAsync</c> lists by that id, so a row written under another tenant's
/// config appears on their registry page.</para>
///
/// <para><b>The app id</b> becomes <c>LinkedAppId</c>, which is what grants an application pull
/// access to the project in the portal.</para>
///
/// <para>And the upsert's own lookup was by config id and project name alone, so the final write
/// could repoint an <em>existing</em> row belonging to somebody else rather than create one.
/// Three separate crossings, one method, all reachable with ids that travel in page markup.</para>
/// </summary>
public class HarborProjectLinkTests : IDisposable
{
    private readonly SqliteConnection connection;
    private readonly ApplicationDbContext db;
    private readonly TestDbContextFactory dbFactory;
    private readonly HarborService sut;

    public HarborProjectLinkTests()
    {
        connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();

        db = new ApplicationDbContext(
            new DbContextOptionsBuilder<ApplicationDbContext>()
                .UseSqlite(connection).Options);
        db.Database.EnsureCreated();
        dbFactory = new TestDbContextFactory(connection);

        // ⚠️ The collaborators are null on purpose. LinkProjectToAppAsync touches nothing but the
        // DbContext — no vault, no storage, no Redis, no CNPG, no catalog, no HTTP — so building
        // five real services (StorageService alone takes eleven arguments) would be scaffolding
        // that proves nothing. Any method that did reach them would fail loudly here rather than
        // pass quietly, which is the behaviour I want from a deliberately partial fixture.
        sut = new HarborService(dbFactory, null!, null!, null!, null!, null!, null!);
    }

    public void Dispose()
    {
        db.Dispose();
        connection.Dispose();
        GC.SuppressFinalize(this);
    }

    /// <summary>A tenant with a customer, an app and a Harbor config of its own.</summary>
    private (Guid Tenant, Guid App, Guid Config) Seed(string slug)
    {
        Guid tenant = Guid.NewGuid(), customer = Guid.NewGuid(), app = Guid.NewGuid();
        Guid environment = Guid.NewGuid(), cluster = Guid.NewGuid();
        Guid component = Guid.NewGuid(), config = Guid.NewGuid();

        db.Tenants.Add(new Tenant { Id = tenant, Name = slug, Slug = $"{slug}-{tenant:N}" });
        db.Customers.Add(new Customer { Id = customer, TenantId = tenant, Name = $"{slug}-co" });
        db.Apps.Add(new App { Id = app, CustomerId = customer, Name = $"{slug}-app" });
        db.Set<Data.Environment>().Add(new Data.Environment
        {
            Id = environment, TenantId = tenant, Name = $"{slug}-prod",
        });
        db.KubernetesClusters.Add(new KubernetesCluster
        {
            Id = cluster, TenantId = tenant, EnvironmentId = environment,
            Name = $"{slug}-cluster", ApiServerUrl = "https://k8s.example.com",
        });
        db.ClusterComponents.Add(new ClusterComponent
        {
            Id = component, ClusterId = cluster, Name = "harbor",
            ComponentType = "Helm", Namespace = "harbor", Status = ComponentStatus.Installed,
        });
        db.Set<HarborComponentConfig>().Add(new HarborComponentConfig
        {
            Id = config, TenantId = tenant, ClusterComponentId = component,
            AdminUsername = "admin",
        });
        db.SaveChanges();

        return (tenant, app, config);
    }

    [Fact]
    public async Task A_project_cannot_be_linked_under_another_tenants_harbor()
    {
        var us = Seed("us");
        var them = Seed("them");

        Func<Task> underTheirHarbor = () => sut.LinkProjectToAppAsync(
            us.Tenant, them.Config, "library", us.App);

        await underTheirHarbor.Should().ThrowAsync<InvalidOperationException>();

        db.Set<HarborProject>().Should().BeEmpty(
            "a row under their config would show up on their registry page");
    }

    [Fact]
    public async Task A_project_cannot_be_linked_to_another_tenants_app()
    {
        var us = Seed("us");
        var them = Seed("them");

        Func<Task> toTheirApp = () => sut.LinkProjectToAppAsync(
            us.Tenant, us.Config, "library", them.App);

        await toTheirApp.Should().ThrowAsync<InvalidOperationException>();

        db.Set<HarborProject>().Should().BeEmpty(
            "LinkedAppId is what grants an app pull access to the project");

        // Our own link, entirely within one tenant, goes through.
        HarborProject ours = await sut.LinkProjectToAppAsync(
            us.Tenant, us.Config, "library", us.App);
        ours.LinkedAppId.Should().Be(us.App);
        ours.TenantId.Should().Be(us.Tenant);
    }

    /// <summary>
    /// ⚠️ The upsert half. With the lookup unscoped, a project name that already exists under
    /// <em>their</em> config would be found and its <c>LinkedAppId</c> rewritten — so the damage
    /// is a repointed link rather than a new row, which a "no row was created" assertion would
    /// have missed entirely.
    /// </summary>
    [Fact]
    public async Task An_existing_project_link_cannot_be_repointed_across_tenants()
    {
        var us = Seed("us");
        var them = Seed("them");

        HarborProject theirs = await sut.LinkProjectToAppAsync(
            them.Tenant, them.Config, "library", them.App);

        Func<Task> repoint = () => sut.LinkProjectToAppAsync(
            us.Tenant, them.Config, "library", us.App);

        await repoint.Should().ThrowAsync<InvalidOperationException>();

        using ApplicationDbContext after = dbFactory.CreateDbContext();
        HarborProject intact = after.Set<HarborProject>().Single(p => p.Id == theirs.Id);
        intact.LinkedAppId.Should().Be(them.App, "their project still points at their app");
        intact.TenantId.Should().Be(them.Tenant);
    }
}
