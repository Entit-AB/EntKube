using EntKube.Web.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace EntKube.Web.Tests;

/// <summary>
/// A test-only IDbContextFactory that produces ApplicationDbContext instances
/// sharing the same in-memory SQLite connection. This ensures all contexts
/// created by the factory see the same seeded test data.
/// </summary>
public sealed class TestDbContextFactory : IDbContextFactory<ApplicationDbContext>
{
    private readonly SqliteConnection connection;

    public TestDbContextFactory(SqliteConnection connection)
    {
        this.connection = connection;
    }

    public ApplicationDbContext CreateDbContext()
    {
        DbContextOptions<ApplicationDbContext> options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseSqlite(connection)
            .Options;
        return new ApplicationDbContext(options);
    }
}
