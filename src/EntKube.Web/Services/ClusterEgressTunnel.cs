using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.RegularExpressions;

namespace EntKube.Web.Services;

/// <summary>
/// Keeps a <c>kubectl port-forward</c> alive per cluster so EntKube can open TCP
/// connections to the in-cluster egress relay (<see cref="ClusterEgressRelay"/>).
///
/// port-forward is what makes the whole approach deployable: it tunnels raw TCP
/// over the cluster's API server, so the only connectivity EntKube needs is the
/// one it already has to manage the cluster at all. Nothing is exposed inbound
/// anywhere, and the relay Service stays ClusterIP-only.
///
/// Singleton, because a forward is a long-lived process that should be shared by
/// every request rather than started per operation. Forwards are started lazily
/// and restarted transparently if the process dies.
/// </summary>
public sealed partial class ClusterEgressTunnel(ILogger<ClusterEgressTunnel> logger) : IAsyncDisposable
{
    private readonly ConcurrentDictionary<Guid, Tunnel> tunnels = new();
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> locks = new();
    private bool disposed;

    /// <summary>How long to wait for kubectl to report the local port before giving up.</summary>
    private static readonly TimeSpan StartupTimeout = TimeSpan.FromSeconds(30);

    [GeneratedRegex(@"Forwarding from 127\.0\.0\.1:(\d+)")]
    private static partial Regex ForwardingLine();

    /// <summary>
    /// Returns the loopback port that currently forwards to the cluster's relay,
    /// starting the forward if there is not already a healthy one.
    /// </summary>
    public async Task<int> GetLocalPortAsync(Guid clusterId, string kubeconfig, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);

        if (tunnels.TryGetValue(clusterId, out Tunnel? existing) && existing.IsAlive)
        {
            return existing.LocalPort;
        }

        SemaphoreSlim gate = locks.GetOrAdd(clusterId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);

        try
        {
            // Re-check: another caller may have started it while we queued.
            if (tunnels.TryGetValue(clusterId, out existing) && existing.IsAlive)
            {
                return existing.LocalPort;
            }

            if (existing is not null)
            {
                logger.LogInformation(
                    "Egress tunnel for cluster {ClusterId} is no longer running; restarting", clusterId);
                existing.Dispose();
                tunnels.TryRemove(clusterId, out _);
            }

            Tunnel started = await StartAsync(kubeconfig, ct);
            tunnels[clusterId] = started;

            logger.LogInformation(
                "Egress tunnel for cluster {ClusterId} listening on 127.0.0.1:{Port}", clusterId, started.LocalPort);

            return started.LocalPort;
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>Stops the forward for one cluster, if any. Used when a connection stops routing through it.</summary>
    public void Close(Guid clusterId)
    {
        if (tunnels.TryRemove(clusterId, out Tunnel? tunnel))
        {
            tunnel.Dispose();
        }
    }

    private async Task<Tunnel> StartAsync(string kubeconfig, CancellationToken ct)
    {
        string kubeconfigPath = Path.Combine(Path.GetTempPath(), $"entkube-egress-{Guid.NewGuid():N}.kubeconfig");
        await File.WriteAllTextAsync(kubeconfigPath, kubeconfig, ct);

        // Restrict the kubeconfig to this user — it is a live cluster credential
        // sitting on disk for as long as the forward runs.
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(kubeconfigPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }

        // ":port" asks kubectl to pick a free local port, which avoids collisions
        // between clusters and with anything else on the host.
        ProcessStartInfo psi = new()
        {
            FileName = "kubectl",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        psi.ArgumentList.Add("port-forward");
        psi.ArgumentList.Add($"svc/{ClusterEgressRelay.Name}");
        psi.ArgumentList.Add($":{ClusterEgressRelay.Port}");
        psi.ArgumentList.Add("--namespace");
        psi.ArgumentList.Add(ClusterEgressRelay.Namespace);
        psi.ArgumentList.Add("--address");
        psi.ArgumentList.Add("127.0.0.1");
        psi.ArgumentList.Add("--kubeconfig");
        psi.ArgumentList.Add(kubeconfigPath);

        Process process = new() { StartInfo = psi, EnableRaisingEvents = true };

        TaskCompletionSource<int> portReady = new(TaskCreationOptions.RunContinuationsAsynchronously);
        List<string> stderr = [];

        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is null) return;

            Match match = ForwardingLine().Match(e.Data);

            if (match.Success && int.TryParse(match.Groups[1].Value, out int port))
            {
                portReady.TrySetResult(port);
            }
        };

        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is null) return;

            lock (stderr) { stderr.Add(e.Data); }
            logger.LogDebug("kubectl port-forward: {Line}", e.Data);
        };

        process.Exited += (_, _) =>
            portReady.TrySetException(new InvalidOperationException(
                BuildFailureMessage(stderr)));

        if (!process.Start())
        {
            throw new InvalidOperationException("Failed to start 'kubectl port-forward'.");
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        try
        {
            using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(StartupTimeout);

            int localPort = await portReady.Task.WaitAsync(timeout.Token);
            return new Tunnel(process, localPort, kubeconfigPath);
        }
        catch (Exception ex)
        {
            Cleanup(process, kubeconfigPath);

            if (ex is OperationCanceledException && !ct.IsCancellationRequested)
            {
                throw new InvalidOperationException(
                    $"'kubectl port-forward' did not become ready within {StartupTimeout.TotalSeconds:0}s. "
                    + BuildFailureMessage(stderr), ex);
            }

            throw;
        }
    }

    /// <summary>
    /// Turns kubectl's stderr into something actionable — the common failures here
    /// are a missing relay or an unreachable API server, and both are fixable.
    /// </summary>
    private static string BuildFailureMessage(List<string> stderr)
    {
        string detail;
        lock (stderr) { detail = string.Join(" ", stderr).Trim(); }

        if (detail.Contains("not found", StringComparison.OrdinalIgnoreCase))
        {
            return $"The egress relay is not deployed on this cluster (kubectl: {detail}). "
                 + "Deploy it from the OpenStack connection before routing through this cluster.";
        }

        return detail.Length > 0
            ? $"kubectl port-forward failed: {detail}"
            : "kubectl port-forward exited without reporting a local port.";
    }

    private static void Cleanup(Process process, string kubeconfigPath)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { /* already gone */ }
        process.Dispose();
        try { File.Delete(kubeconfigPath); } catch { /* best-effort */ }
    }

    public async ValueTask DisposeAsync()
    {
        disposed = true;

        foreach (Tunnel tunnel in tunnels.Values)
        {
            tunnel.Dispose();
        }

        tunnels.Clear();

        foreach (SemaphoreSlim gate in locks.Values)
        {
            gate.Dispose();
        }

        locks.Clear();
        await ValueTask.CompletedTask;
    }

    /// <summary>One running port-forward: the process, the port it published, and the credential file it holds open.</summary>
    private sealed class Tunnel(Process process, int localPort, string kubeconfigPath) : IDisposable
    {
        public int LocalPort { get; } = localPort;

        public bool IsAlive
        {
            get
            {
                try { return !process.HasExited; }
                catch { return false; }
            }
        }

        public void Dispose() => Cleanup(process, kubeconfigPath);
    }
}
