using System.Text.Json;
using System.Text.RegularExpressions;
using EntKube.Cli;
using EntKube.Web.Services.Cost;
using EntKube.Web.Services.PublicApi;
using FluentAssertions;
using Xunit;

namespace EntKube.Web.Tests;

/// <summary>
/// Every column of a table-rendering command produces a VALUE when the API answers.
///
/// <para><b>Why this exists beside <see cref="CliColumnParityTests"/>.</b> That ratchet asks the
/// generated document whether each column names a field the API sends, which is the right first
/// question and cannot answer three others:</para>
///
/// <list type="bullet">
/// <item>a column naming a field that exists but is an <b>array or object</b> — the schema check
/// passes, and <c>TableWriter.Render</c> gives <c>"-"</c> for an empty array, <c>"[7]"</c> for a
/// long one, and 60 characters of raw JSON for an object;</item>
/// <item>a <c>RowsProperty</c> that resolves to something which is not a list — the schema check
/// now rejects that, but only this one shows what the operator would have seen;</item>
/// <item>a column list on a command whose body is a single object, which <c>TableWriter</c>
/// never consults at all.</item>
/// </list>
///
/// <para>⚠️ <b>The response is built the way the handler builds it.</b> A domain object goes
/// through the same response record and is serialised with
/// <c>JsonSerializerDefaults.Web</c> — the same options minimal APIs use — so the JSON these
/// assertions read is byte-for-byte what the route puts on the wire. Writing the JSON by hand
/// would make this a test of my typing.</para>
///
/// <para>⚠️ <b>What it still cannot prove.</b> The fixtures below populate every field, so a
/// field that is always null <em>in production</em> would render here and dash in the field. That
/// gap is closed only by a call against real data, and it is stated rather than papered over.</para>
/// </summary>
public class CliRenderedColumnTests
{
    private static readonly JsonSerializerOptions AsTheApiSerialises = new(JsonSerializerDefaults.Web);

    /// <summary>An array rendered as a count, e.g. "[7]" — a cell carrying no information.</summary>
    private static readonly Regex ArrayCount = new(@"^\[\d+\]$", RegexOptions.Compiled);

    private static CliCommand Command(params string[] path) =>
        CliCommands.All.Single(c => c.Path.SequenceEqual(path));

    private static void EveryColumnRenders(CliCommand command, string json)
    {
        using JsonDocument doc = JsonDocument.Parse(json);

        JsonElement rows = doc.RootElement;
        if (command.RowsProperty is not null)
        {
            doc.RootElement.TryGetProperty(command.RowsProperty, out rows).Should().BeTrue(
                $"`{string.Join(' ', command.Path)}` reads its rows from '{command.RowsProperty}'");
        }

        rows.ValueKind.Should().Be(
            JsonValueKind.Array,
            "TableWriter prints anything else as key/value pairs and ignores the columns entirely");

        List<JsonElement> items = [.. rows.EnumerateArray()];
        items.Should().NotBeEmpty("otherwise every assertion below passes over nothing");

        foreach (JsonElement item in items)
        {
            foreach ((string header, string path) in command.Columns)
            {
                string cell = TableWriter.Cell(item, path);
                string where = $"`{string.Join(' ', command.Path)}` column {header} ('{path}')";

                cell.Should().NotBe(
                    "-", $"{where} rendered nothing — a dash is what an absent or null field gives");

                cell.Should().NotStartWith(
                    "{", $"{where} rendered raw JSON, so it names an object rather than a value");

                ArrayCount.IsMatch(cell).Should().BeFalse(
                    $"{where} rendered an array as a count, which tells the reader nothing");
            }
        }
    }

    [Fact]
    public void The_cost_run_rate_renders_every_column()
    {
        CostReport report = new()
        {
            GeneratedAt = new DateTime(2026, 11, 5, 9, 0, 0, DateTimeKind.Utc),
            Currency = "SEK",
            Namespaces =
            [
                new NamespaceCost
                {
                    Namespace = "billing-prod",
                    ClusterId = Guid.NewGuid(),
                    ClusterName = "prod-north",
                    CustomerId = Guid.NewGuid(),
                    CustomerName = "Acme AB",
                    EnvironmentName = "Production",
                    Apps = [new AppRef(Guid.NewGuid(), "Billing")],
                    CpuCores = 4d,
                    MemoryGiB = 8d,
                    StorageGiB = 16d,
                    CpuMonthlyCost = 100m,
                    MemoryMonthlyCost = 50m,
                    SharedMonthlyCost = 25m,
                    ShareOfCluster = 0.4d,
                },
            ],
        };

        EveryColumnRenders(
            Command("cost"),
            JsonSerializer.Serialize(CostRunRateResponse.From(report), AsTheApiSerialises));
    }

    [Fact]
    public void The_cost_history_renders_every_column()
    {
        CostHistoryReport report = new()
        {
            From = new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc),
            To = new DateTime(2026, 10, 31, 0, 0, 0, DateTimeKind.Utc),
            GroupBy = CostGroupBy.Customer,
            Currency = "SEK",
            TotalCost = 1000m,
            PreviousTotalCost = 800m,
            CoveredHours = 700m,
            ExpectedHours = 720m,
            Groups =
            [
                new CostHistoryGroup
                {
                    Id = Guid.NewGuid(),
                    Label = "Acme AB",
                    Cost = 300m,
                    PreviousCost = 250m,
                    CpuCoreHours = 1.5d,
                    MemoryGiBHours = 2.5d,
                    StorageGiBHours = 3.5d,
                },
            ],
        };

        EveryColumnRenders(
            Command("cost", "history"),
            JsonSerializer.Serialize(CostHistoryResponse.FromReport(report), AsTheApiSerialises));
    }

    [Fact]
    public void The_cost_months_list_renders_every_column()
    {
        CostMonth month = new()
        {
            Year = 2026,
            Month = 10,
            Cost = 1234.56m,
            CoveredHours = 744m,
            ExpectedHours = 744m,
            Currency = "SEK",
        };

        EveryColumnRenders(
            Command("cost", "months"),
            JsonSerializer.Serialize(new[] { CostMonthResponse.From(month) }, AsTheApiSerialises));
    }

    /// <summary>
    /// ⚠️ This is the command whose four dead columns started the whole thread —
    /// <c>clusterName</c>, <c>cpuCorePerMonth</c>, <c>memoryGiBPerMonth</c>,
    /// <c>storageGiBPerMonth</c>, none of which the endpoint has ever sent. With the columns
    /// corrected, this renders what an operator actually sees.
    /// </summary>
    [Fact]
    public void The_cost_rates_list_renders_every_column()
    {
        EntKube.Contracts.Cost.ClusterRate rate = new(
            ClusterId: Guid.NewGuid(),
            CpuCoreHourCost: 0.031m,
            MemoryGiBHourCost: 0.004m,
            StorageGiBMonthCost: 0.12m,
            LoadBalancerMonthlyCost: 18m,
            PublicIpMonthlyCost: 3.5m,
            ClusterMonthlyOverhead: 240m,
            Currency: "SEK",
            ChargeOnRequests: true,
            ChargeIdleCapacity: true,
            UpdatedAt: new DateTime(2026, 11, 1, 0, 0, 0, DateTimeKind.Utc),
            UpdatedBy: "nils");

        EveryColumnRenders(
            Command("cost", "rates", "list"),
            JsonSerializer.Serialize(new[] { ClusterRateResponse.From(rate) }, AsTheApiSerialises));
    }

    [Fact]
    public void The_clusters_list_renders_every_column()
    {
        EntKube.Contracts.Fleet.ClusterSummary cluster = new(
            Id: Guid.NewGuid(),
            TenantId: Guid.NewGuid(),
            Name: "prod-north",
            ApiServerUrl: "https://prod-north.example.com:6443",
            EnvironmentId: Guid.NewGuid(),
            EnvironmentName: "Production",
            ProvisioningStatus: EntKube.Contracts.Fleet.ClusterProvisioningStatus.Provisioned);

        EveryColumnRenders(
            Command("clusters", "list"),
            JsonSerializer.Serialize(
                new[]
                {
                    ClusterResponse.From(
                        cluster: cluster,
                        provisioningStatus: "Provisioned",
                        componentCount: 7),
                },
                AsTheApiSerialises));
    }

    /// <summary>
    /// ⚠️ The fixture's <c>Type</c> is a real <c>ComponentType</c> — how a component is installed
    /// — rather than a catalog key. A fixture whose Type looked like a component NAME
    /// would make a Name/Type transposition read as plausible data rather than as an obvious
    /// defect, and would teach the next reader the wrong meaning of the field.
    /// </summary>
    [Fact]
    public void The_components_list_renders_every_column()
    {
        EntKube.Contracts.Catalog.InstalledComponent component = new(
            Id: Guid.NewGuid(),
            TenantId: Guid.NewGuid(),
            ClusterId: Guid.NewGuid(),
            ClusterName: "prod-north",
            EnvironmentId: Guid.NewGuid(),
            Name: "kube-prometheus-stack",
            ComponentType: "HelmChart",
            Status: EntKube.Contracts.Catalog.ComponentStatus.Installed,
            Namespace: "monitoring",
            ReleaseName: "kps",
            HelmChartName: "kube-prometheus-stack",
            HelmChartVersion: "62.3.0",
            HelmValues: null,
            Configuration: null,
            InstalledAt: new DateTime(2026, 10, 1, 9, 0, 0, DateTimeKind.Utc),
            LastError: null);

        EveryColumnRenders(
            Command("components", "list"),
            JsonSerializer.Serialize(
                new[] { ComponentResponse.From(component) }, AsTheApiSerialises));
    }

    /// <summary>
    /// ⚠️ The fixture's <c>deploymentCount</c> is <b>non-zero</b>. Zero is what
    /// <c>GetValueOrDefault</c> gives an app with no deployments, and it renders as <c>"0"</c>
    /// rather than a dash — so a fixture using 0 would satisfy this assertion while proving
    /// nothing about whether the count was read at all.
    /// </summary>
    [Fact]
    public void The_apps_list_renders_every_column()
    {
        EntKube.Contracts.Delivery.AppSummary app = new(
            Id: Guid.NewGuid(),
            TenantId: Guid.NewGuid(),
            CustomerId: Guid.NewGuid(),
            CustomerName: "Acme AB",
            Name: "invoicing",
            Namespace: "invoicing-prod");

        EveryColumnRenders(
            Command("apps", "list"),
            JsonSerializer.Serialize(
                new[] { AppResponse.From(app, deploymentCount: 7) }, AsTheApiSerialises));
    }

    /// <summary>
    /// ⚠️ The three enum members are all <b>non-zero</b> ones.
    /// <c>Manual</c>/<c>Unknown</c>/<c>Unknown</c> are 0, so a fixture left on the defaults would
    /// render whatever the factory read — including the wrong enum — as a plausible cell.
    /// </summary>
    [Fact]
    public void The_deployments_list_renders_every_column()
    {
        EntKube.Contracts.Delivery.DeploymentSummary deployment = new(
            Id: Guid.NewGuid(),
            TenantId: Guid.NewGuid(),
            AppId: Guid.NewGuid(),
            AppName: "checkout",
            EnvironmentId: Guid.NewGuid(),
            EnvironmentName: "Production",
            ClusterId: Guid.NewGuid(),
            ClusterName: "prod-north",
            Name: "checkout-api",
            Type: EntKube.Contracts.Delivery.DeploymentType.GitHelm,
            Namespace: "checkout-prod",
            SyncStatus: EntKube.Contracts.Delivery.SyncStatus.OutOfSync,
            HealthStatus: EntKube.Contracts.Delivery.HealthStatus.Degraded,
            StatusMessage: "a previous sync timed out",
            LastSyncedAt: new DateTime(2026, 11, 1, 6, 30, 0, DateTimeKind.Utc),
            IsManaged: true);

        EveryColumnRenders(
            Command("deployments", "list"),
            JsonSerializer.Serialize(
                new[] { DeploymentResponse.From(deployment) }, AsTheApiSerialises));
    }

    [Fact]
    public void The_feedback_list_renders_every_column()
    {
        FeedbackResponse entry = new(
            Id: Guid.NewGuid(),
            Kind: "Bug",
            Status: "New",
            Rating: 4,
            Message: "The checkout button does nothing",
            ContactEmail: null,
            Site: "Storefront",
            PagePath: "/checkout",
            HasScreenshot: true,
            TicketId: null,
            CreatedAt: new DateTime(2026, 11, 1, 6, 30, 0, DateTimeKind.Utc));

        EveryColumnRenders(Command("feedback"), JsonSerializer.Serialize(new[] { entry }, AsTheApiSerialises));
    }

    [Fact]
    public void The_synthetics_list_renders_every_column()
    {
        SyntheticCheckResponse check = new(
            Id: Guid.NewGuid(),
            Name: "Storefront",
            Url: "https://www.example.com/health",
            IsEnabled: true,
            State: "Passing",
            ConsecutiveFailures: 0,
            UptimePercent: 99.5,
            LastStatusCode: 200,
            LastDurationMs: 120,
            LastFailure: null,
            LastRunAt: new DateTime(2026, 11, 1, 6, 30, 0, DateTimeKind.Utc));

        EveryColumnRenders(Command("synthetics"), JsonSerializer.Serialize(new[] { check }, AsTheApiSerialises));
    }

    [Fact]
    public void The_errors_list_renders_every_column()
    {
        ErrorIssueResponse issue = new(
            Id: Guid.NewGuid(),
            Source: "Log",
            Scope: "prod-north",
            Namespace: "shop",
            Workload: "api",
            Title: "connection refused to <ip>",
            SampleMessage: "connection refused to 10.0.0.1:5432",
            Occurrences: 12,
            CountIsPartial: false,
            Status: "Open",
            Regressions: 0,
            FirstSeenAt: new DateTime(2026, 11, 1, 6, 30, 0, DateTimeKind.Utc),
            LastSeenAt: new DateTime(2026, 11, 1, 7, 30, 0, DateTimeKind.Utc));

        EveryColumnRenders(Command("errors"), JsonSerializer.Serialize(new[] { issue }, AsTheApiSerialises));
    }

    /// <summary>
    /// ⚠️ The measure. The facts above are exactly the commands
    /// <see cref="CliColumnParityTests"/> can check against a schema, and they must stay in step:
    /// if a route gains a response type and no rendering fact is added for its command, this fails
    /// rather than letting the rendered coverage quietly fall behind the schema coverage.
    /// </summary>
    [Fact]
    public void A_rendering_fact_exists_for_every_command_whose_body_is_described()
    {
        string[] rendered =
        [
            "cost", "cost history", "cost months", "cost rates list",
            "clusters list", "components list", "apps list", "deployments list", "feedback", "synthetics", "errors",
        ];

        IEnumerable<string> describable =
            CliCommands.All
                .Where(c => c.Columns.Length > 0)
                .Select(c => string.Join(' ', c.Path))
                .Where(name => !CliColumnParityTests.BankedAsUnverifiable(name));

        describable.Should().BeEquivalentTo(
            rendered,
            "a command whose response is described can be rendered against a fixture, and the "
            + "schema check cannot see an empty array, an object cell or a column list TableWriter "
            + "never consults — add a fact above when a route is typed");
    }
}
