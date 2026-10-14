using System.Text.RegularExpressions;
using FluentAssertions;

namespace EntKube.Web.Tests;

/// <summary>
/// How much of EntKube changes a cluster without ever reaching the acknowledgment gate — a number
/// that may only come down.
///
/// <para><b>What this found.</b> The gate is described as covering "every human-triggered
/// Kubernetes mutation", and it does cover every mutation that goes through
/// <c>IKubernetesClientFactory</c>, because the factory raises the acknowledgment itself. But a
/// service that spawns <c>kubectl</c> or <c>helm</c> as its own process bypasses the factory
/// entirely, and the gate is never invoked at all — which is also why
/// <c>IClusterChangeRecorder</c> cannot see these: there is no outcome to record when nothing
/// asked.</para>
///
/// <para><b>46 invocations when first measured, across seven files that contain no acknowledgment
/// call whatsoever.</b> <c>ComponentLifecycleService</c> held 21 of them and is the service that
/// installs, upgrades and removes every catalog component on every cluster; it holds 15 now.
/// <c>VaultService</c> holds 11, syncing secrets into namespaces — and those are not the easy
/// win they look like, because they are <c>create secret generic --from-file=</c> with a temp file
/// per value, so routing them through the seam hands the acknowledgment dialog a Secret manifest
/// and shows the values to whoever is at the screen. <c>ClusterProvisioningService</c> held 7 and
/// now holds none that this counts: see <see cref="CannotReachTheSeam"/>.</para>
///
/// <para><b>This is a lower bound, deliberately.</b> A file is counted only when it has <em>no</em>
/// acknowledgment call at all, so one that acknowledges some paths and not others scores zero here
/// — <c>KubernetesOperationsService</c> has ten own-process invocations and fourteen
/// acknowledgments, and this test cannot tell whether they line up. Undercounting is the right
/// error for a ratchet to make: it can never overstate the problem, and every reduction it does
/// measure is real.</para>
///
/// <para><b>The fix is not in this test.</b> It is to route these call sites through
/// <c>IClusterClient</c>, which puts them behind the factory and therefore behind the gate — the
/// same work as docs/decomposition.md §4.0.1, arriving at the same place from the other side. The
/// helm invocations need a seam operation that does not exist yet, which is what stops this number
/// reaching zero today.</para>
/// </summary>
public class ClusterChangeGateCoverageTests
{
    private const string Web = "../../../../../src/EntKube.Web";

    /// <summary>
    /// Own-process <c>kubectl</c>/<c>helm</c> invocations in files that never acknowledge,
    /// measured 2026-10-08. Was 49, then 46 once <c>ApplyExternalRoutesAsync</c> moved onto the
    /// seam, then 45 once the component installer's helm run did.
    ///
    /// <para>Excludes <c>helm repo</c> and <c>helm registry</c> — see <see cref="LooksLocal"/>.
    /// Dropping those was a correction to this metric, not progress.</para>
    ///
    /// <para>41 after two changes landed together, each of which had banked the number it saw on
    /// its own branch. Two independent reductions to one baseline cannot both be right, and the
    /// slack half of this ratchet is what said so — it failed on <c>main</c> rather than letting a
    /// stale ceiling sit there granting four sites of free headroom.</para>
    ///
    /// <para>Then 40, then <b>33</b>: six of <c>ClusterProvisioningService</c>'s seven are
    /// excluded as unreachable (see <see cref="CannotReachTheSeam"/>) and the seventh was
    /// converted. The slack half earned itself again there — I banked 34, having subtracted the
    /// exemption and forgotten the conversion.</para>
    ///
    /// <para>Then <b>28</b>, from <c>ComponentLifecycleService</c>: the namespace LimitRange
    /// defaults, two stalled-workload reads and the ClusterIssuer listing. That file held 15 and
    /// holds 10.</para>
    ///
    /// <para>Now <b>17</b>, because <c>VaultService</c> is at zero. All eleven of its invocations
    /// went: the three namespace creations, the three delete/create Secret pairs, the live Secret
    /// read, and the <c>kubectl label</c> that followed every sync — the labels are part of the
    /// manifest now, so that call stopped existing rather than moving. What unblocked it was
    /// deciding what the acknowledgment dialog shows for a Secret; the values are removed by
    /// <c>SecretRedaction</c>, so routing these through the seam no longer puts credentials on a
    /// screen.</para>
    ///
    /// <para>Now <b>13</b>: <c>SyncComponentSecretsAsync</c> followed VaultService onto
    /// <c>ReplaceSecretAsync</c>. Its <c>--from-file</c> staging was kept deliberately for
    /// reasons that were all about the argument list — a value with a space split into several
    /// arguments, a value with a quote corrupted the command line, and <c>ps</c> shows arguments
    /// to anyone — and base64 in a 0600 manifest satisfies every one of them better. The
    /// prohibition was on <c>--from-literal</c>, which still stands.</para>
    ///
    /// <para>Now <b>12</b>: <c>RolloutService</c>'s own <c>kubectl</c>, which listed a namespace's
    /// Deployments and then ran <c>rollout undo</c> on each. It is the first gate conversion that
    /// needed a new <em>verb</em> rather than an existing one — <c>UndoDeploymentsAsync</c>, and a
    /// <c>ChangeVerb.Rollback</c> to describe it, because nothing in a rollback says what state is
    /// being moved to: the cluster is the one that remembers the revision, not EntKube. An
    /// automatic rollback still runs unattended, which the watcher declares.</para>
    ///
    /// <para>Of the 13, six are in <c>ComponentLifecycleService</c>: two apply an image-pull
    /// Secret, one applies a manifest from a URL, one is <c>kubectl describe</c>, and two are the
    /// rest of the stalled-workload path including <c>kubectl logs -c &lt;container&gt;</c>. The
    /// seam can express none of the last four: it has no apply-from-URL, no describe, and
    /// <c>GetPodLogsAsync</c> takes no container. Only the image-pull pair is work rather than a
    /// missing operation.</para>
    ///
    /// <para><b>6 — and the paragraph above was wrong about "cannot".</b> All six of
    /// <c>ComponentLifecycleService</c>'s went. The image-pull pair moved onto
    /// <c>ReplaceSecretAsync</c>, as predicted. The other four did not need the seam to express
    /// something impossible — they needed <b>two small additions</b>:
    /// <c>ApplyManifestFromUrlAsync</c> (the cluster fetches the URL; downloading it here would
    /// make EntKube the thing that must reach the internet) and a <c>container</c> parameter on
    /// <c>GetPodLogsAsync</c>. The describe had already become two JSON reads.</para>
    ///
    /// <para>The lesson is the same one <see cref="ComponentTenantScopingTests"/> recorded for its
    /// own exemption: "cannot" written next to a number is a claim, and this one survived three
    /// re-readings of this comment because it sounded like an architectural limit. It was a
    /// missing parameter.</para>
    ///
    /// <para><b>4, and the two halves of that are different in kind.</b> One is a removal:
    /// <c>DriftDetectionService</c>'s private <c>RunKubectlAsync</c> (and the <c>TryKill</c> only
    /// it used) had <b>no callers</b>. #137 moved the drift diff onto
    /// <c>IClusterClient.DiffManifestAsync</c> and left the runner behind, so for several PRs this
    /// number counted a kubectl launch that could not happen — though it was one edit away from
    /// happening again, which is why deleting it is progress and not bookkeeping.</para>
    ///
    /// <para>The other is a <b>measurement correction</b>, and it is the second one this metric has
    /// needed. <c>ReleaseVolumeGuard</c> was counted twice: once for the launch it really does, and
    /// once for <c>HelmExecutionResult.DescribeLaunchFailure("kubectl", ex)</c> — a helper that
    /// formats the message when a launch has already failed. Naming the program in an error string
    /// is not running it. Same correction as dropping <c>helm repo</c>: leaving it in made the
    /// number mean something other than its name, and a number in a doc comment is read as a
    /// fact.</para>
    ///
    /// <para><b>What the four are, and why none of them is a caller fix today:</b></para>
    /// <list type="bullet">
    ///   <item><s><c>DockerRegistryService</c></s> — <b>converted here.</b> It was a plain
    ///   <c>kubectl apply -f</c> of a Namespace+Secret manifest, and the seam has had both verbs
    ///   all along: <c>EnsureNamespaceAsync</c> then <c>ReplaceSecretAsync</c>. It wrote a registry
    ///   password into a namespace with nobody asked and nothing recorded. Its two credential reads
    ///   and its private process runner went with it.</item>
    ///   <item><s><c>ReleaseVolumeGuard</c></s> — <b>converted here, and it needed the gate to
    ///   learn something.</b> Its own comment said the direct <c>kubectl</c> was deliberate: "the
    ///   upgrade dialog already names every workload that will be taken down, so routing each
    ///   scale through the change gate would ask the operator to acknowledge, mid-run, something
    ///   they just approved". True of a per-call verb — and the conclusion was still wrong, because
    ///   what it bought was a service invisible to the gate, writing a kubeconfig to disk, with
    ///   nothing recorded. §5.4's own remedy is the one taken:
    ///   <c>IClusterChangeGate.DeclareConsequence(reason)</c>, which suppresses the second dialog
    ///   and records each scale as <c>AppliedAsConsequence</c>. The seam also gained the
    ///   <c>ScaleDeploymentAsync</c> it never had — <c>ChangeVerb.Scale</c> has existed since the
    ///   gate was written, so this is the fifth conversion blocked by a missing verb rather than by
    ///   a caller.</item>
    ///   <item><c>VpnService</c> — same again: expressible, two credential reads.</item>
    ///   <item><c>ClusterEgressTunnel</c> — <c>kubectl port-forward</c>, and this one is a
    ///   <b>seam addition rather than a caller fix</b>. Every verb on the seam is one-shot: run,
    ///   capture output, exit. A port-forward is a long-lived process that reports a local port on
    ///   stderr and must stay alive for as long as the tunnel is used, so there is no existing
    ///   shape to move it onto. It is also the one entry here that changes nothing on the cluster —
    ///   the metric counts reads on purpose (see <see cref="Measure"/>), and a tunnel is neither a
    ///   read nor a change but a channel.</item>
    /// </list>
    /// </summary>
    private const int BaselineUngatedInvocations = 2;

    /// <summary>
    /// Files that run the CLI themselves and never acknowledge. Named rather than counted, so the
    /// failure message says which ones and a new entrant cannot hide inside a total.
    ///
    /// <para>⚠️ <b><c>KubernetesOperationsService</c> is absent for a reason that will expire.</b>
    /// It runs <c>kubectl</c> six times and is excluded only because it <em>acknowledges</em>
    /// elsewhere in the file, which this metric takes as evidence that its CLI calls are gated.
    /// Five of those acknowledgments are its own <c>RequireAckAsync</c>; the rest have moved to
    /// <c>IClusterClient.AcknowledgeAsync</c>. When the last <c>RequireAckAsync</c> goes, the
    /// file stops matching and arrives here with six invocations at once. That is the metric
    /// being coarse, not a regression — the fix at that point is to teach
    /// <see cref="Measure"/> about the seam's acknowledgment, not to bank six.</para>
    /// </summary>
    private static readonly string[] Baseline =
    [
        "ClusterEgressTunnel",
        "VpnService",
        // ComponentLifecycleService left this list entirely — see the baseline's note, and
        // DriftDetectionService followed it: its runner was dead code, not an invocation.
    ];

    /// <summary>
    /// Excluded on purpose. <c>ClusterChangeGate</c> runs <c>kubectl diff</c> to compute the
    /// preview an operator acknowledges — it is the gate, so asking it to acknowledge itself is
    /// not a coherent requirement. <c>KubernetesClientFactory</c> is where the acknowledgment is
    /// raised for everyone else.
    /// </summary>
    private static readonly HashSet<string> IsTheGate =
        new(StringComparer.Ordinal) { "ClusterChangeGate", "KubernetesClientFactory" };

    /// <summary>
    /// Excluded because <b>no seam operation can ever express these</b>, so counting them made
    /// this number mean something other than its name — the same correction as dropping
    /// <c>helm repo</c>, for the same reason.
    ///
    /// <para><c>ClusterProvisioningService</c> creates a cluster that does not exist yet. Six of
    /// its seven invocations target either the ephemeral k3s bootstrap VM or the target cluster
    /// mid-build, and <c>IClusterClientFactory.ForAsync(tenantId, clusterId)</c> resolves a
    /// credential from a <em>registered</em> cluster row — which is written at step 9 of the
    /// sequence, after all six have run. The kubeconfigs they use come from SSH-ing the bootstrap
    /// VM and from clusterctl's output, not from the vault, so there is nothing for the seam to
    /// hand them.</para>
    ///
    /// <para>The seventh was convertible and is converted: <c>RecordNodesAsync</c> runs at step
    /// 10, after registration. The ordering is load-bearing and verified by reading the sequence
    /// rather than the file's header comment, which describes registration as step 5.</para>
    ///
    /// <para>Exempt from the <em>seam</em>, not from scrutiny. These still change a cluster with
    /// no acknowledgment — but the cluster is one being born, with no workloads on it, started by
    /// an operator who is watching the progress log. Asking them to acknowledge each step of a
    /// provision they just began would be ceremony.</para>
    /// </summary>
    private static readonly HashSet<string> CannotReachTheSeam =
        new(StringComparer.Ordinal) { "ClusterProvisioningService" };

    [Fact]
    public void No_new_code_changes_a_cluster_outside_the_gate()
    {
        (int invocations, List<string> files) = Measure();

        files.Should().BeSubsetOf(Baseline,
            "a new file running kubectl or helm as its own process is a new cluster mutation that "
            + "no operator will ever be asked about, and that IClusterChangeRecorder cannot even "
            + "record. Ask IClusterClient instead, which puts the call behind the gate");

        invocations.Should().BeLessThanOrEqualTo(BaselineUngatedInvocations,
            "one more invocation outside the gate");
    }

    [Fact]
    public void The_gate_coverage_baseline_does_not_carry_slack()
    {
        (int invocations, List<string> files) = Measure();

        invocations.Should().Be(BaselineUngatedInvocations,
            $"now {invocations} — bank it by lowering the baseline, or the slack lets ungated "
            + "mutation creep back for free");

        files.Should().BeEquivalentTo(Baseline,
            "a file that stopped running the CLI itself should leave this list");
    }

    /// <summary>
    /// Helm invocations that cannot reach a cluster, and so can never be gated no matter how much
    /// of EntKube moves onto the seam: <c>helm repo add</c>, <c>helm repo update</c> and
    /// <c>helm registry login</c> fetch an index or authenticate to a registry over HTTP and write
    /// helm's local cache.
    ///
    /// <para>Excluded because leaving them in made this number mean something other than its name.
    /// Two of them were being passed <c>--kubeconfig</c>, which is how they came to be counted in
    /// the first place; the flag is gone and it was never used. Checked rather than assumed —
    /// <c>repo add</c> and <c>repo update</c> both succeed with a malformed kubeconfig and with a
    /// path that does not exist.</para>
    /// </summary>
    /// <summary>
    /// A literal naming <c>kubectl</c>/<c>helm</c> as the <em>subject of a message</em> rather than
    /// as a program to run: <c>DescribeLaunchFailure("kubectl", ex)</c> formats the text shown when
    /// a launch has already failed.
    ///
    /// <para>Excluded for the same reason as <see cref="LooksLocal"/>: counting it made this number
    /// larger than the thing it is named after, and this number is quoted as a fact in
    /// docs/decomposition.md §5.4. Narrow on purpose — only a call whose name says it describes —
    /// because the containing regex is deliberately crude and must keep catching argument lists and
    /// object initializers alike.</para>
    /// </summary>
    private static int NamesTheProgramInAMessage(string code)
        => Regex.Matches(code, "Describe\\w*\\(\\s*\"(?:kubectl|helm)\"").Count;

    private static int LooksLocal(string code)
        => Regex.Matches(code, "\"helm\"\\s*,\\s*\\$?\"(?:repo|registry)\\b").Count
         + Regex.Matches(code, "\"helm\"\\s*,\\s*\\n\\s*\\$?\"(?:repo|registry)\\b").Count;

    /// <summary>
    /// Counts <c>"kubectl"</c>/<c>"helm"</c> as the program argument of a process call.
    ///
    /// <para>Deliberately does not parse the verb. An earlier attempt did, to separate mutations
    /// from reads, and could not see six invocations whose arguments were built into a variable
    /// first — including <c>deleteArgs</c> and <c>createArgs</c>, which are plainly mutations. A
    /// count that silently skips what it cannot parse is worse than one that includes a few reads,
    /// and routing a read through the seam is wanted anyway.</para>
    /// </summary>
    private static (int Invocations, List<string> Files) Measure()
    {
        string root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, Web));

        Directory.Exists(root).Should().BeTrue(
            "this test reads the web project's source; if it moved, the test has to follow rather "
            + "than quietly stop checking anything");

        int invocations = 0;
        List<string> files = [];

        foreach (string path in Directory
                     .EnumerateFiles(root, "*.*", SearchOption.AllDirectories)
                     .Where(p => p.EndsWith(".cs", StringComparison.Ordinal)
                              || p.EndsWith(".razor", StringComparison.Ordinal))
                     .Where(p => !p.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                              && !p.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")))
        {
            string name = Path.GetFileNameWithoutExtension(path);
            if (IsTheGate.Contains(name) || CannotReachTheSeam.Contains(name)) continue;

            // Comments only, as with the custody metric: prose naming kubectl is not a call to it.
            string code = string.Join('\n', File.ReadLines(path)
                .Where(l => !l.TrimStart().StartsWith("//", StringComparison.Ordinal)
                         && !l.TrimStart().StartsWith("*", StringComparison.Ordinal)));

            int cli = Regex.Matches(code, "\"(?:kubectl|helm)\"\\s*,").Count
                      - LooksLocal(code)
                      - NamesTheProgramInAMessage(code);

            if (cli <= 0) continue;

            bool acknowledges = Regex.IsMatch(code, @"gate\.AcknowledgeAsync|RequireAckAsync");
            if (acknowledges) continue;

            invocations += cli;
            files.Add(name);
        }

        files.Sort(StringComparer.Ordinal);
        return (invocations, files);
    }
}
