using EntKube.Web.Data;
using EntKube.Web.Services;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;

namespace EntKube.Web.Tests;

/// <summary>
/// Five methods on <see cref="TenantService"/> that took a bare id — and the exemption that
/// said they were fine.
///
/// <para><b>This file exists because a recorded exemption was wrong.</b> `TenantService` was
/// written down as installation-admin surface, where acting across tenants is the feature, and
/// that is true of three of its methods: `UpdateTenantAsync`, `DeleteTenantAsync` and
/// `GetTenantPurgePreviewAsync` are reached only from `/admin/tenants`, and the id they take
/// <em>is</em> the tenant, so a tenant predicate on them would compare the row to itself.</para>
///
/// <para>The other five are reached from <b>tenant</b> pages. `ClusterDetail` deletes a
/// cluster, `CustomerTab` deletes a customer, `GroupTab` deletes a group, and four pages list a
/// customer's apps — all by id, all with the page's own `TenantId` sitting unused one line
/// away. The exemption covered the file; the callers did not agree.</para>
///
/// <para>The deletes assert that the row <b>survives</b>. A delete that returns false while
/// still removing the row would pass a test that only checked the return value.</para>
/// </summary>
public class TenantServiceDeleteTenancyTests : IDisposable
{
    private readonly InterceptingTestDb db;
    private readonly TenantService service;

    private readonly Guid tenantId = Guid.NewGuid();
    private readonly Guid otherTenantId = Guid.NewGuid();

    public TenantServiceDeleteTenancyTests()
    {
        db = new InterceptingTestDb(new byte[32]);
        service = new TenantService(
            db.Factory, db.CreateVaultService(),
            new EntKube.Web.Modules.Api.CatalogApi(db.Factory));

        using ApplicationDbContext ctx = db.CreateContext();
        ctx.Tenants.AddRange(
            new Tenant { Id = tenantId, Name = "ENTIT", Slug = $"entit-{tenantId:N}" },
            new Tenant { Id = otherTenantId, Name = "Annan", Slug = $"annan-{otherTenantId:N}" });
        ctx.SaveChanges();
    }

    public void Dispose()
    {
        db.Dispose();
        GC.SuppressFinalize(this);
    }

    private Guid SeedTheirCustomer(string name = "Region Skåne")
    {
        using ApplicationDbContext ctx = db.CreateContext();
        Guid id = Guid.NewGuid();
        ctx.Customers.Add(new Customer { Id = id, TenantId = otherTenantId, Name = name });
        ctx.SaveChanges();
        return id;
    }

    private Guid SeedTheirGroup()
    {
        using ApplicationDbContext ctx = db.CreateContext();
        Guid id = Guid.NewGuid();
        ctx.Groups.Add(new Group { Id = id, TenantId = otherTenantId, Name = "drift" });
        ctx.SaveChanges();
        return id;
    }

    private Guid SeedTheirCluster()
    {
        using ApplicationDbContext ctx = db.CreateContext();
        Guid envId = Guid.NewGuid(), clusterId = Guid.NewGuid();
        ctx.Environments.Add(new Data.Environment
        {
            Id = envId, TenantId = otherTenantId, Name = "production",
        });
        ctx.KubernetesClusters.Add(new KubernetesCluster
        {
            Id = clusterId, TenantId = otherTenantId, EnvironmentId = envId,
            Name = "deras-prod", ApiServerUrl = "https://k8s.example.invalid",
        });
        ctx.SaveChanges();
        return clusterId;
    }

    // ── The deletes ───────────────────────────────────────────────────────────

    /// <summary>
    /// The sharpest of the five. Removing the registration does not touch the cluster — which
    /// is worse than it sounds: the cluster keeps running and serving traffic with nothing left
    /// that can manage it, and the kubeconfig that reached it was held against this row.
    /// </summary>
    [Fact]
    public async Task Another_tenants_cluster_cannot_be_deregistered()
    {
        Guid theirs = SeedTheirCluster();

        (await service.DeleteClusterAsync(tenantId, theirs)).Should().BeFalse();

        using ApplicationDbContext ctx = db.CreateContext();
        (await ctx.KubernetesClusters.AsNoTracking().AnyAsync(c => c.Id == theirs))
            .Should().BeTrue();
    }

    [Fact]
    public async Task Another_tenants_customer_cannot_be_deleted()
    {
        Guid theirs = SeedTheirCustomer();

        (await service.DeleteCustomerAsync(tenantId, theirs)).Should().BeFalse();

        using ApplicationDbContext ctx = db.CreateContext();
        (await ctx.Customers.AsNoTracking().AnyAsync(c => c.Id == theirs)).Should().BeTrue();
    }

    [Fact]
    public async Task Another_tenants_group_cannot_be_deleted()
    {
        Guid theirs = SeedTheirGroup();

        (await service.DeleteGroupAsync(tenantId, theirs)).Should().BeFalse();

        using ApplicationDbContext ctx = db.CreateContext();
        (await ctx.Groups.AsNoTracking().AnyAsync(g => g.Id == theirs)).Should().BeTrue();
    }

    // ── The reads ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task Another_tenants_customer_is_not_returned()
    {
        Guid theirs = SeedTheirCustomer();

        (await service.GetCustomerAsync(tenantId, theirs)).Should().BeNull();
    }

    [Fact]
    public async Task Another_tenants_apps_are_not_listed()
    {
        Guid theirCustomer = SeedTheirCustomer();
        using (ApplicationDbContext ctx = db.CreateContext())
        {
            ctx.Apps.Add(new App
            {
                Id = Guid.NewGuid(), CustomerId = theirCustomer, Name = "journal",
            });
            ctx.SaveChanges();
        }

        (await service.GetAppsAsync(tenantId, theirCustomer)).Should().BeEmpty();
    }

    // ── The permitted directions ──────────────────────────────────────────────

    [Fact]
    public async Task Our_own_customer_its_apps_and_its_deletion_still_work()
    {
        Customer ours = await service.CreateCustomerAsync(tenantId, "Entit AB");
        App app = await service.CreateAppAsync(tenantId, ours.Id, "billing");

        (await service.GetCustomerAsync(tenantId, ours.Id)).Should().NotBeNull();
        (await service.GetAppsAsync(tenantId, ours.Id)).Should().ContainSingle()
            .Which.Id.Should().Be(app.Id);

        (await service.DeleteCustomerAsync(tenantId, ours.Id)).Should().BeTrue();
        (await service.GetCustomerAsync(tenantId, ours.Id)).Should().BeNull();
    }

    [Fact]
    public async Task Our_own_group_and_cluster_can_still_be_deleted()
    {
        Guid groupId, clusterId, envId = Guid.NewGuid();
        using (ApplicationDbContext ctx = db.CreateContext())
        {
            groupId = Guid.NewGuid();
            clusterId = Guid.NewGuid();
            ctx.Groups.Add(new Group { Id = groupId, TenantId = tenantId, Name = "drift" });
            ctx.Environments.Add(new Data.Environment
            {
                Id = envId, TenantId = tenantId, Name = "production",
            });
            ctx.KubernetesClusters.Add(new KubernetesCluster
            {
                Id = clusterId, TenantId = tenantId, EnvironmentId = envId,
                Name = "var-prod", ApiServerUrl = "https://k8s.example.invalid",
            });
            ctx.SaveChanges();
        }

        (await service.DeleteGroupAsync(tenantId, groupId)).Should().BeTrue();
        (await service.DeleteClusterAsync(tenantId, clusterId)).Should().BeTrue();

        using ApplicationDbContext check = db.CreateContext();
        (await check.Groups.AsNoTracking().AnyAsync(g => g.Id == groupId)).Should().BeFalse();
        (await check.KubernetesClusters.AsNoTracking().AnyAsync(c => c.Id == clusterId))
            .Should().BeFalse();
    }
}
