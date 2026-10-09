using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace EntKube.Web.Services.ClusterChanges;

/// <summary>
/// Default <see cref="IClusterChangeGate"/>. Computes a server-side dry-run diff via kubectl,
/// then — when an interactive sink is registered — blocks for operator acknowledgment.
///
/// Scoped per DI scope (per Blazor circuit). See <see cref="IClusterChangeGate"/> for the
/// "interactive only" scope model.
/// </summary>
public sealed class ClusterChangeGate : IClusterChangeGate
{
    private readonly IConfiguration _config;
    private readonly ILogger<ClusterChangeGate> _logger;
    private readonly IClusterChangeRecorder _recorder;
    private IClusterChangeAckSink? _sink;
    private string? _unattendedReason;

    /// <param name="recorder">
    /// Where outcomes are written. Optional so that the tests about acknowledgment can keep
    /// constructing a gate without one; in the application it is always supplied.
    /// </param>
    public ClusterChangeGate(
        IConfiguration config,
        ILogger<ClusterChangeGate> logger,
        IClusterChangeRecorder? recorder = null)
    {
        _config = config;
        _logger = logger;
        _recorder = recorder ?? new NullClusterChangeRecorder();
    }

    /// <summary>Master switch. When false, the gate never blocks (diffs are never computed).</summary>
    private bool Enabled => _config.GetValue("ClusterChanges:RequireAcknowledgment", true);

    public IDisposable RegisterSink(IClusterChangeAckSink sink)
    {
        _sink = sink;
        return new Unregister(this, sink);
    }

    public IDisposable DeclareUnattended(string reason)
    {
        if (string.IsNullOrWhiteSpace(reason))
        {
            // A blank reason is a declaration that explains nothing, and the reason is the entire
            // value of the declaration — it is what the audit trail has to show.
            throw new ArgumentException(
                "An unattended declaration must say what the work is.", nameof(reason));
        }

        _unattendedReason = reason;
        return new Undeclare(this, reason);
    }

    public async Task AcknowledgeAsync(PlannedClusterChange change, CancellationToken ct = default)
    {
        // Bypass when the feature is off. An operator opting out of the dialog is a different
        // thing from nobody being there to ask, and only the second one grows as more work moves
        // off the circuit — so they are recorded separately.
        if (!Enabled)
        {
            await RecordAsync(change, ClusterChangeOutcome.GateDisabled, null, ct);
            return;
        }

        if (_sink is null)
        {
            // Nobody to ask. What happens now is the context's own declaration, not a default:
            // this used to apply straight through, which is the bypass an in-cluster agent would
            // have inherited (docs/decomposition.md §5.4).
            if (_unattendedReason is null)
            {
                _logger.LogError(
                    "Cluster change REFUSED on {Cluster}: no operator to ask and nothing declared "
                    + "this context may act unattended. {Change}",
                    change.ClusterLabel, change.Describe());

                await RecordAsync(change, ClusterChangeOutcome.RefusedUndeclared, null, ct);

                throw new InvalidOperationException(
                    $"Refused to change {change.ClusterLabel}: {change.Describe()}. There is no "
                    + "operator to acknowledge it and this context has not declared that it may "
                    + "act unattended. Wrap the work in IClusterChangeGate.DeclareUnattended("
                    + "\"<what this work is>\") where its scope is created.");
            }

            _logger.LogInformation(
                "Cluster change applied unattended on {Cluster} as {Reason}: {Change}",
                change.ClusterLabel, _unattendedReason, change.Describe());

            await RecordAsync(change, ClusterChangeOutcome.AppliedUnattended, null, ct);
            return;
        }

        ClusterChangeDiff diff;
        try
        {
            diff = await ComputeDiffAsync(change, ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Fail safe: never let a diff-computation failure silently apply a change.
            _logger.LogWarning(ex, "Could not compute cluster change diff for {Change}; asking for acknowledgment anyway", change.Describe());
            diff = new ClusterChangeDiff
            {
                HasChanges = true,
                DiffText = change.Manifest ?? change.Patch ?? change.Describe(),
                Warning = $"Could not compute a live diff ({ex.Message}). Review the intended change below.",
            };
        }

        // A clean dry-run that reports no change: nothing to acknowledge, and nothing to record
        // either — the cluster is not being altered, and the post-deploy route refresh alone would
        // otherwise write a row per route per deploy. The record is for changes, not for calls.
        if (!diff.HasChanges && diff.Warning is null)
        {
            _logger.LogInformation("Cluster change is a no-op, skipping acknowledgment: {Change}", change.Describe());
            return;
        }

        ClusterChangeDecision decision = await _sink.RequestAsync(change, diff, ct);

        if (decision == ClusterChangeDecision.Acknowledged)
        {
            _logger.LogInformation("Cluster change ACKNOWLEDGED on {Cluster}: {Change}", change.ClusterLabel, change.Describe());
            await RecordAsync(change, ClusterChangeOutcome.Acknowledged, diff, ct);
            return;
        }

        _logger.LogInformation("Cluster change CANCELLED by operator on {Cluster}: {Change}", change.ClusterLabel, change.Describe());
        await RecordAsync(change, ClusterChangeOutcome.Cancelled, diff, ct);
        throw new OperationCanceledException($"Cluster change cancelled by operator: {change.Describe()}");
    }

    /// <summary>
    /// Records an outcome without letting the recording affect the change.
    ///
    /// <para>The interface asks implementations not to throw, but asking is not enforcing — and
    /// the consequence of trusting it is the worst one available: a change an operator has just
    /// approved failing because writing a row about it did. Cancellation is the one exception,
    /// because a cancelled token means the caller is already going away.</para>
    /// </summary>
    private async Task RecordAsync(
        PlannedClusterChange change, ClusterChangeOutcome outcome, ClusterChangeDiff? diff,
        CancellationToken ct)
    {
        try
        {
            await _recorder.RecordAsync(change, outcome, diff, ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not record cluster change outcome {Outcome} for {Change}",
                outcome, change.Describe());
        }
    }

    // ---- diff computation -------------------------------------------------

    private async Task<ClusterChangeDiff> ComputeDiffAsync(PlannedClusterChange change, CancellationToken ct)
    {
        return change.Verb switch
        {
            ChangeVerb.Apply or ChangeVerb.EnsureNamespace => await ComputeApplyDiffAsync(change.Manifest ?? "", change.Kubeconfig, ct),
            ChangeVerb.Delete => await ComputeDeleteDiffAsync(change, ct),
            ChangeVerb.Patch or ChangeVerb.Scale or ChangeVerb.Restart => ComputePatchDiff(change),
            // Helm installs/upgrades have no cheap declarative dry-run diff here; show the summary.
            _ => new ClusterChangeDiff { HasChanges = true, DiffText = change.Describe() },
        };
    }

    /// <summary>
    /// Runs `kubectl diff` (server-side) for an apply. Exit 0 = no change, exit 1 = diff on stdout,
    /// anything else = error → fall back to the server-rendered dry-run object so the operator still
    /// sees what would be sent.
    /// </summary>
    private async Task<ClusterChangeDiff> ComputeApplyDiffAsync(string manifest, string kubeconfig, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(manifest))
            return new ClusterChangeDiff { HasChanges = true, DiffText = "(empty manifest)" };

        string kcfg = await TempAsync(kubeconfig, ct);
        string mfst = await TempAsync(manifest, ct);
        try
        {
            (int code, string stdout, string stderr) = await RunKubectlAsync(
                $"diff -f {mfst} --kubeconfig={kcfg} --server-side --force-conflicts", ct);

            if (code == 0)
                return ClusterChangeDiff.NoChange();
            if (code == 1)
                return new ClusterChangeDiff { HasChanges = true, DiffText = stdout.Length > 0 ? stdout : "(changes detected)" };

            // Diff tooling unavailable or other error — render the server-side dry-run result instead.
            (int c2, string out2, string err2) = await RunKubectlAsync(
                $"apply -f {mfst} --kubeconfig={kcfg} --dry-run=server -o yaml", ct);
            if (c2 == 0)
            {
                return new ClusterChangeDiff
                {
                    HasChanges = true,
                    DiffText = out2,
                    Warning = "Live line-by-line diff was unavailable; showing the server-validated result of the apply.",
                };
            }

            return new ClusterChangeDiff
            {
                HasChanges = true,
                DiffText = manifest,
                Warning = $"Could not dry-run against the cluster ({(stderr + " " + err2).Trim()}). Review the manifest below.",
            };
        }
        finally
        {
            Delete(kcfg);
            Delete(mfst);
        }
    }

    private async Task<ClusterChangeDiff> ComputeDeleteDiffAsync(PlannedClusterChange change, CancellationToken ct)
    {
        // Composite delete (e.g. "delete -f" over a manifest set) — no single kind/name to look up.
        // Show the manifest/summary rather than trying (and failing) a per-object lookup.
        if (string.IsNullOrEmpty(change.Kind) || string.IsNullOrEmpty(change.Name))
        {
            string body = !string.IsNullOrWhiteSpace(change.Manifest)
                ? $"# The following resources will be DELETED:\n\n{change.Manifest}"
                : change.Describe();
            return new ClusterChangeDiff { HasChanges = true, DiffText = body };
        }

        string kcfg = await TempAsync(change.Kubeconfig, ct);
        try
        {
            string nsArg = string.IsNullOrEmpty(change.Namespace) ? "" : $"-n {change.Namespace} ";
            (int code, string stdout, _) = await RunKubectlAsync(
                $"get {change.Kind} {change.Name} {nsArg}--kubeconfig={kcfg} -o yaml", ct);

            if (code != 0)
                // Resource not present — delete would be a no-op (mirrors --ignore-not-found).
                return ClusterChangeDiff.NoChange();

            return new ClusterChangeDiff
            {
                HasChanges = true,
                DiffText = $"# The following resource will be DELETED:\n\n{stdout}",
            };
        }
        finally
        {
            Delete(kcfg);
        }
    }

    private static ClusterChangeDiff ComputePatchDiff(PlannedClusterChange change)
    {
        // Patches/scales/restarts are small and explicit; show the JSON that will be sent.
        string body = change.Patch ?? "(no patch body)";
        string header = $"# {change.Describe()}\n# patch ({(change.StrategicPatch ? "strategic merge" : "merge")}):\n\n";
        return new ClusterChangeDiff { HasChanges = true, DiffText = header + body };
    }

    // ---- kubectl plumbing (mirrors KubernetesClientFactory) ---------------

    private static async Task<(int code, string stdout, string stderr)> RunKubectlAsync(string arguments, CancellationToken ct)
    {
        using Process process = new()
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "kubectl",
                Arguments = arguments,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            },
        };
        process.StartInfo.EnvironmentVariables["HOME"] = "/tmp";
        process.Start();

        Task<string> outputTask = process.StandardOutput.ReadToEndAsync(ct);
        Task<string> errorTask = process.StandardError.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct);
        string stdout = await outputTask;
        string stderr = await errorTask;
        return (process.ExitCode, stdout, stderr);
    }

    private static async Task<string> TempAsync(string content, CancellationToken ct)
    {
        string path = Path.GetTempFileName();
        await File.WriteAllTextAsync(path, content, ct);
        return path;
    }

    private static void Delete(string path)
    {
        try { File.Delete(path); } catch { /* best effort */ }
    }

    private sealed class Unregister : IDisposable
    {
        private readonly ClusterChangeGate _gate;
        private readonly IClusterChangeAckSink _sink;
        public Unregister(ClusterChangeGate gate, IClusterChangeAckSink sink) { _gate = gate; _sink = sink; }
        public void Dispose()
        {
            if (ReferenceEquals(_gate._sink, _sink)) _gate._sink = null;
        }
    }

    /// <summary>
    /// Ends a declaration, so a scope reused for something else does not inherit it. Compares the
    /// reason before clearing, for the same reason <see cref="Unregister"/> compares the sink:
    /// disposing an older declaration must not revoke a newer one.
    /// </summary>
    private sealed class Undeclare : IDisposable
    {
        private readonly ClusterChangeGate _gate;
        private readonly string _reason;
        public Undeclare(ClusterChangeGate gate, string reason) { _gate = gate; _reason = reason; }
        public void Dispose()
        {
            if (ReferenceEquals(_gate._unattendedReason, _reason)) _gate._unattendedReason = null;
        }
    }
}
