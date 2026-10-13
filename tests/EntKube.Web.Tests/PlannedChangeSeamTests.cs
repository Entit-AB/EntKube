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
            new KubernetesClientFactory(gate), Guid.NewGuid(), "prod-eu", TestKubeconfig.Valid);

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
