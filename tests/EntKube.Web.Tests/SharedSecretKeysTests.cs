using System.Text.Json;
using EntKube.Web.Services;
using EntKube.Web.Services.Clusters;
using FluentAssertions;
using Moq;

namespace EntKube.Web.Tests;

/// <summary>
/// One Kubernetes Secret, several writers.
///
/// <para>An app's Secret is fed by its own vault secrets and by the credentials of every service
/// it is bound to, and pointing several of those at one Secret is intended — an app wants a
/// single <c>envFrom</c>, not one per service. <b>This was broken, and in a way that looked
/// additive.</b></para>
///
/// <para>The reported symptom: after updating an app secret in the vault, the Secret in the
/// app's namespace held only the four Redis keys and none of the app's three. Neither writer
/// was doing anything obviously wrong:</para>
///
/// <list type="number">
///   <item>The vault wrote with delete-and-recreate, deliberately, so that a key removed from
///   the vault could not survive in the cluster. That rewrote the whole object.</item>
///   <item>The cache binding then ran a plain <c>kubectl apply</c> of a complete four-key
///   manifest — and apply is <b>not</b> additive. Its three-way merge deletes the keys named in
///   the object's <c>last-applied-configuration</c> annotation but missing from the new config,
///   and that annotation belongs to the <b>object</b>. The vault's apply had just written it, so
///   the Redis apply read the app's three keys as "removed from my config" and deleted
///   them.</item>
/// </list>
///
/// <para>Whichever ran second appeared to win. These tests pin the three properties that make a
/// shared Secret work: a writer sets its own keys, deletes the ones it has dropped, and touches
/// nothing else.</para>
/// </summary>
public class SharedSecretKeysTests
{
    // ── The patch itself ──────────────────────────────────────────────────────

    [Fact]
    public void A_value_is_base64_and_a_removal_is_a_json_null()
    {
        string patch = KubernetesClientFactory.BuildSecretKeyPatch(
            new Dictionary<string, string?> { ["KEEP"] = "hemligt", ["GONE"] = null },
            annotations: null);

        using JsonDocument doc = JsonDocument.Parse(patch);
        JsonElement data = doc.RootElement.GetProperty("data");

        // Base64, not plain text: a merge patch is written into the Secret verbatim, so plain
        // text here is read back as rubbish by anything consuming the Secret.
        data.GetProperty("KEEP").GetString().Should().Be("aGVtbGlndA==");

        // A JSON null is how a merge patch deletes a key. An empty string would set it to empty,
        // which is a different and much quieter kind of wrong.
        data.GetProperty("GONE").ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Fact]
    public void Annotations_ride_along_in_the_same_patch()
    {
        string patch = KubernetesClientFactory.BuildSecretKeyPatch(
            new Dictionary<string, string?> { ["K"] = "v" },
            new Dictionary<string, string> { ["entkube.io/keys.cache"] = "K" });

        using JsonDocument doc = JsonDocument.Parse(patch);
        doc.RootElement.GetProperty("metadata").GetProperty("annotations")
            .GetProperty("entkube.io/keys.cache").GetString().Should().Be("K");
    }

    /// <summary>
    /// The managed-by labels are in <b>every</b> patch, not only in the manifest that creates the
    /// Secret. The deployment importer uses that label to avoid re-adopting EntKube's own
    /// Secrets back into the vault, so a Secret written before this change — or created by
    /// another writer — has to acquire it on the next write rather than never.
    /// </summary>
    [Fact]
    public void Every_patch_carries_the_managed_by_labels()
    {
        string patch = KubernetesClientFactory.BuildSecretKeyPatch(
            new Dictionary<string, string?> { ["K"] = "v" }, annotations: null);

        JsonElement labels = JsonDocument.Parse(patch).RootElement
            .GetProperty("metadata").GetProperty("labels");

        labels.GetProperty(VaultService.ManagedByLabelKey).GetString()
            .Should().Be(VaultService.ManagedByLabelValue);
        labels.GetProperty("entkube.io/managed").GetString().Should().Be("true");
    }

    // ── A writer in a shared Secret ───────────────────────────────────────────

    /// <summary>Captures what a writer asked the cluster to patch.</summary>
    private sealed record Captured(
        string Secret, string Namespace,
        IReadOnlyDictionary<string, string?> Keys,
        IReadOnlyDictionary<string, string>? Annotations);

    private static (Mock<IClusterClient> Cluster, List<Captured> Patches) Cluster(
        string? ownedAnnotation = null, string owner = "cache")
    {
        Mock<IClusterClient> cluster = new();
        List<Captured> patches = [];

        string json = ownedAnnotation is null
            ? """{"metadata":{"name":"s"}}"""
            : "{\"metadata\":{\"name\":\"s\",\"annotations\":{"
              + $"\"entkube.io/keys.{owner}\":\"{ownedAnnotation}\"" + "}}}";

        cluster.Setup(c => c.GetResourceJsonAsync("secret", It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new HelmExecutionResult { Success = true, Output = json });

        cluster.Setup(c => c.MergeSecretKeysAsync(
                It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<IReadOnlyDictionary<string, string?>>(),
                It.IsAny<IReadOnlyDictionary<string, string>?>(),
                It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, IReadOnlyDictionary<string, string?>,
                      IReadOnlyDictionary<string, string>?, string?, CancellationToken>(
                (name, ns, keys, annotations, _, _) =>
                    patches.Add(new Captured(name, ns, keys, annotations)))
            .ReturnsAsync("patched");

        return (cluster, patches);
    }

    /// <summary>
    /// The regression. The cache binding names its four keys and nothing else — so the app's own
    /// keys are not in the patch at all, and a merge patch cannot touch what it does not name.
    /// </summary>
    [Fact]
    public async Task A_writer_names_only_its_own_keys()
    {
        (Mock<IClusterClient> cluster, List<Captured> patches) = Cluster();

        await SharedSecretKeys.WriteAsync(
            cluster.Object, "entitweb-secrets", "ns-entit", SharedSecretKeys.Owners.Cache,
            new Dictionary<string, string?>
            {
                ["REDIS_HOST"] = "redis.cache.svc",
                ["REDIS_PORT"] = "6379",
                ["REDIS_PASSWORD"] = "pw",
                ["REDIS_URL"] = "redis://:pw@redis.cache.svc:6379",
            });

        patches.Should().ContainSingle();
        patches[0].Secret.Should().Be("entitweb-secrets");
        patches[0].Namespace.Should().Be("ns-entit");
        patches[0].Keys.Keys.Should().BeEquivalentTo(
            ["REDIS_HOST", "REDIS_PORT", "REDIS_PASSWORD", "REDIS_URL"]);

        // The app's own keys are untouched because they are absent from the patch.
        patches[0].Keys.Should().NotContainKey("DATABASE_URL");
    }

    [Fact]
    public async Task A_writer_records_the_keys_it_owns()
    {
        (Mock<IClusterClient> cluster, List<Captured> patches) = Cluster();

        await SharedSecretKeys.WriteAsync(
            cluster.Object, "s", "ns", SharedSecretKeys.Owners.Vault,
            new Dictionary<string, string?> { ["B"] = "2", ["A"] = "1" });

        patches[0].Annotations!["entkube.io/keys.vault"].Should().Be("A,B");
    }

    /// <summary>
    /// The property the vault's delete-and-recreate existed to get, kept without it: a key
    /// removed from the vault must not survive in the cluster.
    /// </summary>
    [Fact]
    public async Task A_key_dropped_since_the_last_write_is_deleted_by_name()
    {
        (Mock<IClusterClient> cluster, List<Captured> patches) =
            Cluster("API_KEY,DATABASE_URL,OLD_TOKEN", owner: "vault");

        await SharedSecretKeys.WriteAsync(
            cluster.Object, "s", "ns", SharedSecretKeys.Owners.Vault,
            new Dictionary<string, string?> { ["API_KEY"] = "k", ["DATABASE_URL"] = "d" });

        patches[0].Keys["OLD_TOKEN"].Should().BeNull("a revoked key must not survive");
        patches[0].Keys["API_KEY"].Should().Be("k");
        patches[0].Annotations!["entkube.io/keys.vault"].Should().Be("API_KEY,DATABASE_URL");
    }

    /// <summary>
    /// And the other half of that: one writer's dropped keys are computed from its <i>own</i>
    /// annotation, so another writer's keys are never nulled even when they are in the Secret.
    /// </summary>
    [Fact]
    public async Task One_writers_history_cannot_delete_anothers_keys()
    {
        // The Secret records the vault's keys. The cache binding writes, and must ignore them.
        (Mock<IClusterClient> cluster, List<Captured> patches) =
            Cluster("API_KEY,DATABASE_URL", owner: "vault");

        await SharedSecretKeys.WriteAsync(
            cluster.Object, "s", "ns", SharedSecretKeys.Owners.Cache,
            new Dictionary<string, string?> { ["REDIS_URL"] = "redis://x" });

        patches[0].Keys.Should().ContainSingle()
            .Which.Key.Should().Be("REDIS_URL");
    }

    [Fact]
    public async Task A_configuration_key_that_is_now_unset_is_removed()
    {
        (Mock<IClusterClient> cluster, List<Captured> patches) = Cluster();

        // RabbitMQ's shape: the queue name was cleared, so the key must go rather than linger.
        await SharedSecretKeys.WriteAsync(
            cluster.Object, "s", "ns", SharedSecretKeys.Owners.Messaging,
            new Dictionary<string, string?>
            {
                ["RABBITMQ_URL"] = "amqp://x",
                ["RABBITMQ_QUEUE"] = null,
            });

        patches[0].Keys["RABBITMQ_QUEUE"].Should().BeNull();

        // A key being removed is not a key being owned.
        patches[0].Annotations!["entkube.io/keys.messaging"].Should().Be("RABBITMQ_URL");
    }

    // ── Removing a binding ────────────────────────────────────────────────────

    [Fact]
    public async Task Removing_a_binding_deletes_its_keys_and_leaves_the_secret()
    {
        (Mock<IClusterClient> cluster, List<Captured> patches) =
            Cluster("REDIS_HOST,REDIS_URL", owner: "cache");

        await SharedSecretKeys.RemoveAsync(
            cluster.Object, "entitweb-secrets", "ns-entit", SharedSecretKeys.Owners.Cache);

        patches.Should().ContainSingle();
        patches[0].Keys.Should().HaveCount(2);
        patches[0].Keys.Values.Should().AllSatisfy(v => v.Should().BeNull());
        patches[0].Annotations!["entkube.io/keys.cache"].Should().Be("");

        // The Secret object itself is never deleted — that is what used to take the app's own
        // secrets with it.
        cluster.Verify(c => c.DeleteManifestAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    /// <summary>
    /// Nothing recorded means nothing deleted. A Secret written before ownership was tracked has
    /// no annotation, and guessing which of its keys belonged to this writer is exactly the
    /// mistake this change exists to stop.
    /// </summary>
    [Fact]
    public async Task Removing_a_binding_with_nothing_recorded_patches_nothing()
    {
        (Mock<IClusterClient> cluster, List<Captured> patches) = Cluster();

        await SharedSecretKeys.RemoveAsync(
            cluster.Object, "s", "ns", SharedSecretKeys.Owners.Cache);

        patches.Should().BeEmpty();
    }

    /// <summary>
    /// A read that fails must not be read as "this writer owns nothing", in the direction that
    /// deletes things. It already is not — but the test says so, because the catch is broad.
    /// </summary>
    [Fact]
    public async Task An_unreadable_secret_deletes_nothing()
    {
        Mock<IClusterClient> cluster = new();
        cluster.Setup(c => c.GetResourceJsonAsync("secret", It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("apiserver unreachable"));

        IReadOnlyCollection<string> owned = await SharedSecretKeys.ReadOwnedAsync(
            cluster.Object, "s", "ns", SharedSecretKeys.Owners.Cache);

        owned.Should().BeEmpty();
    }
}
