using FluentAssertions;
using Microsoft.OpenApi;
using Xunit;

namespace EntKube.Web.Tests;

/// <summary>
/// The generated OpenAPI document, committed to the repository.
///
/// <para><b>Why a file and not only a test.</b> Everything this repository knows about the API's
/// described shape is asserted by a count — <c>UndescribedSuccessBodies</c>, the operation
/// buckets, the scope rules. A count tells a reviewer that something changed, never WHAT. Until
/// now an API change arrived as a one-line diff to a ratchet constant and the actual wire
/// difference was invisible in review. The committed document is that difference, and it is the
/// input the generated clients are built from (<c>docs/decomposition.md</c> item 8) — so it has
/// to be a reviewable artifact rather than something that exists only inside a test process.</para>
///
/// <para>⚠️ <b>Regenerating is deliberate, not automatic.</b> Set
/// <c>ENTKUBE_UPDATE_OPENAPI=1</c> and run this test to rewrite the file. A test that silently
/// rewrote its own expectation would make every wire change self-approving, which is the one thing
/// this file exists to prevent.</para>
/// </summary>
[Collection(ApiSurfaceCollection.Name)]
public class ApiDocumentSnapshotTests(GeneratedOpenApiDocumentFixture fixture)
{
    internal const string RelativePath = "docs/openapi/v1.json";

    internal static string RepoRoot()
    {
        DirectoryInfo? dir = new(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "charts")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new DirectoryNotFoundException("Could not locate the repository root.");
    }

    internal static string Path_ => Path.Combine(RepoRoot(), RelativePath);

    /// <summary>
    /// Serialises the live document the way the committed copy is written: OpenAPI 3.1, indented,
    /// with a trailing newline so the file is a well-formed text file and a diff of it is readable.
    /// </summary>
    internal static string Serialise(OpenApiDocument document)
    {
        using StringWriter text = new();
        OpenApiJsonWriter writer = new(text);

        document.SerializeAsV31(writer);
        text.Flush();

        return text.ToString().TrimEnd() + "\n";
    }

    /// <summary>
    /// Every operation names itself, and no two share a name.
    ///
    /// <para><b>⚠️ This is not implied by the compiler check.</b> <c>RequireApiScope</c> requires
    /// an operation name, and ASP.NET Core refuses two endpoints sharing one at startup — so
    /// every route HAS a name. That is a different fact from the name reaching the document.
    /// Measured here because it did not: with all forty-three routes named through
    /// <c>WithName</c>, the generated document came back with <b>0 of 43</b> operationIds, and
    /// <c>PublicApiOpenApi</c> now sets them explicitly. Nothing about the compiler check would
    /// have noticed.</para>
    ///
    /// <para>Without an operationId a generator invents method names from the method and path, so
    /// a path edit silently renames a client method and the name a caller binds to never appears
    /// in review at all.</para>
    /// </summary>
    [Fact]
    public void Every_operation_names_itself_and_no_two_share_a_name()
    {
        fixture.GenerationFailure.Should().BeNull();

        List<(string Method, string Path, string? Id)> operations =
            [.. from path in fixture.Document!.Paths ?? []
                from operation in path.Value.Operations ?? []
                select (operation.Key.Method.ToUpperInvariant(), path.Key, operation.Value.OperationId)];

        operations.Should().NotBeEmpty("otherwise this says nothing");

        List<(string Method, string Path, string? Id)> anonymous =
            [.. operations.Where(o => string.IsNullOrWhiteSpace(o.Id))];

        anonymous.Should().BeEmpty(
            "every operation needs an operationId or a generated client's method name is invented "
            + "from the path: " + string.Join("; ", anonymous.Select(o => $"{o.Method} {o.Path}")));

        operations.Select(o => o.Id).Should().OnlyHaveUniqueItems(
            "an operationId is a method name on every generated client, so two operations sharing "
            + "one produce a client that cannot compile");
    }

    [Fact]
    public void The_committed_document_is_what_the_application_generates()
    {
        fixture.GenerationFailure.Should().BeNull(
            "a generation failure must be reported as itself rather than as an empty document");

        string generated = Serialise(fixture.Document!);

        if (Environment.GetEnvironmentVariable("ENTKUBE_UPDATE_OPENAPI") == "1")
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path_)!);
            File.WriteAllText(Path_, generated);
        }

        File.Exists(Path_).Should().BeTrue(
            $"{RelativePath} is the committed document; regenerate it with ENTKUBE_UPDATE_OPENAPI=1");

        File.ReadAllText(Path_).Should().Be(
            generated,
            "the committed document has drifted from what the application generates. If the API "
            + "changed on purpose, regenerate with ENTKUBE_UPDATE_OPENAPI=1 and review the diff — "
            + "that diff IS the wire change, and it is the only place review can see it.");
    }
}
