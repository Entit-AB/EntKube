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
/// <para><b>46 invocations, across seven files that contain no acknowledgment call whatsoever.</b>
/// <c>ComponentLifecycleService</c> holds 21 of them and is the service that installs, upgrades
/// and removes every catalog component on every cluster. <c>VaultService</c> holds 11, syncing
/// secrets into namespaces. <c>ClusterProvisioningService</c> holds 7.</para>
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
    /// </summary>
    private const int BaselineUngatedInvocations = 40;

    /// <summary>
    /// Files that run the CLI themselves and never acknowledge. Named rather than counted, so the
    /// failure message says which ones and a new entrant cannot hide inside a total.
    /// </summary>
    private static readonly string[] Baseline =
    [
        "ClusterEgressTunnel",
        "ClusterProvisioningService",
        "ComponentLifecycleService",
        "DockerRegistryService",
        "DriftDetectionService",
        "ReleaseVolumeGuard",
        "RolloutService",
        "VaultService",
        "VpnService",
    ];

    /// <summary>
    /// Excluded on purpose. <c>ClusterChangeGate</c> runs <c>kubectl diff</c> to compute the
    /// preview an operator acknowledges — it is the gate, so asking it to acknowledge itself is
    /// not a coherent requirement. <c>KubernetesClientFactory</c> is where the acknowledgment is
    /// raised for everyone else.
    /// </summary>
    private static readonly HashSet<string> IsTheGate =
        new(StringComparer.Ordinal) { "ClusterChangeGate", "KubernetesClientFactory" };

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
            if (IsTheGate.Contains(name)) continue;

            // Comments only, as with the custody metric: prose naming kubectl is not a call to it.
            string code = string.Join('\n', File.ReadLines(path)
                .Where(l => !l.TrimStart().StartsWith("//", StringComparison.Ordinal)
                         && !l.TrimStart().StartsWith("*", StringComparison.Ordinal)));

            int cli = Regex.Matches(code, "\"(?:kubectl|helm)\"\\s*,").Count
                      - LooksLocal(code);

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
