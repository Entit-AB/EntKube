using EntKube.Web.Data;
using EntKube.Web.Services;
using EntKube.Web.Services.Telemetry;
using FluentAssertions;
using Microsoft.Extensions.Configuration;

namespace EntKube.Web.Tests;

/// <summary>
/// Tests for the telemetry collector's ingest defaults. The failure these guard against is silent: a
/// collector installed with the catalog's REPLACE_WITH_* placeholders starts, reports Ready and passes its
/// health check while every export goes to a host that does not resolve — so the only visible symptom is an
/// empty Logs tab on a cluster where "everything is running".
/// </summary>
public class TelemetryIngestDefaultsTests
{
    private const string PublicUrl = "https://entkube.example.com";
    private const string ExpectedIngestUrl = "https://entkube.example.com/ingest/otlp";

    private static readonly Guid ClusterId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid TenantId = Guid.Parse("22222222-2222-2222-2222-222222222222");

    private static CatalogEntry Collector =>
        ComponentCatalog.GetByKey(TelemetryIngestDefaults.CollectorKey)!;

    private static (IngestTokenService Tokens, IConfiguration Config) Build(string? publicUrl = PublicUrl)
    {
        IConfiguration config = TestServices.TestConfiguration(publicUrl);
        return (new IngestTokenService(config), config);
    }

    // ──────── Pre-fill (registration time) ────────

    private const string InClusterUrl = "http://tel-entkube-telemetry-indexer.monitoring:8080/ingest/otlp";

    [Fact]
    public void ApplyTo_PrefersTheClustersOwnIndexerOverTheManagementPlane()
    {
        (IngestTokenService tokens, IConfiguration config) = Build();
        Dictionary<string, string> values = [];

        TelemetryIngestDefaults.ApplyTo(Collector, values, ClusterId, TenantId, tokens, config, InClusterUrl);

        // Keeping a cluster's logs inside it is the entire point of installing the indexer; defaulting the
        // collector back to the public URL would leave the components running and doing nothing.
        values[TelemetryIngestDefaults.EndpointFieldKey].Should().Be(InClusterUrl);
    }

    [Fact]
    public void ApplyTo_FallsBackToTheManagementPlaneWhenTheClusterHasNoIndexer()
    {
        (IngestTokenService tokens, IConfiguration config) = Build();
        Dictionary<string, string> values = [];

        TelemetryIngestDefaults.ApplyTo(Collector, values, ClusterId, TenantId, tokens, config, inClusterIngestUrl: null);

        values[TelemetryIngestDefaults.EndpointFieldKey].Should().Be(ExpectedIngestUrl);
    }

    [Fact]
    public void ApplyTo_StillDoesNotOverrideAnEndpointTheOperatorTyped()
    {
        (IngestTokenService tokens, IConfiguration config) = Build();
        Dictionary<string, string> values = new()
        {
            [TelemetryIngestDefaults.EndpointFieldKey] = "https://chosen.example.com/ingest/otlp",
        };

        TelemetryIngestDefaults.ApplyTo(Collector, values, ClusterId, TenantId, tokens, config, InClusterUrl);

        // An in-cluster indexer is a better default, not a licence to redirect a deliberate choice.
        values[TelemetryIngestDefaults.EndpointFieldKey].Should().Be("https://chosen.example.com/ingest/otlp");
    }

    [Fact]
    public void ApplyTo_UsesTheInClusterIndexerEvenWithNoPublicUrlConfigured()
    {
        // A deployment that never exposed EntKube publicly could not run native telemetry at all before;
        // with an in-cluster indexer it can, because the collector no longer has to reach the internet.
        (IngestTokenService tokens, IConfiguration config) = Build(publicUrl: null);
        Dictionary<string, string> values = [];

        TelemetryIngestDefaults.ApplyTo(Collector, values, ClusterId, TenantId, tokens, config, InClusterUrl);

        values[TelemetryIngestDefaults.EndpointFieldKey].Should().Be(InClusterUrl);
    }

    [Fact]
    public void ApplyTo_FillsBlankIngestUrlAndToken()
    {
        (IngestTokenService tokens, IConfiguration config) = Build();
        Dictionary<string, string> values = [];

        TelemetryIngestDefaults.ApplyTo(Collector, values, ClusterId, TenantId, tokens, config);

        values[TelemetryIngestDefaults.EndpointFieldKey].Should().Be(ExpectedIngestUrl);
        values[TelemetryIngestDefaults.TokenFieldKey].Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void ApplyTo_MintsATokenThatTheIngestEndpointAccepts()
    {
        (IngestTokenService tokens, IConfiguration config) = Build();
        Dictionary<string, string> values = [];

        TelemetryIngestDefaults.ApplyTo(Collector, values, ClusterId, TenantId, tokens, config);

        tokens.TryValidate(values[TelemetryIngestDefaults.TokenFieldKey], out Guid tenant, out Guid cluster)
            .Should().BeTrue();
        tenant.Should().Be(TenantId);
        cluster.Should().Be(ClusterId);
    }

    [Fact]
    public void ApplyTo_LeavesOperatorSuppliedValuesAlone()
    {
        (IngestTokenService tokens, IConfiguration config) = Build();
        Dictionary<string, string> values = new()
        {
            [TelemetryIngestDefaults.EndpointFieldKey] = "https://internal.example.net/ingest/otlp",
            [TelemetryIngestDefaults.TokenFieldKey] = "operator-supplied-token",
        };

        TelemetryIngestDefaults.ApplyTo(Collector, values, ClusterId, TenantId, tokens, config);

        values[TelemetryIngestDefaults.EndpointFieldKey].Should().Be("https://internal.example.net/ingest/otlp");
        values[TelemetryIngestDefaults.TokenFieldKey].Should().Be("operator-supplied-token");
    }

    [Fact]
    public void ApplyTo_ReplacesAValueThatIsItselfAPlaceholder()
    {
        (IngestTokenService tokens, IConfiguration config) = Build();
        Dictionary<string, string> values = new()
        {
            [TelemetryIngestDefaults.EndpointFieldKey] = TelemetryIngestDefaults.EndpointPlaceholder,
            [TelemetryIngestDefaults.TokenFieldKey] = TelemetryIngestDefaults.TokenPlaceholder,
        };

        TelemetryIngestDefaults.ApplyTo(Collector, values, ClusterId, TenantId, tokens, config);

        values[TelemetryIngestDefaults.EndpointFieldKey].Should().Be(ExpectedIngestUrl);
        values[TelemetryIngestDefaults.TokenFieldKey].Should().NotContain("REPLACE_WITH");
    }

    [Fact]
    public void ApplyTo_IgnoresEveryOtherCatalogEntry()
    {
        (IngestTokenService tokens, IConfiguration config) = Build();
        CatalogEntry other = ComponentCatalog.GetByKey("otel-ebpf")!;
        Dictionary<string, string> values = [];

        TelemetryIngestDefaults.ApplyTo(other, values, ClusterId, TenantId, tokens, config);

        values.Should().BeEmpty();
    }

    [Fact]
    public void ApplyTo_StillMintsTheTokenWhenTheIngestUrlIsUnknown()
    {
        // A missing PublicIngestUrl is an operator-fixable gap; it must not cost the token as well.
        (IngestTokenService tokens, IConfiguration config) = Build(publicUrl: null);
        Dictionary<string, string> values = [];

        TelemetryIngestDefaults.ApplyTo(Collector, values, ClusterId, TenantId, tokens, config);

        values.Should().NotContainKey(TelemetryIngestDefaults.EndpointFieldKey);
        values[TelemetryIngestDefaults.TokenFieldKey].Should().NotBeNullOrWhiteSpace();
    }

    // ──────── Merge into the values document ────────

    [Fact]
    public void PreFilledValues_LandOnTheCollectorsExporterEndpointPath()
    {
        // The endpoint's YAML path contains a '/' inside one segment (exporters."otlphttp/entkube"), so this
        // guards the dotted-path merge as much as the pre-fill.
        (IngestTokenService tokens, IConfiguration config) = Build();
        Dictionary<string, string> values = [];
        TelemetryIngestDefaults.ApplyTo(Collector, values, ClusterId, TenantId, tokens, config);

        string merged = CatalogComponentRegistrar.MergeFormValues(Collector, values, []);

        merged.Should().NotContain(TelemetryIngestDefaults.HostMarker);
        YamlFormMerger.ExtractValue(merged, "config.exporters.otlphttp/entkube.endpoint")
            .Should().Be(ExpectedIngestUrl);
    }

    // ──────── Heal (install time) ────────

    [Fact]
    public void FillPlaceholders_ReplacesBothPlaceholdersInTheCatalogDefaults()
    {
        (IngestTokenService tokens, IConfiguration config) = Build();

        (string? yaml, string? minted) = TelemetryIngestDefaults.FillPlaceholders(
            Collector.DefaultValues, ClusterId, TenantId, tokens, config);

        yaml.Should().NotBeNull();
        yaml!.Should().NotContain("REPLACE_WITH");
        yaml.Should().Contain(ExpectedIngestUrl);
        minted.Should().NotBeNull();
        tokens.TryValidate(minted, out Guid tenant, out Guid cluster).Should().BeTrue();
        tenant.Should().Be(TenantId);
        cluster.Should().Be(ClusterId);
    }

    [Fact]
    public void FillPlaceholders_LeavesAConfiguredDocumentUntouched()
    {
        (IngestTokenService tokens, IConfiguration config) = Build();
        const string configured = """
            config:
              exporters:
                otlphttp/entkube:
                  endpoint: https://already.example.net/ingest/otlp
              extensions:
                bearertokenauth:
                  token: "ek1.already.set"
            """;

        (string? yaml, string? minted) = TelemetryIngestDefaults.FillPlaceholders(
            configured, ClusterId, TenantId, tokens, config);

        yaml.Should().Be(configured);
        minted.Should().BeNull();
    }

    [Fact]
    public void FillPlaceholders_SubstitutesABareHostMarker()
    {
        (IngestTokenService tokens, IConfiguration config) = Build();
        const string handEdited = """
            config:
              exporters:
                otlphttp/entkube:
                  endpoint: https://REPLACE_WITH_ENTKUBE_URL/custom/path
            """;

        (string? yaml, _) = TelemetryIngestDefaults.FillPlaceholders(
            handEdited, ClusterId, TenantId, tokens, config);

        yaml!.Should().Contain("https://entkube.example.com/custom/path");
        yaml.Should().NotContain("REPLACE_WITH");
    }

    [Fact]
    public void FillPlaceholders_ThrowsWhenTheIngestUrlIsNotConfigured()
    {
        (IngestTokenService tokens, IConfiguration config) = Build(publicUrl: null);

        Action act = () => TelemetryIngestDefaults.FillPlaceholders(
            Collector.DefaultValues, ClusterId, TenantId, tokens, config);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*Telemetry:PublicIngestUrl*");
    }

    [Fact]
    public void TokenSecretName_MatchesTheCatalogFieldsVaultName()
    {
        TelemetryIngestDefaults.TokenSecretName(Collector).Should().Be("otel-ingest-token");
    }

    // ──────── Repointing an installed collector at an in-cluster indexer ────────

    private const string CollectorValues = """
        config:
          exporters:
            otlphttp/entkube:
              endpoint: PLACEHOLDER
        """;

    private static string ValuesWith(string endpoint) => CollectorValues.Replace("PLACEHOLDER", endpoint);

    [Fact]
    public void An_installed_collector_is_repointed_from_the_management_plane_to_the_cluster()
    {
        (_, IConfiguration config) = Build();

        // The case that actually happens: the indexer DEPENDS on the collector, so the collector is always
        // installed first, pointed at the management plane. Installing the indexer and re-applying the
        // collector has to move it, or the indexer sits there receiving nothing.
        (string? yaml, bool repointed) = TelemetryIngestDefaults.RepointToInCluster(
            ValuesWith(ExpectedIngestUrl), InClusterUrl, config);

        repointed.Should().BeTrue();
        YamlFormMerger.ExtractValue(yaml!, TelemetryIngestDefaults.EndpointYamlPath).Should().Be(InClusterUrl);
    }

    [Fact]
    public void A_destination_the_operator_chose_is_left_alone()
    {
        (_, IConfiguration config) = Build();
        const string chosen = "https://collector-gateway.example.com/ingest/otlp";

        (string? yaml, bool repointed) = TelemetryIngestDefaults.RepointToInCluster(
            ValuesWith(chosen), InClusterUrl, config);

        // Only a URL EntKube generated is ours to change. Redirecting a deliberate choice would silently
        // send someone's telemetry somewhere they did not pick.
        repointed.Should().BeFalse();
        YamlFormMerger.ExtractValue(yaml!, TelemetryIngestDefaults.EndpointYamlPath).Should().Be(chosen);
    }

    [Fact]
    public void A_placeholder_endpoint_is_repointed_too()
    {
        (_, IConfiguration config) = Build();

        (string? yaml, bool repointed) = TelemetryIngestDefaults.RepointToInCluster(
            ValuesWith("https://REPLACE_WITH_ENTKUBE_URL/ingest/otlp"), InClusterUrl, config);

        repointed.Should().BeTrue();
        YamlFormMerger.ExtractValue(yaml!, TelemetryIngestDefaults.EndpointYamlPath).Should().Be(InClusterUrl);
    }

    [Fact]
    public void Nothing_happens_when_the_cluster_has_no_indexer()
    {
        (_, IConfiguration config) = Build();

        (string? yaml, bool repointed) = TelemetryIngestDefaults.RepointToInCluster(
            ValuesWith(ExpectedIngestUrl), inClusterIngestUrl: null, config);

        repointed.Should().BeFalse();
        YamlFormMerger.ExtractValue(yaml!, TelemetryIngestDefaults.EndpointYamlPath).Should().Be(ExpectedIngestUrl);
    }

    [Fact]
    public void Repointing_an_already_repointed_collector_changes_nothing()
    {
        (_, IConfiguration config) = Build();

        // Runs on every install, so it has to be idempotent — otherwise every apply reports a change.
        (_, bool repointed) = TelemetryIngestDefaults.RepointToInCluster(
            ValuesWith(InClusterUrl), InClusterUrl, config);

        repointed.Should().BeFalse();
    }
}
