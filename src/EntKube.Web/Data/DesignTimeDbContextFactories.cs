using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.Extensions.DependencyInjection;

namespace EntKube.Web.Data;

/// <summary>
/// Shared helper for the design-time factories below.
///
/// The Identity EF model only includes the passkey table
/// (<c>AspNetUserPasskeys</c>) when the store schema version is v3+.
/// At runtime that version is configured on <see cref="IdentityOptions"/>
/// in Program.cs, and <see cref="IdentityDbContext{TUser}"/> reads it back
/// through the context's <em>application service provider</em>.
///
/// The design-time factories new up a context with no DI container, so
/// without this the tooling falls back to the default (pre-passkey) schema
/// and <c>dotnet ef migrations add</c> silently omits the passkey table.
/// We attach a minimal service provider that carries the same schema
/// version so generated migrations match the runtime model.
/// </summary>
internal static class DesignTimeIdentity
{
    public static readonly IServiceProvider ServiceProvider = BuildServiceProvider();

    private static IServiceProvider BuildServiceProvider()
    {
        ServiceCollection services = new();
        services.AddOptions();
        services.Configure<IdentityOptions>(options =>
            options.Stores.SchemaVersion = IdentitySchemaVersions.Version3);
        return services.BuildServiceProvider();
    }
}

/// <summary>
/// Design-time factory for the SQLite context. EF Core tooling uses this when
/// running `dotnet ef migrations add` targeting the default (SQLite) context.
/// It creates a throwaway context with a dummy connection string so the tooling
/// can inspect the model and generate migration code.
/// </summary>
public class SqliteDesignTimeDbContextFactory : IDesignTimeDbContextFactory<ApplicationDbContext>
{
    public ApplicationDbContext CreateDbContext(string[] args)
    {
        DbContextOptionsBuilder<ApplicationDbContext> optionsBuilder = new();
        optionsBuilder.UseSqlite("DataSource=:memory:")
            .UseApplicationServiceProvider(DesignTimeIdentity.ServiceProvider);
        return new ApplicationDbContext(optionsBuilder.Options);
    }
}

/// <summary>
/// Design-time factory for the PostgreSQL context. Used when generating
/// PostgreSQL-specific migrations via:
///   dotnet ef migrations add <Name> --context PostgresApplicationDbContext --output-dir Data/Migrations/Postgres
/// </summary>
public class PostgresDesignTimeDbContextFactory : IDesignTimeDbContextFactory<PostgresApplicationDbContext>
{
    public PostgresApplicationDbContext CreateDbContext(string[] args)
    {
        DbContextOptionsBuilder<PostgresApplicationDbContext> optionsBuilder = new();
        optionsBuilder.UseNpgsql("Host=localhost;Database=entkube_design;Username=postgres;Password=postgres")
            .UseApplicationServiceProvider(DesignTimeIdentity.ServiceProvider);
        return new PostgresApplicationDbContext(optionsBuilder.Options);
    }
}

/// <summary>
/// Design-time factory for the SQL Server context. Used when generating
/// SQL Server-specific migrations via:
///   dotnet ef migrations add <Name> --context SqlServerApplicationDbContext --output-dir Data/Migrations/SqlServer
/// </summary>
public class SqlServerDesignTimeDbContextFactory : IDesignTimeDbContextFactory<SqlServerApplicationDbContext>
{
    public SqlServerApplicationDbContext CreateDbContext(string[] args)
    {
        DbContextOptionsBuilder<SqlServerApplicationDbContext> optionsBuilder = new();
        optionsBuilder.UseSqlServer("Server=localhost;Database=entkube_design;Trusted_Connection=True;TrustServerCertificate=True")
            .UseApplicationServiceProvider(DesignTimeIdentity.ServiceProvider);
        return new SqlServerApplicationDbContext(optionsBuilder.Options);
    }
}
