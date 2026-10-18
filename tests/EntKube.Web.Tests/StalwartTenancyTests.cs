using EntKube.Web.Data;
using EntKube.Web.Services;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace EntKube.Web.Tests;

/// <summary>
/// Whose mail server a method acts on.
///
/// <para>Only <see cref="StalwartComponentConfig"/> carries a TenantId; the domains and
/// mailboxes under it carry a ConfigId, so a row is reached as <c>x.Config.TenantId</c>.
/// What is behind these rows is a running mail server: the authored state is applied to it,
/// so a domain written here is a domain that server accepts and signs mail for, and a
/// mailbox written here is an address it delivers to.</para>
/// </summary>
public class StalwartTenancyTests : IDisposable
{
    private readonly SqliteConnection connection;
    private readonly ApplicationDbContext db;
    private readonly StalwartService sut;

    private readonly Guid tenantId = Guid.NewGuid();
    private readonly Guid configId = Guid.NewGuid();
    private readonly Guid domainId = Guid.NewGuid();

    private readonly Guid otherTenantId = Guid.NewGuid();
    private readonly Guid theirConfigId = Guid.NewGuid();
    private readonly Guid theirDomainId = Guid.NewGuid();
    private readonly Guid theirAccountId = Guid.NewGuid();

    public StalwartTenancyTests()
    {
        connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();

        db = new ApplicationDbContext(
            new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(connection).Options);
        db.Database.EnsureCreated();

        db.Tenants.AddRange(
            new Tenant { Id = tenantId, Name = "ENTIT", Slug = $"entit-{tenantId:N}" },
            new Tenant { Id = otherTenantId, Name = "Annan", Slug = $"annan-{otherTenantId:N}" });

        db.StalwartComponentConfigs.AddRange(
            new StalwartComponentConfig
            {
                Id = configId, TenantId = tenantId, Hostname = "mail.entit.se",
            },
            new StalwartComponentConfig
            {
                Id = theirConfigId, TenantId = otherTenantId, Hostname = "mail.annan.se",
            });

        db.StalwartMailDomains.AddRange(
            new StalwartMailDomain { Id = domainId, ConfigId = configId, Name = "entit.se", IsPrimary = true },
            new StalwartMailDomain
            {
                Id = theirDomainId, ConfigId = theirConfigId, Name = "annan.se", IsPrimary = true,
            });

        db.StalwartMailAccounts.Add(new StalwartMailAccount
        {
            Id = theirAccountId,
            ConfigId = theirConfigId,
            DomainId = theirDomainId,
            LocalPart = "anna",
            Aliases = "info",
        });
        db.SaveChanges();

        sut = new StalwartService(
            new TestDbContextFactory(connection), null!, null!, null!, null!, null!, null!, null!,
            NullLogger<StalwartService>.Instance);
    }

    public void Dispose()
    {
        db.Dispose();
        connection.Dispose();
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task Another_tenants_mail_server_is_not_readable()
    {
        (await sut.GetConfigAsync(tenantId, configId)).Should().NotBeNull();
        (await sut.GetConfigAsync(tenantId, theirConfigId)).Should().BeNull();
    }

    /// <summary>
    /// A domain is what the server accepts and signs mail for, so adding one to somebody
    /// else's server puts a name we control into their mail flow.
    /// </summary>
    [Fact]
    public async Task A_domain_cannot_be_added_to_another_tenants_server()
    {
        Func<Task> act = () => sut.AddDomainAsync(tenantId, theirConfigId, "ours.example", true);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("Mail server not found, or it is not this tenant's.");

        (await db.StalwartMailDomains.AsNoTracking().CountAsync(d => d.ConfigId == theirConfigId))
            .Should().Be(1);
    }

    /// <summary>
    /// <b>Two separate reads off the entity.</b> The detached Update writes every column of
    /// the row the Id names, and the unset-the-other-primaries sweep runs over whatever
    /// ConfigId the entity carries — so an IsPrimary=true update naming their server cleared
    /// <i>their</i> primary domain, which is what Stalwart appends to a bare login.
    /// </summary>
    [Fact]
    public async Task Another_tenants_domain_cannot_be_rewritten_or_unset_as_primary()
    {
        Func<Task> act = () => sut.UpdateDomainAsync(tenantId, new StalwartMailDomain
        {
            Id = theirDomainId,
            ConfigId = theirConfigId,
            Name = "annan.se",
            IsPrimary = true,
        });

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("Domain not found, or it is not this tenant's.");

        StalwartMailDomain stored = await db.StalwartMailDomains.AsNoTracking()
            .FirstAsync(d => d.Id == theirDomainId);

        stored.IsPrimary.Should().BeTrue();
        stored.ConfigId.Should().Be(theirConfigId);
    }

    /// <summary>
    /// The other direction: an authorized edit of our own domain that names their server.
    /// Without the pinning the row would move onto their server, and the primary sweep would
    /// run there.
    /// </summary>
    [Fact]
    public async Task Our_own_domain_cannot_be_moved_onto_another_tenants_server()
    {
        StalwartMailDomain ours = await db.StalwartMailDomains.AsNoTracking()
            .FirstAsync(d => d.Id == domainId);

        ours.ConfigId = theirConfigId;
        ours.Description = "edited";
        await sut.UpdateDomainAsync(tenantId, ours);

        StalwartMailDomain stored = await db.StalwartMailDomains.AsNoTracking()
            .FirstAsync(d => d.Id == domainId);

        stored.Description.Should().Be("edited");
        stored.ConfigId.Should().Be(configId);

        // And their primary was not cleared by our sweep.
        (await db.StalwartMailDomains.AsNoTracking().FirstAsync(d => d.Id == theirDomainId))
            .IsPrimary.Should().BeTrue();
    }

    /// <summary>
    /// A mailbox is an address the server delivers to, and Aliases is what <em>else</em>
    /// delivers to it.
    /// </summary>
    [Fact]
    public async Task A_mailbox_cannot_be_created_on_another_tenants_server()
    {
        Func<Task> act = () => sut.AddAccountAsync(tenantId, new StalwartMailAccount
        {
            ConfigId = theirConfigId, DomainId = theirDomainId, LocalPart = "ours",
        });

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("Mail server not found, or it is not this tenant's.");

        (await db.StalwartMailAccounts.AsNoTracking().CountAsync(a => a.ConfigId == theirConfigId))
            .Should().Be(1);
    }

    /// <summary>
    /// An address is a local part <i>and</i> a domain, so the domain is checked against the
    /// server as well — otherwise a mailbox on our server could claim a name under theirs.
    /// </summary>
    [Fact]
    public async Task A_mailbox_cannot_claim_a_domain_from_another_server()
    {
        Func<Task> act = () => sut.AddAccountAsync(tenantId, new StalwartMailAccount
        {
            ConfigId = configId, DomainId = theirDomainId, LocalPart = "ours",
        });

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("That domain is not on this mail server.");
    }

    [Fact]
    public async Task Another_tenants_mailbox_cannot_be_rewritten_or_deleted()
    {
        Func<Task> act = () => sut.UpdateAccountAsync(tenantId, new StalwartMailAccount
        {
            Id = theirAccountId,
            ConfigId = theirConfigId,
            DomainId = theirDomainId,
            LocalPart = "anna",
            Aliases = "info,postmaster,abuse",
        });

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("Mailbox not found, or it is not this tenant's.");

        await sut.DeleteAccountAsync(tenantId, theirAccountId);

        StalwartMailAccount stored = await db.StalwartMailAccounts.AsNoTracking()
            .FirstAsync(a => a.Id == theirAccountId);

        stored.Aliases.Should().Be("info");
    }

    /// <summary>
    /// The import creates a mailbox for every LDAP user with an address in one of the
    /// server's domains — so by config id alone it populated somebody else's server.
    /// </summary>
    [Fact]
    public async Task Mailboxes_cannot_be_imported_into_another_tenants_server()
    {
        Func<Task> act = () => sut.ImportMailboxesFromLdapAsync(tenantId, theirConfigId);

        // ⚠️ The message matters. This method also refuses a server with no LDAP directory
        // linked, and their fixture has none — so asserting THAT message would pass against a
        // build with the tenant predicate deleted. The two refusals say different things.
        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("Mail server not found, or it is not this tenant's.");

        (await db.StalwartMailAccounts.AsNoTracking().CountAsync(a => a.ConfigId == theirConfigId))
            .Should().Be(1);
    }
    /// <summary>
    /// ⚠️ Their <i>other</i> domain, deliberately with no mailboxes on it. DeleteDomainAsync
    /// refuses a domain that still has mailboxes, and their first one does — so a test using
    /// that domain would be refused for the wrong reason and pass against a build with the
    /// tenant predicate deleted.
    /// </summary>
    [Fact]
    public async Task Another_tenants_domain_cannot_be_deleted()
    {
        Guid theirSpareDomain = Guid.NewGuid();
        db.StalwartMailDomains.Add(new StalwartMailDomain
        {
            Id = theirSpareDomain, ConfigId = theirConfigId, Name = "spare.annan.se",
        });
        db.SaveChanges();

        await sut.DeleteDomainAsync(tenantId, theirSpareDomain);

        (await db.StalwartMailDomains.AsNoTracking().AnyAsync(d => d.Id == theirSpareDomain))
            .Should().BeTrue();

        // Ours with no mailboxes does go, or the survival above proves nothing.
        Guid ourSpareDomain = Guid.NewGuid();
        db.StalwartMailDomains.Add(new StalwartMailDomain
        {
            Id = ourSpareDomain, ConfigId = configId, Name = "spare.entit.se",
        });
        db.SaveChanges();

        await sut.DeleteDomainAsync(tenantId, ourSpareDomain);

        (await db.StalwartMailDomains.AsNoTracking().AnyAsync(d => d.Id == ourSpareDomain))
            .Should().BeFalse();
    }

    /// <summary>
    /// The plant, for mailboxes. An authorized edit of our own mailbox that names their
    /// server would otherwise move the row there — the address would start being served by
    /// their Stalwart, on their domain, under their DKIM key.
    /// </summary>
    [Fact]
    public async Task Our_own_mailbox_cannot_be_moved_onto_another_tenants_server()
    {
        StalwartMailAccount ours = await sut.AddAccountAsync(tenantId, new StalwartMailAccount
        {
            ConfigId = configId, DomainId = domainId, LocalPart = "nils",
        });

        ours.ConfigId = theirConfigId;
        ours.Aliases = "sales";
        await sut.UpdateAccountAsync(tenantId, ours);

        StalwartMailAccount stored = await db.StalwartMailAccounts.AsNoTracking()
            .FirstAsync(a => a.Id == ours.Id);

        stored.Aliases.Should().Be("sales");
        stored.ConfigId.Should().Be(configId);
        stored.DomainId.Should().Be(domainId);
    }
}
