using EntKube.Web.Services;
using FluentAssertions;

namespace EntKube.Web.Tests;

/// <summary>
/// What size a MongoDB Community cluster has, decided from what the cluster reports.
///
/// <para><b>Reported from production:</b> after the same thing happened to a CNPG database, the
/// MongoDB page also kept showing the size stored at create time while the volumes had been
/// grown. Mongo differs in one way that matters: the operator builds a StatefulSet whose claim
/// templates can never change, so the size a manifest carries and the size the data has are
/// two different numbers.</para>
/// </summary>
public class MongoStorageTests
{
    private const string StatefulSet50 = """
        {"spec":{"volumeClaimTemplates":[
          {"metadata":{"name":"logs-volume"},"spec":{"resources":{"requests":{"storage":"2G"}}}},
          {"metadata":{"name":"data-volume"},"spec":{"resources":{"requests":{"storage":"50Gi"}},
                                                   "storageClassName":"csi-cinder"}}]}}
        """;

    private static string Claims(params (string Name, string Requested)[] claims) =>
        System.Text.Json.JsonSerializer.Serialize(new
        {
            items = claims.Select(c => new
            {
                metadata = new { name = c.Name },
                spec = new
                {
                    resources = new { requests = new { storage = c.Requested } },
                    volumeName = $"pv-{c.Name}",
                    storageClassName = "csi-cinder",
                },
                status = new { capacity = new { storage = c.Requested } },
            }),
        });

    [Fact]
    public void TheStatefulSetsDataTemplate_IsRead_NotTheLogsOne()
    {
        (string? size, string? storageClass) = MongoStorage.ReadStatefulSetTemplate(StatefulSet50);

        size.Should().Be("50Gi");
        storageClass.Should().Be("csi-cinder");
    }

    [Fact]
    public void TheCommunityResourcesTemplate_IsReadFromUnderStatefulSet()
    {
        (string? size, _) = MongoStorage.ReadCommunityTemplate("""
            {"spec":{"statefulSet":{"spec":{"volumeClaimTemplates":[
              {"metadata":{"name":"data-volume"},"spec":{"resources":{"requests":{"storage":"20Gi"}}}}]}}}}
            """);

        size.Should().Be("20Gi");
        MongoStorage.ReadCommunityTemplate("""{"spec":{"members":3}}""").Size.Should().BeNull();
        MongoStorage.ReadStatefulSetTemplate(null).Size.Should().BeNull();
    }

    /// <summary>
    /// Claims grown past the immutable template: the data really is 300Gi, and that is what
    /// people see and what a restored copy needs, while the manifest keeps the 50Gi template.
    /// </summary>
    [Fact]
    public void ClaimsGrownPastTheTemplate_AreTheEffectiveSize()
    {
        MongoStorageSnapshot snapshot = new()
        {
            TemplateSize = "50Gi",
            Volumes = MongoStorage.ReadClaims(Claims(
                ("data-volume-my-mongo-0", "300Gi"),
                ("data-volume-my-mongo-1", "300Gi"),
                ("logs-volume-my-mongo-0", "500Gi")), "my-mongo"),
        };

        snapshot.EffectiveSize.Should().Be("300Gi", "a big logs volume is not the data size");
        snapshot.TemplateSize.Should().Be("50Gi");
    }

    /// <summary>
    /// Claims are picked by the names a StatefulSet gives them, so a neighbour whose name starts
    /// the same way, or anything else in the namespace, is never resized along with this cluster.
    /// </summary>
    [Fact]
    public void OnlyThisClustersClaims_AreItsVolumes()
    {
        List<CnpgVolumeInfo> claims = MongoStorage.ReadClaims(Claims(
            ("data-volume-my-mongo-0", "300Gi"),
            ("logs-volume-my-mongo-0", "2G"),
            ("data-volume-my-mongo-archive-0", "1Ti"),
            ("data-volume-other-0", "10Gi"),
            ("my-mongo-backup-scratch", "5Gi")), "my-mongo");

        claims.Select(c => c.PvcName).Should().BeEquivalentTo(
            ["data-volume-my-mongo-0", "logs-volume-my-mongo-0"]);
        claims.Single(c => c.PvcName == "data-volume-my-mongo-0").Role.Should().Be(MongoStorage.DataVolume);
        claims.Single(c => c.PvcName == "data-volume-my-mongo-0").Instance.Should().Be("my-mongo-0");
        claims.Single(c => c.PvcName == "logs-volume-my-mongo-0").Role.Should().Be(MongoStorage.LogsVolume);
    }

    [Fact]
    public void WithNoClaimsRead_TheTemplateIsTheSize()
    {
        new MongoStorageSnapshot { TemplateSize = "50Gi" }.EffectiveSize.Should().Be("50Gi");
        new MongoStorageSnapshot().EffectiveSize.Should().BeNull();
    }

    [Fact]
    public void TheClaimPatch_TouchesOnlyTheStorageRequest() =>
        MongoStorage.BuildClaimResizePatch(" 400Gi ")
            .Should().Be("""{"spec":{"resources":{"requests":{"storage":"400Gi"}}}}""");
}
