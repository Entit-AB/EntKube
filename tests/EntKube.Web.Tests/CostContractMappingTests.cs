using EntKube.Contracts.Cost;
using EntKube.Web.Data;
using EntKube.Web.Data.Modules;
using EntKube.Web.Modules.Api;
using EntKube.Web.Services.Cost;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace EntKube.Web.Tests;

/// <summary>
/// The mapping across Cost's contract boundary, field by field.
///
/// <para><b>Why this is the test that matters for a contract.</b> A contract carries its own
/// types — rule 2 of <c>EntKube.Contracts/README.md</c> — so every method crosses a mapping, and
/// a mapping is where a field vanishes without a sound. A dropped field still compiles, still
/// serialises, and still looks like a price sheet; what changes is that a price silently reads
/// zero. <c>ClusterRate</c> has twelve members and <c>ClusterRatePatch</c> is constructed
/// positionally from nine, which is where a <em>swap</em> hides rather than a drop.</para>
///
/// <para>⚠️ So every value in these fixtures is <b>distinct</b>, including the two booleans, which
/// are set to opposite values precisely so that transposing them fails. Tests that pass 0 for
/// several decimals cannot tell a correct mapping from a shuffled one.</para>
///
/// <para>This is not hypothetical for this record: <c>ChargeIdleCapacity</c> was missing from the
/// public API's request type, its response and its handler's initializer at once, and the
/// consequence was an API write silently re-enabling a billing option worth about half the cloud
/// bill.</para>
/// </summary>
public class CostContractMappingTests : IDisposable
{
    private readonly SqliteConnection connection;
    private readonly ApplicationDbContext db;
    private readonly TestDbContextFactory factory;
    private readonly ICostApi api;

    private readonly Guid tenantId = Guid.NewGuid();
    private readonly Guid clusterId = Guid.NewGuid();

    public CostContractMappingTests()
    {
        connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();
        db = new ApplicationDbContext(
            new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(connection).Options);
        db.Database.EnsureCreated();
        factory = new TestDbContextFactory(connection);

        db.Tenants.Add(new Tenant { Id = tenantId, Name = "Acme", Slug = "acme" });

        Data.Environment environment = new() { Id = Guid.NewGuid(), TenantId = tenantId, Name = "Production" };
        db.Environments.Add(environment);
        db.KubernetesClusters.Add(new KubernetesCluster
        {
            Id = clusterId, TenantId = tenantId, EnvironmentId = environment.Id,
            Name = "prod", ApiServerUrl = "https://prod.example.com:6443",
        });
        db.SaveChanges();

        api = new CostApi(new CostRateService(factory));
    }

    public void Dispose()
    {
        db.Dispose();
        connection.Dispose();
        GC.SuppressFinalize(this);
    }

    /// <summary>Every member a different value, so neither a drop nor a swap can coincide.</summary>
    private static readonly ClusterRatePatch Distinct = new(
        CpuCoreHourCost: 0.11m,
        MemoryGiBHourCost: 0.22m,
        StorageGiBMonthCost: 0.33m,
        ClusterMonthlyOverhead: 444m,
        Currency: "EUR",
        ChargeOnRequests: false,
        ChargeIdleCapacity: true,
        LoadBalancerMonthlyCost: 55m,
        PublicIpMonthlyCost: 6m);

    [Fact]
    public async Task Every_field_of_a_price_sheet_survives_the_write_and_the_read()
    {
        ClusterRate? written = await api.PatchRateAsync(tenantId, clusterId, Distinct, "operator");

        written.Should().NotBeNull();

        // Read back through the contract as well, so the assertions cover both directions rather
        // than only the value the write happened to return.
        ClusterRate? read = await api.GetRateAsync(tenantId, clusterId);
        read.Should().NotBeNull();

        foreach (ClusterRate rate in new[] { written!, read! })
        {
            rate.ClusterId.Should().Be(clusterId);
            rate.CpuCoreHourCost.Should().Be(0.11m);
            rate.MemoryGiBHourCost.Should().Be(0.22m);
            rate.StorageGiBMonthCost.Should().Be(0.33m);
            rate.ClusterMonthlyOverhead.Should().Be(444m);
            rate.LoadBalancerMonthlyCost.Should().Be(55m);
            rate.PublicIpMonthlyCost.Should().Be(6m);
            rate.Currency.Should().Be("EUR");
            rate.ChargeOnRequests.Should().BeFalse();
            rate.ChargeIdleCapacity.Should().BeTrue("opposite to ChargeOnRequests, so a swap shows");
            rate.UpdatedBy.Should().Be("operator");
            rate.UpdatedAt.Should().BeAfter(DateTime.UtcNow.AddMinutes(-5));
        }
    }

    /// <summary>
    /// The contract's patch and the service's agree member for member. ⚠️ Both are positional
    /// records of nine values, six of them numeric, so transposing two is a one-character mistake
    /// that nothing else would catch — the test above would too, which is why its values are all
    /// different, and this one states the correspondence directly.
    /// </summary>
    [Fact]
    public void The_contracts_patch_and_the_services_agree_member_for_member()
    {
        string[] contract = [.. typeof(ClusterRatePatch)
            .GetProperties().Select(p => p.Name)];

        string[] service = [.. typeof(CostRatePatch)
            .GetProperties().Select(p => p.Name)];

        contract.Should().Equal(service,
            "two separate declarations of the same idea, in order — if one gains a member the "
            + "other has to, or CostApi's positional construction silently shifts");
    }

    /// <summary>
    /// The refusal survives the boundary: a cluster that is not this tenant's yields null, and
    /// null is what lets the route answer 404 rather than invent an empty sheet.
    /// </summary>
    [Fact]
    public async Task Another_tenants_cluster_is_not_priced_through_the_contract()
    {
        Guid otherTenant = Guid.NewGuid();

        (await api.PatchRateAsync(otherTenant, clusterId, Distinct, "operator")).Should().BeNull();
        (await api.GetRateAsync(otherTenant, clusterId)).Should().BeNull();
        (await api.DeleteRateAsync(otherTenant, clusterId)).Should().BeFalse();

        await using CostDbContext read = ((IDbContextFactory<CostDbContext>)factory).CreateDbContext();
        (await read.ClusterCostRates.AnyAsync()).Should().BeFalse(
            "the refusals must also not have written a sheet");
    }

    /// <summary>
    /// A cluster with no sheet reads as null rather than as a zero-priced one — the distinction the
    /// route's 404 rests on, since a provider reading back a destroyed resource has to be able to
    /// plan a re-create.
    /// </summary>
    [Fact]
    public async Task A_cluster_with_no_sheet_reads_as_null()
    {
        (await api.GetRateAsync(tenantId, clusterId)).Should().BeNull();
        (await api.ListRatesAsync(tenantId)).Should().BeEmpty();
    }
}
