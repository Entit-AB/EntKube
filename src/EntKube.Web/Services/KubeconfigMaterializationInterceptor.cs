using EntKube.Web.Data;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace EntKube.Web.Services;

/// <summary>
/// Transparently populates <see cref="KubernetesCluster.Kubeconfig"/> from the tenant
/// vault whenever a cluster is materialized by EF Core. The kubeconfig plaintext is no
/// longer stored in a database column — it lives encrypted in the vault, referenced by
/// <see cref="KubernetesCluster.KubeconfigSecretId"/>. Rather than rewriting the ~350
/// call sites that read <c>cluster.Kubeconfig</c> to await a vault lookup, this interceptor
/// fills the (now <c>[NotMapped]</c>) property on load so those consumers are unaffected.
/// </summary>
public sealed class KubeconfigMaterializationInterceptor(KubeconfigResolver resolver) : IMaterializationInterceptor
{
    public object InitializedInstance(MaterializationInterceptionData materializationData, object entity)
    {
        if (entity is KubernetesCluster cluster
            && cluster.KubeconfigSecretId is Guid secretId
            && cluster.Kubeconfig is null)
        {
            cluster.Kubeconfig = resolver.Resolve(cluster.TenantId, secretId);
        }

        return entity;
    }
}
