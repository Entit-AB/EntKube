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
