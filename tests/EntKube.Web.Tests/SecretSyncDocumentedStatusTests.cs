using FluentAssertions;
using Microsoft.OpenApi;
using Xunit;

namespace EntKube.Web.Tests;

/// <summary>
/// The generated document declares <c>POST /apps/{appId}/secrets/sync</c>'s <b>207</b>, carrying
/// the same shape as its 200.
///
/// <para>⚠️ <b>Why this needs its own test.</b> Every other response on this API is DERIVED from
/// the handler's return type, so typing the handler is what documents it and nothing can drift.
/// The 207 is the single exception: it is declared with
/// <c>.Produces&lt;AppSecretSyncResponse&gt;(207)</c> because it cannot be derived —
/// <b>measured</b>, with the union alone the document carried 200/404/401/403 and no 207 at all,
/// since <c>JsonHttpResult&lt;T&gt;</c> as a union member contributes nothing. A declaration that
/// nothing asserts is exactly the two-copies-of-one-fact the class comment on
/// <c>PublicApiResponses</c> warns about: delete that line and the only symptom is a generated
/// client that never hears about the normal outcome of a partial sync.</para>
///
/// <para>⚠️ And because <c>IsSuccessStatusCode</c> is true for 207, such a client would read a
/// partial failure as a clean success — the defect <c>CliPartialFailureTests</c> exists because
/// the shipped CLI had.</para>
/// </summary>
[Collection(ApiSurfaceCollection.Name)]
public class SecretSyncDocumentedStatusTests(GeneratedOpenApiDocumentFixture fixture)
{
    [Fact]
    public void The_partial_success_status_is_declared_with_the_same_shape_as_the_clean_one()
    {
        fixture.GenerationFailure.Should().BeNull();

        OpenApiOperation operation = fixture.Document!
            .Paths!["/api/v1/apps/{appId}/secrets/sync"]
            .Operations![HttpMethod.Post];

        operation.Responses!.Keys.Should().Contain(
            "207",
            "the route answers 207 whenever any binding failed, which is the NORMAL partial "
            + "outcome — a client not told about it reads a partial sync as clean");

        string? id207 = SchemaId(operation, "207");
        string? id200 = SchemaId(operation, "200");

        id200.Should().Be(
            nameof(EntKube.Web.Services.PublicApi.AppSecretSyncResponse),
            "the 200 is derived from the handler's Ok<T> arm");

        id207.Should().Be(
            id200,
            "the body is IDENTICAL between the two statuses — the status is the machine-readable "
            + "half of one answer, not a different answer. Two different component ids here would "
            + "also split CliColumnParityTests' view of the route into two 2xx bodies");
    }

    /// <summary>
    /// ⚠️ <b>The two nested <c>error</c> members have DIFFERENT nullability, and this is the only
    /// thing that holds them apart.</b> A mutation widening
    /// <c>SecretFamilySyncFailureResponse.Error</c> from <c>string</c> to <c>string?</c> passed
    /// every other test in the suite — the serialised bytes do not change when the value is
    /// always set, so no wire-shape fact can see it. What changes is the SCHEMA, and therefore
    /// what a generated client declares.
    ///
    /// <para>The asymmetry is the whole reason there are two nested records rather than one:
    /// <c>bindings[].error</c> is nullable because <b>null is how a binding reports success</b>,
    /// while <c>unlistedFamilies[].error</c> is not, because a family appears in that list
    /// precisely BECAUSE listing it failed. Collapsing them would invite a client to read a null
    /// in the families list as success — and the record's doc comment said so while nothing
    /// enforced it.</para>
    /// </summary>
    [Fact]
    public void A_binding_may_report_no_error_and_an_unlisted_family_may_not()
    {
        fixture.GenerationFailure.Should().BeNull();

        IDictionary<string, IOpenApiSchema> schemas = fixture.Document!.Components!.Schemas!;

        IOpenApiSchema bindingError =
            schemas[nameof(EntKube.Web.Services.PublicApi.SecretBindingSyncResponse)]
                .Properties!["error"];

        IOpenApiSchema familyError =
            schemas[nameof(EntKube.Web.Services.PublicApi.SecretFamilySyncFailureResponse)]
                .Properties!["error"];

        // ⚠️ JsonSchemaType is a [Flags] enum in Microsoft.OpenApi v2, so `== JsonSchemaType.String`
        // is FALSE for a nullable string (String|Null). HasFlag is the only correct test.
        Nullable(bindingError).Should().BeTrue(
            "null is how a binding says it synced — a client must be able to receive one");

        Nullable(familyError).Should().BeFalse(
            "a family is in this list because listing it FAILED, so there is always a reason; "
            + "declaring it nullable would let a client read a null here as success");
    }

    private static bool Nullable(IOpenApiSchema schema) =>
        schema.Type?.HasFlag(JsonSchemaType.Null) == true;

    /// <summary>
    /// ⚠️ Reads the component NAME, not the schema object: two <c>OpenApiSchemaReference</c>
    /// instances pointing at one component are different objects, which is why
    /// <c>CliColumnParityTests.SuccessBodies</c> dedupes by <c>Reference.Id</c> rather than by
    /// reference equality.
    /// </summary>
    private static string? SchemaId(OpenApiOperation operation, string status) =>
        operation.Responses![status].Content?.Values
            .Select(c => (c.Schema as OpenApiSchemaReference)?.Reference?.Id)
            .FirstOrDefault(id => id is not null);
}
