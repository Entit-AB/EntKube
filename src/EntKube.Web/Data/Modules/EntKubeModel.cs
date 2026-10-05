using Microsoft.EntityFrameworkCore;

namespace EntKube.Web.Data.Modules;

/// <summary>
/// The whole EntKube model, assembled from one configuration file per module.
///
/// <para>Every context builds the <em>same</em> model — <see cref="ApplicationDbContext"/>
/// and each per-module context alike. What differs between them is not the model but what
/// they <em>expose</em>: a module context declares <c>DbSet</c>s only for the tables its
/// module owns, so reaching another module's table from it is a compile error rather than
/// a code review.</para>
///
/// <para><b>Why one shared model rather than twelve narrow ones.</b> Narrowing the model
/// per module would mean severing the 94 structural cross-module foreign keys first — the
/// navigation properties drag the other module's entities in behind them. That severing is
/// real work this codebase should do, but it is work that belongs with the service
/// contracts, one module at a time. Sharing the model gets the useful half of the boundary
/// now, for free, and provably without changing a single mapping.</para>
/// </summary>
internal static class EntKubeModel
{
    /// <summary>
    /// Applies every module's configuration, in the order the modules are declared.
    /// Callers must have already called <c>base.OnModelCreating</c>.
    /// </summary>
    internal static void ConfigureAll(ModelBuilder builder)
    {
        IdentityModel.Configure(builder);
        FleetModel.Configure(builder);
        CatalogModel.Configure(builder);
        DataServicesModel.Configure(builder);
        MailModel.Configure(builder);
        DeliveryModel.Configure(builder);
        ConnectivityModel.Configure(builder);
        SecretsModel.Configure(builder);
        TelemetryModel.Configure(builder);
        CostModel.Configure(builder);
        SupportModel.Configure(builder);
        AdvisorModel.Configure(builder);
    }
}
