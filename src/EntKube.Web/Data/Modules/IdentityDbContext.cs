using Microsoft.EntityFrameworkCore;

namespace EntKube.Web.Data.Modules;

/// <summary>
/// The Identity module's view of the database — users, tenants, customers, roles, tokens, JIT grants and Keycloak.
///
/// <para>Exposes only the 17 tables Identity owns, plus the shared hubs below. A service
/// that takes this context cannot query another module's table, because the property is not
/// there. See <see cref="ModuleDbContext"/> for what this does and does not enforce.</para>
/// </summary>
public class IdentityDbContext(DbContextOptions<IdentityDbContext> options) : ModuleDbContext(options)
{
    public DbSet<IdentityBinding> IdentityBindings => Set<IdentityBinding>();
    public DbSet<ApplicationUser> ApplicationUsers => Set<ApplicationUser>();
    public DbSet<Tenant> Tenants => Set<Tenant>();
    public DbSet<TenantMembership> TenantMemberships => Set<TenantMembership>();
    public DbSet<TenantRole> TenantRoles => Set<TenantRole>();
    public DbSet<Customer> Customers => Set<Customer>();
    public DbSet<CustomerAccess> CustomerAccesses => Set<CustomerAccess>();
    public DbSet<CustomerEmailDomain> CustomerEmailDomains => Set<CustomerEmailDomain>();
    public DbSet<Group> Groups => Set<Group>();
    public DbSet<GroupMembership> GroupMemberships => Set<GroupMembership>();
    public DbSet<ExternalGroupMapping> ExternalGroupMappings => Set<ExternalGroupMapping>();
    public DbSet<ApiToken> ApiTokens => Set<ApiToken>();
    public DbSet<JitGrant> JitGrants => Set<JitGrant>();
    public DbSet<AuditEvent> AuditEvents => Set<AuditEvent>();
    public DbSet<KeycloakRealm> KeycloakRealms => Set<KeycloakRealm>();
    public DbSet<KeycloakTheme> KeycloakThemes => Set<KeycloakTheme>();
    public DbSet<KeycloakBackup> KeycloakBackups => Set<KeycloakBackup>();
    public DbSet<KeycloakComponentConfig> KeycloakComponentConfigs => Set<KeycloakComponentConfig>();

    // ---- Hubs owned by other modules -------------------------------------------------
    // The measurement in docs/decomposition.md §4.0 found 70 of 164 cross-module foreign
    // keys point at Tenant or Customer, and another 40 at App or KubernetesCluster. Making
    // those four readable here is what stops the boundary being merely annoying. They are
    // IQueryable rather than DbSet on purpose: readable, not writable, and not trackable.

    /// <summary>App, read-only: this module may look one up, never write it.</summary>
    public IQueryable<App> Apps => Set<App>().AsNoTracking();

    /// <summary>KubernetesCluster, read-only: this module may look one up, never write it.</summary>
    public IQueryable<KubernetesCluster> KubernetesClusters => Set<KubernetesCluster>().AsNoTracking();
}
