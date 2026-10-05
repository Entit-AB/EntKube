using Microsoft.EntityFrameworkCore;

namespace EntKube.Web.Data.Modules;

/// <summary>
/// Model configuration for the <see cref="EntKube.Web.Modules.Module.Identity"/> module —
/// Users, tenants, customers, roles, tokens, JIT grants and Keycloak.
///
/// <para>Moved verbatim out of <see cref="ApplicationDbContext.OnModelCreating"/>, which
/// had grown to 163 entity blocks in one method with nothing marking where one module's
/// tables ended and the next began. Which module owns a table is decided by
/// <see cref="EntKube.Web.Modules.ModuleMap"/>, and <c>ModuleBoundaryTests</c> keeps the
/// two from drifting apart.</para>
/// </summary>
internal static class IdentityModel
{
    internal static void Configure(ModelBuilder builder)
    {
        builder.Entity<IdentityBinding>().ToTable("IdentityBindings");
        // Table names, stated rather than inferred.
        //
        // EF derives a table name from the DbSet property on whichever context declares it,
        // so "AlertIncident" became "AlertIncidents" only because ApplicationDbContext spells
        // that property in the plural. The per-module contexts do not declare each other's
        // sets, which means that without this block each of them maps the same entity to a
        // different table — and a query crossing a module boundary would hit a table that is
        // not there. Naming them here makes the schema a property of the model instead of a
        // property of C# property naming.
        builder.Entity<ApiToken>().ToTable("ApiTokens");
        builder.Entity<AuditEvent>().ToTable("AuditEvents");
        builder.Entity<Customer>().ToTable("Customers");
        builder.Entity<CustomerAccess>().ToTable("CustomerAccesses");
        builder.Entity<CustomerEmailDomain>().ToTable("CustomerEmailDomains");
        builder.Entity<ExternalGroupMapping>().ToTable("ExternalGroupMappings");
        builder.Entity<Group>().ToTable("Groups");
        builder.Entity<GroupMembership>().ToTable("GroupMemberships");
        builder.Entity<JitGrant>().ToTable("JitGrants");
        builder.Entity<KeycloakBackup>().ToTable("KeycloakBackups");
        builder.Entity<KeycloakComponentConfig>().ToTable("KeycloakComponentConfigs");
        builder.Entity<KeycloakRealm>().ToTable("KeycloakRealms");
        builder.Entity<KeycloakTheme>().ToTable("KeycloakThemes");
        builder.Entity<Tenant>().ToTable("Tenants");
        builder.Entity<TenantMembership>().ToTable("TenantMemberships");
        builder.Entity<TenantRole>().ToTable("TenantRoles");

        // Tenant — the slug must be unique across all tenants since it's
        // used as the URL-safe identifier in routes and API calls.

        builder.Entity<Tenant>(entity =>
        {
            entity.HasKey(t => t.Id);
            entity.HasIndex(t => t.Slug).IsUnique();
            entity.Property(t => t.Name).HasMaxLength(200).IsRequired();
            entity.Property(t => t.Slug).HasMaxLength(100).IsRequired();
        });

        // ApiToken — the hash is the lookup key on every authenticated API request, so it
        // is uniquely indexed. Tokens cascade with their tenant: a deleted tenant must not
        // leave working credentials behind.

        builder.Entity<ApiToken>(entity =>
        {
            entity.HasKey(t => t.Id);
            entity.HasIndex(t => t.TokenHash).IsUnique();
            entity.HasIndex(t => t.TenantId);
            entity.Property(t => t.Name).HasMaxLength(200).IsRequired();
            entity.Property(t => t.TokenHash).HasMaxLength(64).IsRequired();
            entity.Property(t => t.DisplayPrefix).HasMaxLength(32).IsRequired();
            entity.Property(t => t.Scopes).HasMaxLength(500);
            entity.Property(t => t.CreatedBy).HasMaxLength(256);
            entity.HasOne(t => t.Tenant)
                  .WithMany()
                  .HasForeignKey(t => t.TenantId)
                  .OnDelete(DeleteBehavior.Cascade);
        });

        // JitGrant — like ApiToken, only the hash of the credential is stored and the row
        // outlives the credential so the trail survives. Unlike ApiToken, everything here
        // cascades: a deleted tenant, customer or app must not leave a grant that still names
        // a namespace somebody can reach. The cluster and user relationships are Restrict
        // instead — deleting either out from under a live grant should fail loudly rather than
        // silently drop the record of access that was given.

        builder.Entity<JitGrant>(entity =>
        {
            entity.HasKey(g => g.Id);

            // Every proxied request looks a grant up by token hash, so it is the hot path.
            // Filtered unique would be better (the column is null until approval), but only
            // two of the three providers support it — a plain index keeps the model portable
            // and uniqueness is guaranteed by the 256 bits of CSPRNG behind the value anyway.
            entity.HasIndex(g => g.TokenHash);
            entity.HasIndex(g => new { g.TenantId, g.RequestedAt });
            entity.HasIndex(g => g.UserId);

            // The reaper sweeps on this: approved, not yet torn down, past its expiry.
            entity.HasIndex(g => new { g.ExpiresAt, g.TornDownAt });

            entity.Property(g => g.Namespace).HasMaxLength(63).IsRequired();
            entity.Property(g => g.UserId).HasMaxLength(450).IsRequired();
            entity.Property(g => g.Reason).HasMaxLength(1000).IsRequired();
            entity.Property(g => g.TicketRef).HasMaxLength(200);
            entity.Property(g => g.RequestedBy).HasMaxLength(256).IsRequired();
            entity.Property(g => g.ApprovedBy).HasMaxLength(256);
            entity.Property(g => g.DeniedBy).HasMaxLength(256);
            entity.Property(g => g.RevokedBy).HasMaxLength(256);
            entity.Property(g => g.RevokeReason).HasMaxLength(1000);
            entity.Property(g => g.ServiceAccountName).HasMaxLength(63);
            entity.Property(g => g.TokenHash).HasMaxLength(64);
            entity.Property(g => g.DisplayPrefix).HasMaxLength(32);

            entity.HasOne(g => g.Tenant).WithMany()
                  .HasForeignKey(g => g.TenantId).OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(g => g.Customer).WithMany()
                  .HasForeignKey(g => g.CustomerId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(g => g.App).WithMany()
                  .HasForeignKey(g => g.AppId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(g => g.Environment).WithMany()
                  .HasForeignKey(g => g.EnvironmentId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(g => g.KubernetesCluster).WithMany()
                  .HasForeignKey(g => g.KubernetesClusterId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(g => g.User).WithMany()
                  .HasForeignKey(g => g.UserId).OnDelete(DeleteBehavior.Restrict);
        });

        builder.Entity<CustomerEmailDomain>(entity =>
        {
            entity.HasKey(d => d.Id);

            // One claim per domain per tenant. Two customers claiming the same domain would
            // make a sender's customer depend on which row a query returned first, and the
            // same person would land in different queues on different days.
            entity.HasIndex(d => new { d.TenantId, d.Domain }).IsUnique();
            entity.HasIndex(d => d.CustomerId);

            entity.Property(d => d.Domain).HasMaxLength(253);
            entity.Property(d => d.Notes).HasMaxLength(500);
            entity.Property(d => d.AddedBy).HasMaxLength(256);

            entity.HasOne(d => d.Tenant)
                  .WithMany()
                  .HasForeignKey(d => d.TenantId)
                  .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(d => d.Customer)
                  .WithMany()
                  .HasForeignKey(d => d.CustomerId)
                  .OnDelete(DeleteBehavior.Cascade);
        });

        // ExternalGroupMapping — a group may grant access to several tenants, but only one
        // role per tenant: two roles for the same group in the same tenant would make the
        // resulting access depend on evaluation order.

        builder.Entity<ExternalGroupMapping>(entity =>
        {
            entity.HasKey(m => m.Id);
            entity.HasIndex(m => new { m.ExternalGroup, m.TenantId }).IsUnique();
            entity.Property(m => m.ExternalGroup).HasMaxLength(400).IsRequired();
            entity.Property(m => m.CreatedBy).HasMaxLength(256);
            entity.HasOne(m => m.Tenant)
                  .WithMany()
                  .HasForeignKey(m => m.TenantId)
                  .OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(m => m.Role)
                  .WithMany()
                  .HasForeignKey(m => m.RoleId)
                  // Restrict, not Cascade: deleting a role that a group mapping depends on
                  // must be a deliberate act, not a silent revocation of everyone's access.
                  .OnDelete(DeleteBehavior.Restrict);
        });

        // TenantRole — each role name must be unique within its tenant.
        // A tenant owns its roles; deleting a tenant cascades to its roles.

        builder.Entity<TenantRole>(entity =>
        {
            entity.HasKey(r => r.Id);
            entity.HasIndex(r => new { r.TenantId, r.Name }).IsUnique();
            entity.Property(r => r.Name).HasMaxLength(100).IsRequired();

            entity.HasOne(r => r.Tenant)
                .WithMany(t => t.Roles)
                .HasForeignKey(r => r.TenantId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // TenantMembership — composite key on (UserId, TenantId) ensures
        // a user can only have one membership per tenant. The role FK tells
        // us what they can do in that tenant.

        builder.Entity<TenantMembership>(entity =>
        {
            entity.HasKey(m => new { m.UserId, m.TenantId });

            entity.HasOne(m => m.User)
                .WithMany()
                .HasForeignKey(m => m.UserId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(m => m.Tenant)
                .WithMany(t => t.Memberships)
                .HasForeignKey(m => m.TenantId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(m => m.Role)
                .WithMany(r => r.Memberships)
                .HasForeignKey(m => m.RoleId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        // Group — belongs to a tenant. Name should be unique within a tenant.

        builder.Entity<Group>(entity =>
        {
            entity.HasKey(g => g.Id);
            entity.HasIndex(g => new { g.TenantId, g.Name }).IsUnique();
            entity.Property(g => g.Name).HasMaxLength(200).IsRequired();

            entity.HasOne(g => g.Tenant)
                .WithMany(t => t.Groups)
                .HasForeignKey(g => g.TenantId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // GroupMembership — composite key on (UserId, GroupId) prevents
        // a user from being added to the same group twice.

        builder.Entity<GroupMembership>(entity =>
        {
            entity.HasKey(gm => new { gm.UserId, gm.GroupId });

            entity.HasOne(gm => gm.User)
                .WithMany()
                .HasForeignKey(gm => gm.UserId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(gm => gm.Group)
                .WithMany(g => g.Memberships)
                .HasForeignKey(gm => gm.GroupId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // Customer — belongs to a tenant. Name must be unique within a tenant.

        builder.Entity<Customer>(entity =>
        {
            entity.HasKey(c => c.Id);
            entity.HasIndex(c => new { c.TenantId, c.Name }).IsUnique();
            entity.Property(c => c.Name).HasMaxLength(200).IsRequired();

            entity.HasOne(c => c.Tenant)
                .WithMany(t => t.Customers)
                .HasForeignKey(c => c.TenantId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // CustomerAccess — composite key on (UserId, CustomerId) ensures
        // a user gets exactly one access entry per customer. The Role enum
        // controls what they can do (Viewer, Operator, Admin).

        builder.Entity<CustomerAccess>(entity =>
        {
            entity.HasKey(ca => new { ca.UserId, ca.CustomerId });

            entity.Property(ca => ca.Role)
                .HasConversion<string>()
                .HasMaxLength(20);

            entity.HasOne(ca => ca.User)
                .WithMany()
                .HasForeignKey(ca => ca.UserId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(ca => ca.Customer)
                .WithMany()
                .HasForeignKey(ca => ca.CustomerId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // KeycloakComponentConfig — links a detected Keycloak ClusterComponent to its
        // backing CNPG database. DB credentials and the admin password are stored as
        // component vault secrets (ComponentId = ClusterComponentId) and synced to K8s.

        builder.Entity<KeycloakComponentConfig>(entity =>
        {
            entity.HasKey(c => c.Id);
            entity.Property(c => c.AdminUsername).HasMaxLength(100).IsRequired();
            entity.Property(c => c.AdminUrl).HasMaxLength(500);
            entity.Property(c => c.DisplayName).HasMaxLength(200);

            entity.HasOne(c => c.Tenant)
                .WithMany()
                .HasForeignKey(c => c.TenantId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(c => c.ClusterComponent)
                .WithMany()
                .HasForeignKey(c => c.ClusterComponentId)
                .IsRequired(false)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(c => c.CnpgDatabase)
                .WithMany()
                .HasForeignKey(c => c.CnpgDatabaseId)
                .OnDelete(DeleteBehavior.SetNull);

            entity.HasOne(c => c.RegisteredPostgresDatabase)
                .WithMany()
                .HasForeignKey(c => c.RegisteredPostgresDatabaseId)
                .OnDelete(DeleteBehavior.SetNull);
        });

        // KeycloakTheme — a named CSS theme bundle for a Keycloak instance.
        // Name must be unique within the instance. CSS is stored separately in
        // the vault (keyed by theme Id). Multiple realms can reference the same theme.

        builder.Entity<KeycloakTheme>(entity =>
        {
            entity.HasKey(t => t.Id);
            entity.Property(t => t.Name).HasMaxLength(200).IsRequired();
            entity.Property(t => t.LoginTheme).HasMaxLength(100);
            entity.Property(t => t.AccountTheme).HasMaxLength(100);

            entity.HasIndex(t => new { t.KeycloakComponentConfigId, t.Name }).IsUnique();

            entity.HasOne(t => t.ComponentConfig)
                .WithMany()
                .HasForeignKey(t => t.KeycloakComponentConfigId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // KeycloakRealm — a realm managed via a component config. RealmName must be
        // unique within a config (Keycloak enforces this globally per server).

        builder.Entity<KeycloakRealm>(entity =>
        {
            entity.HasKey(r => r.Id);
            entity.Property(r => r.RealmName).HasMaxLength(100).IsRequired();
            entity.Property(r => r.DisplayName).HasMaxLength(200).IsRequired();
            entity.Property(r => r.BackupSchedule).HasMaxLength(100);

            entity.HasIndex(r => new { r.KeycloakComponentConfigId, r.RealmName }).IsUnique();

            entity.HasOne(r => r.ComponentConfig)
                .WithMany(c => c.Realms)
                .HasForeignKey(r => r.KeycloakComponentConfigId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(r => r.StorageLink)
                .WithMany()
                .HasForeignKey(r => r.StorageLinkId)
                .OnDelete(DeleteBehavior.SetNull);

            entity.HasOne(r => r.LinkedApp)
                .WithMany()
                .HasForeignKey(r => r.LinkedAppId)
                .OnDelete(DeleteBehavior.SetNull);

            entity.HasOne(r => r.Theme)
                .WithMany(t => t.Realms)
                .HasForeignKey(r => r.KeycloakThemeId)
                .OnDelete(DeleteBehavior.SetNull);
        });

        // KeycloakBackup — realm JSON snapshots stored in S3.

        builder.Entity<KeycloakBackup>(entity =>
        {
            entity.HasKey(b => b.Id);
            entity.Property(b => b.ObjectKey).HasMaxLength(1024).IsRequired();
            entity.Property(b => b.RealmName).HasMaxLength(100).IsRequired();

            entity.HasOne(b => b.Realm)
                .WithMany(r => r.Backups)
                .HasForeignKey(b => b.KeycloakRealmId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(b => b.StorageLink)
                .WithMany()
                .HasForeignKey(b => b.StorageLinkId)
                .OnDelete(DeleteBehavior.SetNull);
        });

        // HarborComponentConfig — links an installed Harbor ClusterComponent to its
        // CNPG database and S3 storage link. One config per Harbor install.
        // Admin password and S3/DB credentials are stored in the vault (not here).

        builder.Entity<AuditEvent>(entity =>
        {
            entity.HasKey(a => a.Id);
            entity.Property(a => a.Action).HasMaxLength(100).IsRequired();
            entity.Property(a => a.ResourceKind).HasMaxLength(100).IsRequired();
            entity.Property(a => a.ResourceName).HasMaxLength(253);
            entity.Property(a => a.PerformedBy).HasMaxLength(256);
            entity.Property(a => a.Details).HasMaxLength(1000);

            entity.HasIndex(a => a.DeploymentId);
            entity.HasIndex(a => a.OccurredAt);

            entity.HasOne(a => a.Deployment)
                .WithMany()
                .HasForeignKey(a => a.DeploymentId)
                .OnDelete(DeleteBehavior.Cascade);
        });
    }
}
