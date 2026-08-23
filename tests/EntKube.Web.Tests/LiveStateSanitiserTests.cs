using EntKube.Web.Services.Adoption;
using FluentAssertions;
using YamlDotNet.Serialization;

namespace EntKube.Web.Tests;

/// <summary>
/// Tests for turning a live Kubernetes object into storable desired state.
///
/// A live object is not a manifest. Storing one verbatim produces desired state that
/// re-applies with immutable-field errors and cannot be applied to a second cluster —
/// which is most of what a stored manifest is for. These pin what gets removed, and
/// just as importantly what does not.
/// </summary>
public class LiveStateSanitiserTests
{
    private const string LiveDeployment = """
    {
      "apiVersion": "apps/v1",
      "kind": "Deployment",
      "metadata": {
        "name": "web",
        "namespace": "acme-prod",
        "uid": "3f2b1a44-0000-0000-0000-000000000000",
        "resourceVersion": "884213",
        "generation": 7,
        "creationTimestamp": "2026-01-04T10:11:12Z",
        "labels": { "app": "web" },
        "annotations": {
          "deployment.kubernetes.io/revision": "7",
          "kubectl.kubernetes.io/last-applied-configuration": "{\"apiVersion\":\"apps/v1\"}",
          "owner": "platform-team"
        },
        "managedFields": [ { "manager": "kubectl-client-side-apply" } ]
      },
      "spec": {
        "replicas": 3,
        "template": {
          "spec": {
            "containers": [ { "name": "web", "image": "reg.io/web:1.4.2" } ]
          }
        }
      },
      "status": { "readyReplicas": 3, "conditions": [ { "type": "Available" } ] }
    }
    """;

    private static Dictionary<object, object> Parse(string yaml) =>
        (Dictionary<object, object>)new DeserializerBuilder().Build().Deserialize<object>(yaml)!;

    // ── What must be removed ──

    [Fact]
    public void The_status_subtree_is_removed()
    {
        // Status is entirely server-computed; it describes what happened, not what was wanted.
        SanitisedResource result = LiveStateSanitiser.Sanitise(LiveDeployment);

        result.IsAdoptable.Should().BeTrue();
        Parse(result.Yaml!).Should().NotContainKey("status");
    }

    [Fact]
    public void Server_owned_identity_and_history_are_removed()
    {
        var metadata = (Dictionary<object, object>)Parse(LiveStateSanitiser.Sanitise(LiveDeployment).Yaml!)["metadata"];

        metadata.Should().NotContainKeys("uid", "resourceVersion", "generation", "creationTimestamp", "managedFields");
    }

    [Fact]
    public void The_last_applied_configuration_annotation_is_removed()
    {
        // It holds a full copy of the previous manifest — keeping it nests the object
        // inside itself, and it grows on every apply.
        LiveStateSanitiser.Sanitise(LiveDeployment).Yaml!
            .Should().NotContain("last-applied-configuration");
    }

    [Fact]
    public void Controller_owned_annotations_are_removed()
    {
        LiveStateSanitiser.Sanitise(LiveDeployment).Yaml!
            .Should().NotContain("deployment.kubernetes.io/revision");
    }

    // ── What must be kept ──

    [Fact]
    public void The_operators_own_annotations_survive()
    {
        // Over-stripping silently discards intent. A manifest that quietly lost a field is
        // worse than a verbose one.
        LiveStateSanitiser.Sanitise(LiveDeployment).Yaml!.Should().Contain("platform-team");
    }

    [Fact]
    public void Labels_spec_and_identity_survive()
    {
        Dictionary<object, object> manifest = Parse(LiveStateSanitiser.Sanitise(LiveDeployment).Yaml!);

        manifest["apiVersion"].Should().Be("apps/v1");
        manifest["kind"].Should().Be("Deployment");
        manifest.Should().ContainKey("spec");
        ((Dictionary<object, object>)manifest["metadata"])["name"].Should().Be("web");
    }

    [Fact]
    public void The_drifted_value_is_what_actually_gets_adopted()
    {
        // The whole point: replicas was changed on the cluster, and adopting must carry
        // that number into the stored manifest.
        var spec = (Dictionary<object, object>)Parse(LiveStateSanitiser.Sanitise(LiveDeployment).Yaml!)["spec"];

        spec["replicas"].Should().Be("3");
    }

    [Fact]
    public void Integral_numbers_do_not_render_as_decimals()
    {
        // "replicas: 3.0" is rejected by Kubernetes for an integer field.
        LiveStateSanitiser.Sanitise(LiveDeployment).Yaml!.Should().NotContain("3.0");
    }

    // ── Cluster-bound allocations ──

    [Fact]
    public void A_services_assigned_cluster_ip_and_node_ports_are_dropped()
    {
        const string service = """
        {
          "apiVersion": "v1", "kind": "Service",
          "metadata": { "name": "web" },
          "spec": {
            "type": "NodePort",
            "clusterIP": "10.96.14.22",
            "clusterIPs": ["10.96.14.22"],
            "ports": [ { "port": 80, "targetPort": 8080, "nodePort": 31782 } ]
          }
        }
        """;

        SanitisedResource result = LiveStateSanitiser.Sanitise(service);

        result.Yaml.Should().NotContain("10.96.14.22");
        result.Yaml.Should().NotContain("31782");
        // What the operator chose is still there.
        result.Yaml.Should().Contain("targetPort");
        result.Notes.Should().Contain(n => n.Contains("clusterIP"));
    }

    [Fact]
    public void A_bound_volume_name_is_dropped_from_a_pvc()
    {
        const string pvc = """
        {
          "apiVersion": "v1", "kind": "PersistentVolumeClaim",
          "metadata": { "name": "data" },
          "spec": { "volumeName": "pvc-8821", "storageClassName": "fast" }
        }
        """;

        SanitisedResource result = LiveStateSanitiser.Sanitise(pvc);

        result.Yaml.Should().NotContain("pvc-8821");
        result.Yaml.Should().Contain("fast");
    }

    [Fact]
    public void An_owned_object_loses_its_owner_references()
    {
        const string owned = """
        {
          "apiVersion": "apps/v1", "kind": "ReplicaSet",
          "metadata": { "name": "web-abc", "ownerReferences": [ { "kind": "Deployment", "name": "web" } ] },
          "spec": {}
        }
        """;

        SanitisedResource result = LiveStateSanitiser.Sanitise(owned);

        result.Yaml.Should().NotContain("ownerReferences");
        result.Notes.Should().Contain(n => n.Contains("controller"));
    }

    // ── Secrets ──

    [Fact]
    public void A_secret_is_refused_rather_than_adopted()
    {
        const string secret = """
        {
          "apiVersion": "v1", "kind": "Secret",
          "metadata": { "name": "db-credentials" },
          "type": "Opaque",
          "data": { "password": "c3VwZXJzZWNyZXQ=" }
        }
        """;

        SanitisedResource result = LiveStateSanitiser.Sanitise(secret);

        result.IsAdoptable.Should().BeFalse();
        result.Yaml.Should().BeNull();
        result.Refusal.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void A_refused_secret_never_leaks_its_data_into_the_result()
    {
        // The failure this guards against is a credential ending up in a stored manifest,
        // readable in the editor, the database and any export.
        const string secret = """
        {
          "apiVersion": "v1", "kind": "Secret",
          "metadata": { "name": "db-credentials" },
          "data": { "password": "c3VwZXJzZWNyZXQ=" }
        }
        """;

        SanitisedResource result = LiveStateSanitiser.Sanitise(secret);

        (result.Yaml ?? "").Should().NotContain("c3VwZXJzZWNyZXQ=");
        result.Refusal.Should().NotContain("c3VwZXJzZWNyZXQ=");
    }

    [Fact]
    public void The_secret_refusal_explains_why_stripping_the_data_would_be_worse()
    {
        // Adopting the shape without the data would blank the live Secret on next apply.
        LiveStateSanitiser.Sanitise("""{"kind":"Secret","metadata":{"name":"s"},"data":{}}""")
            .Refusal.Should().Contain("blank");
    }

    [Fact]
    public void A_config_map_is_adopted_with_its_data()
    {
        // ConfigMaps are not secret, and their data IS the desired state.
        const string configMap = """
        {
          "apiVersion": "v1", "kind": "ConfigMap",
          "metadata": { "name": "settings" },
          "data": { "LOG_LEVEL": "debug" }
        }
        """;

        SanitisedResource result = LiveStateSanitiser.Sanitise(configMap);

        result.IsAdoptable.Should().BeTrue();
        result.Yaml.Should().Contain("LOG_LEVEL").And.Contain("debug");
    }

    // ── Robustness ──

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("[]")]
    public void Unreadable_input_is_refused_rather_than_throwing(string? json)
    {
        SanitisedResource result = LiveStateSanitiser.Sanitise(json);

        result.IsAdoptable.Should().BeFalse();
        result.Refusal.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void The_result_is_valid_yaml()
    {
        Action parse = () => Parse(LiveStateSanitiser.Sanitise(LiveDeployment).Yaml!);
        parse.Should().NotThrow();
    }

    [Fact]
    public void An_object_with_no_metadata_still_sanitises()
    {
        LiveStateSanitiser.Sanitise("""{"apiVersion":"v1","kind":"Namespace"}""")
            .IsAdoptable.Should().BeTrue();
    }
}

/// <summary>
/// Tests for the comparison that decides which resources are offered for adoption.
/// An entry offered as "differs" that turns out identical trains people to click
/// through the list without reading it.
/// </summary>
public class AdoptionComparisonTests
{
    [Fact]
    public void Trailing_whitespace_is_not_a_difference()
    {
        DriftAdoptionService.Normalise("kind: Service   \nmetadata:  ")
            .Should().Be(DriftAdoptionService.Normalise("kind: Service\nmetadata:"));
    }

    [Fact]
    public void Blank_lines_are_not_a_difference()
    {
        DriftAdoptionService.Normalise("kind: Service\n\n\nmetadata:\n")
            .Should().Be(DriftAdoptionService.Normalise("kind: Service\nmetadata:"));
    }

    [Fact]
    public void Windows_line_endings_are_not_a_difference()
    {
        DriftAdoptionService.Normalise("kind: Service\r\nmetadata:\r\n")
            .Should().Be(DriftAdoptionService.Normalise("kind: Service\nmetadata:"));
    }

    [Fact]
    public void A_leading_document_marker_is_not_a_difference()
    {
        // Git-managed files routinely start with one; the live object never does.
        DriftAdoptionService.Normalise("---\nkind: Service")
            .Should().Be(DriftAdoptionService.Normalise("kind: Service"));
    }

    [Fact]
    public void A_real_change_is_still_a_difference()
    {
        // The normaliser must not be so forgiving that it hides the thing being adopted.
        DriftAdoptionService.Normalise("spec:\n  replicas: 3")
            .Should().NotBe(DriftAdoptionService.Normalise("spec:\n  replicas: 5"));
    }

    [Fact]
    public void Indentation_is_still_significant()
    {
        // Only TRAILING whitespace is trimmed — leading whitespace is YAML structure.
        DriftAdoptionService.Normalise("spec:\n  replicas: 3")
            .Should().NotBe(DriftAdoptionService.Normalise("spec:\nreplicas: 3"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Empty_input_normalises_without_throwing(string? yaml)
    {
        DriftAdoptionService.Normalise(yaml).Should().BeEmpty();
    }

    [Fact]
    public void Only_a_changed_entry_can_be_selected()
    {
        // Refused, missing and unreadable entries must be impossible to adopt by accident —
        // adopting a missing resource would be adopting a deletion.
        foreach (AdoptionStatus status in Enum.GetValues<AdoptionStatus>())
        {
            AdoptionEntry entry = new()
            {
                ManifestId = Guid.NewGuid(), Kind = "Service", Name = "web", Status = status,
            };

            entry.IsSelectable.Should().Be(status == AdoptionStatus.Changed,
                $"status {status} should only be selectable when it is Changed");
        }
    }
}
