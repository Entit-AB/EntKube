using EntKube.Web.Data;
using EntKube.Web.Services;
using EntKube.Web.Services.Jit;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace EntKube.Web.Tests;

/// <summary>
/// The sweep that removes a finished grant's RBAC, and the one that finds objects the database
/// never heard of.
///
/// Expiry does not depend on this running — the bound token carries its own lifetime — so what is
/// asserted here is cleanup, and specifically that it fails safe: a cluster that cannot be reached
/// must leave the grant marked un-cleaned so the next pass retries, and a freshly minted grant must
/// survive a sweep that happens to land mid-mint.
/// </summary>
public class JitGrantReaperTests : IDisposable
{
    private readonly SqliteConnection connection;
    private readonly ApplicationDbContext db;
    private readonly FakeProvisioner provisioner = new();
    private readonly Mock<IKubernetesClientFactory> k8s = new();
    private readonly ServiceProvider services;
    private readonly JitGrantReaperService sut;

    private readonly Guid tenantId = Guid.NewGuid();
    private readonly Guid customerId = Guid.NewGuid();
    private readonly Guid appId = Guid.NewGuid();
    private readonly Guid envId = Guid.NewGuid();
    private readonly Guid clusterId = Guid.NewGuid();

    public JitGrantReaperTests()
    {
        connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();

        DbContextOptions<ApplicationDbContext> options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseSqlite(connection)
            .Options;

        db = new ApplicationDbContext(options);
        db.Database.EnsureCreated();
        Seed();

        k8s.Setup(f => f.GetJsonAllNamespacesAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("""{"items":[]}""");

        ServiceCollection collection = new();
        collection.AddSingleton<IDbContextFactory<ApplicationDbContext>>(new TestDbContextFactory(connection));
        collection.AddSingleton<IJitProvisioner>(provisioner);
        collection.AddSingleton(k8s.Object);
        services = collection.BuildServiceProvider();

        sut = new JitGrantReaperService(
            services.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<JitGrantReaperService>.Instance);
    }

    // ── Expired grants ────────────────────────────────────────────────────────

    [Fact]
    public async Task An_expired_grant_is_torn_down_and_marked()
    {
        JitGrant grant = AddGrant(expiresAt: DateTime.UtcNow.AddMinutes(-1));

        await sut.SweepAsync(default);

        provisioner.TornDown.Should().Equal(grant.Id);
        Reload(grant.Id).TornDownAt.Should().NotBeNull();
    }

    [Fact]
    public async Task A_live_grant_is_left_alone()
    {
        AddGrant(expiresAt: DateTime.UtcNow.AddHours(1));

        await sut.SweepAsync(default);

        provisioner.TornDown.Should().BeEmpty();
    }

    [Fact]
    public async Task A_pending_request_has_nothing_to_tear_down()
    {
        AddGrant(approvedAt: null, expiresAt: null);

        await sut.SweepAsync(default);

        provisioner.TornDown.Should().BeEmpty();
    }

    [Fact]
    public async Task An_already_reaped_grant_is_not_reaped_twice()
    {
        JitGrant grant = AddGrant(expiresAt: DateTime.UtcNow.AddMinutes(-1));

        await sut.SweepAsync(default);
        await sut.SweepAsync(default);

        provisioner.TornDown.Should().Equal(grant.Id);
    }

    [Fact]
    public async Task A_teardown_that_fails_is_retried_next_sweep()
    {
        // A cluster that is temporarily unreachable must not leave the grant marked clean —
        // that would strand its RBAC in the customer's namespace permanently.
        JitGrant grant = AddGrant(expiresAt: DateTime.UtcNow.AddMinutes(-1));
        provisioner.FailNext = true;

        await sut.SweepAsync(default);

        Reload(grant.Id).TornDownAt.Should().BeNull();

        await sut.SweepAsync(default);

        Reload(grant.Id).TornDownAt.Should().NotBeNull();
    }

    [Fact]
    public async Task One_failing_grant_does_not_stop_the_others()
    {
        provisioner.FailNext = true;
        AddGrant(expiresAt: DateTime.UtcNow.AddMinutes(-1));
        JitGrant second = AddGrant(expiresAt: DateTime.UtcNow.AddMinutes(-1));

        await sut.SweepAsync(default);

        provisioner.TornDown.Should().Contain(second.Id);
    }

    // ── Orphan selection ──────────────────────────────────────────────────────
    //
    // Driven directly rather than through a sweep, because a cluster's kubeconfig is materialized
    // from the vault by an interceptor that is not wired in tests — so the loop around this can
    // only be exercised against a live cluster, while the decision it makes can be exercised here.

    [Fact]
    public void An_object_no_grant_claims_is_an_orphan()
    {
        // The crash-mid-mint case: the manifest landed, the row never committed, and nothing in
        // the database will ever point at those objects again.
        Guid orphan = Guid.NewGuid();
        string json = ServiceAccountListJson(orphan, "acme-billing", Aged(TimeSpan.FromHours(1)));

        JitGrantReaperService.SelectOrphans(json, new HashSet<Guid>(), Cutoff())
            .Should().ContainSingle()
            .Which.Should().Be((orphan, "acme-billing"));
    }

    [Fact]
    public void A_claimed_object_is_left_alone()
    {
        Guid claimed = Guid.NewGuid();
        string json = ServiceAccountListJson(claimed, "acme-billing", Aged(TimeSpan.FromHours(1)));

        JitGrantReaperService.SelectOrphans(json, new HashSet<Guid> { claimed }, Cutoff())
            .Should().BeEmpty();
    }

    [Fact]
    public void A_freshly_created_object_is_inside_the_grace_window()
    {
        // The manifest lands a moment before the row commits. A sweep in that window must not
        // delete the RBAC of a grant that is about to become live.
        string json = ServiceAccountListJson(Guid.NewGuid(), "acme-billing", Aged(TimeSpan.Zero));

        JitGrantReaperService.SelectOrphans(json, new HashSet<Guid>(), Cutoff())
            .Should().BeEmpty();
    }

    [Fact]
    public void Orphans_and_live_objects_are_separated_in_one_pass()
    {
        Guid live = Guid.NewGuid();
        Guid orphan = Guid.NewGuid();
        string json = "{\"items\":["
            + ServiceAccountItemJson(live, "acme-billing", Aged(TimeSpan.FromHours(1)), false) + ","
            + ServiceAccountItemJson(orphan, "globex-api", Aged(TimeSpan.FromHours(1)), false)
            + "]}";

        JitGrantReaperService.SelectOrphans(json, new HashSet<Guid> { live }, Cutoff())
            .Should().ContainSingle().Which.Should().Be((orphan, "globex-api"));
    }

    private static DateTime Cutoff() => DateTime.UtcNow - JitGrantReaperService.OrphanGrace;

    private static string Aged(TimeSpan age) =>
        DateTime.UtcNow.Subtract(age).ToString("yyyy-MM-ddTHH:mm:ssZ");

    // ── Parsing ───────────────────────────────────────────────────────────────

    [Fact]
    public void Service_accounts_are_parsed_from_api_server_output()
    {
        Guid grantId = Guid.NewGuid();
        string json = ServiceAccountListJson(
            grantId, "acme-billing", "2026-09-15T10:00:00Z", withManagedLabel: true);

        var parsed = JitGrantReaperService.ParseJitServiceAccounts(json).ToList();

        parsed.Should().ContainSingle();
        parsed[0].GrantId.Should().Be(grantId);
        parsed[0].Namespace.Should().Be("acme-billing");
        parsed[0].CreatedAt.Should().Be(new DateTime(2026, 9, 15, 10, 0, 0, DateTimeKind.Utc));
    }

    [Theory]
    [InlineData("""{"items":[{"metadata":{"name":"x","namespace":"ns"}}]}""")]
    [InlineData("""{"items":[{"metadata":{"name":"x","namespace":"ns","labels":{}}}]}""")]
    [InlineData("""{"items":[{"metadata":{"name":"x","labels":{"entkube.io/jit-grant":"not-a-guid"}}}]}""")]
    public void An_object_that_cannot_be_traced_back_is_skipped(string json)
    {
        // Deleting something because its label looked wrong is worse than leaving it.
        JitGrantReaperService.ParseJitServiceAccounts(json).Should().BeEmpty();
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("{}")]
    [InlineData("""{"items":{}}""")]
    public void Unusable_output_yields_nothing_rather_than_throwing(string json)
    {
        // A sweep that throws on odd output stops reaping everything else on that cluster.
        JitGrantReaperService.ParseJitServiceAccounts(json).Should().BeEmpty();
    }

    [Fact]
    public void An_object_with_no_creation_timestamp_is_treated_as_new()
    {
        // It cannot be aged out of the grace window, so the safe reading is "leave it".
        Guid grantId = Guid.NewGuid();
        string json = ServiceAccountListJson(grantId, "ns", createdAt: null);

        var parsed = JitGrantReaperService.ParseJitServiceAccounts(json).ToList();

        parsed.Should().ContainSingle();
        parsed[0].CreatedAt.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromSeconds(5));
    }

    // ── Fixtures ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Shapes a ServiceAccount the way the API server returns one. Built by concatenation rather
    /// than a raw literal because the JSON's own closing braces fight raw-string interpolation.
    /// </summary>
    private static string ServiceAccountItemJson(
        Guid grantId, string ns, string? createdAt, bool withManagedLabel)
    {
        List<string> labels = [];
        if (withManagedLabel)
            labels.Add($"\"{JitRbacBuilder.ManagedLabel}\":\"{JitRbacBuilder.ManagedValue}\"");
        labels.Add($"\"{JitRbacBuilder.GrantLabel}\":\"{grantId}\"");

        List<string> metadata =
        [
            $"\"name\":\"{JitRbacBuilder.ServiceAccountName(grantId)}\"",
            $"\"namespace\":\"{ns}\"",
        ];
        if (createdAt is not null)
            metadata.Add($"\"creationTimestamp\":\"{createdAt}\"");
        metadata.Add("\"labels\":{" + string.Join(",", labels) + "}");

        return "{\"metadata\":{" + string.Join(",", metadata) + "}}";
    }

    private static string ServiceAccountListJson(
        Guid grantId, string ns, string? createdAt, bool withManagedLabel = false) =>
        "{\"items\":[" + ServiceAccountItemJson(grantId, ns, createdAt, withManagedLabel) + "]}";

    private JitGrant Reload(Guid id) => db.JitGrants.AsNoTracking().Single(g => g.Id == id);

    private JitGrant AddGrant(DateTime? expiresAt, DateTime? approvedAt = null)
    {
        JitGrant grant = new()
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId, CustomerId = customerId, AppId = appId,
            EnvironmentId = envId, KubernetesClusterId = clusterId,
            Namespace = "acme-billing",
            UserId = "customer@acme.example",
            Reason = "debugging",
            RequestedBy = "customer@acme.example",
            ApprovedAt = expiresAt is null && approvedAt is null ? null : approvedAt ?? DateTime.UtcNow.AddHours(-1),
            ExpiresAt = expiresAt,
        };

        db.JitGrants.Add(grant);
        db.SaveChanges();
        return grant;
    }

    private void Seed()
    {
        db.Tenants.Add(new Tenant { Id = tenantId, Name = "Entit", Slug = "entit" });
        db.Customers.Add(new Customer { Id = customerId, TenantId = tenantId, Name = "Acme" });
        db.Environments.Add(new EntKube.Web.Data.Environment
        {
            Id = envId, TenantId = tenantId, Name = "Production"
        });
        db.KubernetesClusters.Add(new KubernetesCluster
        {
            Id = clusterId, TenantId = tenantId, EnvironmentId = envId,
            Name = "primary", ApiServerUrl = "https://c:6443", Kubeconfig = "apiVersion: v1"
        });
        db.Apps.Add(new App { Id = appId, CustomerId = customerId, Name = "Billing API" });
        db.Users.Add(new ApplicationUser
        {
            Id = "customer@acme.example", UserName = "customer@acme.example"
        });
        db.SaveChanges();
    }

    public void Dispose()
    {
        services.Dispose();
        db.Dispose();
        connection.Dispose();
        GC.SuppressFinalize(this);
    }

    private sealed class FakeProvisioner : IJitProvisioner
    {
        public List<Guid> TornDown { get; } = [];
        public bool FailNext { get; set; }

        public Task<MintedCredential> MintAsync(
            JitGrant grant, TimeSpan duration, CancellationToken ct = default) =>
            throw new NotSupportedException("The reaper never mints.");

        public Task TearDownAsync(JitGrant grant, CancellationToken ct = default)
        {
            if (FailNext)
            {
                FailNext = false;
                throw new InvalidOperationException("cluster unreachable");
            }

            TornDown.Add(grant.Id);
            return Task.CompletedTask;
        }
    }
}
