using System.Text.RegularExpressions;
using EntKube.Web.Services;
using FluentAssertions;

namespace EntKube.Web.Tests;

/// <summary>
/// The PromQL behind the cluster-health figures.
///
/// <para>These assert the <em>shape</em> of a query, which is as far as a unit test can reach —
/// there is no Prometheus here to answer it. That is a real limit and worth naming: the defects
/// these pin were all cases where a query was valid PromQL, returned a number, and the number was
/// of the wrong thing. Nothing about the query failing could have revealed them, so what is
/// checked is the specific confusion that produced each one.</para>
/// </summary>
public class ClusterHealthQueryTests
{
    private const string Web = "../../../../../src/EntKube.Web";

    /// <summary>
    /// ⭐ <c>count</c> over a 0/1 gauge counts series, not values, so the figure could not move.
    ///
    /// <para>A NotReady node keeps its <c>status="true"</c> series and simply holds 0, so
    /// <c>count(kube_node_status_condition{condition="Ready",status="true"})</c> returned the node
    /// total on every cluster. This asserts the aggregator directly, because that single word is
    /// the whole defect.</para>
    /// </summary>
    [Fact]
    public void Ready_nodes_sums_the_gauge_rather_than_counting_its_series()
    {
        ClusterHealthQueries.ReadyNodes.Should().StartWith("sum(");
        ClusterHealthQueries.ReadyNodes.Should().NotContain("count(",
            "kube_node_status_condition is a 0/1 gauge with a series per (node, condition, status) "
            + "— the status=\"true\" series exists for a NotReady node too, holding 0, so counting "
            + "series always yielded the total node count and a NotReady node could never appear");

        ClusterHealthQueries.ReadyNodes.Should().Contain("condition=\"Ready\"")
            .And.Contain("status=\"true\"");
    }

    /// <summary>
    /// A percentage over nodes must pool, because nodes are not the same size.
    ///
    /// <para>The mean of per-node ratios weights a 4 GiB control-plane node equally with a 128 GiB
    /// worker. ⚠️ Asserting the absence of <c>avg(</c> is the point: the correct and incorrect
    /// queries are both valid PromQL returning a plausible percentage, so there is no failure to
    /// detect — only a shape to insist on.</para>
    /// </summary>
    [Theory]
    [InlineData(nameof(ClusterHealthQueries.NodeMemoryPercent))]
    [InlineData(nameof(ClusterHealthQueries.NodeDiskPercent))]
    public void A_percentage_over_nodes_is_pooled_not_averaged(string which)
    {
        string q = which == nameof(ClusterHealthQueries.NodeMemoryPercent)
            ? ClusterHealthQueries.NodeMemoryPercent
            : ClusterHealthQueries.NodeDiskPercent;

        q.Should().NotContain("avg(",
            "the mean of per-node ratios weights every node equally regardless of its size, and is "
            + "furthest from the truth exactly when the cluster is most lopsided");

        Regex.Matches(q, @"\bsum\(").Should().HaveCount(2,
            "pooled means summing the numerator and the denominator separately");
    }

    /// <summary>
    /// ⚠️ The companion to the test above, and the reason it is worded about <em>nodes</em>.
    ///
    /// <para>CPU keeps <c>avg</c> on purpose. <c>node_cpu_seconds_total</c> is per (node, cpu), so
    /// each series carries exactly one core of weight and the unweighted mean over cores already
    /// <em>is</em> the pooled figure. Without this test, the natural next step after the memory fix
    /// is to "make CPU consistent", which would be a regression dressed as tidying.</para>
    /// </summary>
    [Fact]
    public void Cpu_keeps_its_average_because_its_series_are_cores_not_nodes()
    {
        ClusterHealthQueries.NodeCpuPercent.Should().Contain("avg(rate(node_cpu_seconds_total",
            "every cpu series is one core, so the mean over cores is the pooled figure — do not "
            + "change this to sum/sum to match NodeMemoryPercent");
    }

    /// <summary>
    /// "Not running" must mean "wrong", not merely "not Running".
    ///
    /// <para><c>phase!="Running"</c> swept in <c>Succeeded</c>, so every finished Job and CronJob
    /// pod counted toward a warning figure until its TTL collected it. The phases are enumerated
    /// so that a phase Kubernetes adds later stays out until someone decides it belongs.</para>
    /// </summary>
    [Fact]
    public void Pods_not_running_excludes_the_ones_that_finished_successfully()
    {
        ClusterHealthQueries.NotRunningPods.Should().NotContain("phase!=",
            "negating Running counts Succeeded, and completed CronJob pods accumulate — the panel "
            + "showed a permanent growing orange count on a healthy cluster");

        ClusterHealthQueries.NotRunningPods.Should().Contain("Pending")
            .And.Contain("Failed").And.Contain("Unknown");
        ClusterHealthQueries.NotRunningPods.Should().NotContain("Succeeded");
    }

    /// <summary>
    /// ⭐ The drift ratchet: a figure gets one definition.
    ///
    /// <para>Each of these queries had been written out at three or four call sites, and they
    /// disagreed — the health tile averaged cluster memory while the panel beside it pooled the
    /// same quantity, and disk excluded <c>tmpfs</c>/<c>overlay</c> in one place only. Fixing the
    /// arithmetic without closing that would leave the next copy free to drift again.</para>
    ///
    /// <para>⚠️ Comments are stripped first. This file's own subject — <c>ClusterHealthQueries</c>
    /// — documents each defect by quoting the query it replaced, so an unstripped scan would
    /// report the fix as the defect. That is not hypothetical: a test earlier in this programme
    /// matched its own explanatory comment.</para>
    /// </summary>
    [Fact]
    public void No_source_file_spells_a_health_query_out_for_itself()
    {
        string[] fingerprints =
        [
            "kube_node_status_condition",
            "node_memory_MemAvailable_bytes",
            "node_filesystem_avail_bytes",
            "kube_pod_status_phase",
            "machine_memory_bytes",
            "kubelet_running_pods",
        ];

        string root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, Web));
        Directory.Exists(root).Should().BeTrue(
            "this test reads the web project's source; if it moved, the test has to follow rather "
            + "than quietly stop checking anything");

        List<string> offenders = [];
        int filesRead = 0;

        foreach (string file in Directory.EnumerateFiles(root, "*.*", SearchOption.AllDirectories)
                     .Where(p => p.EndsWith(".cs", StringComparison.Ordinal)
                                 || p.EndsWith(".razor", StringComparison.Ordinal))
                     .Where(p => !p.Replace('\\', '/').Contains("/obj/")
                                 && !p.Replace('\\', '/').Contains("/bin/")))
        {
            if (Path.GetFileName(file) == "ClusterHealthQueries.cs")
            {
                continue; // the one place they are allowed to be spelled out
            }

            filesRead++;
            string body = Strip(File.ReadAllText(file));

            foreach (string metric in fingerprints.Where(m => body.Contains(m, StringComparison.Ordinal)))
            {
                offenders.Add($"{Path.GetFileName(file)} spells out {metric}");
            }
        }

        // Survives success: were this to read nothing — a moved root, a build that staged no
        // sources — the offender list would be empty and this test would pass while checking
        // nothing at all.
        filesRead.Should().BeGreaterThan(300,
            "the scan must have actually read the web project; an empty corpus makes every "
            + "assertion below vacuously true");

        offenders.Should().BeEmpty(
            "every cluster-health figure is defined once in ClusterHealthQueries; a second "
            + "spelling is how the health tile and the metrics panel came to disagree about "
            + "cluster memory");
    }

    /// <summary>
    /// Blanks comments while keeping line positions, after <c>TypedSdkMutationTests.Strip</c>.
    ///
    /// <para>⚠️ <b>Trailing comments are stripped too, which the original did not do.</b> That
    /// version passed this file's drift scan over everything except one line —
    /// <c>rNode = nodes; // kubelet_running_pods only reports healthy nodes</c> — where prose
    /// after real code named a metric. A whole-line-only strip reported that comment as a
    /// duplicated query. Measuring code means measuring code, so the trailing case is handled
    /// rather than the comment reworded to suit the test.</para>
    ///
    /// <para>The <c>://</c> guard keeps a URL in a string literal from being mistaken for the
    /// start of a comment. PromQL contains no <c>//</c>, so nothing else on these lines needs
    /// protecting.</para>
    ///
    /// <para>⚠️ Returns a single string by design: an overload taking and returning
    /// <c>string[]</c> made four absence assertions vacuously true in an earlier test in this
    /// programme, because the inferred element type was wrong.</para>
    /// </summary>
    private static string Strip(string source) =>
        string.Join('\n', source.Split('\n').Select(l =>
        {
            string t = l.TrimStart();
            if (t.StartsWith("//", StringComparison.Ordinal)
                || t.StartsWith("*", StringComparison.Ordinal)
                || t.StartsWith("/*", StringComparison.Ordinal)
                || t.StartsWith("@*", StringComparison.Ordinal))
            {
                return "";
            }

            for (int i = 1; i < l.Length - 1; i++)
            {
                if (l[i] == '/' && l[i + 1] == '/' && l[i - 1] != ':')
                {
                    return l[..i];
                }
            }

            return l;
        }));
}
