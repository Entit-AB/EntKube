namespace EntKube.Web.Services.Clusters;

/// <summary>Whether a cluster already matches a manifest, differs from it, or could not say.</summary>
public enum DiffOutcome
{
    /// <summary>The cluster already holds what the manifest describes.</summary>
    Matches,

    /// <summary>The cluster differs, and <see cref="ManifestDiff.Text"/> says how.</summary>
    Differs,

    /// <summary>The question could not be answered — unreachable, timed out, no diff tooling.</summary>
    Unknown,
}

/// <summary>
/// The result of asking a cluster how it differs from a manifest, by server-side dry run.
///
/// <para><b>Three states, not two, because the third is the dangerous one.</b> "Could not tell"
/// has to be distinguishable from "no difference": a caller that collapses them reports a drifted
/// deployment as healthy the moment the cluster stops answering, which is precisely when someone
/// wants to know. <c>kubectl diff</c> encodes this as exit 0, exit 1 and anything else, and the
/// distinction is preserved here rather than flattened into a bool.</para>
/// </summary>
/// <param name="Outcome">Which of the three happened.</param>
/// <param name="Text">The diff, when there is one.</param>
/// <param name="Error">Why the answer is <see cref="DiffOutcome.Unknown"/>, when it is.</param>
public sealed record ManifestDiff(DiffOutcome Outcome, string? Text = null, string? Error = null);
