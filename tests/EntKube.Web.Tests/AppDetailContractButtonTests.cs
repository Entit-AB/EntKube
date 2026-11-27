using Bunit;
using EntKube.Web.Components.Pages.Tenants;
using EntKube.Web.Data;
using EntKube.Web.Services;
using EntKube.Web.Services.Telemetry;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace EntKube.Web.Tests;

/// <summary>
/// The application page, as a customer's apps open it.
///
/// <para>This class tested the tenant page's customer view, which has gone: a customer has pages
/// of their own now (<see cref="CustomerPageRenderTests"/>). What is left is the application
/// page's side of the contract move.</para>
/// </summary>
public class AppDetailContractButtonTests : BunitContext, IDisposable
{
    private static readonly byte[] TestRootKey = Convert.FromBase64String(
        "dGhpcyBpcyBhIDMyIGJ5dGUga2V5ISEhMTIzNDU2Nzg=");

    private readonly InterceptingTestDb testDb;
    private readonly ApplicationDbContext db;

    private readonly Guid tenantId = Guid.NewGuid();
    private readonly Customer acme;
    private readonly Customer boliden;

    public AppDetailContractButtonTests()
    {
        testDb = new InterceptingTestDb(TestRootKey);
        db = testDb.CreateContext();

        acme = new Customer { Id = Guid.NewGuid(), TenantId = tenantId, Name = "Acme AB" };
        boliden = new Customer { Id = Guid.NewGuid(), TenantId = tenantId, Name = "Boliden" };

        db.Tenants.Add(new Tenant { Id = tenantId, Name = "ENTIT", Slug = "entit" });
        db.Customers.AddRange(acme, boliden);

        // One app each, so a level-2 render has something of that customer's to show and the
        // two levels are told apart by more than a heading.
        db.Apps.Add(new App { Id = Guid.NewGuid(), CustomerId = acme.Id, Name = "Journalportalen" });
        db.Apps.Add(new App { Id = Guid.NewGuid(), CustomerId = boliden.Id, Name = "Gruvdata" });
        db.SaveChanges();

        VaultService vault = testDb.CreateVaultService();

        Services.AddSingleton(testDb.Factory);
        Services.AddSingleton(new TenantService(testDb.Factory, vault, new EntKube.Web.Modules.Api.CatalogApi(testDb.Factory)));
        Services.AddSingleton(new CustomerAccessService(testDb.Factory));
        Services.AddSingleton(new PrometheusService(
            new EntKube.Web.Modules.Api.DeliveryApi(testDb.Factory),
            new EntKube.Web.Services.Clusters.ClusterClientFactory(testDb.Factory, new Mock<IKubernetesClientFactory>().Object),
            new EntKube.Web.Modules.Api.CatalogApi(testDb.Factory),
            new PromQueryCache(),
            NullLogger<PrometheusService>.Instance));
    }

    public new void Dispose()
    {
        db.Dispose();
        testDb.Dispose();
        base.Dispose();
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Contracts are managed on the customer page; the application page has no Contract button
    /// and no Annex A editor any more (its Knowledge button stays).
    /// </summary>
    [Fact]
    public void The_application_page_has_no_contract_button()
    {
        Microsoft.Extensions.Configuration.IConfiguration none = new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build();
        EntKube.Web.Services.ClusterChanges.ClusterChangeGate gate = new(none, NullLogger<EntKube.Web.Services.ClusterChanges.ClusterChangeGate>.Instance);
        Mock<IKubernetesClientFactory> k8s = new();
        Services.AddSingleton(new KubernetesOperationsService(
            testDb.Factory,
            new AuditService(testDb.Factory, new EntKube.Web.Modules.Api.DeliveryApi(testDb.Factory)),
            new KyvernoPolicyService(testDb.Factory, k8s.Object,
                new EntKube.Web.Services.Clusters.ClusterClientFactory(testDb.Factory, k8s.Object), gate,
                new EntKube.Web.Modules.Api.CatalogApi(testDb.Factory), NullLogger<KyvernoPolicyService>.Instance),
            gate,
            new EntKube.Web.Services.Clusters.ClusterClientFactory(testDb.Factory, k8s.Object),
            new EntKube.Web.Modules.Api.CatalogApi(testDb.Factory),
            new EntKube.Web.Services.Rollouts.NoOpRolloutStarter(),
            NullLogger<KubernetesOperationsService>.Instance));
        Services.AddSingleton(new ToastService());
        JSInterop.Mode = JSRuntimeMode.Loose;

        App app = db.Apps.AsNoTracking().Single(a => a.CustomerId == acme.Id);
        IRenderedComponent<AppDetail> page = Render<AppDetail>(p => p.Add(c => c.App, app).Add(c => c.TenantId, tenantId));

        page.WaitForAssertion(() => page.FindAll("button").Should().Contain(b => b.TextContent.Trim() == "Knowledge"));
        page.FindAll("button").Should().NotContain(b => b.TextContent.Trim() == "Contract");
        page.Markup.Should().NotContain("Annex A");
    }
}
