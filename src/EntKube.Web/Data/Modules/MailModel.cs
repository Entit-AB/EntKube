using Microsoft.EntityFrameworkCore;

namespace EntKube.Web.Data.Modules;

/// <summary>
/// Model configuration for the <see cref="EntKube.Web.Modules.Module.Mail"/> module —
/// The Stalwart mail stack.
///
/// <para>Moved verbatim out of <see cref="ApplicationDbContext.OnModelCreating"/>, which
/// had grown to 163 entity blocks in one method with nothing marking where one module's
/// tables ended and the next began. Which module owns a table is decided by
/// <see cref="EntKube.Web.Modules.ModuleMap"/>, and <c>ModuleBoundaryTests</c> keeps the
/// two from drifting apart.</para>
/// </summary>
internal static class MailModel
{
    internal static void Configure(ModelBuilder builder)
    {
        // Table names, stated rather than inferred.
        //
        // EF derives a table name from the DbSet property on whichever context declares it,
        // so "AlertIncident" became "AlertIncidents" only because ApplicationDbContext spells
        // that property in the plural. The per-module contexts do not declare each other's
        // sets, which means that without this block each of them maps the same entity to a
        // different table — and a query crossing a module boundary would hit a table that is
        // not there. Naming them here makes the schema a property of the model instead of a
        // property of C# property naming.
        builder.Entity<StalwartComponentConfig>().ToTable("StalwartComponentConfigs");
        builder.Entity<StalwartMailAccount>().ToTable("StalwartMailAccounts");
        builder.Entity<StalwartMailDomain>().ToTable("StalwartMailDomains");

        // Stalwart mail — the server's whole configuration is authored here and converged onto the
        // running deployment by regenerating its manifest and replaying a declarative apply plan.
        // Admin credentials and the LDAP bind password live in the vault, never in these columns.

        builder.Entity<StalwartComponentConfig>(entity =>
        {
            entity.HasKey(c => c.Id);
            entity.Property(c => c.DisplayName).HasMaxLength(200);
            entity.Property(c => c.Hostname).HasMaxLength(253).IsRequired();
            entity.Property(c => c.AdminHostname).HasMaxLength(253);
            entity.Property(c => c.AdminUsername).HasMaxLength(100).IsRequired();
            entity.Property(c => c.StorageSize).HasMaxLength(20).IsRequired();
            entity.Property(c => c.StorageClass).HasMaxLength(100);
            entity.Property(c => c.AuthMode).HasConversion<string>().HasMaxLength(20);
            entity.Property(c => c.LdapUrl).HasMaxLength(400);
            entity.Property(c => c.LdapBaseDn).HasMaxLength(400);
            entity.Property(c => c.LdapBindDn).HasMaxLength(400);
            entity.Property(c => c.LdapLoginFilter).HasMaxLength(600).IsRequired();
            entity.Property(c => c.LdapMailboxFilter).HasMaxLength(600).IsRequired();
            entity.Property(c => c.LdapMemberOfFilter).HasMaxLength(600).IsRequired();
            entity.Property(c => c.OidcIssuerUrl).HasMaxLength(400);
            entity.Property(c => c.OidcClaimUsername).HasMaxLength(100).IsRequired();
            entity.Property(c => c.OidcUsernameDomain).HasMaxLength(253);
            entity.Property(c => c.OidcClaimGroups).HasMaxLength(100);
            entity.Property(c => c.OidcRequireAudience).HasMaxLength(200);
            entity.Property(c => c.OidcRequireScopes).HasMaxLength(400).IsRequired();
            entity.Property(c => c.TlsMode).HasConversion<string>().HasMaxLength(20);
            entity.Property(c => c.AcmeChallenge).HasConversion<string>().HasMaxLength(20);
            entity.Property(c => c.ClusterIssuer).HasMaxLength(200);
            entity.Property(c => c.WebClusterIssuer).HasMaxLength(200);
            entity.Property(c => c.AcmeContact).HasMaxLength(320);
            entity.Property(c => c.ExposeMode).HasConversion<string>().HasMaxLength(20);
            entity.Property(c => c.LoadBalancerIp).HasMaxLength(100);
            entity.Property(c => c.LoadBalancerAnnotations).HasMaxLength(2000);
            entity.Property(c => c.ProxyTrustedNetworks).HasMaxLength(2000);
            // No foreign key: a realm deleted in Keycloak should leave the mail server's stored issuer
            // working rather than cascade a delete into a running configuration. Resolution simply
            // finds nothing and the preflight says so.
            entity.Property(c => c.RspamdHost).HasMaxLength(253);
            entity.Property(c => c.CoordinatorRedisHost).HasMaxLength(253);

            entity.HasOne(c => c.Tenant)
                .WithMany()
                .HasForeignKey(c => c.TenantId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(c => c.ClusterComponent)
                .WithMany()
                .HasForeignKey(c => c.ClusterComponentId)
                .IsRequired(false)
                .OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<StalwartMailDomain>(entity =>
        {
            entity.HasKey(d => d.Id);
            entity.Property(d => d.Name).HasMaxLength(253).IsRequired();
            entity.Property(d => d.Description).HasMaxLength(500);
            entity.Property(d => d.CatchAllLocalPart).HasMaxLength(100);
            entity.HasIndex(d => new { d.ConfigId, d.Name }).IsUnique();

            entity.HasOne(d => d.Config)
                .WithMany(c => c.Domains)
                .HasForeignKey(d => d.ConfigId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<StalwartMailAccount>(entity =>
        {
            entity.HasKey(a => a.Id);
            entity.Property(a => a.LocalPart).HasMaxLength(100).IsRequired();
            entity.Property(a => a.DisplayName).HasMaxLength(200);
            entity.Property(a => a.Description).HasMaxLength(500);
            entity.Property(a => a.Aliases).HasMaxLength(2000);
            entity.HasIndex(a => new { a.DomainId, a.LocalPart }).IsUnique();

            entity.HasOne(a => a.Config)
                .WithMany(c => c.Accounts)
                .HasForeignKey(a => a.ConfigId)
                .OnDelete(DeleteBehavior.Cascade);

            // Both FKs cascade from the same config root, which SQL Server rejects as multiple
            // cascade paths. The config path cascades; deleting a domain is blocked while it still
            // has mailboxes, and the service removes them first.
            entity.HasOne(a => a.Domain)
                .WithMany()
                .HasForeignKey(a => a.DomainId)
                .OnDelete(DeleteBehavior.Restrict);
        });
    }
}
