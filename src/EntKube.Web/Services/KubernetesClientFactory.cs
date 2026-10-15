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

            await RunKubectlAsync(BuildDeleteArguments(kind, name, ns, kubeconfigPath), ct);
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
                // Closed immediately: a kubeconfig without usable credentials makes kubectl
                // prompt, and an inherited stdin never delivers the EOF that ends the prompt —
                // which in a background sweep is a hang with nobody there to see it.
                RedirectStandardInput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            },
        };
        process.StartInfo.EnvironmentVariables["HOME"] = "/tmp";
        process.Start();
        process.StandardInput.Close();

        Task<string> stdout = process.StandardOutput.ReadToEndAsync(ct);
        Task<string> stderr = process.StandardError.ReadToEndAsync(ct);

        try
        {
            await process.WaitForExitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            // Otherwise a cancelled shutdown leaves kubectl running against the cluster.
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch (Exception) { }
            throw;
        }

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
    /// Raises the acknowledgment for a change carried out by the caller rather than here. See
    /// <see cref="IKubernetesClientFactory.AcknowledgeAsync"/>.
    /// </summary>
    public Task AcknowledgeAsync(
        ClusterChanges.PlannedChange change, string kubeconfig, string clusterLabel,
        CancellationToken ct = default)
        => _gate.AcknowledgeAsync(change.WithCredential(kubeconfig, clusterLabel), ct);

    /// <summary>
    /// Runs a CronJob now, by copying its template into a new Job. See
    /// <see cref="IKubernetesClientFactory.CreateJobFromCronJobAsync"/>.
    /// </summary>
    public async Task<HelmExecutionResult> CreateJobFromCronJobAsync(
        string jobName, string cronJobName, string ns, string kubeconfig,
        string? summary = null, CancellationToken ct = default)
    {
        await _gate.AcknowledgeAsync(new PlannedClusterChange
        {
            // Apply, and no manifest: what is being created is a copy of a template this process
            // never sees, so the summary is the only honest preview — the same shape as an apply
            // from a URL.
            Verb = ChangeVerb.Apply,
            Kind = "Job",
            Name = jobName,
            Namespace = ns,
            Kubeconfig = kubeconfig,
            Summary = summary ?? $"Create Job/{jobName} from CronJob/{cronJobName} in {ns}",
        }, ct);

        string kubeconfigPath = Path.GetTempFileName();

        try
        {
            await SecretFile.WriteAsync(kubeconfigPath, kubeconfig, ct);

            (int code, string stdout, string stderr) = await RunKubectlRawAsync(
                BuildCreateJobFromCronJobArguments(jobName, cronJobName, ns, kubeconfigPath), ct);

            string output = string.IsNullOrWhiteSpace(stdout) ? stderr : stdout;
            return new HelmExecutionResult { Success = code == 0, Output = output.TrimEnd() };
        }
        finally
        {
            File.Delete(kubeconfigPath);
        }
    }

    /// <summary>The command line for running a CronJob now.</summary>
    public static string BuildCreateJobFromCronJobArguments(
        string jobName, string cronJobName, string ns, string kubeconfigPath)
        => $"create job {jobName} --from=cronjob/{cronJobName} -n {ns}"
           + $" --kubeconfig={kubeconfigPath}";

    /// <summary>
    /// Drains a node. See <see cref="IKubernetesClientFactory.DrainNodeAsync"/>.
    /// </summary>
    public async Task<HelmExecutionResult> ScaleDeploymentAsync(
        string name, string ns, int replicas, string kubeconfig,
        string? summary = null, CancellationToken ct = default)
    {
        await _gate.AcknowledgeAsync(new PlannedClusterChange
        {
            Verb = ChangeVerb.Scale,
            Kind = "Deployment",
            Name = name,
            Namespace = ns,
            Resource = $"deployment/{name}",
            Kubeconfig = kubeconfig,
            Summary = summary ?? $"Scale Deployment/{name} in {ns} to {replicas} replica(s)",
        }, ct);

        string kubeconfigPath = Path.GetTempFileName();

        try
        {
            await SecretFile.WriteAsync(kubeconfigPath, kubeconfig, ct);

            (int code, string stdout, string stderr) = await RunKubectlRawAsync(
                BuildScaleArguments(name, ns, replicas, kubeconfigPath), ct);

            string output = string.IsNullOrWhiteSpace(stdout) ? stderr : stdout;
            return new HelmExecutionResult { Success = code == 0, Output = output.TrimEnd() };
        }
        finally
        {
            File.Delete(kubeconfigPath);
        }
    }

    public async Task<HelmExecutionResult> DrainNodeAsync(
        string nodeName, string kubeconfig, int gracePeriodSeconds = 30,
        bool ignoreDaemonSets = true, bool deleteEmptyDirData = false,
        string? summary = null, CancellationToken ct = default)
    {
        await _gate.AcknowledgeAsync(new PlannedClusterChange
        {
            // Its own verb, not Delete: a drain leaves the Node object in place, so a delete
            // preview both describes the wrong act and — when the object cannot be read — reports
            // "no change" and lets the drain through unasked. See ChangeVerb.Drain.
            Verb = ChangeVerb.Drain,
            Kind = "Node",
            Name = nodeName,
            Kubeconfig = kubeconfig,
            Summary = summary ?? $"Drain Node/{nodeName} (cordon + evict all evictable pods)",
        }, ct);

        string kubeconfigPath = Path.GetTempFileName();

        try
        {
            await SecretFile.WriteAsync(kubeconfigPath, kubeconfig, ct);

            (int code, string stdout, string stderr) = await RunKubectlRawAsync(
                BuildDrainArguments(nodeName, gracePeriodSeconds, ignoreDaemonSets,
                    deleteEmptyDirData, kubeconfigPath),
                ct);

            string output = string.IsNullOrWhiteSpace(stdout) ? stderr : stdout;
            return new HelmExecutionResult { Success = code == 0, Output = output.TrimEnd() };
        }
        finally
        {
            File.Delete(kubeconfigPath);
        }
    }

    /// <summary>
    /// The command line for a node drain. <c>--timeout=5m</c> is not optional: without it a drain
    /// blocked by a PodDisruptionBudget waits forever, and this runs inside a request.
    /// </summary>
    /// <summary>
    /// <c>kubectl scale deployment/&lt;name&gt; --replicas=N</c>. Public and static so the command
    /// line is assertable without a cluster.
    /// </summary>
    public static string BuildScaleArguments(string name, string ns, int replicas, string kubeconfigPath)
        => $"scale deployment/{name} -n {ns} --replicas={replicas} --kubeconfig={kubeconfigPath}";

    public static string BuildDrainArguments(
        string nodeName, int gracePeriodSeconds, bool ignoreDaemonSets,
        bool deleteEmptyDirData, string kubeconfigPath)
    {
        List<string> args =
        [
            "drain", nodeName, $"--grace-period={gracePeriodSeconds}", "--timeout=5m",
        ];

        if (ignoreDaemonSets) args.Add("--ignore-daemonsets");
        if (deleteEmptyDirData) args.Add("--delete-emptydir-data");
        args.Add($"--kubeconfig={kubeconfigPath}");

        return string.Join(" ", args);
    }

    /// <summary>
    /// Rolls every Deployment in a namespace back to its previous recorded revision. See
    /// <see cref="IKubernetesClientFactory.UndoDeploymentsAsync"/>.
    ///
    /// <para>The list and the undos run under one acknowledgment, because they are one decision:
    /// asking per workload would put the operator in the middle of a half-rolled-back
    /// namespace.</para>
    /// </summary>
    public async Task<Clusters.WorkloadUndoResult> UndoDeploymentsAsync(
        string ns, string kubeconfig, string? summary = null, CancellationToken ct = default)
    {
        await _gate.AcknowledgeAsync(new PlannedClusterChange
        {
            Verb = ChangeVerb.Rollback,
            Resource = "every Deployment",
            Kubeconfig = kubeconfig,
            Namespace = ns,
            Summary = summary,
        }, ct);

        string kubeconfigPath = Path.GetTempFileName();

        try
        {
            await SecretFile.WriteAsync(kubeconfigPath, kubeconfig, ct);

            (int listCode, string listOut, string listErr) = await RunKubectlRawAsync(
                $"get deployments -n {ns} --kubeconfig={kubeconfigPath} -o name", ct);

            if (listCode != 0)
            {
                return new Clusters.WorkloadUndoResult(
                    false, $"Could not list workloads to roll back: {listErr.Trim()}");
            }

            string[] workloads = listOut
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

            if (workloads.Length == 0)
            {
                return new Clusters.WorkloadUndoResult(false, $"No Deployments found in namespace {ns}.");
            }

            List<string> results = [];
            bool allSucceeded = true;

            foreach (string workload in workloads)
            {
                (int code, string output, string error) = await RunKubectlRawAsync(
                    $"rollout undo {workload} -n {ns} --kubeconfig={kubeconfigPath}", ct);

                results.Add($"{workload}: {(code == 0 ? output.Trim() : error.Trim())}");

                if (code != 0)
                {
                    allSucceeded = false;
                }
            }

            return new Clusters.WorkloadUndoResult(allSucceeded, string.Join('\n', results));
        }
        finally
        {
            try { File.Delete(kubeconfigPath); } catch (IOException) { /* best effort */ }
        }
    }

    /// <summary>
    /// Replaces one Secret's contents. See <see cref="IKubernetesClientFactory.ReplaceSecretAsync"/>
    /// for why this is delete-then-apply behind a single acknowledgment.
    /// </summary>
    public async Task<string> ReplaceSecretAsync(
        string name, string ns, string manifest, string kubeconfig, string? summary = null,
        CancellationToken ct = default)
    {
        // Asked about the apply, not the delete: the diff is then the real before-and-after, and
        // the values in it are removed by SecretRedaction on the way into ClusterChangeDiff.
        await _gate.AcknowledgeAsync(new PlannedClusterChange
        {
            Verb = ChangeVerb.Apply,
            Manifest = manifest,
            Kubeconfig = kubeconfig,
            Namespace = ns,
            Summary = summary,
        }, ct);

        string kubeconfigPath = Path.GetTempFileName();
        string manifestPath = Path.GetTempFileName();

        try
        {
            await SecretFile.WriteAsync(kubeconfigPath, kubeconfig, ct);
            await SecretFile.WriteAsync(manifestPath, manifest, ct);

            // The values are in the manifest file, never in the argument list — which is the
            // property the --from-file form this replaces existed to get. A process argument list
            // is readable by anything that can see the process table.
            string output = "";

            foreach (string arguments in BuildReplaceSecretSequence(
                         name, ns, manifestPath, kubeconfigPath))
            {
                output = await RunKubectlAsync(arguments, ct);
            }

            return output;
        }
        finally
        {
            File.Delete(kubeconfigPath);
            File.Delete(manifestPath);
        }
    }

    /// <summary>
    /// The command line for clearing a Secret before it is rewritten.
    ///
    /// <para><c>--ignore-not-found</c> is what makes a first-time sync work: there is nothing to
    /// remove, and without it the whole replace would fail on a Secret that does not exist yet.
    /// Extracted and asserted because every test on these paths reaches the cluster through a
    /// mocked factory, so argv built inline is argv no test runs.</para>
    /// </summary>
    public static string BuildSecretDeleteArguments(string name, string ns, string kubeconfigPath)
        => $"delete secret {name} --namespace {ns} --ignore-not-found --kubeconfig={kubeconfigPath}";

    /// <summary>
    /// The two commands a replace runs, in order.
    ///
    /// <para>Returned as data so the <em>ordering and the presence of both steps</em> can be
    /// asserted. Every caller of <see cref="ReplaceSecretAsync"/> reaches the cluster through a
    /// mocked factory, so a mutant that drops the delete — turning the replace into a plain apply,
    /// and letting a revoked key survive in the cluster — is invisible to all of their tests. That
    /// is the fourth time a mock at the seam has hidden the seam, so this time the sequence is
    /// extracted while the operation is being written rather than after a mutant found it.</para>
    /// </summary>
    public static IReadOnlyList<string> BuildReplaceSecretSequence(
        string name, string ns, string manifestPath, string kubeconfigPath) =>
    [
        BuildSecretDeleteArguments(name, ns, kubeconfigPath),
        BuildApplyArguments(manifestPath, kubeconfigPath, ns),
    ];

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

            return await RunKubectlAsync(
                BuildGetJsonArguments(resource, ns, labelSelector, kubeconfigPath), ct);
        }
        finally
        {
            File.Delete(kubeconfigPath);
        }
    }

    /// <summary>One named resource as YAML. See <see cref="IKubernetesClientFactory.GetResourceYamlAsync"/>.</summary>
    public async Task<string> GetResourceYamlAsync(
        string resource, string name, string ns, string kubeconfig, CancellationToken ct = default)
    {
        string kubeconfigPath = Path.GetTempFileName();

        try
        {
            await SecretFile.WriteAsync(kubeconfigPath, kubeconfig, ct);

            return await RunKubectlAsync(
                BuildResourceYamlArguments(resource, name, ns, kubeconfigPath), ct);
        }
        finally
        {
            File.Delete(kubeconfigPath);
        }
    }

    /// <summary>
    /// The command line for a pod log tail, optionally for one container of several.
    /// </summary>
    public static string BuildPodLogsArguments(
        string target, string ns, int tailLines, string? container, string kubeconfigPath)
        => $"logs {target}{(string.IsNullOrEmpty(container) ? "" : $" -c {container}")}"
           + $" -n {ns} --kubeconfig={kubeconfigPath} --tail={tailLines}";

    /// <summary>
    /// Server-side applies a manifest the cluster fetches by URL. See
    /// <see cref="IKubernetesClientFactory.ApplyManifestFromUrlAsync"/> for why the URL is not
    /// downloaded here, and why the flags are what they are.
    ///
    /// <para>Returns the result rather than throwing, because the operator installs that use this
    /// report their output into a component's install log either way.</para>
    /// </summary>
    public async Task<HelmExecutionResult> ApplyManifestFromUrlAsync(
        string manifestUrl, string kubeconfig, string? summary = null, CancellationToken ct = default)
    {
        await _gate.AcknowledgeAsync(new PlannedClusterChange
        {
            Verb = ChangeVerb.Apply,
            Kubeconfig = kubeconfig,
            Summary = summary ?? $"Apply {manifestUrl}",
        }, ct);

        string kubeconfigPath = Path.GetTempFileName();

        try
        {
            await SecretFile.WriteAsync(kubeconfigPath, kubeconfig, ct);

            (int code, string stdout, string stderr) = await RunKubectlRawAsync(
                BuildApplyFromUrlArguments(manifestUrl, kubeconfigPath), ct);

            string output = string.IsNullOrWhiteSpace(stdout) ? stderr : stdout;
            return new HelmExecutionResult { Success = code == 0, Output = output.TrimEnd() };
        }
        finally
        {
            File.Delete(kubeconfigPath);
        }
    }

    /// <summary>The command line for a server-side apply of a manifest fetched by URL.</summary>
    public static string BuildApplyFromUrlArguments(string manifestUrl, string kubeconfigPath)
        => $"apply --server-side --force-conflicts -f {manifestUrl} --kubeconfig={kubeconfigPath}";

    /// <summary>
    /// The command line for listing a resource as JSON, optionally by label.
    ///
    /// <para>An empty namespace means a <b>cluster-scoped</b> resource and the flag is omitted —
    /// the same convention <see cref="ResourceExistsAsync"/> and
    /// <see cref="BuildDeleteArguments"/> use. Without it, a caller listing Namespaces or CRDs had
    /// to keep its own kubectl, which is how <c>CertificateDistributionService</c> ended up with
    /// one.</para>
    /// </summary>
    public static string BuildGetJsonArguments(
        string resource, string ns, string labelSelector, string kubeconfigPath)
        => $"get {resource}{(string.IsNullOrEmpty(ns) ? "" : $" -n {ns}")}"
           + $" --kubeconfig={kubeconfigPath}"
           + (string.IsNullOrEmpty(labelSelector) ? "" : $" -l {labelSelector}")
           + " -o json";

    /// <summary>
    /// The command line for deleting one named resource.
    ///
    /// <para><b>An empty namespace means a cluster-scoped resource</b>, so the flag is omitted
    /// rather than sent empty — <c>kubectl delete x y -n</c> is rejected outright.
    /// <see cref="ResourceExistsAsync"/> already read an empty <c>ns</c> that way and documents
    /// the convention; this did not, so a caller holding a cluster-scoped CR had to keep its own
    /// kubectl. <c>TrustBundleService</c> was doing exactly that for a trust Bundle.</para>
    /// </summary>
    public static string BuildDeleteArguments(string kind, string name, string ns, string kubeconfigPath)
        => $"delete {kind} {name}{(string.IsNullOrEmpty(ns) ? "" : $" -n {ns}")}"
           + $" --kubeconfig={kubeconfigPath} --ignore-not-found";

    /// <summary>
    /// One named resource as JSON. Returns the result rather than throwing: its caller refreshes
    /// a set of manifests and counts the ones the cluster no longer has, so "not found" is an
    /// answer rather than a failure.
    /// </summary>
    /// <summary>
    /// Asks the API server for a short-lived token bound to a ServiceAccount.
    ///
    /// <para>Requested with an explicit duration rather than mounted as a Secret, so expiry is
    /// enforced by the cluster: if every background job in EntKube stopped running, the credential
    /// would still stop working. The API server may return a shorter life than was asked for
    /// (<c>--service-account-max-token-expiration</c>), so <c>status.expirationTimestamp</c> is
    /// read back and treated as authoritative rather than the requested window — which is why this
    /// asks for JSON rather than the bare token <c>kubectl create token</c> prints by default.</para>
    ///
    /// <para><b>Not gated.</b> It creates no durable object — the token is bound to a
    /// ServiceAccount that an acknowledged apply has already created — and the acknowledgment for
    /// that apply named the grant it belongs to. A dialog here would ask about the consequence of a
    /// change the operator approved one line earlier, which is how an operator learns to click
    /// through dialogs.</para>
    /// </summary>
    public async Task<ServiceAccountToken> RequestServiceAccountTokenAsync(
        string serviceAccount, string ns, TimeSpan duration, IReadOnlyList<string> audiences,
        string kubeconfig, CancellationToken ct = default)
    {
        string kubeconfigPath = Path.GetTempFileName();

        try
        {
            await SecretFile.WriteAsync(kubeconfigPath, kubeconfig, ct);

            string json = await RunKubectlAsync(
                BuildCreateTokenArguments(serviceAccount, ns, duration, audiences, kubeconfigPath), ct);

            return ReadServiceAccountToken(json, serviceAccount, ns);
        }
        finally
        {
            File.Delete(kubeconfigPath);
        }
    }

    /// <summary>
    /// The command line for a bound-token request. Split out for the same reason as
    /// <see cref="BuildDeleteArguments"/>: every test on this path reaches the cluster through a
    /// mocked factory, so an argv built inline is argv no test ever runs.
    /// </summary>
    public static string BuildCreateTokenArguments(
        string serviceAccount, string ns, TimeSpan duration, IReadOnlyList<string> audiences,
        string kubeconfigPath)
        => $"create token {serviceAccount} -n {ns}"
           + $" --duration={(long)duration.TotalSeconds}s"
           + string.Concat(audiences.Select(a => $" --audience={a}"))
           + $" -o json --kubeconfig={kubeconfigPath}";

    /// <summary>
    /// Reads the token and its real expiry out of a TokenRequest. An absent token is an error
    /// rather than an empty credential, because a caller handed "" would mint a grant that cannot
    /// authenticate and look successful doing it.
    /// </summary>
    public static ServiceAccountToken ReadServiceAccountToken(string json, string serviceAccount, string ns)
    {
        using JsonDocument doc = JsonDocument.Parse(json);

        string? token = doc.RootElement.TryGetProperty("status", out JsonElement status)
            && status.TryGetProperty("token", out JsonElement t)
                ? t.GetString()
                : null;

        if (string.IsNullOrWhiteSpace(token))
        {
            throw new InvalidOperationException(
                $"The API server returned no token for ServiceAccount '{serviceAccount}' in '{ns}'.");
        }

        DateTime? expires =
            status.TryGetProperty("expirationTimestamp", out JsonElement e)
            && e.TryGetDateTimeOffset(out DateTimeOffset parsed)
                ? parsed.UtcDateTime
                : null;

        return new ServiceAccountToken(token, expires);
    }

    public async Task<HelmExecutionResult> GetResourceJsonAsync(
        string resource, string name, string ns, string kubeconfig, CancellationToken ct = default)
    {
        string kubeconfigPath = Path.GetTempFileName();

        try
        {
            await SecretFile.WriteAsync(kubeconfigPath, kubeconfig, ct);

            (int code, string stdout, string stderr) = await RunKubectlRawAsync(
                BuildResourceJsonArguments(resource, name, ns, kubeconfigPath), ct);

            return new HelmExecutionResult
            {
                Success = code == 0,
                Output = code == 0 ? stdout : (string.IsNullOrWhiteSpace(stderr) ? stdout : stderr).TrimEnd(),
            };
        }
        finally
        {
            File.Delete(kubeconfigPath);
        }
    }

    /// <summary>The command line for one named resource as JSON.</summary>
    public static string BuildResourceJsonArguments(
        string resource, string name, string ns, string kubeconfigPath)
        => $"get {resource} {name}{(string.IsNullOrEmpty(ns) ? "" : $" -n {ns}")}"
           + $" --kubeconfig={kubeconfigPath} -o json";

    /// <summary>
    /// The command line for one resource's YAML. Public and static so it can be asserted without
    /// a cluster, as <see cref="BuildDeleteSetArguments"/> already is — the last three seam
    /// additions each needed a mutant to find a missing flag before it was written this way.
    /// </summary>
    public static string BuildResourceYamlArguments(
        string resource, string name, string ns, string kubeconfigPath)
        => $"get {resource} {name} -n {ns} --kubeconfig={kubeconfigPath} -o yaml";

    public async Task<string> GetPodLogsAsync(
        string target, string ns, string kubeconfig, int tailLines = 200,
        CancellationToken ct = default, string? container = null)
    {
        string kubeconfigPath = Path.GetTempFileName();

        try
        {
            await File.WriteAllTextAsync(kubeconfigPath, kubeconfig, ct);

            return await RunKubectlAsync(
                BuildPodLogsArguments(target, ns, tailLines, container, kubeconfigPath), ct);
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
