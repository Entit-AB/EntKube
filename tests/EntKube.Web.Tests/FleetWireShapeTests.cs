using System.Text.Json;
using EntKube.Contracts.Catalog;
using EntKube.Contracts.Fleet;
using EntKube.Web.Services.PublicApi;
using FluentAssertions;
using Xunit;

namespace EntKube.Web.Tests;

/// <summary>
/// <c>GET /clusters</c> and <c>GET /clusters/{clusterId}/components</c> serialise exactly as the
/// anonymous objects they replaced.
///
/// <para>⚠️ <b>The oracle is the OLD code, cut out of <c>origin/main</c> rather than retyped.</b> A
/// record's mapping and its expected values written from one reading of the handler would agree on
/// a consistent misreading of it. The literals below came out of the file, so they are an
/// independent statement of the wire contract.</para>
///
/// <para>⚠️ <b>Two fields beside each other are different kinds of thing, and that is the trap.</b>
/// <c>type</c> is <c>ComponentType</c>, already a <c>string</c> on the contract and not a closed
/// set — the entity's own doc says it "will be refined later with a proper enum/value set" — while
/// <c>status</c> is an enum published by NAME in its own case. Typing the first as an enum, or lowercasing the second to
/// match <c>CostHistoryResponse.GroupBy</c> a few records up, each breaks a client silently.</para>
/// </summary>
public class FleetWireShapeTests
{
    private static readonly JsonSerializerOptions AsTheApiSerialises = new(JsonSerializerDefaults.Web);

    private static readonly Guid ClusterId = Guid.Parse("c0000000-0000-0000-0000-00000000000c");
    private static readonly Guid ComponentId = Guid.Parse("c0000000-0000-0000-0000-00000000000d");

    private static ClusterSummary Cluster() => new(
        Id: ClusterId,
        TenantId: Guid.Parse("70000000-0000-0000-0000-000000000007"),
        Name: "prod-north",
        ApiServerUrl: "https://prod-north.example.com:6443",
        EnvironmentId: Guid.Parse("e0000000-0000-0000-0000-00000000000e"),
        EnvironmentName: "Production",
        ProvisioningStatus: ClusterProvisioningStatus.Provisioned);

    /// <summary>
    /// Every nullable member is SET, because the point is a byte comparison and a null on both
    /// sides proves nothing about whether the right member was read.
    /// </summary>
    private static InstalledComponent Component() => new(
        Id: ComponentId,
        TenantId: Guid.Parse("70000000-0000-0000-0000-000000000007"),
        ClusterId: ClusterId,
        ClusterName: "prod-north",
        EnvironmentId: Guid.Parse("e0000000-0000-0000-0000-00000000000e"),
        Name: "kube-prometheus-stack",
        ComponentType: "HelmChart",
        Status: ComponentStatus.Installed,
        Namespace: "monitoring",
        ReleaseName: "kps",
        HelmChartName: "kube-prometheus-stack",
        HelmChartVersion: "62.3.0",
        HelmValues: "replicas: 2",
        Configuration: "{}",
        InstalledAt: new DateTime(2026, 10, 1, 9, 0, 0, DateTimeKind.Utc),
        LastError: "a previous upgrade timed out");

    [Fact]
    public void A_cluster_serialises_exactly_as_the_anonymous_object_it_replaced()
    {
        ClusterSummary c = Cluster();
        const string published = "Provisioned";
        const int componentCount = 7;

        // Copied verbatim from origin/main's handler — the independent oracle. The two
        // handler-supplied values stand in for PublishedProvisioningStatus(...) and the
        // per-cluster lookup, which are the handler's and not the summary's.
        object asItWas = new
        {
            id = c.Id,
            name = c.Name,
            apiServerUrl = c.ApiServerUrl,
            environment = c.EnvironmentName,
            provisioningStatus = published,
            componentCount,
        };

        JsonSerializer.Serialize(
            ClusterResponse.From(
                cluster: c, provisioningStatus: published, componentCount: componentCount),
            AsTheApiSerialises)
            .Should().Be(JsonSerializer.Serialize(asItWas, AsTheApiSerialises));
    }

    [Fact]
    public void A_component_serialises_exactly_as_the_anonymous_object_it_replaced()
    {
        InstalledComponent c = Component();

        // Copied verbatim from origin/main's handler.
        object asItWas = new
        {
            id = c.Id,
            name = c.Name,
            type = c.ComponentType,
            status = c.Status.ToString(),
            ns = c.Namespace,
            chart = c.HelmChartName,
            version = c.HelmChartVersion,
            installedAt = c.InstalledAt,
        };

        JsonSerializer.Serialize(ComponentResponse.From(c), AsTheApiSerialises)
            .Should().Be(JsonSerializer.Serialize(asItWas, AsTheApiSerialises));
    }

    /// <summary>
    /// Named as well as compared, so a failure says WHICH key moved instead of printing two long
    /// strings — and in key ORDER, because that is what a client reading the JSON sees.
    /// </summary>
    [Fact]
    public void The_keys_are_the_ones_clients_already_read()
    {
        System.Text.Json.Nodes.JsonObject cluster =
            JsonSerializer.SerializeToNode(
                ClusterResponse.From(Cluster(), "Provisioned", 7), AsTheApiSerialises)!.AsObject();

        cluster.Select(p => p.Key).Should().Equal(
            "id", "name", "apiServerUrl", "environment", "provisioningStatus", "componentCount");

        System.Text.Json.Nodes.JsonObject component =
            JsonSerializer.SerializeToNode(
                ComponentResponse.From(Component()), AsTheApiSerialises)!.AsObject();

        component.Select(p => p.Key).Should().Equal(
            "id", "name", "type", "status", "ns", "chart", "version", "installedAt");
    }

    /// <summary>
    /// ⚠️ The renames, asserted as VALUES. The key lists above would be satisfied by a mapping that
    /// put the right names on the wrong members, and three of these pairs are adjacent strings.
    /// </summary>
    [Fact]
    public void The_renamed_fields_carry_the_value_their_source_member_holds()
    {
        ClusterResponse cluster = ClusterResponse.From(Cluster(), "Provisioned", 7);

        cluster.Environment.Should().Be("Production", "`environment` is EnvironmentName");
        cluster.Name.Should().Be("prod-north", "and not the environment, which is the transposition");
        cluster.ApiServerUrl.Should().Be("https://prod-north.example.com:6443");

        ComponentResponse component = ComponentResponse.From(Component());

        component.Ns.Should().Be("monitoring", "`ns` is Namespace");
        component.Chart.Should().Be("kube-prometheus-stack", "`chart` is HelmChartName");
        component.Version.Should().Be("62.3.0", "`version` is HelmChartVersion, not the chart");
        component.Type.Should().Be(
            "HelmChart",
            "`type` is ComponentType — how the component is installed, not the catalog key that "
            + "says which component it is");
        component.Name.Should().Be(
            "kube-prometheus-stack",
            "and `name` is the catalog key, which happens to match the chart name here — so the "
            + "chart assertion above is the one that would catch a Name/Chart swap");
    }

    /// <summary>
    /// ⚠️ <c>status</c> is the enum's own name, in its own case. Not lowercased — unlike
    /// <c>CostHistoryResponse.GroupBy</c>, which IS lowercased a few records up in the same file,
    /// so a reader pattern-matching on that neighbour would break every client reading this.
    /// </summary>
    [Theory]
    [InlineData(ComponentStatus.NotInstalled, "NotInstalled")]
    [InlineData(ComponentStatus.Installing, "Installing")]
    [InlineData(ComponentStatus.Installed, "Installed")]
    [InlineData(ComponentStatus.Failed, "Failed")]
    [InlineData(ComponentStatus.Uninstalling, "Uninstalling")]
    public void A_component_status_is_published_by_name_in_its_own_case(
        ComponentStatus status, string expected)
    {
        ComponentResponse response = ComponentResponse.From(Component() with { Status = status });

        response.Status.Should().Be(expected);

        JsonSerializer.Serialize(response, AsTheApiSerialises)
            .Should().Contain($"\"status\":\"{expected}\"", "a number here would break every client");
    }

    /// <summary>
    /// ⚠️ A nullable member publishes <b>null</b>; it is not omitted and it is not "". A client
    /// reading an empty string would take it for a namespace called "".
    /// </summary>
    [Fact]
    public void A_component_that_was_never_installed_publishes_nulls()
    {
        ComponentResponse response = ComponentResponse.From(Component() with
        {
            Namespace = null,
            HelmChartName = null,
            HelmChartVersion = null,
            InstalledAt = null,
        });

        string json = JsonSerializer.Serialize(response, AsTheApiSerialises);

        json.Should().Contain("\"ns\":null");
        json.Should().Contain("\"chart\":null");
        json.Should().Contain("\"version\":null");
        json.Should().Contain("\"installedAt\":null");
    }
}
