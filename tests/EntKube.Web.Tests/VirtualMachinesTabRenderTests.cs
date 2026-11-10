using System.Security.Claims;
using Bunit;
using EntKube.Web.Components.Pages.Tenants;
using EntKube.Web.Data;
using EntKube.Web.Services;
using FluentAssertions;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace EntKube.Web.Tests;

/// <summary>
/// Services › Virtual Machines as rendered: which cluster it opens on, and what it says about a cluster
/// without KubeVirt before anyone fills in a form that could not work there.
/// </summary>
public class VirtualMachinesTabRenderTests : BunitContext, IDisposable
{
    private static readonly byte[] TestRootKey = Convert.FromBase64String(TestServices.TestRootKeyBase64);

    private readonly InterceptingTestDb testDb;
    private readonly ApplicationDbContext db;
    private readonly Mock<IKubernetesClientFactory> k8s = new();

    private readonly Guid tenantId = Guid.NewGuid();
    private readonly Guid envId = Guid.NewGuid();
    private readonly Guid plain = Guid.NewGuid();
    private readonly Guid virtualized = Guid.NewGuid();

    public VirtualMachinesTabRenderTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;

        testDb = new InterceptingTestDb(TestRootKey);
        db = testDb.CreateContext();

        db.Tenants.Add(new Tenant { Id = tenantId, Name = "ENTIT", Slug = $"entit-{tenantId:N}" });
        db.Set<Data.Environment>().Add(new Data.Environment { Id = envId, TenantId = tenantId, Name = "production" });
        // "a-plain" sorts first, so opening on the KubeVirt cluster is a choice the page makes.
        db.KubernetesClusters.AddRange(
            new KubernetesCluster { Id = plain, TenantId = tenantId, EnvironmentId = envId, Name = "a-plain", ApiServerUrl = "https://a:6443" },
            new KubernetesCluster { Id = virtualized, TenantId = tenantId, EnvironmentId = envId, Name = "b-virt", ApiServerUrl = "https://b:6443" });
        db.SaveChanges();

        VaultService vault = testDb.CreateVaultService();
        vault.InitializeVaultAsync(tenantId).GetAwaiter().GetResult();
        testDb.SeedKubeconfigAsync(vault, tenantId, plain, TestKubeconfig.Valid).GetAwaiter().GetResult();
        testDb.SeedKubeconfigAsync(vault, tenantId, virtualized, TestKubeconfig.Valid).GetAwaiter().GetResult();

        k8s.Setup(x => x.GetJsonAllNamespacesAsync(It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("{\"items\":[]}");
        k8s.Setup(x => x.GetJsonAllNamespacesAsync("virtualmachines.kubevirt.io", It.IsAny<string>(),
                It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("""
                {"items":[{
                  "metadata": { "name": "web-01", "namespace": "vms" },
                  "spec": { "runStrategy": "Halted", "template": { "spec": { "domain": {} } } },
                  "status": { "printableStatus": "Stopped" }
                }]}
                """);

        Services.AddSingleton(new VirtualMachineService(
            testDb.Factory,
            new EntKube.Web.Services.Clusters.ClusterClientFactory(testDb.Factory, k8s.Object),
            new EntKube.Web.Modules.Api.CatalogApi(testDb.Factory),
            new EntKube.Web.Services.ClusterChanges.ClusterChangeGate(
                new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build(),
                NullLogger<EntKube.Web.Services.ClusterChanges.ClusterChangeGate>.Instance),
            new AuditService(testDb.Factory, new EntKube.Web.Modules.Api.DeliveryApi(testDb.Factory)),
            NullLogger<VirtualMachineService>.Instance));
        Services.AddSingleton(new ToastService());
        Services.AddSingleton<AuthenticationStateProvider>(new SignedInAs("nils"));
        Services.AddScoped<CurrentActor>();
    }

    public new void Dispose()
    {
        db.Dispose();
        testDb.Dispose();
        base.Dispose();
        GC.SuppressFinalize(this);
    }

    private sealed class SignedInAs(string name) : AuthenticationStateProvider
    {
        public override Task<AuthenticationState> GetAuthenticationStateAsync() =>
            Task.FromResult(new AuthenticationState(new ClaimsPrincipal(
                new ClaimsIdentity([new Claim(ClaimTypes.Name, name)], "test"))));
    }

    private void InstallKubeVirt(Guid clusterId)
    {
        db.ClusterComponents.Add(new ClusterComponent
        {
            Id = Guid.NewGuid(), ClusterId = clusterId, Name = "kubevirt", ComponentType = "Manifest",
            ReleaseName = "kubevirt", Status = ComponentStatus.Installed, Namespace = "kubevirt"
        });
        db.SaveChanges();
    }

    private IRenderedComponent<VirtualMachinesTab> RenderTab() =>
        Render<VirtualMachinesTab>(p => p.Add(c => c.TenantId, tenantId).Add(c => c.EnvironmentId, envId));

    [Fact]
    public void It_opens_on_the_cluster_that_has_kubevirt_and_lists_its_vms()
    {
        InstallKubeVirt(virtualized);

        IRenderedComponent<VirtualMachinesTab> tab = RenderTab();

        tab.WaitForAssertion(() => tab.Markup.Should().Contain("web-01"));
        tab.Find("select[aria-label=Cluster]").GetAttribute("value").Should().Be(virtualized.ToString());
        tab.Markup.Should().NotContain("KubeVirt is not installed");
        tab.Find("button[title=Start]").Should().NotBeNull("a halted VM offers Start, not Stop");
        tab.Find("button.btn-primary").HasAttribute("disabled").Should().BeFalse();
    }

    [Fact]
    public void Without_kubevirt_it_says_what_to_install_and_offers_no_create()
    {
        IRenderedComponent<VirtualMachinesTab> tab = RenderTab();

        tab.WaitForAssertion(() => tab.Markup.Should().Contain("KubeVirt is not installed on <b>a-plain</b>"));
        tab.FindAll("button").Single(b => b.TextContent.Contains("New virtual machine"))
            .HasAttribute("disabled").Should().BeTrue();
    }

    [Fact]
    public void Mounted_without_a_tenant_it_says_so_instead_of_blaming_the_cluster()
    {
        IRenderedComponent<VirtualMachinesTab> tab = Render<VirtualMachinesTab>(p => p.Add(c => c.TenantId, Guid.Empty));

        tab.Markup.Should().Contain("wiring mistake");
    }
}
