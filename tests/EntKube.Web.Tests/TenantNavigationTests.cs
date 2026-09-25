using System.Text.RegularExpressions;
using FluentAssertions;

namespace EntKube.Web.Tests;

/// <summary>
/// The tenant tree's structure: every entry goes somewhere, nothing is declared twice.
///
/// <para><b>Why this reads the source.</b> The navigation is a set of <c>private static
/// readonly</c> arrays inside a Razor component, and the routing is a switch in the same
/// file. Reaching them properly would mean extracting the whole thing, and the tree is
/// being actively reshaped — three times in as many days — so a refactor of the file in
/// flux buys less than a check that works today.</para>
///
/// <para>The check itself is the one that has been run by hand after every one of those
/// reshuffles, which is reason enough to stop running it by hand. It is deliberately
/// narrow: shape, not content. Whether Routes belongs under Observability is a judgement
/// nobody should encode; whether it still opens anything is not.</para>
/// </summary>
public class TenantNavigationTests
{
    private const string Component =
        "../../../../../src/EntKube.Web/Components/Pages/Tenants/TenantExplorer.razor";

    private static string Source()
    {
        string path = Path.Combine(AppContext.BaseDirectory, Component);

        File.Exists(path).Should().BeTrue(
            $"the navigation lives in {Component}; if it moved, this test has to follow it "
            + "rather than quietly stop checking anything");

        return File.ReadAllText(path);
    }

    /// <summary>A leaf: <c>new("key", "Label", "bi-icon")</c>.</summary>
    private static List<string> LeafKeys(string source) =>
        [.. Regex.Matches(source, """new\("([a-z0-9-]+)",\s*"[^"]+",\s*"bi-[a-z0-9-]+"\)""")
            .Select(m => m.Groups[1].Value)];

    /// <summary>A group: <c>new("Name", "bi-icon", [ … ])</c>.</summary>
    private static List<string> GroupNames(string source) =>
        [.. Regex.Matches(source, """new\("([A-Z][^"]*)",\s*"bi-[a-z0-9-]+",""")
            .Select(m => m.Groups[1].Value)];

    private static HashSet<string> RoutedKeys(string source) =>
        [.. Regex.Matches(source, """case "([a-z0-9-]+)":""").Select(m => m.Groups[1].Value)];

    /// <summary>
    /// <b>The one that matters.</b> An entry in the tree with no case in the router renders
    /// a blank pane — the tree says the feature is there and clicking it shows nothing.
    /// Moving a leaf between groups is a copy and a delete, and the delete is the half that
    /// gets forgotten.
    /// </summary>
    [Fact]
    public void Every_entry_in_the_tree_opens_something()
    {
        string source = Source();

        List<string> orphaned = [.. LeafKeys(source).Distinct()
            .Where(key => !RoutedKeys(source).Contains(key))
            .Order()];

        string.Join(", ", orphaned).Should().BeEmpty(
            "an entry the router does not know about renders an empty pane, and the tree "
            + "goes on advertising it");
    }

    /// <summary>
    /// The same key in two groups highlights both when either is opened, because selection
    /// is by key. Harmless-looking and confusing to use.
    /// </summary>
    [Fact]
    public void No_entry_appears_in_two_places()
    {
        List<string> twice = [.. LeafKeys(Source())
            .GroupBy(k => k)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .Order()];

        string.Join(", ", twice).Should().BeEmpty(
            "selection is by key, so a duplicate highlights in both places at once");
    }

    /// <summary>
    /// Group names key the expand/collapse state. Two groups sharing a name open and close
    /// together, which is the sort of thing a rename introduces silently — and this tree
    /// has just been renamed twice.
    /// </summary>
    [Fact]
    public void No_two_groups_share_a_name()
    {
        List<string> twice = [.. GroupNames(Source())
            .GroupBy(n => n)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .Order()];

        string.Join(", ", twice).Should().BeEmpty(
            "GroupOpen keys off the name, so two groups called the same thing expand and "
            + "collapse as one");
    }

    /// <summary>
    /// A sanity check on the checks: if the patterns stop matching the file, the three
    /// tests above would pass by finding nothing at all, which is the failure mode of every
    /// test that reads source.
    /// </summary>
    [Fact]
    public void The_tree_is_found_at_all()
    {
        string source = Source();

        LeafKeys(source).Should().HaveCountGreaterThan(30,
            "the tree has dozens of entries; matching almost none means the pattern has "
            + "stopped fitting the file and these tests are checking nothing");

        GroupNames(source).Should().HaveCountGreaterThan(5);
        RoutedKeys(source).Should().HaveCountGreaterThan(30);
    }
}
