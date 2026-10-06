using FluentAssertions;

namespace EntKube.Web.Tests;

/// <summary>
/// How widely a cluster's kubeconfig travels through the code, as a number that may only
/// come down.
///
/// <para><b>What this found.</b> Every one of the eighteen methods on
/// <c>IKubernetesClientFactory</c> takes a <c>string kubeconfig</c>. So any code that wants
/// to apply a manifest, read a pod or create a namespace must first load the cluster row and
/// pull the credential out of it — and 534 places do. That is why
/// <c>KubernetesCluster</c> is read by 31 services across module boundaries: not because they
/// want a cluster's name, but because the client factory's signature makes every caller a
/// custodian of its credentials.</para>
///
/// <para><b>Why it matters twice over.</b> As security, a credential that passes through
/// twelve modules has twelve places it can be logged, cached or mishandled. As architecture,
/// no module can stop depending on Fleet's cluster table while it needs the kubeconfig out of
/// it — so this is also a hard blocker on the decomposition, and one
/// <c>docs/decomposition.md</c> did not account for when it was written.</para>
///
/// <para><b>The fix is not in this test.</b> It is to give the factory methods a cluster id
/// and let them resolve the credential themselves, inside Fleet, so it never reaches a
/// caller. That is a 534-site change and belongs in its own piece of work. Until then this
/// stops the number growing, which is the cheap half of the job.</para>
/// </summary>
public class ClusterCredentialCustodyTests
{
    private const string Web = "../../../../../src/EntKube.Web";

    /// <summary>
    /// Places that take a cluster's credential out of the row, measured 2026-10-06.
    ///
    /// <para><b>409 and falling.</b> 508 after the count was corrected (below), then 499
    /// (<c>RedisService</c>), 451 (<c>CnpgService</c>) and 409 (<c>ElasticsearchService</c>).
    /// None of the three takes <see cref="IKubernetesClientFactory"/> any more, which is the
    /// clearest evidence a service is done: it cannot reach the raw-credential API even if a
    /// later change wanted to.</para>
    ///
    /// <para><b>508, corrected down from the 534 first reported.</b> The first count matched
    /// <c>.Kubeconfig</c> as plain text, which also caught <c>.KubeconfigSecretId</c> — a
    /// foreign key, not a credential — and <c>VaultSecretType.Kubeconfig</c>, an enum member.
    /// Thirty of the original number were those. Overstating the problem is no better than
    /// understating it, so the match now requires the property itself.</para>
    ///
    /// <para>Where it concentrates: <c>KubernetesOperationsService</c> 72, <c>CnpgService</c>
    /// 48, <c>MongoService</c> 47, <c>ElasticsearchService</c> 42, <c>RabbitMQService</c> 37.
    /// Five files hold nearly half of it, which is also where converting pays best.</para>
    /// </summary>
    private const int BaselineOccurrences = 409;

    /// <summary>Files doing so. A file that has stopped should not be able to start again quietly.</summary>
    private const int BaselineFiles = 54;

    private static (int Occurrences, int Files) Measure()
    {
        string root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, Web));

        Directory.Exists(root).Should().BeTrue(
            "this test reads the web project's source; if it moved, the test has to follow "
            + "rather than quietly stop checking anything");

        int occurrences = 0, files = 0;

        foreach (string path in Directory
                     .EnumerateFiles(root, "*.*", SearchOption.AllDirectories)
                     .Where(p => p.EndsWith(".cs", StringComparison.Ordinal)
                              || p.EndsWith(".razor", StringComparison.Ordinal))
                     .Where(p => !p.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                              && !p.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")
                              && !p.Contains($"{Path.DirectorySeparatorChar}Migrations{Path.DirectorySeparatorChar}")))
        {
            int n = Credential.Matches(File.ReadAllText(path)).Count;

            if (n > 0)
            {
                occurrences += n;
                files++;
            }
        }

        return (occurrences, files);
    }

    /// <summary>
    /// The credential property itself, and not the things that merely look like it.
    ///
    /// <para>The lookahead excludes <c>.KubeconfigSecretId</c> and anything else that
    /// continues the identifier; the lookbehind excludes
    /// <c>VaultSecretType.Kubeconfig</c>, which names a kind of secret rather than holding
    /// one. Both were counted by the first version of this test.</para>
    /// </summary>
    private static readonly System.Text.RegularExpressions.Regex Credential =
        new(@"(?<!VaultSecretType)\.Kubeconfig(?![A-Za-z0-9_])",
            System.Text.RegularExpressions.RegexOptions.Compiled);

    [Fact]
    public void The_cluster_credential_does_not_spread_any_further()
    {
        (int occurrences, int files) = Measure();

        occurrences.Should().BeLessThanOrEqualTo(BaselineOccurrences,
            "a new place handling a raw kubeconfig is a new place it can be logged or cached, "
            + "and one more caller that cannot stop depending on Fleet's cluster table. Ask "
            + "the client factory to act on a cluster id instead, so the credential stays "
            + "inside Fleet");

        files.Should().BeLessThanOrEqualTo(BaselineFiles,
            "a file that did not handle cluster credentials should not start");
    }

    [Fact]
    public void The_custody_baseline_does_not_carry_slack()
    {
        (int occurrences, int files) = Measure();

        occurrences.Should().Be(BaselineOccurrences,
            $"this has been reduced to {occurrences} — bank it by lowering the baseline, or "
            + "the slack lets the credential spread back for free");

        files.Should().Be(BaselineFiles, $"now {files} files; lower the baseline to match");
    }
}
