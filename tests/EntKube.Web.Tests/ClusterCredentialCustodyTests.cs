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
    /// <para><b>284 and falling</b>, from 508 once the count was corrected (below):
    /// <c>RedisService</c> 9, <c>CnpgService</c> 48, <c>ElasticsearchService</c> 42,
    /// <c>RabbitMQService</c> 37, <c>MongoService</c> 47, then <c>KafkaService</c> 15 and
    /// <c>RegisteredPostgresService</c> 16. None of those seven injects
    /// <see cref="IKubernetesClientFactory"/> any more, which is the clearest evidence a service
    /// is done: it cannot reach the raw-credential API even if a later change wanted to.</para>
    ///
    /// <para><c>TrustBundleService</c> and <c>CertificateDistributionService</c> each kept a local
    /// copy of kubectl plumbing purely because <c>ApplyManifestAsync</c> discarded kubectl's
    /// output and they log it. The factory now returns it, so both copies are gone. Their
    /// remaining sites are the change gate's: <c>PlannedClusterChange</c> <em>requires</em> a
    /// kubeconfig, so every gated call site must hold one. Fixing that is the next unit and needs
    /// a diff capability on the seam — the gate shells out to <c>kubectl diff</c>, which is not
    /// among the factory's eighteen methods.</para>
    ///
    /// <para>By module: Fleet 102 (unremarkable — it owns clusters, and
    /// <c>KubernetesOperationsService</c> alone is 72), Catalog 52, Telemetry 28, Delivery 26,
    /// Connectivity 23, DataServices 18, then single figures elsewhere.</para>
    ///
    /// <para><b>There are five ways to reach a cluster here, and the seam models two.</b> Worth
    /// knowing before planning the remainder, because "convert the next service" has twice turned
    /// out to mean something other than expected. By sites, and a file may use several: a
    /// hand-rolled process spawn 152, the typed Kubernetes SDK 145, <c>helm</c> 97,
    /// <see cref="IKubernetesClientFactory"/> 80, an HTTP proxy pool 29, and 28 that only hand the
    /// credential to someone else.</para>
    ///
    /// <para><see cref="Clusters.IClusterClient"/> covers the factory, and the SDK through
    /// <c>CreateSdkClient()</c> — ten services had hand-rolled those four lines. It has
    /// <em>no</em> helm operations at all, nothing for the proxy pool, and nothing for a service
    /// that wants to spawn its own process. Those need new capabilities on the seam or a rewrite
    /// of the call sites, and either is a design decision rather than more substitution.</para>
    ///
    /// <para><b>The change gate comes last, not next.</b> <c>PlannedClusterChange</c> requires a
    /// kubeconfig, so every gated call site holds one — but the gate is invoked from inside
    /// <see cref="IKubernetesClientFactory"/>, and not one of its eighteen methods receives a
    /// cluster identity to put there instead. Moving the gating up into the seam early would let
    /// every unconverted caller bypass the acknowledgment entirely, which is a safety regression
    /// rather than a refactor. The gate can only change once its callers already route through
    /// the seam.</para>
    ///
    private const int BaselineOccurrences = 284;

    /// <summary>Files doing so. A file that has stopped should not be able to start again quietly.</summary>
    private const int BaselineFiles = 48;

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
