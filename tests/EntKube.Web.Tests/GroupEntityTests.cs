using EntKube.Web.Data;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace EntKube.Web.Tests;

/// <summary>
/// Groups provide a way to organize users within a tenant. A group belongs
/// to exactly one tenant and can contain multiple users. This enables bulk
/// permission assignment and logical grouping of team members.
/// </summary>
public class GroupEntityTests : IDisposable
{
    private readonly SqliteConnection connection;
    private readonly ApplicationDbContext context;

    public GroupEntityTests()
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
    public async Task Group_CanBeCreated_WithinTenant()
    {
        // A group lives within a tenant's boundary. It has a name and belongs
        // to exactly one tenant — you can't share groups across tenants.

        Tenant tenant = new() { Id = Guid.NewGuid(), Name = "Acme", Slug = "acme" };
        context.Tenants.Add(tenant);

        Group group = new()
        {
            Id = Guid.NewGuid(),
            TenantId = tenant.Id,
            Name = "Engineering"
        };

        context.Groups.Add(group);
        await context.SaveChangesAsync();

        Group? retrieved = await context.Groups
            .Include(g => g.Tenant)
            .FirstOrDefaultAsync(g => g.Name == "Engineering");

        retrieved.Should().NotBeNull();
        retrieved!.Tenant.Name.Should().Be("Acme");
    }

    [Fact]
    public async Task Group_CanHaveMultipleMembers()
    {
        // Users are added to groups through a membership join entity.
        // Multiple users can belong to the same group.

        Tenant tenant = new() { Id = Guid.NewGuid(), Name = "Acme", Slug = "acme" };
        context.Tenants.Add(tenant);

        Group group = new() { Id = Guid.NewGuid(), TenantId = tenant.Id, Name = "Engineering" };
        context.Groups.Add(group);

        ApplicationUser userA = new()
        {
            Id = Guid.NewGuid().ToString(),
            UserName = "alice@acme.com",
            NormalizedUserName = "ALICE@ACME.COM",
            Email = "alice@acme.com",
            NormalizedEmail = "ALICE@ACME.COM",
            EmailConfirmed = true,
            SecurityStamp = Guid.NewGuid().ToString()
        };

        ApplicationUser userB = new()
        {
            Id = Guid.NewGuid().ToString(),
            UserName = "bob@acme.com",
            NormalizedUserName = "BOB@ACME.COM",
            Email = "bob@acme.com",
            NormalizedEmail = "BOB@ACME.COM",
            EmailConfirmed = true,
            SecurityStamp = Guid.NewGuid().ToString()
        };

        context.Users.AddRange(userA, userB);

        context.GroupMemberships.AddRange(
            new GroupMembership { UserId = userA.Id, GroupId = group.Id },
            new GroupMembership { UserId = userB.Id, GroupId = group.Id }
        );

        await context.SaveChangesAsync();

        List<GroupMembership> members = await context.GroupMemberships
            .Where(gm => gm.GroupId == group.Id)
            .ToListAsync();

        members.Should().HaveCount(2);
    }

    [Fact]
    public async Task User_CanBelongToMultipleGroups()
    {
        // A user might be in "Engineering" and "On-Call" simultaneously.

        Tenant tenant = new() { Id = Guid.NewGuid(), Name = "Acme", Slug = "acme" };
        context.Tenants.Add(tenant);

        Group groupA = new() { Id = Guid.NewGuid(), TenantId = tenant.Id, Name = "Engineering" };
        Group groupB = new() { Id = Guid.NewGuid(), TenantId = tenant.Id, Name = "On-Call" };
        context.Groups.AddRange(groupA, groupB);

        ApplicationUser user = new()
        {
            Id = Guid.NewGuid().ToString(),
            UserName = "alice@acme.com",
            NormalizedUserName = "ALICE@ACME.COM",
            Email = "alice@acme.com",
            NormalizedEmail = "ALICE@ACME.COM",
            EmailConfirmed = true,
            SecurityStamp = Guid.NewGuid().ToString()
        };

        context.Users.Add(user);

        context.GroupMemberships.AddRange(
            new GroupMembership { UserId = user.Id, GroupId = groupA.Id },
            new GroupMembership { UserId = user.Id, GroupId = groupB.Id }
        );

        await context.SaveChangesAsync();

        List<GroupMembership> memberships = await context.GroupMemberships
            .Where(gm => gm.UserId == user.Id)
            .ToListAsync();

        memberships.Should().HaveCount(2);
    }

    public void Dispose()
    {
        context.Dispose();
        connection.Dispose();
    }
}
