using EntKube.Web.Data;
using EntKube.Web.Services;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace EntKube.Web.Tests;

/// <summary>
/// Whose customer a portal grant is for.
///
/// <para>The rows this service writes <b>are</b> authorization — a <c>CustomerAccess</c> row is
/// what lets a user open a customer in the portal, and at Operator it lets them restart pods
/// and trigger redeployments. Three of the five methods took a bare <c>customerId</c> from a
/// tenant page: the grant, the revoke, and the list of who holds access.</para>
///
/// <para>⚠️ <b>The consumer side is correct, and that is what made this dangerous rather than
/// untidy.</b> <c>GetAccessAsync</c> and <c>GetAccessibleCustomersAsync</c> are keyed on the
/// signed-in user and deliberately take no tenant — the user is the principal, so there is no
/// caller tenant distinct from the row. A row planted against another tenant's customer is
/// therefore honoured by a check doing exactly its job. A scoped consumer does not contain an
/// unscoped producer; it legitimises it.</para>
///
/// <para>This file was written because the recorded exemption for this service said
/// "principal-keyed" — true of one method out of five, and inherited across several
/// conversions without opening the callers.</para>
/// </summary>
public class CustomerAccessTenancyTests : IDisposable
{
    private readonly SqliteConnection connection;
    private readonly ApplicationDbContext db;
    private readonly CustomerAccessService sut;

    private readonly Guid tenantId = Guid.NewGuid();
    private readonly Guid otherTenantId = Guid.NewGuid();
    private readonly Guid ourCustomerId = Guid.NewGuid();
    private readonly Guid theirCustomerId = Guid.NewGuid();

    private readonly ApplicationUser outsider;
    private readonly ApplicationUser theirStaff;

    public CustomerAccessTenancyTests()
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
            new Customer { Id = ourCustomerId, TenantId = tenantId, Name = "Entit AB" },
            new Customer { Id = theirCustomerId, TenantId = otherTenantId, Name = "Region Skåne" });

        outsider = new ApplicationUser
        {
            Id = Guid.NewGuid().ToString(), UserName = "oss@entit.example",
            Email = "oss@entit.example",
        };
        theirStaff = new ApplicationUser
        {
            Id = Guid.NewGuid().ToString(), UserName = "drift@region.example",
            Email = "drift@region.example",
        };
        db.Users.AddRange(outsider, theirStaff);
        db.SaveChanges();

        sut = new CustomerAccessService(new TestDbContextFactory(connection));
    }

    public void Dispose()
    {
        db.Dispose();
        connection.Dispose();
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// The whole point, in one test: the grant is refused, <b>and</b> the portal's own check —
    /// the one that is correctly keyed on the signed-in user — is asked afterwards and finds
    /// nothing. Without the second half this only proves an exception was thrown.
    /// </summary>
    [Fact]
    public async Task Access_to_another_tenants_customer_cannot_be_granted()
    {
        Func<Task> act = () => sut.GrantAccessAsync(
            tenantId, outsider.Id, theirCustomerId, CustomerAccessRole.Admin);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*does not belong to this tenant*");

        (await db.CustomerAccesses.AsNoTracking().AnyAsync()).Should().BeFalse();

        // The portal would have honoured the row, so the row must not exist.
        (await sut.GetAccessAsync(outsider.Id, theirCustomerId)).Should().BeNull();
        (await sut.GetAccessibleCustomersAsync(outsider.Id)).Should().BeEmpty();
    }

    [Fact]
    public async Task Access_to_another_tenants_customer_cannot_be_revoked()
    {
        await sut.GrantAccessAsync(
            otherTenantId, theirStaff.Id, theirCustomerId, CustomerAccessRole.Operator);

        await sut.RevokeAccessAsync(tenantId, theirStaff.Id, theirCustomerId);

        // Their person is still able to get in, which is the point of the refusal.
        (await sut.GetAccessAsync(theirStaff.Id, theirCustomerId)).Should().NotBeNull();
    }

    /// <summary>
    /// The escalation direction. No caller today, which is why it is worth a test: the one
    /// unscoped way to change a role would otherwise be waiting for its first caller.
    /// </summary>
    [Fact]
    public async Task A_role_on_another_tenants_customer_cannot_be_changed()
    {
        await sut.GrantAccessAsync(
            otherTenantId, theirStaff.Id, theirCustomerId, CustomerAccessRole.Viewer);

        await sut.UpdateRoleAsync(
            tenantId, theirStaff.Id, theirCustomerId, CustomerAccessRole.Admin);

        CustomerAccess? stored = await sut.GetAccessAsync(theirStaff.Id, theirCustomerId);
        stored!.Role.Should().Be(CustomerAccessRole.Viewer);
    }

    [Fact]
    public async Task Another_tenants_portal_users_are_not_listed()
    {
        await sut.GrantAccessAsync(
            otherTenantId, theirStaff.Id, theirCustomerId, CustomerAccessRole.Operator);

        List<CustomerAccess> found = await sut.GetCustomerUsersAsync(tenantId, theirCustomerId);

        found.Should().BeEmpty();
    }

    [Fact]
    public async Task Our_own_customers_access_is_granted_listed_changed_and_revoked()
    {
        CustomerAccess granted = await sut.GrantAccessAsync(
            tenantId, outsider.Id, ourCustomerId, CustomerAccessRole.Viewer);
        granted.Role.Should().Be(CustomerAccessRole.Viewer);

        (await sut.GetCustomerUsersAsync(tenantId, ourCustomerId)).Should().ContainSingle()
            .Which.User.Email.Should().Be("oss@entit.example");

        await sut.UpdateRoleAsync(
            tenantId, outsider.Id, ourCustomerId, CustomerAccessRole.Operator);
        (await sut.GetAccessAsync(outsider.Id, ourCustomerId))!
            .Role.Should().Be(CustomerAccessRole.Operator);

        await sut.RevokeAccessAsync(tenantId, outsider.Id, ourCustomerId);
        (await sut.GetAccessAsync(outsider.Id, ourCustomerId)).Should().BeNull();
    }
}
