using EntKube.Web.Services;
using FluentAssertions;

namespace EntKube.Web.Tests;

/// <summary>
/// What an operator is told when <c>helm --wait</c> runs out.
///
/// <para>Helm's own words for it are "Error: context deadline exceeded" and nothing else: no resource, no
/// reason, not even the fact that waiting was the problem. On a chart the size of Harbor — seven
/// workloads, several volumes, an external database — that is a failed install with nowhere to start,
/// and the cluster forgets nothing only for as long as nobody deletes the release. These tests cover
/// reading the answer out of the cluster while it is still there.</para>
/// </summary>
public class HelmWaitTimeoutDiagnosticsTests
{
    private static string Pods(params string[] items) => "{\"items\":[" + string.Join(",", items) + "]}";

    private static string ReadyPod(string name) =>
        "{\"metadata\":{\"name\":\"" + name + "\"},\"status\":{\"phase\":\"Running\","
        + "\"containerStatuses\":[{\"name\":\"main\",\"ready\":true,\"restartCount\":0,"
        + "\"state\":{\"running\":{}}}]}}";

    [Theory]
    [InlineData("Error: context deadline exceeded")]
    [InlineData("Error: timed out waiting for the condition")]
    public void Both_of_helm_s_wordings_for_a_lapsed_wait_are_recognised(string output)
    {
        ComponentLifecycleService.LooksLikeWaitTimeout(output).Should().BeTrue();
    }

    [Fact]
    public void A_real_error_is_not_mistaken_for_a_timeout()
    {
        ComponentLifecycleService.LooksLikeWaitTimeout(
            "Error: INSTALLATION FAILED: cannot re-use a name that is still in use").Should().BeFalse();
    }

    /// <summary>
    /// The most common shape of this failure and the one hardest to guess from helm's message: a pod that
    /// never got a node because its volume was never provisioned.
    /// </summary>
    [Fact]
    public void An_unschedulable_pod_is_reported_with_the_scheduler_s_reason()
    {
        string summary = ComponentLifecycleService.SummarizeStalledWorkloads("harbor", Pods(
            """
            {"metadata":{"name":"harbor-redis-0"},"status":{"phase":"Pending","conditions":[
              {"type":"PodScheduled","status":"False","reason":"Unschedulable",
               "message":"0/3 nodes are available: 3 pod has unbound immediate PersistentVolumeClaims."}]}}
            """), null);

        summary.Should().Contain("pod/harbor-redis-0");
        summary.Should().Contain("Unschedulable");
        summary.Should().Contain("unbound immediate PersistentVolumeClaims");
    }

    [Fact]
    public void A_crashing_container_is_reported_with_its_reason_and_restart_count()
    {
        string summary = ComponentLifecycleService.SummarizeStalledWorkloads("harbor", Pods(
            """
            {"metadata":{"name":"harbor-core-abc"},"status":{"phase":"Running","containerStatuses":[
              {"name":"core","ready":false,"restartCount":7,
               "state":{"waiting":{"reason":"CrashLoopBackOff","message":"back-off 5m0s restarting failed container"}}}]}}
            """), null);

        summary.Should().Contain("pod/harbor-core-abc");
        summary.Should().Contain("CrashLoopBackOff");
        summary.Should().Contain("7 restarts");
    }

    /// <summary>
    /// A timed-out install of a big chart has a dozen healthy pods around the one or two that are not.
    /// Listing the healthy ones buries the answer, which is the failure mode this whole thing exists to
    /// avoid.
    /// </summary>
    [Fact]
    public void Healthy_pods_are_left_out()
    {
        string summary = ComponentLifecycleService.SummarizeStalledWorkloads("harbor", Pods(
            ReadyPod("harbor-portal-1"),
            ReadyPod("harbor-registry-1"),
            """
            {"metadata":{"name":"harbor-trivy-0"},"status":{"phase":"Running","containerStatuses":[
              {"name":"trivy","ready":false,"restartCount":0,"state":{"waiting":{"reason":"ImagePullBackOff"}}}]}}
            """), null);

        summary.Should().NotContain("harbor-portal-1");
        summary.Should().NotContain("harbor-registry-1");
        summary.Should().Contain("harbor-trivy-0");
    }

    /// <summary>A completed Helm hook job is not a stalled workload.</summary>
    [Fact]
    public void A_succeeded_pod_is_not_a_finding()
    {
        string summary = ComponentLifecycleService.SummarizeStalledWorkloads("harbor", Pods(
            """{"metadata":{"name":"harbor-migrate"},"status":{"phase":"Succeeded"}}"""), null);

        summary.Should().BeEmpty();
    }

    [Fact]
    public void An_unbound_claim_is_named_along_with_where_to_look()
    {
        string summary = ComponentLifecycleService.SummarizeStalledWorkloads("harbor", null,
            """{"items":[{"metadata":{"name":"data-harbor-redis-0"},"status":{"phase":"Pending"}}]}""");

        summary.Should().Contain("persistentvolumeclaim/data-harbor-redis-0");
        summary.Should().Contain("StorageClass");
    }

    /// <summary>
    /// Diagnostics must never replace helm's own error with an exception of their own, so anything that
    /// cannot be read yields nothing at all.
    /// </summary>
    [Fact]
    public void Unreadable_output_yields_nothing_rather_than_throwing()
    {
        ComponentLifecycleService.SummarizeStalledWorkloads("harbor", "not json at all", null)
            .Should().BeEmpty();
        ComponentLifecycleService.SummarizeStalledWorkloads("harbor", null, null).Should().BeEmpty();
    }

    /// <summary>
    /// The case that made this necessary: harbor-core comes up, registers a screenful of adapters, blocks
    /// on a dependency and never reports ready. Nothing in the pod status says why — only the end of the
    /// log does, and the beginning of it looks identical to a healthy start.
    /// </summary>
    [Fact]
    public void A_running_container_that_never_reports_ready_is_worth_quoting_a_log_from()
    {
        List<(string Pod, string Container)> quote = ComponentLifecycleService.FindSilentlyUnreadyContainers(Pods(
            """
            {"metadata":{"name":"harbor-core-7d9"},"status":{"phase":"Running","containerStatuses":[
              {"name":"core","ready":false,"restartCount":0,"state":{"running":{"startedAt":"2026-09-15T12:29:32Z"}}}]}}
            """));

        quote.Should().ContainSingle();
        quote[0].Pod.Should().Be("harbor-core-7d9");
        quote[0].Container.Should().Be("core");
    }

    /// <summary>
    /// A waiting or crash-looping container already carries its reason in the pod status, so quoting its
    /// log on top of that is noise.
    /// </summary>
    [Fact]
    public void A_container_that_already_explains_itself_is_not_quoted()
    {
        ComponentLifecycleService.FindSilentlyUnreadyContainers(Pods(
            """
            {"metadata":{"name":"harbor-trivy-0"},"status":{"phase":"Running","containerStatuses":[
              {"name":"trivy","ready":false,"restartCount":3,"state":{"waiting":{"reason":"CrashLoopBackOff"}}}]}}
            """)).Should().BeEmpty();
    }

    [Fact]
    public void Healthy_containers_are_never_quoted()
    {
        ComponentLifecycleService.FindSilentlyUnreadyContainers(Pods(ReadyPod("harbor-core-1")))
            .Should().BeEmpty();
    }

    /// <summary>One broken chart must not flood the component output with logs.</summary>
    [Fact]
    public void At_most_three_containers_are_quoted()
    {
        string stalled(string name) =>
            "{\"metadata\":{\"name\":\"" + name + "\"},\"status\":{\"phase\":\"Running\","
            + "\"containerStatuses\":[{\"name\":\"c\",\"ready\":false,\"restartCount\":0,"
            + "\"state\":{\"running\":{}}}]}}";

        ComponentLifecycleService.FindSilentlyUnreadyContainers(
            Pods(stalled("a"), stalled("b"), stalled("c"), stalled("d"), stalled("e")))
            .Should().HaveCount(3);
    }

    /// <summary>Everything healthy means helm timed out on something else; say nothing rather than guess.</summary>
    [Fact]
    public void A_healthy_namespace_produces_no_summary()
    {
        ComponentLifecycleService.SummarizeStalledWorkloads("harbor", Pods(ReadyPod("harbor-core-1")), null)
            .Should().BeEmpty();
    }
}
