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
    /// ⚠️ BREACHED is a bool, and false is the value most objectives carry: a cell that rendered a
    /// dash for false would hide every objective in good standing.
    /// </summary>
    [Fact]
    public void The_slos_list_renders_every_column()
    {
        SloResponse slo = new(
            Id: Guid.NewGuid(),
            Name: "Checkout availability",
            Kind: "ServiceAvailability",
            TargetPercent: 99.9,
            WindowDays: 30,
            Good: 998_000,
            Total: 1_000_000,
            AttainedPercent: 99.8,
            BudgetSpentFraction: 2.0,
            BurnRate: 1.5,
            IsBreached: false);

        EveryColumnRenders(Command("slos"), JsonSerializer.Serialize(new[] { slo }, AsTheApiSerialises));
    }

    /// <summary>
    /// The rows sit under <c>machines</c>, beside a <c>warnings</c> list the table does not show; a
    /// RowsProperty that pointed at the wrong one would render the warnings as rows.
    /// </summary>
    [Fact]
    public void The_vms_list_renders_every_column()
    {
        VirtualMachineListResponse list = new(
            Machines:
            [
                new VirtualMachineResponse(
                    Ns: "vms", Name: "web-01", Status: "Running", RunStrategy: "Always", Cores: 2, Memory: "4Gi",
                    Node: "worker-2", Ip: "10.42.1.17", LiveMigratable: true,
                    CreatedAt: new DateTime(2026, 11, 1, 6, 30, 0, DateTimeKind.Utc)),
            ],
            Warnings: []);

        EveryColumnRenders(Command("vms", "list"), JsonSerializer.Serialize(list, AsTheApiSerialises));
    }

    /// <summary>
    /// ⚠️ <b>Every binding in the fixture carries an ERROR.</b> <c>TableWriter</c> renders a null
    /// as <c>"-"</c>, and this fact's whole job is that no column renders a dash — but
    /// <c>bindings[].error</c> is null for a binding that SYNCED, which is the normal case. So a
    /// fixture of successful bindings would fail this assertion while the CLI was working
    /// perfectly. The honest reading is that the ERROR column is dash-by-design on success, and
    /// what this fact can prove is that the column RESOLVES when there is an error to show.
    ///
    /// <para>⚠️ That makes this the one rendering fact in the file whose fixture is deliberately
    /// a failure case rather than a populated happy path — stated here because the class summary's
    /// "the fixtures below populate every field" does not hold for it.</para>
    /// </summary>
    [Fact]
    public void The_secrets_sync_renders_every_column()
    {
        EntKube.Contracts.Secrets.AppSecretSyncResult result = new(
            Output: "synced 2 of 3",
            Bindings:
            [
                new(EntKube.Contracts.Secrets.SecretBindingKind.Database, "billing-db", "connection refused"),
                new(EntKube.Contracts.Secrets.SecretBindingKind.Cache, "billing-redis", "no such namespace"),
            ],
            UnlistedFamilies:
            [
                new(EntKube.Contracts.Secrets.SecretBindingKind.Storage, "the storage family could not be listed"),
            ],
            FatalError: null);

        EveryColumnRenders(
            Command("secrets", "sync"),
            JsonSerializer.Serialize(AppSecretSyncResponse.From(result), AsTheApiSerialises));
    }

    [Fact]
    public void The_advisor_renders_every_column()
    {
        EntKube.Contracts.Advisor.FindingSummary finding = new(
            Id: "cert:11111111-1111-1111-1111-111111111111",
            Category: EntKube.Contracts.Advisor.FindingCategory.Security,
            Severity: EntKube.Contracts.Advisor.FindingSeverity.Warning,
            Horizon: EntKube.Contracts.Advisor.FindingHorizon.ThisWeek,
            Title: "A certificate expires in six days",
            Detail: "wildcard.example.com",
            ScopeLabel: "prod-north / istio-system",
            DueAt: new DateTime(2026, 11, 18, 0, 0, 0, DateTimeKind.Utc),
            Remediation: "Renew it, or let cert-manager renew it",
            Source: "certificates",
            State: EntKube.Contracts.Advisor.FindingStatus.Active,
            ClusterId: Guid.NewGuid(),
            CustomerName: "Acme AB");

        EveryColumnRenders(
            Command("advisor"),
            JsonSerializer.Serialize(
                new[] { AdvisorFindingResponse.From(finding) }, AsTheApiSerialises));
    }

    /// <summary>
    /// ⚠️ The fixture's <c>severity</c> is free text as the alert rule wrote it, NOT an enum
    /// member — see <see cref="IncidentResponse"/>. A fixture using a tidy
    /// <c>"Critical"</c> would teach the next reader that this field is a closed set.
    /// </summary>
    [Fact]
    public void The_incidents_render_every_column()
    {
        EntKube.Contracts.Telemetry.IncidentSummary incident = new(
            Id: Guid.NewGuid(),
            ClusterId: Guid.NewGuid(),
            ClusterName: "prod-north",
            AlertName: "KubePodCrashLooping",
            Summary: "billing-api has restarted 14 times in 10 minutes",
            Severity: "page",
            Status: EntKube.Contracts.Telemetry.IncidentStatus.Active,
            StartsAt: new DateTime(2026, 11, 12, 6, 30, 0, DateTimeKind.Utc),
            ResolvedAt: null,
            AcknowledgedBy: "nils",
            RunbookUrl: "https://runbooks.example.com/crashloop");

        EveryColumnRenders(
            Command("incidents"),
            JsonSerializer.Serialize(
                new[] { IncidentResponse.From(incident) }, AsTheApiSerialises));
    }

    /// <summary>
    /// ⚠️ <b>The fixture must have DATA.</b> Three of this command's seven columns —
    /// <c>achievedPercent</c>, <c>budgetConsumedFraction</c>, <c>burnRateRecent</c> — are
    /// <c>double?</c> and are null for a deployment with no health samples in the window, which
    /// <c>TableWriter</c> renders as <c>"-"</c>. A no-data budget would therefore fail this fact
    /// while the CLI was working correctly: for such a row the dashes are the honest answer, and
    /// <c>hasData</c> is what says so. What this proves is that the columns RESOLVE when there is
    /// a measurement to show.
    ///
    /// <para>⚠️ Also note the envelope is read through <c>RowsProperty = "budgets"</c>, so this
    /// serialises the whole <see cref="ErrorBudgetReportResponse"/> rather than a bare array.</para>
    /// </summary>
    [Fact]
    public void The_error_budgets_render_every_column()
    {
        EntKube.Contracts.Telemetry.ErrorBudgetSummary budget = new(
            DeploymentId: Guid.NewGuid(),
            DeploymentName: "billing-api-prod",
            AppName: "billing",
            CustomerId: Guid.NewGuid(),
            CustomerName: "Acme AB",
            ClusterId: Guid.NewGuid(),
            TargetPercent: 99.9d,
            WindowDays: 30,
            AchievedPercent: 99.82d,
            BudgetConsumedFraction: 1.8d,
            BurnRateRecent: 2.4d,
            HoursToExhaustion: null,
            HasData: true,
            IsBreached: true);

        EveryColumnRenders(
            Command("error-budgets"),
            JsonSerializer.Serialize(
                ErrorBudgetReportResponse.From([budget]), AsTheApiSerialises));
    }

    /// <summary>
    /// ⚠️ The fixture's component has a chart name, an installed AND a latest version. All three
    /// are <c>string?</c> on the contract and render as <c>"-"</c> when absent — which is the
    /// honest answer for a component whose chart could not be resolved, so a fixture leaving them
    /// null would fail this fact while the CLI worked.
    /// </summary>
    [Fact]
    public void The_upgrades_render_every_column()
    {
        EntKube.Contracts.Catalog.UpgradeReportSummary report = new(
            Components:
            [
                new(
                    ClusterId: Guid.NewGuid(),
                    ClusterName: "prod-north",
                    ComponentId: Guid.NewGuid(),
                    DisplayName: "Prometheus stack",
                    ChartName: "kube-prometheus-stack",
                    InstalledVersion: "62.3.0",
                    LatestVersion: "65.1.0",
                    Status: EntKube.Contracts.Catalog.UpgradeAvailability.UpgradeAvailable,
                    Lag: EntKube.Contracts.Catalog.UpgradeLag.Major,
                    VersionsBehind: 3),
            ],
            UpgradeCount: 1,
            MajorCount: 1,
            DeprecatedCount: 0,
            UnknownCount: 0);

        EveryColumnRenders(
            Command("upgrades"),
            JsonSerializer.Serialize(UpgradeReportResponse.From(report), AsTheApiSerialises));
    }

    /// <summary>
    /// ⚠️ The fixture's row is <c>Drifted</c> with a non-zero <c>changedLines</c>. An
    /// <c>InSync</c> row has 0, which renders as <c>"0"</c> rather than a dash — so this fact
    /// would pass either way, and the reason for choosing Drifted is that it is the row an
    /// operator is looking at the table for.
    /// </summary>
    [Fact]
    public void The_drift_renders_every_column()
    {
        EntKube.Web.Services.Upgrades.DriftReport report = new()
        {
            GeneratedAt = new DateTime(2026, 11, 12, 6, 0, 0, DateTimeKind.Utc),
            Results =
            [
                new()
                {
                    DeploymentId = Guid.NewGuid(),
                    DeploymentName = "billing-api-prod",
                    AppName = "billing",
                    ClusterId = Guid.NewGuid(),
                    ClusterName = "prod-north",
                    Namespace = "billing-prod",
                    State = EntKube.Web.Services.Upgrades.DriftState.Drifted,
                    ChangedLines = 14,
                    Note = "image tag differs from the declared manifest",
                },
            ],
        };

        EveryColumnRenders(
            Command("drift"),
            JsonSerializer.Serialize(DriftReportResponse.FromReport(report), AsTheApiSerialises));
    }

    /// <summary>
    /// ⚠️ <b>The fixture's image is SCANNED</b>, with non-zero findings. An <c>Unscanned</c> image
    /// reports <c>critical: 0</c> because the counts are <c>Scan?.X ?? 0</c> — and that 0 means
    /// UNKNOWN, not clean. It would still render (as <c>"0"</c>, not a dash), so this fact cannot
    /// tell the two apart; what it proves is that every column resolves. The distinction itself is
    /// pinned in <c>UpgradeScanWireShapeTests</c>, which is where it belongs.
    /// </summary>
    [Fact]
    public void The_supply_chain_renders_every_column()
    {
        EntKube.Web.Services.SupplyChain.SupplyChainReport report = new()
        {
            GeneratedAt = new DateTime(2026, 11, 12, 6, 0, 0, DateTimeKind.Utc),
            Images =
            [
                new()
                {
                    Image = "ghcr.io/acme/billing:1.4.2",
                    ClusterId = Guid.NewGuid(),
                    ClusterName = "prod-north",
                    Workloads = ["deployment/billing-api"],
                    State = EntKube.Web.Services.SupplyChain.ImageScanState.Vulnerable,
                    IsDigestPinned = false,
                    Scan = new() { Critical = 2, High = 7, Fixable = 5 },
                },
            ],
        };

        EveryColumnRenders(
            Command("supply-chain"),
            JsonSerializer.Serialize(
                SupplyChainReportResponse.FromReport(report), AsTheApiSerialises));
    }

    /// <summary>
    /// ⚠️ <b>The fixture's rollout is FINISHED and carries a verdict</b>, because the
    /// <c>VERDICT</c> column reads a field that is null for every release still inside its
    /// analysis window — the most common row on the route. So this fact proves the column
    /// resolves; it does not prove an operator watching a release in flight sees anything there,
    /// and they do not. The same goes for <c>finishedAt</c>, which this command happens not to
    /// render.
    /// </summary>
    [Fact]
    public void The_rollouts_render_every_column()
    {
        EntKube.Contracts.Delivery.RolloutSummary rollout = new(
            Id: Guid.Parse("50110000-0000-0000-0000-00000000000a"),
            AppName: "billing",
            DeploymentName: "billing-api-prod",
            EnvironmentName: "prod",
            ClusterName: "prod-north",
            Status: EntKube.Contracts.Delivery.RolloutVerdictStatus.RolledBack,
            StartedAt: new DateTime(2026, 11, 12, 6, 0, 0, DateTimeKind.Utc),
            FinishedAt: new DateTime(2026, 11, 12, 6, 12, 0, DateTimeKind.Utc),
            Verdict: "error rate 4.1% over the window, threshold 1%",
            TriggeredBy: "nils@entit.se");

        EveryColumnRenders(
            Command("rollouts"),
            JsonSerializer.Serialize(
                new[] { RolloutResponse.From(rollout) }, AsTheApiSerialises));
    }

    /// <summary>
    /// ⚠️ <b><c>LAST BACKUP</c> renders only because this fixture's usable backup has a
    /// <c>CompletedAt</c>.</b> <c>VeleroBackup.IsUsable</c> does not require one, so a cluster can
    /// answer <c>restorable: true</c> with <c>lastUsableBackupAt: null</c> — and this command
    /// would then print a dash in that column beside a <c>RESTORABLE</c> of True. That
    /// combination is reachable in production and is NOT what this fact measures; it is named in
    /// <see cref="DrStatusResponse"/> and pinned in <c>RolloutDrWireShapeTests</c>.
    ///
    /// <para>The backup is <c>Completed</c> with no errors on purpose: a <c>PartiallyFailed</c>
    /// one carries a completion time too, and would make <c>RESTORABLE</c> render <c>False</c>
    /// while every other column stayed green.</para>
    /// </summary>
    [Fact]
    public void The_disaster_recovery_posture_renders_every_column()
    {
        DateTime now = new(2026, 11, 12, 6, 0, 0, DateTimeKind.Utc);

        EntKube.Web.Services.Dr.ClusterDrStatus status = new()
        {
            ClusterId = Guid.Parse("c0000000-0000-0000-0000-00000000000c"),
            ClusterName = "prod-north",
            IsVeleroInstalled = true,
            Schedules =
            [
                new()
                {
                    Name = "daily",
                    Cron = "0 2 * * *",
                    LastBackupAt = now.AddHours(-4),
                },
            ],
            Backups =
            [
                new()
                {
                    Name = "daily-20261112",
                    Phase = EntKube.Web.Services.Dr.VeleroPhase.Completed,
                    StartedAt = now.AddHours(-4),
                    CompletedAt = now.AddHours(-4).AddMinutes(11),
                },
            ],
            Restores =
            [
                new()
                {
                    Name = "drill-20261001",
                    BackupName = "daily-20261001",
                    Phase = EntKube.Web.Services.Dr.VeleroPhase.Completed,
                    CompletedAt = now.AddDays(-42),
                },
            ],
        };

        EveryColumnRenders(
            Command("dr"),
            JsonSerializer.Serialize(
                new[] { DrStatusResponse.From(status, now) }, AsTheApiSerialises));
    }

    /// <summary>
    /// The support queue. Every column is filled, an assignee, a reminder and a tag included, so a
    /// dash here can only mean the column names a field the API does not send.
    /// </summary>
    [Fact]
    public void The_ticket_queue_renders_every_column()
    {
        DateTime reported = new(2026, 11, 12, 8, 0, 0, DateTimeKind.Utc);

        EntKube.Contracts.Support.TicketSummary ticket = new(
            Id: Guid.Parse("71c00000-0000-0000-0000-000000000001"),
            Number: 1042,
            Title: "Login fails for all users",
            Status: "InProgress",
            Priority: "P2",
            PriorityConfirmed: true,
            CustomerId: Guid.Parse("c0570000-0000-0000-0000-000000000001"),
            Customer: "Entit AB",
            Assignee: "nils",
            Tags: ["login", "vpn"],
            ReportedAt: reported,
            ReminderAt: reported.AddHours(6),
            Response: new EntKube.Contracts.Support.TicketClock(reported.AddHours(4), null, false, false),
            Resolution: new EntKube.Contracts.Support.TicketClock(reported.AddDays(2), null, false, false),
            UpdateDue: reported.AddHours(4),
            PenaltyDeviation: false);

        EveryColumnRenders(
            Command("tickets", "list"),
            JsonSerializer.Serialize(new[] { TicketSummaryResponse.From(ticket) }, AsTheApiSerialises));
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
            "clusters list", "components list", "apps list", "deployments list",
            "secrets sync", "feedback", "synthetics", "errors", "slos",
            "advisor", "incidents", "error-budgets",
            "upgrades", "drift", "supply-chain",
            "vms list",
            "rollouts", "dr",
            "tickets list",
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
