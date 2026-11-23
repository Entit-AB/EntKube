using EntKube.Web.Data;
using EntKube.Web.Services;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace EntKube.Web.Tests;

/// <summary>
/// Kyverno's policy reports are where Audit-mode findings live, and the only place they live. Reading
/// them must stay inside the tenant's own namespaces, and a partial or empty read must never look like
/// everything passed.
/// </summary>
public class KyvernoPolicyReportServiceTests : IDisposable
{
    private readonly SqliteConnection connection;
    private readonly ApplicationDbContext db;
    private readonly KyvernoPolicyReportService service;
    private readonly Guid sharedCluster = Guid.NewGuid();

    public KyvernoPolicyReportServiceTests()
    {
        connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();
        TestDbContextFactory factory = new(connection);
        db = factory.CreateDbContext();
        db.Database.EnsureCreated();
        service = new KyvernoPolicyReportService(factory, TestServices.NoClusterAccess);
    }

    public void Dispose()
    {
        db.Dispose();
        connection.Dispose();
    }

    // ── Parsing the two report shapes ────────────────────────────────────────

    [Fact]
    public void A_per_resource_report_names_its_resource_in_scope()
    {
        // Kyverno 1.10+ writes one PolicyReport per resource, results carrying no resource of their own.
        List<PolicyResultRow> rows = [.. KyvernoPolicyReportService.ParseReports(PerResourceReport, "aks-prod", "acme-billing")];

        rows.Should().HaveCount(2);
        PolicyResultRow fail = rows.Single(r => r.Result == "fail");
        fail.Policy.Should().Be("require-labels");
        fail.Rule.Should().Be("require-team");
        fail.ResourceKind.Should().Be("Deployment");
        fail.ResourceName.Should().Be("billing-api");
        fail.Severity.Should().Be("medium");
        fail.Message.Should().Contain("label 'team' is required");
        fail.Timestamp.Should().Be(DateTimeOffset.FromUnixTimeSeconds(1763600000));
        fail.Cluster.Should().Be("aks-prod");
        fail.Namespace.Should().Be("acme-billing");
    }

    [Fact]
    public void An_aggregated_report_yields_one_row_per_named_resource()
    {
        // Older Kyverno aggregated many resources into one report, naming them on each result.
        List<PolicyResultRow> rows = [.. KyvernoPolicyReportService.ParseReports(AggregatedReport, "c", "acme-web")];

        rows.Where(r => r.Policy == "disallow-latest-tag").Select(r => r.ResourceName)
            .Should().BeEquivalentTo(["web-1", "web-2"]);
    }

    [Fact]
    public void A_list_without_items_is_no_results_not_an_error()
    {
        KyvernoPolicyReportService.ParseReports("""{"kind":"PolicyReportList","metadata":{}}""", "c", "ns")
            .Should().BeEmpty();
    }

    // ── Summarising ──────────────────────────────────────────────────────────

    [Fact]
    public void Failures_come_first_and_only_failures_warnings_and_errors_are_findings()
    {
        PolicyResultRow[] rows =
        [
            Row("only-passes", "pass"), Row("only-passes", "pass"),
            Row("warns", "warn"),
            Row("fails", "pass"), Row("fails", "fail"), Row("fails", "error"), Row("fails", "skip"),
        ];

        IReadOnlyList<PolicyResultSummary> summary = KyvernoPolicyReportService.Summarize(rows);

        summary.Select(p => p.Policy).Should().Equal("fails", "warns", "only-passes");

        PolicyResultSummary fails = summary[0];
        (fails.Pass, fails.Fail, fails.Error, fails.Skip).Should().Be((1, 1, 1, 1));
        fails.Findings.Select(f => f.Result).Should().Equal("fail", "error");
        summary[2].Findings.Should().BeEmpty();
    }

    // ── Reading: scoped to the tenant, honest about gaps ─────────────────────

    [Fact]
    public async Task Only_the_tenants_own_namespaces_are_read_on_a_shared_cluster()
    {
        (Guid tenantA, Guid envA) = Seed("tenant-a", "acme-billing", "acme-web");
        Seed("tenant-b", "globex-api");
        await db.SaveChangesAsync();

        List<string> asked = [];
        await service.GetOverviewAsync(tenantA, envA, (_, ns, _) =>
        {
            lock (asked) { asked.Add(ns); }
            return Task.FromResult(new NamespaceReports(true, []));
        }, default);

        asked.Should().BeEquivalentTo(["acme-billing", "acme-web"]);
    }

    [Fact]
    public async Task Another_tenants_environment_id_reads_nothing()
    {
        // Environments are tenant-owned, so the environment filter alone looks like isolation. It is
        // not: the id comes from the caller. Only the tenant filter stops tenant A naming B's environment.
        (Guid tenantA, _) = Seed("tenant-a", "acme-billing");
        (_, Guid envB) = Seed("tenant-b", "globex-api");
        await db.SaveChangesAsync();

        List<string> asked = [];
        KyvernoReportOverview overview = await service.GetOverviewAsync(tenantA, envB, (_, ns, _) =>
        {
            lock (asked) { asked.Add(ns); }
            return Task.FromResult(new NamespaceReports(true, [PerResourceReport]));
        }, default);

        asked.Should().BeEmpty();
        overview.NamespacesAsked.Should().Be(0);
    }

    [Fact]
    public async Task A_namespace_that_cannot_be_read_makes_the_answer_incomplete()
    {
        (Guid tenant, Guid env) = Seed("tenant-a", "acme-billing", "acme-web");
        await db.SaveChangesAsync();

        KyvernoReportOverview overview = await service.GetOverviewAsync(tenant, env, (_, ns, _) =>
            ns == "acme-web"
                ? throw new HttpRequestException("connection refused")
                : Task.FromResult(new NamespaceReports(true, [PerResourceReport])), default);

        overview.IsComplete.Should().BeFalse("a failure in the unread namespace would be invisible");
        overview.NamespacesAsked.Should().Be(2);
        overview.NamespacesRead.Should().Be(1);
        overview.Gaps.Should().ContainSingle().Which.Should().Be(
            new PolicyReportGap(ClusterName, "acme-web", "connection refused"));
        overview.Failing.Should().Be(1, "what was read is still shown");
    }

    [Fact]
    public async Task A_cluster_without_the_reports_api_is_one_gap_not_one_per_namespace()
    {
        (Guid tenant, Guid env) = Seed("tenant-a", "acme-billing", "acme-web", "acme-jobs");
        await db.SaveChangesAsync();

        KyvernoReportOverview overview = await service.GetOverviewAsync(tenant, env,
            (_, _, _) => Task.FromResult(new NamespaceReports(false, [])), default);

        overview.IsComplete.Should().BeFalse();
        overview.Gaps.Should().ContainSingle().Which.Reason.Should().Contain("Kyverno is not installed");
    }

    [Fact]
    public async Task Reports_with_no_results_are_not_counted_as_passing()
    {
        // The background scan has not run, or nothing matches: "nothing recorded" is not "all clear".
        (Guid tenant, Guid env) = Seed("tenant-a", "acme-billing");
        await db.SaveChangesAsync();

        KyvernoReportOverview overview = await service.GetOverviewAsync(tenant, env,
            (_, _, _) => Task.FromResult(new NamespaceReports(true, ["""{"items":[]}"""])), default);

        overview.IsComplete.Should().BeTrue();
        overview.HasNoResults.Should().BeTrue();
        overview.Passing.Should().Be(0);
    }

    [Fact]
    public async Task Results_from_both_report_apis_are_combined()
    {
        // wgpolicyk8s.io is Kyverno's default; with OpenReports enabled it writes openreports.io instead.
        (Guid tenant, Guid env) = Seed("tenant-a", "acme-billing");
        await db.SaveChangesAsync();

        KyvernoReportOverview overview = await service.GetOverviewAsync(tenant, env,
            (_, _, _) => Task.FromResult(new NamespaceReports(true, [PerResourceReport, OpenReportsReport])), default);

        overview.Policies.Select(p => p.Policy).Should().Contain(["require-labels", "restrict-host-path"]);
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private const string ClusterName = "shared-cluster";

    private (Guid TenantId, Guid EnvId) Seed(string tenantSlug, params string[] namespaces)
    {
        Tenant tenant = new() { Id = Guid.NewGuid(), Name = tenantSlug, Slug = tenantSlug };
        Customer customer = new() { Id = Guid.NewGuid(), TenantId = tenant.Id, Name = $"{tenantSlug}-customer" };
        Data.Environment env = new() { Id = Guid.NewGuid(), TenantId = tenant.Id, Name = $"{tenantSlug}-prod" };
        db.Tenants.Add(tenant);
        db.Customers.Add(customer);
        db.Environments.Add(env);

        if (!db.KubernetesClusters.Local.Any(c => c.Id == sharedCluster))
        {
            db.KubernetesClusters.Add(new KubernetesCluster
            {
                Id = sharedCluster, TenantId = tenant.Id, EnvironmentId = env.Id,
                Name = ClusterName, ApiServerUrl = "https://cluster.example:6443"
            });
        }

        foreach (string ns in namespaces)
        {
            App app = new() { Id = Guid.NewGuid(), CustomerId = customer.Id, Name = $"{tenantSlug}-{ns}" };
            db.Apps.Add(app);
            db.AppDeployments.Add(new AppDeployment
            {
                Id = Guid.NewGuid(), AppId = app.Id, EnvironmentId = env.Id, ClusterId = sharedCluster,
                Name = $"{ns}-deployment", Namespace = ns
            });
        }

        return (tenant.Id, env.Id);
    }

    private static PolicyResultRow Row(string policy, string result) =>
        new("c", "ns", policy, "rule", result, null, null, "Deployment", "app", null);

    /// <summary>The shape Kyverno 1.18 writes: one report per resource, the resource in scope.</summary>
    private const string PerResourceReport = """
        {
          "apiVersion": "wgpolicyk8s.io/v1alpha2",
          "kind": "PolicyReportList",
          "metadata": {},
          "items": [
            {
              "apiVersion": "wgpolicyk8s.io/v1alpha2",
              "kind": "PolicyReport",
              "metadata": {
                "name": "6f1c8a2e-1b9e-4c7f-9d3a-2a4b5c6d7e8f",
                "namespace": "acme-billing",
                "labels": { "app.kubernetes.io/managed-by": "kyverno" }
              },
              "scope": {
                "apiVersion": "apps/v1", "kind": "Deployment", "name": "billing-api",
                "namespace": "acme-billing", "uid": "6f1c8a2e-1b9e-4c7f-9d3a-2a4b5c6d7e8f"
              },
              "results": [
                {
                  "message": "validation error: label 'team' is required. rule require-team failed at path /metadata/labels/team/",
                  "policy": "require-labels", "result": "fail", "rule": "require-team",
                  "scored": true, "severity": "medium", "source": "kyverno",
                  "timestamp": { "nanos": 0, "seconds": 1763600000 }
                },
                {
                  "message": "validation rule 'validate-image-tag' passed.",
                  "policy": "disallow-latest-tag", "result": "pass", "rule": "validate-image-tag",
                  "scored": true, "source": "kyverno",
                  "timestamp": { "nanos": 0, "seconds": 1763600000 }
                }
              ],
              "summary": { "error": 0, "fail": 1, "pass": 1, "skip": 0, "warn": 0 }
            }
          ]
        }
        """;

    /// <summary>The pre-1.10 shape: one report for the namespace, resources named on each result.</summary>
    private const string AggregatedReport = """
        {
          "items": [
            {
              "kind": "PolicyReport",
              "metadata": { "name": "polr-ns-acme-web", "namespace": "acme-web" },
              "results": [
                {
                  "policy": "disallow-latest-tag", "rule": "validate-image-tag", "result": "fail",
                  "message": "using a mutable image tag e.g. 'latest' is not allowed",
                  "resources": [
                    { "apiVersion": "v1", "kind": "Pod", "name": "web-1", "namespace": "acme-web" },
                    { "apiVersion": "v1", "kind": "Pod", "name": "web-2", "namespace": "acme-web" }
                  ]
                }
              ]
            }
          ]
        }
        """;

    /// <summary>openreports.io/v1alpha1 Report, which Kyverno writes when OpenReports is enabled.</summary>
    private const string OpenReportsReport = """
        {
          "apiVersion": "openreports.io/v1alpha1",
          "kind": "ReportList",
          "items": [
            {
              "apiVersion": "openreports.io/v1alpha1",
              "kind": "Report",
              "metadata": { "name": "a1b2", "namespace": "acme-billing" },
              "scope": { "apiVersion": "v1", "kind": "Pod", "name": "billing-api-7d9f", "namespace": "acme-billing" },
              "results": [
                {
                  "policy": "restrict-host-path", "rule": "host-path", "result": "warn",
                  "message": "hostPath volumes are discouraged", "source": "kyverno",
                  "timestamp": { "nanos": 0, "seconds": 1763600100 }
                }
              ],
              "summary": { "error": 0, "fail": 0, "pass": 0, "skip": 0, "warn": 1 }
            }
          ]
        }
        """;
}
