using EntKube.Contracts.Secrets;
using EntKube.Web.Services;

namespace EntKube.Web.Modules.Api;

/// <summary>
/// Secrets' implementation of <see cref="ISecretsApi"/>, over <see cref="AppSecretSyncService"/>.
///
/// <para>A thin mapping and nothing else, which is the point: the operation itself already exists
/// as a service because it was lifted out of a Razor page, so the contract's job is only to restate
/// it in types that <c>EntKube.Contracts</c> owns. README rule 2 forbids handing a caller a type
/// from the web application, and the alternative — moving the report records into Contracts —
/// would put a module's internal shape in everyone's compile graph.</para>
///
/// <para>⚠️ The two enums are kept in step by <c>SecretsApiTests</c> rather than by a cast. They
/// are declared in different projects for a reason and will not stay aligned by luck: a member
/// added to one and not the other is a mapping that throws, or worse, silently relabels a binding
/// family in an operator's report.</para>
/// </summary>
public sealed class SecretsApi(AppSecretSyncService sync) : ISecretsApi
{
    public async Task<AppSecretSyncResult> SyncAppSecretsAsync(
        Guid tenantId, Guid appId, Guid? environmentId = null, CancellationToken ct = default)
    {
        AppSecretSyncReport report = await sync.SyncAppAsync(tenantId, appId, environmentId, ct);

        return new AppSecretSyncResult(
            Output: report.VaultOutput,
            Bindings: [.. report.Bindings.Select(b =>
                new SecretSyncOutcome(ToKind(b.Family), b.SecretName, b.Error))],
            UnlistedFamilies: [.. report.LoadFailures.Select(f =>
                new SecretSyncFamilyFailure(ToKind(f.Family), f.Error))],
            FatalError: report.FatalError);
    }

    /// <summary>
    /// The module's family enum as the contract's. Exhaustive on purpose — a new
    /// <see cref="SyncFamily"/> member must be named here rather than defaulting to something
    /// plausible, because a mislabelled family in a report is worse than a failure.
    /// </summary>
    /// <remarks>
    /// Public rather than private so <c>SecretsApiTests</c> can pin it directly. The test project
    /// has no <c>InternalsVisibleTo</c>, and a pure mapping between two enums is exactly the thing
    /// worth testing without standing a service up.
    /// </remarks>
    public static SecretBindingKind ToKind(SyncFamily family) => family switch
    {
        SyncFamily.Database => SecretBindingKind.Database,
        SyncFamily.Cache => SecretBindingKind.Cache,
        SyncFamily.Messaging => SecretBindingKind.Messaging,
        SyncFamily.Storage => SecretBindingKind.Storage,
        SyncFamily.Identity => SecretBindingKind.Identity,
        _ => throw new ArgumentOutOfRangeException(nameof(family), family,
            "A binding family with no contract equivalent — add it to SecretBindingKind."),
    };
}
