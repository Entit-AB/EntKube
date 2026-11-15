using System.Reflection;
using System.Text.Json;
using Microsoft.AspNetCore.Http.Metadata;

namespace EntKube.Web.Services.Scim;

/// <summary>
/// Writes a SCIM body, and tells the generated document what that body is.
///
/// <para><b>⚠️ Why not <c>TypedResults.Ok</c>.</b> Every other route in this API gained its
/// schema by returning <c>TypedResults.Ok(record)</c>. On these seven that would also change the
/// media type to <c>application/json</c> — silently, with the body otherwise intact — and some
/// directory connectors reject anything but <c>application/scim+json</c>. <c>TypedResults.Json</c>
/// does not help either: it writes whatever content type it is given but still declares
/// <c>application/json</c> in the document, so the document would contradict the wire. These
/// results write SCIM's media type AND declare it.</para>
///
/// <para><b>What <c>TBody</c> means.</b> It is the SUCCESS body, which is what the document
/// describes. The instance carries whatever actually happened, so a failure travels through
/// <see cref="ScimOk{TBody}"/> too, as the error envelope with its own status. The declared
/// failures are a separate piece of work; what matters here is that the success is no longer
/// <c>object</c>.</para>
/// </summary>
public abstract class ScimJsonResult : IResult
{
    /// <summary>SCIM's own content type. Some connectors check it and reject application/json.</summary>
    public const string ContentType = "application/scim+json";

    /// <summary>
    /// ⚠️ <c>Web</c>, matching what minimal APIs serialise with everywhere else, so the members
    /// these records do NOT carry an attribute for are camelCased the same way they were when
    /// they were <c>JsonObject</c> literals.
    /// </summary>
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// The exact text this result writes.
    ///
    /// <para>⚠️ Public so a test can assert what SHIPS. The alternative — a test serialising the
    /// same record with its own <see cref="JsonSerializerOptions"/> — would pass through a change
    /// to the options used here, which is where the camelCase that renames <c>Resources</c>
    /// comes from in the first place.</para>
    /// </summary>
    public static string Serialize(object body) => JsonSerializer.Serialize(body, Options);

    private readonly int status;
    private readonly object? body;

    private protected ScimJsonResult(int status, object? body)
    {
        this.status = status;
        this.body = body;
    }

    public Task ExecuteAsync(HttpContext httpContext)
    {
        httpContext.Response.StatusCode = status;

        // ⚠️ A 204 sets no content type and writes nothing. Writing "{}" with a content type is
        // what the IResult this replaces did for an empty body, and on a 204 that is a malformed
        // response rather than an empty one.
        if (body is null)
        {
            return Task.CompletedTask;
        }

        httpContext.Response.ContentType = ContentType;
        return httpContext.Response.WriteAsync(Serialize(body), httpContext.RequestAborted);
    }

    /// <summary>
    /// The one place a SCIM response's documented shape is declared. Written as a hand-rolled
    /// <see cref="IProducesResponseTypeMetadata"/> rather than <c>Produces&lt;T&gt;</c> so the
    /// content type travels with it: a declaration of <c>application/json</c> beside a wire of
    /// <c>application/scim+json</c> is the specific lie this class exists to avoid.
    /// </summary>
    internal sealed class Declared(int statusCode, Type? type, bool hasBody = true)
        : IProducesResponseTypeMetadata
    {
        public Type? Type => type;
        public int StatusCode => statusCode;

        // ⚠️ A 204 declares NO content type. An entry naming a media type while carrying no type
        // is inconsistent, and the description layer drops it silently — which left the deletion
        // documented as answering only its two ways of failing.
        public IEnumerable<string> ContentTypes => hasBody ? [ContentType] : [];
    }
}

/// <summary>A SCIM success of 200, carrying <typeparamref name="TBody"/>.</summary>
public sealed class ScimOk<TBody> : ScimJsonResult, IEndpointMetadataProvider
    where TBody : notnull
{
    private ScimOk(int status, object? body) : base(status, body) { }

    public static ScimOk<TBody> Success(TBody body) =>
        new(StatusCodes.Status200OK, body);

    public static ScimOk<TBody> Failure(int status, ScimErrorResponse error) =>
        new(status, error);

    public static void PopulateMetadata(MethodInfo method, EndpointBuilder builder) =>
        builder.Metadata.Add(new Declared(StatusCodes.Status200OK, typeof(TBody)));
}

/// <summary>
/// A SCIM success of 201, carrying <typeparamref name="TBody"/>. Separate from
/// <see cref="ScimOk{TBody}"/> because <c>PopulateMetadata</c> is static and cannot read the
/// status off an instance — and a creation documented as 200 is a client that never sees its own
/// 201.
/// </summary>
public sealed class ScimCreated<TBody> : ScimJsonResult, IEndpointMetadataProvider
    where TBody : notnull
{
    private ScimCreated(int status, object? body) : base(status, body) { }

    public static ScimCreated<TBody> Success(TBody body) =>
        new(StatusCodes.Status201Created, body);

    public static ScimCreated<TBody> Failure(int status, ScimErrorResponse error) =>
        new(status, error);

    public static void PopulateMetadata(MethodInfo method, EndpointBuilder builder) =>
        builder.Metadata.Add(new Declared(StatusCodes.Status201Created, typeof(TBody)));
}

/// <summary>
/// A SCIM success of 204 — a deletion, which has nothing to describe. A failure still travels
/// through it as the error envelope.
/// </summary>
public sealed class ScimNoContent : ScimJsonResult, IEndpointMetadataProvider
{
    private ScimNoContent(int status, object? body) : base(status, body) { }

    public static ScimNoContent Success() => new(StatusCodes.Status204NoContent, null);

    public static ScimNoContent Failure(int status, ScimErrorResponse error) =>
        new(status, error);

    public static void PopulateMetadata(MethodInfo method, EndpointBuilder builder) =>
        builder.Metadata.Add(
            new Declared(StatusCodes.Status204NoContent, typeof(void), hasBody: false));
}


/// <summary>
/// Declares the refusals a SCIM route can answer with.
///
/// <para>⚠️ <b>Not <c>ProducesProblem</c>.</b> That declares a <c>ProblemDetails</c> at
/// <c>application/problem+json</c>, and a SCIM route answers its own envelope at
/// <c>application/scim+json</c> — see <see cref="ScimErrorResponse"/>. Declaring the wrong one
/// would put in the document exactly the lie
/// <c>ScimDocumentedResponseTests.Every_scim_success_is_declared_as_scim_json</c> exists to
/// catch, one status family over.</para>
///
/// <para>⚠️ The <c>401</c> and <c>403</c> are NOT declared here and must not be: those come from
/// <c>ApiTokenAuthFilter</c>, which returns <c>Results.Problem</c>, so for those two statuses a
/// SCIM route really does answer <c>problem+json</c>. The central declaration in
/// <c>RequireApiScope</c> is accurate for them, and this one is accurate for the rest.</para>
/// </summary>
public static class ScimErrorDeclaration
{
    public static TBuilder ProducesScimError<TBuilder>(this TBuilder builder, params int[] statuses)
        where TBuilder : IEndpointConventionBuilder
    {
        foreach (int status in statuses.Distinct().Order())
        {
            builder.WithMetadata(
                new ScimJsonResult.Declared(status, typeof(ScimErrorResponse)));
        }

        return builder;
    }
}
