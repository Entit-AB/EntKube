using EntKube.Web.Services;
using FluentAssertions;

namespace EntKube.Web.Tests;

/// <summary>
/// Tests for the YamlFormMerger utility that takes form field values
/// (key-value pairs with dot-notation paths) and merges them into a
/// YAML string. This is how form-based configuration gets applied on
/// top of the raw YAML that users can also edit directly.
/// </summary>
public class YamlFormMergerTests
{
    // ──────── Simple top-level paths ────────

    [Fact]
    public void MergeFormValues_WithSimplePath_SetsValue()
    {
        // A form field targeting "rootUser" should set the top-level key.

        string baseYaml = """
            rootUser: admin
            rootPassword: changeme
            """;

        Dictionary<string, string> formValues = new() { ["rootUser"] = "minio-admin" };

        string result = YamlFormMerger.MergeFormValues(baseYaml, formValues);

        result.Should().Contain("rootUser: minio-admin");
    }

    [Fact]
    public void MergeFormValues_WithNestedPath_SetsNestedValue()
    {
        // A dot-notation path like "grafana.adminPassword" should set the
        // nested YAML property grafana → adminPassword.

        string baseYaml = """
            grafana:
              enabled: true
              adminPassword: admin
            """;

        Dictionary<string, string> formValues = new() { ["grafana.adminPassword"] = "s3cret!" };

        string result = YamlFormMerger.MergeFormValues(baseYaml, formValues);

        result.Should().Contain("adminPassword: s3cret!");
    }

    [Fact]
    public void MergeFormValues_WithDeeplyNestedPath_SetsValue()
    {
        // Paths can be arbitrarily deep, like "prometheus.prometheusSpec.retention".

        string baseYaml = """
            prometheus:
              prometheusSpec:
                retention: 15d
                resources:
                  requests:
                    memory: 512Mi
            """;

        Dictionary<string, string> formValues = new() { ["prometheus.prometheusSpec.retention"] = "30d" };

        string result = YamlFormMerger.MergeFormValues(baseYaml, formValues);

        result.Should().Contain("retention: 30d");
        result.Should().NotContain("retention: 15d");
    }

    [Fact]
    public void MergeFormValues_WithEmptyBaseYaml_CreatesStructure()
    {
        // When the base YAML is empty, the merger should create the nested
        // structure from scratch based on the dot-notation path.

        string baseYaml = "";

        Dictionary<string, string> formValues = new() { ["service.type"] = "LoadBalancer" };

        string result = YamlFormMerger.MergeFormValues(baseYaml, formValues);

        result.Should().Contain("service:");
        result.Should().Contain("type: LoadBalancer");
    }

    [Fact]
    public void MergeFormValues_WithMultipleValues_SetsAll()
    {
        // Multiple form fields should all be applied.

        string baseYaml = """
            rootUser: admin
            rootPassword: changeme
            persistence:
              size: 50Gi
            """;

        Dictionary<string, string> formValues = new()
        {
            ["rootUser"] = "operator",
            ["rootPassword"] = "strong-pass",
            ["persistence.size"] = "100Gi"
        };

        string result = YamlFormMerger.MergeFormValues(baseYaml, formValues);

        result.Should().Contain("rootUser: operator");
        result.Should().Contain("rootPassword: strong-pass");
        result.Should().Contain("size: 100Gi");
    }

    [Fact]
    public void MergeFormValues_WithNewPath_AddsToExistingStructure()
    {
        // If the path doesn't exist in the base YAML but parent nodes do,
        // the merger should add the missing leaf without destroying siblings.

        string baseYaml = """
            grafana:
              enabled: true
            """;

        Dictionary<string, string> formValues = new() { ["grafana.adminPassword"] = "secret" };

        string result = YamlFormMerger.MergeFormValues(baseYaml, formValues);

        result.Should().Contain("enabled: true");
        result.Should().Contain("adminPassword: secret");
    }

    [Fact]
    public void MergeFormValues_WithBooleanValue_WritesUnquoted()
    {
        // Boolean-like values (true/false) should remain unquoted in YAML.

        string baseYaml = """
            alertmanager:
              enabled: true
            """;

        Dictionary<string, string> formValues = new() { ["alertmanager.enabled"] = "false" };

        string result = YamlFormMerger.MergeFormValues(baseYaml, formValues);

        result.Should().Contain("enabled: false");
        result.Should().NotContain("enabled: 'false'");
        result.Should().NotContain("enabled: \"false\"");
    }

    [Fact]
    public void MergeFormValues_WithEmptyFormValues_ReturnsBaseYamlUnchanged()
    {
        // If no form values are provided, the YAML comes back untouched.

        string baseYaml = """
            rootUser: admin
            rootPassword: changeme
            """;

        Dictionary<string, string> formValues = new();

        string result = YamlFormMerger.MergeFormValues(baseYaml, formValues);

        result.Should().Contain("rootUser: admin");
        result.Should().Contain("rootPassword: changeme");
    }

    // ──────── EnsureWireGuardGatewayPort ────────

    [Fact]
    public void EnsureWireGuardGatewayPort_WithNoPorts_SeedsStandardPortsPlusWireguard()
    {
        // The Istio gateway's stored values typically only set service.type.
        // Adding the UDP port must also re-list the standard ports, because
        // specifying service.ports overrides the chart defaults.

        string baseYaml = """
            service:
              type: LoadBalancer
            resources:
              requests:
                cpu: 100m
            """;

        string result = YamlFormMerger.EnsureWireGuardGatewayPort(baseYaml);

        // Ports come back as a proper YAML sequence with integer (unquoted) ports.
        result.Should().Contain("name: status-port");
        result.Should().Contain("name: http2");
        result.Should().Contain("name: https");
        result.Should().Contain("name: wireguard");
        result.Should().Contain("port: 51820");
        result.Should().Contain("protocol: UDP");
        result.Should().NotContain("port: \"51820\"");

        // It's a sequence (list item), not a map keyed "0".
        result.Should().Contain("- name: status-port");
        result.Should().NotContain("'0':");

        // Existing keys are preserved.
        result.Should().Contain("type: LoadBalancer");
        result.Should().Contain("cpu: 100m");

        // And the result round-trips as valid YAML.
        string? extracted = YamlFormMerger.ExtractValue(result, "service.type");
        extracted.Should().Be("LoadBalancer");
    }

    [Fact]
    public void EnsureWireGuardGatewayPort_WhenWireguardAlreadyPresent_IsNoOp()
    {
        // Re-applying must be idempotent — no duplicate wireguard entries.

        string baseYaml = """
            service:
              type: LoadBalancer
              ports:
                - name: http2
                  port: 80
                  protocol: TCP
                  targetPort: 80
                - name: wireguard
                  port: 51820
                  protocol: UDP
                  targetPort: 51820
            """;

        string result = YamlFormMerger.EnsureWireGuardGatewayPort(baseYaml);

        int wireguardCount = result.Split("name: wireguard").Length - 1;
        wireguardCount.Should().Be(1);
        // It should NOT inject the standard ports again on top of an existing list.
        result.Should().NotContain("name: status-port");
    }

    // ──────── Numeric sequence-index paths ────────

    [Fact]
    public void MergeFormValues_WithSequenceIndexPath_OverwritesExistingListElement()
    {
        // Regression for the Loki S3 storage bug: linking an S3 bucket must flip the
        // active schema period's object_store from "filesystem" to "s3". The path
        // reaches into an existing sequence element by numeric index
        // (loki.schemaConfig.configs.0.object_store). If this stops resolving, chunks
        // are written to the read-only filesystem store instead of S3 and the ingester
        // OOMKills in a loop.

        string baseYaml = """
            loki:
              storage:
                type: filesystem
              schemaConfig:
                configs:
                  - from: "2024-04-01"
                    store: tsdb
                    object_store: filesystem
                    schema: v13
                    index:
                      prefix: index_
                      period: 24h
            """;

        Dictionary<string, string> formValues = new()
        {
            ["loki.storage.type"] = "s3",
            ["loki.schemaConfig.configs.0.object_store"] = "s3"
        };

        string result = YamlFormMerger.MergeFormValues(baseYaml, formValues);

        // The schema period now points at S3, and the sibling keys survive.
        result.Should().Contain("object_store: s3");
        result.Should().NotContain("object_store: filesystem");
        result.Should().Contain("store: tsdb");
        result.Should().Contain("prefix: index_");
        // It must remain a list element, not become a mapping keyed "0".
        result.Should().NotContain("0:");
    }
}
