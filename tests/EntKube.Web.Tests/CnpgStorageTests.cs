using EntKube.Web.Services;
using FluentAssertions;

namespace EntKube.Web.Tests;

/// <summary>
/// What size a CNPG Cluster manifest may carry, decided from what the cluster reports.
///
/// <para><b>Reported from production:</b> a database ran out of disk, its volumes were grown
/// from 50Gi to 300Gi outside EntKube, and EntKube — which had stored 50Gi at create time —
/// then asked CNPG to shrink the cluster on every action that re-applies the manifest.</para>
/// </summary>
public class CnpgStorageTests
{
    private const string Cluster300 = """
        {"spec":{"storage":{"size":"300Gi","storageClass":"csi-cinder"}}}
        """;

    private static string Claims(params (string Name, string Role, string Requested, string Capacity)[] claims)
    {
        var items = claims.Select(c => new Dictionary<string, object>
        {
            ["metadata"] = new Dictionary<string, object>
            {
                ["name"] = c.Name,
                ["labels"] = new Dictionary<string, string>
                {
                    ["cnpg.io/pvcRole"] = c.Role,
                    ["cnpg.io/instanceName"] = c.Name,
                },
            },
            ["spec"] = new
            {
                resources = new { requests = new { storage = c.Requested } },
                volumeName = $"pv-{c.Name}",
                storageClassName = "csi-cinder",
            },
            ["status"] = new { capacity = new { storage = c.Capacity } },
        });

        return System.Text.Json.JsonSerializer.Serialize(new { items });
    }

    [Fact]
    public void TheLiveSpecIsTheSize_NotWhatWasStored()
    {
        CnpgStorageSnapshot snapshot = new();
        CnpgStorage.ReadClusterSpec(Cluster300, snapshot);
        snapshot.Volumes = CnpgStorage.ReadClaims(Claims(
            ("pg-1", "PG_DATA", "300Gi", "300Gi"),
            ("pg-2", "PG_DATA", "300Gi", "300Gi")));

        snapshot.ClusterRead.Should().BeTrue();
        snapshot.EffectiveSize.Should().Be("300Gi");
        snapshot.StorageClass.Should().Be("csi-cinder");
    }

    /// <summary>
    /// Claims grown by hand while the Cluster still says 50Gi: the manifest must carry the
    /// claims' size, or a replica created later would be too small for the data.
    /// </summary>
    [Fact]
    public void ClaimsGrownByHand_OutrankASmallerSpec()
    {
        CnpgStorageSnapshot snapshot = new();
        CnpgStorage.ReadClusterSpec("""{"spec":{"storage":{"size":"50Gi"}}}""", snapshot);
        snapshot.Volumes = CnpgStorage.ReadClaims(Claims(
            ("pg-1", "PG_DATA", "300Gi", "300Gi"),
            ("pg-2", "PG_DATA", "50Gi", "50Gi")));

        snapshot.EffectiveSize.Should().Be("300Gi");
    }

    /// <summary>
    /// A WAL volume is sized by spec.walStorage, not spec.storage — a big WAL claim must not
    /// inflate the data size. And a capacity rounded up by the provisioner is not a size anybody set.
    /// </summary>
    [Fact]
    public void WalClaimsAndCapacities_DoNotCount()
    {
        CnpgStorageSnapshot snapshot = new();
        CnpgStorage.ReadClusterSpec(
            """{"spec":{"storage":{"size":"300Gi"},"walStorage":{"size":"500Gi"}}}""", snapshot);
        snapshot.Volumes = CnpgStorage.ReadClaims(Claims(
            ("pg-1", "PG_DATA", "300Gi", "322122547201"),
            ("pg-1-wal", "PG_WAL", "500Gi", "500Gi")));

        snapshot.EffectiveSize.Should().Be("300Gi");
        snapshot.WalSpecSize.Should().Be("500Gi");
    }

    [Fact]
    public void QuantitiesCompareByBytes_NotByText()
    {
        // "1Ti" sorts before "300Gi" as text, and "300G" is smaller than "300Gi".
        CnpgStorage.Largest(["300Gi", "1Ti", "300G"]).Should().Be("1Ti");
        CnpgStorage.Largest(["300G", "300Gi"]).Should().Be("300Gi");
        CnpgStorage.Largest([null, "garbage", "50Gi"]).Should().Be("50Gi");
        CnpgStorage.Largest([null, "garbage"]).Should().BeNull();
    }

    [Fact]
    public void AClusterDeclaringAPvcTemplate_IsReadToo()
    {
        CnpgStorageSnapshot snapshot = new();
        CnpgStorage.ReadClusterSpec(
            """{"spec":{"storage":{"pvcTemplate":{"resources":{"requests":{"storage":"120Gi"}}}}}}""", snapshot);

        snapshot.SpecSize.Should().Be("120Gi");
    }

    [Fact]
    public void ResizeInUseVolumesFalse_IsNoticed()
    {
        CnpgStorageSnapshot snapshot = new();
        CnpgStorage.ReadClusterSpec(
            """{"spec":{"storage":{"size":"50Gi","resizeInUseVolumes":false}}}""", snapshot);

        snapshot.ResizeInUseVolumes.Should().BeFalse();
    }

    [Fact]
    public void AClaimMidResize_SaysSo()
    {
        List<CnpgVolumeInfo> volumes = CnpgStorage.ReadClaims("""
            {"items":[{"metadata":{"name":"pg-1"},
              "spec":{"resources":{"requests":{"storage":"400Gi"}}},
              "status":{"capacity":{"storage":"300Gi"},
                        "conditions":[{"type":"FileSystemResizePending","status":"True"}]}}]}
            """);

        volumes.Should().ContainSingle();
        volumes[0].Requested.Should().Be("400Gi");
        volumes[0].Capacity.Should().Be("300Gi");
        volumes[0].Resizing.Should().Be("FileSystemResizePending");
        volumes[0].IsData.Should().BeTrue("a claim without a role label is a data claim");
    }

    /// <summary>Kubernetes defaults allowVolumeExpansion to false, so absent means no.</summary>
    [Fact]
    public void AStorageClassWithoutAllowVolumeExpansion_CannotExpand()
    {
        CnpgStorage.ReadAllowVolumeExpansion("""{"metadata":{"name":"slow"}}""").Should().BeFalse();
        CnpgStorage.ReadAllowVolumeExpansion("""{"metadata":{"name":"fast"},"allowVolumeExpansion":true}""")
            .Should().BeTrue();
        CnpgStorage.ReadAllowVolumeExpansion(null).Should().BeNull("unread is not the same as no");
    }

    [Theory]
    [InlineData("400Gi", true)]
    [InlineData("1Ti", true)]
    [InlineData("1.5Ti", true)]
    [InlineData("500G", true)]
    [InlineData("0Gi", false)]
    [InlineData("-1Gi", false)]
    [InlineData("lots", false)]
    [InlineData("400Gi\"}}", false)]
    [InlineData("", false)]
    public void OnlyAPlainPositiveQuantity_IsASize(string input, bool valid) =>
        CnpgStorage.IsValidSize(input).Should().Be(valid);

    [Fact]
    public void TheResizePatch_TouchesOnlyTheSize() =>
        CnpgStorage.BuildResizePatch(" 400Gi ").Should().Be("""{"spec":{"storage":{"size":"400Gi"}}}""");

    [Fact]
    public void Describe_IsForPeople()
    {
        CnpgStorage.Describe("300Gi").Should().Be("300 GiB");
        CnpgStorage.Describe("1536Gi").Should().Be("1.5 TiB");
        CnpgStorage.Describe(null).Should().Be("—");
    }
}
