using EntKube.Web.Data;
using EntKube.Web.Services;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Moq;
using YamlDotNet.RepresentationModel;

namespace EntKube.Web.Tests;

/// <summary>
/// One tenant's cluster, a stranger's beside it, and the question every test here asks of them: what
/// values would an apply hand to helm? Asked through <see cref="ComponentLifecycleService.GetInstallCommandAsync"/>
/// rather than of the steps alone, because half of each rule is WHERE in that method the step runs.
/// </summary>
public abstract class ValuesAtApply : IDisposable
{
    private readonly SqliteConnection connection;
    private readonly ComponentLifecycleService sut;

    protected ApplicationDbContext Db { get; }
    protected Guid TenantId { get; } = Guid.NewGuid();
    protected Guid ClusterId { get; } = Guid.NewGuid();
    protected Guid StrangersClusterId { get; } = Guid.NewGuid();

    protected ValuesAtApply()
    {
        connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();
        Db = new ApplicationDbContext(
            new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(connection).Options);
        Db.Database.EnsureCreated();

        foreach ((Guid tenant, Guid cluster) in new[] { (TenantId, ClusterId), (Guid.NewGuid(), StrangersClusterId) })
        {
            Db.Tenants.Add(new Tenant { Id = tenant, Name = $"t-{tenant:N}", Slug = $"t-{tenant:N}" });
            Data.Environment env = new() { Id = Guid.NewGuid(), TenantId = tenant, Name = "prod" };
            Db.Environments.Add(env);
            Db.KubernetesClusters.Add(new KubernetesCluster
            {
                Id = cluster, TenantId = tenant, EnvironmentId = env.Id,
                Name = $"c-{cluster:N}", ApiServerUrl = "https://k8s.example",
            });
        }
        Db.SaveChanges();

        TestDbContextFactory factory = new(connection);
        VaultService vault = new(factory,
            new VaultEncryptionService(Convert.FromBase64String(TestServices.TestRootKeyBase64)),
            TestServices.NoClusterAccess);

        // The tenant's own cluster answers through the seam; the stranger's is not reachable from this tenant.
        Cluster.SetupGet(c => c.ClusterId).Returns(ClusterId);
        Cluster.SetupGet(c => c.ClusterName).Returns("ours");
        ServesMonitorKinds(true);
        Mock<EntKube.Web.Services.Clusters.IClusterClientFactory> clusters = new();
        clusters.Setup(f => f.ForAsync(TenantId, ClusterId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => Reachable ? Cluster.Object : null);

        Microsoft.Extensions.Configuration.IConfiguration config = TestServices.TestConfiguration();
        IngestTokenService tokens = new(config);
        sut = new ComponentLifecycleService(
            factory, vault, TestServices.BuildKeycloak(factory, vault), tokens,
            new EntKubeTelemetryService(new EntKube.Web.Modules.Api.DataServicesApi(factory), vault, tokens,
                new EntKube.Web.Modules.Api.CatalogApi(factory), config),
            config, clusters.Object,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<ComponentLifecycleService>.Instance);
    }

    /// <summary>The tenant's cluster as the cluster-client seam hands it out.</summary>
    protected Mock<EntKube.Web.Services.Clusters.IClusterClient> Cluster { get; } = new();

    /// <summary>Whether the seam can reach the cluster at all (a stored kubeconfig, this tenant's).</summary>
    protected bool Reachable { get; set; } = true;

    /// <summary>Monitors the cluster holds, as the API server lists them: (kind resource, JSON object).</summary>
    protected List<(string Resource, string Json)> LiveMonitors { get; } = [];

    protected const string CrdResource = "customresourcedefinitions.apiextensions.k8s.io";
    protected const string ServiceMonitors = "servicemonitors.monitoring.coreos.com";
    protected const string PodMonitors = "podmonitors.monitoring.coreos.com";

    /// <summary>Whether the cluster serves the Prometheus Operator's kinds (its CRDs are installed).</summary>
    protected void ServesMonitorKinds(bool served)
    {
        foreach (string crd in new[] { ServiceMonitors, PodMonitors })
        {
            Cluster.Setup(c => c.ResourceExistsAsync(CrdResource, crd, "", It.IsAny<CancellationToken>()))
                .ReturnsAsync(served);
        }

        foreach (string kind in new[] { ServiceMonitors, PodMonitors })
        {
            Cluster.Setup(c => c.GetJsonAsync(kind, It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((string resource, string ns, string selector, CancellationToken _) =>
                    served ? ListOf(resource, ns, selector)
                           : throw new InvalidOperationException($"error: the server doesn't have a resource type \"{resource}\""));
        }
    }

    /// <summary>What <c>kubectl get &lt;resource&gt; -n ns -l a=b,c=d -o json</c> answers.</summary>
    private string ListOf(string resource, string ns, string selector)
    {
        Dictionary<string, string> wanted = selector.Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(p => p.Split('=', 2)).ToDictionary(p => p[0], p => p[1]);
        System.Text.Json.Nodes.JsonArray items = [];
        foreach ((string kind, string json) in LiveMonitors.Where(m => m.Resource == resource))
        {
            System.Text.Json.Nodes.JsonNode item = System.Text.Json.Nodes.JsonNode.Parse(json)!;
            if ((string?)item["metadata"]!["namespace"] != ns) continue;
            System.Text.Json.Nodes.JsonObject labels = item["metadata"]!["labels"]!.AsObject();
            if (!wanted.All(w => labels.TryGetPropertyValue(w.Key, out System.Text.Json.Nodes.JsonNode? v)
                                 && (string?)v == w.Value)) continue;
            items.Add(item);
        }
        return new System.Text.Json.Nodes.JsonObject { ["kind"] = "List", ["items"] = items }.ToJsonString();
    }

    /// <summary>A monitor as the API server lists it. By default the monitor engine's, for this tenant's cluster.</summary>
    protected string Monitor(string kind, string name, string ns, Guid? labelledFor = null, bool managedBy = true)
    {
        System.Text.Json.Nodes.JsonObject labels = new()
        {
            ["entkube.io/scrape-monitor"] = (labelledFor ?? ClusterId).ToString(),
        };
        if (managedBy) labels["app.kubernetes.io/managed-by"] = "entkube";
        return new System.Text.Json.Nodes.JsonObject
        {
            ["apiVersion"] = "monitoring.coreos.com/v1",
            ["kind"] = kind,
            ["metadata"] = new System.Text.Json.Nodes.JsonObject { ["name"] = name, ["namespace"] = ns, ["labels"] = labels },
        }.ToJsonString();
    }

    /// <summary>The monitor engine's own ServiceMonitor for the catalog's Keycloak (release keycloak, namespace keycloak).</summary>
    protected void EnginesKeycloakMonitorStands(string release = "keycloak", string ns = "keycloak")
        => LiveMonitors.Add((ServiceMonitors, Monitor("ServiceMonitor", $"entkube-keycloak-{release}", ns)));

    public void Dispose()
    {
        Db.Dispose();
        connection.Dispose();
        GC.SuppressFinalize(this);
    }

    protected static string CatalogDefaults(string key) => ComponentCatalog.GetByKey(key)!.DefaultValues!;

    /// <summary>A catalog component as registration stores it, with the values given.</summary>
    protected Guid Install(string key, string? values)
    {
        CatalogEntry entry = ComponentCatalog.GetByKey(key)!;
        return InstallChart(key, entry.HelmChartName!, entry.DefaultReleaseName, entry.DefaultNamespace, values);
    }

    protected Guid InstallChart(string name, string chart, string? release, string? @namespace, string? values)
    {
        ClusterComponent component = new()
        {
            Id = Guid.NewGuid(), ClusterId = ClusterId, Name = name, ComponentType = "HelmChart",
            HelmChartName = chart, ReleaseName = release, Namespace = @namespace, HelmValues = values,
        };
        Db.ClusterComponents.Add(component);
        Db.SaveChanges();
        return component.Id;
    }

    /// <param name="configuration">
    /// The component's Configuration JSON, where an adopted stack records the namespace it really runs in.
    /// </param>
    protected ClusterComponent InstallPrometheus(
        Guid? cluster = null, string? @namespace = "monitoring", ComponentStatus status = ComponentStatus.Installed,
        string? configuration = null)
    {
        ClusterComponent prometheus = new()
        {
            Id = Guid.NewGuid(), ClusterId = cluster ?? ClusterId, Name = "kube-prometheus-stack",
            ComponentType = "HelmChart", HelmChartName = "kube-prometheus-stack",
            ReleaseName = "kube-prometheus-stack", Namespace = @namespace, Status = status,
            Configuration = configuration,
        };
        Db.ClusterComponents.Add(prometheus);
        Db.SaveChanges();
        return prometheus;
    }

    protected async Task<string> ApplyAsync(Guid componentId)
        => (await sut.GetInstallCommandAsync(TenantId, componentId)).ValuesYaml!;

    protected async Task<string?> StoredAsync(Guid componentId)
    {
        Db.ChangeTracker.Clear();
        return (await Db.ClusterComponents.SingleAsync(c => c.Id == componentId)).HelmValues;
    }

    protected static YamlMappingNode Parse(string yaml)
    {
        YamlStream stream = new();
        stream.Load(new StringReader(yaml));
        return (YamlMappingNode)stream.Documents[0].RootNode;
    }

    protected static YamlNode? At(YamlMappingNode root, params string[] path)
    {
        YamlNode current = root;
        foreach (string segment in path)
        {
            if (current is not YamlMappingNode mapping) return null;
            YamlScalarNode? key = mapping.Children.Keys.OfType<YamlScalarNode>().FirstOrDefault(k => k.Value == segment);
            if (key is null) return null;
            current = mapping.Children[key];
        }
        return current;
    }

    protected static string? Scalar(string yaml, params string[] path) => (At(Parse(yaml), path) as YamlScalarNode)?.Value;

    /// <summary>
    /// What a node says, spelled one way: keys sorted, and a quoted <c>"000"</c> told apart from a plain
    /// <c>000</c>, because to helm the first is a string and the second a number. Compared as text because
    /// FluentAssertions' structural comparison never finishes on YamlDotNet's nodes.
    /// </summary>
    protected static string Said(YamlNode node) => node switch
    {
        YamlScalarNode { Style: YamlDotNet.Core.ScalarStyle.Plain } plain => plain.Value ?? "",
        YamlScalarNode text => $"'{text.Value}'",
        YamlSequenceNode list => $"[{string.Join(",", list.Children.Select(Said))}]",
        YamlMappingNode map => "{" + string.Join(",", map.Children
            .Select(kv => $"{Said(kv.Key)}:{Said(kv.Value)}").Order(StringComparer.Ordinal)) + "}",
        _ => throw new InvalidOperationException($"Not a node helm values can hold: {node.GetType().Name}"),
    };

    /// <summary>Everything a document says except one top-level key — to show a step changed only that one.</summary>
    protected static string SaidApartFrom(string yaml, string key)
    {
        YamlMappingNode root = Parse(yaml);
        root.Children.Remove(new YamlScalarNode(key));
        return Said(root);
    }
}

/// <summary>
/// Keycloak's metrics are scraped through EntKube's own ServiceMonitor where the engine made one, so there the
/// chart's has to stop.
///
/// <para>The catalog turned the chart's on for every install. Its kind exists only where the Prometheus
/// Operator is, so on a cluster without it helm refused the whole release and Keycloak did not install.
/// And where EntKube's own monitor stands beside it, every number would be doubled. Everywhere else the chart's
/// may be the only monitor there is, and is handed to helm as stored.</para>
/// </summary>
public class KeycloakMonitorHandoverTests : ValuesAtApply
{
    /// <summary>The block the catalog shipped from 2026-08-20 until this change, with its neighbours.</summary>
    private const string StoredByTheOldCatalog = """
        database:
          vendor: postgres
        extraEnv: |
          - name: KC_PROXY_HEADERS
            value: xforwarded
          - name: KC_HOSTNAME_STRICT
            value: "false"
        metrics:
          enabled: true
        health:
          enabled: true
        serviceMonitor:
          enabled: true

        # HTTP configuration
        http:
          relativePath: /auth
        """;

    [Fact]
    public void The_catalog_no_longer_asks_the_chart_for_a_monitor()
    {
        At(Parse(CatalogDefaults("keycloak")), "serviceMonitor").Should().BeNull(
            "the chart renders it without asking whether the cluster has the CRD, and EntKube makes its own");

        // The endpoint the monitor reads is still there.
        Scalar(CatalogDefaults("keycloak"), "metrics", "enabled").Should().Be("true");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_new_keycloak_installs_without_the_charts_monitor_whether_or_not_there_is_a_prometheus(bool prometheus)
    {
        if (prometheus) InstallPrometheus();
        else ServesMonitorKinds(false);

        string values = await ApplyAsync(Install("keycloak", CatalogDefaults("keycloak")));

        Scalar(values, "serviceMonitor", "enabled").Should().NotBe("true");
    }

    /// <summary>
    /// Where the monitor engine's own ServiceMonitor for this Keycloak already stands, the chart's beside it would
    /// be a second scrape of the same pods. Read from the cluster, whatever Catalog says: here no
    /// kube-prometheus-stack is registered at all (it may be uninstalling, or its row gone) and ours still runs.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Where_the_engines_own_monitor_stands_the_old_catalog_block_is_turned_off(bool catalogPrometheus)
    {
        if (catalogPrometheus) InstallPrometheus();
        EnginesKeycloakMonitorStands();

        string values = await ApplyAsync(Install("keycloak", StoredByTheOldCatalog));

        Scalar(values, "serviceMonitor", "enabled").Should().Be("false",
            "off in words rather than removed: an absent key is what the catalog fill-in writes into");
    }

    /// <summary>
    /// The skeptic's case (a): EntKube's Prometheus serves the cluster (or another Prometheus does, or one EntKube
    /// is still installing), but the engine made nothing for this Keycloak: Prometheus selects the chart's monitor
    /// and the planner sees it covered, or it is sharded, ambiguous, unreadable. The chart's may be the only monitor
    /// there is, so the block is handed to helm as it was stored.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData(ComponentStatus.Installed)]
    [InlineData(ComponentStatus.NotInstalled)]
    [InlineData(ComponentStatus.Failed)]
    [InlineData(ComponentStatus.Installing)]
    public async Task Where_no_monitor_of_ours_stands_the_block_is_left_as_stored(ComponentStatus? status)
    {
        if (status is { } prometheus) InstallPrometheus(status: prometheus);

        string values = await ApplyAsync(Install("keycloak", StoredByTheOldCatalog));

        Said(At(Parse(values), "serviceMonitor")!).Should().Be("{enabled:true}",
            "nothing of EntKube's scrapes this Keycloak, so the chart's is the only monitor there is");
    }

    /// <summary>
    /// Only the engine's own monitor for this release counts: its name, its namespace, both ownership labels, this
    /// cluster's id. A same-named monitor somebody else wrote is not ours, nor one for another release.
    /// </summary>
    [Theory]
    [InlineData("for another release")]
    [InlineData("in another namespace")]
    [InlineData("labelled for another cluster")]
    [InlineData("not managed by entkube")]
    [InlineData("a PodMonitor of that name")]
    public async Task A_monitor_that_is_not_wholly_ours_does_not_hand_keycloak_over(string which)
    {
        InstallPrometheus();
        LiveMonitors.Add(which switch
        {
            "for another release" => (ServiceMonitors, Monitor("ServiceMonitor", "entkube-keycloak-other", "keycloak")),
            "in another namespace" => (ServiceMonitors, Monitor("ServiceMonitor", "entkube-keycloak-keycloak", "identity")),
            "labelled for another cluster" => (ServiceMonitors,
                Monitor("ServiceMonitor", "entkube-keycloak-keycloak", "keycloak", labelledFor: StrangersClusterId)),
            "not managed by entkube" => (ServiceMonitors,
                Monitor("ServiceMonitor", "entkube-keycloak-keycloak", "keycloak", managedBy: false)),
            "a PodMonitor of that name" => (PodMonitors, Monitor("PodMonitor", "entkube-keycloak-keycloak", "keycloak")),
            _ => throw new ArgumentOutOfRangeException(nameof(which)),
        });

        string values = await ApplyAsync(Install("keycloak", StoredByTheOldCatalog));

        Said(At(Parse(values), "serviceMonitor")!).Should().Be("{enabled:true}");
    }

    /// <summary>
    /// keycloak-crdless-regression: on a cluster without the Prometheus Operator's CRDs the chart's ServiceMonitor
    /// cannot be created, and helm refuses the whole release ("no matches for kind ServiceMonitor"). So the
    /// catalog's block is turned off there, whatever the handover would say, with or without a Prometheus row.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData(ComponentStatus.Installed)]
    [InlineData(ComponentStatus.Uninstalling)]
    public async Task Where_the_cluster_has_no_servicemonitor_kind_the_block_is_turned_off_so_helm_can_install(
        ComponentStatus? status)
    {
        if (status is { } prometheus) InstallPrometheus(status: prometheus);
        ServesMonitorKinds(false);

        string values = await ApplyAsync(Install("keycloak", StoredByTheOldCatalog));

        Said(At(Parse(values), "serviceMonitor")!).Should().Be("{enabled:false}",
            "a ServiceMonitor helm cannot create fails the release, and Keycloak with it");
    }

    /// <summary>
    /// Unknown means do nothing: a cluster that cannot be reached, a CRD probe or a monitor list that fails. Each of
    /// a refused release, a double scrape and a lost one is on the table, so the apply fails before helm is given
    /// anything rather than guess.
    /// </summary>
    [Theory]
    [InlineData("unreachable")]
    [InlineData("kind")]
    [InlineData("list")]
    [InlineData("garbage")]
    public async Task Where_the_cluster_cannot_say_the_apply_fails_before_helm_is_given_anything(string failure)
    {
        InstallPrometheus();
        string expected = "*cluster unreachable*";
        switch (failure)
        {
            case "unreachable":
                Reachable = false;
                expected = "*cannot be told*";
                break;
            case "kind":
                Cluster.Setup(c => c.ResourceExistsAsync(CrdResource, ServiceMonitors, "", It.IsAny<CancellationToken>()))
                    .ThrowsAsync(new InvalidOperationException("cluster unreachable"));
                break;
            case "list":
                Cluster.Setup(c => c.GetJsonAsync(ServiceMonitors, It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                    .ThrowsAsync(new InvalidOperationException("cluster unreachable"));
                break;
            case "garbage":
                Cluster.Setup(c => c.GetJsonAsync(ServiceMonitors, It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                    .ReturnsAsync("Unable to connect to the server");
                expected = "*could not be read*";
                break;
        }
        Guid keycloak = Install("keycloak", StoredByTheOldCatalog);

        Func<Task> act = () => ApplyAsync(keycloak);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage(expected);
    }

    /// <summary>The cluster is asked only where the answer can change something: a Keycloak without the block is not read for.</summary>
    [Fact]
    public async Task A_keycloak_without_the_catalogs_block_asks_the_cluster_nothing()
    {
        InstallPrometheus();
        Reachable = false;

        string values = await ApplyAsync(Install("keycloak", CatalogDefaults("keycloak")));

        At(Parse(values), "serviceMonitor").Should().BeNull();
        Cluster.Invocations.Should().BeEmpty();
    }

    [Fact]
    public async Task A_prometheus_on_another_cluster_does_not_hand_keycloak_over()
    {
        InstallPrometheus(cluster: StrangersClusterId);

        string values = await ApplyAsync(Install("keycloak", StoredByTheOldCatalog));

        Said(At(Parse(values), "serviceMonitor")!).Should().Be("{enabled:true}");
    }

    [Theory]
    [InlineData("serviceMonitor: {enabled: true}")]
    [InlineData("serviceMonitor:\n  enabled: \"true\"")]
    [InlineData("serviceMonitor:\n  # chart default is off\n  enabled: true # scrape it")]
    public async Task The_same_block_written_another_way_is_still_the_catalogs(string block)
    {
        InstallPrometheus();
        EnginesKeycloakMonitorStands();
        Guid keycloak = Install("keycloak", "metrics:\n  enabled: true\n" + block + "\n");

        Scalar(await ApplyAsync(keycloak), "serviceMonitor", "enabled").Should().Be("false");
    }

    [Theory]
    [InlineData("serviceMonitor:\n  enabled: true\n  interval: 30s")]
    [InlineData("serviceMonitor:\n  enabled: true\n  labels:\n    release: prom")]
    [InlineData("serviceMonitor:\n  enabled: false")]
    [InlineData("serviceMonitor:\n  labels:\n    team: identity")]
    [InlineData("serviceMonitor: {}")]
    [InlineData("extraServiceMonitor:\n  enabled: true")]
    public async Task A_block_the_operator_made_theirs_is_kept_to_the_letter(string block)
    {
        InstallPrometheus();
        EnginesKeycloakMonitorStands();
        string stored = CatalogDefaults("keycloak") + "\n# ours\n" + block + "\n";

        (await ApplyAsync(Install("keycloak", stored))).Should().Be(stored);
    }

    [Fact]
    public async Task An_install_older_than_the_block_is_not_given_one()
    {
        InstallPrometheus();
        EnginesKeycloakMonitorStands();
        Guid keycloak = Install("keycloak", "database:\n  vendor: postgres\nhttp:\n  relativePath: /auth\n");

        string values = await ApplyAsync(keycloak);

        Scalar(values, "metrics", "enabled").Should().Be("true", "the fill-in itself still runs");
        At(Parse(values), "serviceMonitor").Should().BeNull(
            "the fill-in used to add the catalog's monitor to installs that never had one");
    }

    [Fact]
    public async Task Turning_it_off_leaves_one_serviceMonitor_key_and_the_rest_as_it_was()
    {
        InstallPrometheus();
        EnginesKeycloakMonitorStands();
        Guid keycloak = Install("keycloak", StoredByTheOldCatalog);

        string values = await ApplyAsync(keycloak);

        // Parse refuses a document with a key twice, so this also says the fill-in did not add a second.
        Said(At(Parse(values), "serviceMonitor")!).Should().Be("{enabled:false}");

        // Only what the fill-in brings (proxy, args, resources, extraEnvFrom) is new; every key that was
        // stored says what it said.
        YamlMappingNode before = Parse(StoredByTheOldCatalog);
        YamlMappingNode after = Parse(values);
        foreach (YamlScalarNode key in before.Children.Keys.Cast<YamlScalarNode>().Where(k => k.Value != "serviceMonitor"))
        {
            Said(after.Children[key]).Should().Be(Said(before.Children[key]), $"{key.Value} was not ours to touch");
        }
    }

    /// <summary>
    /// ⚠️ Pins that the step runs BEFORE the theme fragments are appended. They are joined on as text, so a
    /// component with its own <c>extraVolumes</c> ends up with that key twice: helm takes the last one, and
    /// our parser refuses the document. After them, this Keycloak would keep the chart's monitor.
    /// </summary>
    [Fact]
    public async Task A_keycloak_with_themes_and_volumes_of_its_own_is_handed_over_too()
    {
        InstallPrometheus();
        EnginesKeycloakMonitorStands();
        Guid keycloak = Install("keycloak", StoredByTheOldCatalog + "\nextraVolumes: |\n  - name: ours\n    emptyDir: {}\n");
        KeycloakComponentConfig config = new()
        {
            Id = Guid.NewGuid(), TenantId = TenantId, ClusterComponentId = keycloak,
            AdminUrl = "https://id.example/auth", AdminUsername = "admin",
        };
        Db.KeycloakComponentConfigs.Add(config);
        Db.KeycloakThemes.Add(new KeycloakTheme
        {
            Id = Guid.NewGuid(), TenantId = TenantId, KeycloakComponentConfigId = config.Id, Name = "Entit Blue",
        });
        Db.SaveChanges();

        string values = await ApplyAsync(keycloak);

        // Read as text: this is the document our parser will not open.
        values.Split('\n').Count(l => l.StartsWith("extraVolumes:", StringComparison.Ordinal)).Should().Be(2,
            "the theme's fragment is joined on after the operator's — the state this test is about");
        System.Text.RegularExpressions.Regex.Matches(values, @"^serviceMonitor:\r?\n\s+enabled: (\w+)",
                System.Text.RegularExpressions.RegexOptions.Multiline)
            .Select(m => m.Groups[1].Value).Should().Equal("false");
    }

    /// <summary>
    /// The step writes the document out again, and the writer used to delete every <c>---</c> that ended a
    /// line — which is the end of a certificate's first line, and the separator in a list of manifests.
    /// </summary>
    [Fact]
    public async Task Certificates_and_manifests_kept_in_the_values_come_through_whole()
    {
        InstallPrometheus();
        EnginesKeycloakMonitorStands();
        Guid keycloak = Install("keycloak", StoredByTheOldCatalog + """

            secrets:
              truststore:
                stringData:
                  ca.crt: |
                    -----BEGIN CERTIFICATE-----
                    MIIBszCCAVmgAwIBAgIU
                    -----END CERTIFICATE-----
            extraManifests: |
              apiVersion: v1
              kind: ConfigMap
              ---
              apiVersion: v1
              kind: Secret
            """ + "\n");
        string stored = (await StoredAsync(keycloak))!;

        string values = await ApplyAsync(keycloak);

        Scalar(values, "serviceMonitor", "enabled").Should().Be("false", "the document was written out again");
        foreach (string key in new[] { "secrets", "extraManifests" })
        {
            Said(At(Parse(values), key)!).Should().Be(Said(At(Parse(stored), key)!));
        }
    }

    /// <summary>
    /// A script kept as the last thing in the document keeps its last newline. The chart hashes
    /// <c>startupScripts</c> into the pod template, so losing it would restart Keycloak — and this change
    /// restarts nothing.
    /// </summary>
    [Fact]
    public async Task The_last_text_in_the_document_keeps_its_newline()
    {
        InstallPrometheus();
        EnginesKeycloakMonitorStands();
        Guid keycloak = Install("keycloak", CatalogDefaults("keycloak")
            + "\nserviceMonitor:\n  enabled: true\nstartupScripts:\n  init.sh: |\n    #!/bin/sh\n    echo ready\n");

        string values = await ApplyAsync(keycloak);

        Scalar(values, "serviceMonitor", "enabled").Should().Be("false", "the document was written out again");
        Scalar(values, "startupScripts", "init.sh").Should().Be("#!/bin/sh\necho ready\n");
    }

    [Fact]
    public async Task The_stored_values_are_not_rewritten()
    {
        InstallPrometheus();
        EnginesKeycloakMonitorStands();
        Guid keycloak = Install("keycloak", StoredByTheOldCatalog);

        await ApplyAsync(keycloak);

        (await StoredAsync(keycloak)).Should().Be(StoredByTheOldCatalog);
    }

    [Fact]
    public async Task Another_charts_serviceMonitor_is_not_keycloaks()
    {
        const string stored = "serviceMonitor:\n  enabled: true\n";

        (await ApplyAsync(InstallChart("acme-exporter", "acme-exporter", "acme-exporter", "acme", stored)))
            .Should().Be(stored);
    }
}

/// <summary>
/// SeaweedFS's NetworkPolicies admit only the release's own pods and the S3 port, so Prometheus cannot
/// reach the metrics port of any of its four servers. One rule lets Prometheus in, and nobody else: the
/// same port serves Go's pprof.
/// </summary>
public class SeaweedFsLetsPrometheusInTests : ValuesAtApply
{
    private static string ThePrometheusRule(string @namespace, params int[] ports) =>
        "{from:[{namespaceSelector:{matchLabels:{kubernetes.io/metadata.name:'" + @namespace
        + "'}},podSelector:{matchLabels:{app.kubernetes.io/name:prometheus}}}],ports:["
        + string.Join(",", (ports.Length == 0 ? [9327] : ports).Select(p => $"{{port:{p},protocol:TCP}}")) + "]}";

    /// <summary>The catalog's values with <paramref name="line"/> as the first line of each server's block.</summary>
    private static string CatalogWithServerLines(params (string Server, string Line)[] lines)
    {
        string values = CatalogDefaults("seaweedfs");
        foreach ((string server, string line) in lines)
        {
            string header = $"\n{server}:\n";
            values.Should().Contain(header);
            values = values.Replace(header, $"{header}  {line}\n");
        }
        return values;
    }

    private const string TheS3Rule = "{ports:[{port:8333,protocol:TCP}]}";

    private static List<string> Rules(string values)
        => [.. ((YamlSequenceNode)At(Parse(values), "networkPolicy", "extraIngress")!).Children.Select(Said)];

    /// <summary>The catalog's values with its <c>networkPolicy</c> block replaced by the operator's.</summary>
    private static string CatalogWithPolicy(string policy)
    {
        string values = CatalogDefaults("seaweedfs");
        int start = values.IndexOf("networkPolicy:", StringComparison.Ordinal);
        int end = values.IndexOf("\n\n", start, StringComparison.Ordinal);
        return values[..start] + policy + values[end..];
    }

    [Fact]
    public async Task Where_there_is_a_prometheus_its_pods_may_reach_the_metrics_port()
    {
        InstallPrometheus(@namespace: "monitoring");

        string values = await ApplyAsync(Install("seaweedfs", CatalogDefaults("seaweedfs")));

        Rules(values).Should().Equal(TheS3Rule, ThePrometheusRule("monitoring"));
    }

    /// <summary>
    /// ⚠️ Two entries under <c>from</c> would mean OR: every pod in the monitoring namespace, and every pod
    /// calling itself prometheus in SeaweedFS's own. One entry holding both selectors means AND.
    /// </summary>
    [Fact]
    public async Task The_namespace_and_the_pod_have_to_match_together()
    {
        InstallPrometheus();

        string values = await ApplyAsync(Install("seaweedfs", CatalogDefaults("seaweedfs")));

        YamlSequenceNode rules = (YamlSequenceNode)At(Parse(values), "networkPolicy", "extraIngress")!;
        YamlSequenceNode from = (YamlSequenceNode)At((YamlMappingNode)rules.Children[^1], "from")!;
        from.Children.Count.Should().Be(1);
        ((YamlMappingNode)from.Children[0]).Children.Keys.Cast<YamlScalarNode>().Select(k => k.Value)
            .Should().BeEquivalentTo("namespaceSelector", "podSelector");
    }

    [Fact]
    public async Task The_rule_names_the_namespace_this_clusters_prometheus_runs_in()
    {
        InstallPrometheus(@namespace: "observability");

        string values = await ApplyAsync(Install("seaweedfs", CatalogDefaults("seaweedfs")));

        Rules(values).Should().Equal(TheS3Rule, ThePrometheusRule("observability"));
    }

    /// <summary>
    /// Kubernetes allows a namespace called 2026 and refuses a label value that is a number. And whatever is
    /// recorded as the namespace must never turn the rule into an empty one, which would admit everyone.
    /// </summary>
    [Theory]
    [InlineData("2026")]
    [InlineData("a: [b")]
    public async Task The_namespace_is_a_text_whatever_it_looks_like(string @namespace)
    {
        InstallPrometheus(@namespace: @namespace);

        string values = await ApplyAsync(Install("seaweedfs", CatalogDefaults("seaweedfs")));

        Rules(values).Should().Equal(TheS3Rule, ThePrometheusRule(@namespace));
    }

    [Fact]
    public async Task Nothing_but_the_rule_changes()
    {
        InstallPrometheus();
        string stored = CatalogDefaults("seaweedfs");

        string values = await ApplyAsync(Install("seaweedfs", stored));

        Rules(values).Count.Should().Be(2, "the rule was added");
        SaidApartFrom(values, "networkPolicy").Should().Be(SaidApartFrom(stored, "networkPolicy"));
        Scalar(values, "networkPolicy", "enabled").Should().Be("true");
    }

    [Fact]
    public async Task The_operators_own_rules_stay_as_they_are_and_where_they_are()
    {
        InstallPrometheus();
        Guid seaweed = Install("seaweedfs", """
            networkPolicy:
              enabled: true
              extraIngress:
                - from:
                    - ipBlock:
                        cidr: 10.0.0.0/8
                  ports:
                    - protocol: TCP
                      port: 9327
                - ports:
                    - protocol: TCP
                      port: 8333
              components:
                filer:
                  extraIngress:
                    - ports:
                        - port: 8888
            """);

        string values = await ApplyAsync(seaweed);

        Rules(values).Should().Equal(
            "{from:[{ipBlock:{cidr:10.0.0.0/8}}],ports:[{port:9327,protocol:TCP}]}",
            TheS3Rule,
            ThePrometheusRule("monitoring"));
        Said(At(Parse(values), "networkPolicy", "components")!).Should().Be("{filer:{extraIngress:[{ports:[{port:8888}]}]}}");
    }

    [Fact]
    public async Task Values_that_already_hold_the_rule_are_handed_to_helm_as_they_are()
    {
        InstallPrometheus();
        string applied = await ApplyAsync(Install("seaweedfs", CatalogDefaults("seaweedfs")));
        Rules(applied).Should().Contain(ThePrometheusRule("monitoring"));

        Db.ClusterComponents.RemoveRange(Db.ClusterComponents.Where(c => c.HelmChartName == "seaweedfs"));
        Db.SaveChanges();

        (await ApplyAsync(Install("seaweedfs", applied))).Should().Be(applied, "the rule is there, however it got there");
    }

    /// <summary>
    /// ⚠️ Pins the ORDER. An install with no <c>networkPolicy</c> key gets the catalog's from the fill-in,
    /// enabled, with the S3 rule. Run before the fill-in, this step would see no policy and add nothing —
    /// and the fill-in would then close the port to Prometheus.
    /// </summary>
    [Fact]
    public async Task An_install_the_fill_in_gives_a_policy_to_gets_the_rule_with_it()
    {
        InstallPrometheus();
        Guid seaweed = Install("seaweedfs", "s3:\n  enabled: true\n  enableAuth: true\n");

        string values = await ApplyAsync(seaweed);

        Scalar(values, "networkPolicy", "enabled").Should().Be("true");
        Rules(values).Should().Equal(TheS3Rule, ThePrometheusRule("monitoring"));
    }

    [Theory]
    [InlineData("networkPolicy:\n  enabled: true")]
    [InlineData("networkPolicy:\n  enabled: true\n  extraIngress:")]
    [InlineData("networkPolicy:\n  enabled: true\n  extraIngress: null")]
    [InlineData("networkPolicy:\n  enabled: true\n  extraIngress: []")]
    public async Task A_policy_with_no_extra_rules_is_given_this_one(string policy)
    {
        InstallPrometheus();

        string values = await ApplyAsync(Install("seaweedfs", CatalogWithPolicy(policy)));

        Rules(values).Should().Equal(ThePrometheusRule("monitoring"));
    }

    /// <summary>
    /// Off means the chart renders no policy and every port is reachable already. Nothing is written, and in
    /// particular no policy is switched on: it would be the first to select these pods, and would cut off S3.
    /// </summary>
    [Theory]
    [InlineData("networkPolicy:\n  enabled: false\n  extraIngress:\n    - ports:\n        - port: 8333")]
    [InlineData("networkPolicy: {}")]
    [InlineData("networkPolicy:\n  extraIngress: []")]
    [InlineData("networkPolicy:")]
    public async Task Where_the_operator_has_the_policy_off_nothing_is_written(string policy)
    {
        InstallPrometheus();
        string stored = CatalogWithPolicy(policy);

        (await ApplyAsync(Install("seaweedfs", stored))).Should().Be(stored);
    }

    /// <summary>A list EntKube cannot read is not a list it may replace.</summary>
    [Theory]
    [InlineData("networkPolicy:\n  enabled: true\n  extraIngress:\n    s3: 8333")]
    [InlineData("networkPolicy:\n  enabled: true\n  extraIngress: \"see wiki\"")]
    public async Task Rules_in_a_shape_that_is_not_a_list_are_left_alone(string policy)
    {
        InstallPrometheus();
        string stored = CatalogWithPolicy(policy);

        (await ApplyAsync(Install("seaweedfs", stored))).Should().Be(stored);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(ComponentStatus.NotInstalled)]
    [InlineData(ComponentStatus.Failed)]
    public async Task Without_a_prometheus_nothing_is_opened(ComponentStatus? status)
    {
        if (status is { } prometheus) InstallPrometheus(status: prometheus);
        string stored = CatalogDefaults("seaweedfs");

        (await ApplyAsync(Install("seaweedfs", stored))).Should().Be(stored);
    }

    [Fact]
    public async Task Another_tenants_prometheus_does_not_count()
    {
        InstallPrometheus(cluster: StrangersClusterId);
        string stored = CatalogDefaults("seaweedfs");

        (await ApplyAsync(Install("seaweedfs", stored))).Should().Be(stored);
    }

    /// <summary>
    /// The engine places Prometheus where <see cref="PrometheusService.GetPrometheusConfig(ClusterComponent)"/>
    /// says, and an adopted stack's Configuration overrides the column. Opening the port to the column's
    /// namespace instead lets in prometheus-labelled pods of a namespace that is not Prometheus's, and leaves
    /// the real one blocked while the panel tells the operator to allow it in.
    /// </summary>
    [Theory]
    [InlineData("monitoring", "{\"namespace\":\"observability\"}", "observability")]
    [InlineData(null, "{\"namespace\":\"observability\"}", "observability")]
    [InlineData("  ", "{\"Namespace\":\"observability\"}", "observability")]
    [InlineData("monitoring", "{\"namespace\":\"\"}", "monitoring")]
    [InlineData("monitoring", "{\"serviceName\":\"prom\"}", "monitoring")]
    [InlineData("monitoring", "not json", "monitoring")]
    public async Task The_rule_names_the_namespace_the_engine_places_prometheus_in(
        string? column, string configuration, string expected)
    {
        ClusterComponent prometheus = InstallPrometheus(@namespace: column, configuration: configuration);

        string values = await ApplyAsync(Install("seaweedfs", CatalogDefaults("seaweedfs")));

        Rules(values).Should().Equal(TheS3Rule, ThePrometheusRule(expected));
        PrometheusService.GetPrometheusConfig(prometheus).Namespace.Should().Be(expected,
            "the apply and the engine give one answer");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("  ")]
    public async Task A_prometheus_whose_namespace_is_not_recorded_opens_nothing(string? @namespace)
    {
        InstallPrometheus(@namespace: @namespace);
        string stored = CatalogDefaults("seaweedfs");

        (await ApplyAsync(Install("seaweedfs", stored))).Should().Be(stored,
            "a guessed namespace would admit somebody else's pods to the port that serves pprof");
    }

    /// <summary>
    /// seaweedfs-default-namespace: the two halves agree. The engine judges admission by the policies it reads
    /// (<see cref="EntKube.Web.Services.ScrapeMonitors.PrometheusAdmission"/>) against the Prometheus it finds
    /// live; the apply writes the rule. Fed the policy the chart renders from the values the apply hands helm,
    /// the engine lets Prometheus in exactly where the apply opened the port to it.
    ///
    /// <para><b>A blank namespace is unknown on both sides.</b> The engine looks for Prometheus in its default
    /// (monitoring) and may well find it there, but the apply opened nothing for a namespace nobody recorded, and
    /// the engine, reading the policy rather than the record, sees the port shut: BlockedByNetworkPolicy, no
    /// monitor made, nothing guessed open to pods that only call themselves prometheus.</para>
    /// </summary>
    [Theory]
    [InlineData("monitoring", null, "monitoring", true)]
    [InlineData("monitoring", "{\"namespace\":\"observability\"}", "observability", true)]
    [InlineData("monitoring", null, "observability", false)]
    [InlineData(null, null, "monitoring", false)]
    [InlineData("  ", null, "monitoring", false)]
    public async Task The_engine_lets_prometheus_in_exactly_where_the_apply_opened_the_port(
        string? column, string? configuration, string livePrometheusNamespace, bool admitted)
    {
        InstallPrometheus(@namespace: column, configuration: configuration);

        string values = await ApplyAsync(Install("seaweedfs", CatalogDefaults("seaweedfs")));

        k8s.Models.V1Pod filer = new()
        {
            Metadata = new k8s.Models.V1ObjectMeta
            {
                Name = "seaweedfs-filer-0", NamespaceProperty = "storage",
                Labels = new Dictionary<string, string>
                {
                    ["app.kubernetes.io/name"] = "seaweedfs", ["app.kubernetes.io/component"] = "filer",
                },
            },
            Spec = new k8s.Models.V1PodSpec
            {
                Containers =
                [
                    new k8s.Models.V1Container
                    {
                        Name = "seaweedfs",
                        Ports = [new k8s.Models.V1ContainerPort { Name = "metrics", ContainerPort = 9327 }],
                    },
                ],
            },
            Status = new k8s.Models.V1PodStatus { PodIP = "10.0.0.5" },
        };
        EntKube.Web.Services.ScrapeMonitors.PrometheusAdmission engine = new(
            livePrometheusNamespace,
            new Dictionary<string, string> { ["kubernetes.io/metadata.name"] = livePrometheusNamespace },
            [new Dictionary<string, string> { ["app.kubernetes.io/name"] = "prometheus" }]);

        engine.Admits(
                new EntKube.Web.Services.ScrapeMonitors.ScrapedEndpoint(filer, 9327, "/metrics"),
                new EntKube.Web.Services.ScrapeMonitors.LiveNamespace("storage", [filer], [], [TheChartsPolicy(values)]))
            .Should().Be(admitted);
    }

    /// <summary>
    /// The policy the chart renders for a server: its own rule (the S3 port) and every networkPolicy.extraIngress
    /// rule, over all of the release's pods.
    /// </summary>
    private static k8s.Models.V1NetworkPolicy TheChartsPolicy(string values)
    {
        YamlMappingNode policy = new(
            new YamlScalarNode("apiVersion"), new YamlScalarNode("networking.k8s.io/v1"),
            new YamlScalarNode("kind"), new YamlScalarNode("NetworkPolicy"),
            new YamlScalarNode("metadata"), new YamlMappingNode(
                new YamlScalarNode("name"), new YamlScalarNode("seaweedfs-filer"),
                new YamlScalarNode("namespace"), new YamlScalarNode("storage")),
            new YamlScalarNode("spec"), new YamlMappingNode(
                new YamlScalarNode("podSelector"), new YamlMappingNode(),
                new YamlScalarNode("policyTypes"), new YamlSequenceNode(new YamlScalarNode("Ingress")),
                new YamlScalarNode("ingress"), At(Parse(values), "networkPolicy", "extraIngress")!));
        using StringWriter text = new();
        new YamlStream(new YamlDocument(policy)).Save(text, assignAnchors: false);
        return k8s.KubernetesYaml.Deserialize<k8s.Models.V1NetworkPolicy>(text.ToString().TrimEnd('.', '\n', '\r'));
    }

    /// <summary>
    /// The rule EntKube adds lives only in the copy handed to helm, so once Prometheus is gone the next
    /// apply renders without it and helm takes it away — nothing to undo here. A rule found in the STORED
    /// values was written by someone, and is kept: nothing tells it from the operator's own.
    /// </summary>
    [Fact]
    public async Task Once_prometheus_is_gone_entkubes_rule_goes_and_a_stored_one_stays()
    {
        InstallPrometheus();
        Guid seaweed = Install("seaweedfs", CatalogDefaults("seaweedfs"));
        string withPrometheus = await ApplyAsync(seaweed);
        Rules(withPrometheus).Should().Contain(ThePrometheusRule("monitoring"));
        (await StoredAsync(seaweed)).Should().Be(CatalogDefaults("seaweedfs"), "the rule was never stored");

        ClusterComponent prometheus = await Db.ClusterComponents.SingleAsync(c => c.HelmChartName == "kube-prometheus-stack");
        prometheus.Status = ComponentStatus.NotInstalled;
        await Db.SaveChangesAsync();

        (await ApplyAsync(seaweed)).Should().Be(CatalogDefaults("seaweedfs"));

        ClusterComponent row = await Db.ClusterComponents.SingleAsync(c => c.Id == seaweed);
        row.HelmValues = withPrometheus;
        await Db.SaveChangesAsync();

        (await ApplyAsync(seaweed)).Should().Be(withPrometheus);
    }

    /// <summary>
    /// The engine finds each server's port by its name, wherever its <c>metricsPort</c> put it; the rule has
    /// to open that same number, or a release with a port of its own stays blocked for good.
    /// </summary>
    [Fact]
    public async Task Each_servers_configured_metrics_port_is_opened()
    {
        InstallPrometheus();
        string stored = CatalogWithServerLines(
            ("master", "metricsPort: 9401"), ("volume", "metricsPort: \"9402\""), ("filer", "metricsPort: 9401"));

        string values = await ApplyAsync(Install("seaweedfs", stored));

        // The S3 gateway sets none, so it has the chart's 9327. Each number once, in order.
        Rules(values).Should().Equal(TheS3Rule, ThePrometheusRule("monitoring", 9327, 9401, 9402));
    }

    [Fact]
    public async Task Where_every_server_names_one_port_only_that_port_is_opened()
    {
        InstallPrometheus();
        string stored = CatalogWithServerLines(
            ("master", "metricsPort: 9500"), ("volume", "metricsPort: 9500"),
            ("filer", "metricsPort: 9500"), ("s3", "metricsPort: 9500"));

        Rules(await ApplyAsync(Install("seaweedfs", stored)))
            .Should().Equal(TheS3Rule, ThePrometheusRule("monitoring", 9500));
    }

    /// <summary>
    /// A port EntKube cannot read is not one it may guess: 9327 may be something else on that server. The
    /// others are still opened.
    /// </summary>
    [Theory]
    [InlineData("\"{{ .Values.port }}\"")]
    [InlineData("0")]
    [InlineData("70000")]
    [InlineData("{port: 9500}")]
    public async Task A_metrics_port_that_is_not_a_port_number_is_not_guessed(string unreadable)
    {
        InstallPrometheus();
        string stored = CatalogWithServerLines(
            ("master", $"metricsPort: {unreadable}"), ("volume", "metricsPort: 9500"),
            ("filer", "metricsPort: 9500"), ("s3", "metricsPort: 9500"));

        Rules(await ApplyAsync(Install("seaweedfs", stored)))
            .Should().Equal(TheS3Rule, ThePrometheusRule("monitoring", 9500));
    }

    /// <summary>
    /// ⚠️ With no port it can read, there is no rule at all. A rule with an empty ports list is not "no
    /// port": it admits Prometheus's pods to every port, the unauthenticated gRPC ones among them.
    /// </summary>
    [Fact]
    public async Task Where_no_server_has_a_port_that_can_be_read_nothing_is_opened()
    {
        InstallPrometheus();
        string stored = CatalogWithServerLines(
            ("master", "metricsPort: abc"), ("volume", "metricsPort: abc"),
            ("filer", "metricsPort: abc"), ("s3", "metricsPort: abc"));

        (await ApplyAsync(Install("seaweedfs", stored))).Should().Be(stored);
    }

    [Fact]
    public async Task A_server_switched_off_opens_no_port_of_its_own()
    {
        InstallPrometheus();
        string stored = CatalogWithServerLines(
            ("master", "metricsPort: 9500"), ("volume", "metricsPort: 9500"), ("filer", "metricsPort: 9500"))
            .Replace("\ns3:\n  enabled: true\n", "\ns3:\n  enabled: false\n  metricsPort: 9600\n");
        stored.Should().Contain("metricsPort: 9600");

        Rules(await ApplyAsync(Install("seaweedfs", stored)))
            .Should().Equal(TheS3Rule, ThePrometheusRule("monitoring", 9500));
    }

    [Fact]
    public async Task Another_charts_network_policy_is_not_seaweedfs()
    {
        InstallPrometheus();
        const string stored = "networkPolicy:\n  enabled: true\n  extraIngress: []\n";

        (await ApplyAsync(InstallChart("acme-store", "acme-store", "acme-store", "acme", stored))).Should().Be(stored);
    }
}
