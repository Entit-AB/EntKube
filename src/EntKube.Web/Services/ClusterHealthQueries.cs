namespace EntKube.Web.Services;

/// <summary>
/// The PromQL behind every cluster-health figure the product shows, defined once.
///
/// <para><b>Why these are constants.</b> Each of these queries was written out as a string
/// literal at three or four call sites — the health summary, the metrics history, and the
/// infrastructure panels — and they drifted. Cluster memory was computed one way in the health
/// tile and another way in the panel the operator can open beside it, so the same product gave
/// two different answers for the same quantity. Disk excluded <c>tmpfs</c> and <c>overlay</c>
/// in one place and not the other. A figure is a single fact about a cluster, so it gets a
/// single definition; <see cref="PrometheusService.RestartsInWindowQuery"/> set this precedent.</para>
///
/// <para>⚠️ These strings are not URL-encoded. Encode with <c>Q(...)</c> at the call site, as
/// the existing queries do.</para>
/// </summary>
public static class ClusterHealthQueries
{
    /// <summary>
    /// Cluster CPU in use, as a percentage.
    ///
    /// <para><b><c>avg</c> is correct here, and deliberately kept.</b> <c>node_cpu_seconds_total</c>
    /// carries one series per (node, cpu), so <c>rate(...)</c> is the idle fraction of <em>one
    /// core</em> and every series carries exactly one core of weight. The unweighted mean over
    /// cores is therefore already the pooled figure: total idle capacity over total capacity.
    /// This is the opposite of <see cref="NodeMemoryPercent"/>, where the series are nodes and
    /// nodes are not the same size — so do not "fix" this one to match that one.</para>
    /// </summary>
    public static readonly string NodeCpuPercent =
        $"100 - (avg(rate(node_cpu_seconds_total{{mode=\"idle\"}}[{PromRateWindow.Token}])) * 100)";

    /// <summary>
    /// Cluster memory in use, as a percentage, pooled over nodes.
    ///
    /// <para>⚠️ This was <c>100 - (avg(node_memory_MemAvailable_bytes / node_memory_MemTotal_bytes)
    /// * 100)</c> — the mean of per-node ratios, which weights a 4 GiB node the same as a 128 GiB
    /// one. On a cluster with a few small control-plane nodes and large workers, that reads several
    /// points away from the truth in whichever direction the small nodes happen to sit, and it is
    /// furthest off exactly when the cluster is most lopsided.</para>
    ///
    /// <para>The pooled form here is not new to the product: the infrastructure panel's
    /// "Memory utilization" tile already used it, which is how the disagreement was noticed. The
    /// two now share this constant and cannot disagree again.</para>
    /// </summary>
    public const string NodeMemoryPercent =
        "100 * (1 - sum(node_memory_MemAvailable_bytes) / sum(node_memory_MemTotal_bytes))";

    /// <summary>
    /// Root-filesystem use across nodes, as a percentage, pooled.
    ///
    /// <para>Pooled for the same reason as <see cref="NodeMemoryPercent"/>, and the
    /// <c>fstype</c> exclusion is applied to <em>both</em> sides. The panel filtered only the
    /// numerator; that happens to work, because PromQL pairs the two vectors on their full label
    /// sets and <c>fstype</c> is one of those labels, so the filter propagates. Relying on that
    /// is a trap for whoever edits it next, so the intent is written out instead of inferred.</para>
    ///
    /// <para>⚠️ Pooling answers "how full is the cluster's disk", NOT "is any node about to run
    /// out" — one node at 98% among nine at 20% is invisible in both the old mean and this sum.
    /// That is a <c>max by (instance)</c> question and a different figure; it is not answered
    /// here and is not pretended to be.</para>
    /// </summary>
    public const string NodeDiskPercent =
        "100 * (1 - sum(node_filesystem_avail_bytes{mountpoint=\"/\",fstype!~\"tmpfs|overlay\"})"
        + " / sum(node_filesystem_size_bytes{mountpoint=\"/\",fstype!~\"tmpfs|overlay\"}))";

    /// <summary>Nodes in the cluster. One series per node, each valued 1, so a count is the count.</summary>
    public const string TotalNodes = "count(kube_node_info)";

    /// <summary>
    /// Nodes that are actually Ready.
    ///
    /// <para>⭐ <b>This was <c>count(kube_node_status_condition{condition="Ready",status="true"})</c>,
    /// which always returned the total node count.</b> <c>kube_node_status_condition</c> is a
    /// 0/1 gauge with a series for every (node, condition, status) triple: a NotReady node still
    /// has a <c>status="true"</c> series, holding the value 0. <c>count</c> counts
    /// <em>series</em>, not values, so the series existed either way and the figure could not
    /// move. "Ready nodes" equalled "total nodes" on every cluster, forever, and a NotReady node
    /// — the one thing this figure exists to show — was the case it was structurally unable to
    /// report.</para>
    ///
    /// <para>The line directly beneath it in the health summary already read
    /// <c>sum(kube_pod_status_phase{...})</c> for the identical 0/1-gauge shape, so the correct
    /// form was sitting one line away the whole time.</para>
    /// </summary>
    public const string ReadyNodes =
        "sum(kube_node_status_condition{condition=\"Ready\",status=\"true\"})";

    /// <summary>Pods in the cluster. One series per pod, valued 1.</summary>
    public const string TotalPods = "count(kube_pod_info)";

    /// <summary>Running pods. A 0/1 gauge per (pod, phase), so this sums values.</summary>
    public const string RunningPods = "sum(kube_pod_status_phase{phase=\"Running\"})";

    /// <summary>
    /// Pods that are not running <em>and that is a problem</em>.
    ///
    /// <para>⚠️ This was <c>sum(kube_pod_status_phase{phase!="Running"})</c>, labelled "Pods not
    /// running" and drawn in warning orange. <c>Succeeded</c> is not Running, so every completed
    /// Job and CronJob pod counted — and those accumulate until their TTL or history limit
    /// collects them. A cluster running a five-minute CronJob therefore showed a permanent,
    /// growing orange count with nothing wrong with it, which is the kind of number that teaches
    /// an operator to stop reading the panel.</para>
    ///
    /// <para>The phases are enumerated rather than negated, so a phase added to Kubernetes later
    /// is excluded until someone decides it belongs here, instead of silently joining a warning
    /// count.</para>
    /// </summary>
    public const string NotRunningPods =
        "sum(kube_pod_status_phase{phase=~\"Pending|Failed|Unknown\"})";

    /// <summary>
    /// The kubelet/cAdvisor fallbacks, for a Prometheus scraping neither kube-state-metrics nor
    /// node-exporter. These were already pooled (<c>sum / sum</c>) and are unchanged — they are
    /// here so that both branches of a health query are read in one place.
    /// </summary>
    public static readonly string KubeletCpuPercent =
        $"100 * sum(rate(container_cpu_usage_seconds_total{{container!=\"\",namespace!=\"\"}}[{PromRateWindow.Token}])) / sum(machine_cpu_cores)";

    /// <inheritdoc cref="KubeletCpuPercent"/>
    public const string KubeletMemoryPercent =
        "100 * sum(container_memory_working_set_bytes{container!=\"\",namespace!=\"\"}) / sum(machine_memory_bytes)";

    /// <inheritdoc cref="KubeletCpuPercent"/>
    public const string KubeletNodes = "count(count by (node) (kubelet_running_pods))";

    /// <inheritdoc cref="KubeletCpuPercent"/>
    public const string KubeletRunningPods = "sum(kubelet_running_pods)";
}
