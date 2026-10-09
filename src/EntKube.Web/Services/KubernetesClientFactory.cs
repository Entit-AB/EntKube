using System.Diagnostics;
using System.Text;
using System.Text.Json;
using EntKube.Web.Services.ClusterChanges;

namespace EntKube.Web.Services;

/// <summary>
/// Implements IKubernetesClientFactory using kubectl commands.
/// Writes manifests to temporary files and applies/deletes them via kubectl,
/// using the provided kubeconfig for authentication.
///
/// Every mutating primitive routes through <see cref="IClusterChangeGate"/> first, so an
/// interactive operator acknowledges a server-side dry-run diff before the write happens.
/// In non-interactive (background) scopes the gate passes through untouched.
/// </summary>
public class KubernetesClientFactory : IKubernetesClientFactory
{
    private readonly IClusterChangeGate _gate;

    public KubernetesClientFactory(IClusterChangeGate gate) => _gate = gate;

    /// <summary>
    /// Applies a YAML manifest by writing it to a temp file and running kubectl apply.
    /// The kubeconfig is written to a separate temp file for authentication.
    /// </summary>
    public Task<string> ApplyManifestAsync(string manifest, string kubeconfig, CancellationToken ct = default)
        => ApplyManifestInNamespaceAsync(manifest, "", kubeconfig, ct: ct);

    /// <summary>
    /// Applies a manifest set, defaulting documents that name no namespace to <paramref name="ns"/>.
    /// Some callers rely on that: KEDA's manifests deliberately omit a namespace so a structured
    /// ScaledObject and a user's own YAML both land in the app's.
    /// </summary>
    /// <param name="summary">
    /// What the operator is shown in the acknowledgment dialog. Worth passing: a caller that used
    /// to raise its own acknowledgment knows things this does not — which deployment, how many
    /// documents — and without it the dialog degrades to "Apply manifest".
    /// </param>
    public async Task<string> ApplyManifestInNamespaceAsync(
        string manifest, string ns, string kubeconfig, string? summary = null,
        CancellationToken ct = default)
    {
        await _gate.AcknowledgeAsync(new PlannedClusterChange
        {
            Verb = ChangeVerb.Apply,
            Manifest = manifest,
            Kubeconfig = kubeconfig,
            Namespace = string.IsNullOrEmpty(ns) ? null : ns,
            Summary = summary,
        }, ct);

        string kubeconfigPath = Path.GetTempFileName();
        string manifestPath = Path.GetTempFileName();

        try
        {
            await File.WriteAllTextAsync(kubeconfigPath, kubeconfig, ct);
            await File.WriteAllTextAsync(manifestPath, manifest, ct);

            return await RunKubectlAsync(
                BuildApplyArguments(manifestPath, kubeconfigPath, ns), ct);
        }
        finally
        {
            File.Delete(kubeconfigPath);
            File.Delete(manifestPath);
        }
    }

    /// <summary>
    /// The command line for applying a manifest set.
    ///
    /// <para>Split out for the same reason as <see cref="BuildDeleteSetArguments"/>: every test on
    /// these paths reaches the cluster through a mocked factory, so an argv built inline is argv no
    /// test ever runs. <c>--namespace</c> is a default here, not a filter — drop it and documents
    /// that name no namespace of their own land in <c>default</c> instead of the app's.</para>
    /// </summary>
    public static string BuildApplyArguments(string manifestPath, string kubeconfigPath, string ns = "")
        => $"apply -f {manifestPath} --kubeconfig={kubeconfigPath}"
         + (string.IsNullOrEmpty(ns) ? "" : $" --namespace {ns}");

    /// <summary>
    /// Deletes a specific Kubernetes resource by kind, name, and namespace.
    /// </summary>
    public async Task DeleteManifestAsync(
        string kind, string name, string ns, string kubeconfig, CancellationToken ct = default)
    {
        await _gate.AcknowledgeAsync(new PlannedClusterChange
        {
            Verb = ChangeVerb.Delete,
            Kind = kind,
            Name = name,
            Namespace = ns,
            Kubeconfig = kubeconfig,
        }, ct);

        string kubeconfigPath = Path.GetTempFileName();

        try
        {
            await File.WriteAllTextAsync(kubeconfigPath, kubeconfig, ct);

            await RunKubectlAsync(
                $"delete {kind} {name} -n {ns} --kubeconfig={kubeconfigPath} --ignore-not-found", ct);
        }
        finally
        {
            File.Delete(kubeconfigPath);
        }
    }

    /// <summary>
    /// Applies a JSON merge patch to a specific Kubernetes resource.
    /// Safer than ApplyManifestAsync for partial updates because only the specified fields change;
    /// all other fields (including spec.users lists on CRDs) are left untouched.
    /// </summary>
    public async Task PatchJsonAsync(
        string resource, string name, string ns, string jsonPatch, string kubeconfig, CancellationToken ct = default)
    {
        await _gate.AcknowledgeAsync(new PlannedClusterChange
        {
            Verb = ChangeVerb.Patch,
            Resource = resource,
            Name = name,
            Namespace = ns,
            Patch = jsonPatch,
            Kubeconfig = kubeconfig,
        }, ct);

        string kubeconfigPath = Path.GetTempFileName();
        string patchPath = Path.GetTempFileName();

        try
        {
            await File.WriteAllTextAsync(kubeconfigPath, kubeconfig, ct);
            await File.WriteAllTextAsync(patchPath, jsonPatch, ct);

            await RunKubectlAsync(
                $"patch {resource} {name} -n {ns} --type=merge --patch-file={patchPath} --kubeconfig={kubeconfigPath}", ct);
        }
        finally
        {
            File.Delete(kubeconfigPath);
            File.Delete(patchPath);
        }
    }

    /// <summary>
    /// Applies a strategic merge patch. Writes the patch JSON to a temp file
    /// so special characters (quotes, braces) are not mangled by shell argument parsing.
    /// </summary>
    public async Task PatchStrategicAsync(
        string resource, string name, string ns, string jsonPatch, string kubeconfig, CancellationToken ct = default)
    {
        await _gate.AcknowledgeAsync(new PlannedClusterChange
        {
            Verb = ChangeVerb.Patch,
            Resource = resource,
            Name = name,
            Namespace = ns,
            Patch = jsonPatch,
            StrategicPatch = true,
            Kubeconfig = kubeconfig,
        }, ct);

        string kubeconfigPath = Path.GetTempFileName();
        string patchPath = Path.GetTempFileName();

        try
        {
            await File.WriteAllTextAsync(kubeconfigPath, kubeconfig, ct);
            await File.WriteAllTextAsync(patchPath, jsonPatch, ct);

            await RunKubectlAsync(
                $"patch {resource} {name} -n {ns} --type=strategic --patch-file={patchPath} --kubeconfig={kubeconfigPath}", ct);
        }
        finally
        {
            File.Delete(kubeconfigPath);
            File.Delete(patchPath);
        }
    }

    /// <summary>
    /// Creates a namespace if it doesn't already exist.
    /// </summary>
    public async Task EnsureNamespaceAsync(string ns, string kubeconfig, CancellationToken ct = default)
    {
        string manifest = $"apiVersion: v1\nkind: Namespace\nmetadata:\n  name: {ns}\n";
        await ApplyManifestAsync(manifest, kubeconfig, ct);
    }

    /// <summary>
    /// Gets JSON output from kubectl for a given resource type in a namespace.
    /// Supports optional label selectors for filtering.
    /// </summary>
    /// <summary>
    /// Asks a cluster how it differs from a manifest.
    ///
    /// <para>Exit 0 means the cluster matches, exit 1 means it differs and the diff is on stdout,
    /// and anything else means the question went unanswered — so this does not use
    /// <see cref="RunKubectlAsync"/>, which treats any non-zero exit as a failure and would turn
    /// "it differs" into an exception.</para>
    ///
    /// <para>No timeout here on purpose: <c>kubectl diff</c> against an unresponsive API server
    /// can hang, and how long that is worth waiting for belongs to the caller, which passes a
    /// linked token.</para>
    /// </summary>
    public async Task<Clusters.ManifestDiff> DiffManifestAsync(
        string manifest, string ns, string kubeconfig, CancellationToken ct = default)
    {
        string kubeconfigPath = Path.GetTempFileName();
        string manifestPath = Path.GetTempFileName();

        try
        {
            await SecretFile.WriteAsync(kubeconfigPath, kubeconfig, ct);
            await SecretFile.WriteAsync(manifestPath, manifest, ct);

            string nsArg = string.IsNullOrEmpty(ns) ? "" : $" --namespace {ns}";

            (int code, string stdout, string stderr) = await RunKubectlRawAsync(
                $"diff -f {manifestPath} --kubeconfig={kubeconfigPath}{nsArg} --server-side --force-conflicts",
                ct);

            return InterpretDiffExit(code, stdout, stderr);
        }
        finally
        {
            File.Delete(kubeconfigPath);
            File.Delete(manifestPath);
        }
    }

    /// <summary>
    /// What <c>kubectl diff</c>'s exit code means: 0 the cluster matches, 1 it differs and the
    /// diff is on stdout, anything else the question went unanswered.
    ///
    /// <para>Split out so it can be asserted on directly. Callers reach this through a mocked
    /// factory, so a mutant that turned "differs" into "could not tell" passed every test — the
    /// third time a seam operation's own logic has been invisible that way (after the helm argv
    /// and the manifest-set delete flags). The mapping is the whole operation: get it wrong in the
    /// Unknown direction and a drifted deployment reports as healthy.</para>
    /// </summary>
    public static Clusters.ManifestDiff InterpretDiffExit(int exitCode, string stdout, string stderr)
        => exitCode switch
        {
            0 => new Clusters.ManifestDiff(Clusters.DiffOutcome.Matches),
            1 => new Clusters.ManifestDiff(Clusters.DiffOutcome.Differs, stdout),
            _ => new Clusters.ManifestDiff(Clusters.DiffOutcome.Unknown,
                Error: string.IsNullOrWhiteSpace(stderr) ? "kubectl diff failed." : stderr.Trim()),
        };

    /// <summary>The cluster's server version, or null when it cannot be read.</summary>
    public async Task<string?> GetServerVersionAsync(string kubeconfig, CancellationToken ct = default)
    {
        string kubeconfigPath = Path.GetTempFileName();

        try
        {
            await SecretFile.WriteAsync(kubeconfigPath, kubeconfig, ct);

            (int code, string stdout, _) = await RunKubectlRawAsync(
                $"version -o json --kubeconfig={kubeconfigPath}", ct);

            if (code != 0 || string.IsNullOrWhiteSpace(stdout)) return null;

            using JsonDocument doc = JsonDocument.Parse(stdout);

            return doc.RootElement.TryGetProperty("serverVersion", out JsonElement server)
                   && server.TryGetProperty("gitVersion", out JsonElement git)
                ? git.GetString()
                : null;
        }
        catch (JsonException)
        {
            // kubectl answered with something that is not the version document; "unknown" is the
            // honest result and the caller already treats null as that.
            return null;
        }
        finally
        {
            File.Delete(kubeconfigPath);
        }
    }

    /// <summary>
    /// Runs kubectl and hands back the exit code rather than raising on a non-zero one, for the
    /// commands where a non-zero exit is an answer instead of a failure.
    /// </summary>
    private static async Task<(int Code, string Stdout, string Stderr)> RunKubectlRawAsync(
        string arguments, CancellationToken ct)
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

        Task<string> stdout = process.StandardOutput.ReadToEndAsync(ct);
        Task<string> stderr = process.StandardError.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct);

        return (process.ExitCode, await stdout, await stderr);
    }

    /// <summary>
    /// Deletes every resource in a manifest set.
    ///
    /// <para><c>--ignore-not-found</c>, so removing something already gone is a success rather
    /// than an error — these run on teardown paths where a half-removed state is the normal case.
    /// The acknowledgment carries the manifest, which is what the gate needs: a composite delete
    /// has no single kind and name to look up, so its preview is the set itself.</para>
    /// </summary>
    public async Task<string> DeleteManifestSetAsync(
        string manifest, string kubeconfig, string ns = "", string? summary = null,
        CancellationToken ct = default)
    {
        await _gate.AcknowledgeAsync(new PlannedClusterChange
        {
            Verb = ChangeVerb.Delete,
            Manifest = manifest,
            Kubeconfig = kubeconfig,
            Namespace = string.IsNullOrEmpty(ns) ? null : ns,
            Summary = summary,
        }, ct);

        string kubeconfigPath = Path.GetTempFileName();
        string manifestPath = Path.GetTempFileName();

        try
        {
            await SecretFile.WriteAsync(kubeconfigPath, kubeconfig, ct);
            await SecretFile.WriteAsync(manifestPath, manifest, ct);

            return await RunKubectlAsync(
                BuildDeleteSetArguments(manifestPath, kubeconfigPath, ns), ct);
        }
        finally
        {
            File.Delete(kubeconfigPath);
            File.Delete(manifestPath);
        }
    }

    /// <summary>
    /// The command line for deleting a manifest set.
    ///
    /// <para>Split out so <c>--ignore-not-found</c> can be asserted on. It was inline, and a
    /// mutant that dropped it passed every test — the tests reach this through a mocked factory,
    /// so the argv never ran. The flag is load-bearing: these run on teardown paths where some of
    /// the set is already gone, and without it kubectl fails the whole call.</para>
    /// </summary>
    public static string BuildDeleteSetArguments(string manifestPath, string kubeconfigPath, string ns = "")
        => $"delete -f {manifestPath} --kubeconfig={kubeconfigPath}"
         + (string.IsNullOrEmpty(ns) ? "" : $" --namespace {ns}")
         + " --ignore-not-found";

    /// <summary>
    /// Runs one helm invocation against a cluster.
    ///
    /// <para>Three things happen here that the callers used to each do for themselves: the
    /// acknowledgment is raised, the credential is written to a 0600 file and passed as
    /// <c>--kubeconfig</c>, and the file is removed afterwards. The arguments are otherwise passed
    /// through untouched, because chart resolution is the caller's business — see
    /// <see cref="HelmInvocation"/>.</para>
    ///
    /// <para>Unlike the kubectl operations here, this returns a result rather than throwing. Every
    /// helm caller in EntKube inspects <see cref="HelmExecutionResult.Output"/> — a failed release
    /// is something an operator reads, not an exception to unwind — and a non-zero helm exit is an
    /// ordinary outcome of installing a chart.</para>
    /// </summary>
    public async Task<HelmExecutionResult> RunHelmAsync(
        Clusters.HelmInvocation invocation, string kubeconfig, CancellationToken ct = default)
    {
        await _gate.AcknowledgeAsync(new PlannedClusterChange
        {
            Verb = ChangeVerb.Helm,
            Name = invocation.ReleaseName,
            Namespace = invocation.Namespace,
            Kubeconfig = kubeconfig,
            Summary = invocation.Summary ?? $"helm {invocation.Verb} {invocation.ReleaseName} in {invocation.Namespace}",
        }, ct);

        string kubeconfigPath = Path.GetTempFileName();

        try
        {
            await SecretFile.WriteAsync(kubeconfigPath, kubeconfig, ct);

            return await RunHelmProcessAsync(BuildHelmArguments(invocation, kubeconfigPath), ct);
        }
        finally
        {
            File.Delete(kubeconfigPath);
        }
    }

    /// <summary>
    /// The command line for one invocation: the verb, then the caller's arguments, then the
    /// credential this seam supplies.
    ///
    /// <para>Split out so it can be asserted on. It was inlined, and a mutant that stopped
    /// appending <c>--kubeconfig</c> passed every test — because the tests exercised the callers
    /// through a mocked factory and so never ran this. The one line that makes the seam a seam
    /// deserves to be observable.</para>
    /// </summary>
    public static string BuildHelmArguments(Clusters.HelmInvocation invocation, string kubeconfigPath)
        => string.Join(' ',
            new[] { invocation.Verb }
                .Concat(invocation.Arguments)
                .Concat(["--kubeconfig", kubeconfigPath]));

    /// <summary>
    /// Runs helm and captures its combined output. Separate from <see cref="RunKubectlAsync"/>
    /// because helm's non-zero exit is a result to report rather than a failure to raise.
    /// </summary>
    private static async Task<HelmExecutionResult> RunHelmProcessAsync(string arguments, CancellationToken ct)
    {
        using Process process = new()
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "helm",
                Arguments = arguments,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            },
        };
        // As for kubectl: the container user's real HOME is not writable, and helm wants a cache.
        process.StartInfo.EnvironmentVariables["HOME"] = "/tmp";

        try
        {
            process.Start();

            Task<string> stdout = process.StandardOutput.ReadToEndAsync(ct);
            Task<string> stderr = process.StandardError.ReadToEndAsync(ct);
            await process.WaitForExitAsync(ct);

            string output = (await stdout).Trim();
            string error = (await stderr).Trim();

            if (error.Length > 0)
            {
                output = output.Length > 0 ? $"{output}\n{error}" : error;
            }

            return new HelmExecutionResult
            {
                Success = process.ExitCode == 0,
                ExitCode = process.ExitCode,
                Output = output,
            };
        }
        catch (Exception ex)
        {
            return new HelmExecutionResult
            {
                Success = false,
                Output = HelmExecutionResult.DescribeLaunchFailure("helm", ex),
            };
        }
    }

    /// <summary>
    /// Whether one named resource exists. <c>--ignore-not-found</c> makes "absent" an empty answer
    /// rather than a failure, so only an unreachable cluster raises.
    /// </summary>
    public async Task<bool> ResourceExistsAsync(
        string resource, string name, string ns, string kubeconfig, CancellationToken ct = default)
    {
        string kubeconfigPath = Path.GetTempFileName();

        try
        {
            await File.WriteAllTextAsync(kubeconfigPath, kubeconfig, ct);

            string nsArg = string.IsNullOrEmpty(ns) ? "" : $" -n {ns}";
            string output = await RunKubectlAsync(
                $"get {resource} {name}{nsArg} --kubeconfig={kubeconfigPath} --ignore-not-found -o name", ct);

            return !string.IsNullOrWhiteSpace(output);
        }
        finally
        {
            File.Delete(kubeconfigPath);
        }
    }

    public async Task<string> GetJsonAsync(
        string resource, string ns, string kubeconfig, string labelSelector = "", CancellationToken ct = default)
    {
        string kubeconfigPath = Path.GetTempFileName();

        try
        {
            await File.WriteAllTextAsync(kubeconfigPath, kubeconfig, ct);

            string selector = string.IsNullOrEmpty(labelSelector) ? "" : $" -l {labelSelector}";
            return await RunKubectlAsync(
                $"get {resource} -n {ns} --kubeconfig={kubeconfigPath}{selector} -o json", ct);
        }
        finally
        {
            File.Delete(kubeconfigPath);
        }
    }

    public async Task<string> GetPodLogsAsync(
        string target, string ns, string kubeconfig, int tailLines = 200, CancellationToken ct = default)
    {
        string kubeconfigPath = Path.GetTempFileName();

        try
        {
            await File.WriteAllTextAsync(kubeconfigPath, kubeconfig, ct);

            return await RunKubectlAsync(
                $"logs {target} -n {ns} --kubeconfig={kubeconfigPath} --tail={tailLines}", ct);
        }
        finally
        {
            File.Delete(kubeconfigPath);
        }
    }

    public async Task<string> GetJsonAllNamespacesAsync(
        string resource, string kubeconfig, string labelSelector = "", CancellationToken ct = default)
    {
        string kubeconfigPath = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(kubeconfigPath, kubeconfig, ct);
            string selector = string.IsNullOrEmpty(labelSelector) ? "" : $" -l {labelSelector}";
            return await RunKubectlAsync(
                $"get {resource} -A --kubeconfig={kubeconfigPath}{selector} -o json", ct);
        }
        finally
        {
            File.Delete(kubeconfigPath);
        }
    }

    /// <summary>
    /// Executes SQL on the CNPG primary pod via kubectl exec + psql.
    /// The primary pod is identified by the CNPG naming convention: {cluster}-1.
    /// </summary>
    public async Task ExecuteSqlAsync(
        string clusterName, string ns, string sql, string kubeconfig, CancellationToken ct = default)
    {
        string kubeconfigPath = Path.GetTempFileName();

        try
        {
            await File.WriteAllTextAsync(kubeconfigPath, kubeconfig, ct);

            string primaryPod = await ResolveCnpgPrimaryPodAsync(clusterName, ns, kubeconfigPath, ct);

            // Pipe SQL via stdin to avoid shell argument splitting issues.
            // The -i flag tells kubectl exec to pass stdin to the container.

            await RunKubectlWithStdinAsync(
                $"exec -i {primaryPod} -n {ns} --kubeconfig={kubeconfigPath} -- psql -U postgres",
                sql, ct);
        }
        finally
        {
            File.Delete(kubeconfigPath);
        }
    }

    public Task ExecuteMongoAsync(
        string clusterName, string ns, string script, string kubeconfig,
        string? username = null, string? password = null, CancellationToken ct = default) =>
        ExecuteMongoWithOutputAsync(clusterName, ns, script, kubeconfig, username, password, ct);

    public async Task<string> ExecuteMongoWithOutputAsync(
        string clusterName, string ns, string script, string kubeconfig,
        string? username = null, string? password = null, CancellationToken ct = default)
    {
        string kubeconfigPath = Path.GetTempFileName();

        try
        {
            await File.WriteAllTextAsync(kubeconfigPath, kubeconfig, ct);

            // MongoDB Community Operator names StatefulSet pods {cluster}-0, {cluster}-1, ...
            string primaryPod = $"{clusterName}-0";

            string mongoshArgs = !string.IsNullOrEmpty(username) && !string.IsNullOrEmpty(password)
                ? $"--username {username} --password {password} --authenticationDatabase admin --quiet"
                : "--quiet";

            return await RunKubectlWithStdinAsync(
                $"exec -i {primaryPod} -n {ns} --kubeconfig={kubeconfigPath} -- mongosh {mongoshArgs}",
                script, ct);
        }
        finally
        {
            File.Delete(kubeconfigPath);
        }
    }

    public async Task<string?> GetSecretValueAsync(
        string secretName, string key, string ns, string kubeconfig, CancellationToken ct = default)
    {
        string json = await GetJsonAsync($"secret/{secretName}", ns, kubeconfig, ct: ct);

        try
        {
            using JsonDocument doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("data", out JsonElement data)
                && data.TryGetProperty(key, out JsonElement valEl))
            {
                string? b64 = valEl.GetString();
                if (string.IsNullOrEmpty(b64)) return null;
                return Encoding.UTF8.GetString(Convert.FromBase64String(b64));
            }
            return null;
        }
        catch (Exception ex) when (ex is not InvalidOperationException)
        {
            return null;
        }
    }

    public Task ExecuteSqlOnPodAsync(
        string podName, string ns, string sql, string kubeconfig,
        string username = "postgres", string? password = null, CancellationToken ct = default) =>
        ExecuteSqlOnPodCoreAsync(podName, ns, sql, kubeconfig, username, password, ct);

    public async Task<string> ExecuteSqlOnPodWithOutputAsync(
        string podName, string ns, string sql, string kubeconfig,
        string username = "postgres", string? password = null, CancellationToken ct = default) =>
        await ExecuteSqlOnPodCoreAsync(podName, ns, sql, kubeconfig, username, password, ct);

    public async Task<string> RunCommandOnPodAsync(
        string podName, string ns, IReadOnlyList<string> command, string kubeconfig,
        IReadOnlyDictionary<string, string>? envVars = null, CancellationToken ct = default,
        int timeoutSeconds = 0, bool verbose = false)
    {
        string kubeconfigPath = Path.GetTempFileName();

        try
        {
            await File.WriteAllTextAsync(kubeconfigPath, kubeconfig, ct);

            ProcessStartInfo startInfo = new()
            {
                FileName = "kubectl",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            // The container user's real HOME (/home/appuser) is not writable, so point
            // kubectl's cache at /tmp. Auth uses embedded certs, so HOME doesn't affect it.
            startInfo.EnvironmentVariables["HOME"] = "/tmp";

            startInfo.ArgumentList.Add("exec");
            if (verbose) startInfo.ArgumentList.Add("--v=6");
            startInfo.ArgumentList.Add(podName);
            startInfo.ArgumentList.Add("-n");
            startInfo.ArgumentList.Add(ns);
            startInfo.ArgumentList.Add($"--kubeconfig={kubeconfigPath}");
            startInfo.ArgumentList.Add("--");

            if (envVars is { Count: > 0 })
            {
                startInfo.ArgumentList.Add("env");
                foreach (KeyValuePair<string, string> kv in envVars)
                    startInfo.ArgumentList.Add($"{kv.Key}={kv.Value}");
            }

            foreach (string arg in command)
                startInfo.ArgumentList.Add(arg);

            using Process process = new() { StartInfo = startInfo };
            process.Start();

            Task<string> outputTask = process.StandardOutput.ReadToEndAsync(ct);
            Task<string> errorTask = process.StandardError.ReadToEndAsync(ct);

            // Bound the wait when a timeout is requested so a wedged `kubectl exec`
            // (e.g. an unreachable API server or stalled streaming upgrade) fails with
            // diagnostics instead of hanging the caller forever.
            using CancellationTokenSource? timeoutCts = timeoutSeconds > 0
                ? CancellationTokenSource.CreateLinkedTokenSource(ct)
                : null;
            timeoutCts?.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));

            try
            {
                await process.WaitForExitAsync(timeoutCts?.Token ?? ct);
            }
            catch (OperationCanceledException) when (
                timeoutCts is { IsCancellationRequested: true } && !ct.IsCancellationRequested)
            {
                try { process.Kill(entireProcessTree: true); } catch { /* already gone */ }

                string diag = "";
                try { diag = await errorTask.WaitAsync(TimeSpan.FromSeconds(5), ct); }
                catch { /* best-effort */ }

                throw new InvalidOperationException(
                    $"kubectl exec timed out after {timeoutSeconds}s (pod {podName}, ns {ns}). " +
                    "The exec never completed — the source cluster API/kubelet streaming path is " +
                    $"likely unreachable from this container. kubectl -v=6 diagnostics:\n{diag}");
            }

            string output = await outputTask;
            string error = await errorTask;

            if (process.ExitCode != 0)
                throw new InvalidOperationException($"kubectl exec failed (exit {process.ExitCode}): {error}");

            return output;
        }
        finally
        {
            File.Delete(kubeconfigPath);
        }
    }

    public async Task ExecuteSqlInCnpgDatabaseAsync(
        string clusterName, string ns, string database, string sql, string kubeconfig, CancellationToken ct = default)
    {
        await ExecuteSqlInCnpgDatabaseWithOutputAsync(clusterName, ns, database, sql, kubeconfig, ct);
    }

    public async Task<string> ExecuteSqlInCnpgDatabaseWithOutputAsync(
        string clusterName, string ns, string database, string sql, string kubeconfig, CancellationToken ct = default)
    {
        string kubeconfigPath = Path.GetTempFileName();

        try
        {
            await File.WriteAllTextAsync(kubeconfigPath, kubeconfig, ct);

            string primaryPod = await ResolveCnpgPrimaryPodAsync(clusterName, ns, kubeconfigPath, ct);

            return await RunKubectlWithStdinAsync(
                $"exec -i {primaryPod} -n {ns} --kubeconfig={kubeconfigPath} -- psql -U postgres -d \"{database}\" -t -A",
                sql, ct);
        }
        finally
        {
            File.Delete(kubeconfigPath);
        }
    }

    public async Task<string> RunCommandOnPodWithStdinAsync(
        string podName, string ns, IReadOnlyList<string> command, string stdin, string kubeconfig,
        CancellationToken ct = default, string? container = null)
    {
        string kubeconfigPath = Path.GetTempFileName();

        try
        {
            await File.WriteAllTextAsync(kubeconfigPath, kubeconfig, ct);

            ProcessStartInfo startInfo = new()
            {
                FileName = "kubectl",
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            startInfo.ArgumentList.Add("exec");
            startInfo.ArgumentList.Add("-i");
            startInfo.ArgumentList.Add(podName);
            startInfo.ArgumentList.Add("-n");
            startInfo.ArgumentList.Add(ns);
            if (!string.IsNullOrWhiteSpace(container))
            {
                startInfo.ArgumentList.Add("-c");
                startInfo.ArgumentList.Add(container);
            }
            startInfo.ArgumentList.Add($"--kubeconfig={kubeconfigPath}");
            startInfo.ArgumentList.Add("--");

            foreach (string arg in command)
                startInfo.ArgumentList.Add(arg);

            using Process process = new() { StartInfo = startInfo };
            process.Start();

            // Drain stdout/stderr before writing stdin to avoid a pipe-buffer deadlock
            // on large payloads (see RunKubectlWithStdinAsync for the full explanation).
            Task<string> outputTask = process.StandardOutput.ReadToEndAsync(ct);
            Task<string> errorTask = process.StandardError.ReadToEndAsync(ct);

            await process.StandardInput.WriteAsync(stdin.AsMemory(), ct);
            await process.StandardInput.FlushAsync(ct);
            process.StandardInput.Close();

            await process.WaitForExitAsync(ct);

            string output = await outputTask;
            string error = await errorTask;

            if (process.ExitCode != 0)
                throw new InvalidOperationException($"kubectl exec failed (exit {process.ExitCode}): {error}");

            return output;
        }
        finally
        {
            File.Delete(kubeconfigPath);
        }
    }

    private async Task<string> ExecuteSqlOnPodCoreAsync(
        string podName, string ns, string sql, string kubeconfig,
        string username, string? password, CancellationToken ct)
    {
        string kubeconfigPath = Path.GetTempFileName();

        try
        {
            await File.WriteAllTextAsync(kubeconfigPath, kubeconfig, ct);

            // Use ArgumentList (not Arguments string) so passwords with special
            // characters are never mangled by shell quoting or .NET arg splitting.
            ProcessStartInfo startInfo = new()
            {
                FileName = "kubectl",
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            startInfo.ArgumentList.Add("exec");
            startInfo.ArgumentList.Add("-i");
            startInfo.ArgumentList.Add(podName);
            startInfo.ArgumentList.Add("-n");
            startInfo.ArgumentList.Add(ns);
            startInfo.ArgumentList.Add($"--kubeconfig={kubeconfigPath}");
            startInfo.ArgumentList.Add("--");

            if (!string.IsNullOrEmpty(password))
            {
                startInfo.ArgumentList.Add("env");
                startInfo.ArgumentList.Add($"PGPASSWORD={password}");
            }

            startInfo.ArgumentList.Add("psql");
            startInfo.ArgumentList.Add("-U");
            startInfo.ArgumentList.Add(username);
            // Force TCP so pg_hba host rules apply (not peer/local socket rules).
            startInfo.ArgumentList.Add("-h");
            startInfo.ArgumentList.Add("127.0.0.1");
            startInfo.ArgumentList.Add("-t");

            using Process process = new() { StartInfo = startInfo };
            process.Start();

            // Drain stdout/stderr before writing stdin to avoid a pipe-buffer deadlock
            // on large payloads (see RunKubectlWithStdinAsync for the full explanation).
            Task<string> outputTask = process.StandardOutput.ReadToEndAsync(ct);
            Task<string> errorTask = process.StandardError.ReadToEndAsync(ct);

            await process.StandardInput.WriteAsync(sql.AsMemory(), ct);
            await process.StandardInput.FlushAsync(ct);
            process.StandardInput.Close();

            await process.WaitForExitAsync(ct);

            string output = await outputTask;
            string error = await errorTask;

            if (process.ExitCode != 0)
            {
                throw new InvalidOperationException($"kubectl failed (exit {process.ExitCode}): {error}");
            }

            return output;
        }
        finally
        {
            File.Delete(kubeconfigPath);
        }
    }

    /// <summary>
    /// Resolves the current CNPG primary pod by label. CNPG names pods with an
    /// incrementing serial ({cluster}-1, {cluster}-2, …) and the primary moves on every
    /// failover, switchover, or rolling restart — so {cluster}-1 is only the primary
    /// right after first creation and will not exist after the serial advances.
    /// Falls back to the historical {cluster}-1 assumption if the label lookup yields nothing.
    /// </summary>
    private static async Task<string> ResolveCnpgPrimaryPodAsync(
        string clusterName, string ns, string kubeconfigPath, CancellationToken ct)
    {
        // cnpg.io/instanceRole is the current label (CNPG ≥1.22); role is the legacy one.
        foreach (string roleSelector in new[] { "cnpg.io/instanceRole=primary", "role=primary" })
        {
            try
            {
                string name = (await RunKubectlAsync(
                    $"get pods -n {ns} -l cnpg.io/cluster={clusterName},{roleSelector} " +
                    $"--kubeconfig={kubeconfigPath} -o jsonpath={{.items[0].metadata.name}}", ct)).Trim();
                if (!string.IsNullOrEmpty(name)) return name;
            }
            catch (InvalidOperationException)
            {
                // Selector unsupported or lookup failed — try the next label scheme.
            }
        }

        return $"{clusterName}-1";
    }

    private static async Task<string> RunKubectlAsync(string arguments, CancellationToken ct)
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
                CreateNoWindow = true
            }
        };
        // The container user's real HOME (/home/appuser) is not writable, so point
        // kubectl's cache at /tmp. Auth uses embedded certs, so HOME doesn't affect it.
        process.StartInfo.EnvironmentVariables["HOME"] = "/tmp";

        process.Start();

        StringBuilder output = new();
        StringBuilder error = new();

        Task<string> outputTask = process.StandardOutput.ReadToEndAsync(ct);
        Task<string> errorTask = process.StandardError.ReadToEndAsync(ct);

        await process.WaitForExitAsync(ct);

        output.Append(await outputTask);
        error.Append(await errorTask);

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"kubectl failed (exit {process.ExitCode}): {error}");
        }

        return output.ToString();
    }

    /// <summary>
    /// Runs kubectl with input piped via stdin. Used for executing SQL/scripts
    /// where passing the content as command-line arguments would break due to
    /// shell escaping and argument splitting in Process.
    /// </summary>
    private static async Task<string> RunKubectlWithStdinAsync(
        string arguments, string stdinContent, CancellationToken ct)
    {
        using Process process = new()
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "kubectl",
                Arguments = arguments,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            }
        };
        // The container user's real HOME (/home/appuser) is not writable, so point
        // kubectl's cache at /tmp. Auth uses embedded certs, so HOME doesn't affect it.
        process.StartInfo.EnvironmentVariables["HOME"] = "/tmp";

        process.Start();

        // Start draining stdout/stderr BEFORE writing stdin. Otherwise a large
        // stdin payload (e.g. a multi-MB pg_dump) deadlocks: the child fills its
        // stdout pipe, blocks, stops reading stdin, and our WriteAsync blocks too.
        Task<string> outputTask = process.StandardOutput.ReadToEndAsync(ct);
        Task<string> errorTask = process.StandardError.ReadToEndAsync(ct);

        // Write the script/SQL to stdin and close it so the process knows input is done.
        await process.StandardInput.WriteAsync(stdinContent.AsMemory(), ct);
        await process.StandardInput.FlushAsync(ct);
        process.StandardInput.Close();

        await process.WaitForExitAsync(ct);

        string output = await outputTask;
        string error = await errorTask;

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"kubectl failed (exit {process.ExitCode}): {error}");
        }

        return output;
    }
}
