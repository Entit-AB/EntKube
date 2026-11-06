using System.Text.Json;
using EntKube.Contracts.Delivery;
using EntKube.Web.Services.PublicApi;
using FluentAssertions;
using Xunit;

namespace EntKube.Web.Tests;

/// <summary>
/// <c>GET /apps</c> and <c>GET /deployments</c> serialise exactly as the anonymous objects they
/// replaced.
///
/// <para>⚠️ <b>The oracle is the OLD code, cut out of <c>origin/main</c> rather than retyped.</b>
/// A record's mapping and its expected values written from one reading of the handler would agree
/// on a consistent misreading of it. The literals below came out of the file, so they are an
/// independent statement of the wire contract.</para>
///
/// <para>⚠️ <b>The enum assertions are literal JSON text, and that is the whole point of them.</b>
/// <c>ModuleApiTests</c> holds the three contract enums to Delivery's own by comparing the two
/// <c>Enum.GetNames</c> lists — which means a rename applied to <b>both</b> declarations keeps
/// that test green while <c>"GitHelm"</c> becomes something else on every client's wire. Writing
/// the expectation as <c>DeploymentType.GitHelm.ToString()</c> here would inherit the same
/// blindness: it would be the production expression compared against itself. So the expected
/// values are hard-coded strings, and a renamed member fails this file.</para>
/// </summary>
public class AppsAndDeploymentsWireShapeTests
{
    private static readonly JsonSerializerOptions AsTheApiSerialises = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// ⚠️ <c>Name</c> and <c>Customer</c> hold deliberately different strings, and the three
    /// members this route has never published (<c>TenantId</c>, <c>CustomerId</c>,
    /// <c>Namespace</c>) hold recognisable values so that one leaking onto the wire is visible in
    /// the failure rather than merely changing a count.
    /// </summary>
    private static AppSummary App() => new(
        Id: Guid.Parse("a0000000-0000-0000-0000-00000000000a"),
        TenantId: Guid.Parse("70000000-0000-0000-0000-000000000007"),
        CustomerId: Guid.Parse("c0000000-0000-0000-0000-00000000000c"),
        CustomerName: "Acme AB",
        Name: "invoicing",
        Namespace: "must-not-ship");

    /// <summary>
    /// ⚠️ Every nullable member is SET and all eight adjacent strings differ, because the point is
    /// a byte comparison: a null on both sides, or two fields holding one value, proves nothing
    /// about whether the right member was read. The three enum members are all non-zero ones —
    /// <c>Manual</c>, <c>Unknown</c> and <c>Unknown</c> are 0, so a fixture on the defaults would
    /// pass even if the factory read the wrong enum.
    ///
    /// <para><c>LastSyncedAt</c> is pinned to <c>DateTimeKind.Utc</c> and the SAME local is used
    /// in the oracle. An <c>Unspecified</c> Kind serialises without the trailing <c>Z</c>, and the
    /// comparison would then be measuring the fixture's clock handling rather than the
    /// mapping.</para>
    /// </summary>
    private static DeploymentSummary Deployment() => new(
        Id: Guid.Parse("d0000000-0000-0000-0000-00000000000d"),
        TenantId: Guid.Parse("70000000-0000-0000-0000-000000000007"),
        AppId: Guid.Parse("a0000000-0000-0000-0000-00000000000a"),
        AppName: "checkout",
        EnvironmentId: Guid.Parse("e0000000-0000-0000-0000-00000000000e"),
        EnvironmentName: "Production",
        ClusterId: Guid.Parse("c1000000-0000-0000-0000-00000000000c"),
        ClusterName: "prod-north",
        Name: "checkout-api",
        Type: DeploymentType.GitHelm,
        Namespace: "checkout-prod",
        SyncStatus: SyncStatus.OutOfSync,
        HealthStatus: HealthStatus.Degraded,
        StatusMessage: "must-not-ship",
        LastSyncedAt: new DateTime(2026, 11, 1, 6, 30, 0, DateTimeKind.Utc),
        IsManaged: true);

    [Fact]
    public void An_app_serialises_exactly_as_the_anonymous_object_it_replaced()
    {
        AppSummary a = App();
        const int deploymentCount = 7;

        // Copied verbatim from origin/main's handler — the independent oracle. The count stands in
        // for deploymentCounts.GetValueOrDefault(a.Id), which is the handler's and not the
        // summary's.
        object asItWas = new
        {
            id = a.Id,
            name = a.Name,
            customer = a.CustomerName,
            deploymentCount,
        };

        JsonSerializer.Serialize(AppResponse.From(a, deploymentCount), AsTheApiSerialises)
            .Should().Be(JsonSerializer.Serialize(asItWas, AsTheApiSerialises));
    }

    [Fact]
    public void A_deployment_serialises_exactly_as_the_anonymous_object_it_replaced()
    {
        DeploymentSummary d = Deployment();

        // Copied verbatim from origin/main's handler.
        object asItWas = new
        {
            id = d.Id,
            name = d.Name,
            app = d.AppName,
            type = d.Type.ToString(),
            environment = d.EnvironmentName,
            cluster = d.ClusterName,
            ns = d.Namespace,
            syncStatus = d.SyncStatus.ToString(),
            healthStatus = d.HealthStatus.ToString(),
            lastSyncedAt = d.LastSyncedAt,
            isManaged = d.IsManaged,
        };

        JsonSerializer.Serialize(DeploymentResponse.From(d), AsTheApiSerialises)
            .Should().Be(JsonSerializer.Serialize(asItWas, AsTheApiSerialises));
    }

    /// <summary>
    /// Named as well as compared, so a failure says WHICH key moved instead of printing two long
    /// strings — and in key ORDER, because that is what a client reading the JSON sees. The counts
    /// are what hold the unpublished source members off the wire: four and eleven, no more.
    /// </summary>
    [Fact]
    public void The_keys_are_the_ones_clients_already_read()
    {
        System.Text.Json.Nodes.JsonObject app =
            JsonSerializer.SerializeToNode(
                AppResponse.From(App(), 7), AsTheApiSerialises)!.AsObject();

        app.Select(p => p.Key).Should().Equal("id", "name", "customer", "deploymentCount");

        System.Text.Json.Nodes.JsonObject deployment =
            JsonSerializer.SerializeToNode(
                DeploymentResponse.From(Deployment()), AsTheApiSerialises)!.AsObject();

        deployment.Select(p => p.Key).Should().Equal(
            "id", "name", "app", "type", "environment", "cluster", "ns",
            "syncStatus", "healthStatus", "lastSyncedAt", "isManaged");
    }

    /// <summary>
    /// ⚠️ The renames, asserted as VALUES. The key lists above would be satisfied by a mapping
    /// that put the right names on the wrong members, and on <c>DeploymentResponse</c> eight
    /// adjacent parameters are strings.
    /// </summary>
    [Fact]
    public void The_renamed_fields_carry_the_value_their_source_member_holds()
    {
        AppResponse app = AppResponse.From(App(), 7);

        app.Customer.Should().Be("Acme AB", "`customer` is CustomerName");
        app.Name.Should().Be("invoicing", "and not the customer, which is the transposition");
        app.DeploymentCount.Should().Be(
            7, "the count is the handler's argument and nothing on AppSummary can contradict it");

        DeploymentResponse d = DeploymentResponse.From(Deployment());

        d.Name.Should().Be("checkout-api", "`name` is the deployment's own name");
        d.App.Should().Be("checkout", "`app` is AppName, and the two are the likeliest swap");
        d.Environment.Should().Be("Production", "`environment` is EnvironmentName");
        d.Cluster.Should().Be("prod-north", "`cluster` is ClusterName, not the environment");
        d.Ns.Should().Be("checkout-prod", "`ns` is Namespace");
        d.SyncStatus.Should().Be("OutOfSync", "`syncStatus` is SyncStatus, not the health");
        d.HealthStatus.Should().Be("Degraded", "`healthStatus` is HealthStatus, not the sync");
        d.IsManaged.Should().BeTrue();
    }

    /// <summary>
    /// ⚠️ The source members this route has never published stay unpublished. Asserted against
    /// the fixture's recognisable values rather than only by the key count, so the failure names
    /// what leaked.
    /// </summary>
    [Fact]
    public void The_members_this_route_has_never_published_are_still_absent()
    {
        string app = JsonSerializer.Serialize(AppResponse.From(App(), 7), AsTheApiSerialises);
        string deployment =
            JsonSerializer.Serialize(DeploymentResponse.From(Deployment()), AsTheApiSerialises);

        app.Should().NotContain(
            "must-not-ship", "AppSummary.Namespace has never been on GET /apps");
        app.Should().NotContain(
            "70000000", "nor TenantId — the token fixes the tenant");
        app.Should().NotContain("c0000000", "nor CustomerId; only the customer's name is published");

        deployment.Should().NotContain(
            "must-not-ship", "DeploymentSummary.StatusMessage has never been on GET /deployments");
        deployment.Should().NotContain("70000000", "nor TenantId");
        deployment.Should().NotContain(
            "a0000000",
            "nor AppId — it is a query PARAMETER of this route, not a field of it, so adding it "
            + "would be a new wire field rather than a transcription");
        deployment.Should().NotContain("e0000000", "nor EnvironmentId");
        deployment.Should().NotContain("c1000000", "nor ClusterId");
    }

    /// <summary>
    /// ⚠️ <b>Hard-coded expectations, deliberately.</b> See the class summary: comparing against
    /// <c>status.ToString()</c> would be the production expression compared against itself, and
    /// <c>ModuleApiTests</c> cannot see a rename applied to both enum declarations. A number here
    /// would break every client, and so would a camelCased name.
    /// </summary>
    [Theory]
    [InlineData(DeploymentType.Manual, "Manual")]
    [InlineData(DeploymentType.Yaml, "Yaml")]
    [InlineData(DeploymentType.HelmChart, "HelmChart")]
    [InlineData(DeploymentType.GitYaml, "GitYaml")]
    [InlineData(DeploymentType.GitHelm, "GitHelm")]
    [InlineData(DeploymentType.GitAppOfApps, "GitAppOfApps")]
    public void A_deployment_type_is_published_by_name_in_its_own_case(
        DeploymentType type, string expected)
    {
        DeploymentResponse response = DeploymentResponse.From(Deployment() with { Type = type });

        response.Type.Should().Be(expected);

        JsonSerializer.Serialize(response, AsTheApiSerialises)
            .Should().Contain($"\"type\":\"{expected}\"", "a number here would break every client");
    }

    [Theory]
    [InlineData(SyncStatus.Unknown, "Unknown")]
    [InlineData(SyncStatus.Synced, "Synced")]
    [InlineData(SyncStatus.OutOfSync, "OutOfSync")]
    [InlineData(SyncStatus.Syncing, "Syncing")]
    [InlineData(SyncStatus.Failed, "Failed")]
    public void A_sync_status_is_published_by_name_in_its_own_case(
        SyncStatus status, string expected)
    {
        DeploymentResponse response =
            DeploymentResponse.From(Deployment() with { SyncStatus = status });

        response.SyncStatus.Should().Be(expected);

        JsonSerializer.Serialize(response, AsTheApiSerialises)
            .Should().Contain($"\"syncStatus\":\"{expected}\"");
    }

    [Theory]
    [InlineData(HealthStatus.Unknown, "Unknown")]
    [InlineData(HealthStatus.Healthy, "Healthy")]
    [InlineData(HealthStatus.Progressing, "Progressing")]
    [InlineData(HealthStatus.Degraded, "Degraded")]
    [InlineData(HealthStatus.Missing, "Missing")]
    [InlineData(HealthStatus.Suspended, "Suspended")]
    public void A_health_status_is_published_by_name_in_its_own_case(
        HealthStatus status, string expected)
    {
        DeploymentResponse response =
            DeploymentResponse.From(Deployment() with { HealthStatus = status });

        response.HealthStatus.Should().Be(expected);

        JsonSerializer.Serialize(response, AsTheApiSerialises)
            .Should().Contain($"\"healthStatus\":\"{expected}\"");
    }

    /// <summary>
    /// ⚠️ A nullable member publishes <b>null</b>; it is not omitted and it is not "". A client
    /// reading an empty string would take it for a namespace called "", and one reading an absent
    /// key cannot tell "no namespace" from "this version of the API does not send one".
    ///
    /// <para>⚠️ <b><c>ns</c> is not reachable as null through this route today</b>, and saying so
    /// is the honest version: <c>DeliveryApi</c> projects it through a <c>Row</c> whose
    /// <c>Namespace</c> is a non-nullable <c>string</c>, off a column declared
    /// <c>nullable: false</c>. The contract declares <c>string?</c>, so the record must carry a
    /// null correctly, and this fact is what holds it to that — but it is a statement about the
    /// CONTRACT, not an observation of a response. <c>lastSyncedAt</c> is different: a deployment
    /// that has never synced really does have none.</para>
    /// </summary>
    [Fact]
    public void A_deployment_with_no_namespace_and_no_sync_publishes_nulls()
    {
        DeploymentResponse response = DeploymentResponse.From(Deployment() with
        {
            Namespace = null,
            LastSyncedAt = null,
        });

        string json = JsonSerializer.Serialize(response, AsTheApiSerialises);

        json.Should().Contain("\"ns\":null");
        json.Should().Contain("\"lastSyncedAt\":null");
    }
}
