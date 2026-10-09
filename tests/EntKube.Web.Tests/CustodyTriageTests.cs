using System.Text.RegularExpressions;
using FluentAssertions;

namespace EntKube.Web.Tests;

/// <summary>
/// Of the cluster-credential sites still left, how many are in a method that could ask the seam
/// today — and how many cannot, because nothing has told them which tenant is asking.
///
/// <para><b>Why this number exists.</b> I had been choosing conversion targets by site count, and
/// it kept being the wrong order. <c>PrometheusService</c> looks like the obvious next job at
/// twenty sites; all twenty are unreachable, because <c>IClusterClientFactory.ForAsync</c> needs a
/// tenant and not one of that service's fifteen public methods receives one. Site count measures
/// the size of the edit. This measures whether the edit is possible.</para>
///
/// <para><b>What it says.</b> 32 of the 235 credential sites remaining are in a method with a
/// tenant in scope; the other 203 are not. The second group is gated on threading a tenant down from
/// callers — which is not a refactor but an authorization question, because a method that reaches
/// a cluster by id alone will reach any cluster in the installation. See
/// docs/decomposition.md §4.0.1.</para>
///
/// <para>The same file set as <see cref="ClusterCredentialCustodyTests"/>, deliberately, so the
/// two reconcile. My first attempt filtered to files with five or more sites and reported 20 and
/// 162 — figures that added up to nothing, which is the kind of thing that stops being noticed
/// once it is prose instead of an assertion.</para>
///
/// <para><b>What this is not.</b> It is an indicator, not a proof. "Has a tenant in scope" means
/// the enclosing declaration's own signature mentions one — so a private helper whose callers all
/// have a tenant counts as gated here while being a one-line thread-through in practice, and
/// walking back to the nearest declaration has mis-attributed a site before (a Stalwart helper,
/// PR #149). The absolute figure is therefore soft. What the ratchet is for is the direction: the
/// bucket with no excuse may not grow.</para>
/// </summary>
public class CustodyTriageTests
{
    private const string Web = "../../../../../src/EntKube.Web";

    /// <summary>
    /// Credential sites whose enclosing method already has a tenant, measured 2026-10-09. These
    /// are the ones that could move onto <c>IClusterClient</c> without touching a signature.
    ///
    /// <para><b>26 of 231, after StorageService. Corrected from 32 — and the correction matters
    /// more than the number.</b>
    /// See <see cref="Declaration"/>: a method returning a tuple was not recognised as a
    /// declaration, so sites inside it were judged by the signature of the method above. Six were
    /// credited with a tenant they did not have. Every figure this test has published, including
    /// the "35 of 241" that was used to pick targets, overstated the easy group by six.</para>
    ///
    /// <para>And the reduction this PR claimed for itself was in the wrong column: with the
    /// pattern fixed, its three conversions took <b>gated</b> from 212 to 206 and left reachable
    /// at 29. None of them was in a method with a tenant — which is exactly what the triage was
    /// built to tell apart, and it could not, because of the pattern above.</para>
    ///
    /// <para>Where the 26 are: <c>StalwartService</c> 4, <c>VeleroService</c> 4,
    /// <c>KyvernoPolicyService</c> 3, <c>DriftAdoptionService</c> 3, then two each in
    /// <c>VaultService</c>, <c>KeycloakService</c> and <c>StorageService</c>, and one each in
    /// <c>OpenLdapService</c>, <c>TenantService</c>, <c>IngressDashboardService</c> and
    /// <c>StalwartDnsService</c>. <c>ClusterClient</c>'s own two are the seam resolving the
    /// credential, which is the one place that should.</para>
    ///
    /// <para>⚠️ <c>VaultService</c>'s two are not the free conversion they look like. Its kubectl
    /// calls are <c>create secret generic --from-file=</c> with one temp file per value, the same
    /// deliberate pattern as <c>SyncComponentSecretsAsync</c>: moving them onto the seam hands the
    /// gate a Secret manifest, and the acknowledgment dialog shows the diff — the values — to
    /// whoever is at the screen. That is a disclosure decision, not a refactor.</para>
    /// </summary>
    private const int BaselineReachable = 26;

    [Fact]
    public void The_sites_that_could_already_ask_the_seam_do_not_multiply()
    {
        (int reachable, int gated) = Measure();

        reachable.Should().BeLessThanOrEqualTo(BaselineReachable,
            $"a new credential site in a method that already knows its tenant has no excuse — it "
            + $"could ask IClusterClient instead. ({gated} others are still gated on a tenant)");
    }

    [Fact]
    public void The_triage_baseline_does_not_carry_slack()
    {
        (int reachable, _) = Measure();

        reachable.Should().Be(BaselineReachable,
            $"now {reachable} — bank it, or the slack lets the easy ones come back for free");
    }

    private static readonly Regex Credential =
        new(@"(?<!VaultSecretType)\.Kubeconfig(?![A-Za-z0-9_])", RegexOptions.Compiled);

    /// <summary>
    /// A method declaration, so each site can be attributed to the signature above it.
    ///
    /// <para>⚠️ The return type must allow parentheses, or a method returning a tuple is not
    /// recognised as a declaration at all and every site inside it is attributed to the method
    /// <em>above</em> — judged by the wrong signature, and so put in the wrong group. That is not
    /// hypothetical: it is what made this test report 35 reachable when the number was 29. Six
    /// sites in tuple-returning methods were credited with a tenant that belonged to a different
    /// method, which is six "easy wins" that were never easy. The identical defect was naming the
    /// wrong method in <c>ComponentTenantScopingTests</c>.</para>
    /// </summary>
    private static readonly Regex Declaration =
        new(@"^\s+(?:public|private|internal|protected)\s+(?:static\s+)?(?:async\s+)?[\w<>?,\.\s\[\]\(\)]+?\s\w+\(",
            RegexOptions.Compiled);

    private static (int Reachable, int Gated) Measure()
    {
        string root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, Web));

        Directory.Exists(root).Should().BeTrue(
            "this test reads the web project's source; if it moved, the test has to follow rather "
            + "than quietly stop checking anything");

        int reachable = 0, gated = 0;

        foreach (string path in Directory
                     .EnumerateFiles(root, "*.*", SearchOption.AllDirectories)
                     .Where(p => p.EndsWith(".cs", StringComparison.Ordinal)
                              || p.EndsWith(".razor", StringComparison.Ordinal))
                     .Where(p => !p.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                              && !p.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")
                              && !p.Contains($"{Path.DirectorySeparatorChar}Migrations{Path.DirectorySeparatorChar}")))
        {
            string[] lines = File.ReadAllLines(path);

            // Declaration line numbers, so each site can be attributed to the one above it.
            List<int> declarations = [];
            for (int i = 0; i < lines.Length; i++)
            {
                if (Declaration.IsMatch(lines[i])) declarations.Add(i);
            }

            for (int i = 0; i < lines.Length; i++)
            {
                string trimmed = lines[i].TrimStart();

                // Code only. Prose naming the property inflated this metric once already.
                if (trimmed.StartsWith("//", StringComparison.Ordinal)
                    || trimmed.StartsWith("*", StringComparison.Ordinal)) continue;

                // Occurrences, not matching lines, so this and the custody metric add up: a
                // single line can take the credential twice.
                int occurrences = Credential.Matches(lines[i]).Count;
                if (occurrences == 0) continue;

                int declaration = declarations.LastOrDefault(d => d < i, -1);
                string signature = declaration < 0
                    ? ""
                    : string.Join(' ', lines.Skip(declaration).Take(4));

                if (signature.Contains("tenantId", StringComparison.Ordinal)
                    || signature.Contains("TenantId", StringComparison.Ordinal)) reachable += occurrences;
                else gated += occurrences;
            }
        }

        (reachable + gated).Should().BeGreaterThan(0, "the credential metric found nothing at all, "
            + "which means the regex or the path is wrong rather than that the work is done");

        return (reachable, gated);
    }
}
