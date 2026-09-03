using EntKube.Web.Data;
using Microsoft.AspNetCore.Http;
using EntKube.Web.Services.PublicApi;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace EntKube.Web.Tests;

/// <summary>
/// Tests for public API tokens. The security-critical properties are that the
/// plaintext is never recoverable, that revoked/expired tokens stop working, and
/// that a token can never reach another tenant.
/// </summary>
public class ApiTokenServiceTests : IDisposable
{
    private readonly SqliteConnection connection;
    private readonly ApplicationDbContext db;
    private readonly ApiTokenService sut;
    private readonly Guid tenantA = Guid.NewGuid();
    private readonly Guid tenantB = Guid.NewGuid();

    public ApiTokenServiceTests()
    {
        connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();

        DbContextOptions<ApplicationDbContext> options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseSqlite(connection)
            .Options;

        db = new ApplicationDbContext(options);
        db.Database.EnsureCreated();

        db.Tenants.Add(new Tenant { Id = tenantA, Name = "Tenant A", Slug = "tenant-a" });
        db.Tenants.Add(new Tenant { Id = tenantB, Name = "Tenant B", Slug = "tenant-b" });
        db.SaveChanges();

        sut = new ApiTokenService(new TestDbContextFactory(connection), NullLogger<ApiTokenService>.Instance);
    }

    public void Dispose()
    {
        db.Dispose();
        connection.Dispose();
        GC.SuppressFinalize(this);
    }

    private Task<CreatedApiToken> CreateAsync(
        Guid tenantId, params string[] scopes) =>
        sut.CreateAsync(tenantId, "test token", scopes.Length > 0 ? scopes : [ApiScopes.OpsRead],
            createdBy: "nils", expiresAt: null);

    // ── Minting ──

    [Fact]
    public async Task Mints_a_prefixed_token_and_stores_only_its_hash()
    {
        CreatedApiToken created = await CreateAsync(tenantA);

        created.Plaintext.Should().StartWith(ApiTokenService.TokenPrefix);
        created.Token.TokenHash.Should().Be(ApiTokenService.Hash(created.Plaintext));

        // The plaintext must not be recoverable from storage.
        ApiToken stored = await db.ApiTokens.AsNoTracking().SingleAsync();
        stored.TokenHash.Should().NotContain(created.Plaintext);
        stored.TokenHash.Should().HaveLength(64);
    }

    [Fact]
    public async Task Two_tokens_are_never_the_same()
    {
        CreatedApiToken first = await CreateAsync(tenantA);
        CreatedApiToken second = await CreateAsync(tenantA);

        second.Plaintext.Should().NotBe(first.Plaintext);
    }

    [Fact]
    public async Task Stores_a_display_prefix_that_identifies_without_revealing()
    {
        CreatedApiToken created = await CreateAsync(tenantA);

        created.Token.DisplayPrefix.Should().HaveLength(12);
        created.Plaintext.Should().StartWith(created.Token.DisplayPrefix);
        created.Token.DisplayPrefix.Length.Should().BeLessThan(created.Plaintext.Length);
    }

    [Fact]
    public async Task Refuses_to_mint_a_token_with_no_usable_scopes()
    {
        // A scopeless token can do nothing but would sit in the list looking like access.
        Func<Task> act = () => sut.CreateAsync(tenantA, "bad", ["not:a:scope"], null, null);
        await act.Should().ThrowAsync<ArgumentException>();

        Func<Task> empty = () => sut.CreateAsync(tenantA, "bad", [], null, null);
        await empty.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task Refuses_to_mint_a_token_with_no_name()
    {
        Func<Task> act = () => sut.CreateAsync(tenantA, "  ", [ApiScopes.OpsRead], null, null);
        await act.Should().ThrowAsync<ArgumentException>();
    }

    // ── Validation ──

    [Fact]
    public async Task Validates_a_live_token_and_returns_its_tenant_and_scopes()
    {
        CreatedApiToken created = await CreateAsync(tenantA, ApiScopes.FleetRead, ApiScopes.OpsRead);

        ApiTokenPrincipal? principal = await sut.ValidateAsync(created.Plaintext);

        principal.Should().NotBeNull();
        principal!.TenantId.Should().Be(tenantA);
        principal.HasScope(ApiScopes.FleetRead).Should().BeTrue();
        principal.HasScope(ApiScopes.OpsRead).Should().BeTrue();
        principal.HasScope(ApiScopes.AppsWrite).Should().BeFalse();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-a-token")]
    [InlineData("Bearer something")]
    public async Task Rejects_anything_that_is_not_a_token(string? presented)
    {
        (await sut.ValidateAsync(presented)).Should().BeNull();
    }

    [Fact]
    public async Task Rejects_a_well_formed_token_that_was_never_issued()
    {
        (await sut.ValidateAsync(ApiTokenService.TokenPrefix + "AAAAAAAAAAAAAAAAAAAAAA")).Should().BeNull();
    }

    [Fact]
    public async Task A_revoked_token_stops_working_immediately()
    {
        CreatedApiToken created = await CreateAsync(tenantA);
        (await sut.ValidateAsync(created.Plaintext)).Should().NotBeNull();

        (await sut.RevokeAsync(tenantA, created.Token.Id)).Should().BeTrue();

        (await sut.ValidateAsync(created.Plaintext)).Should().BeNull();
    }

    [Fact]
    public async Task An_expired_token_stops_working()
    {
        CreatedApiToken created = await sut.CreateAsync(
            tenantA, "expired", [ApiScopes.OpsRead], null,
            expiresAt: DateTime.UtcNow.AddSeconds(-1));

        (await sut.ValidateAsync(created.Plaintext)).Should().BeNull();
    }

    [Fact]
    public async Task A_token_expiring_in_the_future_still_works()
    {
        CreatedApiToken created = await sut.CreateAsync(
            tenantA, "future", [ApiScopes.OpsRead], null,
            expiresAt: DateTime.UtcNow.AddHours(1));

        (await sut.ValidateAsync(created.Plaintext)).Should().NotBeNull();
    }

    [Fact]
    public async Task Validation_stamps_last_used()
    {
        CreatedApiToken created = await CreateAsync(tenantA);
        (await db.ApiTokens.AsNoTracking().SingleAsync()).LastUsedAt.Should().BeNull();

        await sut.ValidateAsync(created.Plaintext);

        (await db.ApiTokens.AsNoTracking().SingleAsync()).LastUsedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task An_unknown_scope_stored_on_a_token_is_not_granted()
    {
        // A scope this build cannot enforce must not be treated as held, or it would
        // grant access that no check governs.
        CreatedApiToken created = await CreateAsync(tenantA);
        ApiToken token = await db.ApiTokens.SingleAsync(t => t.Id == created.Token.Id);
        token.Scopes = "ops:read future:superpower";
        await db.SaveChangesAsync();

        ApiTokenPrincipal? principal = await sut.ValidateAsync(created.Plaintext);

        principal!.Scopes.Should().BeEquivalentTo([ApiScopes.OpsRead]);
        principal.HasScope("future:superpower").Should().BeFalse();
    }

    // ── Tenant isolation ──

    [Fact]
    public async Task A_token_resolves_only_to_the_tenant_it_was_minted_for()
    {
        CreatedApiToken tokenForA = await CreateAsync(tenantA);
        CreatedApiToken tokenForB = await CreateAsync(tenantB);

        (await sut.ValidateAsync(tokenForA.Plaintext))!.TenantId.Should().Be(tenantA);
        (await sut.ValidateAsync(tokenForB.Plaintext))!.TenantId.Should().Be(tenantB);
    }

    [Fact]
    public async Task A_tenant_cannot_revoke_another_tenants_token()
    {
        CreatedApiToken tokenForA = await CreateAsync(tenantA);

        (await sut.RevokeAsync(tenantB, tokenForA.Token.Id)).Should().BeFalse();

        // Still live — the failed revoke must not have touched it.
        (await sut.ValidateAsync(tokenForA.Plaintext)).Should().NotBeNull();
    }

    [Fact]
    public async Task Listing_returns_only_the_tenants_own_tokens()
    {
        await CreateAsync(tenantA);
        await CreateAsync(tenantA);
        await CreateAsync(tenantB);

        (await sut.ListAsync(tenantA)).Should().HaveCount(2);
        (await sut.ListAsync(tenantB)).Should().HaveCount(1);
    }

    [Fact]
    public async Task Revoking_keeps_the_row_so_the_audit_trail_survives()
    {
        CreatedApiToken created = await CreateAsync(tenantA);
        await sut.RevokeAsync(tenantA, created.Token.Id);

        List<ApiToken> tokens = await sut.ListAsync(tenantA);

        tokens.Should().ContainSingle();
        tokens[0].RevokedAt.Should().NotBeNull();
        tokens[0].CreatedBy.Should().Be("nils");
    }

    [Fact]
    public async Task Revoking_an_already_revoked_token_is_a_no_op()
    {
        CreatedApiToken created = await CreateAsync(tenantA);
        (await sut.RevokeAsync(tenantA, created.Token.Id)).Should().BeTrue();
        (await sut.RevokeAsync(tenantA, created.Token.Id)).Should().BeFalse();
    }

    [Fact]
    public async Task Deleting_a_tenant_removes_its_tokens()
    {
        // A deleted tenant must not leave working credentials behind.
        CreatedApiToken created = await CreateAsync(tenantA);

        db.Tenants.Remove(await db.Tenants.SingleAsync(t => t.Id == tenantA));
        await db.SaveChangesAsync();

        (await sut.ValidateAsync(created.Plaintext)).Should().BeNull();
    }

    // ── Header extraction ──

    [Fact]
    public void Extracts_a_bearer_token_from_the_authorization_header()
    {
        DefaultHttpContext ctx = new();
        ctx.Request.Headers.Authorization = "Bearer ekp_abc123";

        ApiTokenService.ExtractToken(ctx.Request).Should().Be("ekp_abc123");
    }

    [Fact]
    public void Bearer_scheme_matching_is_case_insensitive()
    {
        DefaultHttpContext ctx = new();
        ctx.Request.Headers.Authorization = "bearer ekp_abc123";

        ApiTokenService.ExtractToken(ctx.Request).Should().Be("ekp_abc123");
    }

    [Theory]
    [InlineData("Basic dXNlcjpwYXNz")]
    [InlineData("ekp_abc123")]
    [InlineData("")]
    public void Ignores_non_bearer_authorization_headers(string header)
    {
        DefaultHttpContext ctx = new();
        ctx.Request.Headers.Authorization = header;

        ApiTokenService.ExtractToken(ctx.Request).Should().BeNull();
    }

    // ── Scope vocabulary ──

    [Fact]
    public void Serializing_scopes_drops_unknown_ones_and_deduplicates()
    {
        ApiScopes.Serialize([ApiScopes.OpsRead, "bogus", ApiScopes.OpsRead, ApiScopes.FleetRead])
            .Should().Be($"{ApiScopes.FleetRead} {ApiScopes.OpsRead}");
    }

    [Fact]
    public void Every_scope_has_a_description_for_the_grant_ui()
    {
        foreach (string scope in ApiScopes.All)
        {
            ApiScopes.Describe(scope).Should().NotBe(scope);
        }
    }

    [Fact]
    public void Read_and_write_are_always_distinct_scopes()
    {
        // The common integration needs no write access; granting it implicitly would be wrong.
        ApiScopes.All.Should().Contain(ApiScopes.AppsRead).And.Contain(ApiScopes.AppsWrite);
        ApiScopes.Parse(ApiScopes.AppsRead).Should().NotContain(ApiScopes.AppsWrite);
    }
}
