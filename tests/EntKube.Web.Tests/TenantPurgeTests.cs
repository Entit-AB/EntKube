using EntKube.Web.Data;
using EntKube.Web.Services;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;

namespace EntKube.Web.Tests;

/// <summary>
/// Removing a tenant "from the app" must purge every app-side row tied to it — across the
/// tricky Restrict-FK boundaries (cluster↔environment, deployment↔env/cluster, membership↔role,
/// connectivity↔peer-app, VPN↔cluster, kyverno↔env) AND the telemetry/dashboard tables that
/// carry a TenantId with no FK — without disturbing any other tenant or shared users. It is a
/// pure database purge; no infrastructure is touched (there is nothing to touch in a unit test,
/// which is precisely the point — the delete path calls no cluster/helm/vault-sync code).
///
/// Backed by relational SQLite so real ON DELETE CASCADE / NO ACTION semantics are exercised —
/// a naive Tenants.Remove() would throw on the Restrict boundaries and orphan the FK-less tables.
/// </summary>
public class TenantPurgeTests : IDisposable
{
    private readonly InterceptingTestDb db;
    private readonly TenantService tenants;

    public TenantPurgeTests()
    {
        db = new InterceptingTestDb(new byte[32]);
        tenants = new TenantService(db.Factory, db.CreateVaultService());
    }

    [Fact]
    public async Task DeleteTenant_PurgesEntireSubtree_AcrossRestrictBoundaries_AndOrphanTables()
    {
        Guid t1 = await SeedFullTenantAsync("Acme", "acme");
        Guid t2 = await SeedFullTenantAsync("Other", "other");

        bool ok = await tenants.DeleteTenantAsync(t1);
        ok.Should().BeTrue();

        await using ApplicationDbContext ctx = db.CreateContext();

        // ── The target tenant is gone, top to bottom ──
        (await ctx.Tenants.CountAsync(x => x.Id == t1)).Should().Be(0);
        (await ctx.Environments.CountAsync(e => e.TenantId == t1)).Should().Be(0);
        (await ctx.Customers.CountAsync(c => c.TenantId == t1)).Should().Be(0);
        (await ctx.Apps.CountAsync(a => a.Customer.TenantId == t1)).Should().Be(0);
        (await ctx.AppEnvironments.CountAsync(ae => ae.App.Customer.TenantId == t1)).Should().Be(0);
        (await ctx.AppDeployments.CountAsync(d => d.App.Customer.TenantId == t1)).Should().Be(0);
        (await ctx.KubernetesClusters.CountAsync(c => c.TenantId == t1)).Should().Be(0);
        (await ctx.ClusterComponents.CountAsync(cc => cc.Cluster.TenantId == t1)).Should().Be(0);
        (await ctx.ConnectivityRules.CountAsync(r => r.App.Customer.TenantId == t1)).Should().Be(0);
        (await ctx.KyvernoPolicies.CountAsync(k => k.TenantId == t1)).Should().Be(0);
        (await ctx.VpnTunnels.CountAsync(v => v.TenantId == t1)).Should().Be(0);
        // VpnLocalEndpoint has no back-nav; both tenants seed one, so t1's is gone iff 1 remains.
        (await ctx.VpnLocalEndpoints.CountAsync()).Should().Be(1);
        (await ctx.TenantMemberships.CountAsync(m => m.TenantId == t1)).Should().Be(0);
        (await ctx.TenantRoles.CountAsync(r => r.TenantId == t1)).Should().Be(0);
        (await ctx.SecretVaults.CountAsync(v => v.TenantId == t1)).Should().Be(0);
        (await ctx.VaultSecrets.CountAsync(s => s.Vault.TenantId == t1)).Should().Be(0);

        // ── FK-less orphan tables purged explicitly ──
        (await ctx.Dashboards.CountAsync(x => x.TenantId == t1)).Should().Be(0);
        (await ctx.TelemetryAlertRules.CountAsync(x => x.TenantId == t1)).Should().Be(0);
        (await ctx.RumSites.CountAsync(x => x.TenantId == t1)).Should().Be(0);

        // ── The other tenant is completely untouched ──
        (await ctx.Tenants.CountAsync(x => x.Id == t2)).Should().Be(1);
        (await ctx.Apps.CountAsync(a => a.Customer.TenantId == t2)).Should().Be(2);
        (await ctx.KubernetesClusters.CountAsync(c => c.TenantId == t2)).Should().Be(1);
        (await ctx.Dashboards.CountAsync(x => x.TenantId == t2)).Should().Be(1);

        // ── Shared (non-tenant-scoped) users survive; only their membership was removed ──
        (await ctx.Users.CountAsync()).Should().Be(2);
    }

    [Fact]
    public async Task DeleteTenant_ReturnsFalse_WhenTenantMissing()
    {
        (await tenants.DeleteTenantAsync(Guid.NewGuid())).Should().BeFalse();
    }

    [Fact]
    public async Task GetTenantPurgePreview_CountsTheSubtree()
    {
        Guid t1 = await SeedFullTenantAsync("Acme", "acme");

        TenantPurgePreview? preview = await tenants.GetTenantPurgePreviewAsync(t1);

        preview.Should().NotBeNull();
        preview!.TenantName.Should().Be("Acme");
        preview.Clusters.Should().Be(1);
        preview.Components.Should().Be(1);
        preview.Apps.Should().Be(2);
        preview.Deployments.Should().Be(1);
        preview.Environments.Should().Be(1);
        preview.Customers.Should().Be(1);
        preview.Secrets.Should().Be(1);
        preview.Dashboards.Should().Be(1);
        preview.Total.Should().BeGreaterThan(0);
    }

    /// <summary>
    /// Seeds a tenant with at least one row on every Restrict-FK boundary and every FK-less
    /// orphan table, so a successful purge proves the ordering and the orphan cleanup.
    /// </summary>
    private async Task<Guid> SeedFullTenantAsync(string name, string slug)
    {
        await using ApplicationDbContext ctx = db.CreateContext();

        Tenant tenant = new() { Id = Guid.NewGuid(), Name = name, Slug = slug };
        Data.Environment env = new() { Id = Guid.NewGuid(), TenantId = tenant.Id, Name = "prod" };
        Customer customer = new() { Id = Guid.NewGuid(), TenantId = tenant.Id, Name = "Cust" };
        App app1 = new() { Id = Guid.NewGuid(), CustomerId = customer.Id, Name = "app1" };
        App app2 = new() { Id = Guid.NewGuid(), CustomerId = customer.Id, Name = "app2" };

        KubernetesCluster cluster = new()
        {
            Id = Guid.NewGuid(), TenantId = tenant.Id, EnvironmentId = env.Id,
            Name = "k1", ApiServerUrl = "https://k8s.example.com"
        };
        ClusterComponent component = new()
        {
            Id = Guid.NewGuid(), ClusterId = cluster.Id, Name = "traefik", ComponentType = "ingress"
        };

        AppEnvironment appEnv = new() { AppId = app1.Id, EnvironmentId = env.Id };
        AppDeployment deployment = new()
        {
            Id = Guid.NewGuid(), AppId = app1.Id, Name = "app1-prod",
            EnvironmentId = env.Id, ClusterId = cluster.Id, Namespace = "app1"
        };

        // Restrict boundary: a rule on app1 that references app2 as its peer.
        ConnectivityRule rule = new()
        {
            Id = Guid.NewGuid(), AppId = app1.Id, EnvironmentId = env.Id, PeerAppId = app2.Id
        };
        KyvernoPolicy kyverno = new() { Id = Guid.NewGuid(), TenantId = tenant.Id, EnvironmentId = env.Id };

        // Restrict boundary: a VPN local endpoint pinned to the cluster.
        VpnTunnel tunnel = new() { Id = Guid.NewGuid(), TenantId = tenant.Id, Name = "vpn1" };
        VpnLocalEndpoint endpoint = new()
        {
            Id = Guid.NewGuid(), VpnTunnelId = tunnel.Id, ClusterId = cluster.Id, Subnets = "10.0.0.0/24"
        };

        // Restrict boundary: membership → role.
        TenantRole role = new() { Id = Guid.NewGuid(), TenantId = tenant.Id, Name = "Administrator" };
        ApplicationUser user = new()
        {
            Id = Guid.NewGuid().ToString(),
            UserName = $"admin@{slug}.com", NormalizedUserName = $"ADMIN@{slug.ToUpperInvariant()}.COM",
            Email = $"admin@{slug}.com", NormalizedEmail = $"ADMIN@{slug.ToUpperInvariant()}.COM",
            EmailConfirmed = true, SecurityStamp = Guid.NewGuid().ToString()
        };
        TenantMembership membership = new() { UserId = user.Id, TenantId = tenant.Id, RoleId = role.Id };

        SecretVault vault = new()
        {
            Id = Guid.NewGuid(), TenantId = tenant.Id,
            EncryptedDataKey = [1, 2, 3], Nonce = new byte[12]
        };
        VaultSecret secret = new()
        {
            Id = Guid.NewGuid(), VaultId = vault.Id, Name = "db-password",
            EncryptedValue = [4, 5, 6], Nonce = new byte[12]
        };

        // FK-less orphan tables (TenantId only).
        Dashboard dashboard = new() { Id = Guid.NewGuid(), TenantId = tenant.Id, Name = "Overview" };
        TelemetryAlertRule alertRule = new() { Id = Guid.NewGuid(), TenantId = tenant.Id, Name = "High errors" };
        RumSite rumSite = new()
        {
            Id = Guid.NewGuid(), TenantId = tenant.Id, Name = "web",
            PublicKey = Guid.NewGuid().ToString("N")
        };

        ctx.Tenants.Add(tenant);
        ctx.Environments.Add(env);
        ctx.Customers.Add(customer);
        ctx.Apps.AddRange(app1, app2);
        ctx.KubernetesClusters.Add(cluster);
        ctx.ClusterComponents.Add(component);
        ctx.AppEnvironments.Add(appEnv);
        ctx.AppDeployments.Add(deployment);
        ctx.ConnectivityRules.Add(rule);
        ctx.KyvernoPolicies.Add(kyverno);
        ctx.VpnTunnels.Add(tunnel);
        ctx.VpnLocalEndpoints.Add(endpoint);
        ctx.TenantRoles.Add(role);
        ctx.Users.Add(user);
        ctx.TenantMemberships.Add(membership);
        ctx.SecretVaults.Add(vault);
        ctx.VaultSecrets.Add(secret);
        ctx.Dashboards.Add(dashboard);
        ctx.TelemetryAlertRules.Add(alertRule);
        ctx.RumSites.Add(rumSite);

        await ctx.SaveChangesAsync();
        return tenant.Id;
    }

    public void Dispose() => db.Dispose();
}
