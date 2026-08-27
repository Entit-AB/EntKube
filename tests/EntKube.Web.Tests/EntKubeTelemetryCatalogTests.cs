using EntKube.Web.Data;
using EntKube.Web.Services;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace EntKube.Web.Tests;

/// <summary>
/// Ties the two in-cluster telemetry catalog entries to the chart they install.
///
/// The failure this exists to catch is silent. A form field's <see cref="ComponentFormField.YamlPath"/> is
/// merged into the Helm values verbatim; if the chart has no such key, the value is written, accepted, and
/// ignored. The operator sets a 90-day retention, the install succeeds, and the node runs on the default —
/// nothing errors and nothing warns. Checking every path against the chart's own values.yaml is the only
/// place that mismatch becomes visible.
/// </summary>
public class EntKubeTelemetryCatalogTests
{
    /// <summary>The 32-byte fixture key the other vault-backed tests use.</summary>
    private static readonly byte[] TestRootKey = Convert.FromBase64String(
        "dGhpcyBpcyBhIDMyIGJ5dGUga2V5ISEhMTIzNDU2Nzg=");

    private static readonly string[] Keys =
        [EntKubeTelemetryService.IndexerKey, EntKubeTelemetryService.QuerierKey];

    /// <summary>Walks up from the test binary to the repository root, which holds charts/.</summary>
    private static string RepoRoot()
    {
        DirectoryInfo? dir = new(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "charts")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new DirectoryNotFoundException("Could not locate the repository root.");
    }

    /// <summary>
    /// Every key path in the chart's values.yaml, in the dot notation the form fields use. Parsed by
    /// indentation rather than a YAML library so the test has no dependency the app does not already carry.
    /// </summary>
    private static HashSet<string> ChartValuePaths()
    {
        string path = Path.Combine(RepoRoot(), "charts", "entkube-telemetry", "values.yaml");
        File.Exists(path).Should().BeTrue($"the chart should exist at {path}");

        HashSet<string> paths = new(StringComparer.Ordinal);
        List<string> stack = [];

        foreach (string raw in File.ReadAllLines(path))
        {
            if (raw.TrimStart().StartsWith('#') || string.IsNullOrWhiteSpace(raw)) continue;
            int colon = raw.IndexOf(':');
            if (colon < 0) continue;

            string keyPart = raw[..colon];
            if (keyPart.TrimStart().StartsWith('-')) continue;      // list items are not addressable keys
            string key = keyPart.Trim();
            if (key.Length == 0) continue;

            int depth = (keyPart.Length - keyPart.TrimStart().Length) / 2;
            while (stack.Count > depth) stack.RemoveAt(stack.Count - 1);
            stack.Add(key);
            paths.Add(string.Join('.', stack));
        }
        return paths;
    }

    /// <summary>
    /// The part of a form field path that values.yaml can actually declare.
    ///
    /// YamlFormMerger indexes sequences with a numeric segment ("imagePullSecrets.0.name"), and the
    /// elements of an empty list obviously do not appear in values.yaml. What must exist is the list
    /// itself, so a path with a numeric segment is checked up to that segment.
    /// </summary>
    private static string ListKeyOf(string yamlPath)
    {
        string[] segments = yamlPath.Split('.');
        int numeric = Array.FindIndex(segments, seg => int.TryParse(seg, out _));
        return numeric < 0 ? yamlPath : string.Join('.', segments.Take(numeric));
    }

    [Theory]
    [InlineData(EntKubeTelemetryService.IndexerKey)]
    [InlineData(EntKubeTelemetryService.QuerierKey)]
    public void Every_form_field_targets_a_path_the_chart_actually_defines(string key)
    {
        CatalogEntry entry = ComponentCatalog.GetByKey(key)!;
        HashSet<string> chartPaths = ChartValuePaths();

        // The StorageLink field is a pseudo-path ("<component>:storage-link-id") handled by
        // EntKubeTelemetryService rather than merged into the values, so it is exempt by design.
        string[] targeted = [.. entry.FormFields
            .Select(f => f.YamlPath)
            .Where(p => !p.Contains(':', StringComparison.Ordinal))];

        targeted.Should().NotBeEmpty();
        targeted.Should().OnlyContain(p => chartPaths.Contains(ListKeyOf(p)),
            "a form field writing to a path the chart does not define is silently discarded at install time");
    }

    [Theory]
    [InlineData(EntKubeTelemetryService.IndexerKey)]
    [InlineData(EntKubeTelemetryService.QuerierKey)]
    public void Both_entries_install_the_same_chart(string key)
    {
        CatalogEntry entry = ComponentCatalog.GetByKey(key)!;

        // One image, two roles — the role is chosen by the chart's values, not by a different chart.
        entry.HelmChartName.Should().Be(EntKubeTelemetryService.ChartName);
        entry.HelmChartVersion.Should().NotBeNullOrWhiteSpace("an unpinned chart makes installs irreproducible");
    }

    [Theory]
    [InlineData(EntKubeTelemetryService.IndexerKey)]
    [InlineData(EntKubeTelemetryService.QuerierKey)]
    public void The_pinned_chart_version_matches_the_chart_in_this_repository(string key)
    {
        CatalogEntry entry = ComponentCatalog.GetByKey(key)!;
        string chartYaml = File.ReadAllText(
            Path.Combine(RepoRoot(), "charts", "entkube-telemetry", "Chart.yaml"));

        string? version = chartYaml.Split('\n')
            .Select(l => l.Trim())
            .FirstOrDefault(l => l.StartsWith("version:", StringComparison.Ordinal))
            ?.Split(':', 2)[1].Trim();

        // The drift this catches: someone bumps the chart, publishes it, and the catalog keeps asking the
        // registry for the old version. The install does not fail loudly — it succeeds, on the previous
        // chart, and the change appears not to have taken. Pinning is right; unnoticed pinning is not.
        entry.HelmChartVersion.Should().Be(version,
            "the catalog pins the chart version, so it has to move when the chart does");
    }

    [Fact]
    public void The_querier_requires_the_indexer()
    {
        CatalogEntry querier = ComponentCatalog.GetByKey(EntKubeTelemetryService.QuerierKey)!;

        // It borrows the segment list and the hot tier from the indexer over HTTP; on its own it can
        // neither find segments nor see anything not yet sealed.
        querier.Dependencies.Should().Contain(EntKubeTelemetryService.IndexerKey);
    }

    [Fact]
    public void The_indexer_requires_a_collector_to_feed_it()
    {
        CatalogEntry indexer = ComponentCatalog.GetByKey(EntKubeTelemetryService.IndexerKey)!;
        indexer.Dependencies.Should().Contain("otel-collector");
    }

    [Theory]
    [InlineData(EntKubeTelemetryService.IndexerKey)]
    [InlineData(EntKubeTelemetryService.QuerierKey)]
    public void Credentials_and_tokens_are_vault_backed_and_never_shown_in_the_form(string key)
    {
        CatalogEntry entry = ComponentCatalog.GetByKey(key)!;

        string[] sensitive =
        [
            EntKubeTelemetryService.IngestTokenSecret,
            EntKubeTelemetryService.QueryTokenSecret,
            EntKubeTelemetryService.S3AccessKeySecret,
            EntKubeTelemetryService.S3SecretKeySecret,
        ];

        foreach (string secretName in sensitive)
        {
            ComponentFormField? field = entry.FormFields.FirstOrDefault(f => f.SecretName == secretName);
            field.Should().NotBeNull($"{key} must carry a field for {secretName}");
            // Stored encrypted and injected at install time; typing them by hand would put a token that
            // reads raw log bodies into a plaintext values blob.
            field!.StoreAsSecret.Should().BeTrue();
            field.Hidden.Should().BeTrue();
        }
    }

    [Theory]
    [InlineData(EntKubeTelemetryService.IndexerKey)]
    [InlineData(EntKubeTelemetryService.QuerierKey)]
    public void Both_entries_offer_a_bucket_for_sealed_segments(string key)
    {
        CatalogEntry entry = ComponentCatalog.GetByKey(key)!;

        // Without object storage the indexer seals to its own volume — history dies with the volume, and
        // a querier cannot read it at all, since the bucket is the only thing the two pods share.
        entry.FormFields.Should().Contain(f => f.Type == FormFieldType.StorageLink);
    }

    [Fact]
    public void The_querier_turns_its_own_workload_on()
    {
        CatalogEntry querier = ComponentCatalog.GetByKey(EntKubeTelemetryService.QuerierKey)!;

        // The chart defaults querier.enabled to false, since the indexer answers queries on its own.
        // Installing this component is the act of asking for query pods, so it must flip that.
        ComponentFormField? toggle = querier.FormFields.FirstOrDefault(f => f.YamlPath == "querier.enabled");
        toggle.Should().NotBeNull();
        toggle!.DefaultValue.Should().Be("true");
    }

    // ──────── Identity heal ────────

    [Fact]
    public async Task An_indexer_registered_without_an_identity_is_healed_at_install()
    {
        using SqliteConnection connection = new("DataSource=:memory:");
        connection.Open();
        using ApplicationDbContext db = new(
            new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(connection).Options);
        db.Database.EnsureCreated();

        Guid tenantId = Guid.NewGuid();
        Guid envId = Guid.NewGuid();
        Guid clusterId = Guid.NewGuid();
        db.Tenants.Add(new Tenant { Id = tenantId, Name = "Acme", Slug = "acme" });
        db.Environments.Add(new EntKube.Web.Data.Environment { Id = envId, TenantId = tenantId, Name = "prod" });
        db.KubernetesClusters.Add(new KubernetesCluster
        {
            Id = clusterId, TenantId = tenantId, EnvironmentId = envId,
            Name = "c1", ApiServerUrl = "https://k8s.example.com",
        });
        ClusterComponent component = new()
        {
            Id = Guid.NewGuid(), ClusterId = clusterId, Name = EntKubeTelemetryService.IndexerKey,
            ComponentType = "HelmChart", HelmChartName = EntKubeTelemetryService.ChartName,
            Namespace = "monitoring",
        };
        db.ClusterComponents.Add(component);
        db.SaveChanges();

        TestDbContextFactory factory = new(connection);
        IConfiguration config = TestServices.TestConfiguration("https://entkube.example.com");
        VaultService vault = new(factory, new VaultEncryptionService(TestRootKey));
        EntKubeTelemetryService sut = new(factory, vault, new IngestTokenService(config));

        component.Cluster = db.KubernetesClusters.First(c => c.Id == clusterId);

        // Registered before the identity hook existed: the chart refuses to render without these, so
        // without a heal the only fix is deleting the component and adding it again.
        string? healed = await sut.FillMissingIdentityAsync(component, valuesYaml: null);

        healed.Should().NotBeNull();
        YamlFormMerger.ExtractValue(healed!, "node.tenantId").Should().Be(tenantId.ToString());
        YamlFormMerger.ExtractValue(healed!, "node.clusterId").Should().Be(clusterId.ToString());
        YamlFormMerger.ExtractValue(healed!, "node.ingestToken").Should().NotBeNullOrWhiteSpace();
        YamlFormMerger.ExtractValue(healed!, "node.queryToken").Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task A_value_the_operator_already_set_is_never_overwritten()
    {
        using SqliteConnection connection = new("DataSource=:memory:");
        connection.Open();
        using ApplicationDbContext db = new(
            new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(connection).Options);
        db.Database.EnsureCreated();

        Guid tenantId = Guid.NewGuid();
        Guid envId = Guid.NewGuid();
        Guid clusterId = Guid.NewGuid();
        db.Tenants.Add(new Tenant { Id = tenantId, Name = "Acme", Slug = "acme" });
        db.Environments.Add(new EntKube.Web.Data.Environment { Id = envId, TenantId = tenantId, Name = "prod" });
        db.KubernetesClusters.Add(new KubernetesCluster
        {
            Id = clusterId, TenantId = tenantId, EnvironmentId = envId,
            Name = "c1", ApiServerUrl = "https://k8s.example.com",
        });
        db.SaveChanges();

        TestDbContextFactory factory = new(connection);
        IConfiguration config = TestServices.TestConfiguration("https://entkube.example.com");
        VaultService vault = new(factory, new VaultEncryptionService(TestRootKey));
        EntKubeTelemetryService sut = new(factory, vault, new IngestTokenService(config));

        // Persisted, because healing stores the query token as a vault secret and that row has a
        // foreign key to the component.
        ClusterComponent component = new()
        {
            Id = Guid.NewGuid(), ClusterId = clusterId, Name = EntKubeTelemetryService.IndexerKey,
            ComponentType = "HelmChart", HelmChartName = EntKubeTelemetryService.ChartName,
        };
        db.ClusterComponents.Add(component);
        db.SaveChanges();
        component.Cluster = db.KubernetesClusters.First(c => c.Id == clusterId);

        string existing = "node:\n  ingestToken: operator-chose-this\n";
        string? healed = await sut.FillMissingIdentityAsync(component, existing);

        // Healing fills gaps; it does not take over values someone set deliberately.
        YamlFormMerger.ExtractValue(healed!, "node.ingestToken").Should().Be("operator-chose-this");
        YamlFormMerger.ExtractValue(healed!, "node.tenantId").Should().Be(tenantId.ToString());
    }

    [Theory]
    [InlineData(EntKubeTelemetryService.IndexerKey)]
    [InlineData(EntKubeTelemetryService.QuerierKey)]
    public void The_registry_serving_these_images_is_declared_so_a_pull_secret_is_created(string key)
    {
        CatalogEntry entry = ComponentCatalog.GetByKey(key)!;

        // Without this, the chart installs cleanly and every pod sits in ImagePullBackOff — the cluster's
        // kubelet does the pull, so EntKube's own registry session is no help to it. Declaring the host is
        // what makes EntKube create the dockerconfigjson Secret from its configured credentials.
        entry.ImageRegistryHost.Should().Be("entit.azurecr.io");
    }

    [Fact]
    public void Third_party_components_declare_no_registry_because_their_images_are_public()
    {
        // The catalog's other entries pull from Docker Hub, quay.io and friends anonymously — which is why
        // none of them has ever needed a pull secret, and why this must stay opt-in rather than blanket.
        CatalogEntry collector = ComponentCatalog.GetByKey("otel-collector")!;
        collector.ImageRegistryHost.Should().BeNull();
    }
}
