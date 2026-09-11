using EntKube.Web.Services;
using FluentAssertions;

namespace EntKube.Web.Tests;

/// <summary>
/// Each of these components can install cleanly and be useless, and each failure looks like
/// something else: nodes that never go Ready look like a node problem rather than a CNI one, an
/// uncleared taint looks like a scheduling problem rather than a missing cloud-controller-manager,
/// a storage class with nothing behind it looks like a slow volume. The checks exist to name the
/// actual cause, so the messages are asserted alongside the verdicts.
/// </summary>
public class FoundationChecksTests
{
    private static string Nodes(params string[] nodes) =>
        $$"""{"items": [{{string.Join(",", nodes)}}]}""";

    private static string Node(string name, bool ready = true, string? providerId = "openstack:///abc", string[]? taints = null)
    {
        string taintJson = taints is null || taints.Length == 0
            ? ""
            : $""","taints":[{string.Join(",", taints.Select(t => $$"""{"key":"{{t}}","effect":"NoSchedule"}"""))}]""";

        string provider = providerId is null ? "" : $$""","providerID":"{{providerId}}" """;

        return $$"""
            {
              "metadata": {"name": "{{name}}"},
              "spec": {"unused": true{{provider}}{{taintJson}}},
              "status": {"conditions": [{"type":"Ready","status":"{{(ready ? "True" : "False")}}"}]}
            }
            """;
    }

    // ── Nodes / CNI ──

    [Fact]
    public void All_nodes_ready_passes_and_says_how_many()
    {
        FoundationCheck check = FoundationChecks.Nodes(Nodes(Node("cp-1"), Node("worker-1")));

        check.Passed.Should().BeTrue();
        check.Detail.Should().Contain("2 node(s)");
    }

    [Fact]
    public void A_node_that_is_not_ready_is_named_and_the_likely_cause_given()
    {
        FoundationCheck check = FoundationChecks.Nodes(Nodes(Node("cp-1"), Node("worker-1", ready: false)));

        check.Passed.Should().BeFalse();
        check.Detail.Should().Contain("worker-1");
        check.Detail.Should().Contain("pod network");
    }

    [Fact]
    public void No_nodes_at_all_fails_rather_than_passing_vacuously()
    {
        // An empty list is the shape an unreadable cluster produces, and "all zero nodes are ready"
        // is the kind of true statement that hides an outage.
        FoundationChecks.Nodes("""{"items": []}""").Passed.Should().BeFalse();
        FoundationChecks.Nodes("").Passed.Should().BeFalse();
        FoundationChecks.Nodes("{not json").Passed.Should().BeFalse();
    }

    // ── Cloud controller manager ──

    [Fact]
    public void Initialized_nodes_with_provider_ids_pass()
    {
        FoundationChecks.CloudControllerManager(Nodes(Node("cp-1"), Node("worker-1")))
            .Passed.Should().BeTrue();
    }

    [Fact]
    public void A_node_still_carrying_the_uninitialized_taint_fails_and_explains_the_symptom()
    {
        // This is the one that presents as "nothing schedules" with no obvious cause.
        FoundationCheck check = FoundationChecks.CloudControllerManager(
            Nodes(Node("cp-1"), Node("worker-1", taints: ["node.cloudprovider.kubernetes.io/uninitialized"])));

        check.Passed.Should().BeFalse();
        check.Detail.Should().Contain("worker-1");
        check.Detail.Should().Contain("schedule");
    }

    [Fact]
    public void A_node_without_a_provider_id_fails()
    {
        FoundationCheck check = FoundationChecks.CloudControllerManager(
            Nodes(Node("cp-1"), Node("worker-1", providerId: null)));

        check.Passed.Should().BeFalse();
        check.Detail.Should().Contain("Load balancers");
    }

    // ── Storage class ──

    [Fact]
    public void Exactly_one_default_storage_class_passes()
    {
        string json = """
            {"items": [
              {"metadata": {"name": "cinder-default", "annotations": {"storageclass.kubernetes.io/is-default-class": "true"}}},
              {"metadata": {"name": "cinder-retain"}}
            ]}
            """;

        FoundationCheck check = FoundationChecks.DefaultStorageClass(json);

        check.Passed.Should().BeTrue();
        check.Detail.Should().Contain("cinder-default");
    }

    [Fact]
    public void The_beta_default_annotation_still_counts()
    {
        // Several charts still write it, and treating those clusters as having no default would be
        // a false alarm on a working cluster.
        string json = """
            {"items": [{"metadata": {"name": "cinder", "annotations": {"storageclass.beta.kubernetes.io/is-default-class": "true"}}}]}
            """;

        FoundationChecks.DefaultStorageClass(json).Passed.Should().BeTrue();
    }

    [Fact]
    public void Storage_classes_with_no_default_fail_and_say_what_happens()
    {
        FoundationCheck check = FoundationChecks.DefaultStorageClass(
            """{"items": [{"metadata": {"name": "cinder"}}]}""");

        check.Passed.Should().BeFalse();
        check.Detail.Should().Contain("Pending");
    }

    [Fact]
    public void Two_defaults_fail_because_which_one_wins_is_undefined()
    {
        // Worse than none: the same claim can land on different storage on different days.
        string json = """
            {"items": [
              {"metadata": {"name": "a", "annotations": {"storageclass.kubernetes.io/is-default-class": "true"}}},
              {"metadata": {"name": "b", "annotations": {"storageclass.kubernetes.io/is-default-class": "true"}}}
            ]}
            """;

        FoundationCheck check = FoundationChecks.DefaultStorageClass(json);

        check.Passed.Should().BeFalse();
        check.Detail.Should().Contain("undefined");
    }

    // ── Volume binding ──

    [Fact]
    public void A_bound_probe_claim_is_the_proof_that_storage_works()
    {
        string json = """{"items": [{"metadata": {"name": "entkube-storage-check"}, "status": {"phase": "Bound"}}]}""";

        FoundationChecks.VolumeBinding(json, "entkube-storage-check").Passed.Should().BeTrue();
    }

    [Fact]
    public void A_pending_probe_claim_points_at_the_csi_controller()
    {
        string json = """{"items": [{"metadata": {"name": "entkube-storage-check"}, "status": {"phase": "Pending"}}]}""";

        FoundationCheck check = FoundationChecks.VolumeBinding(json, "entkube-storage-check");

        check.Passed.Should().BeFalse();
        check.Detail.Should().Contain("CSI controller");
    }

    // ── Deployments and backups ──

    [Fact]
    public void A_deployment_with_no_available_replica_is_installed_and_doing_nothing()
    {
        string json = """{"items": [{"metadata": {"name": "metrics-server"}, "status": {"availableReplicas": 0}}]}""";

        FoundationCheck check = FoundationChecks.Deployment(json, "metrics", "Metrics server", "metrics-server");

        check.Passed.Should().BeFalse();
        check.Detail.Should().Contain("doing nothing");
    }

    [Fact]
    public void A_missing_deployment_says_so_rather_than_reporting_zero_replicas()
    {
        FoundationChecks.Deployment("""{"items": []}""", "metrics", "Metrics server", "metrics-server")
            .Detail.Should().Contain("No deployment");
    }

    [Fact]
    public void An_unavailable_backup_location_is_the_difference_between_backups_and_believing_in_them()
    {
        string json = """
            {"items": [{"metadata": {"name": "default"}, "status": {"phase": "Unavailable"}}]}
            """;

        FoundationCheck check = FoundationChecks.BackupLocation(json);

        check.Passed.Should().BeFalse();
        check.Detail.Should().Contain("fail every one of them");
    }

    [Fact]
    public void No_backup_location_at_all_fails()
    {
        FoundationChecks.BackupLocation("""{"items": []}""").Passed.Should().BeFalse();
    }

    [Fact]
    public void An_available_backup_location_passes()
    {
        string json = """{"items": [{"metadata": {"name": "default"}, "status": {"phase": "Available"}}]}""";

        FoundationChecks.BackupLocation(json).Passed.Should().BeTrue();
    }
}
