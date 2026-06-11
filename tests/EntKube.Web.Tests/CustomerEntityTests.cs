using EntKube.Web.Data;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace EntKube.Web.Tests;

/// <summary>
/// A customer represents an end-client or account within a tenant. Tenants
/// may serve multiple customers, and this entity tracks that relationship.
/// </summary>
public class CustomerEntityTests : IDisposable
{
    private readonly SqliteConnection connection;
    private readonly ApplicationDbContext context;

    public CustomerEntityTests()
    {
        connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();

        DbContextOptions<ApplicationDbContext> options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseSqlite(connection)
            .Options;

        context = new ApplicationDbContext(options);
        context.Database.EnsureCreated();
    }

    [Fact]
    public async Task Customer_CanBeCreated_WithinTenant()
    {
        // A customer belongs to a tenant. It represents an end-client
        // or account that the tenant organization serves.

        Tenant tenant = new() { Id = Guid.NewGuid(), Name = "Acme", Slug = "acme" };
        context.Tenants.Add(tenant);

        Customer customer = new()
        {
            Id = Guid.NewGuid(),
            TenantId = tenant.Id,
            Name = "Big Corp"
        };

        context.Customers.Add(customer);
        await context.SaveChangesAsync();

        Customer? retrieved = await context.Customers
            .Include(c => c.Tenant)
            .FirstOrDefaultAsync(c => c.Name == "Big Corp");

        retrieved.Should().NotBeNull();
        retrieved!.Tenant.Name.Should().Be("Acme");
    }

    [Fact]
    public async Task Customer_NameMustBeUniqueWithinTenant()
    {
        // Two customers in the same tenant cannot share the same name.

        Tenant tenant = new() { Id = Guid.NewGuid(), Name = "Acme", Slug = "acme" };
        context.Tenants.Add(tenant);

        context.Customers.Add(new Customer { Id = Guid.NewGuid(), TenantId = tenant.Id, Name = "Big Corp" });
        await context.SaveChangesAsync();

        context.Customers.Add(new Customer { Id = Guid.NewGuid(), TenantId = tenant.Id, Name = "Big Corp" });
        Func<Task> act = async () => await context.SaveChangesAsync();

        await act.Should().ThrowAsync<DbUpdateException>();
    }

    [Fact]
    public async Task Customer_SameNameAllowedInDifferentTenants()
    {
        // Different tenants can each have a customer named "Big Corp".

        Tenant tenantA = new() { Id = Guid.NewGuid(), Name = "Acme", Slug = "acme" };
        Tenant tenantB = new() { Id = Guid.NewGuid(), Name = "Beta", Slug = "beta" };
        context.Tenants.AddRange(tenantA, tenantB);

        context.Customers.AddRange(
            new Customer { Id = Guid.NewGuid(), TenantId = tenantA.Id, Name = "Big Corp" },
            new Customer { Id = Guid.NewGuid(), TenantId = tenantB.Id, Name = "Big Corp" }
        );

        await context.SaveChangesAsync();

        List<Customer> customers = await context.Customers.Where(c => c.Name == "Big Corp").ToListAsync();
        customers.Should().HaveCount(2);
    }

    [Fact]
    public async Task Tenant_CanHaveMultipleCustomers()
    {
        // A tenant typically serves several customers.

        Tenant tenant = new() { Id = Guid.NewGuid(), Name = "Acme", Slug = "acme" };
        context.Tenants.Add(tenant);

        context.Customers.AddRange(
            new Customer { Id = Guid.NewGuid(), TenantId = tenant.Id, Name = "Customer A" },
            new Customer { Id = Guid.NewGuid(), TenantId = tenant.Id, Name = "Customer B" },
            new Customer { Id = Guid.NewGuid(), TenantId = tenant.Id, Name = "Customer C" }
        );

        await context.SaveChangesAsync();

        List<Customer> customers = await context.Customers.Where(c => c.TenantId == tenant.Id).ToListAsync();
        customers.Should().HaveCount(3);
    }

    public void Dispose()
    {
        context.Dispose();
        connection.Dispose();
    }
}
