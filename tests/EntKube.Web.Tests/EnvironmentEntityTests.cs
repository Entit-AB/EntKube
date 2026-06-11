using EntKube.Web.Data;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace EntKube.Web.Tests;

/// <summary>
/// An environment represents a deployment stage within a tenant (e.g. "dev",
/// "staging", "production"). Resources like clusters and services are scoped
/// to an environment within a tenant.
/// </summary>
public class EnvironmentEntityTests : IDisposable
{
    private readonly SqliteConnection connection;
    private readonly ApplicationDbContext context;

    public EnvironmentEntityTests()
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
    public async Task Environment_CanBeCreated_WithinTenant()
    {
        // Each tenant has its own set of environments that represent
        // deployment stages. An environment has a name and belongs
        // to exactly one tenant.

        Tenant tenant = new() { Id = Guid.NewGuid(), Name = "Acme", Slug = "acme" };
        context.Tenants.Add(tenant);

        Data.Environment environment = new()
        {
            Id = Guid.NewGuid(),
            TenantId = tenant.Id,
            Name = "Production"
        };

        context.Environments.Add(environment);
        await context.SaveChangesAsync();

        Data.Environment? retrieved = await context.Environments
            .Include(e => e.Tenant)
            .FirstOrDefaultAsync(e => e.Name == "Production");

        retrieved.Should().NotBeNull();
        retrieved!.Tenant.Name.Should().Be("Acme");
    }

    [Fact]
    public async Task Environment_NameMustBeUniqueWithinTenant()
    {
        // Two environments in the same tenant cannot share the same name —
        // you can't have two "Production" environments in one organization.

        Tenant tenant = new() { Id = Guid.NewGuid(), Name = "Acme", Slug = "acme" };
        context.Tenants.Add(tenant);

        context.Environments.Add(new Data.Environment { Id = Guid.NewGuid(), TenantId = tenant.Id, Name = "Production" });
        await context.SaveChangesAsync();

        context.Environments.Add(new Data.Environment { Id = Guid.NewGuid(), TenantId = tenant.Id, Name = "Production" });
        Func<Task> act = async () => await context.SaveChangesAsync();

        await act.Should().ThrowAsync<DbUpdateException>();
    }

    [Fact]
    public async Task Environment_SameNameAllowedInDifferentTenants()
    {
        // Different tenants can each have a "Production" environment —
        // uniqueness is scoped to the tenant, not globally.

        Tenant tenantA = new() { Id = Guid.NewGuid(), Name = "Acme", Slug = "acme" };
        Tenant tenantB = new() { Id = Guid.NewGuid(), Name = "Beta", Slug = "beta" };
        context.Tenants.AddRange(tenantA, tenantB);

        context.Environments.AddRange(
            new Data.Environment { Id = Guid.NewGuid(), TenantId = tenantA.Id, Name = "Production" },
            new Data.Environment { Id = Guid.NewGuid(), TenantId = tenantB.Id, Name = "Production" }
        );

        await context.SaveChangesAsync();

        List<Data.Environment> envs = await context.Environments.Where(e => e.Name == "Production").ToListAsync();
        envs.Should().HaveCount(2);
    }

    [Fact]
    public async Task Tenant_CanHaveMultipleEnvironments()
    {
        // A typical tenant might have dev, staging, and production environments.

        Tenant tenant = new() { Id = Guid.NewGuid(), Name = "Acme", Slug = "acme" };
        context.Tenants.Add(tenant);

        context.Environments.AddRange(
            new Data.Environment { Id = Guid.NewGuid(), TenantId = tenant.Id, Name = "Development" },
            new Data.Environment { Id = Guid.NewGuid(), TenantId = tenant.Id, Name = "Staging" },
            new Data.Environment { Id = Guid.NewGuid(), TenantId = tenant.Id, Name = "Production" }
        );

        await context.SaveChangesAsync();

        List<Data.Environment> envs = await context.Environments.Where(e => e.TenantId == tenant.Id).ToListAsync();
        envs.Should().HaveCount(3);
    }

    public void Dispose()
    {
        context.Dispose();
        connection.Dispose();
    }
}
