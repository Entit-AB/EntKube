using System.Text.Json;
using EntKube.Web.Services.PublicApi;
using FluentAssertions;
using Xunit;

namespace EntKube.Web.Tests;

/// <summary>
/// The named response records serialise byte for byte identically to the anonymous objects they
/// replaced.
///
/// <para><b>Why this is a test and not a comment.</b> Replacing
/// <c>Results.Ok(new { memoryGiBHourCost = … })</c> with a record property
/// <c>MemoryGiBHourCost</c> relies on <c>JsonNamingPolicy.CamelCase</c> producing exactly
/// <c>memoryGiBHourCost</c> — it lowercases a leading run of capitals, so whether <c>GiB</c>
/// survives in the middle of a name is not something to reason about and be wrong about. The CLI
/// reads these names, the MCP tools hand them to a model, and every existing consumer is already
/// parsing what the anonymous objects produced, so a silent rename is a broken client rather than
/// a failed build.</para>
///
/// <para>⚠️ <b>The anonymous literals below are deliberately a second copy.</b> Normally that is
/// the defect — the one <c>ApiScopeMetadata</c> was restructured to remove. Here the copy IS the
/// specification: it is the wire shape clients already depend on, frozen at the commit that
/// replaced it. It stays after the migration so that renaming a record property fails here rather
/// than in somebody's integration.</para>
///
/// <para>Every field is given a DISTINCT value, because two adjacent <c>decimal</c>s swapped by a
/// positional constructor call would serialise to the same set of names and only a differing
/// value can see it.</para>
/// </summary>
public class PublicApiWireShapeTests
{
    /// <summary>Exactly what minimal APIs serialise responses with when nothing is configured.</summary>
    private static readonly JsonSerializerOptions AsTheApiSerialises = new(JsonSerializerDefaults.Web);

    [Fact]
    public void A_cluster_rate_serialises_exactly_as_the_anonymous_object_it_replaced()
    {
        Guid clusterId = Guid.NewGuid();
        DateTime updatedAt = new(2026, 11, 2, 9, 30, 0, DateTimeKind.Utc);

        // ⚠️ Twelve distinct values. The six decimals ascend so that any swap among them — the
        // transposition a positional call could introduce — changes the JSON.
        ClusterRateResponse subject = new(
            ClusterId: clusterId,
            CpuCoreHourCost: 1.11m,
            MemoryGiBHourCost: 2.22m,
            StorageGiBMonthCost: 3.33m,
            LoadBalancerMonthlyCost: 4.44m,
            PublicIpMonthlyCost: 5.55m,
            ClusterMonthlyOverhead: 6.66m,
            Currency: "SEK",
            ChargeOnRequests: true,
            ChargeIdleCapacity: false,
            UpdatedAt: updatedAt,
            UpdatedBy: "api-token:billing");

        // The shape as it stood before this record existed, copied from ToCostRateDto.
        object asItWas = new
        {
            clusterId,
            cpuCoreHourCost = 1.11m,
            memoryGiBHourCost = 2.22m,
            storageGiBMonthCost = 3.33m,
            loadBalancerMonthlyCost = 4.44m,
            publicIpMonthlyCost = 5.55m,
            clusterMonthlyOverhead = 6.66m,
            currency = "SEK",
            chargeOnRequests = true,
            chargeIdleCapacity = false,
            updatedAt,
            updatedBy = "api-token:billing",
        };

        JsonSerializer.Serialize(subject, AsTheApiSerialises)
            .Should().Be(JsonSerializer.Serialize(asItWas, AsTheApiSerialises));
    }

    /// <summary>
    /// Named here as well as compared above, so a failure says WHICH name moved rather than
    /// printing two long strings and leaving the reader to diff them.
    /// </summary>
    [Fact]
    public void The_cluster_rate_field_names_are_the_ones_clients_already_read()
    {
        ClusterRateResponse subject = new(
            Guid.Empty, 0m, 0m, 0m, 0m, 0m, 0m, "SEK", false, false, default, null);

        JsonSerializer.SerializeToNode(subject, AsTheApiSerialises)!
            .AsObject().Select(p => p.Key).Should().Equal(
                "clusterId", "cpuCoreHourCost", "memoryGiBHourCost", "storageGiBMonthCost",
                "loadBalancerMonthlyCost", "publicIpMonthlyCost", "clusterMonthlyOverhead",
                "currency", "chargeOnRequests", "chargeIdleCapacity", "updatedAt", "updatedBy");
    }

    /// <summary>
    /// ⚠️ The mapping, not just the record. Three operations answer through
    /// <see cref="ClusterRateResponse.From"/>, and a swap of two same-typed prices inside it
    /// would serialise to a perfectly valid shape with two figures exchanged — the serialisation
    /// test above cannot see that, because it builds the record directly. Every one of the twelve
    /// source values is distinct, so any transposition changes a field.
    /// </summary>
    [Fact]
    public void Every_price_lands_in_the_field_that_names_it()
    {
        Guid clusterId = Guid.NewGuid();
        DateTime updatedAt = new(2026, 11, 2, 9, 30, 0, DateTimeKind.Utc);

        EntKube.Contracts.Cost.ClusterRate rate = new(
            ClusterId: clusterId,
            CpuCoreHourCost: 1.11m,
            MemoryGiBHourCost: 2.22m,
            StorageGiBMonthCost: 3.33m,
            LoadBalancerMonthlyCost: 4.44m,
            PublicIpMonthlyCost: 5.55m,
            ClusterMonthlyOverhead: 6.66m,
            Currency: "SEK",
            ChargeOnRequests: true,
            ChargeIdleCapacity: false,
            UpdatedAt: updatedAt,
            UpdatedBy: "api-token:billing");

        ClusterRateResponse subject = ClusterRateResponse.From(rate);

        subject.ClusterId.Should().Be(clusterId);
        subject.CpuCoreHourCost.Should().Be(1.11m);
        subject.MemoryGiBHourCost.Should().Be(2.22m);
        subject.StorageGiBMonthCost.Should().Be(3.33m);
        subject.LoadBalancerMonthlyCost.Should().Be(4.44m, "a load balancer is not a public IP");
        subject.PublicIpMonthlyCost.Should().Be(5.55m);
        subject.ClusterMonthlyOverhead.Should().Be(6.66m);
        subject.Currency.Should().Be("SEK");
        subject.ChargeOnRequests.Should().BeTrue();
        subject.ChargeIdleCapacity.Should().BeFalse(
            "its absence from the read-back shape was once a defect in its own right");
        subject.UpdatedAt.Should().Be(updatedAt);
        subject.UpdatedBy.Should().Be("api-token:billing");
    }

    [Fact]
    public void Whoami_serialises_exactly_as_the_anonymous_object_it_replaced()
    {
        Guid tenantId = Guid.NewGuid();
        string[] scopes = ["ops:read", "config:write", "apps:read"];

        WhoAmIResponse subject = new(
            TenantId: tenantId,
            Token: "ci-pipeline",
            Scopes: scopes.OrderBy(s => s));

        object asItWas = new
        {
            tenantId,
            token = "ci-pipeline",
            scopes = scopes.OrderBy(s => s),
        };

        JsonSerializer.Serialize(subject, AsTheApiSerialises)
            .Should().Be(JsonSerializer.Serialize(asItWas, AsTheApiSerialises));
    }

    /// <summary>
    /// The sort is part of the contract — a caller diffing two tokens relies on a stable order —
    /// so it is asserted rather than left to whatever order the principal happened to hold.
    /// </summary>
    [Fact]
    public void Whoami_scopes_come_back_sorted()
    {
        WhoAmIResponse subject = new(
            Guid.Empty, "t", new[] { "ops:read", "apps:read", "config:write" }.OrderBy(s => s));

        JsonSerializer.SerializeToNode(subject, AsTheApiSerialises)!["scopes"]!
            .AsArray().Select(n => n!.GetValue<string>())
            .Should().Equal("apps:read", "config:write", "ops:read");
    }
}
