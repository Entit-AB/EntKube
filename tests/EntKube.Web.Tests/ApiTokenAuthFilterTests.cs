using EntKube.Web.Data;
using EntKube.Web.Services.PublicApi;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace EntKube.Web.Tests;

/// <summary>
/// Tests the endpoint filter that guards every public API route: that an unauthenticated
/// caller is refused, that a token missing the route's scope is refused, and that the
/// authenticated principal reaches the handler.
/// </summary>
public class ApiTokenAuthFilterTests : IDisposable
{
    private readonly SqliteConnection connection;
    private readonly ApplicationDbContext db;
    private readonly ApiTokenService tokenService;
    private readonly ServiceProvider services;
    private readonly Guid tenantId = Guid.NewGuid();

    public ApiTokenAuthFilterTests()
    {
        connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();

        DbContextOptions<ApplicationDbContext> options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseSqlite(connection)
            .Options;

        db = new ApplicationDbContext(options);
        db.Database.EnsureCreated();
        db.Tenants.Add(new Tenant { Id = tenantId, Name = "T", Slug = "t" });
        db.SaveChanges();

        tokenService = new ApiTokenService(
            new TestDbContextFactory(connection), NullLogger<ApiTokenService>.Instance);

        ServiceCollection collection = new();
        collection.AddSingleton(tokenService);
        // Results.Problem(...) resolves ILoggerFactory when it executes, so the fake
        // request services must carry one or rendering the result throws.
        collection.AddLogging();
        services = collection.BuildServiceProvider();
    }

    public void Dispose()
    {
        services.Dispose();
        db.Dispose();
        connection.Dispose();
        GC.SuppressFinalize(this);
    }

    /// <summary>Runs the filter over a request, returning what it produced and whether the handler ran.</summary>
    private async Task<(object? Result, bool HandlerRan, HttpContext Context)> InvokeAsync(
        string? requiredScope, string? bearer)
    {
        DefaultHttpContext http = new() { RequestServices = services };
        if (bearer is not null)
        {
            http.Request.Headers.Authorization = $"Bearer {bearer}";
        }

        EndpointFilterInvocationContext context =
            EndpointFilterInvocationContext.Create(http);

        bool handlerRan = false;
        ApiTokenAuthFilter filter = new(requiredScope);

        object? result = await filter.InvokeAsync(context, _ =>
        {
            handlerRan = true;
            return ValueTask.FromResult<object?>(Results.Ok("handler output"));
        });

        return (result, handlerRan, http);
    }

    /// <summary>Renders a minimal-API result to its status code without a live server.</summary>
    private static async Task<int> StatusOfAsync(object? result, HttpContext http)
    {
        if (result is IResult r)
        {
            http.Response.Body = new MemoryStream();
            await r.ExecuteAsync(http);
            return http.Response.StatusCode;
        }

        return 0;
    }

    [Fact]
    public async Task A_request_with_no_token_is_refused_and_the_handler_never_runs()
    {
        (object? result, bool handlerRan, HttpContext http) = await InvokeAsync(ApiScopes.OpsRead, bearer: null);

        handlerRan.Should().BeFalse();
        (await StatusOfAsync(result, http)).Should().Be(StatusCodes.Status401Unauthorized);
    }

    [Fact]
    public async Task An_unauthenticated_refusal_advertises_bearer_auth()
    {
        (object? _, bool _, HttpContext http) = await InvokeAsync(ApiScopes.OpsRead, bearer: null);

        http.Response.Headers.WWWAuthenticate.ToString().Should().Contain("Bearer");
    }

    [Fact]
    public async Task An_unknown_token_is_refused()
    {
        (object? result, bool handlerRan, HttpContext http) =
            await InvokeAsync(ApiScopes.OpsRead, bearer: "ekp_nope");

        handlerRan.Should().BeFalse();
        (await StatusOfAsync(result, http)).Should().Be(StatusCodes.Status401Unauthorized);
    }

    [Fact]
    public async Task A_revoked_token_is_refused_like_an_unknown_one()
    {
        CreatedApiToken created = await tokenService.CreateAsync(
            tenantId, "t", [ApiScopes.OpsRead], null, null);
        await tokenService.RevokeAsync(tenantId, created.Token.Id);

        (object? result, bool handlerRan, HttpContext http) =
            await InvokeAsync(ApiScopes.OpsRead, created.Plaintext);

        handlerRan.Should().BeFalse();
        (await StatusOfAsync(result, http)).Should().Be(StatusCodes.Status401Unauthorized);
    }

    [Fact]
    public async Task A_valid_token_missing_the_scope_is_forbidden_not_unauthorized()
    {
        // 403 rather than 401/404: the caller is known, so hiding the route buys nothing
        // and would make a scope mistake very hard to debug.
        CreatedApiToken created = await tokenService.CreateAsync(
            tenantId, "read only", [ApiScopes.OpsRead], null, null);

        (object? result, bool handlerRan, HttpContext http) =
            await InvokeAsync(ApiScopes.AppsWrite, created.Plaintext);

        handlerRan.Should().BeFalse();
        (await StatusOfAsync(result, http)).Should().Be(StatusCodes.Status403Forbidden);
    }

    [Fact]
    public async Task A_valid_token_with_the_scope_reaches_the_handler()
    {
        CreatedApiToken created = await tokenService.CreateAsync(
            tenantId, "writer", [ApiScopes.AppsWrite], null, null);

        (object? _, bool handlerRan, HttpContext _) =
            await InvokeAsync(ApiScopes.AppsWrite, created.Plaintext);

        handlerRan.Should().BeTrue();
    }

    [Fact]
    public async Task The_authenticated_principal_is_available_to_the_handler()
    {
        CreatedApiToken created = await tokenService.CreateAsync(
            tenantId, "ci", [ApiScopes.FleetRead], null, null);

        (object? _, bool _, HttpContext http) =
            await InvokeAsync(ApiScopes.FleetRead, created.Plaintext);

        ApiTokenPrincipal? principal = http.GetApiPrincipal();
        principal.Should().NotBeNull();
        principal!.TenantId.Should().Be(tenantId);
        principal.TokenName.Should().Be("ci");
    }

    [Fact]
    public async Task A_route_with_no_required_scope_still_requires_authentication()
    {
        (object? result, bool handlerRan, HttpContext http) = await InvokeAsync(null, bearer: null);

        handlerRan.Should().BeFalse();
        (await StatusOfAsync(result, http)).Should().Be(StatusCodes.Status401Unauthorized);
    }

    [Fact]
    public async Task A_route_with_no_required_scope_accepts_any_live_token()
    {
        CreatedApiToken created = await tokenService.CreateAsync(
            tenantId, "any", [ApiScopes.OpsRead], null, null);

        (object? _, bool handlerRan, HttpContext _) = await InvokeAsync(null, created.Plaintext);

        handlerRan.Should().BeTrue();
    }
}
