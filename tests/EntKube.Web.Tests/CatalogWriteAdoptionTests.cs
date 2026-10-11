using EntKube.Web.Data;
using EntKube.Web.Services;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace EntKube.Web.Tests;

/// <summary>
/// The contract's write half, which until now had no callers at all.
///
/// <para><b>Why this matters more than the reads.</b> <c>ICatalogApi</c> was documented as
/// read-only because the first measurement of this table looked for
/// <c>.ClusterComponents.Add/Remove/Update</c>, found none, and concluded nothing outside Catalog
/// wrote to it. EF writes through change tracking, so <c>component.HelmValues = x</c> followed by
/// <c>SaveChangesAsync</c> is a write with no <c>Update()</c> call anywhere near it — and there
/// were 22 of them. <see cref="EntKube.Contracts.Catalog.ICatalogApi.SetHelmValuesAsync"/> was
/// written for exactly that and then adopted by nobody, which is the same "interface with no
/// consumers" problem the contract work exists to fix.</para>
///
/// <para>Replacing a direct EF write with a contract call is the kind of change that fails
/// silently: a manifest that stops being stored looks like a component that was never configured,
/// and nothing says why. These assert the row.</para>
/// </summary>
public class CatalogWriteAdoptionTests : IDisposable
{
    private static readonly byte[] TestRootKey = Convert.FromBase64String(TestServices.TestRootKeyBase64);

    private readonly InterceptingTestDb testDb;
    private readonly ApplicationDbContext db;

    private Guid ours, theirs, ourComponent, theirComponent;

    public CatalogWriteAdoptionTests()
    {
        testDb = new InterceptingTestDb(TestRootKey);
        db = testDb.CreateContext();
    }

    public void Dispose()
    {
        db.Dispose();
        testDb.Dispose();
        GC.SuppressFinalize(this);
    }

    private EntKube.Web.Modules.Api.CatalogApi Catalog() => new(testDb.Factory);

    private async Task SeedAsync()
    {
        ours = Guid.NewGuid();
        theirs = Guid.NewGuid();
        ourComponent = await SeedTenantAsync(ours, "ours");
        theirComponent = await SeedTenantAsync(theirs, "theirs");
    }

    private async Task<Guid> SeedTenantAsync(Guid tenantId, string label)
    {
        Guid clusterId = Guid.NewGuid();
        Guid envId = Guid.NewGuid();
        Guid componentId = Guid.NewGuid();

        db.Tenants.Add(new Tenant { Id = tenantId, Name = label, Slug = $"{label}-{tenantId:N}" });
        db.Set<Data.Environment>().Add(new Data.Environment
        {
            Id = envId, TenantId = tenantId, Name = "production",
        });
        db.KubernetesClusters.Add(new KubernetesCluster
        {
            Id = clusterId, TenantId = tenantId, EnvironmentId = envId,
            Name = $"{label}-cluster", ApiServerUrl = "https://k8s.example.com",
        });
        db.ClusterComponents.Add(new ClusterComponent
        {
            Id = componentId, ClusterId = clusterId, Name = "openldap",
            ComponentType = "Helm", ReleaseName = "ldap", Namespace = "directory",
            Status = ComponentStatus.Installed, HelmValues = "existing: values",
            Configuration = "{\"keep\":true}",
        });
        await db.SaveChangesAsync();

        return componentId;
    }

    private string? StoredValues(Guid componentId)
    {
        using ApplicationDbContext read = testDb.CreateContext();
        return read.ClusterComponents.Single(c => c.Id == componentId).HelmValues;
    }

    private string? StoredConfiguration(Guid componentId)
    {
        using ApplicationDbContext read = testDb.CreateContext();
        return read.ClusterComponents.Single(c => c.Id == componentId).Configuration;
    }

    // ════════════════════════════════════════════════════════════════

    [Fact]
    public async Task Setting_helm_values_through_the_contract_actually_persists_them()
    {
        await SeedAsync();

        EntKube.Contracts.Catalog.InstalledComponent? result =
            await Catalog().SetHelmValuesAsync(ours, ourComponent, "rendered: manifest");

        result.Should().NotBeNull("the caller gets the component back as it now stands");
        result!.HelmValues.Should().Be("rendered: manifest");

        StoredValues(ourComponent).Should().Be("rendered: manifest",
            "a write that returns the right thing and saves nothing is the failure this is for");
    }

    /// <summary>
    /// The configuration blob is left alone when it is not supplied. Both converted callers pass
    /// only the manifest, so a <c>SetHelmValuesAsync</c> that cleared it would wipe the installer
    /// module's own JSON — which is what the component's screen reads to repopulate its form.
    /// </summary>
    [Fact]
    public async Task Setting_helm_values_leaves_the_configuration_untouched()
    {
        await SeedAsync();

        await Catalog().SetHelmValuesAsync(ours, ourComponent, "rendered: manifest");

        StoredConfiguration(ourComponent).Should().Be("{\"keep\":true}");
    }

    [Fact]
    public async Task Another_tenants_component_is_not_written_and_reads_as_absent()
    {
        await SeedAsync();

        EntKube.Contracts.Catalog.InstalledComponent? result =
            await Catalog().SetHelmValuesAsync(ours, theirComponent, "rendered: manifest");

        result.Should().BeNull("not this tenant's reads as absent rather than as a forbidden write");
        StoredValues(theirComponent).Should().Be("existing: values", "and nothing was written");
    }

    // ════════════════════════════════════════════════════════════════
    //  Through the service that was converted to use it
    // ════════════════════════════════════════════════════════════════

    /// <summary>
    /// <c>OpenLdapService</c> held the last tracked write to this table from outside Catalog. This
    /// drives it end to end: configure a directory, refresh, and read the row back.
    /// </summary>
    [Fact]
    public async Task Refreshing_openldap_values_stores_a_manifest_through_the_contract()
    {
        await SeedAsync();

        OpenLdapService ldap = new(
            testDb.Factory, testDb.CreateVaultService(), null!, null!, Catalog(),
            NullLogger<OpenLdapService>.Instance);

        db.Set<OpenLdapComponentConfig>().Add(new OpenLdapComponentConfig
        {
            Id = Guid.NewGuid(),
            TenantId = ours,
            ClusterComponentId = ourComponent,
            BaseDn = "dc=example,dc=com",
            Organization = "Example",
        });
        await db.SaveChangesAsync();

        await ldap.RefreshHelmValuesIfConfiguredAsync(ours, ourComponent);

        string? stored = StoredValues(ourComponent);

        stored.Should().NotBe("existing: values", "the refresh replaced the values");
        stored.Should().NotBeNullOrWhiteSpace();
    }

    /// <summary>
    /// The expected-name check that moved from SQL into memory when
    /// <c>StalwartService.ResolveComponentAsync</c> went to the contract. The old query filtered
    /// <c>c.Name == expectedName</c> in the database; <c>GetComponentAsync</c> answers "this
    /// component, if it is this tenant's" and knows nothing about which component the caller
    /// meant.
    ///
    /// <para>Dropping it would let the rspamd refresh render its manifest over a roundcube
    /// component — one webmail's Helm values written onto the other's row, and both would then
    /// deploy wrong. No test covered this before, in either form.</para>
    /// </summary>
    [Fact]
    public async Task Refreshing_rspamd_against_a_different_component_kind_writes_nothing()
    {
        await SeedAsync();

        // Our own component, our own tenant — but it is an openldap component, not rspamd.
        StalwartService mail = new(
            testDb.Factory, testDb.CreateVaultService(), null!, null!, null!, null!, null!,
            Catalog(), NullLogger<StalwartService>.Instance);

        await mail.RefreshRspamdManifestAsync(ours, ourComponent);

        StoredValues(ourComponent).Should().Be("existing: values",
            "the component is not an rspamd one, so its values are not rspamd's to rewrite");
    }

    [Fact]
    public async Task Refreshing_openldap_values_for_another_tenant_writes_nothing()
    {
        await SeedAsync();

        OpenLdapService ldap = new(
            testDb.Factory, testDb.CreateVaultService(), null!, null!, Catalog(),
            NullLogger<OpenLdapService>.Instance);

        db.Set<OpenLdapComponentConfig>().Add(new OpenLdapComponentConfig
        {
            Id = Guid.NewGuid(),
            TenantId = ours,
            ClusterComponentId = theirComponent,
            BaseDn = "dc=example,dc=com",
            Organization = "Example",
        });
        await db.SaveChangesAsync();

        await ldap.RefreshHelmValuesIfConfiguredAsync(ours, theirComponent);

        StoredValues(theirComponent).Should().Be("existing: values");
    }
}
