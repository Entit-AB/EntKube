namespace EntKube.Web.Services;

/// <summary>
/// Checks that a provisioned cluster's foundation actually works, rather than that it was
/// installed.
///
/// <para>The distinction is the whole point. Every component here can install cleanly and be
/// useless: a CNI whose nodes never go Ready, a cloud-controller-manager that never cleared the
/// uninitialized taint, a CSI driver with a storage class nothing binds against, a Velero pointed
/// at a bucket it cannot reach. Helm exiting zero says the chart was applied. These say the thing
/// does its job.</para>
///
/// <para>Read-only apart from one deliberate exception: storage is verified by creating a small
/// PersistentVolumeClaim and watching it bind, because nothing short of that distinguishes a
/// working CSI driver from a storage class with no provisioner behind it. The claim is removed
/// afterwards, and on every path out — including the one where it never bound.</para>
/// </summary>
public class FoundationVerifier(IKubernetesClientFactory k8s, ILogger<FoundationVerifier> logger)
{
    /// <summary>Namespace the storage probe is created in. kube-system always exists.</summary>
    private const string ProbeNamespace = "kube-system";

    private const string ProbeClaimName = "entkube-storage-check";

    public async Task<IReadOnlyList<FoundationCheck>> VerifyAsync(
        string kubeconfig, bool checkBackups, CancellationToken ct = default)
    {
        List<FoundationCheck> checks = [];

        string nodesJson = await ReadAsync("nodes", kubeconfig, ct);
        checks.Add(FoundationChecks.Nodes(nodesJson));
        checks.Add(FoundationChecks.CloudControllerManager(nodesJson));

        string storageClassJson = await ReadAsync("storageclasses.storage.k8s.io", kubeconfig, ct);
        FoundationCheck storageClass = FoundationChecks.DefaultStorageClass(storageClassJson);
        checks.Add(storageClass);

        // Only worth probing if there is a default class to probe against; otherwise the claim
        // stays Pending for reasons the previous check has already explained.
        if (storageClass.Passed)
        {
            checks.Add(await VerifyVolumeBindingAsync(kubeconfig, ct));
        }

        string deploymentsJson = await ReadAsync("deployments.apps", kubeconfig, ct);
        checks.Add(FoundationChecks.Deployment(deploymentsJson, "metrics", "Metrics server", "metrics-server"));

        if (checkBackups)
        {
            string locationsJson = await ReadAsync("backupstoragelocations.velero.io", kubeconfig, ct);
            checks.Add(FoundationChecks.BackupLocation(locationsJson));
        }

        return checks;
    }

    /// <summary>
    /// Creates a small claim, waits for it to bind, and removes it. The only check here that writes
    /// anything, and the only one that can tell a working CSI driver from a storage class with
    /// nothing behind it.
    /// </summary>
    private async Task<FoundationCheck> VerifyVolumeBindingAsync(string kubeconfig, CancellationToken ct)
    {
        string manifest = $"""
            apiVersion: v1
            kind: PersistentVolumeClaim
            metadata:
              name: {ProbeClaimName}
              namespace: {ProbeNamespace}
              labels:
                app.kubernetes.io/managed-by: entkube
              annotations:
                entkube.io/purpose: >-
                  Transient probe that proves dynamic provisioning works. Safe to delete.
            spec:
              accessModes: [ReadWriteOnce]
              resources:
                requests:
                  storage: 1Gi
            """;

        try
        {
            await k8s.ApplyManifestAsync(manifest, kubeconfig, ct);

            // A Cinder volume is created and attached, which is not instant on any cloud.
            for (int attempt = 0; attempt < 20; attempt++)
            {
                ct.ThrowIfCancellationRequested();
                await Task.Delay(TimeSpan.FromSeconds(3), ct);

                string json = await k8s.GetJsonAsync("persistentvolumeclaims", ProbeNamespace, kubeconfig, ct: ct);
                FoundationCheck check = FoundationChecks.VolumeBinding(json, ProbeClaimName);

                if (check.Passed)
                {
                    return check;
                }
            }

            return FoundationCheck.Fail("volume", "Volume provisioning",
                "A test claim was created and had not bound after a minute. The storage class exists but "
                + "nothing is serving it — check the CSI controller's logs.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return FoundationCheck.Fail("volume", "Volume provisioning",
                $"The storage probe could not be run: {ex.Message}");
        }
        finally
        {
            // On every path, including the one where it never bound — a probe left behind is a
            // volume somebody pays for and nobody recognises.
            try
            {
                await k8s.DeleteManifestAsync("persistentvolumeclaim", ProbeClaimName, ProbeNamespace,
                    kubeconfig, CancellationToken.None);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Could not remove the storage probe claim {Claim}", ProbeClaimName);
            }
        }
    }

    /// <summary>
    /// Reads a resource, treating an error as an empty list. Every judgement reports "nothing found"
    /// as a failure with its own explanation, which is more useful than a guess made here.
    /// </summary>
    private async Task<string> ReadAsync(string resource, string kubeconfig, CancellationToken ct)
    {
        try
        {
            return await k8s.GetJsonAllNamespacesAsync(resource, kubeconfig, ct: ct);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Foundation check could not read {Resource}", resource);
            return "";
        }
    }
}
