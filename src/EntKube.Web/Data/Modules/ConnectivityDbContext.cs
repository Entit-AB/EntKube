using Microsoft.EntityFrameworkCore;

namespace EntKube.Web.Data.Modules;

/// <summary>
/// The Connectivity module's view of the database — routes, network policy, service mesh, mTLS and VPN.
///
/// <para>Exposes only the 13 tables Connectivity owns, plus the shared hubs below. A service
/// that takes this context cannot query another module's table, because the property is not
/// there. See <see cref="ModuleDbContext"/> for what this does and does not enforce.</para>
/// </summary>
public class ConnectivityDbContext(DbContextOptions<ConnectivityDbContext> options) : ModuleDbContext(options)
{
    public DbSet<AppRoute> AppRoutes => Set<AppRoute>();
    public DbSet<AppL4Route> AppL4Routes => Set<AppL4Route>();
    public DbSet<AppNetworkPolicy> AppNetworkPolicies => Set<AppNetworkPolicy>();
    public DbSet<ConnectivityRule> ConnectivityRules => Set<ConnectivityRule>();
    public DbSet<ExternalRoute> ExternalRoutes => Set<ExternalRoute>();
    public DbSet<ExternalRouteHealthHistory> ExternalRouteHealthHistories => Set<ExternalRouteHealthHistory>();
    public DbSet<MeshMtlsPolicy> MeshMtlsPolicies => Set<MeshMtlsPolicy>();
    public DbSet<OutboundMtlsCredential> OutboundMtlsCredentials => Set<OutboundMtlsCredential>();
    public DbSet<ClientCaBundle> ClientCaBundles => Set<ClientCaBundle>();
    public DbSet<ClientCaCertificate> ClientCaCertificates => Set<ClientCaCertificate>();
    public DbSet<VpnTunnel> VpnTunnels => Set<VpnTunnel>();
    public DbSet<VpnLocalEndpoint> VpnLocalEndpoints => Set<VpnLocalEndpoint>();
    public DbSet<VpnRemoteEndpoint> VpnRemoteEndpoints => Set<VpnRemoteEndpoint>();

    // ---- Hubs owned by other modules -------------------------------------------------
    // The measurement in docs/decomposition.md §4.0 found 70 of 164 cross-module foreign
    // keys point at Tenant or Customer, and another 40 at App or KubernetesCluster. Making
    // those four readable here is what stops the boundary being merely annoying. They are
    // IQueryable rather than DbSet on purpose: readable, not writable, and not trackable.

    /// <summary>Tenant, read-only: this module may look one up, never write it.</summary>
    public IQueryable<Tenant> Tenants => Set<Tenant>().AsNoTracking();

    /// <summary>Customer, read-only: this module may look one up, never write it.</summary>
    public IQueryable<Customer> Customers => Set<Customer>().AsNoTracking();

    /// <summary>App, read-only: this module may look one up, never write it.</summary>
    public IQueryable<App> Apps => Set<App>().AsNoTracking();

    /// <summary>KubernetesCluster, read-only: this module may look one up, never write it.</summary>
    public IQueryable<KubernetesCluster> KubernetesClusters => Set<KubernetesCluster>().AsNoTracking();
}
