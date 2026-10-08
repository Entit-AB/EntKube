using System.Text.RegularExpressions;
using FluentAssertions;

namespace EntKube.Web.Tests;

/// <summary>
/// How many services still reach into Catalog's table directly instead of asking
/// <c>ICatalogApi</c> — a number that may only come down.
///
/// <para><b>Why this test exists at all.</b> Four module contracts were written, registered in
/// DI and unit-tested, and <em>nothing in production called any of them</em>. An interface with
/// no consumers does not decompose anything; it only looks like progress. This counts the thing
/// the contract was supposed to replace, so "we have a contract" and "the contract is used" stop
/// being the same claim.</para>
///
/// <para><b>What measuring it found.</b> 87 direct queries in 34 foreign services, and — missed
/// by the first attempt — <b>22 property writes in 9 of them</b>. The earlier count looked for
/// <c>.ClusterComponents.Add/Remove/Update</c>, found none, and concluded the table was read-only
/// outside Catalog. EF writes through change tracking, so <c>component.HelmValues = x</c> followed
/// by <c>SaveChangesAsync</c> is a write with no <c>Update()</c> anywhere near it. That false
/// conclusion was written into <c>ICatalogApi</c>'s own documentation as the reason it had no
/// write methods, which is how a measurement mistake becomes an API.</para>
///
/// <para><b>What is left is sequenced behind the credential work.</b> Of the direct queries, the
/// ones whose surrounding code also takes <c>cluster.Kubeconfig</c> cannot move: the contract
/// deliberately carries no credential, so those callers have to go through
/// <c>IClusterClient</c> first (docs/decomposition.md §4.0.1). That is seven services, and it
/// makes Phase 1 and the custody work the same dependency chain rather than two separate efforts.</para>
/// </summary>
public class CatalogContractAdoptionTests
{
    private const string Web = "../../../../../src/EntKube.Web";

    /// <summary>
    /// Direct <c>.ClusterComponents</c> uses in services Catalog does not own, measured
    /// 2026-10-07. Was 87 before Tempo, Mimir and Loki moved to the contract.
    /// </summary>
    private const int BaselineQueries = 66;

    /// <summary>
    /// Tracked writes to a <c>ClusterComponent</c> from services Catalog does not own. Was 22;
    /// the same three services accounted for six of them.
    /// </summary>
    private const int BaselineWrites = 3;

    [Fact]
    public void No_new_service_reaches_past_the_catalog_contract()
    {
        (int queries, int writes) = Measure();

        queries.Should().BeLessThanOrEqualTo(BaselineQueries,
            "a new direct query on Catalog's table is one more caller that cannot be moved off "
            + "the shared database later. Ask ICatalogApi instead");

        writes.Should().BeLessThanOrEqualTo(BaselineWrites,
            "a foreign module mutating a component through change tracking is the write this "
            + "contract exists to express — use MergeHelmValuesAsync or SetHelmValuesAsync");
    }

    [Fact]
    public void The_adoption_baseline_does_not_carry_slack()
    {
        (int queries, int writes) = Measure();

        queries.Should().Be(BaselineQueries,
            $"now {queries} — bank it by lowering the baseline, or the slack lets direct access "
            + "creep back for free");

        writes.Should().Be(BaselineWrites, $"now {writes} writes; lower the baseline to match");
    }

    /// <summary>
    /// Mutable scalar properties on <c>ClusterComponent</c>, read out of the entity itself so a
    /// new property cannot be written by a foreign module without this test noticing.
    /// </summary>
    private static IReadOnlySet<string> MutableProperties(string root)
    {
        string entity = File.ReadAllText(Path.Combine(root, "Data", "ClusterComponent.cs"));

        return Regex.Matches(entity, @"public\s+[\w<>?\[\]]+\s+(\w+)\s*\{\s*get;\s*set;")
            .Select(m => m.Groups[1].Value)
            .ToHashSet(StringComparer.Ordinal);
    }

    /// <summary>Which module owns each service, straight from <c>ModuleServiceMap</c>.</summary>
    private static IReadOnlyDictionary<string, string> ServiceModules(string root)
    {
        string map = File.ReadAllText(Path.Combine(root, "Modules", "ModuleServiceMap.cs"));

        return Regex.Matches(map, @"\[typeof\(([\w\.<>]+)\)\]\s*=\s*Module\.(\w+)")
            .GroupBy(m => m.Groups[1].Value.Split('.')[^1])
            .ToDictionary(g => g.Key, g => g.First().Groups[2].Value, StringComparer.Ordinal);
    }

    private static (int Queries, int Writes) Measure()
    {
        string root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, Web));

        Directory.Exists(root).Should().BeTrue(
            "this test reads the web project's source; if it moved, the test has to follow "
            + "rather than quietly stop checking anything");

        IReadOnlyDictionary<string, string> modules = ServiceModules(root);
        IReadOnlySet<string> properties = MutableProperties(root);

        modules.Should().NotBeEmpty("ModuleServiceMap is how this test knows whose code it is reading");
        properties.Should().NotBeEmpty("ClusterComponent's settable properties are what a write writes");

        int queries = 0, writes = 0;

        foreach (string path in Directory.EnumerateFiles(
                     Path.Combine(root, "Services"), "*.cs", SearchOption.AllDirectories))
        {
            string service = Path.GetFileNameWithoutExtension(path);

            // Services Catalog owns are supposed to use the table; so is anything the map does
            // not place in a module, which is not a service at all.
            if (!modules.TryGetValue(service, out string? owner) || owner == "Catalog") continue;

            string[] lines = File.ReadAllLines(path);
            string body = string.Join('\n', lines);

            // Anything named as a ClusterComponent — what a tracked write would be written
            // through. Deliberately any declaration position, not just a local assigned where it
            // is declared: the first version of this matched `ClusterComponent x = …` and
            // `foreach`, which silently missed every method parameter, and a check that cannot
            // see a write is worse than no check. `List<ClusterComponent> xs` does not match,
            // because the `>` sits where the name would be.
            HashSet<string> held = Regex.Matches(body, @"ClusterComponent[?!]?\s+(\w+)\b")
                .Select(m => m.Groups[1].Value)
                .ToHashSet(StringComparer.Ordinal);

            foreach (string line in lines)
            {
                string trimmed = line.TrimStart();

                // Prose about the credential inflated the custody metric until it was fixed to
                // read code only; the same mistake is not worth making twice.
                if (trimmed.StartsWith("//", StringComparison.Ordinal)
                    || trimmed.StartsWith("*", StringComparison.Ordinal)) continue;

                queries += Regex.Matches(line, @"\.ClusterComponents\b").Count;

                foreach (string name in held)
                {
                    foreach (Match m in Regex.Matches(line, $@"\b{Regex.Escape(name)}\.(\w+)\s*=(?!=)"))
                    {
                        if (properties.Contains(m.Groups[1].Value)) writes++;
                    }
                }
            }
        }

        return (queries, writes);
    }
}
