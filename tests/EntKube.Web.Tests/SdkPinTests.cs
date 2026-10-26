using System.Text.RegularExpressions;
using FluentAssertions;
using Xunit;

namespace EntKube.Web.Tests;

/// <summary>
/// Several files name a .NET SDK, and they have to name the same one.
///
/// <para>
/// ⚠️ THE IMAGE IS THE ARTIFACT. A Dockerfile's <c>sdk:</c> tag is the compiler that builds what
/// customers run, so a suite compiled with any other SDK is not testing the artifact. For three runs
/// the Tests job did exactly that — it floated to 10.0.403, whose Razor compiler mis-generates a
/// hand-written <c>__builder</c> lambda used as a switch arm, and failed at build while the
/// published image was green throughout.
/// </para>
///
/// <para>
/// ⚠️ A PIN IN CI IS NOT A PIN. <c>setup-dotnet</c>'s version input chooses what to INSTALL; the
/// runner already has a newer SDK in the same directory and <c>dotnet</c> takes the highest one it
/// finds. Only <c>global.json</c> chooses what RUNS. #394 named the right version in the workflow,
/// reported it as the fix, and nothing read it.
/// </para>
///
/// <para>
/// ⚠️ ONCE <c>global.json</c> EXISTS, A FLOATING VERSION ELSEWHERE IS A BREAK, NOT A MISMATCH. An
/// SDK that global.json refuses is an SDK <c>dotnet</c> will not start with, so the job runs
/// nothing. <c>release-telemetry.yml</c> asked for <c>10.0.x</c> and was exactly that.
/// </para>
/// </summary>
public class SdkPinTests
{
    private static string RepoRoot()
    {
        DirectoryInfo? dir = new(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "charts")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new DirectoryNotFoundException("Could not locate the repository root.");
    }

    /// <summary>
    /// Every Dockerfile in the tree, excluding anything under <c>.git</c>. Enumerated rather than
    /// listed, because a new image's Dockerfile has to be caught the day it is added — there are two
    /// today (the management plane and the telemetry node) and listing them would quietly exempt a
    /// third.
    /// </summary>
    private static List<string> DockerfilePaths()
        => [.. Directory.EnumerateFiles(RepoRoot(), "Dockerfile*", SearchOption.AllDirectories)
            // ⚠️ ORDINAL, because the enumeration above is NOT case-sensitive on macOS or Windows:
            // the pattern matched monaco-editor's vendored `dockerfile/dockerfile.js`, and the
            // measure test below is what reported it. A case-insensitive match here would have made
            // the comparison demand an sdk: tag from a syntax-highlighting file.
            .Where(p => Path.GetFileName(p).StartsWith("Dockerfile", StringComparison.Ordinal))
            .Where(p => !Excluded(p))
            .OrderBy(p => p, StringComparer.Ordinal)];

    /// <summary>Build output and vendored trees hold copies that are not ours to pin.</summary>
    private static bool Excluded(string path)
    {
        char sep = Path.DirectorySeparatorChar;
        foreach (string dir in new[] { ".git", "bin", "obj", "node_modules", "lib" })
        {
            if (path.Contains($"{sep}{dir}{sep}", StringComparison.Ordinal)) return true;
        }
        return false;
    }

    private static List<string> WorkflowPaths()
        => [.. Directory.EnumerateFiles(Path.Combine(RepoRoot(), ".github", "workflows"), "*.yml")
            .OrderBy(p => p, StringComparer.Ordinal)];

    private static string Rel(string path)
        => Path.GetRelativePath(RepoRoot(), path);

    /// <summary>Each Dockerfile's <c>sdk:</c> tag, as (file, version).</summary>
    private static List<(string File, string Version)> DockerfileSdks()
        => [.. DockerfilePaths()
            .SelectMany(p => Regex.Matches(File.ReadAllText(p), @"mcr\.microsoft\.com/dotnet/sdk:([\w.-]+)")
                .Select(m => (Rel(p), m.Groups[1].Value)))];

    /// <summary>
    /// Each workflow's <c>dotnet-version</c>, as (file, version). The pattern deliberately accepts
    /// <c>x</c> and optional quotes: a floating <c>'10.0.x'</c> must be CAPTURED so it can be
    /// compared and rejected, not skipped and therefore invisible.
    /// </summary>
    private static List<(string File, string Version)> WorkflowSdks()
        => [.. WorkflowPaths()
            .SelectMany(p => Regex.Matches(File.ReadAllText(p), @"dotnet-version:\s*'?([\w.]+)'?")
                .Select(m => (Rel(p), m.Groups[1].Value)))];

    private static string? GlobalJsonSdk()
    {
        Match m = Regex.Match(
            File.ReadAllText(Path.Combine(RepoRoot(), "global.json")),
            @"""version""\s*:\s*""([\d.]+)""");
        return m.Success ? m.Groups[1].Value : null;
    }

    /// <summary>The authority: the SDK that builds the management-plane image customers run.</summary>
    private static string ShippedSdk()
        => DockerfileSdks().Single(d => d.File == "Dockerfile").Version;

    /// <summary>
    /// ⚠️ THIS TEST IS THE MEASURE, and it exists because the comparison below cannot be trusted on
    /// its own: regexes that match nothing produce an empty set, and every member of an empty set
    /// agrees with everything. It is not enough to find SOME pin — a scan that parsed one Dockerfile
    /// and silently skipped another would compare one value to itself and pass. So each count is
    /// checked against the number of files actually on disk.
    /// </summary>
    [Fact]
    public void Every_file_that_names_an_sdk_was_actually_parsed()
    {
        List<string> dockerfiles = DockerfilePaths();
        dockerfiles.Should().HaveCountGreaterThan(1,
            "there are two images with a build stage; finding fewer means the enumeration broke");
        DockerfileSdks().Select(d => d.File).Should().BeEquivalentTo(
            dockerfiles.Select(Rel),
            "every Dockerfile must yield an sdk: tag — one that parses to nothing drops out of the "
            + "comparison instead of failing it");

        List<string> withSetupDotnet = [.. WorkflowPaths()
            .Where(p => File.ReadAllText(p).Contains("setup-dotnet", StringComparison.Ordinal))];
        withSetupDotnet.Should().NotBeEmpty("CI installs an SDK somewhere");
        WorkflowSdks().Select(w => w.File).Distinct().Should().BeEquivalentTo(
            withSetupDotnet.Select(Rel),
            "every workflow that installs an SDK must say which one, in a form this test can read");

        GlobalJsonSdk().Should().NotBeNull(
            "global.json is the only one of these that decides which SDK actually RUNS; if its shape "
            + "changed so this no longer finds it, the comparison below goes vacuous");
    }

    /// <summary>
    /// ⚠️ Every drift fails here EXCEPT bumping <c>global.json</c> alone: no installed SDK then
    /// satisfies it, so <c>dotnet</c> refuses to run anything and names the file and the version it
    /// wanted. That is a better failure than a red test, and the reason this does not try to assert
    /// on it. Verified by mutation in every direction, plus the measure-sabotage above.
    /// </summary>
    [Fact]
    public void Everything_that_names_an_sdk_names_the_one_the_image_ships()
    {
        string shipped = ShippedSdk();

        foreach ((string file, string version) in DockerfileSdks())
        {
            version.Should().Be(shipped,
                $"{file} builds a shipped image, and two images built by different compilers is the "
                + "same defect as a suite built by a third");
        }

        GlobalJsonSdk().Should().Be(shipped,
            "global.json selects the SDK that builds the tests, and the Dockerfile tag is the one "
            + "that builds the artifact — when they differ, a green suite says nothing about the image");

        foreach ((string file, string version) in WorkflowSdks())
        {
            version.Should().Be(shipped,
                $"{file} installs the SDK global.json must then accept; an SDK global.json does not "
                + "ask for is not a wrong compiler but a dead job");
        }
    }

    /// <summary>
    /// 10.0.1xx and 10.0.4xx are different feature bands, and it was the band that broke. Allowing
    /// patches inside the shipped band keeps a developer on 10.0.108 working; <c>disable</c> would
    /// refuse them for no benefit, and anything wider lets the band drift back open.
    /// </summary>
    [Fact]
    public void Global_json_rolls_forward_only_within_the_shipped_feature_band()
    {
        Match m = Regex.Match(
            File.ReadAllText(Path.Combine(RepoRoot(), "global.json")),
            @"""rollForward""\s*:\s*""(\w+)""");

        m.Success.Should().BeTrue("global.json must state a rollForward rather than leave it defaulted");
        m.Groups[1].Value.Should().Be("latestPatch",
            "a wider rollForward silently re-admits the feature band whose Razor compiler cannot "
            + "build this repo, which is the failure global.json was added to prevent");
    }
}
