using System.Text.RegularExpressions;
using EntKube.Web.Data;
using EntKube.Web.Data.Modules;
using EntKube.Web.Services.Cost;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace EntKube.Web.Tests;

/// <summary>
/// What a partial write to a cluster's price sheet must leave alone.
///
/// <para><b>The defect these were written for.</b> <c>PUT /api/v1/cost-rates/{clusterId}</c> built
/// a whole <c>ClusterCostRate</c> from an optional request body and handed it to
/// <c>SaveAsync</c>, which writes every field. So each <c>?? default</c> in that handler was an
/// <em>overwrite</em> rather than a fallback, and one field was not in the initializer at all:</para>
///
/// <list type="number">
///   <item><c>ChargeIdleCapacity</c> was absent from the request type, so no client could set it;
///     absent from the response, so no client could read it back to detect drift; and absent from
///     the object the handler built, so it took the entity's <c>= true</c> on <b>every</b> write.
///     A tenant who turned idle-capacity charging off in the settings page had it silently turned
///     back on by any API write to that cluster's sheet — and the endpoint's own comment says the
///     caller it exists for is a Terraform provider doing update-in-place, which is precisely the
///     client that writes repeatedly.</item>
///   <item><c>LoadBalancerMonthlyCost</c> and <c>PublicIpMonthlyCost</c> were zeroed when omitted,
///     which is the opposite of what their own comment in <c>CostRateInput</c> promised: "Nullable
///     so an existing client that does not send them leaves the stored values alone rather than
///     silently zeroing a rate."</item>
/// </list>
///
/// <para>⚠️ <b>There was no test of any kind for these four routes.</b> The only mention of
/// <c>CostRateService</c> anywhere in this suite was a line in
/// <see cref="PublicApiContractAdoptionTests"/> listing it as remaining work. Full CRUD, built
/// deliberately so a provider could manage price sheets declaratively, and nothing exercised it —
/// which is how a field could be unreadable, unwritable and silently reset all at once.</para>
///
/// <para>The settings page was never affected: <c>CostTab.razor</c> sets all nine fields from a
/// form that shows all nine, so a full replacement is the correct operation there.
/// <c>SaveAsync</c> keeps that job; <c>PatchAsync</c> is for a body where absence means
/// something.</para>
/// </summary>
public class CostRatePatchTests : IDisposable
{
    private readonly SqliteConnection connection;
    private readonly ApplicationDbContext db;
    private readonly TestDbContextFactory factory;
    private readonly CostRateService rates;

    private readonly Guid tenantId = Guid.NewGuid();
    private readonly Guid otherTenantId = Guid.NewGuid();
    private readonly Guid clusterId = Guid.NewGuid();
    private readonly Guid otherClusterId = Guid.NewGuid();

    public CostRatePatchTests()
    {
        connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();
        db = new ApplicationDbContext(
            new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(connection).Options);
        db.Database.EnsureCreated();

        db.Tenants.Add(new Tenant { Id = tenantId, Name = "Acme", Slug = "acme" });
        db.Tenants.Add(new Tenant { Id = otherTenantId, Name = "Other", Slug = "other" });

        Data.Environment ours = new() { Id = Guid.NewGuid(), TenantId = tenantId, Name = "Production" };
        Data.Environment theirs = new() { Id = Guid.NewGuid(), TenantId = otherTenantId, Name = "Production" };
        db.Environments.AddRange(ours, theirs);

        db.KubernetesClusters.Add(new KubernetesCluster
        {
            Id = clusterId, TenantId = tenantId, EnvironmentId = ours.Id,
            Name = "prod", ApiServerUrl = "https://prod.example.com:6443",
        });
        db.KubernetesClusters.Add(new KubernetesCluster
        {
            Id = otherClusterId, TenantId = otherTenantId, EnvironmentId = theirs.Id,
            Name = "theirs", ApiServerUrl = "https://theirs.example.com:6443",
        });
        db.SaveChanges();

        factory = new TestDbContextFactory(connection);
        rates = new CostRateService(factory);
    }

    public void Dispose()
    {
        db.Dispose();
        connection.Dispose();
        GC.SuppressFinalize(this);
    }

    /// <summary>A sheet with every optional field set away from its default, so a reset shows.</summary>
    private async Task<ClusterCostRate> StoredSheetAsync()
    {
        ClusterCostRate stored = await rates.SaveAsync(
            tenantId, clusterId,
            new ClusterCostRate
            {
                CpuCoreHourCost = 0.04m,
                MemoryGiBHourCost = 0.01m,
                StorageGiBMonthCost = 0.10m,
                ClusterMonthlyOverhead = 250m,
                LoadBalancerMonthlyCost = 18m,
                PublicIpMonthlyCost = 4m,
                Currency = "EUR",
                ChargeOnRequests = false,
                ChargeIdleCapacity = false,
            },
            updatedBy: "operator")
            ?? throw new Xunit.Sdk.XunitException("the fixture could not store a sheet to patch");

        return stored;
    }

    /// <summary>The four required prices, with every optional field omitted.</summary>
    private static CostRatePatch PricesOnly() => new(
        CpuCoreHourCost: 0.05m,
        MemoryGiBHourCost: 0.02m,
        StorageGiBMonthCost: 0.11m,
        ClusterMonthlyOverhead: 300m);

    private async Task<ClusterCostRate> ReloadAsync()
    {
        using CostDbContext read = ((IDbContextFactory<CostDbContext>)factory).CreateDbContext();
        return await read.ClusterCostRates.AsNoTracking().SingleAsync(r => r.ClusterId == clusterId);
    }

    /// <summary>
    /// ⭐ <b>The defect itself.</b> An operator turned idle-capacity charging off; an API write
    /// that says nothing about it must not turn it back on.
    ///
    /// <para>Idle capacity is not a minor flag: it was about half the cloud bill in the case that
    /// introduced it, so silently re-enabling it changes what every subsequent cost report says.
    /// </para>
    /// </summary>
    [Fact]
    public async Task An_omitted_idle_capacity_flag_keeps_the_stored_value()
    {
        await StoredSheetAsync();

        await rates.PatchAsync(tenantId, clusterId, PricesOnly(), "api-token:terraform");

        ClusterCostRate after = await ReloadAsync();

        after.ChargeIdleCapacity.Should().BeFalse(
            "the operator turned it off and this write said nothing about it");

        // The prices it DID send are written, so this is not passing by ignoring the patch.
        after.CpuCoreHourCost.Should().Be(0.05m);
        after.ClusterMonthlyOverhead.Should().Be(300m);
    }

    /// <summary>
    /// The same property for the two fields whose comment already promised it, and did not have it.
    /// </summary>
    [Fact]
    public async Task An_omitted_network_price_keeps_the_stored_value_rather_than_zeroing_it()
    {
        await StoredSheetAsync();

        await rates.PatchAsync(tenantId, clusterId, PricesOnly(), "api-token:terraform");

        ClusterCostRate after = await ReloadAsync();

        after.LoadBalancerMonthlyCost.Should().Be(18m);
        after.PublicIpMonthlyCost.Should().Be(4m);
        after.Currency.Should().Be("EUR", "an omitted currency is not a request for USD");
        after.ChargeOnRequests.Should().BeFalse();
    }

    /// <summary>
    /// And absence has to mean absence, not "false" — otherwise the fix above would be a
    /// different bug, where a client could never turn the flag off.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_supplied_idle_capacity_flag_is_written(bool charge)
    {
        await StoredSheetAsync();

        await rates.PatchAsync(
            tenantId, clusterId,
            PricesOnly() with { ChargeIdleCapacity = charge },
            "api-token:terraform");

        (await ReloadAsync()).ChargeIdleCapacity.Should().Be(charge);
    }

    /// <summary>
    /// A sheet that does not exist yet needs no special case: "keep what is there" resolves to the
    /// entity's own initializers, which are the documented defaults.
    /// </summary>
    [Fact]
    public async Task A_new_sheet_takes_the_documented_defaults_for_what_was_not_sent()
    {
        ClusterCostRate? created = await rates.PatchAsync(
            tenantId, clusterId, PricesOnly(), "api-token:terraform");

        created.Should().NotBeNull();

        ClusterCostRate after = await ReloadAsync();
        after.Currency.Should().Be("USD");
        after.ChargeOnRequests.Should().BeTrue();
        after.ChargeIdleCapacity.Should().BeTrue();
        after.LoadBalancerMonthlyCost.Should().Be(0m);
        after.CpuCoreHourCost.Should().Be(0.05m);
    }

    /// <summary>
    /// The refusal <c>SaveAsync</c> already made, kept: a crafted cluster id must not let one
    /// tenant price another tenant's cluster. ⚠️ Asserted by reading the row back, not only by the
    /// null return — a method that refused and wrote anyway would pass the weaker check.
    /// </summary>
    [Fact]
    public async Task Another_tenants_cluster_is_not_priced()
    {
        ClusterCostRate? refused = await rates.PatchAsync(
            tenantId, otherClusterId, PricesOnly(), "api-token:terraform");

        refused.Should().BeNull();

        using CostDbContext read = ((IDbContextFactory<CostDbContext>)factory).CreateDbContext();
        (await read.ClusterCostRates.AnyAsync(r => r.ClusterId == otherClusterId))
            .Should().BeFalse("the refusal must also not have written a sheet");
    }

    /// <summary>
    /// Clamping survives the move off <c>SaveAsync</c>. A negative price produces credits that
    /// corrupt the proportional overhead split, which is why that method clamped rather than
    /// trusting its input.
    /// </summary>
    [Fact]
    public async Task A_negative_price_is_still_clamped()
    {
        await rates.PatchAsync(
            tenantId, clusterId,
            new CostRatePatch(-5m, -1m, -2m, -100m, LoadBalancerMonthlyCost: -7m),
            "api-token:terraform");

        ClusterCostRate after = await ReloadAsync();
        after.CpuCoreHourCost.Should().Be(0m);
        after.ClusterMonthlyOverhead.Should().Be(0m);
        after.LoadBalancerMonthlyCost.Should().Be(0m);
    }

    // ── The wiring, which no behavioural test here can reach ──────────────────────────────────
    //
    // The merge lives in PatchAsync and is tested above. What these two pin is that the route
    // actually uses it, and that the field is visible in the response — the other two thirds of
    // the defect. There is no HTTP harness for PublicApiEndpoints in this suite, and the handler
    // is a lambda in a registration method, so the route's own behaviour is not reachable from a
    // test; the repo pins this kind of thing by reading the source, as the acknowledgment
    // dialog's markup is pinned.

    private const string Endpoints =
        "../../../../../src/EntKube.Web/Services/PublicApi/PublicApiEndpoints.cs";

    private static string EndpointSource()
    {
        string path = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, Endpoints));

        File.Exists(path).Should().BeTrue(
            "this test reads the route registrations; if the file moved, the test has to follow "
            + "rather than quietly stop checking anything");

        return File.ReadAllText(path);
    }

    [Fact]
    public void The_cost_rate_route_merges_rather_than_replaces()
    {
        string source = EndpointSource();

        // Health check: the route is still here under the name this file talks about.
        source.Should().Contain("MapPut(\"/cost-rates/{clusterId:guid}\"");

        source.Should().Contain("rates.PatchAsync(",
            "an optional request body cannot be expressed as a whole entity: an unset property is "
            + "indistinguishable from one deliberately set to its default");

        Regex.Matches(source, @"rates\.SaveAsync\(").Should().BeEmpty(
            "SaveAsync writes every field, so routing an optional body through it turns each "
            + "omitted field into an overwrite");
    }

    [Fact]
    public void The_cost_rate_response_exposes_the_idle_capacity_flag()
    {
        string source = EndpointSource();

        source.Should().Contain("chargeIdleCapacity = rate.ChargeIdleCapacity",
            "a client that cannot read a field back cannot detect drift on it, which is the whole "
            + "reason these four routes are full CRUD");

        source.Should().Contain("bool? ChargeIdleCapacity",
            "and it must be nullable in the request, or absent and false are the same value again");
    }
}
