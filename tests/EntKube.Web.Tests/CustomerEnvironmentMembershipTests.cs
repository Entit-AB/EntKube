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
    private Guid devId;
    private Guid prodId;

    public CustomerEnvironmentMembershipTests()
    {
        db = new InterceptingTestDb(new byte[32]);
        service = new TenantService(db.Factory, db.CreateVaultService());

        using ApplicationDbContext ctx = db.CreateContext();
        ctx.Tenants.Add(new Tenant { Id = tenantId, Name = "Acme", Slug = "acme" });
        ctx.SaveChanges();

        devId = service.CreateEnvironmentAsync(tenantId, "Development").GetAwaiter().GetResult().Id;
        prodId = service.CreateEnvironmentAsync(tenantId, "Production").GetAwaiter().GetResult().Id;
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

        (await service.AddCustomerToEnvironmentAsync(customer.Id, devId)).Should().BeTrue();
        (await service.AddCustomerToEnvironmentAsync(customer.Id, devId)).Should().BeFalse();

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
        App app = await service.CreateAppAsync(customer.Id, "Payments");

        await service.LinkAppToEnvironmentAsync(app.Id, devId);

        List<CustomerEnvironment> links = await service.GetCustomerEnvironmentsAsync(tenantId);
        links.Select(l => l.EnvironmentId).Should().BeEquivalentTo([devId, prodId]);
    }

    [Fact]
    public async Task RemoveFromEnvironment_RefusedWhileAppsRemain()
    {
        Customer customer = await service.CreateCustomerAsync(tenantId, "Big Corp", prodId);
        App app = await service.CreateAppAsync(customer.Id, "Payments");
        await service.LinkAppToEnvironmentAsync(app.Id, prodId);

        (await service.RemoveCustomerFromEnvironmentAsync(customer.Id, prodId)).Should().BeFalse();
        (await service.GetCustomerEnvironmentsAsync(tenantId)).Should().ContainSingle();
    }

    [Fact]
    public async Task RemoveFromEnvironment_DropsMembershipWhenEmpty()
    {
        Customer customer = await service.CreateCustomerAsync(tenantId, "Big Corp", prodId);
        await service.AddCustomerToEnvironmentAsync(customer.Id, devId);

        (await service.RemoveCustomerFromEnvironmentAsync(customer.Id, devId)).Should().BeTrue();

        List<CustomerEnvironment> links = await service.GetCustomerEnvironmentsAsync(tenantId);
        links.Should().ContainSingle();
        links[0].EnvironmentId.Should().Be(prodId);
    }

    [Fact]
    public async Task DeletingAnEnvironment_ClearsItsMemberships()
    {
        // The membership FK is Restrict, so the delete would otherwise fail.
        Customer customer = await service.CreateCustomerAsync(tenantId, "Big Corp", devId);

        (await service.DeleteEnvironmentAsync(devId)).Should().BeTrue();

        (await service.GetCustomerEnvironmentsAsync(tenantId)).Should().BeEmpty();
        customer.Should().NotBeNull();
    }

    [Fact]
    public async Task DeletingACustomer_ClearsItsMemberships()
    {
        Customer customer = await service.CreateCustomerAsync(tenantId, "Big Corp", prodId);

        (await service.DeleteCustomerAsync(customer.Id)).Should().BeTrue();

        (await service.GetCustomerEnvironmentsAsync(tenantId)).Should().BeEmpty();
    }

    public void Dispose()
    {
        db.Dispose();
        GC.SuppressFinalize(this);
    }
}
