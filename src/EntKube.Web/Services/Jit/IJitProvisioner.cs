using EntKube.Web.Data;

namespace EntKube.Web.Services.Jit;

/// <summary>
/// The credential produced by minting a grant. <see cref="Plaintext"/> and
/// <see cref="Kubeconfig"/> are returned once and never stored.
/// </summary>
public sealed record MintedCredential
{
    public required string ServiceAccountName { get; init; }

    /// <summary>The EntKube token the customer presents to the proxy. Shown once.</summary>
    public required string Plaintext { get; init; }

    /// <summary>SHA-256 of <see cref="Plaintext"/>, hex-encoded. This is what is stored.</summary>
    public required string TokenHash { get; init; }

    /// <summary>Leading characters of the token, in the clear, so grants can be told apart.</summary>
    public required string DisplayPrefix { get; init; }

    /// <summary>
    /// When the bound ServiceAccount token expires according to the API server, which may cap
    /// a requested duration below what was asked for.
    /// </summary>
    public DateTime? TokenExpiresAt { get; init; }

    /// <summary>A ready-to-use kubeconfig pointing at the proxy, not at the API server.</summary>
    public string? Kubeconfig { get; init; }
}

/// <summary>
/// Everything a grant does to a cluster, behind one seam.
///
/// Separated from <see cref="JitAccessService"/> so the lifecycle can be exercised — and shipped —
/// without a cluster, and so the one place that creates RBAC for a grant is also the one place
/// that removes it. A provisioner that mints without a matching teardown is how orphaned
/// ServiceAccounts accumulate.
/// </summary>
public interface IJitProvisioner
{
    /// <summary>
    /// Creates the per-grant ServiceAccount, Role and RoleBinding in the grant's namespace and
    /// returns a credential for it. Called with the grant already carrying its approved level,
    /// namespace and expiry.
    /// </summary>
    Task<MintedCredential> MintAsync(JitGrant grant, TimeSpan duration, CancellationToken ct = default);

    /// <summary>
    /// Removes the grant's cluster objects. Must be safe to call more than once and on a grant
    /// whose objects are already gone — the reaper cannot tell a crash mid-mint from a clean
    /// teardown, so it calls this either way.
    /// </summary>
    Task TearDownAsync(JitGrant grant, CancellationToken ct = default);
}

/// <summary>
/// A provisioner that touches no cluster. Used where the workflow is wanted without the access —
/// tests, and any deployment that has not enabled JIT cluster access.
/// </summary>
public sealed class NullJitProvisioner : IJitProvisioner
{
    public Task<MintedCredential> MintAsync(JitGrant grant, TimeSpan duration, CancellationToken ct = default) =>
        throw new InvalidOperationException(
            "JIT cluster access is not enabled on this deployment, so a grant cannot be minted.");

    public Task TearDownAsync(JitGrant grant, CancellationToken ct = default) => Task.CompletedTask;
}
