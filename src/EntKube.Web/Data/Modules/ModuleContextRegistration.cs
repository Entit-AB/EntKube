using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using EntKube.Web.Services;

namespace EntKube.Web.Data.Modules;

/// <summary>
/// Registers a <see cref="IDbContextFactory{TContext}"/> for each module context.
///
/// <para>Factories rather than scoped contexts, for the same reason
/// <see cref="ApplicationDbContext"/> is offered that way: a Blazor Server circuit can have
/// several components running async work at once, and one shared context is not safe under
/// that.</para>
///
/// <para>Each factory carries the <c>KubeconfigMaterializationInterceptor</c>, which is not
/// optional — it is what decrypts a registered cluster's kubeconfig out of the vault on
/// materialization. A module context without it would hand back a cluster whose
/// <c>Kubeconfig</c> is silently null, which looks like a cluster that was never given one.</para>
/// </summary>
internal static class ModuleContextRegistration
{
    internal static IServiceCollection AddModuleContexts(
        this IServiceCollection services, string databaseProvider, string connectionString)
    {
        Add<IdentityDbContext>(services, databaseProvider, connectionString);
        Add<FleetDbContext>(services, databaseProvider, connectionString);
        Add<CatalogDbContext>(services, databaseProvider, connectionString);
        Add<DataServicesDbContext>(services, databaseProvider, connectionString);
        Add<MailDbContext>(services, databaseProvider, connectionString);
        Add<DeliveryDbContext>(services, databaseProvider, connectionString);
        Add<ConnectivityDbContext>(services, databaseProvider, connectionString);
        Add<SecretsDbContext>(services, databaseProvider, connectionString);
        Add<TelemetryDbContext>(services, databaseProvider, connectionString);
        Add<CostDbContext>(services, databaseProvider, connectionString);
        Add<SupportDbContext>(services, databaseProvider, connectionString);
        Add<AdvisorDbContext>(services, databaseProvider, connectionString);

        return services;
    }

    private static void Add<TContext>(
        IServiceCollection services, string databaseProvider, string connectionString)
        where TContext : ModuleDbContext
    {
        services.AddDbContextFactory<TContext>((sp, options) =>
        {
            switch (databaseProvider)
            {
                case "Postgres":
                    options.UseNpgsql(connectionString);
                    break;

                case "SqlServer":
                    options.UseSqlServer(connectionString);
                    break;

                default:
                    options.UseSqlite(connectionString);
                    break;
            }

            options
                .ConfigureWarnings(w => w.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning))
                .AddInterceptors(sp.GetRequiredService<KubeconfigMaterializationInterceptor>());
        });
    }
}
