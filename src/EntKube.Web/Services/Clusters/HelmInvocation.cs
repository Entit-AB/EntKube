namespace EntKube.Web.Services.Clusters;

/// <summary>
/// One helm invocation against a cluster, described by the caller and performed by the seam.
///
/// <para><b>Why this is not a modelled Helm API.</b> A first sketch had
/// <c>HelmUpgradeAsync(release, chart, version, values…)</c>, which looked tidier and did not fit
/// either caller. Both resolve the chart reference themselves and in three different ways — a repo
/// alias they have just added, a full OCI URI, or a chart directory <em>extracted from the release
/// secret already on the cluster</em> — and both interleave the helm run with kubectl work around
/// it: ensuring the namespace carries a default LimitRange before installing, and describing
/// stalled workloads afterwards when <c>--wait</c> times out. Modelling the chart surface here
/// would have moved that orchestration into the seam, where it does not belong.</para>
///
/// <para>So the seam takes what it actually needs to do its two jobs: the <see cref="Arguments"/>
/// to run, and enough identity (<see cref="Verb"/>, <see cref="ReleaseName"/>,
/// <see cref="Namespace"/>) for the acknowledgment gate to describe the change to an operator.
/// <b>The caller never names the credential</b> — the seam appends <c>--kubeconfig</c> itself,
/// which is the entire point.</para>
///
/// <para><b>Values files cross as paths, not contents.</b> A values file routinely holds a
/// secret — <c>grafana.adminPassword</c> is a catalog form field — so the caller writes it with
/// <c>SecretFile.WriteAsync</c> and passes <c>--values &lt;path&gt;</c> in
/// <see cref="Arguments"/>. Nothing sensitive travels through this record, and nothing sensitive
/// reaches the gate, whose helm preview is the summary rather than a diff.</para>
/// </summary>
/// <param name="Verb">
/// The helm verb as it appears on the command line — <c>upgrade --install</c>, <c>uninstall</c>.
/// Carried separately from <see cref="Arguments"/> only so the gate can say what is about to
/// happen; it is prepended to the arguments unchanged.
/// </param>
/// <param name="ReleaseName">The release being changed.</param>
/// <param name="Namespace">The namespace it lives in.</param>
/// <param name="Arguments">
/// Everything after the verb: the release name, the chart reference, and the flags. Passed through
/// verbatim, minus <c>--kubeconfig</c>, which the seam supplies.
/// </param>
/// <param name="Summary">
/// A one-line description for the acknowledgment dialog. Worth supplying: the gate's own fallback
/// for a helm change is the bare words "Helm manifest", and the callers already know the chart and
/// version an operator would want to see.
/// </param>
public sealed record HelmInvocation(
    string Verb,
    string ReleaseName,
    string Namespace,
    IReadOnlyList<string> Arguments,
    string? Summary = null);
