using System.Text.RegularExpressions;
using FluentAssertions;

namespace EntKube.Web.Tests;

/// <summary>
/// Component lookups outside Catalog that are not scoped to a tenant — a list that may only shrink.
///
/// <para><b>What this found.</b> Fourteen places outside the Catalog module fetched a cluster
/// component by id with no tenant predicate at all, <em>in methods that already took a
/// <c>tenantId</c></em>. The argument was there and unused, so each of them would answer about any
/// component in the installation. They are closed; this stops more appearing.</para>
///
/// <para><b>Why the count is not simply "every tenant-less lookup".</b> A flat sweep finds 36, and
/// 17 of those are <c>ComponentLifecycleService</c>, <c>ReleaseVolumeGuard</c> and the install
/// orchestrators — <b>Catalog's own code, acting on Catalog's own table</b>. The installer
/// legitimately works by component id; demanding a tenant there would be ceremony, not safety. So
/// this test asks the module map who owns the file and ignores the owner, which is the difference
/// between a number that means something and a number that is merely alarming.</para>
///
/// <para><b>The five that remain are a different problem.</b> None of them has a tenant in scope,
/// so closing them means changing a signature and then its callers — real work with a blast radius,
/// not a one-line predicate. They are named rather than counted so that fixing one is visibly
/// progress and adding one is visibly not.</para>
/// </summary>
public class ComponentTenantScopingTests
{
    private const string Web = "../../../../../src/EntKube.Web";

    /// <summary>
    /// Sites still fetching a component by id without a tenant, outside Catalog. Each needs a
    /// <c>tenantId</c> threaded in from its callers first.
    /// </summary>
    private static readonly string[] Baseline =
    [
        "ExternalRouteService.AddRouteAsync",
        "ExternalRouteService.ShadowedHostnamesAsync",
        "HeadscaleService.EnsureExternalRouteAfterInstallAsync",
        "StalwartService.StoreManifestAsync",
        "SupportMailboxService.DisposeOfAsync",
    ];

    [Fact]
    public void No_new_component_lookup_escapes_its_tenant()
    {
        List<string> found = Measure();

        found.Should().BeSubsetOf(Baseline,
            "a component fetched by id with no tenant predicate answers about any component in the "
            + "installation. Add `&& c.Cluster.TenantId == tenantId`, or ask ICatalogApi, whose "
            + "tenant is a parameter rather than a filter you have to remember");
    }

    [Fact]
    public void The_tenant_scoping_baseline_does_not_carry_slack()
    {
        List<string> found = Measure();

        found.Should().BeEquivalentTo(Baseline,
            "a site that gained a tenant predicate should leave this list, so the next reader is "
            + "not told there is more left than there is");
    }

    /// <summary>Which module owns each service, from <c>ModuleServiceMap</c>.</summary>
    private static IReadOnlyDictionary<string, string> Owners(string root)
    {
        string map = File.ReadAllText(Path.Combine(root, "Modules", "ModuleServiceMap.cs"));

        return Regex.Matches(map, @"\[typeof\(([\w\.<>]+)\)\]\s*=\s*Module\.(\w+)")
            .GroupBy(m => m.Groups[1].Value.Split('.')[^1])
            .ToDictionary(g => g.Key, g => g.First().Groups[2].Value, StringComparer.Ordinal);
    }

    private static List<string> Measure()
    {
        string root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, Web));

        Directory.Exists(root).Should().BeTrue(
            "this test reads the web project's source; if it moved, the test has to follow rather "
            + "than quietly stop checking anything");

        IReadOnlyDictionary<string, string> owners = Owners(root);
        owners.Should().NotBeEmpty("the module map is how this test knows whose table it is");

        List<string> found = [];

        foreach (string path in Directory
                     .EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
                     .Where(p => !p.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                              && !p.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")
                              && !p.Contains($"{Path.DirectorySeparatorChar}Migrations{Path.DirectorySeparatorChar}")))
        {
            string file = Path.GetFileNameWithoutExtension(path);

            // Catalog owns the table; its own code may address a component by id.
            if (owners.TryGetValue(file, out string? owner) && owner == "Catalog") continue;

            string[] lines = File.ReadAllLines(path);

            for (int i = 0; i < lines.Length; i++)
            {
                string trimmed = lines[i].TrimStart();
                if (!lines[i].Contains(".ClusterComponents", StringComparison.Ordinal)
                    || trimmed.StartsWith("//", StringComparison.Ordinal)
                    || trimmed.StartsWith("*", StringComparison.Ordinal)) continue;

                // The statement, which may run over several lines.
                string statement = string.Join(' ',
                    lines.Skip(i).Take(8).Select(l => l.Trim()));

                bool byComponentId = Regex.IsMatch(statement, @"c\.Id\s*==\s*\w*[Cc]omponent\w*");
                if (!byComponentId || statement.Contains("TenantId", StringComparison.Ordinal)) continue;

                found.Add($"{file}.{EnclosingMethod(lines, i)}");
            }
        }

        found.Sort(StringComparer.Ordinal);
        return found;
    }

    /// <summary>
    /// The method a line sits in. Walks back to the nearest declaration — which is why the
    /// baseline names methods rather than line numbers: a line number moves every time anything
    /// above it is edited, and then the list reads as churn instead of as a debt.
    /// </summary>
    private static string EnclosingMethod(string[] lines, int index)
    {
        for (int i = index; i >= 0; i--)
        {
            Match m = Regex.Match(lines[i],
                @"^\s+(?:public|private|internal|protected)\s+(?:static\s+)?(?:async\s+)?[\w<>?,\.\s\[\]]+?\s(\w+)\(");

            if (m.Success) return m.Groups[1].Value;
        }

        return "(unknown)";
    }
}
