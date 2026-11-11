using EntKube.Web.Services;
using EntKube.Web.Services.ClusterChanges;
using EntKube.Web.Services.Clusters;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace EntKube.Web.Tests;

/// <summary>
/// Asking the gate without holding a credential.
///
/// <para><b>The requirement this removes.</b> <c>PlannedClusterChange.Kubeconfig</c> is
/// <c>required</c> — that is what guarantees the gate can always compute a dry-run preview. For
/// callers that mutate through the seam it costs nothing, because the factory raises the
/// acknowledgment on the way through and already has the credential. For callers that mutate
/// through the <em>k8s SDK</em> — scaling a Deployment, patching a CronJob — it meant holding a
/// kubeconfig purely in order to ask, which is why a dozen methods in
/// <c>KubernetesOperationsService</c> read a cluster row they otherwise needed nothing from.</para>
///
/// <para>So <c>PlannedChange</c> describes the operation and the seam completes it. The invariant
/// is unchanged: a <c>PlannedClusterChange</c> still cannot exist without a credential, and the
/// only thing that can make one from a <c>PlannedChange</c> is something already holding one.</para>
/// </summary>
public class PlannedChangeSeamTests
{
    /// <summary>
    /// Against the real factory and a real gate, not a mock of either — the #144 lesson. A
    /// cancelling sink means the throw itself proves the acknowledgment was raised.
    /// </summary>
    [Fact]
    public async Task A_client_raises_the_acknowledgment_with_its_own_credential_and_label()
    {
        ClusterChangeGate gate = new(
            new ConfigurationBuilder().Build(), NullLogger<ClusterChangeGate>.Instance);
        RecordingSink sink = new();
        using IDisposable registration = gate.RegisterSink(sink);

        IClusterClient client = new ClusterClient(
            new KubernetesClientFactory(gate, new KubernetesProxyClientPool(Microsoft.Extensions.Logging.Abstractions.NullLogger<KubernetesProxyClientPool>.Instance)), Guid.NewGuid(), "prod-eu", TestKubeconfig.Valid);

        Func<Task> act = () => client.AcknowledgeAsync(new PlannedChange
        {
            Verb = ChangeVerb.Scale,
            Resource = "deployment",
            Name = "billing-api",
            Namespace = "billing-ns",
            Summary = "Scale Deployment/billing-api to 3 replica(s)",
            Patch = "spec.replicas = 3",
        });

        await act.Should().ThrowAsync<OperationCanceledException>();

        sink.Asked.Should().ContainSingle();
        PlannedClusterChange asked = sink.Asked[0];

        asked.Kubeconfig.Should().Be(TestKubeconfig.Valid,
            "the credential comes from the client, which is the entire point — a caller that had "
            + "to supply one would still be holding one");
        asked.ClusterLabel.Should().Be("prod-eu",
            "and so does the label the operator reads, rather than each caller passing a name");
    }

    /// <summary>
    /// Every field, asserted one by one. A mapping method is exactly where a field goes missing
    /// silently: a dropped <c>Patch</c> or <c>Namespace</c> still acknowledges, still applies,
    /// and only shows up as a dialog that describes less than it is about to do.
    /// </summary>
    [Fact]
    public void Completing_a_planned_change_carries_every_field()
    {
        PlannedChange change = new()
        {
            Verb = ChangeVerb.Patch,
            Summary = "a summary",
            Manifest = "kind: ConfigMap\n",
            Kind = "ConfigMap",
            Name = "app-config",
            Namespace = "billing-ns",
            Resource = "configmap",
            Patch = "data.key = value",
            StrategicPatch = true,
        };

        PlannedClusterChange completed = change.WithCredential("kubeconfig-text", "prod-eu");

        completed.Verb.Should().Be(ChangeVerb.Patch);
        completed.Summary.Should().Be("a summary");
        completed.Manifest.Should().Be("kind: ConfigMap\n");
        completed.Kind.Should().Be("ConfigMap");
        completed.Name.Should().Be("app-config");
        completed.Namespace.Should().Be("billing-ns");
        completed.Resource.Should().Be("configmap");
        completed.Patch.Should().Be("data.key = value");
        completed.StrategicPatch.Should().BeTrue();
        completed.Kubeconfig.Should().Be("kubeconfig-text");
        completed.ClusterLabel.Should().Be("prod-eu");
    }

    /// <summary>
    /// A cluster with no name falls back to "cluster" rather than acknowledging an operation
    /// against the empty string, which the gate's own default already did.
    /// </summary>
    [Fact]
    public void A_nameless_cluster_still_reads_as_a_cluster()
    {
        PlannedChange change = new() { Verb = ChangeVerb.Delete };

        change.WithCredential("kubeconfig-text", "   ").ClusterLabel.Should().Be("cluster");
    }

    /// <summary>
    /// The new read verb's command line, asserted directly: the resource and name are positional,
    /// the namespace is a flag, and <c>-o yaml</c> is the whole reason this is not
    /// <c>GetJsonAsync</c>.
    /// </summary>
    [Fact]
    public void A_resource_yaml_read_names_the_resource_the_namespace_and_the_output_format()
    {
        KubernetesClientFactory
            .BuildResourceYamlArguments("deployment", "billing-api", "billing-ns", "/tmp/kc.yaml")
            .Should().Be("get deployment billing-api -n billing-ns --kubeconfig=/tmp/kc.yaml -o yaml");
    }

    /// <summary>
    /// A cluster-scoped delete omits the namespace flag rather than sending it empty.
    ///
    /// <para><c>kubectl delete x y -n</c> is rejected outright, so a caller holding a
    /// cluster-scoped CR — a trust-manager <c>Bundle</c>, for instance — could not use the seam
    /// at all and kept its own kubectl with its own copy of the credential.
    /// <c>ResourceExistsAsync</c> had read an empty <c>ns</c> this way all along; delete had not,
    /// and the inconsistency was invisible until something needed it.</para>
    /// </summary>
    [Fact]
    public void A_delete_omits_the_namespace_flag_for_a_cluster_scoped_resource()
    {
        KubernetesClientFactory
            .BuildDeleteArguments("bundles.trust.cert-manager.io", "ca-bundle", "", "/tmp/kc.yaml")
            .Should().Be("delete bundles.trust.cert-manager.io ca-bundle "
                + "--kubeconfig=/tmp/kc.yaml --ignore-not-found");

        KubernetesClientFactory
            .BuildDeleteArguments("secret", "tls", "billing-ns", "/tmp/kc.yaml")
            .Should().Be("delete secret tls -n billing-ns "
                + "--kubeconfig=/tmp/kc.yaml --ignore-not-found");
    }

    /// <summary>
    /// A cluster-scoped <em>list</em> omits the namespace flag too, and keeps the label selector.
    ///
    /// <para>Namespaces are not in a namespace, so a caller listing them could not use
    /// <c>GetJsonAsync</c> and kept its own kubectl —
    /// <c>CertificateDistributionService.ListClusterNamespacesAsync</c> was exactly that. Three
    /// methods now read an empty <c>ns</c> the same way; the convention was always there, just
    /// not everywhere.</para>
    /// </summary>
    [Fact]
    public void A_json_list_omits_the_namespace_flag_for_a_cluster_scoped_resource()
    {
        KubernetesClientFactory
            .BuildGetJsonArguments("namespaces", "", "team=billing", "/tmp/kc.yaml")
            .Should().Be("get namespaces --kubeconfig=/tmp/kc.yaml -l team=billing -o json");

        KubernetesClientFactory
            .BuildGetJsonArguments("secrets", "billing-ns", "", "/tmp/kc.yaml")
            .Should().Be("get secrets -n billing-ns --kubeconfig=/tmp/kc.yaml -o json");
    }

    /// <summary>
    /// The two verbs that retired a "cannot": an apply the cluster fetches by URL, and a log tail
    /// for one container of several.
    ///
    /// <para>Both were listed in <see cref="ClusterChangeGateCoverageTests"/> as things the seam
    /// could not express, which kept a kubeconfig on disk for the whole of every helm run. One was
    /// a missing operation; the other was a missing <em>parameter</em>.</para>
    /// </summary>
    [Fact]
    public void The_two_additions_that_retired_a_cannot_build_the_commands_they_replaced()
    {
        KubernetesClientFactory
            .BuildApplyFromUrlArguments("https://example.test/operator.yaml", "/tmp/kc.yaml")
            .Should().Be("apply --server-side --force-conflicts "
                + "-f https://example.test/operator.yaml --kubeconfig=/tmp/kc.yaml",
                "server-side because a client-side apply writes the whole manifest into an "
                + "annotation, and CRD bundles exceed its 256 KB ceiling");

        KubernetesClientFactory
            .BuildPodLogsArguments("api-0", "billing-ns", 12, "sidecar", "/tmp/kc.yaml")
            .Should().Be("logs api-0 -c sidecar -n billing-ns --kubeconfig=/tmp/kc.yaml --tail=12");

        // And without a container, unchanged — the parameter is additive.
        KubernetesClientFactory
            .BuildPodLogsArguments("api-0", "billing-ns", 200, null, "/tmp/kc.yaml")
            .Should().Be("logs api-0 -n billing-ns --kubeconfig=/tmp/kc.yaml --tail=200");
    }

    /// <summary>
    /// An apply from a URL is a mutation, so it is acknowledged — and the acknowledgment carries
    /// the URL rather than a manifest, because there is no manifest text to preview. Against the
    /// real factory and a cancelling sink, so the throw itself proves the gate was raised.
    /// </summary>
    [Fact]
    public async Task An_apply_from_a_url_is_acknowledged_with_the_url()
    {
        ClusterChangeGate gate = new(
            new ConfigurationBuilder().Build(), NullLogger<ClusterChangeGate>.Instance);
        RecordingSink sink = new();
        using IDisposable registration = gate.RegisterSink(sink);

        KubernetesClientFactory factory = new(gate, new KubernetesProxyClientPool(Microsoft.Extensions.Logging.Abstractions.NullLogger<KubernetesProxyClientPool>.Instance));

        Func<Task> act = () => factory.ApplyManifestFromUrlAsync(
            "https://example.test/operator.yaml", TestKubeconfig.Valid);

        await act.Should().ThrowAsync<OperationCanceledException>();

        sink.Asked.Should().ContainSingle();
        sink.Asked[0].Verb.Should().Be(ChangeVerb.Apply);
        sink.Asked[0].Summary.Should().Contain("https://example.test/operator.yaml");
        sink.Asked[0].Manifest.Should().BeNull("the cluster fetches the manifest; EntKube never holds it");
    }

    /// <summary>
    /// A patched apply changes what the release URL alone would have applied, so the person asked must
    /// be told: the acknowledgment names each patch and why, next to the URL.
    /// </summary>
    [Fact]
    public async Task A_patched_apply_from_a_url_names_its_patches_in_the_acknowledgment()
    {
        ClusterChangeGate gate = new(
            new ConfigurationBuilder().Build(), NullLogger<ClusterChangeGate>.Instance);
        RecordingSink sink = new();
        using IDisposable registration = gate.RegisterSink(sink);

        KubernetesClientFactory factory = new(gate, new KubernetesProxyClientPool(Microsoft.Extensions.Logging.Abstractions.NullLogger<KubernetesProxyClientPool>.Instance));

        Func<Task> act = () => factory.ApplyManifestFromUrlAsync(
            "https://example.test/operator.yaml", TestKubeconfig.Valid, patches:
            [new ManifestPatch("apps", "v1", "Deployment", "op", "[]", "runs on any node")]);

        await act.Should().ThrowAsync<OperationCanceledException>();

        sink.Asked.Should().ContainSingle().Which.Summary.Should()
            .Contain("https://example.test/operator.yaml").And.Contain("Deployment op — runs on any node");
    }

    [Fact]
    public void A_kustomization_names_the_url_as_its_resource_and_each_patch_by_its_target()
    {
        string kustomization = KubernetesClientFactory.BuildKustomization(
            "https://example.test/operator.yaml",
            [new ManifestPatch("apps", "v1", "Deployment", "op", "- op: remove\n  path: /spec/x", "why")]);

        using System.Text.Json.JsonDocument doc = System.Text.Json.JsonDocument.Parse(kustomization);
        System.Text.Json.JsonElement root = doc.RootElement;

        root.GetProperty("kind").GetString().Should().Be("Kustomization");
        root.GetProperty("resources").EnumerateArray().Select(r => r.GetString())
            .Should().Equal("https://example.test/operator.yaml");
        System.Text.Json.JsonElement patch = root.GetProperty("patches")[0];
        patch.GetProperty("target").GetProperty("kind").GetString().Should().Be("Deployment");
        patch.GetProperty("target").GetProperty("name").GetString().Should().Be("op");
        patch.GetProperty("patch").GetString().Should().Be("- op: remove\n  path: /spec/x");

        KubernetesClientFactory.BuildApplyKustomizationArguments("/tmp/k", "/tmp/kc.yaml")
            .Should().Be("apply --server-side --force-conflicts -k /tmp/k --kubeconfig=/tmp/kc.yaml");
    }

    /// <summary>
    /// The drain verb: the command line it builds, and that it is always acknowledged.
    ///
    /// <para><b>This test earned its keep immediately.</b> The drain was first written as
    /// <c>ChangeVerb.Delete</c>, and the test failed because nothing was thrown — the gate had
    /// computed a delete preview for the Node object, found it absent, concluded "no change" and
    /// skipped the dialog. A drain applied without anyone being asked. It also described the
    /// wrong act when the object <em>was</em> readable: a drain does not delete the node.</para>
    ///
    /// <para>So a drain is its own verb, with no dry run — the same reasoning as
    /// <c>ChangeVerb.Rollback</c>: what it will evict depends on budgets and on what happens to
    /// be running, so the summary is the honest preview.</para>
    /// </summary>
    [Fact]
    public async Task A_drain_is_always_acknowledged_and_carries_its_flags()
    {
        KubernetesClientFactory.BuildDrainArguments("node-3", 30, true, false, "/tmp/kc.yaml")
            .Should().Be("drain node-3 --grace-period=30 --timeout=5m --ignore-daemonsets "
                + "--kubeconfig=/tmp/kc.yaml",
                "the timeout is not optional: a drain blocked by a PodDisruptionBudget would "
                + "otherwise wait forever inside a request");

        KubernetesClientFactory.BuildDrainArguments("node-3", 0, false, true, "/tmp/kc.yaml")
            .Should().Be("drain node-3 --grace-period=0 --timeout=5m --delete-emptydir-data "
                + "--kubeconfig=/tmp/kc.yaml");

        ClusterChangeGate gate = new(
            new ConfigurationBuilder().Build(), NullLogger<ClusterChangeGate>.Instance);
        RecordingSink sink = new();
        using IDisposable registration = gate.RegisterSink(sink);

        Func<Task> act = () => new KubernetesClientFactory(gate, new KubernetesProxyClientPool(Microsoft.Extensions.Logging.Abstractions.NullLogger<KubernetesProxyClientPool>.Instance))
            .DrainNodeAsync("node-3", TestKubeconfig.Valid);

        await act.Should().ThrowAsync<OperationCanceledException>();

        sink.Asked.Should().ContainSingle();
        sink.Asked[0].Verb.Should().Be(ChangeVerb.Drain,
            "a drain is not a delete: as Delete, an unreadable Node made the gate report no "
            + "change and apply the drain unasked");
        sink.Asked[0].Kind.Should().Be("Node");
        sink.Asked[0].Name.Should().Be("node-3");
        sink.Asked[0].Summary.Should().Contain("evict");
    }

    /// <summary>
    /// The two reads-and-writes added for the install and routing paths: running a CronJob now,
    /// and fetching one named resource as JSON.
    /// </summary>
    [Fact]
    public async Task Running_a_cronjob_now_is_acknowledged_without_a_manifest()
    {
        KubernetesClientFactory
            .BuildCreateJobFromCronJobArguments("nightly-manual-1", "nightly", "billing-ns", "/tmp/kc.yaml")
            .Should().Be("create job nightly-manual-1 --from=cronjob/nightly -n billing-ns "
                + "--kubeconfig=/tmp/kc.yaml");

        KubernetesClientFactory
            .BuildResourceJsonArguments("deployment", "api", "billing-ns", "/tmp/kc.yaml")
            .Should().Be("get deployment api -n billing-ns --kubeconfig=/tmp/kc.yaml -o json");

        ClusterChangeGate gate = new(
            new ConfigurationBuilder().Build(), NullLogger<ClusterChangeGate>.Instance);
        RecordingSink sink = new();
        using IDisposable registration = gate.RegisterSink(sink);

        Func<Task> act = () => new KubernetesClientFactory(gate, new KubernetesProxyClientPool(Microsoft.Extensions.Logging.Abstractions.NullLogger<KubernetesProxyClientPool>.Instance))
            .CreateJobFromCronJobAsync("nightly-manual-1", "nightly", "billing-ns", TestKubeconfig.Valid);

        await act.Should().ThrowAsync<OperationCanceledException>();

        sink.Asked.Should().ContainSingle();
        sink.Asked[0].Verb.Should().Be(ChangeVerb.Apply);
        sink.Asked[0].Name.Should().Be("nightly-manual-1");
        sink.Asked[0].Manifest.Should().BeNull(
            "the Job is copied from a template on the cluster, so there is no manifest text to "
            + "preview — the summary is the honest one, as with an apply from a URL");
    }

    private sealed class RecordingSink : IClusterChangeAckSink
    {
        public List<PlannedClusterChange> Asked { get; } = [];

        public Task<ClusterChangeDecision> RequestAsync(
            PlannedClusterChange change, ClusterChangeDiff diff, CancellationToken ct)
        {
            Asked.Add(change);
            return Task.FromResult(ClusterChangeDecision.Cancelled);
        }
    }
}
