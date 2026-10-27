namespace EntKube.Contracts.Secrets;

/// <summary>Which kind of binding a sync outcome belongs to.</summary>
public enum SecretBindingKind
{
    Database,
    Cache,
    Messaging,
    Storage,
    Identity,
}

/// <summary>
/// One binding's result. <paramref name="Error"/> is null when it synced.
///
/// <para>⚠️ <b>The secret's NAME, never its value.</b> This crosses a module boundary and, through
/// the public API, leaves the process — so it carries the Kubernetes secret name an operator needs
/// in order to know which binding failed, and nothing that would be a credential. Worth stating
/// here rather than relying on nobody adding a field later.</para>
/// </summary>
/// <param name="Kind">Which family the binding belongs to.</param>
/// <param name="SecretName">The Kubernetes secret the binding writes.</param>
/// <param name="Error">Why it failed, or null when it synced.</param>
public sealed record SecretSyncOutcome(SecretBindingKind Kind, string SecretName, string? Error)
{
    public bool Succeeded => Error is null;
}

/// <summary>A whole family that could not be listed, so none of its bindings were attempted.</summary>
/// <param name="Kind">Which family.</param>
/// <param name="Error">Why it could not be listed.</param>
public sealed record SecretSyncFamilyFailure(SecretBindingKind Kind, string Error);

/// <summary>
/// What a whole-app secret sync did.
///
/// <para>⚠️ A sync is <b>partially successful by design</b>: one binding failing must not stop its
/// siblings, and one family failing to list must not stop the other families. So this is not a
/// boolean — a caller that only reads a success flag would report a wholly-failed cluster as a
/// success whenever a single other binding worked. <see cref="AnythingFailed"/> exists so that a
/// caller which genuinely wants one bit does not have to invent its own rule for what counts.</para>
/// </summary>
/// <param name="Output">Whatever the vault-side sync printed, when it printed anything.</param>
/// <param name="Bindings">Every binding that was attempted, in the order attempted.</param>
/// <param name="UnlistedFamilies">Families that could not be listed at all.</param>
/// <param name="FatalError">Set when the sync stopped before reaching the bindings.</param>
public sealed record AppSecretSyncResult(
    string? Output,
    IReadOnlyList<SecretSyncOutcome> Bindings,
    IReadOnlyList<SecretSyncFamilyFailure> UnlistedFamilies,
    string? FatalError)
{
    public bool AnythingFailed => FatalError is not null
        || UnlistedFamilies.Count > 0
        || Bindings.Any(b => !b.Succeeded);
}

/// <summary>
/// What Secrets offers the rest of EntKube.
///
/// <para><b>One method, because one question is being asked from outside the module.</b> The
/// public API needs to re-sync an app's bound credentials into its clusters — the operation that
/// until now lived inside <c>PortalSecretsDetail.razor</c> and could only be run by pressing a
/// button. Reading, writing, revealing and rotating individual secrets all stay inside Secrets,
/// because nothing outside it does those.</para>
///
/// <para>⚠️ Written from its caller backwards, which README rule 3 and the four unused contracts
/// behind it (#138) are the argument for. A method added here "for symmetry" — list an app's
/// secrets, read one, rotate one — would be a method nothing measures, and the four contracts
/// that were written as complete-looking surfaces had no callers at all. This contract grows when
/// a consumer appears, not before.</para>
/// </summary>
public interface ISecretsApi
{
    /// <summary>
    /// Re-sync every credential this app's deployments have bound into their clusters, plus the
    /// app's own vault secrets.
    /// </summary>
    /// <param name="tenantId">The asking tenant. Nothing is resolved without it.</param>
    /// <param name="appId">The app whose bindings to sync.</param>
    /// <param name="environmentId">
    /// Scopes the app's own vault secrets to one environment. Null means the shared set, which is
    /// what the page passes when it is not in an environment context.
    /// </param>
    /// <param name="ct">Cancellation.</param>
    Task<AppSecretSyncResult> SyncAppSecretsAsync(
        Guid tenantId, Guid appId, Guid? environmentId = null, CancellationToken ct = default);
}
