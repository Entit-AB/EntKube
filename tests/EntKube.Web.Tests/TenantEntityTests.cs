using EntKube.Web.Data;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace EntKube.Web.Tests;

/// <summary>
/// A tenant represents an organization or workspace in EntKube. Users can
/// belong to multiple tenants, each membership carrying a role that determines
/// what that user can do within that tenant's scope.
/// </summary>
public class TenantEntityTests : IDisposable
{
    private readonly SqliteConnection connection;
    private readonly ApplicationDbContext context;

    public TenantEntityTests()
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
    public async Task Tenant_CanBeCreated_WithNameAndSlug()
    {
        // A tenant is the fundamental organizational unit. Every resource
        // in EntKube is scoped to a tenant. We need at minimum a name
        // (human-friendly) and a slug (URL-safe identifier).

        Tenant tenant = new()
        {
            Id = Guid.NewGuid(),
            Name = "Acme Corp",
            Slug = "acme-corp"
        };

        context.Tenants.Add(tenant);
        await context.SaveChangesAsync();

        Tenant? retrieved = await context.Tenants.FirstOrDefaultAsync(t => t.Slug == "acme-corp");

        retrieved.Should().NotBeNull();
        retrieved!.Name.Should().Be("Acme Corp");
    }

    [Fact]
    public async Task Tenant_SlugMustBeUnique()
    {
        // Two tenants cannot share the same slug — it's the unique identifier
        // used in URLs and API calls to target a specific tenant.

        Tenant first = new() { Id = Guid.NewGuid(), Name = "First", Slug = "shared-slug" };
        Tenant second = new() { Id = Guid.NewGuid(), Name = "Second", Slug = "shared-slug" };

        context.Tenants.Add(first);
        await context.SaveChangesAsync();

        context.Tenants.Add(second);
        Func<Task> act = async () => await context.SaveChangesAsync();

        await act.Should().ThrowAsync<DbUpdateException>();
    }

    [Fact]
    public async Task TenantRole_CanBeCreated_WithinTenant()
    {
        // Each tenant defines its own set of roles. The "Administrator" role
        // is the primary role that grants full control within a tenant.

        Tenant tenant = new() { Id = Guid.NewGuid(), Name = "Acme Corp", Slug = "acme" };
        context.Tenants.Add(tenant);

        TenantRole adminRole = new()
        {
            Id = Guid.NewGuid(),
            TenantId = tenant.Id,
            Name = "Administrator"
        };

        context.TenantRoles.Add(adminRole);
        await context.SaveChangesAsync();

        TenantRole? retrieved = await context.TenantRoles
            .FirstOrDefaultAsync(r => r.TenantId == tenant.Id && r.Name == "Administrator");

        retrieved.Should().NotBeNull();
    }

    [Fact]
    public async Task TenantMembership_AssociatesUserWithTenantAndRole()
    {
        // A user joins a tenant through a membership, which also assigns them
        // a role within that tenant. This is the many-to-many relationship
        // with role context — a user might be an Administrator in one tenant
        // but a regular member in another.

        Tenant tenant = new() { Id = Guid.NewGuid(), Name = "Acme", Slug = "acme" };
        context.Tenants.Add(tenant);

        TenantRole role = new() { Id = Guid.NewGuid(), TenantId = tenant.Id, Name = "Administrator" };
        context.TenantRoles.Add(role);

        ApplicationUser user = new()
        {
            Id = Guid.NewGuid().ToString(),
            UserName = "admin@acme.com",
            NormalizedUserName = "ADMIN@ACME.COM",
            Email = "admin@acme.com",
            NormalizedEmail = "ADMIN@ACME.COM",
            EmailConfirmed = true,
            SecurityStamp = Guid.NewGuid().ToString()
        };

        context.Users.Add(user);

        TenantMembership membership = new()
        {
            UserId = user.Id,
            TenantId = tenant.Id,
            RoleId = role.Id
        };

        context.TenantMemberships.Add(membership);
        await context.SaveChangesAsync();

        // Verify we can navigate from a user to their tenants and role.
        TenantMembership? retrieved = await context.TenantMemberships
            .Include(m => m.Tenant)
            .Include(m => m.Role)
            .FirstOrDefaultAsync(m => m.UserId == user.Id);

        retrieved.Should().NotBeNull();
        retrieved!.Tenant.Name.Should().Be("Acme");
        retrieved.Role.Name.Should().Be("Administrator");
    }

    [Fact]
    public async Task User_CanBelongToMultipleTenants()
    {
        // A user might work across several organizations. Each membership
        // is independent — different tenants, potentially different roles.

        ApplicationUser user = new()
        {
            Id = Guid.NewGuid().ToString(),
            UserName = "user@example.com",
            NormalizedUserName = "USER@EXAMPLE.COM",
            Email = "user@example.com",
            NormalizedEmail = "USER@EXAMPLE.COM",
            EmailConfirmed = true,
            SecurityStamp = Guid.NewGuid().ToString()
        };

        context.Users.Add(user);

        Tenant tenantA = new() { Id = Guid.NewGuid(), Name = "Tenant A", Slug = "tenant-a" };
        Tenant tenantB = new() { Id = Guid.NewGuid(), Name = "Tenant B", Slug = "tenant-b" };
        context.Tenants.AddRange(tenantA, tenantB);

        TenantRole adminRole = new() { Id = Guid.NewGuid(), TenantId = tenantA.Id, Name = "Administrator" };
        TenantRole memberRole = new() { Id = Guid.NewGuid(), TenantId = tenantB.Id, Name = "Member" };
        context.TenantRoles.AddRange(adminRole, memberRole);

        context.TenantMemberships.AddRange(
            new TenantMembership { UserId = user.Id, TenantId = tenantA.Id, RoleId = adminRole.Id },
            new TenantMembership { UserId = user.Id, TenantId = tenantB.Id, RoleId = memberRole.Id }
        );

        await context.SaveChangesAsync();

        List<TenantMembership> memberships = await context.TenantMemberships
            .Where(m => m.UserId == user.Id)
            .ToListAsync();

        memberships.Should().HaveCount(2);
    }

    public void Dispose()
    {
        context.Dispose();
        connection.Dispose();
    }
}
