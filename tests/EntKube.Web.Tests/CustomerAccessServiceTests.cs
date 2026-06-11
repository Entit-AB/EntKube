using EntKube.Web.Data;
using EntKube.Web.Services;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace EntKube.Web.Tests;

/// <summary>
/// Tests for the CustomerAccessService which manages user access to specific
/// customers. A tenant admin grants access, and the portal uses this to filter
/// which customers a user can see.
/// </summary>
public class CustomerAccessServiceTests : IDisposable
{
    private readonly SqliteConnection connection;
    private readonly ApplicationDbContext db;
    private readonly TestDbContextFactory dbFactory;
    private readonly CustomerAccessService sut;

    public CustomerAccessServiceTests()
    {
        connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();

        DbContextOptions<ApplicationDbContext> options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseSqlite(connection)
            .Options;

        db = new ApplicationDbContext(options);
        dbFactory = new TestDbContextFactory(connection);
        db.Database.EnsureCreated();

        sut = new CustomerAccessService(dbFactory);
    }

    public void Dispose()
    {
        db.Dispose();
        connection.Dispose();
    }

    // ── Helpers ──

    /// <summary>
    /// Creates a tenant with a customer and an application user — the minimum
    /// scaffolding needed to grant customer access.
    /// </summary>
    private (ApplicationUser user, Customer customer, Tenant tenant) CreateUserAndCustomer(
        string userName = "alice@example.com",
        string customerName = "Contoso")
    {
        Tenant tenant = new() { Id = Guid.NewGuid(), Name = "TestCo", Slug = "testco" };
        db.Tenants.Add(tenant);

        Customer customer = new() { Id = Guid.NewGuid(), TenantId = tenant.Id, Name = customerName };
        db.Customers.Add(customer);

        ApplicationUser user = new() { Id = Guid.NewGuid().ToString(), UserName = userName, Email = userName };
        db.Users.Add(user);

        db.SaveChanges();
        return (user, customer, tenant);
    }

    // ════════════════════════════════════════════════════════════════
    //  GrantAccessAsync
    // ════════════════════════════════════════════════════════════════

    [Fact]
    public async Task GrantAccessAsync_CreatesAccessWithDefaultViewerRole()
    {
        // Arrange — we have a user and a customer.
        (ApplicationUser user, Customer customer, _) = CreateUserAndCustomer();

        // Act — grant the user access to the customer.
        CustomerAccess access = await sut.GrantAccessAsync(user.Id, customer.Id);

        // Assert — access is created with Viewer role by default.
        access.UserId.Should().Be(user.Id);
        access.CustomerId.Should().Be(customer.Id);
        access.Role.Should().Be(CustomerAccessRole.Viewer);
    }

    [Fact]
    public async Task GrantAccessAsync_WithOperatorRole_StoresRole()
    {
        // Arrange
        (ApplicationUser user, Customer customer, _) = CreateUserAndCustomer();

        // Act
        CustomerAccess access = await sut.GrantAccessAsync(
            user.Id, customer.Id, CustomerAccessRole.Operator);

        // Assert
        access.Role.Should().Be(CustomerAccessRole.Operator);
    }

    [Fact]
    public async Task GrantAccessAsync_DuplicateGrant_Throws()
    {
        // Arrange — grant access once.
        (ApplicationUser user, Customer customer, _) = CreateUserAndCustomer();
        await sut.GrantAccessAsync(user.Id, customer.Id);

        // Act & Assert — granting again should fail (composite key violation).
        Func<Task> act = () => sut.GrantAccessAsync(user.Id, customer.Id);
        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    // ════════════════════════════════════════════════════════════════
    //  RevokeAccessAsync
    // ════════════════════════════════════════════════════════════════

    [Fact]
    public async Task RevokeAccessAsync_RemovesAccess()
    {
        // Arrange
        (ApplicationUser user, Customer customer, _) = CreateUserAndCustomer();
        await sut.GrantAccessAsync(user.Id, customer.Id);

        // Act
        await sut.RevokeAccessAsync(user.Id, customer.Id);

        // Assert
        List<Customer> customers = await sut.GetAccessibleCustomersAsync(user.Id);
        customers.Should().BeEmpty();
    }

    // ════════════════════════════════════════════════════════════════
    //  UpdateRoleAsync
    // ════════════════════════════════════════════════════════════════

    [Fact]
    public async Task UpdateRoleAsync_ChangesRole()
    {
        // Arrange — user starts as Viewer.
        (ApplicationUser user, Customer customer, _) = CreateUserAndCustomer();
        await sut.GrantAccessAsync(user.Id, customer.Id, CustomerAccessRole.Viewer);

        // Act — promote to Admin.
        await sut.UpdateRoleAsync(user.Id, customer.Id, CustomerAccessRole.Admin);

        // Assert
        CustomerAccess? access = await sut.GetAccessAsync(user.Id, customer.Id);
        access.Should().NotBeNull();
        access!.Role.Should().Be(CustomerAccessRole.Admin);
    }

    // ════════════════════════════════════════════════════════════════
    //  GetAccessibleCustomersAsync
    // ════════════════════════════════════════════════════════════════

    [Fact]
    public async Task GetAccessibleCustomersAsync_ReturnsOnlyGrantedCustomers()
    {
        // Arrange — user has access to Contoso but not Fabrikam.
        (ApplicationUser user, Customer contoso, Tenant tenant) = CreateUserAndCustomer();

        Customer fabrikam = new() { Id = Guid.NewGuid(), TenantId = tenant.Id, Name = "Fabrikam" };
        db.Customers.Add(fabrikam);
        db.SaveChanges();

        await sut.GrantAccessAsync(user.Id, contoso.Id);

        // Act
        List<Customer> customers = await sut.GetAccessibleCustomersAsync(user.Id);

        // Assert — only Contoso, not Fabrikam.
        customers.Should().HaveCount(1);
        customers[0].Name.Should().Be("Contoso");
    }

    [Fact]
    public async Task GetAccessibleCustomersAsync_IncludesApps()
    {
        // Arrange — customer with an app.
        (ApplicationUser user, Customer customer, _) = CreateUserAndCustomer();

        App app = new() { Id = Guid.NewGuid(), CustomerId = customer.Id, Name = "billing-api" };
        db.Apps.Add(app);
        db.SaveChanges();

        await sut.GrantAccessAsync(user.Id, customer.Id);

        // Act
        List<Customer> customers = await sut.GetAccessibleCustomersAsync(user.Id);

        // Assert — the customer includes its apps.
        customers[0].Apps.Should().HaveCount(1);
        customers[0].Apps.First().Name.Should().Be("billing-api");
    }

    // ════════════════════════════════════════════════════════════════
    //  GetAccessAsync
    // ════════════════════════════════════════════════════════════════

    [Fact]
    public async Task GetAccessAsync_ReturnsNullWhenNoAccess()
    {
        // Arrange
        (ApplicationUser user, Customer customer, _) = CreateUserAndCustomer();

        // Act — no access has been granted.
        CustomerAccess? access = await sut.GetAccessAsync(user.Id, customer.Id);

        // Assert
        access.Should().BeNull();
    }

    // ════════════════════════════════════════════════════════════════
    //  GetCustomerUsersAsync
    // ════════════════════════════════════════════════════════════════

    [Fact]
    public async Task GetCustomerUsersAsync_ReturnsAllUsersWithAccess()
    {
        // Arrange — two users with access to the same customer.
        (ApplicationUser alice, Customer customer, _) = CreateUserAndCustomer("alice@example.com");

        ApplicationUser bob = new() { Id = Guid.NewGuid().ToString(), UserName = "bob@example.com", Email = "bob@example.com" };
        db.Users.Add(bob);
        db.SaveChanges();

        await sut.GrantAccessAsync(alice.Id, customer.Id, CustomerAccessRole.Admin);
        await sut.GrantAccessAsync(bob.Id, customer.Id, CustomerAccessRole.Viewer);

        // Act
        List<CustomerAccess> accesses = await sut.GetCustomerUsersAsync(customer.Id);

        // Assert
        accesses.Should().HaveCount(2);
        accesses.Should().Contain(a => a.User.UserName == "alice@example.com" && a.Role == CustomerAccessRole.Admin);
        accesses.Should().Contain(a => a.User.UserName == "bob@example.com" && a.Role == CustomerAccessRole.Viewer);
    }
}
