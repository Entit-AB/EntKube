using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using EntKube.ApiClient;
using FluentAssertions;
using Xunit;

namespace EntKube.Web.Tests;

/// <summary>
/// The generated client, and the chain that produces it:
/// application → <c>docs/openapi/v1.json</c> → <c>EntKubeApi.g.cs</c>.
///
/// <para>Each step has its own ratchet, so a failure says WHICH link moved:
/// <see cref="ApiDocumentSnapshotTests"/> fails when the application and the document disagree,
/// and this file fails when the document and the client disagree.</para>
///
/// <para>⚠️ <b>The assertions below reflect over the COMPILED types, not the generated text.</b>
/// A generated file is still source, and a test that grepped it would pass on a declaration that
/// never compiled into anything — the same mistake as asserting a route exists by finding its
/// string in a file. Reflection reads what the build actually produced.</para>
/// </summary>
public class ApiClientGeneratedTests
{
    private static string Path_ =>
        System.IO.Path.Combine(ApiDocumentSnapshotTests.RepoRoot(), ApiClientGenerator.RelativePath);

    [Fact]
    public void The_generated_client_is_what_the_document_produces()
    {
        string generated = ApiClientGenerator.Generate(
            File.ReadAllText(ApiDocumentSnapshotTests.Path_));

        // ⚠️ Not both at once. This test's INPUT is the other's OUTPUT, and xUnit does not order
        // them — so updating both in one run generated the client from the document as it stood
        // BEFORE the snapshot test rewrote it, and committed a client missing a schema the
        // document had just gained. Refusing is the only way that is not silent.
        if (Environment.GetEnvironmentVariable("ENTKUBE_UPDATE_OPENAPI") == "1"
            && Environment.GetEnvironmentVariable("ENTKUBE_UPDATE_API_CLIENT") == "1")
        {
            Assert.Fail(
                "Regenerate the document first, then the client: ENTKUBE_UPDATE_OPENAPI=1, then "
                + "ENTKUBE_UPDATE_API_CLIENT=1. Setting both in one run generates the client from "
                + "whichever version of the document happened to be on disk when this test ran.");
        }

        if (Environment.GetEnvironmentVariable("ENTKUBE_UPDATE_API_CLIENT") == "1")
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path_)!);
            File.WriteAllText(Path_, generated);
        }

        File.Exists(Path_).Should().BeTrue(
            $"{ApiClientGenerator.RelativePath} is the generated client; regenerate it with "
            + "ENTKUBE_UPDATE_API_CLIENT=1");

        File.ReadAllText(Path_).Should().Be(
            generated,
            "the committed client has drifted from what the document produces. Regenerate with "
            + "ENTKUBE_UPDATE_API_CLIENT=1 — and if the document changed, regenerate THAT first, "
            + "because this generator reads the committed document rather than the live one.");
    }

    /// <summary>
    /// Every operation the document describes has a method, and none is <c>object</c>.
    ///
    /// <para>⚠️ The second half is the point of the whole programme. <c>UndescribedSuccessBodies</c>
    /// counted operations a generator would have to return <c>object</c> for; it is 0, so no
    /// method here may return one. This is that count re-asserted from the other end — on the
    /// generated client rather than on the document — because the two could only disagree if the
    /// generator were dropping something the document describes.</para>
    /// </summary>
    [Fact]
    public void Every_operation_has_a_typed_method()
    {
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(ApiDocumentSnapshotTests.Path_));

        List<string> expected =
            [.. from path in document.RootElement.GetProperty("paths").EnumerateObject()
                from verb in path.Value.EnumerateObject()
                where verb.Name is "get" or "post" or "put" or "patch" or "delete"
                select verb.Value.GetProperty("operationId").GetString() + "Async"];

        expected.Should().HaveCount(43, "the document describes 43 operations");

        MethodInfo[] methods = typeof(EntKubeApi).GetMethods(BindingFlags.Public | BindingFlags.Instance);

        methods.Select(m => m.Name).Should().Contain(expected,
            "every operation the document describes needs a method on the generated client");

        List<string> untyped =
            [.. from method in methods
                where expected.Contains(method.Name)
                let returned = method.ReturnType.GetGenericArguments().FirstOrDefault()
                where returned is not null && returned.IsGenericType
                let body = returned.GetGenericArguments().FirstOrDefault()
                where body == typeof(object)
                select method.Name];

        untyped.Should().BeEmpty(
            "UndescribedSuccessBodies is 0, so no generated method may return object: "
            + string.Join(", ", untyped));
    }

    /// <summary>
    /// ⚠️ <b>Money is <c>decimal</c>.</b> The server declares <c>decimal</c>; .NET's OpenAPI
    /// generation writes <c>format: double</c>, losing that; and a client that read a price into a
    /// <c>double</c> would put binary floating-point error into a billing API. This repository has
    /// already shipped eight cost-arithmetic defects, so the generator's departure from a literal
    /// reading of the document is pinned rather than left as a comment.
    /// </summary>
    [Fact]
    public void A_price_is_a_decimal_not_a_double()
    {
        Type rate = typeof(EntKubeApi).Assembly.GetType("EntKube.ApiClient.ClusterRateResponse")!;

        rate.Should().NotBeNull("the cost-rate response is generated from the document");

        foreach (string money in new[]
        {
            "CpuCoreHourCost", "MemoryGiBHourCost", "StorageGiBMonthCost",
            "LoadBalancerMonthlyCost", "PublicIpMonthlyCost", "ClusterMonthlyOverhead",
        })
        {
            rate.GetProperty(money)!.PropertyType.Should().Be(
                typeof(decimal), $"{money} is a price, and the server stores it as a decimal");
        }
    }

    /// <summary>
    /// ⚠️ A number .NET writes as <c>type: ["integer","string"]</c> is a number. Read as a literal
    /// union it is "integer or string", which a generator resolves to <c>object</c> — and 109
    /// properties in this document are written that way, including every count and every price.
    /// </summary>
    [Fact]
    public void A_number_that_may_arrive_as_a_string_is_still_a_number()
    {
        Type app = typeof(EntKubeApi).Assembly.GetType("EntKube.ApiClient.AppResponse")!;

        app.GetProperty("DeploymentCount")!.PropertyType.Should().Be(
            typeof(int), "the document writes it as [\"integer\",\"string\"] with format int32");
    }

    /// <summary>
    /// ⚠️ SCIM's capital-R <c>Resources</c> survives into the client, and it does so without a
    /// special case: the generator writes a <c>[JsonPropertyName]</c> for every property straight
    /// out of the document, so the one member in RFC 7644 that is not lowerCamelCase needs no
    /// rule of its own. Without that attribute the client would deserialise <c>resources</c>,
    /// find nothing, and report an empty directory.
    /// </summary>
    [Fact]
    public void The_scim_list_envelope_keeps_its_capital_R()
    {
        Type list = typeof(EntKubeApi).Assembly.GetType("EntKube.ApiClient.ScimListResponse")!;

        PropertyInfo resources = list.GetProperty("Resources")!;

        resources.GetCustomAttribute<JsonPropertyNameAttribute>()!.Name.Should().Be(
            "Resources", "RFC 7644 §3.4.2 spells it with a capital R");
    }

    /// <summary>
    /// A body that does not match the schema is a FAILURE, not a success carrying null. A caller
    /// reading <c>Value</c> as null would treat "the API contradicted its own document" as
    /// "there is nothing there" — an empty list of clusters rather than a broken contract.
    /// </summary>
    [Fact]
    public void A_body_that_does_not_match_the_schema_is_not_a_success()
    {
        Type cluster = typeof(EntKubeApi).Assembly.GetType("EntKube.ApiClient.WhoAmIResponse")!;

        object response = typeof(ApiResponse<>).MakeGenericType(cluster)
            .GetMethod("From", BindingFlags.Public | BindingFlags.Static)!
            .Invoke(null, [new ApiResult(true, "this is not json", 200)])!;

        response.GetType().GetProperty("Success")!.GetValue(response).Should().Be(false);
        response.GetType().GetProperty("Value")!.GetValue(response).Should().BeNull();
        ((string)response.GetType().GetProperty("Body")!.GetValue(response)!)
            .Should().Contain("could not read").And.Contain("this is not json");
    }
}
