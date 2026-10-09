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
    /// Places that take a cluster's credential out of the row, measured 2026-10-07.
    ///
    /// <para><b>223 and falling</b>, from 508 once the count was corrected (below):
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
    /// <para>By module: Fleet 86 (unremarkable — it owns clusters, and
    /// <c>KubernetesOperationsService</c> alone is 56), Catalog 52, Telemetry 28, Delivery 26,
    /// Connectivity 23, DataServices 18, then single figures elsewhere.</para>
    ///
    /// <para><b>What the remaining sites actually do</b>, classified per site rather than per file —
    /// two earlier attempts to plan this inferred a site's purpose from its file's contents and were
    /// wrong both times. Of the 275 measured a day earlier: a <b>reachability guard</b> 96
    /// (<c>IsNullOrWhiteSpace(cluster.Kubeconfig)</c>, which is not use of the credential at all —
    /// <c>ForAsync</c> returning null already answers that question), handed to <b>one of the
    /// service's own helpers</b> 70, <b>building an SDK client</b> 31, a <b>local assignment</b> 16,
    /// the <b>gate's model</b> 16, and an actual <b>factory call</b> 13.</para>
    ///
    /// <para>The sixteen that went next were the first two categories meeting in one place: the
    /// routing, gateway, L4 and mesh paths of <c>KubernetesOperationsService</c> shared eight
    /// private helpers that each took a <c>string kubeconfig</c> only because their callers had
    /// one — <c>ApplyRawYamlAsync</c> alone was the funnel for ten applies. Converting the helpers
    /// rather than the public methods moved every one of their callers at once, and turned each
    /// caller's reachability guard into the resolution that answers it. See
    /// <c>RoutingClusterCallTests</c>, which is also the first coverage this service has had of
    /// what it sends a cluster: the kubectl invocation used to sit behind a <c>private static</c>
    /// method, so the seam and the coverage had to arrive together.</para>
    ///
    /// <para>So the bulk is not exotic transports. It is guards that become a resolution, and
    /// private helpers whose signatures take a credential because their callers had one. Both are
    /// the ordinary conversion this work has been doing.</para>
    ///
    /// <para><b>Helm is on the seam now, and it turned out to need two operations rather than a
    /// general passthrough — because five of the eight invocations do not touch a cluster at all.</b>
    /// <c>helm repo add</c>, <c>repo update</c> and <c>registry login</c> fetch an index over HTTP
    /// and write a local cache; two of them were being handed <c>--kubeconfig</c> anyway, which is
    /// why every census of this counted them. Checked rather than reasoned about: both succeed
    /// with a malformed kubeconfig and with a path that does not exist.</para>
    ///
    /// <para><b>helm is 8 invocations in 2 files</b>, not the 97 an earlier count suggested — that
    /// figure was credential sites in files that merely mention helm. The two files are
    /// <c>KubernetesOperationsService</c> and <c>ComponentLifecycleService</c>, which between them
    /// hold <b>72 of the 259</b> and use the SDK, their own process spawning and helm. That one
    /// file, now 56, is still the centre of gravity of everything left: the YAML-deployment path
    /// (apply, prune, delete-from-cluster), the four helm invocations, and eighteen remaining
    /// places that build an SDK client of their own.</para>
    ///
    /// <para><b>The change gate comes last, not next.</b> <c>PlannedClusterChange</c> requires a
    /// kubeconfig, so every gated call site holds one — but the gate is invoked from inside
    /// <see cref="IKubernetesClientFactory"/>, and not one of its eighteen methods receives a
    /// cluster identity to put there instead. Moving the gating up into the seam early would let
    /// every unconverted caller bypass the acknowledgment entirely, which is a safety regression
    /// rather than a refactor. The gate can only change once its callers already route through
    /// the seam.</para>
    ///
    private const int BaselineOccurrences = 223;

    /// <summary>
    /// Files doing so. A file that has stopped should not be able to start again quietly.
    ///
    /// <para>43: <c>VaultService</c> left the list entirely, which is the first time a file has.
    /// It had eleven own-process kubectl calls and held the credential for all of them.</para>
    /// </summary>
    private const int BaselineFiles = 43;

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
            int n = CodeOccurrences(File.ReadAllText(path));

            if (n > 0)
            {
                occurrences += n;
                files++;
            }
        }

        return (occurrences, files);
    }

    /// <summary>
    /// Occurrences in code, ignoring comments and doc prose.
    ///
    /// <para>Nine of the 284 this used to report were sentences about the credential rather than
    /// uses of it — including one in a comment this very test's author had written, which had to
    /// be reworded to get the number down. A security metric that a paragraph can move is a
    /// metric nobody should trust, so prose is now excluded and the figure means what it says.</para>
    /// </summary>
    private static int CodeOccurrences(string text)
    {
        int n = 0;

        foreach (string line in text.Split('\n'))
        {
            string trimmed = line.TrimStart();

            if (trimmed.StartsWith("//", StringComparison.Ordinal)
                || trimmed.StartsWith('*'))
            {
                continue;
            }

            n += Credential.Matches(line).Count;
        }

        return n;
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
