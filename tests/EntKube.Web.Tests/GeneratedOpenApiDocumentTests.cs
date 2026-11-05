using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.RegularExpressions;
using EntKube.Web.Data;
using EntKube.Web.Modules;
using EntKube.Web.Services.PublicApi;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.OpenApi;

namespace EntKube.Web.Tests;

/// <summary>
/// Boots the real application once, generates the real OpenAPI document, and reads the real
/// route table out of the router — so the assertions in
/// <see cref="GeneratedOpenApiDocumentTests"/> compare the document against the API as the
/// application actually serves it, not against a regex over the source.
/// </summary>
public sealed class GeneratedOpenApiDocumentFixture : IDisposable
{
    private readonly string _tempDir =
        Path.Combine(Path.GetTempPath(), "openapi-doc-" + Guid.NewGuid().ToString("N"));

    private readonly WebApplicationFactory<Program> _factory;

    /// <summary>The generated document, or null when generation threw.</summary>
    public OpenApiDocument? Document { get; }

    /// <summary>The exception generation threw, if it did. See the class remarks on why this is captured.</summary>
    public Exception? GenerationFailure { get; }

    /// <summary>Every operation in the document, as (METHOD, path, description, security-requirement count).</summary>
    public IReadOnlyList<DocumentedOperation> Operations { get; } = [];

    /// <summary>
    /// Every route the application actually serves that went through <c>RequireApiScope</c>,
    /// as (METHOD, path) with route constraints stripped so it is comparable to a document path.
    /// </summary>
    public IReadOnlyList<(string Method, string Path)> ScopedRoutes { get; } = [];

    /// <summary>
    /// A live API token holding exactly one scope, keyed by that scope — so a request can be made
    /// as a caller who holds precisely what the document claims is needed, and as one who does not.
    /// </summary>
    public IReadOnlyDictionary<string, string> TokenPerScope { get; } = new Dictionary<string, string>();

    /// <summary>
    /// The booted host's services, for a suite that has to seed a row before it can ask a
    /// question. Resolve an <c>IDbContextFactory</c> from it rather than holding a context.
    /// </summary>
    /// <remarks>
    /// ⚠️ The database behind this is SHARED by every suite in the collection and is already
    /// mutated by one of them: the scope probe below calls
    /// <c>POST /advisor/findings/{id}/acknowledge</c>, which inserts an <c>AdvisorFindingState</c>
    /// row for <see cref="TokenTenantId"/> keyed by a random GUID on every run. Classes within a
    /// collection run sequentially but in an unspecified order, so a seeding suite must assume
    /// something ran before it and something will run after. Never assert a count or an emptiness
    /// over this tenant that a sibling suite could change.
    /// </remarks>
    public IServiceProvider Services { get; } = null!;

    /// <summary>
    /// The tenant every token in <see cref="TokenPerScope"/> belongs to — so a suite can seed a
    /// row that those tokens legitimately own, and a second tenant's row that they must not reach.
    /// </summary>
    public Guid TokenTenantId { get; }

    /// <summary>
    /// Every scoped route as <c>"VERB /full/path"</c> with route parameters flattened to
    /// <c>{}</c> — the comparison key the client-parity ratchets use.
    ///
    /// <para>⚠️ <b>Read out of the ROUTER, which is the point.</b> Both parity ratchets previously
    /// regexed <c>api.Map(Get|Post|Put|Delete)("…")</c> out of one source file. That cannot see a
    /// route behind a sub-group, a route registered through a receiver not named <c>api</c>, a
    /// template held in a const, a <c>MapPatch</c> or a <c>MapMethods</c> — and it could not see
    /// <c>/scim/v2</c> at all, because that is a different file. Asking the thing that serves
    /// requests is immune to all of those at once.</para>
    ///
    /// <para>The parameter NAME is deliberately discarded. A route's parameter is called
    /// <c>{clusterId}</c> and a client's variable is called whatever its author liked; requiring
    /// the two files to agree character for character would make this ratchet fail on a rename
    /// that changes nothing an operator can observe.</para>
    /// </summary>
    public IReadOnlyList<string> ApiSurface { get; } = [];

    /// <summary>Sends a request to a documented operation as the holder of <paramref name="scope"/>.</summary>
    public async Task<HttpStatusCode> CallAsync(DocumentedOperation operation, string scope)
    {
        // The document writes `{clusterId}`; the server wants a value. Any value will do: the
        // scope check runs before the handler looks anything up, so a row that does not exist
        // produces a 404 — which is still, decisively, not a 403.
        string url = Regex.Replace(operation.Path, @"\{[^}]+\}", _ => Guid.NewGuid().ToString());

        // ⚠️ And a required query parameter must be supplied, for the same reason the stub body
        // below exists: binding runs first, so omitting `workload` on a deployment restart answers
        // 400 and the scope check never happens. The document is asked which parameters those are,
        // rather than this test keeping its own list of them.
        if (operation.RequiredQueryParameters.Count > 0)
        {
            url += (url.Contains('?') ? "&" : "?") + string.Join(
                '&', operation.RequiredQueryParameters.Select(
                    name => $"{Uri.EscapeDataString(name)}={Guid.NewGuid()}"));
        }

        using HttpRequestMessage request = new(new HttpMethod(operation.Method), url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", TokenPerScope[scope]);

        if (operation.Method is not ("GET" or "DELETE" or "OPTIONS" or "HEAD"))
        {
            // ⚠️ Parameter binding runs BEFORE the endpoint filter pipeline, so a write route whose
            // body fails to bind answers 400 without the scope check ever running — and the whole
            // request would prove nothing. An empty JSON object binds to these DTOs.
            request.Content = new StringContent("{}", Encoding.UTF8, "application/json");
        }

        using HttpResponseMessage response = await _client.SendAsync(request);
        return response.StatusCode;
    }

    /// <summary>
    /// Sends one request exactly as given, with no stub body and no parameter filling, as the
    /// holder of <paramref name="scope"/>. For asserting what the server does with a request a
    /// client could not make — see <c>McpEndpointParityTests</c>'s bodyless-PUT check.
    /// </summary>
    public async Task<HttpStatusCode> SendRawAsync(
        string method, string url, string scope, string? body)
    {
        using HttpRequestMessage request = new(new HttpMethod(method), url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", TokenPerScope[scope]);

        if (body is not null)
        {
            request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        }

        using HttpResponseMessage response = await _client.SendAsync(request);
        return response.StatusCode;
    }

    /// <summary>
    /// As <see cref="SendRawAsync"/>, returning the response body as well as the status.
    ///
    /// <para>Needed because one operation's confinement cannot be seen in a status at all:
    /// <c>GET /deployments?appId=</c> takes the id as a FILTER, and a filter that matches nothing
    /// is an empty 200, not a refusal.</para>
    /// </summary>
    public async Task<string> SendForBodyAsync(string method, string url, string scope, string? body)
    {
        using HttpRequestMessage request = new(new HttpMethod(method), url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", TokenPerScope[scope]);

        if (body is not null)
        {
            request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        }

        using HttpResponseMessage response = await _client.SendAsync(request);
        return await response.Content.ReadAsStringAsync();
    }

    /// <summary>
    /// The whole answer — status, media type and body — for a route called as the holder of
    /// <paramref name="scope"/>.
    ///
    /// <para>⚠️ <b>The media type is why this exists.</b> <see cref="SendForBodyAsync"/> returns
    /// only the body, and the SCIM routes' most fragile property is their
    /// <c>application/scim+json</c> content type: some connectors reject
    /// <c>application/json</c> outright, and <c>TypedResults.Ok</c> — the shape the
    /// response-typing work applies everywhere else — would switch it silently while leaving the
    /// body correct. A source scan for the literal would pass on a call that never runs, which is
    /// how the self-description shipped as a 500 with a green source test beside it.</para>
    /// </summary>
    public async Task<(HttpStatusCode Status, string? MediaType, string Body)> SendForResponseAsync(
        string method, string url, string scope)
    {
        using HttpRequestMessage request = new(new HttpMethod(method), url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", TokenPerScope[scope]);

        using HttpResponseMessage response = await _client.SendAsync(request);

        return (
            response.StatusCode,
            response.Content.Headers.ContentType?.MediaType,
            await response.Content.ReadAsStringAsync());
    }

    private readonly HttpClient _client;

    public GeneratedOpenApiDocumentFixture()
    {
        Directory.CreateDirectory(_tempDir);
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseEnvironment("Development");
            // UseSetting, not ConfigureAppConfiguration: Program reads these during service
            // registration, which happens before app configuration would be applied.
            b.UseSetting("DatabaseProvider", "Sqlite");
            b.UseSetting("ConnectionStrings:DefaultConnection", $"Data Source={Path.Combine(_tempDir, "app.db")}");
            b.UseSetting("Telemetry:DataPath", Path.Combine(_tempDir, "telemetry"));
            b.UseSetting("Telemetry:ObjectStorage:Bucket", "");
            b.UseSetting("Vault:RootKey", "dGhpcyBpcyBhIDMyIGJ5dGUga2V5ISEhMTIzNDU2Nzg=");
            b.UseSetting("DataProtection:KeyPath", Path.Combine(_tempDir, "keys"));
            b.UseSetting("Seed:AdminEmail", "");

            // ⚠️ A precaution, and deliberately not described as more than that: a mutation
            // switching this back to true leaves every test in this collection passing, so
            // nothing here DEMONSTRATES the need. What is measured is the exposure.
            // HostedWork.RunsSingletonWork defaults to TRUE
            // (deliberately — an existing install must keep sweeping after an upgrade), so this
            // host was running every AddSingletonWork registration across all twelve module
            // compositions for the life of the collection. They found nothing only because the
            // database held one tenant and some tokens.
            //
            // That stops being true the moment a suite seeds a cluster. DriftScanService waits
            // 5 minutes and SupplyChainScanService 8 (DriftScanService.cs:82) and this project's
            // CI run has reached 18m29s, so those delays ARE crossed — and the sweep would then
            // dial the fake ApiServerUrl of a seeded cluster and fail somewhere unrelated to
            // whatever test was running. Switching the flag off is what it exists for, and it
            // makes the shared host cheaper and more deterministic for every suite here.
            b.UseSetting(HostedWork.RunSingletonWorkKey, "false");
        });

        IServiceProvider services = _factory.Services; // builds the host

        // A redirect to the login page would be reported as a 302 rather than the status the API
        // chose, and nothing here should ever redirect.
        // ⚠️ http, deliberately, and an earlier version of this changed it to https with a
        // comment calling that "load-bearing for every non-GET probe in this collection". A
        // mutation refuted it: with the API's status-code re-execution exemption in place, every
        // probe passes over plain http. What produced the 500 ("AntiforgeryOptions.Cookie
        // .SecurePolicy = Always, but the current request is not an SSL request") was the
        // RE-EXECUTION of a bodyless API 404 against the /not-found Razor page, not the API call
        // itself. Fixing the cause removed the need for the workaround, and a precaution with a
        // false justification is worse than no precaution.
        //
        // A test that posts to a cookie-authenticated form endpoint here WILL meet that 500 and
        // should set BaseAddress to https for itself rather than changing it for all seven suites.
        _client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        Services = services;
        TokenPerScope = MintOneTokenPerScope(services, out Guid tokenTenantId);
        TokenTenantId = tokenTenantId;

        try
        {
            var provider = services.GetRequiredKeyedService<Microsoft.AspNetCore.OpenApi.IOpenApiDocumentProvider>(
                PublicApiOpenApi.DocumentName);
            Document = provider.GetOpenApiDocumentAsync(CancellationToken.None).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            GenerationFailure = ex;
            return;
        }

        Operations =
        [
            .. from path in Document.Paths ?? []
               from operation in path.Value.Operations ?? []
               select new DocumentedOperation(
                   operation.Key.ToString().ToUpperInvariant(),
                   path.Key,
                   operation.Value.Description ?? "",
                   operation.Value.Security?.Count ?? 0,
                   [.. from parameter in operation.Value.Parameters ?? []
                       where parameter.In == ParameterLocation.Query && parameter.Required
                       select parameter.Name ?? ""])
        ];

        ScopedRoutes =
        [
            .. from endpoint in services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>()
               where endpoint.Metadata.GetMetadata<ApiScopeMetadata>() is not null
               let template = "/" + (endpoint.RoutePattern.RawText ?? "").TrimStart('/')
               // ⚠️ The document does not describe itself. MapOpenApi's own route carries
               // ApiScopeMetadata — it is served behind the same token as everything else — but it
               // has no API description, so it is absent from the document by design rather than
               // by omission. Excluded by its exact path so a typo cannot widen this.
               where template != "/api/v1/openapi.json"
               from method in endpoint.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods ?? []
               select (method.ToUpperInvariant(), StripConstraints(template))
        ];

        ApiSurface =
        [
            .. ScopedRoutes
                .Select(r => $"{r.Method} {Regex.Replace(r.Path, @"\{[^}]+\}", "{}")}")
                .Distinct(StringComparer.Ordinal)
                .OrderBy(r => r, StringComparer.Ordinal)
        ];
    }

    /// <summary>
    /// One token per scope, each holding exactly that one scope.
    ///
    /// <para>A token holding nothing would be the obvious negative case, but
    /// <c>ApiTokenService.CreateAsync</c> refuses to mint one — correctly, since it could do
    /// nothing while looking like working access. A token holding exactly one *other* scope is
    /// the same test and uses the supported path.</para>
    /// </summary>
    private static Dictionary<string, string> MintOneTokenPerScope(
        IServiceProvider services, out Guid tenantId)
    {
        var dbFactory = services.GetRequiredService<IDbContextFactory<ApplicationDbContext>>();
        tenantId = Guid.NewGuid();
        using (ApplicationDbContext db = dbFactory.CreateDbContext())
        {
            db.Tenants.Add(new Tenant
            {
                Id = tenantId,
                Name = "OpenApi document test",
                Slug = "openapi-" + tenantId.ToString("N")[..8],
            });
            db.SaveChanges();
        }

        using IServiceScope scope0 = services.CreateScope();
        var tokens = scope0.ServiceProvider.GetRequiredService<ApiTokenService>();
        Dictionary<string, string> minted = new(StringComparer.Ordinal);
        foreach (string scope in ApiScopes.All)
        {
            CreatedApiToken created = tokens
                .CreateAsync(tenantId, $"doc-test {scope}", [scope], "openapi-tests", null)
                .GetAwaiter().GetResult();
            minted[scope] = created.Plaintext;
        }

        return minted;
    }

    /// <summary>
    /// `{clusterId:guid}` in a route template is `{clusterId}` in the document: the constraint is
    /// how the router decides whether to match, and carries no meaning to a caller.
    /// </summary>
    private static string StripConstraints(string template) =>
        Regex.Replace(template, @"\{([^:?}]+)[^}]*\}", "{$1}");

    public void Dispose()
    {
        _client.Dispose();
        _factory.Dispose();
        try
        {
            Directory.Delete(_tempDir, recursive: true);
        }
        catch (IOException)
        {
            // A leftover temp directory is not worth failing a passing run over.
        }
    }
}

/// <param name="SecurityRequirements">
/// How many security requirements the operation declares. Zero means the document tells a caller
/// the route needs no credential.
/// </param>
/// <summary>
/// The xUnit collection that shares ONE booted application between every suite which needs to ask
/// the running system what its API surface is.
///
/// <para>⚠️ <b>A collection fixture, not three class fixtures, and the reason is measured.</b>
/// <c>IClassFixture</c> is constructed once per test CLASS, so three suites consuming it would
/// boot three <c>WebApplicationFactory</c> hosts and hold them all alive at once. This suite has
/// already exhausted a 7.8 GB CI runner by rooting internal service providers that nothing could
/// evict; a host is far heavier than one of those. One boot, shared.</para>
///
/// <para>The cost is that these suites no longer run in parallel with each other. That is the
/// trade: a few seconds of wall clock against a class of failure that took four wrong diagnoses
/// to find last time.</para>
/// </summary>
[CollectionDefinition(Name)]
public sealed class ApiSurfaceCollection : ICollectionFixture<GeneratedOpenApiDocumentFixture>
{
    public const string Name = "api-surface";
}

/// <param name="RequiredQueryParameters">
/// Query parameters the document marks required. A request that omits one is rejected by
/// parameter binding before the scope check runs, so a probe must supply them.
/// </param>
public sealed record DocumentedOperation(
    string Method, string Path, string Description, int SecurityRequirements,
    IReadOnlyList<string> RequiredQueryParameters)
{
    /// <summary>
    /// The scope the document claims this operation needs, or null when it claims any live token
    /// will do. Read back out of the rendered description rather than from the metadata, because
    /// the description is the thing a human and a generated client actually read.
    /// </summary>
    public string? StatedScope =>
        Regex.Match(Description, @"Requires the `([^`]+)` scope") is { Success: true } m
            ? m.Groups[1].Value
            : null;

    /// <summary>True when the description claims any live token suffices.</summary>
    public bool ClaimsAnyTokenWillDo =>
        Description.Contains("Requires any live API token", StringComparison.Ordinal);

    public override string ToString() => $"{Method} {Path}";
}

/// <summary>
/// The generated OpenAPI document, asserted against the routes the application serves.
///
/// <para><b>Why this file exists, and what it already caught.</b> <c>OpenApiDocumentTests</c> scans
/// the source and said so plainly: it did not generate the document, and generation was "wired and
/// builds, and is not yet asserted". Generating it here found two defects that no source scan could
/// have seen:</para>
///
/// <para>1. <b>The document did not generate at all.</b> The document transformer indexed into
/// <c>Components.SecuritySchemes</c>, which OpenAPI.NET v2 leaves null rather than empty, so every
/// generation threw a <see cref="NullReferenceException"/> and <c>GET /api/v1/openapi.json</c>
/// answered a bare 500 with an empty body. That empty 500 is why the earlier attempt was abandoned
/// as a hosting problem: the failure mode of this feature is indistinguishable from the failure
/// mode of the test harness, unless you resolve the provider in-process and let the exception out,
/// which is what <see cref="GeneratedOpenApiDocumentFixture"/> does.</para>
///
/// <para>2. <b>The document described 16 routes that are not this API</b> — Identity's
/// <c>/Account/*</c> posts, OTLP and RUM ingest, the git and ticket webhooks, admin
/// backup/restore — each stamped "UNAUTHENTICATED" because they are guarded by signatures,
/// cookies or antiforgery instead of an API scope. <c>PublicApiOpenApi.ApiPathPrefixes</c> now
/// bounds the document, and <see cref="Nothing_that_requires_a_scope_is_left_out_of_the_document"/>
/// is what stops that list from going stale.</para>
/// </summary>
[Collection(ApiSurfaceCollection.Name)]
public class GeneratedOpenApiDocumentTests(GeneratedOpenApiDocumentFixture fixture)
{
    /// <summary>
    /// ⚠️ THE MEASURE. Every other test here reads <c>fixture.Operations</c>, and an empty list
    /// satisfies all of them. This is the one that fails if the document stops being produced.
    /// </summary>
    [Fact]
    public void The_document_generates_and_is_not_empty()
    {
        fixture.GenerationFailure.Should().BeNull(
            "generating the document is the whole feature, and when it throws the only thing a "
            + "caller sees is an empty 500 from /api/v1/openapi.json");

        fixture.Document!.Info?.Title.Should().Be("EntKube API");
        fixture.Operations.Should().HaveCountGreaterThan(15,
            "the API serves around thirty operations; a near-empty document would make every "
            + "assertion in this file pass over nothing");
    }

    /// <summary>
    /// The scope is the part worth having: a client that knows a route needs <c>config:write</c>
    /// can tell an operator which token to mint, instead of letting them find out from a 403.
    /// </summary>
    [Fact]
    public void Every_operation_states_which_scope_it_needs()
    {
        List<DocumentedOperation> silent =
            [.. fixture.Operations.Where(o => o.StatedScope is null && !o.ClaimsAnyTokenWillDo)];

        silent.Should().BeEmpty(
            "these operations describe no credential at all:\n  "
            + string.Join("\n  ", silent.Select(o => o.ToString())));
    }

    /// <summary>
    /// ⚠️ <b>THE ASSERTION THE FEATURE IS FOR.</b> Everything above checks that the document says
    /// something; this checks that what it says is <b>true of the running server</b>.
    ///
    /// <para>Two calls per operation, each with a token holding exactly one scope: the scope the
    /// document names must get past the check, and a different scope must not. A document that
    /// names the wrong scope fails the first; a document that has quietly stopped naming scopes at
    /// all — enforcement untouched, every operation downgraded to "any live API token" — fails the
    /// second. That second case is the quietest possible regression here, and a test that only
    /// looked for the word "Requires" in the description passed it happily.</para>
    ///
    /// <para>403 is the only status this reads. A route may answer 404 for a random id, 400 for a
    /// stub body, or 500 against an empty database; none of those are a scope refusal, and
    /// treating them as "allowed" is what makes this test independent of every handler's
    /// behaviour.</para>
    /// </summary>
    [Fact]
    public async Task The_scope_each_operation_states_is_the_scope_the_server_enforces()
    {
        fixture.Operations.Should().NotBeEmpty("⚠️ the measure: no operations means nothing is called");

        List<string> wrong = [];

        foreach (DocumentedOperation operation in fixture.Operations)
        {
            string stated = operation.StatedScope ?? ApiScopes.All[0];
            string other = ApiScopes.All.First(s => !string.Equals(s, stated, StringComparison.Ordinal));

            HttpStatusCode withStated = await fixture.CallAsync(operation, stated);
            if (withStated == HttpStatusCode.Forbidden)
            {
                wrong.Add($"{operation} — document says `{operation.StatedScope ?? "any token"}`, "
                          + $"but a token holding exactly that was refused");
            }

            HttpStatusCode withOther = await fixture.CallAsync(operation, other);
            bool shouldBeRefused = !operation.ClaimsAnyTokenWillDo;

            if (shouldBeRefused && withOther != HttpStatusCode.Forbidden)
            {
                wrong.Add($"{operation} — document says `{operation.StatedScope}` is required, but a "
                          + $"token holding only `{other}` was not refused (got {(int)withOther})");
            }
            else if (!shouldBeRefused && withOther == HttpStatusCode.Forbidden)
            {
                wrong.Add($"{operation} — document says any live token will do, but a token holding "
                          + $"`{other}` was refused, so the document understates what this needs");
            }
        }

        wrong.Should().BeEmpty("the document disagrees with the server:\n  " + string.Join("\n  ", wrong));
    }

    /// <summary>Each operation also carries the machine-readable requirement, not just prose.</summary>
    [Fact]
    public void Every_operation_requires_the_bearer_scheme()
    {
        fixture.Document!.Components?.SecuritySchemes.Should().ContainKey("apiToken",
            "the scheme each operation references has to be declared, or the document is "
            + "internally inconsistent and a generator will produce a client with no auth");

        List<DocumentedOperation> unsecured = [.. fixture.Operations.Where(o => o.SecurityRequirements == 0)];

        unsecured.Should().BeEmpty(
            "an operation with no security requirement tells a generated client the route is "
            + "open:\n  " + string.Join("\n  ", unsecured.Select(o => o.ToString())));
    }

    /// <summary>
    /// ⚠️ This is the assertion that would fail if the prefix list in
    /// <see cref="PublicApiOpenApi.ApiPathPrefixes"/> went stale — a new scoped surface mounted at
    /// a new prefix would be served, authenticated, and silently missing from the document, which
    /// is exactly the kind of gap that made the CLI fall seven endpoints behind.
    /// </summary>
    [Fact]
    public void Nothing_that_requires_a_scope_is_left_out_of_the_document()
    {
        fixture.ScopedRoutes.Should().HaveCountGreaterThan(15,
            "⚠️ the measure for this test: if the router scan finds nothing, the comparison below "
            + "is between two empty sets");

        IEnumerable<(string, string)> documented =
            fixture.Operations.Select(o => (o.Method, o.Path));

        List<(string Method, string Path)> missing = [.. fixture.ScopedRoutes.Except(documented)];

        missing.Should().BeEmpty(
            "these routes require an API scope but the document does not describe them:\n  "
            + string.Join("\n  ", missing.Select(r => $"{r.Method} {r.Path}")));
    }

    /// <summary>
    /// The other direction: the document must not describe anything the API does not serve under
    /// an API scope. A generated client would grow methods for Identity's form posts.
    /// </summary>
    [Fact]
    public void The_document_describes_nothing_but_the_API()
    {
        IEnumerable<(string, string)> served = fixture.ScopedRoutes.Select(r => (r.Method, r.Path));

        List<DocumentedOperation> foreign =
            [.. fixture.Operations.Where(o => !served.Contains((o.Method, o.Path)))];

        foreign.Should().BeEmpty(
            "the document describes routes that are not part of the scoped API:\n  "
            + string.Join("\n  ", foreign.Select(o => o.ToString())));

        fixture.Operations.Should().NotContain(o => o.Description.Contains("UNAUTHENTICATED", StringComparison.Ordinal),
            "an API route that never went through RequireApiScope has no token check at all");
    }
}
