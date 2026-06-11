using EntKube.Web.Data;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace EntKube.Web.Tests;

/// <summary>
/// An app belongs to a customer and can be deployed to one or many environments.
/// The many-to-many between App and Environment is tracked via AppEnvironment.
/// </summary>
public class AppEntityTests : IDisposable
{
    private readonly SqliteConnection connection;
    private readonly ApplicationDbContext context;

    public AppEntityTests()
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
    public async Task App_CanBeCreated_WithinCustomer()
    {
        // An app represents a software application owned by a customer.
        // It belongs to exactly one customer.

        Tenant tenant = new() { Id = Guid.NewGuid(), Name = "Acme", Slug = "acme" };
        context.Tenants.Add(tenant);

        Customer customer = new() { Id = Guid.NewGuid(), TenantId = tenant.Id, Name = "Big Corp" };
        context.Customers.Add(customer);

        App app = new()
        {
            Id = Guid.NewGuid(),
            CustomerId = customer.Id,
            Name = "Payment Service"
        };

        context.Apps.Add(app);
        await context.SaveChangesAsync();

        App? retrieved = await context.Apps
            .Include(a => a.Customer)
            .FirstOrDefaultAsync(a => a.Name == "Payment Service");

        retrieved.Should().NotBeNull();
        retrieved!.Customer.Name.Should().Be("Big Corp");
    }

    [Fact]
    public async Task App_NameMustBeUniqueWithinCustomer()
    {
        // Two apps under the same customer cannot share the same name.

        Tenant tenant = new() { Id = Guid.NewGuid(), Name = "Acme", Slug = "acme" };
        context.Tenants.Add(tenant);

        Customer customer = new() { Id = Guid.NewGuid(), TenantId = tenant.Id, Name = "Big Corp" };
        context.Customers.Add(customer);

        context.Apps.Add(new App { Id = Guid.NewGuid(), CustomerId = customer.Id, Name = "API" });
        await context.SaveChangesAsync();

        context.Apps.Add(new App { Id = Guid.NewGuid(), CustomerId = customer.Id, Name = "API" });
        Func<Task> act = async () => await context.SaveChangesAsync();

        await act.Should().ThrowAsync<DbUpdateException>();
    }

    [Fact]
    public async Task App_CanBelongToOneEnvironment()
    {
        // An app is deployed to environments via the AppEnvironment join.
        // Here we test the simplest case: one app in one environment.

        Tenant tenant = new() { Id = Guid.NewGuid(), Name = "Acme", Slug = "acme" };
        context.Tenants.Add(tenant);

        Customer customer = new() { Id = Guid.NewGuid(), TenantId = tenant.Id, Name = "Big Corp" };
        context.Customers.Add(customer);

        Data.Environment env = new() { Id = Guid.NewGuid(), TenantId = tenant.Id, Name = "Production" };
        context.Environments.Add(env);

        App app = new() { Id = Guid.NewGuid(), CustomerId = customer.Id, Name = "API" };
        context.Apps.Add(app);

        context.AppEnvironments.Add(new AppEnvironment { AppId = app.Id, EnvironmentId = env.Id });
        await context.SaveChangesAsync();

        List<AppEnvironment> links = await context.AppEnvironments
            .Where(ae => ae.AppId == app.Id)
            .Include(ae => ae.Environment)
            .ToListAsync();

        links.Should().HaveCount(1);
        links[0].Environment.Name.Should().Be("Production");
    }

    [Fact]
    public async Task App_CanBelongToMultipleEnvironments()
    {
        // A typical app might be deployed to dev, staging, and production
        // simultaneously. Each link is a separate AppEnvironment record.

        Tenant tenant = new() { Id = Guid.NewGuid(), Name = "Acme", Slug = "acme" };
        context.Tenants.Add(tenant);

        Customer customer = new() { Id = Guid.NewGuid(), TenantId = tenant.Id, Name = "Big Corp" };
        context.Customers.Add(customer);

        Data.Environment dev = new() { Id = Guid.NewGuid(), TenantId = tenant.Id, Name = "Development" };
        Data.Environment staging = new() { Id = Guid.NewGuid(), TenantId = tenant.Id, Name = "Staging" };
        Data.Environment prod = new() { Id = Guid.NewGuid(), TenantId = tenant.Id, Name = "Production" };
        context.Environments.AddRange(dev, staging, prod);

        App app = new() { Id = Guid.NewGuid(), CustomerId = customer.Id, Name = "API" };
        context.Apps.Add(app);

        context.AppEnvironments.AddRange(
            new AppEnvironment { AppId = app.Id, EnvironmentId = dev.Id },
            new AppEnvironment { AppId = app.Id, EnvironmentId = staging.Id },
            new AppEnvironment { AppId = app.Id, EnvironmentId = prod.Id }
        );

        await context.SaveChangesAsync();

        List<AppEnvironment> links = await context.AppEnvironments
            .Where(ae => ae.AppId == app.Id)
            .ToListAsync();

        links.Should().HaveCount(3);
    }

    [Fact]
    public async Task Environment_CanHaveMultipleApps()
    {
        // Multiple apps can be deployed to the same environment.

        Tenant tenant = new() { Id = Guid.NewGuid(), Name = "Acme", Slug = "acme" };
        context.Tenants.Add(tenant);

        Customer customer = new() { Id = Guid.NewGuid(), TenantId = tenant.Id, Name = "Big Corp" };
        context.Customers.Add(customer);

        Data.Environment prod = new() { Id = Guid.NewGuid(), TenantId = tenant.Id, Name = "Production" };
        context.Environments.Add(prod);

        App appA = new() { Id = Guid.NewGuid(), CustomerId = customer.Id, Name = "API" };
        App appB = new() { Id = Guid.NewGuid(), CustomerId = customer.Id, Name = "Worker" };
        context.Apps.AddRange(appA, appB);

        context.AppEnvironments.AddRange(
            new AppEnvironment { AppId = appA.Id, EnvironmentId = prod.Id },
            new AppEnvironment { AppId = appB.Id, EnvironmentId = prod.Id }
        );

        await context.SaveChangesAsync();

        List<AppEnvironment> links = await context.AppEnvironments
            .Where(ae => ae.EnvironmentId == prod.Id)
            .ToListAsync();

        links.Should().HaveCount(2);
    }

    [Fact]
    public async Task AppEnvironment_CannotDuplicate()
    {
        // The same app cannot be linked to the same environment twice.
        // We use a second context instance to bypass the change tracker
        // and verify the database-level unique constraint.

        Tenant tenant = new() { Id = Guid.NewGuid(), Name = "Acme", Slug = "acme" };
        context.Tenants.Add(tenant);

        Customer customer = new() { Id = Guid.NewGuid(), TenantId = tenant.Id, Name = "Big Corp" };
        context.Customers.Add(customer);

        Data.Environment prod = new() { Id = Guid.NewGuid(), TenantId = tenant.Id, Name = "Production" };
        context.Environments.Add(prod);

        App app = new() { Id = Guid.NewGuid(), CustomerId = customer.Id, Name = "API" };
        context.Apps.Add(app);

        context.AppEnvironments.Add(new AppEnvironment { AppId = app.Id, EnvironmentId = prod.Id });
        await context.SaveChangesAsync();

        // Use a fresh context to avoid the change tracker catching the duplicate.
        DbContextOptions<ApplicationDbContext> options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseSqlite(connection)
            .Options;

        ApplicationDbContext secondContext = new(options);
        secondContext.AppEnvironments.Add(new AppEnvironment { AppId = app.Id, EnvironmentId = prod.Id });
        Func<Task> act = async () => await secondContext.SaveChangesAsync();

        await act.Should().ThrowAsync<DbUpdateException>();
    }

    public void Dispose()
    {
        context.Dispose();
        connection.Dispose();
    }
}
