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
/// Which of CustomerTab's three levels it opens on.
///
/// <para>The tenant tree lists the customers as leaves of their own heading, so clicking one
/// has to land on <em>that</em> customer rather than on the list of all of them — otherwise
/// the leaf is a decoration and the operator still has to pick the customer a second time,
/// from a page that cannot tell them which one they just clicked.</para>
///
/// <para>The list is still the right answer when nothing was named: <c>?section=customers</c>,
/// which is what the top-nav mega menu links to, asks for all of them.</para>
/// </summary>
public class CustomerTabRenderTests : BunitContext, IDisposable
{
    private static readonly byte[] TestRootKey = Convert.FromBase64String(
        "dGhpcyBpcyBhIDMyIGJ5dGUga2V5ISEhMTIzNDU2Nzg=");

    private readonly InterceptingTestDb testDb;
    private readonly ApplicationDbContext db;

    private readonly Guid tenantId = Guid.NewGuid();
    private readonly Customer acme;
    private readonly Customer boliden;

    public CustomerTabRenderTests()
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
    /// <b>The one that matters.</b> Selecting a customer in the tree opens that customer,
    /// not the list — the whole point of giving them a heading of their own.
    /// </summary>
    [Fact]
    public void Naming_a_customer_opens_that_customer()
    {
        IRenderedComponent<CustomerTab> tab = Render<CustomerTab>(p => p
            .Add(c => c.TenantId, tenantId)
            .Add(c => c.InitialCustomerId, acme.Id));

        // Level 2 is the only level with a way back to the list.
        tab.Markup.Should().Contain("All Customers");
        tab.Markup.Should().Contain("Acme AB");
        tab.Markup.Should().Contain("Journalportalen");

        // And it is one customer's page, not both customers listed.
        tab.Markup.Should().NotContain("Gruvdata");
    }

    /// <summary>
    /// A customer id that does not belong to this tenant — a stale link, or one hand-edited —
    /// falls back to the list rather than rendering an empty page about nobody.
    /// </summary>
    [Fact]
    public void An_unknown_customer_falls_back_to_the_list()
    {
        IRenderedComponent<CustomerTab> tab = Render<CustomerTab>(p => p
            .Add(c => c.TenantId, tenantId)
            .Add(c => c.InitialCustomerId, Guid.NewGuid()));

        tab.Markup.Should().NotContain("All Customers");
        tab.Markup.Should().Contain("Acme AB");
        tab.Markup.Should().Contain("Boliden");
    }

    /// <summary>
    /// Naming nobody still asks for everybody: this is <c>?section=customers</c>, which the
    /// top-nav mega menu links to and which must keep showing the whole list.
    /// </summary>
    [Fact]
    public void Naming_nobody_opens_the_list()
    {
        IRenderedComponent<CustomerTab> tab = Render<CustomerTab>(p => p
            .Add(c => c.TenantId, tenantId));

        tab.Markup.Should().NotContain("All Customers");
        tab.Markup.Should().Contain("Acme AB");
        tab.Markup.Should().Contain("Boliden");
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
