using EntKube.Web.Data;
using EntKube.Web.Services.Contracts;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace EntKube.Web.Tests;

/// <summary>
/// Whose customer the contract rows belong to.
///
/// <para>Most of <see cref="ContractService"/> was already scoped — twenty-six of its
/// twenty-nine methods carried a real tenant predicate. What was left divides into two
/// shapes, and the second is the interesting one.</para>
///
/// <para>Two list methods took a bare <c>customerId</c>: the mail domains and the support
/// addresses registered to a customer. Those are the routing table for support mail, so
/// reading another tenant's tells you which companies they serve and at which addresses.</para>
///
/// <para>The writes were worse than unscoped — they were <b>half</b> scoped. They stamped
/// <c>TenantId = tenantId</c>, correctly, onto a row whose <c>CustomerId</c> nobody checked.
/// A row carrying our tenant and their customer is not a harmless mismatch: the mail matcher
/// follows the pointer, and <c>AddSupportAddressAsync</c>'s "only one address replies come
/// from" loop cleared that flag on every row with the given <c>CustomerId</c> — their rows
/// included.</para>
/// </summary>
public class ContractTenancyTests : IDisposable
{
    private static readonly DateTime January = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private readonly SqliteConnection connection;
    private readonly ApplicationDbContext db;
    private readonly ContractService contracts;

    private readonly Guid tenantId = Guid.NewGuid();
    private readonly Guid customerId = Guid.NewGuid();

    private readonly Guid otherTenantId = Guid.NewGuid();
    private readonly Guid theirCustomerId = Guid.NewGuid();

    public ContractTenancyTests()
    {
        connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();

        db = new ApplicationDbContext(
            new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(connection).Options);
        db.Database.EnsureCreated();

        db.Tenants.AddRange(
            new Tenant { Id = tenantId, Name = "ENTIT", Slug = $"entit-{tenantId:N}" },
            new Tenant { Id = otherTenantId, Name = "Annan", Slug = $"annan-{otherTenantId:N}" });
        db.Customers.AddRange(
            new Customer { Id = customerId, TenantId = tenantId, Name = "Entit AB" },
            new Customer { Id = theirCustomerId, TenantId = otherTenantId, Name = "Region Skåne" });
        db.SaveChanges();

        contracts = new ContractService(new TestDbContextFactory(connection));
    }

    public void Dispose()
    {
        db.Dispose();
        connection.Dispose();
        GC.SuppressFinalize(this);
    }

    private void SeedTheirDomain(string domain = "regionskane.example")
    {
        db.CustomerEmailDomains.Add(new CustomerEmailDomain
        {
            Id = Guid.NewGuid(),
            TenantId = otherTenantId,
            CustomerId = theirCustomerId,
            Domain = domain,
        });
        db.SaveChanges();
    }

    private Guid SeedTheirAddress(string address = "skane@support.example", bool replyFrom = true)
    {
        CustomerSupportAddress row = new()
        {
            Id = Guid.NewGuid(),
            TenantId = otherTenantId,
            CustomerId = theirCustomerId,
            Address = address,
            ReplyFromThis = replyFrom,
        };
        db.CustomerSupportAddresses.Add(row);
        db.SaveChanges();
        return row.Id;
    }

    // ── The reads ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task Another_tenants_mail_domains_are_not_listed()
    {
        SeedTheirDomain();

        List<CustomerEmailDomain> found =
            await contracts.ListEmailDomainsAsync(tenantId, theirCustomerId);

        found.Should().BeEmpty();
    }

    [Fact]
    public async Task Another_tenants_support_addresses_are_not_listed()
    {
        SeedTheirAddress();

        List<CustomerSupportAddress> found =
            await contracts.ListSupportAddressesAsync(tenantId, theirCustomerId);

        found.Should().BeEmpty();
    }

    // ── The half-scoped writes ────────────────────────────────────────────────

    [Fact]
    public async Task A_mail_domain_cannot_be_registered_to_another_tenants_customer()
    {
        (CustomerEmailDomain? added, string? refused) =
            await contracts.AddEmailDomainAsync(
                tenantId, theirCustomerId, "regionskane.example", null, "nils");

        added.Should().BeNull();
        refused.Should().Contain("does not belong to this tenant");
        (await db.CustomerEmailDomains.AsNoTracking().AnyAsync()).Should().BeFalse();
    }

    [Fact]
    public async Task A_support_address_cannot_be_given_to_another_tenants_customer()
    {
        (CustomerSupportAddress? added, string? refused) =
            await contracts.AddSupportAddressAsync(
                tenantId, theirCustomerId, "skane@support.example", true, null, "nils");

        added.Should().BeNull();
        refused.Should().Contain("does not belong to this tenant");
        (await db.CustomerSupportAddresses.AsNoTracking().AnyAsync()).Should().BeFalse();
    }

    /// <summary>
    /// The sharp one. <c>ReplyFromThis</c> decides which address a customer's support replies
    /// are sent from, and at most one may claim it — so adding an address clears the flag on
    /// the others. That loop selected on <c>CustomerId</c> alone.
    ///
    /// <para>⚠️ Plant, don't steal. Crossing with their <c>customerId</c> proves nothing here:
    /// the customer guard added above refuses before the loop ever runs, so a crossing test
    /// stays green with the loop's tenant filter deleted. What reaches the loop is a row that
    /// is already wrong — our customer, their tenant — which is exactly what the half-scoped
    /// writes this PR fixes used to produce. The loop must leave it alone.</para>
    /// </summary>
    [Fact]
    public async Task Claiming_reply_from_does_not_clear_a_foreign_rows_flag()
    {
        CustomerSupportAddress planted = new()
        {
            Id = Guid.NewGuid(),
            TenantId = otherTenantId,
            CustomerId = customerId,
            Address = "gammal@support.example",
            ReplyFromThis = true,
        };
        db.CustomerSupportAddresses.Add(planted);
        db.SaveChanges();

        (CustomerSupportAddress? added, string? refused) =
            await contracts.AddSupportAddressAsync(
                tenantId, customerId, "nytt@support.example", true, null, "nils");

        refused.Should().BeNull();
        added!.ReplyFromThis.Should().BeTrue();

        (await db.CustomerSupportAddresses.AsNoTracking().FirstAsync(a => a.Id == planted.Id))
            .ReplyFromThis.Should().BeTrue();
    }

    /// <summary>
    /// The crossing is still worth keeping, for the guard that does carry it.
    /// </summary>
    [Fact]
    public async Task Claiming_reply_from_for_another_tenants_customer_is_refused()
    {
        Guid theirs = SeedTheirAddress("skane@support.example", replyFrom: true);

        (CustomerSupportAddress? added, string? refused) =
            await contracts.AddSupportAddressAsync(
                tenantId, theirCustomerId, "nytt@support.example", true, null, "nils");

        added.Should().BeNull();
        refused.Should().Contain("does not belong to this tenant");
        (await db.CustomerSupportAddresses.AsNoTracking().FirstAsync(a => a.Id == theirs))
            .ReplyFromThis.Should().BeTrue();
    }

    // ── The price lists ───────────────────────────────────────────────────────

    [Fact]
    public async Task A_price_list_stores_the_callers_tenant_not_the_objects()
    {
        PriceList claimed = StandardPriceList.Create(otherTenantId, January);

        PriceList stored = await contracts.AddPriceListAsync(tenantId, claimed);

        stored.TenantId.Should().Be(tenantId);
        (await db.PriceLists.AsNoTracking().AnyAsync(p => p.TenantId == otherTenantId))
            .Should().BeFalse();
    }

    [Fact]
    public async Task A_price_list_cannot_be_negotiated_for_another_tenants_customer()
    {
        PriceList forTheirs = StandardPriceList.Create(tenantId, January, theirCustomerId);

        Func<Task> act = () => contracts.AddPriceListAsync(tenantId, forTheirs);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*does not belong to this tenant*");
        (await db.PriceLists.AsNoTracking().AnyAsync()).Should().BeFalse();
    }

    /// <summary>
    /// Indexation reads the list in force and writes a raised copy. The read falls back to the
    /// tenant's standard list when the customer has none — so a foreign customer id found
    /// <i>our</i> list, and the copy was written back bound to their customer. The refusal has
    /// to come before the read, not from it.
    /// </summary>
    [Fact]
    public async Task Indexing_for_another_tenants_customer_writes_nothing()
    {
        db.PriceLists.Add(StandardPriceList.Create(tenantId, January));
        db.SaveChanges();

        Func<Task> act = () => contracts.IndexPriceListAsync(
            tenantId, theirCustomerId, January.AddYears(1), 3m, "nils");

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*does not belong to this tenant*");
        (await db.PriceLists.AsNoTracking().CountAsync()).Should().Be(1);
    }

    // ── The permitted directions ──────────────────────────────────────────────

    [Fact]
    public async Task Our_own_customers_mail_routing_still_works()
    {
        (CustomerEmailDomain? domain, string? domainRefused) =
            await contracts.AddEmailDomainAsync(
                tenantId, customerId, "entit.example", null, "nils");
        domainRefused.Should().BeNull();
        domain!.CustomerId.Should().Be(customerId);

        (CustomerSupportAddress? first, string? firstRefused) =
            await contracts.AddSupportAddressAsync(
                tenantId, customerId, "entit@support.example", true, null, "nils");
        firstRefused.Should().BeNull();

        (CustomerSupportAddress? second, string? secondRefused) =
            await contracts.AddSupportAddressAsync(
                tenantId, customerId, "entit2@support.example", true, null, "nils");
        secondRefused.Should().BeNull();

        // The newest claim wins, which is the behaviour the scoped loop must preserve.
        (await db.CustomerSupportAddresses.AsNoTracking().FirstAsync(a => a.Id == first!.Id))
            .ReplyFromThis.Should().BeFalse();
        (await db.CustomerSupportAddresses.AsNoTracking().FirstAsync(a => a.Id == second!.Id))
            .ReplyFromThis.Should().BeTrue();

        (await contracts.ListEmailDomainsAsync(tenantId, customerId)).Should().ContainSingle();
        (await contracts.ListSupportAddressesAsync(tenantId, customerId)).Should().HaveCount(2);
    }

    [Fact]
    public async Task Indexing_our_own_standard_list_still_works()
    {
        db.PriceLists.Add(StandardPriceList.Create(tenantId, January));
        db.SaveChanges();

        PriceList? indexed = await contracts.IndexPriceListAsync(
            tenantId, null, January.AddYears(1), 3m, "nils");

        indexed.Should().NotBeNull();
        indexed!.TenantId.Should().Be(tenantId);
        indexed.Entries.Should().NotBeEmpty();
    }
}
