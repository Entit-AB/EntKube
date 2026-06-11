using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace EntKube.Web.Data;

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
        optionsBuilder.UseSqlite("DataSource=:memory:");
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
        optionsBuilder.UseNpgsql("Host=localhost;Database=entkube_design;Username=postgres;Password=postgres");
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
        optionsBuilder.UseSqlServer("Server=localhost;Database=entkube_design;Trusted_Connection=True;TrustServerCertificate=True");
        return new SqlServerApplicationDbContext(optionsBuilder.Options);
    }
}
