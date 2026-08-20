using EntKube.Web.Data;
using EntKube.Web.Services;
using FluentAssertions;

namespace EntKube.Web.Tests;

/// <summary>
/// Reconciliation of a cluster scan against configured autoscalers: deciding which live objects
/// EntKube owns, which are somebody else's, and which ones are fighting over the same workload.
/// The scan exists to catch autoscalers EntKube never wrote — a raw manifest or a hand-applied
/// one — which the save-time conflict check cannot see.
/// </summary>
public class AutoscalerScanTests
{
    private static LiveAutoscaler Live(
        string name, LiveAutoscalerKind kind, string targetName,
        string targetKind = "Deployment", string cluster = "prod", string ns = "billing",
        AutoscalerOwner owner = AutoscalerOwner.External, string? ownerName = null) =>
        new()
        {
            ClusterName = cluster,
            Namespace = ns,
            Kind = kind,
            Name = name,
            TargetKind = targetKind,
            TargetName = targetName,
            Owner = owner,
            OwnerName = ownerName,
        };

    private static KedaScaler Configured(
        string name, KedaScalerKind kind = KedaScalerKind.Hpa,
        string targetName = "api", string? customYaml = null) =>
        new()
        {
            Id = Guid.NewGuid(),
            Name = name,
            Kind = kind,
            ScaleTargetKind = "Deployment",
            ScaleTargetName = targetName,
            CustomYaml = customYaml,
        };

    [Fact]
    public void ReconcileScan_ClaimsLiveObjectsThatMatchAConfiguredScaler()
    {
        AutoscalerScanResult scan = new()
        {
            ScannedTargets = { "prod/billing" },
            Live = { Live("api-cpu", LiveAutoscalerKind.Hpa, "api") },
        };

        KedaScalerService.ReconcileScan(scan, [Configured("api-cpu")]);

        scan.Live[0].Owner.Should().Be(AutoscalerOwner.EntKube);
        scan.Unmanaged.Should().BeEmpty();
    }

    [Fact]
    public void ReconcileScan_LeavesUnknownObjectsMarkedExternal()
    {
        AutoscalerScanResult scan = new()
        {
            ScannedTargets = { "prod/billing" },
            Live = { Live("legacy-hpa", LiveAutoscalerKind.Hpa, "api") },
        };

        KedaScalerService.ReconcileScan(scan, []);

        scan.Unmanaged.Should().ContainSingle().Which.Name.Should().Be("legacy-hpa");
    }

    [Fact]
    public void ReconcileScan_DoesNotPitAKedaOwnedHpaAgainstItsOwnScaledObject()
    {
        // KEDA creates keda-hpa-<name> for each ScaledObject. Treating that as a second
        // autoscaler would report every healthy KEDA scaler as a conflict.
        AutoscalerScanResult scan = new()
        {
            ScannedTargets = { "prod/billing" },
            Live =
            {
                Live("worker-queue", LiveAutoscalerKind.ScaledObject, "worker"),
                Live("keda-hpa-worker-queue", LiveAutoscalerKind.Hpa, "worker",
                     owner: AutoscalerOwner.Keda, ownerName: "worker-queue"),
            },
        };

        KedaScalerService.ReconcileScan(scan, [Configured("worker-queue", KedaScalerKind.ScaledObject, "worker")]);

        scan.Conflicting.Should().BeEmpty();
        scan.Live.Single(l => l.Kind == LiveAutoscalerKind.Hpa).Owner.Should().Be(AutoscalerOwner.Keda);
        scan.Live.Single(l => l.Kind == LiveAutoscalerKind.ScaledObject).Owner.Should().Be(AutoscalerOwner.EntKube);
    }

    [Fact]
    public void ReconcileScan_FlagsBothSidesWhenTwoAutoscalersShareAWorkload()
    {
        AutoscalerScanResult scan = new()
        {
            ScannedTargets = { "prod/billing" },
            Live =
            {
                Live("api-cpu", LiveAutoscalerKind.Hpa, "api"),          // ours
                Live("legacy-hpa", LiveAutoscalerKind.Hpa, "api"),       // shipped in raw manifests
            },
        };

        KedaScalerService.ReconcileScan(scan, [Configured("api-cpu")]);

        scan.Conflicting.Should().HaveCount(2);
        scan.Live.Single(l => l.Name == "api-cpu").Conflict.Should().Contain("legacy-hpa");
        scan.Live.Single(l => l.Name == "legacy-hpa").Conflict.Should().Contain("api-cpu");
    }

    [Fact]
    public void ReconcileScan_FlagsAScaledObjectAndHpaOnTheSameWorkload()
    {
        AutoscalerScanResult scan = new()
        {
            ScannedTargets = { "prod/billing" },
            Live =
            {
                Live("api-events", LiveAutoscalerKind.ScaledObject, "api"),
                Live("api-cpu", LiveAutoscalerKind.Hpa, "api"),
            },
        };

        KedaScalerService.ReconcileScan(scan, []);

        scan.Conflicting.Should().HaveCount(2);
    }

    [Fact]
    public void ReconcileScan_KeepsWorkloadsSeparatePerClusterNamespaceAndKind()
    {
        AutoscalerScanResult scan = new()
        {
            ScannedTargets = { "prod/billing", "dr/billing" },
            Live =
            {
                Live("api-cpu", LiveAutoscalerKind.Hpa, "api", cluster: "prod"),
                Live("api-cpu", LiveAutoscalerKind.Hpa, "api", cluster: "dr"),
                Live("api-sts", LiveAutoscalerKind.Hpa, "api", targetKind: "StatefulSet"),
            },
        };

        KedaScalerService.ReconcileScan(scan, []);

        scan.Conflicting.Should().BeEmpty();
    }

    [Fact]
    public void ReconcileScan_MatchesCustomYamlByTheNameInsideTheDocument()
    {
        // A Custom scaler's Kubernetes resource name comes from its YAML, not from the row's name.
        KedaScaler custom = Configured("nightly-batch", KedaScalerKind.Custom, customYaml:
            "apiVersion: keda.sh/v1alpha1\n" +
            "kind: ScaledJob\n" +
            "metadata:\n" +
            "  name: batch-runner\n" +
            "spec:\n" +
            "  maxReplicaCount: 10\n");

        AutoscalerScanResult scan = new()
        {
            ScannedTargets = { "prod/billing" },
            Live = { Live("batch-runner", LiveAutoscalerKind.ScaledJob, "") },
        };

        KedaScalerService.ReconcileScan(scan, [custom]);

        scan.Live[0].Owner.Should().Be(AutoscalerOwner.EntKube);
    }

    [Fact]
    public void ReconcileScan_ReportsConfiguredScalersMissingFromTheCluster()
    {
        AutoscalerScanResult scan = new()
        {
            ScannedTargets = { "prod/billing" },
            Live = { Live("api-cpu", LiveAutoscalerKind.Hpa, "api") },
        };

        KedaScalerService.ReconcileScan(scan, [
            Configured("api-cpu"),
            Configured("worker-cpu", targetName: "worker"),
            // Custom YAML is excluded: its resource name is only as good as the parse, so a
            // false "not applied" would be worse than silence.
            Configured("raw", KedaScalerKind.Custom, customYaml: "not: valid: yaml: ["),
        ]);

        scan.NotApplied.Should().ContainSingle().Which.Should().Be("worker-cpu");
    }

    [Fact]
    public void ReconcileScan_StaysQuietAboutMissingScalersWhenAScanTargetFailed()
    {
        // An unreadable namespace means "unknown", not "empty" — claiming the scaler is
        // missing would send the operator chasing a phantom.
        AutoscalerScanResult scan = new()
        {
            ScannedTargets = { "prod/billing" },
            Errors = { "prod/billing: connection refused" },
        };

        KedaScalerService.ReconcileScan(scan, [Configured("api-cpu")]);

        scan.NotApplied.Should().BeEmpty();
    }

    // ── Transient API failures ────────────────────────────────────────────────

    private const string StorageInitializing =
        """
        {"kind":"Status","apiVersion":"v1","metadata":{},"status":"Failure",
         "message":"storage is (re)initializing","reason":"TooManyRequests",
         "details":{"retryAfterSeconds":3},"code":429}
        """;

    [Fact]
    public void RetryDelayFor_WaitsAtLeastAsLongAsTheServerAsked()
    {
        KedaScalerService.RetryDelayFor(StorageInitializing, attempt: 1)
            .Should().Be(TimeSpan.FromSeconds(3));
    }

    [Fact]
    public void RetryDelayFor_KeepsBackingOffWhenTheHintStaysShort()
    {
        // An apiserver warming up answers "1 second" every time; obeying that literally would
        // burn every attempt inside the first few seconds and report a failure too early.
        string body = """{"details":{"retryAfterSeconds":1}}""";

        KedaScalerService.RetryDelayFor(body, attempt: 1).Should().Be(TimeSpan.FromSeconds(1));
        KedaScalerService.RetryDelayFor(body, attempt: 3).Should().Be(TimeSpan.FromSeconds(4));
    }

    [Fact]
    public void RetryDelayFor_BacksOffWhenTheServerGivesNoHint()
    {
        KedaScalerService.RetryDelayFor("{\"message\":\"boom\"}", attempt: 1).Should().Be(TimeSpan.FromSeconds(1));
        KedaScalerService.RetryDelayFor(null, attempt: 2).Should().Be(TimeSpan.FromSeconds(2));
        KedaScalerService.RetryDelayFor("not json at all", attempt: 3).Should().Be(TimeSpan.FromSeconds(4));
    }

    [Fact]
    public void RetryDelayFor_ClampsAnAbsurdHint()
    {
        // A confused or hostile hint must not stall the scan for minutes.
        string body = """{"details":{"retryAfterSeconds":3600}}""";
        KedaScalerService.RetryDelayFor(body, attempt: 1).Should().Be(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void DescribeApiError_LiftsTheMessageOutOfAKubernetesStatusBody()
    {
        Exception ex = ApiError(System.Net.HttpStatusCode.TooManyRequests, StorageInitializing);

        string described = KedaScalerService.DescribeApiError(ex);

        described.Should().StartWith("storage is (re)initializing (HTTP 429)");
        described.Should().Contain("try again shortly");
        described.Should().NotContain("apiVersion");     // no raw JSON dumped at the operator
    }

    [Fact]
    public void DescribeApiError_FallsBackToTheExceptionMessageForNonStatusBodies()
    {
        KedaScalerService.DescribeApiError(new InvalidOperationException("no kubeconfig"))
            .Should().Be("no kubeconfig");

        KedaScalerService.DescribeApiError(ApiError(System.Net.HttpStatusCode.BadGateway, "<html>gateway</html>"))
            .Should().Contain("Boom");
    }

    private static k8s.Autorest.HttpOperationException ApiError(System.Net.HttpStatusCode code, string body) =>
        new("Boom")
        {
            Response = new k8s.Autorest.HttpResponseMessageWrapper(
                new System.Net.Http.HttpResponseMessage(code), body),
        };
}
