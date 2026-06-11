using EntKube.Web.Data;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace EntKube.Web.Tests;

/// <summary>
/// Verifies that the ApplicationDbContext can be created and used with SQLite,
/// and that Identity tables are correctly defined in the model. This gives us
/// confidence that the EF Core model is valid and migrations will apply cleanly.
/// </summary>
public class ApplicationDbContextTests : IDisposable
{
    private readonly SqliteConnection connection;
    private readonly ApplicationDbContext context;

    public ApplicationDbContextTests()
    {
        // We use an in-memory SQLite database that stays open for the test's
        // lifetime. This is the fastest way to test EF Core behavior with a
        // real relational provider (not the InMemory provider which lacks
        // constraint checking).

        connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();

        DbContextOptions<ApplicationDbContext> options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseSqlite(connection)
            .Options;

        context = new ApplicationDbContext(options);
        context.Database.EnsureCreated();
    }

    [Fact]
    public void Constructor_WithSqliteOptions_CreatesContextSuccessfully()
    {
        // The context should be usable — if EnsureCreated worked, the model is valid.
        context.Should().NotBeNull();
        context.Database.CanConnect().Should().BeTrue();
    }

    [Fact]
    public void Model_ContainsIdentityTables()
    {
        // The Identity schema should define the Users table via the ApplicationUser entity.
        IEnumerable<string> entityTypes = context.Model.GetEntityTypes().Select(e => e.ClrType.Name);
        entityTypes.Should().Contain("ApplicationUser");
    }

    [Fact]
    public async Task Users_CanInsertAndRetrieve()
    {
        // Arrange — create a user entity to persist.
        ApplicationUser user = new()
        {
            Id = Guid.NewGuid().ToString(),
            UserName = "testuser@example.com",
            NormalizedUserName = "TESTUSER@EXAMPLE.COM",
            Email = "testuser@example.com",
            NormalizedEmail = "TESTUSER@EXAMPLE.COM",
            EmailConfirmed = true,
            SecurityStamp = Guid.NewGuid().ToString()
        };

        // Act — insert and read back.
        context.Users.Add(user);
        await context.SaveChangesAsync();

        ApplicationUser? retrieved = await context.Users.FirstOrDefaultAsync(u => u.UserName == "testuser@example.com");

        // Assert — the user should round-trip through the database.
        retrieved.Should().NotBeNull();
        retrieved!.Email.Should().Be("testuser@example.com");
    }

    public void Dispose()
    {
        context.Dispose();
        connection.Dispose();
    }
}
