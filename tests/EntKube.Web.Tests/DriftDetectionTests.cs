using EntKube.Web.Data;
using EntKube.Web.Services.Upgrades;
using FluentAssertions;

namespace EntKube.Web.Tests;

/// <summary>
/// Tests for drift detection's pure parts: the shared desired-manifest composer
/// (which apply and drift must render identically), the removed-API scanner, and
/// diff measurement.
/// </summary>
public class DriftDetectionTests
{
    // ── Desired-manifest composition (shared with the apply path) ──

    [Fact]
    public void Prepends_a_namespace_document_so_the_namespace_is_created()
    {
        string combined = DeploymentManifestComposer.Combine("acme-prod", ["kind: Service"]);

        combined.Should().StartWith("apiVersion: v1\nkind: Namespace\nmetadata:\n  name: acme-prod");
        combined.Should().Contain("kind: Service");
    }

    [Fact]
    public void Joins_manifests_with_a_single_document_separator()
    {
        string combined = DeploymentManifestComposer.Combine("ns", ["kind: A", "kind: B"]);

        combined.Should().Contain("kind: A\n---\nkind: B");
        combined.Should().NotContain("---\n---");
    }

    [Fact]
    public void Strips_a_leading_document_marker_so_no_empty_document_is_produced()
    {
        // Git-managed YAML files routinely start with "---"; keeping it would emit an
        // empty document that some kubectl versions reject.
        string combined = DeploymentManifestComposer.Combine("ns", ["---\nkind: A", "---\nkind: B"]);

        combined.Should().NotContain("---\n---");
        combined.Should().Contain("kind: A");
        combined.Should().Contain("kind: B");
    }

    [Fact]
    public void Orders_manifests_by_their_sort_order_not_their_query_order()
    {
        List<DeploymentManifest> manifests =
        [
            new() { Kind = "Service", Name = "b", SortOrder = 2, YamlContent = "kind: Service" },
            new() { Kind = "ConfigMap", Name = "a", SortOrder = 1, YamlContent = "kind: ConfigMap" },
        ];

        string combined = DeploymentManifestComposer.Combine("ns", manifests);

        combined.IndexOf("ConfigMap", StringComparison.Ordinal)
            .Should().BeLessThan(combined.IndexOf("kind: Service", StringComparison.Ordinal));
    }

    // ── Removed-API scanning ──

    [Fact]
    public void Flags_a_kind_specific_api_removal()
    {
        IReadOnlyList<DeprecatedApiUsage> usages = DeprecatedApiScanner.Scan(
            "apiVersion: batch/v1beta1\nkind: CronJob\nmetadata:\n  name: nightly\n", "1.24");

        usages.Should().ContainSingle();
        usages[0].Kind.Should().Be("CronJob");
        usages[0].RemovedInMinor.Should().Be("1.25");
        usages[0].ReplacedBy.Should().Be("batch/v1");
        usages[0].Name.Should().Be("nightly");
    }

    [Fact]
    public void Flags_a_whole_group_version_removal()
    {
        DeprecatedApiScanner.Scan("apiVersion: apps/v1beta1\nkind: Deployment\n", "1.15")
            .Should().ContainSingle().Which.RemovedInMinor.Should().Be("1.16");
    }

    [Fact]
    public void Marks_a_usage_already_removed_when_the_cluster_is_past_the_removal()
    {
        DeprecatedApiScanner.Scan("apiVersion: batch/v1beta1\nkind: CronJob\n", "1.31")
            .Should().ContainSingle().Which.AlreadyRemoved.Should().BeTrue();
    }

    [Fact]
    public void Reports_an_upcoming_removal_as_not_yet_removed()
    {
        DeprecatedApiScanner.Scan("apiVersion: flowcontrol.apiserver.k8s.io/v1beta3\nkind: FlowSchema\n", "1.30")
            .Should().ContainSingle().Which.AlreadyRemoved.Should().BeFalse();
    }

    [Fact]
    public void A_kind_that_survived_its_group_version_pruning_is_not_flagged()
    {
        // policy/v1beta1 lost PodDisruptionBudget and PodSecurityPolicy, but the table is
        // keyed per kind so an unlisted kind in that group must not be reported.
        DeprecatedApiScanner.Scan("apiVersion: policy/v1beta1\nkind: SomethingElse\n", "1.30")
            .Should().BeEmpty();
    }

    [Fact]
    public void Current_apis_are_never_flagged()
    {
        DeprecatedApiScanner.Scan(
            "apiVersion: apps/v1\nkind: Deployment\n---\napiVersion: v1\nkind: Service\n", "1.31")
            .Should().BeEmpty();
    }

    [Fact]
    public void Scans_every_document_in_a_multi_document_manifest()
    {
        string yaml = """
            apiVersion: apps/v1
            kind: Deployment
            ---
            apiVersion: batch/v1beta1
            kind: CronJob
            ---
            apiVersion: policy/v1beta1
            kind: PodDisruptionBudget
            """;

        IReadOnlyList<DeprecatedApiUsage> usages = DeprecatedApiScanner.Scan(yaml, "1.24");

        usages.Should().HaveCount(2);
        usages.Select(u => u.Kind).Should().BeEquivalentTo(["CronJob", "PodDisruptionBudget"]);
        usages.Select(u => u.DocumentIndex).Should().BeEquivalentTo([2, 3]);
    }

    [Fact]
    public void Ignores_nested_apiversion_fields_belonging_to_embedded_objects()
    {
        // A CRD body or a template can carry its own apiVersion indented inside; only the
        // document's own top-level one identifies the object being applied.
        string yaml = """
            apiVersion: apps/v1
            kind: Deployment
            spec:
              template:
                apiVersion: batch/v1beta1
                kind: CronJob
            """;

        DeprecatedApiScanner.Scan(yaml, "1.31").Should().BeEmpty();
    }

    [Fact]
    public void Tolerates_manifests_that_a_strict_yaml_parser_would_reject()
    {
        // Helm template syntax is not valid YAML, but the removed API two lines up is
        // still worth reporting.
        string yaml = "apiVersion: batch/v1beta1\nkind: CronJob\nmetadata:\n  name: {{ .Release.Name }}\n";

        DeprecatedApiScanner.Scan(yaml, "1.31").Should().ContainSingle();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void An_empty_manifest_yields_no_usages(string? yaml)
    {
        DeprecatedApiScanner.Scan(yaml, "1.31").Should().BeEmpty();
    }

    [Fact]
    public void A_null_target_version_reports_removals_as_upcoming()
    {
        DeprecatedApiScanner.Scan("apiVersion: batch/v1beta1\nkind: CronJob\n", null)
            .Should().ContainSingle().Which.AlreadyRemoved.Should().BeFalse();
    }

    // ── Diff measurement ──

    [Fact]
    public void Counts_added_and_removed_lines_but_not_hunk_headers()
    {
        string diff = """
            --- /tmp/live
            +++ /tmp/desired
            @@ -1,4 +1,4 @@
             metadata:
            -  replicas: 1
            +  replicas: 3
            +  newField: x
            """;

        DriftDetectionService.CountChangedLines(diff).Should().Be(3);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void An_absent_diff_counts_as_no_changed_lines(string? diff)
    {
        DriftDetectionService.CountChangedLines(diff).Should().Be(0);
    }

    // ── Cache semantics ──

    [Fact]
    public void An_unscanned_tenant_reports_no_cached_report_rather_than_an_empty_one()
    {
        // The advisor relies on this distinction: "not scanned yet" must not read as
        // "swept and found nothing wrong".
        new DriftScanCache().Get(Guid.NewGuid()).Should().BeNull();
    }

    [Fact]
    public void Storing_a_sweep_replaces_the_previous_one_for_that_tenant()
    {
        DriftScanCache cache = new();
        Guid tenantId = Guid.NewGuid();

        cache.Set(tenantId, new DriftReport { Results = [], GeneratedAt = new DateTime(2026, 1, 1) });
        cache.Set(tenantId, new DriftReport { Results = [], GeneratedAt = new DateTime(2026, 2, 1) });

        cache.Get(tenantId)!.GeneratedAt.Should().Be(new DateTime(2026, 2, 1));

        cache.Clear(tenantId);
        cache.Get(tenantId).Should().BeNull();
    }
}

/// <summary>
/// Tests for updating a single row of a cached drift sweep, which is what lets the UI
/// act on one deployment without re-walking the fleet.
/// </summary>
public class DriftCacheReplaceTests
{
    private static readonly DateTime Swept = new(2026, 8, 23, 9, 0, 0, DateTimeKind.Utc);

    private static DriftResult Row(string app, DriftState state, int changedLines = 0, Guid? id = null) => new()
    {
        DeploymentId = id ?? Guid.NewGuid(),
        DeploymentName = "web",
        AppName = app,
        ClusterId = Guid.NewGuid(),
        ClusterName = "prod",
        Namespace = "ns",
        State = state,
        ChangedLines = changedLines,
        CheckedAt = Swept,
    };

    [Fact]
    public void Replacing_a_row_leaves_the_other_rows_alone()
    {
        // Re-running the whole sweep to refresh one row would walk every cluster.
        DriftScanCache cache = new();
        Guid tenantId = Guid.NewGuid();
        DriftResult target = Row("storefront", DriftState.Drifted, 12);
        DriftResult other = Row("billing", DriftState.Drifted, 4);

        cache.Set(tenantId, new DriftReport { Results = [target, other], GeneratedAt = Swept });

        cache.Replace(tenantId, target with { State = DriftState.InSync, ChangedLines = 0 });

        DriftReport updated = cache.Get(tenantId)!;
        updated.Results.Should().HaveCount(2);
        updated.Results.Single(r => r.AppName == "storefront").State.Should().Be(DriftState.InSync);
        updated.Results.Single(r => r.AppName == "billing").ChangedLines.Should().Be(4);
    }

    [Fact]
    public void A_converged_deployment_stops_counting_as_drifted_immediately()
    {
        // Otherwise the advisor keeps reporting drift that has already been fixed, and an
        // operator re-applies something that is already converged.
        DriftScanCache cache = new();
        Guid tenantId = Guid.NewGuid();
        DriftResult target = Row("storefront", DriftState.Drifted, 12);

        cache.Set(tenantId, new DriftReport { Results = [target], GeneratedAt = Swept });
        cache.Get(tenantId)!.DriftedCount.Should().Be(1);

        cache.Replace(tenantId, target with { State = DriftState.InSync, ChangedLines = 0 });

        cache.Get(tenantId)!.DriftedCount.Should().Be(0);
        cache.Get(tenantId)!.InSyncCount.Should().Be(1);
    }

    [Fact]
    public void Replacing_into_an_unswept_tenant_creates_a_report_rather_than_dropping_the_result()
    {
        DriftScanCache cache = new();
        Guid tenantId = Guid.NewGuid();

        cache.Replace(tenantId, Row("storefront", DriftState.InSync));

        cache.Get(tenantId)!.Results.Should().ContainSingle();
    }

    [Fact]
    public void The_replaced_row_is_not_duplicated()
    {
        DriftScanCache cache = new();
        Guid tenantId = Guid.NewGuid();
        Guid deploymentId = Guid.NewGuid();
        DriftResult target = Row("storefront", DriftState.Drifted, 12, deploymentId);

        cache.Set(tenantId, new DriftReport { Results = [target], GeneratedAt = Swept });
        cache.Replace(tenantId, target with { ChangedLines = 3 });
        cache.Replace(tenantId, target with { ChangedLines = 1 });

        cache.Get(tenantId)!.Results.Should().ContainSingle()
            .Which.ChangedLines.Should().Be(1);
    }

    [Fact]
    public void Rows_stay_ordered_with_drifted_first_after_a_replace()
    {
        // The table is read top-down; a converged row must not stay at the top pushing
        // the still-broken ones out of sight.
        DriftScanCache cache = new();
        Guid tenantId = Guid.NewGuid();
        DriftResult first = Row("aaa", DriftState.Drifted, 99);
        DriftResult second = Row("bbb", DriftState.Drifted, 5);

        cache.Set(tenantId, new DriftReport { Results = [first, second], GeneratedAt = Swept });
        cache.Replace(tenantId, first with { State = DriftState.InSync, ChangedLines = 0 });

        cache.Get(tenantId)!.Results[0].AppName.Should().Be("bbb");
    }
}
