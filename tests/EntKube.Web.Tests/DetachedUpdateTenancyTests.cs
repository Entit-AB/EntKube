using System.Text.RegularExpressions;
using FluentAssertions;

namespace EntKube.Web.Tests;

/// <summary>
/// Where a whole-row <c>Update</c> gets permission from.
///
/// <para><b>Why this test exists.</b> <c>db.Set.Update(entity)</c> on a <em>detached</em> entity
/// writes every column of whichever row the entity's Id names. So the Id alone decides what is
/// rewritten, and the entity's own foreign keys decide where the row ends up afterwards. That is
/// not defacement — it is a move: the row leaves one tenant's list, keeps its CreatedAt, and
/// arrives in another's.</para>
///
/// <para>Seven methods in the web project do this, and five of them did it with no tenant in
/// scope at all (<c>KnowledgeService</c> 2, <c>StalwartService</c> 2,
/// <c>MailTriageRuleService</c> 1). Two were already written correctly and are what the rule
/// below was derived from rather than invented against: <c>OnCallService.SaveSubconsultantAsync</c>
/// checks the row then pins <c>TenantId</c>, and <c>ContractService.SaveContactAsync</c> checks
/// the parent customer for the create path AND the row for the update path.</para>
///
/// <para><b>The rule.</b> A detached <c>Update(p)</c> where <c>p</c> is a parameter must be
/// preceded, in the same method, by a read that compares a TenantId to the method's own tenant.
/// Nine other <c>Update</c> calls in the project load their row first and are deliberately not
/// covered: a read happened, so a predicate was available on it.</para>
///
/// <para>⚠️ <b>What this test must not do is quietly stop matching.</b> A regex that finds
/// nothing reports zero violations, which reads as compliance — the failure mode
/// <see cref="VacuousTenantCheckTests"/> hit when one level of indirection hid thirteen sites.
/// So <see cref="Every_detached_update_is_still_found"/> asserts a floor on the number of sites
/// the pattern finds, and the pattern collapsing fails the suite rather than passing it.</para>
/// </summary>
public class DetachedUpdateTenancyTests
{
    private const string Web = "../../../../../src/EntKube.Web";

    /// <summary>Detached whole-row updates that reach a tenant-scoped read first. All of them.</summary>
    private const int BaselineViolations = 0;

    /// <summary>
    /// How many detached sites exist at all. A floor, not a target: the point is that the
    /// pattern is still finding the thing it names.
    /// </summary>
    private const int KnownDetachedSites = 7;

    private static readonly Regex LineComment = new(@"//.*$", RegexOptions.Compiled | RegexOptions.Multiline);
    private static readonly Regex UpdateCall = new(@"\.Update\((\w+)\)", RegexOptions.Compiled);
    private static readonly Regex MemberStart =
        new(@"^    (?:public|private|internal|protected)\s", RegexOptions.Compiled);

    private sealed record Site(string Where, string Method, bool ScopedRead);

    private static List<Site> Measure()
    {
        string root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, Web));

        Directory.Exists(root).Should().BeTrue(
            "this test reads the web project's source; if it moved, the test has to follow "
            + "rather than quietly stop checking anything");

        List<Site> sites = [];

        foreach (string path in Directory
                     .EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
                     .OrderBy(p => p, StringComparer.Ordinal))
        {
            // Comments first, so prose describing one of these calls is not counted as one.
            // Three of this programme's own doc comments quote the old code verbatim.
            string[] lines = [.. LineComment.Replace(File.ReadAllText(path), "").Split('\n')];

            for (int i = 0; i < lines.Length; i++)
            {
                Match call = UpdateCall.Match(lines[i]);
                if (!call.Success || lines[i].Contains("UpdateRange", StringComparison.Ordinal))
                {
                    continue;
                }

                string variable = call.Groups[1].Value;

                int start = -1;
                for (int j = i; j >= 0 && j > i - 200; j--)
                {
                    if (MemberStart.IsMatch(lines[j])) { start = j; break; }
                }

                if (start < 0)
                {
                    continue;
                }

                string signature = string.Join(" ", lines[start..Math.Min(lines.Length, start + 6)]);
                string head = signature.Contains('{', StringComparison.Ordinal)
                    ? signature[..signature.IndexOf('{', StringComparison.Ordinal)]
                    : signature;

                // Is the updated variable a parameter of this method — i.e. detached, supplied
                // by the caller — rather than a row this method loaded itself?
                bool detached = Regex.IsMatch(head, $@"\b\w[\w<>?\[\],. ]*\s{Regex.Escape(variable)}\b\s*[,)]");
                if (!detached)
                {
                    continue;
                }

                string body = string.Join("\n", lines[start..(i + 1)]);
                bool scoped = body.Contains("TenantId == tenantId", StringComparison.Ordinal);

                string method = Regex.Match(head, @"\b(\w+)\s*\(").Groups[1].Value;
                sites.Add(new Site(
                    $"{Path.GetFileName(path)}:{i + 1}", method, scoped));
            }
        }

        return sites;
    }

    /// <summary>
    /// A detached <c>Update</c> rewrites whatever row the Id names, so the method has to have
    /// established whose row it is before reaching this line.
    /// </summary>
    [Fact]
    public void No_detached_update_writes_a_row_it_has_not_checked()
    {
        List<Site> unchecked_ = [.. Measure().Where(s => !s.ScopedRead)];

        unchecked_.Count.Should().BeLessThanOrEqualTo(BaselineViolations,
            "a detached Update writes every column of the row its entity's Id names — including "
            + "the columns that say which tenant and which parent the row belongs to. Load the "
            + "row with the tenant in the predicate first, then copy the fields onto it.\n  "
            + string.Join("\n  ", unchecked_.Select(s => $"{s.Where} {s.Method}")));
    }

    /// <summary>Bank an improvement by lowering the baseline, or the slack lets it come back.</summary>
    [Fact]
    public void The_ratchet_does_not_carry_slack()
    {
        List<Site> sites = Measure();

        sites.Count(s => !s.ScopedRead).Should().Be(BaselineViolations,
            "if this is now lower than the baseline, lower the baseline in the same change");
    }

    /// <summary>
    /// ⚠️ The guard on the guard. If the pattern stops matching, every other assertion here
    /// passes while checking nothing — which is how <see cref="VacuousTenantCheckTests"/> read
    /// zero with thirteen live sites.
    /// </summary>
    [Fact]
    public void Every_detached_update_is_still_found()
    {
        List<Site> sites = Measure();

        sites.Count.Should().BeGreaterThanOrEqualTo(KnownDetachedSites,
            $"the project has {KnownDetachedSites} detached whole-row updates; finding fewer "
            + "means the pattern stopped matching rather than the code changing");

        sites.Should().AllSatisfy(s => s.Method.Should().NotBeEmpty(
            "a site whose method could not be named is a site this test cannot reason about"));
    }
}
