using EntKube.Web.Data;
using EntKube.Web.Services;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;

namespace EntKube.Web.Tests;

/// <summary>
/// A customer belongs to the environment it was created under — not to every environment
/// in the tenant. Membership is explicit (CustomerEnvironment) so a customer with no apps
/// yet still has exactly one place in the tenant tree.
/// </summary>
public class CustomerEnvironmentMembershipTests : IDisposable
{
    private readonly InterceptingTestDb db;
    private readonly TenantService service;

    private readonly Guid tenantId = Guid.NewGuid();

    /// <summary>A second tenant with an environment and a customer of its own.</summary>
    private readonly Guid strangerTenant = Guid.NewGuid();
    private Guid strangerEnvId;
    private Guid strangerCustomerId;
    private Guid devId;
    private Guid prodId;

    public CustomerEnvironmentMembershipTests()
    {
        db = new InterceptingTestDb(new byte[32]);
        service = new TenantService(db.Factory, db.CreateVaultService(), new EntKube.Web.Modules.Api.CatalogApi(db.Factory));

        using ApplicationDbContext ctx = db.CreateContext();
        ctx.Tenants.AddRange(
            new Tenant { Id = tenantId, Name = "Acme", Slug = "acme" },
            new Tenant { Id = strangerTenant, Name = "Stranger", Slug = "stranger" });
        ctx.SaveChanges();

        devId = service.CreateEnvironmentAsync(tenantId, "Development").GetAwaiter().GetResult().Id;
        prodId = service.CreateEnvironmentAsync(tenantId, "Production").GetAwaiter().GetResult().Id;

        strangerEnvId = service.CreateEnvironmentAsync(strangerTenant, "Production")
            .GetAwaiter().GetResult().Id;
        strangerCustomerId = service.CreateCustomerAsync(strangerTenant, "Other Corp", strangerEnvId)
            .GetAwaiter().GetResult().Id;
    }

    [Fact]
    public async Task CreatedCustomer_JoinsOnlyItsParentEnvironment()
    {
        Customer customer = await service.CreateCustomerAsync(tenantId, "Big Corp", prodId);

        List<CustomerEnvironment> links = await service.GetCustomerEnvironmentsAsync(tenantId);

        links.Should().ContainSingle();
        links[0].CustomerId.Should().Be(customer.Id);
        links[0].EnvironmentId.Should().Be(prodId);
    }

    [Fact]
    public async Task CreatedCustomer_WithoutParentEnvironment_JoinsNothing()
    {
        // The tenant-wide customer list can create without picking an environment.
        await service.CreateCustomerAsync(tenantId, "Big Corp");

        (await service.GetCustomerEnvironmentsAsync(tenantId)).Should().BeEmpty();
    }

    [Fact]
    public async Task AddToEnvironment_IsIdempotent()
    {
        Customer customer = await service.CreateCustomerAsync(tenantId, "Big Corp", prodId);

        (await service.AddCustomerToEnvironmentAsync(tenantId, customer.Id, devId)).Should().BeTrue();
        (await service.AddCustomerToEnvironmentAsync(tenantId, customer.Id, devId)).Should().BeFalse();

        List<CustomerEnvironment> links = await service.GetCustomerEnvironmentsAsync(tenantId);
        links.Should().HaveCount(2);
        links.Select(l => l.EnvironmentId).Should().BeEquivalentTo([devId, prodId]);
    }

    [Fact]
    public async Task LinkingAnApp_MakesItsCustomerAMemberOfThatEnvironment()
    {
        // Deploying into an environment implies membership — otherwise the app would have
        // nowhere to hang in the tree.
        Customer customer = await service.CreateCustomerAsync(tenantId, "Big Corp", prodId);
        App app = await service.CreateAppAsync(tenantId, customer.Id, "Payments");

        await service.LinkAppToEnvironmentAsync(tenantId, app.Id, devId);

        List<CustomerEnvironment> links = await service.GetCustomerEnvironmentsAsync(tenantId);
        links.Select(l => l.EnvironmentId).Should().BeEquivalentTo([devId, prodId]);
    }

    [Fact]
    public async Task RemoveFromEnvironment_RefusedWhileAppsRemain()
    {
        Customer customer = await service.CreateCustomerAsync(tenantId, "Big Corp", prodId);
        App app = await service.CreateAppAsync(tenantId, customer.Id, "Payments");
        await service.LinkAppToEnvironmentAsync(tenantId, app.Id, prodId);

        (await service.RemoveCustomerFromEnvironmentAsync(tenantId, customer.Id, prodId)).Should().BeFalse();
        (await service.GetCustomerEnvironmentsAsync(tenantId)).Should().ContainSingle();
    }

    [Fact]
    public async Task RemoveFromEnvironment_DropsMembershipWhenEmpty()
    {
        Customer customer = await service.CreateCustomerAsync(tenantId, "Big Corp", prodId);
        await service.AddCustomerToEnvironmentAsync(tenantId, customer.Id, devId);

        (await service.RemoveCustomerFromEnvironmentAsync(tenantId, customer.Id, devId)).Should().BeTrue();

        List<CustomerEnvironment> links = await service.GetCustomerEnvironmentsAsync(tenantId);
        links.Should().ContainSingle();
        links[0].EnvironmentId.Should().Be(prodId);
    }

    [Fact]
    public async Task DeletingAnEnvironment_ClearsItsMemberships()
    {
        // The membership FK is Restrict, so the delete would otherwise fail.
        Customer customer = await service.CreateCustomerAsync(tenantId, "Big Corp", devId);

        (await service.DeleteEnvironmentAsync(tenantId, devId)).Should().BeTrue();

        (await service.GetCustomerEnvironmentsAsync(tenantId)).Should().BeEmpty();
        customer.Should().NotBeNull();
    }

    [Fact]
    public async Task DeletingACustomer_ClearsItsMemberships()
    {
        Customer customer = await service.CreateCustomerAsync(tenantId, "Big Corp", prodId);

        (await service.DeleteCustomerAsync(tenantId, customer.Id)).Should().BeTrue();

        (await service.GetCustomerEnvironmentsAsync(tenantId)).Should().BeEmpty();
    }

    public void Dispose()
    {
        db.Dispose();
        GC.SuppressFinalize(this);
    }
    // ── Tenancy of the app and environment lifecycle ──────────────────────────

    /// <summary>
    /// ⚠️ The one to read twice. <c>DeleteAppAsync</c> took an app id alone, and besides cascading
    /// the app away it runs
    /// <c>db.ConnectivityRules.Where(r => r.PeerAppId == id).ExecuteDeleteAsync()</c> — rules that
    /// <em>other</em> apps own, matched only on the peer id. So deleting by id alone would remove
    /// another tenant's application and silently take their connectivity rules with it.
    /// </summary>
    [Fact]
    public async Task A_stranger_cannot_delete_or_rename_another_tenants_app()
    {
        Customer customer = await service.CreateCustomerAsync(tenantId, "Big Corp", prodId);
        App app = await service.CreateAppAsync(tenantId, customer.Id, "Billing");

        (await service.RenameAppAsync(strangerTenant, app.Id, "Renamed")).Should().BeFalse();
        (await service.DeleteAppAsync(strangerTenant, app.Id)).Should().BeFalse();

        using ApplicationDbContext check = db.CreateContext();
        App after = await check.Apps.FirstAsync(a => a.Id == app.Id);
        after.Name.Should().Be("Billing", "the app is still here and still called what it was");

        (await service.DeleteAppAsync(tenantId, app.Id)).Should().BeTrue("the owner can delete it");
    }

    /// <summary>
    /// A new app hangs off a customer, and the customer is the only thing tying it to a tenant.
    /// </summary>
    [Fact]
    public async Task A_stranger_cannot_create_an_app_under_another_tenants_customer()
    {
        Customer customer = await service.CreateCustomerAsync(tenantId, "Big Corp", prodId);

        Func<Task> act = () => service.CreateAppAsync(strangerTenant, customer.Id, "Smuggled");

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*does not belong to this tenant*");

        using ApplicationDbContext check = db.CreateContext();
        check.Apps.Should().BeEmpty();
    }

    /// <summary>
    /// A link is a <b>pairing</b>, and that makes it a different shape from a single lookup: both
    /// ends have to be the caller's. Unscoped, this attaches one tenant's app to another tenant's
    /// environment — and the branch inside then adds a <c>CustomerEnvironment</c>, writing that
    /// customer into the other tenant's tree.
    /// </summary>
    [Fact]
    public async Task An_app_cannot_be_linked_across_a_tenant_boundary()
    {
        Customer customer = await service.CreateCustomerAsync(tenantId, "Big Corp", prodId);
        App app = await service.CreateAppAsync(tenantId, customer.Id, "Billing");

        // Our app, their environment.
        Func<Task> theirEnv = () => service.LinkAppToEnvironmentAsync(tenantId, app.Id, strangerEnvId);
        await theirEnv.Should().ThrowAsync<InvalidOperationException>();

        // Their app, our environment — the same pairing from the other side.
        App theirApp = await service.CreateAppAsync(
            strangerTenant, strangerCustomerId, "Their Billing");
        Func<Task> theirApp2 = () => service.LinkAppToEnvironmentAsync(tenantId, theirApp.Id, prodId);
        await theirApp2.Should().ThrowAsync<InvalidOperationException>();

        using ApplicationDbContext check = db.CreateContext();
        check.AppEnvironments.Should().BeEmpty("neither pairing may be written");

        await service.LinkAppToEnvironmentAsync(tenantId, app.Id, prodId);
        using ApplicationDbContext after = db.CreateContext();
        after.AppEnvironments.Should().ContainSingle("the legitimate pairing still works");
    }

    /// <summary>
    /// Deleting an environment drops every <c>CustomerEnvironment</c> for its id, so an unscoped
    /// delete removes another tenant's deployment stage and every customer's membership of it.
    /// </summary>
    [Fact]
    public async Task A_stranger_cannot_delete_another_tenants_environment()
    {
        (await service.DeleteEnvironmentAsync(strangerTenant, prodId)).Should().BeFalse();

        using ApplicationDbContext check = db.CreateContext();
        check.Environments.Should().Contain(e => e.Id == prodId);

        (await service.DeleteEnvironmentAsync(tenantId, devId)).Should().BeTrue();
    }

    [Fact]
    public async Task Customer_environment_membership_cannot_cross_a_tenant_boundary()
    {
        Customer customer = await service.CreateCustomerAsync(tenantId, "Big Corp", prodId);

        Func<Task> act = () =>
            service.AddCustomerToEnvironmentAsync(tenantId, customer.Id, strangerEnvId);
        await act.Should().ThrowAsync<InvalidOperationException>();

        (await service.RemoveCustomerFromEnvironmentAsync(strangerTenant, customer.Id, prodId))
            .Should().BeFalse("the pair is not the stranger's to unpick");

        List<CustomerEnvironment> links = await service.GetCustomerEnvironmentsAsync(tenantId);
        links.Should().ContainSingle(l => l.CustomerId == customer.Id && l.EnvironmentId == prodId);
    }
}
