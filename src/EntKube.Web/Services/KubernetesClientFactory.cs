using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Net.Http.Json;
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
    private readonly KubernetesProxyClientPool _proxies;

    public KubernetesClientFactory(IClusterChangeGate gate, KubernetesProxyClientPool proxies)
    {
        _gate = gate;
        _proxies = proxies;
    }

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
    /// <summary>
    /// Creates the namespace if it is missing, and does nothing at all if it is already there.
    ///
    /// <para>⚠️ <b>The existence check is about the acknowledgment, not about kubectl.</b>
    /// Applying a Namespace that exists is a no-op — but it is a <i>change</i> as far as the gate
    /// is concerned, so it raised a prompt. Almost every caller does this before writing into a
    /// namespace that has existed for months, and a secret sync walks one group per
    /// (secret, namespace) pair, so the operator was asked to acknowledge a series of namespace
    /// applies that could not do anything, which buries the one change they should be reading.
    /// </para>
    /// </summary>
    public async Task EnsureNamespaceAsync(string ns, string kubeconfig, CancellationToken ct = default)
    {
        if (await ResourceExistsAsync("namespace", ns, "", kubeconfig, ct))
        {
            return;
        }

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

    // ════════════════════════════════════════════════════════════════
    //  A rollback that names the revision it wants
    // ════════════════════════════════════════════════════════════════

    /// <summary>The annotation Kubernetes stamps the current revision number onto.</summary>
    private const string RevisionAnnotation = "deployment.kubernetes.io/revision";

    /// <summary>
    /// The annotation listing the revision numbers a ReplicaSet has <em>already served</em>.
    ///
    /// <para>Kubernetes writes it when it reuses an existing ReplicaSet for a new revision, which
    /// is exactly what an undo does: rolling back to revision 4 does not restore the number 4, it
    /// re-points the Deployment at revision 4's ReplicaSet and renumbers that ReplicaSet to the
    /// new maximum. ⚠️ Revision 4 therefore <b>disappears</b> from the history, and the bad
    /// release keeps its own number — which is why a second relative undo lands back on it.</para>
    /// </summary>
    private const string RevisionHistoryAnnotation = "deployment.kubernetes.io/revision-history";

    /// <summary>
    /// What a namespace rollback will do to one Deployment, decided before anything runs.
    /// </summary>
    /// <param name="Workload">The <c>deployment.apps/&lt;name&gt;</c> identifier handed to kubectl.</param>
    /// <param name="ToRevision">
    /// The revision to pin the undo to, or null when no undo will be run for this workload.
    /// ⚠️ Never 0 and never negative: kubectl reads <c>--to-revision=0</c> as "the previous one",
    /// which is the relative move this plan exists to replace, so a 0 here would silently restore
    /// the defect rather than fail.
    /// </param>
    /// <param name="AlreadyRolledBack">
    /// True when the workload is <em>already</em> sitting on a revision somebody brought forward
    /// from the past. Then there is nothing to undo, and undoing anyway would move it on to the
    /// release we are trying to get rid of. ⚠️ This is not a failure — the state we wanted is the
    /// state it is in — and reporting it as one would page a human about a rollback that worked.
    /// </param>
    /// <param name="Explanation">The operator-facing line recorded against this workload.</param>
    public sealed record WorkloadUndoStep(
        string Workload, int? ToRevision, bool AlreadyRolledBack, string Explanation);

    /// <summary>One ReplicaSet's place in a Deployment's history.</summary>
    private sealed record RevisionRecord(int Revision, DateTimeOffset CreatedAt, bool HasServedBefore);

    /// <summary>
    /// ⭐ Works out, for every Deployment in a namespace, <b>which revision number</b> its undo
    /// should name — or why it must not be undone at all.
    ///
    /// <para><b>What this used to be.</b> The rollback ran
    /// <c>kubectl rollout undo deployment.apps/x -n ns</c> with no <c>--to-revision</c>. That is a
    /// <em>relative</em> move — "whatever is one step back from wherever you are right now" —
    /// evaluated at the instant kubectl runs. It is correct only if nothing has moved the workload
    /// since the watch opened, and nothing guarantees that. A second undo from any source (an
    /// operator typing <c>kubectl rollout undo</c> while the watcher is judging; a retry after a
    /// partial failure; a second instance, before the judgement claim existed) steps the workload
    /// <em>forward</em> again — from the good revision back on to the bad release — and reports
    /// success while doing it.</para>
    ///
    /// <para>⚠️ <b>Pinning alone does not fix that, and it is worth being exact about why.</b>
    /// An undo renumbers: after rolling back to revision 4, revision 4's ReplicaSet is renumbered
    /// to the new maximum and the number 4 is gone, so a second attempt that recomputed
    /// "the one below current" would compute the bad release's number and pin <em>that</em>.
    /// Pinning makes the command absolute; it is the refusal below that makes a second undo
    /// harmless.</para>
    ///
    /// <para>⭐ <b>The tell that a workload has already been rolled back</b> is that its current
    /// ReplicaSet is not the most recently <em>created</em> one. Creation timestamps never change,
    /// while revision numbers are rewritten on every undo, so "something newer than what you are
    /// running exists" is the one statement about this that survives renumbering. Kubernetes'
    /// own <c>revision-history</c> annotation says the same thing from the other side and is
    /// checked too, because creation timestamps are serialised to whole seconds and two
    /// ReplicaSets born in the same second would tie.</para>
    ///
    /// <para>⚠️ <b>What this still cannot know.</b> <c>DeploymentRollout</c> records no revision
    /// number — see <c>RolloutService.RollbackAsync</c> — so this is "the revision below the one
    /// running now", not "the revision this watch started from". They are the same thing when
    /// nobody else has touched the namespace, and the refusal above is what catches the case where
    /// somebody has. A release applied outside EntKube between the watch opening and the verdict
    /// is still invisible here.</para>
    ///
    /// <para>Pure and public so the whole decision is assertable without a cluster — the same
    /// reason <see cref="BuildScaleArguments"/> and <see cref="BuildDrainArguments"/> are.</para>
    /// </summary>
    public static List<WorkloadUndoStep> PlanNamespaceUndo(string deploymentsJson, string replicaSetsJson)
    {
        Dictionary<string, List<RevisionRecord>> byOwner = [];

        using (JsonDocument replicaSets = JsonDocument.Parse(replicaSetsJson))
        {
            foreach (JsonElement rs in ListItems(replicaSets))
            {
                if (!rs.TryGetProperty("metadata", out JsonElement meta))
                {
                    continue;
                }

                // A ReplicaSet with no revision annotation is not part of a Deployment's recorded
                // history, and must not be allowed to look like revision 0.
                if (!int.TryParse(Annotation(meta, RevisionAnnotation), out int revision))
                {
                    continue;
                }

                DateTimeOffset created = DateTimeOffset.MinValue;
                if (meta.TryGetProperty("creationTimestamp", out JsonElement ts)
                    && ts.ValueKind == JsonValueKind.String
                    && DateTimeOffset.TryParse(
                        ts.GetString(), CultureInfo.InvariantCulture,
                        DateTimeStyles.RoundtripKind, out DateTimeOffset parsed))
                {
                    created = parsed;
                }

                bool servedBefore = !string.IsNullOrWhiteSpace(Annotation(meta, RevisionHistoryAnnotation));

                // ⚠️ Owner uid, not a name prefix. Two Deployments in one namespace whose names
                // share a prefix ("api" and "api-worker") would cross-contaminate each other's
                // history, and the pinned revision would then come off the wrong workload.
                if (!meta.TryGetProperty("ownerReferences", out JsonElement owners)
                    || owners.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }

                foreach (JsonElement owner in owners.EnumerateArray())
                {
                    if (!owner.TryGetProperty("kind", out JsonElement kind)
                        || kind.ValueKind != JsonValueKind.String
                        || kind.GetString() != "Deployment"
                        || !owner.TryGetProperty("uid", out JsonElement ownerUidElement)
                        || ownerUidElement.ValueKind != JsonValueKind.String)
                    {
                        continue;
                    }

                    string? ownerUid = ownerUidElement.GetString();
                    if (string.IsNullOrEmpty(ownerUid))
                    {
                        continue;
                    }

                    if (!byOwner.TryGetValue(ownerUid, out List<RevisionRecord>? history))
                    {
                        history = [];
                        byOwner[ownerUid] = history;
                    }

                    history.Add(new RevisionRecord(revision, created, servedBefore));
                }
            }
        }

        List<WorkloadUndoStep> steps = [];

        using JsonDocument deployments = JsonDocument.Parse(deploymentsJson);

        foreach (JsonElement deployment in ListItems(deployments))
        {
            if (!deployment.TryGetProperty("metadata", out JsonElement meta))
            {
                continue;
            }

            string name = meta.TryGetProperty("name", out JsonElement nameElement)
                ? nameElement.GetString() ?? string.Empty
                : string.Empty;

            if (name.Length == 0)
            {
                continue;
            }

            string workload = $"deployment.apps/{name}";
            string uid = meta.TryGetProperty("uid", out JsonElement uidElement)
                ? uidElement.GetString() ?? string.Empty
                : string.Empty;

            if (!int.TryParse(Annotation(meta, RevisionAnnotation), out int current))
            {
                steps.Add(new WorkloadUndoStep(workload, null, false,
                    "Kubernetes has recorded no current revision for it, so there is no revision "
                    + "to name and a relative undo would be a guess."));
                continue;
            }

            List<RevisionRecord> revisions =
                uid.Length > 0 && byOwner.TryGetValue(uid, out List<RevisionRecord>? owned)
                    ? owned
                    : [];

            RevisionRecord? live = revisions.FirstOrDefault(r => r.Revision == current);

            if (live is null)
            {
                steps.Add(new WorkloadUndoStep(workload, null, false,
                    $"The ReplicaSet for its current revision {current} is gone, so what it is "
                    + "running now cannot be established."));
                continue;
            }

            if (revisions.Any(r => r.CreatedAt > live.CreatedAt) || live.HasServedBefore)
            {
                steps.Add(new WorkloadUndoStep(workload, null, true,
                    $"Already rolled back: revision {current} is an older ReplicaSet brought "
                    + "forward, so undoing again would move it on to the release being rolled "
                    + "back. Left alone."));
                continue;
            }

            int? target = revisions
                .Where(r => r.Revision < current)
                .Select(r => (int?)r.Revision)
                .Max();

            if (target is not int toRevision)
            {
                steps.Add(new WorkloadUndoStep(workload, null, false,
                    $"No revision earlier than {current} is still recorded — a first release, or "
                    + "the revision history limit pruned it — so there is nothing to roll back to."));
                continue;
            }

            steps.Add(new WorkloadUndoStep(workload, toRevision, false,
                $"Roll back from revision {current} to revision {toRevision}."));
        }

        return steps;
    }

    /// <summary>
    /// <c>kubectl rollout undo &lt;workload&gt; -n &lt;ns&gt; --to-revision=N</c>. Public and
    /// static so the command line is assertable without a cluster.
    ///
    /// <para>⚠️ Throws rather than emitting <c>--to-revision=0</c>. Zero is kubectl's own spelling
    /// of "the previous revision", so a 0 slipping through would reinstate the relative undo while
    /// looking, in the command line and in the logs, exactly like the pinned one.</para>
    /// </summary>
    public static string BuildUndoArguments(string workload, string ns, int toRevision, string kubeconfigPath)
    {
        if (toRevision < 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(toRevision), toRevision,
                "A pinned rollback must name a real revision: kubectl reads --to-revision=0 as "
                + "\"the previous one\", which is the relative move this replaces.");
        }

        return $"rollout undo {workload} -n {ns} --to-revision={toRevision} --kubeconfig={kubeconfigPath}";
    }

    /// <summary>
    /// How a pinned undo's failure is reported. The revision number is in the text on purpose:
    /// the expected failure here is kubectl's "unable to find specified revision N in history",
    /// which happens when the history limit prunes the ReplicaSet between the plan and the undo,
    /// and an operator reading the verdict needs to know which revision went missing.
    ///
    /// <para>⭐ A pinned undo that fails loudly is the point. The relative one it replaced could
    /// not fail this way — it always found <em>something</em> to move to, including the release
    /// being rolled back.</para>
    /// </summary>
    public static string DescribeUndoFailure(string workload, int toRevision, string error) =>
        $"{workload}: could not roll back to revision {toRevision} — {error.Trim()}";

    /// <summary>Enumerates a kubectl list's <c>items</c>, tolerating a response that has none.</summary>
    private static IEnumerable<JsonElement> ListItems(JsonDocument doc) =>
        doc.RootElement.TryGetProperty("items", out JsonElement items)
        && items.ValueKind == JsonValueKind.Array
            ? items.EnumerateArray()
            : [];

    /// <summary>One annotation off a metadata block, or null if the block carries none.</summary>
    private static string? Annotation(JsonElement metadata, string name) =>
        metadata.TryGetProperty("annotations", out JsonElement annotations)
        && annotations.ValueKind == JsonValueKind.Object
        && annotations.TryGetProperty(name, out JsonElement value)
        && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    /// <summary>
    /// Rolls every Deployment in a namespace back to the revision it was on before its current
    /// one, naming that revision explicitly. See
    /// <see cref="IKubernetesClientFactory.UndoDeploymentsAsync"/>.
    ///
    /// <para>The list, the history read and the undos run under one acknowledgment, because they
    /// are one decision: asking per workload would put the operator in the middle of a
    /// half-rolled-back namespace.</para>
    ///
    /// <para>⭐ Every undo names <c>--to-revision</c>, and a workload that has already been rolled
    /// back is left alone rather than undone a second time. See <see cref="PlanNamespaceUndo"/>
    /// for what the relative undo did instead, and why a second one rolled <em>forward</em> on to
    /// the bad release.</para>
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

            // -o json rather than -o name: the names alone cannot say which revision anything is
            // on, and which revision is the whole question.
            (int listCode, string deploymentsJson, string listErr) = await RunKubectlRawAsync(
                $"get deployments -n {ns} --kubeconfig={kubeconfigPath} -o json", ct);

            if (listCode != 0)
            {
                return new Clusters.WorkloadUndoResult(
                    false, $"Could not list workloads to roll back: {listErr.Trim()}");
            }

            (int historyCode, string replicaSetsJson, string historyErr) = await RunKubectlRawAsync(
                $"get replicasets -n {ns} --kubeconfig={kubeconfigPath} -o json", ct);

            // ⚠️ Not a degrade back to the relative undo. Without the ReplicaSets there is no
            // revision history, so the only undo still available is the blind one — and the blind
            // one is the defect, not the fallback. Refusing leaves the bad release live and says
            // so, which a human can act on; a blind undo might instead quietly restore it.
            if (historyCode != 0)
            {
                return new Clusters.WorkloadUndoResult(
                    false,
                    "Could not read the revision history to roll back to, and will not roll back "
                    + $"blindly: {historyErr.Trim()}");
            }

            List<WorkloadUndoStep> plan;

            try
            {
                plan = PlanNamespaceUndo(deploymentsJson, replicaSetsJson);
            }
            catch (JsonException ex)
            {
                return new Clusters.WorkloadUndoResult(
                    false, $"Could not read the namespace's revision history: {ex.Message}");
            }

            if (plan.Count == 0)
            {
                return new Clusters.WorkloadUndoResult(false, $"No Deployments found in namespace {ns}.");
            }

            List<string> results = [];
            bool allSucceeded = true;

            foreach (WorkloadUndoStep step in plan)
            {
                if (step.ToRevision is not int toRevision)
                {
                    results.Add($"{step.Workload}: {step.Explanation}");

                    // ⚠️ Only one of the two refusals is a failure. "Already rolled back" means
                    // the workload is where we wanted to put it, so failing the whole rollback
                    // over it would page a human about a rollback that worked. "Cannot pin a
                    // revision" means the bad release is still live, which is a failure.
                    if (!step.AlreadyRolledBack)
                    {
                        allSucceeded = false;
                    }

                    continue;
                }

                (int code, string output, string error) = await RunKubectlRawAsync(
                    BuildUndoArguments(step.Workload, ns, toRevision, kubeconfigPath), ct);

                if (code != 0)
                {
                    results.Add(DescribeUndoFailure(step.Workload, toRevision, error));
                    allSucceeded = false;
                    continue;
                }

                results.Add($"{step.Workload}: rolled back to revision {toRevision}. {output.Trim()}");
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
    /// <summary>
    /// Builds the merge patch for <see cref="MergeSecretKeysAsync"/>. Separated so the JSON is
    /// assertable without a cluster: a null value must appear as a JSON null (which is how a
    /// merge patch deletes a key), and a value must be base64 — a patch carrying plain text
    /// would be written into the Secret verbatim and read back as rubbish.
    /// </summary>
    public static string BuildSecretKeyPatch(
        IReadOnlyDictionary<string, string?> keys,
        IReadOnlyDictionary<string, string>? annotations)
    {
        Dictionary<string, object?> data = [];

        foreach ((string key, string? value) in keys)
        {
            data[key] = value is null
                ? null
                : Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(value));
        }

        // ⚠️ The managed-by labels go in every patch, not only in the manifest that creates the
        // Secret. The deployment importer uses exactly that label to avoid re-adopting EntKube's
        // own Secrets back into the vault, and a Secret written before this change — or created
        // by another writer — would otherwise never acquire it. Setting it on each write is also
        // how the old whole-manifest apply kept it true.
        Dictionary<string, object?> metadata = new()
        {
            ["labels"] = new Dictionary<string, object?>
            {
                ["app.kubernetes.io/managed-by"] = "entkube",
                ["entkube.io/managed"] = "true",
            },
        };

        if (annotations is { Count: > 0 })
        {
            metadata["annotations"] = annotations.ToDictionary(a => a.Key, a => (object?)a.Value);
        }

        Dictionary<string, object?> patch = new()
        {
            ["metadata"] = metadata,
            ["data"] = data,
        };

        return System.Text.Json.JsonSerializer.Serialize(patch);
    }

    public async Task<string> MergeSecretKeysAsync(
        string name, string ns, IReadOnlyDictionary<string, string?> keys,
        IReadOnlyDictionary<string, string>? annotations, string kubeconfig,
        string? summary = null, CancellationToken ct = default)
    {
        string patch = BuildSecretKeyPatch(keys, annotations);

        await _gate.AcknowledgeAsync(new PlannedClusterChange
        {
            Verb = ChangeVerb.Patch,
            Resource = "secret",
            Name = name,
            Namespace = ns,
            Patch = patch,
            StrategicPatch = true,
            Kubeconfig = kubeconfig,
            Summary = summary,
        }, ct);

        string kubeconfigPath = Path.GetTempFileName();
        string patchPath = Path.GetTempFileName();

        try
        {
            // SecretFile rather than File.WriteAllTextAsync. ⚠️ Not because the path above is
            // exposed — Path.GetTempFileName goes through mkstemp and yields 0600, which
            // SecretTempFileTests checks on this platform rather than assuming — but because the
            // guarantee should not rest on remembering which of two temp-path idioms this line
            // used. The patch carries the secret values; SecretFile states the requirement at
            // the point of the write instead of leaving it to be re-derived.
            await SecretFile.WriteAsync(kubeconfigPath, kubeconfig, ct);
            await SecretFile.WriteAsync(patchPath, patch, ct);

            // kubectl patch fails on a Secret that does not exist yet, so create an empty one
            // first. Created rather than applied with the keys in it: an apply would write a
            // last-applied annotation naming them, and that annotation is what makes a later
            // writer's apply delete them.
            if (!await ResourceExistsAsync("secret", name, ns, kubeconfig, ct))
            {
                string empty = $"""
                    apiVersion: v1
                    kind: Secret
                    metadata:
                      name: {name}
                      namespace: {ns}
                      labels:
                        app.kubernetes.io/managed-by: entkube
                        entkube.io/managed: "true"
                    type: Opaque
                    """;

                string emptyPath = Path.GetTempFileName();
                try
                {
                    await SecretFile.WriteAsync(emptyPath, empty, ct);
                    await RunKubectlAsync(
                        $"create -f {emptyPath} --kubeconfig={kubeconfigPath}", ct);
                }
                finally
                {
                    File.Delete(emptyPath);
                }
            }

            return await RunKubectlAsync(
                $"patch secret {name} -n {ns} --type=merge --patch-file={patchPath}"
                + $" --kubeconfig={kubeconfigPath}", ct);
        }
        finally
        {
            File.Delete(kubeconfigPath);
            File.Delete(patchPath);
        }
    }

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
            // SecretFile for the same reason as above: a kubeconfig is a credential, and the
            // owner-only mode is asserted here rather than inherited from GetTempFileName's
            // mkstemp behaviour.
            await SecretFile.WriteAsync(kubeconfigPath, kubeconfig, ct);

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
        string manifestUrl, string kubeconfig, string? summary = null, CancellationToken ct = default,
        IReadOnlyList<ManifestPatch>? patches = null)
    {
        patches ??= [];
        string described = summary ?? $"Apply {manifestUrl}";
        if (patches.Count > 0)
            described += "; patched: " + string.Join("; ", patches.Select(p => $"{p.Kind} {p.Name} — {p.Reason}"));

        await _gate.AcknowledgeAsync(new PlannedClusterChange
        {
            Verb = ChangeVerb.Apply,
            Kubeconfig = kubeconfig,
            Summary = described,
        }, ct);

        string kubeconfigPath = Path.GetTempFileName();
        DirectoryInfo? kustomizeDir = null;

        try
        {
            await SecretFile.WriteAsync(kubeconfigPath, kubeconfig, ct);

            string arguments;
            if (patches.Count == 0)
            {
                arguments = BuildApplyFromUrlArguments(manifestUrl, kubeconfigPath);
            }
            else
            {
                // Holds no secret: the URL and the catalog's patches. kubectl still fetches the URL.
                kustomizeDir = Directory.CreateTempSubdirectory("entkube-kustomize-");
                await File.WriteAllTextAsync(
                    Path.Combine(kustomizeDir.FullName, "kustomization.yaml"),
                    BuildKustomization(manifestUrl, patches), ct);
                arguments = BuildApplyKustomizationArguments(kustomizeDir.FullName, kubeconfigPath);
            }

            (int code, string stdout, string stderr) = await RunKubectlRawAsync(arguments, ct);

            string output = string.IsNullOrWhiteSpace(stdout) ? stderr : stdout;
            return new HelmExecutionResult { Success = code == 0, Output = output.TrimEnd() };
        }
        finally
        {
            File.Delete(kubeconfigPath);
            kustomizeDir?.Delete(recursive: true);
        }
    }

    /// <summary>The command line for a server-side apply of a manifest fetched by URL.</summary>
    public static string BuildApplyFromUrlArguments(string manifestUrl, string kubeconfigPath)
        => $"apply --server-side --force-conflicts -f {manifestUrl} --kubeconfig={kubeconfigPath}";

    /// <summary>The same apply, of a kustomization directory that fetches the URL and patches it.</summary>
    public static string BuildApplyKustomizationArguments(string directory, string kubeconfigPath)
        => $"apply --server-side --force-conflicts -k {directory} --kubeconfig={kubeconfigPath}";

    /// <summary>
    /// A kustomization taking <paramref name="resource"/> (a URL, or a file beside it) and applying each
    /// patch to its one target. Written as JSON, which YAML reads as it is, so no value needs escaping.
    /// </summary>
    public static string BuildKustomization(string resource, IReadOnlyList<ManifestPatch> patches) =>
        JsonSerializer.Serialize(new
        {
            apiVersion = "kustomize.config.k8s.io/v1beta1",
            kind = "Kustomization",
            resources = new[] { resource },
            patches = patches.Select(p => new
            {
                target = new { group = p.Group, version = p.Version, kind = p.Kind, name = p.Name },
                patch = p.JsonPatch
            })
        }, new JsonSerializerOptions { WriteIndented = true });

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
    /// Reads the API server address, CA roots and TLS-verification flag out of a kubeconfig.
    ///
    /// <para>What it deliberately does <b>not</b> return is the client certificate or token the
    /// kubeconfig authenticates with. That omission is the entire value: the caller gets enough to
    /// reach and verify the API server and nothing with which to act as EntKube on it.</para>
    /// </summary>
    public EntKube.Web.Services.Clusters.ClusterConnection ReadConnection(string kubeconfig)
    {
        using MemoryStream stream = new(Encoding.UTF8.GetBytes(kubeconfig));

        k8s.KubernetesClientConfiguration config =
            k8s.KubernetesClientConfiguration.BuildConfigFromConfigFile(stream);

        return new EntKube.Web.Services.Clusters.ClusterConnection(
            new Uri(config.Host),
            config.SslCaCerts is { Count: > 0 } ? config.SslCaCerts : null,
            config.SkipTlsVerify);
    }

    /// <summary>Calls an action subresource. See <see cref="IKubernetesClientFactory.InvokeSubresourceAsync"/>.</summary>
    public async Task InvokeSubresourceAsync(
        string groupVersion, string plural, string name, string ns, string subresource,
        string kubeconfig, string? summary = null, CancellationToken ct = default)
    {
        string path = BuildSubresourcePath(groupVersion, plural, name, ns, subresource);

        await _gate.AcknowledgeAsync(new PlannedClusterChange
        {
            Verb = ChangeVerb.Invoke,
            Kind = plural,
            Name = name,
            Namespace = ns,
            Resource = $"{plural}/{subresource}",
            Kubeconfig = kubeconfig,
            Summary = summary ?? $"{subresource} {plural}/{name} in {ns}",
        }, ct);

        k8s.Kubernetes client = _proxies.Get(kubeconfig);

        using HttpRequestMessage request = new(HttpMethod.Put, client.BaseUri.ToString().TrimEnd('/') + path)
        {
            // KubeVirt's action endpoints take an options object; an empty one means the defaults.
            Content = new StringContent("{}", System.Text.Encoding.UTF8, "application/json"),
        };

        using HttpResponseMessage response = await client.HttpClient.SendAsync(request, ct);

        if (!response.IsSuccessStatusCode)
        {
            string body = await response.Content.ReadAsStringAsync(ct);
            throw new InvalidOperationException(
                $"{subresource} {plural}/{name} failed ({(int)response.StatusCode}): {SubresourceError(body)}");
        }
    }

    public async Task<System.Net.WebSockets.WebSocket> ConnectSubresourceAsync(
        string groupVersion, string plural, string name, string ns, string subresource,
        string webSocketSubProtocol, string kubeconfig, CancellationToken ct = default)
    {
        string path = BuildSubresourcePath(groupVersion, plural, name, ns, subresource);

        using MemoryStream stream = new(System.Text.Encoding.UTF8.GetBytes(kubeconfig));
        using SubresourceStreamClient client = new(
            k8s.KubernetesClientConfiguration.BuildConfigFromConfigFile(stream));

        // The socket outlives the client: it is a connected ClientWebSocket of its own, and the client
        // only lent it the credential while connecting.
        return await client.ConnectAsync(path, webSocketSubProtocol, ct);
    }

    /// <summary>
    /// The library's websocket connect is protected; its public callers only build pod exec, attach
    /// and port-forward paths. This exposes it for one already-validated path and nothing else.
    /// </summary>
    private sealed class SubresourceStreamClient(k8s.KubernetesClientConfiguration config)
        : k8s.Kubernetes(config)
    {
        public Task<System.Net.WebSockets.WebSocket> ConnectAsync(
            string path, string subProtocol, CancellationToken ct) =>
            StreamConnectAsync(ToWebSocketUri(BaseUri, path), subProtocol, null, ct);
    }

    /// <summary>
    /// The API server's address as a websocket URI. The library's own exec and attach callers make this
    /// conversion before connecting, so its connect does not — given https:// it fails the handshake.
    /// A server URL with a path prefix (a proxy in front of the API server) keeps it.
    /// </summary>
    public static Uri ToWebSocketUri(Uri apiServer, string path)
    {
        UriBuilder builder = new(apiServer)
        {
            Scheme = apiServer.Scheme == Uri.UriSchemeHttps ? "wss" : "ws",
            Port = apiServer.IsDefaultPort ? -1 : apiServer.Port,
        };
        builder.Path = builder.Path.TrimEnd('/') + "/" + path.TrimStart('/');
        return builder.Uri;
    }

    /// <summary>
    /// The request path for <see cref="InvokeSubresourceAsync"/>. Every segment must be a DNS label or
    /// subdomain (the group version may carry one '/'), so nothing a caller passes can add a segment,
    /// a query or a traversal.
    /// </summary>
    public static string BuildSubresourcePath(
        string groupVersion, string plural, string name, string ns, string subresource)
    {
        string[] gv = groupVersion.Split('/');
        if (gv.Length != 2 || !IsDnsName(gv[0]) || !IsDnsName(gv[1]))
            throw new ArgumentException($"'{groupVersion}' is not a group/version.", nameof(groupVersion));

        foreach ((string value, string param) in new[]
                 { (plural, nameof(plural)), (name, nameof(name)), (ns, nameof(ns)), (subresource, nameof(subresource)) })
        {
            if (!IsDnsName(value))
                throw new ArgumentException($"'{value}' is not a valid {param}.", param);
        }

        return $"/apis/{groupVersion}/namespaces/{ns}/{plural}/{name}/{subresource}";

        static bool IsDnsName(string s) =>
            s.Length is > 0 and <= 253
            && System.Text.RegularExpressions.Regex.IsMatch(s, "^[a-z0-9]([-a-z0-9.]*[a-z0-9])?$");
    }

    /// <summary>The API server's Status message when the body is one, otherwise the body itself.</summary>
    private static string SubresourceError(string body)
    {
        try
        {
            using System.Text.Json.JsonDocument doc = System.Text.Json.JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("message", out System.Text.Json.JsonElement m)
                && m.GetString() is { Length: > 0 } message)
                return message;
        }
        catch (System.Text.Json.JsonException) { }

        return body.Length > 500 ? body[..500] : body;
    }

    /// <summary>
    /// One HTTP request to an in-cluster Service, through the API server's proxy endpoint.
    ///
    /// <para>⚠️ <b>Authorization is not touched.</b> The request authenticates to the API SERVER,
    /// and the k8s client sets that header from the kubeconfig. Overwriting it with the Service's
    /// own credential makes the API server reject the call with a 401 before it is ever forwarded —
    /// which reads exactly like the Service rejecting the credential and is nothing of the sort.
    /// Unknown headers pass through untouched, so a Service's own credential travels in one of
    /// those. That lesson was learned painfully in the telemetry node client and belongs here now
    /// that the request is built here.</para>
    ///
    /// <para>Not gated: a proxied request is the caller's own protocol with something running in
    /// the cluster, and the gate acknowledges changes to cluster STATE. The pool is shared per
    /// kubeconfig so a page issuing a dozen queries pays one TLS handshake.</para>
    /// </summary>
    public async Task<ServiceProxyResponse> ProxyServiceAsync(
        string ns, string service, int port, HttpMethod method, string path, string kubeconfig,
        object? body = null, IReadOnlyDictionary<string, string>? headers = null,
        CancellationToken ct = default)
    {
        k8s.Kubernetes client = _proxies.Get(kubeconfig);

        string baseUrl = client.BaseUri.ToString().TrimEnd('/')
            + $"/api/v1/namespaces/{ns}/services/{service}:{port}/proxy";

        using HttpRequestMessage request = new(method, $"{baseUrl}/{path.TrimStart('/')}");

        if (headers is not null)
        {
            foreach ((string name, string value) in headers) request.Headers.Add(name, value);
        }

        if (body is not null) request.Content = JsonContent.Create(body, body.GetType());

        using HttpResponseMessage response = await client.HttpClient.SendAsync(request, ct);

        return new ServiceProxyResponse(
            (int)response.StatusCode,
            response.ReasonPhrase,
            await response.Content.ReadAsStringAsync(ct));
    }

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
        string clusterName, string ns, string sql, string kubeconfig, ChangeIntent intent, CancellationToken ct = default)
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
        string clusterName, string ns, string script, string kubeconfig, ChangeIntent intent,
        string? username = null, string? password = null, CancellationToken ct = default) =>
        ExecuteMongoWithOutputAsync(clusterName, ns, script, kubeconfig, intent, username, password, ct);

    public async Task<string> ExecuteMongoWithOutputAsync(
        string clusterName, string ns, string script, string kubeconfig, ChangeIntent intent,
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
        string podName, string ns, string sql, string kubeconfig, ChangeIntent intent,
        string username = "postgres", string? password = null, CancellationToken ct = default) =>
        ExecuteSqlOnPodCoreAsync(podName, ns, sql, kubeconfig, username, password, ct);

    public async Task<string> ExecuteSqlOnPodWithOutputAsync(
        string podName, string ns, string sql, string kubeconfig, ChangeIntent intent,
        string username = "postgres", string? password = null, CancellationToken ct = default) =>
        await ExecuteSqlOnPodCoreAsync(podName, ns, sql, kubeconfig, username, password, ct);

    public async Task<string> RunCommandOnPodAsync(
        string podName, string ns, IReadOnlyList<string> command, string kubeconfig, ChangeIntent intent,
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
        string clusterName, string ns, string database, string sql, string kubeconfig, ChangeIntent intent, CancellationToken ct = default)
    {
        await ExecuteSqlInCnpgDatabaseWithOutputAsync(clusterName, ns, database, sql, kubeconfig, intent, ct);
    }

    public async Task<string> ExecuteSqlInCnpgDatabaseWithOutputAsync(
        string clusterName, string ns, string database, string sql, string kubeconfig, ChangeIntent intent, CancellationToken ct = default)
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
        string podName, string ns, IReadOnlyList<string> command, string stdin, string kubeconfig, ChangeIntent intent,
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
