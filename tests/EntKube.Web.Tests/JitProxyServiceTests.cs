using System.Net;
using EntKube.Web.Data;
using EntKube.Web.Services;
using EntKube.Web.Services.Jit;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace EntKube.Web.Tests;

/// <summary>
/// The proxy's gate: which requests get as far as an upstream call at all.
///
/// The forwarding itself needs a live API server, so what is exercised here is everything that
/// happens before it — because that is where a mistake hands a customer something. A request that
/// is refused never reaches the cluster; a request that is accepted has already had its namespace
/// checked twice, once here and once by the grant's RoleBinding.
/// </summary>
public class JitProxyServiceTests : IDisposable
{
    private readonly SqliteConnection connection;
    private readonly ApplicationDbContext db;
    private readonly JitUpstreamClientPool pool;
    private readonly JitProxyService sut;

    private readonly Guid tenantId = Guid.NewGuid();
    private readonly Guid customerId = Guid.NewGuid();
    private readonly Guid appId = Guid.NewGuid();
    private readonly Guid envId = Guid.NewGuid();
    private readonly Guid clusterId = Guid.NewGuid();
    private const string Ns = "acme-billing";
    private const string User = "customer@acme.example";

    private readonly VaultEncryptionService encryption =
        new(Convert.FromHexString(new string('a', 64)));

    public JitProxyServiceTests()
    {
        connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();

        DbContextOptions<ApplicationDbContext> options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseSqlite(connection)
            .Options;

        db = new ApplicationDbContext(options);
        db.Database.EnsureCreated();
        Seed();

        TestDbContextFactory factory = new(connection);
        pool = new JitUpstreamClientPool(NullLogger<JitUpstreamClientPool>.Instance);
        sut = new JitProxyService(
            factory, pool, encryption, new AuditService(factory), NullLogger<JitProxyService>.Instance);
    }

    // ── Authentication ────────────────────────────────────────────────────────

    [Fact]
    public async Task A_request_with_no_token_is_unauthorized()
    {
        JitGrant grant = AddLiveGrant(out _);

        HttpContext http = Request("GET", token: null);
        await sut.HandleAsync(http, grant.Id, $"api/v1/namespaces/{Ns}/pods");

        http.Response.StatusCode.Should().Be((int)HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task A_wrong_token_is_unauthorized()
    {
        JitGrant grant = AddLiveGrant(out _);

        HttpContext http = Request("GET", "ekj_wrong");
        await sut.HandleAsync(http, grant.Id, $"api/v1/namespaces/{Ns}/pods");

        http.Response.StatusCode.Should().Be((int)HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task A_token_cannot_be_pointed_at_a_different_grant()
    {
        // The grant id lives in the URL, so it is attacker-controlled: editing one character of a
        // kubeconfig must not turn a valid token into access to somebody else's grant.
        AddLiveGrant(out string tokenForFirst);
        JitGrant second = AddLiveGrant(out _, ns: "globex-api");

        HttpContext http = Request("GET", tokenForFirst);
        await sut.HandleAsync(http, second.Id, "api/v1/namespaces/globex-api/pods");

        http.Response.StatusCode.Should().Be((int)HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task An_unknown_grant_is_unauthorized()
    {
        AddLiveGrant(out string token);

        HttpContext http = Request("GET", token);
        await sut.HandleAsync(http, Guid.NewGuid(), $"api/v1/namespaces/{Ns}/pods");

        http.Response.StatusCode.Should().Be((int)HttpStatusCode.Unauthorized);
    }

    // ── Liveness ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task An_expired_grant_is_refused()
    {
        JitGrant grant = AddLiveGrant(out string token);
        grant.ExpiresAt = DateTime.UtcNow.AddMinutes(-1);
        db.SaveChanges();

        HttpContext http = Request("GET", token);
        await sut.HandleAsync(http, grant.Id, $"api/v1/namespaces/{Ns}/pods");

        http.Response.StatusCode.Should().Be((int)HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task A_revoked_grant_is_refused_immediately()
    {
        // This is what makes revocation instant. The bound ServiceAccount token is still valid to
        // the cluster for the rest of its life — nothing can invalidate it — so the row is what
        // has to stop the request, and it has to stop it here.
        JitGrant grant = AddLiveGrant(out string token);
        grant.RevokedAt = DateTime.UtcNow;
        db.SaveChanges();

        HttpContext http = Request("GET", token);
        await sut.HandleAsync(http, grant.Id, $"api/v1/namespaces/{Ns}/pods");

        http.Response.StatusCode.Should().Be((int)HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task A_pending_request_is_not_a_credential()
    {
        JitGrant grant = AddLiveGrant(out string token);
        grant.ApprovedAt = null;
        db.SaveChanges();

        HttpContext http = Request("GET", token);
        await sut.HandleAsync(http, grant.Id, $"api/v1/namespaces/{Ns}/pods");

        http.Response.StatusCode.Should().Be((int)HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Every_refusal_looks_the_same()
    {
        // Distinguishing "no such grant" from "revoked" from "wrong token" would confirm which
        // guesses were closer, and the fact that a grant exists is itself worth not confirming.
        JitGrant revoked = AddLiveGrant(out string revokedToken);
        revoked.RevokedAt = DateTime.UtcNow;
        db.SaveChanges();

        string unknownBody = await BodyOf(Guid.NewGuid(), "ekj_nope");
        string revokedBody = await BodyOf(revoked.Id, revokedToken);

        revokedBody.Should().Be(unknownBody);
    }

    // ── Namespace confinement ─────────────────────────────────────────────────

    [Fact]
    public async Task Another_namespace_is_forbidden_not_unauthorized()
    {
        // 403, not 401: the caller is known and their credential is fine — what they asked for is
        // outside the grant. Answering 401 would send kubectl looking for better credentials.
        JitGrant grant = AddLiveGrant(out string token);

        HttpContext http = Request("GET", token);
        await sut.HandleAsync(http, grant.Id, "api/v1/namespaces/globex-api/pods");

        http.Response.StatusCode.Should().Be((int)HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task A_cluster_wide_list_is_forbidden()
    {
        JitGrant grant = AddLiveGrant(out string token);

        HttpContext http = Request("GET", token);
        await sut.HandleAsync(http, grant.Id, "api/v1/pods");

        http.Response.StatusCode.Should().Be((int)HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task A_refusal_is_recorded_against_the_person()
    {
        JitGrant grant = AddLiveGrant(out string token);

        HttpContext http = Request("GET", token);
        await sut.HandleAsync(http, grant.Id, "api/v1/nodes");

        AuditEvent refusal = db.AuditEvents.AsNoTracking()
            .Single(e => e.Action == "JitAccessRefused");

        refusal.PerformedBy.Should().Be(User);
        refusal.ResourceName.Should().Be(grant.Id.ToString());
        refusal.Details.Should().Contain("nodes");
    }

    [Fact]
    public async Task An_expired_grant_is_refused_before_its_path_is_even_considered()
    {
        // Ordering matters: a dead grant must not produce a 403 that reveals the namespace it
        // used to cover.
        JitGrant grant = AddLiveGrant(out string token);
        grant.RevokedAt = DateTime.UtcNow;
        db.SaveChanges();

        HttpContext http = Request("GET", token);
        await sut.HandleAsync(http, grant.Id, "api/v1/namespaces/globex-api/pods");

        http.Response.StatusCode.Should().Be((int)HttpStatusCode.Unauthorized);
        db.AuditEvents.Should().BeEmpty();
    }

    // ── Credential state ──────────────────────────────────────────────────────

    [Fact]
    public async Task A_grant_whose_credential_was_destroyed_cannot_be_used()
    {
        // Teardown clears the stored bound token. A grant row that outlives it must not still
        // reach the cluster.
        JitGrant grant = AddLiveGrant(out string token);
        grant.EncryptedClusterToken = null;
        grant.ClusterTokenNonce = null;
        grant.ClusterTokenKey = null;
        grant.ClusterTokenKeyNonce = null;
        db.SaveChanges();

        HttpContext http = Request("GET", token);
        await sut.HandleAsync(http, grant.Id, $"api/v1/namespaces/{Ns}/pods");

        http.Response.StatusCode.Should().Be((int)HttpStatusCode.Unauthorized);
    }

    [Fact]
    public void The_stored_cluster_token_round_trips_but_is_not_in_the_clear()
    {
        JitGrant grant = AddLiveGrant(out _);

        JitGrant stored = db.JitGrants.AsNoTracking().Single(g => g.Id == grant.Id);

        JitClusterTokenStore.Read(encryption, stored).Should().Be("bound-sa-token");
        System.Text.Encoding.UTF8.GetString(stored.EncryptedClusterToken!)
            .Should().NotContain("bound-sa-token");
    }

    // ── Token extraction ──────────────────────────────────────────────────────

    [Theory]
    [InlineData("Bearer ekj_abc", "ekj_abc")]
    [InlineData("bearer ekj_abc", "ekj_abc")]
    [InlineData("Bearer   ekj_abc  ", "ekj_abc")]
    public void A_bearer_token_is_read_from_the_header(string header, string expected)
    {
        DefaultHttpContext http = new();
        http.Request.Headers.Authorization = header;

        JitProxyService.ExtractToken(http.Request).Should().Be(expected);
    }

    [Theory]
    [InlineData("Basic dXNlcjpwYXNz")]
    [InlineData("ekj_abc")]
    [InlineData("")]
    public void Anything_that_is_not_a_bearer_token_is_ignored(string header)
    {
        DefaultHttpContext http = new();
        http.Request.Headers.Authorization = header;

        JitProxyService.ExtractToken(http.Request).Should().BeNull();
    }

    // ── Fixtures ──────────────────────────────────────────────────────────────

    private async Task<string> BodyOf(Guid grantId, string token)
    {
        HttpContext http = Request("GET", token);
        await sut.HandleAsync(http, grantId, $"api/v1/namespaces/{Ns}/pods");

        http.Response.Body.Position = 0;
        return await new StreamReader(http.Response.Body).ReadToEndAsync();
    }

    private static DefaultHttpContext Request(string method, string? token)
    {
        DefaultHttpContext http = new();
        http.Request.Method = method;
        http.Response.Body = new MemoryStream();
        if (token is not null) http.Request.Headers.Authorization = $"Bearer {token}";
        return http;
    }

    private JitGrant AddLiveGrant(out string plaintext, string ns = Ns)
    {
        plaintext = $"ekj_{Guid.NewGuid():N}";

        byte[] dataKey = encryption.GenerateDataKey();
        (byte[] sealedKey, byte[] keyNonce) = encryption.SealDataKey(dataKey);
        (byte[] ciphertext, byte[] nonce) = encryption.Encrypt(dataKey, "bound-sa-token");

        JitGrant grant = new()
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId, CustomerId = customerId, AppId = appId,
            EnvironmentId = envId, KubernetesClusterId = clusterId,
            Namespace = ns,
            UserId = User, Reason = "debugging", RequestedBy = User,
            ApprovedAt = DateTime.UtcNow.AddMinutes(-5),
            ApprovedBy = "operator@entit.se",
            ExpiresAt = DateTime.UtcNow.AddHours(1),
            ServiceAccountName = "jit-test",
            TokenHash = KubernetesJitProvisioner.Hash(plaintext),
            DisplayPrefix = plaintext[..12],
            ClusterTokenKey = sealedKey,
            ClusterTokenKeyNonce = keyNonce,
            EncryptedClusterToken = ciphertext,
            ClusterTokenNonce = nonce,
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
            Name = "primary", ApiServerUrl = "https://c:6443"
        });
        db.Apps.Add(new App { Id = appId, CustomerId = customerId, Name = "Billing API" });
        db.Users.Add(new ApplicationUser { Id = User, UserName = User });
        db.SaveChanges();
    }

    public void Dispose()
    {
        pool.Dispose();
        db.Dispose();
        connection.Dispose();
        GC.SuppressFinalize(this);
    }
}
