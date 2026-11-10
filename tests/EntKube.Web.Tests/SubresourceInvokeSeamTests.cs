using EntKube.Web.Services;
using EntKube.Web.Services.ClusterChanges;
using EntKube.Web.Services.Clusters;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace EntKube.Web.Tests;

/// <summary>
/// <c>InvokeSubresourceAsync</c>: the seam verb for action endpoints such as KubeVirt's pause, which
/// neither kubectl nor the typed SDK reaches. The alternative was the SDK's raw <c>HttpClient</c>,
/// which no gate and no ratchet can see.
/// </summary>
public class SubresourceInvokeSeamTests
{
    /// <summary>
    /// Against the real factory and a real gate. A cancelling sink means the throw proves the
    /// acknowledgment was raised. Had the request gone first, the test kubeconfig's unreachable server
    /// would have failed it with a network error instead of a cancellation.
    /// </summary>
    [Fact]
    public async Task An_invocation_is_acknowledged_before_anything_is_sent()
    {
        ClusterChangeGate gate = new(new ConfigurationBuilder().Build(), NullLogger<ClusterChangeGate>.Instance);
        RecordingSink sink = new();
        using IDisposable registration = gate.RegisterSink(sink);

        IClusterClient client = new ClusterClient(
            new KubernetesClientFactory(gate, new KubernetesProxyClientPool(NullLogger<KubernetesProxyClientPool>.Instance)),
            Guid.NewGuid(), "prod-eu", TestKubeconfig.Valid);

        Func<Task> act = () => client.InvokeSubresourceAsync(
            "subresources.kubevirt.io/v1", "virtualmachineinstances", "web-01", "vms", "pause",
            "Pause virtual machine vms/web-01");

        await act.Should().ThrowAsync<OperationCanceledException>();

        PlannedClusterChange asked = sink.Asked.Should().ContainSingle().Subject;
        asked.Verb.Should().Be(ChangeVerb.Invoke);
        asked.Name.Should().Be("web-01");
        asked.Namespace.Should().Be("vms");
        asked.Resource.Should().Be("virtualmachineinstances/pause");
        asked.Summary.Should().Be("Pause virtual machine vms/web-01");
        asked.Kubeconfig.Should().Be(TestKubeconfig.Valid, "the client supplies its own credential");
    }

    [Fact]
    public void The_path_is_the_subresource_of_the_named_object()
    {
        KubernetesClientFactory.BuildSubresourcePath(
                "subresources.kubevirt.io/v1", "virtualmachineinstances", "web-01", "vms", "unpause")
            .Should().Be("/apis/subresources.kubevirt.io/v1/namespaces/vms/virtualmachineinstances/web-01/unpause");
    }

    [Theory]
    [InlineData("subresources.kubevirt.io/v1", "virtualmachineinstances", "../../secrets/x", "vms", "pause")]
    [InlineData("subresources.kubevirt.io/v1", "virtualmachineinstances", "web-01", "vms/x", "pause")]
    [InlineData("subresources.kubevirt.io/v1", "virtualmachineinstances", "web-01", "vms", "pause?dryRun=All")]
    [InlineData("subresources.kubevirt.io/v1/x", "virtualmachineinstances", "web-01", "vms", "pause")]
    [InlineData("subresources.kubevirt.io/v1", "", "web-01", "vms", "pause")]
    [InlineData("subresources.kubevirt.io/v1", "virtualmachineinstances", "Web-01", "vms", "pause")]
    public void Nothing_a_caller_passes_can_reach_another_path(
        string groupVersion, string plural, string name, string ns, string subresource)
    {
        Action build = () => KubernetesClientFactory.BuildSubresourcePath(groupVersion, plural, name, ns, subresource);

        build.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void The_gate_never_reads_an_invocation_as_no_change()
    {
        // ComputeDiffAsync falls through to "has changes, show the summary" for this verb. The summary
        // a dialog shows without one is what Describe gives.
        new PlannedClusterChange
        {
            Verb = ChangeVerb.Invoke, Kubeconfig = "x", Resource = "virtualmachineinstances/pause",
            Name = "web-01", Namespace = "vms"
        }.Describe().Should().Be("Invoke virtualmachineinstances/pause on web-01 in vms");
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
