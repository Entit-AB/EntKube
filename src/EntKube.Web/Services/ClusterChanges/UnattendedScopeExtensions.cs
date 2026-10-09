using Microsoft.Extensions.DependencyInjection;

namespace EntKube.Web.Services.ClusterChanges;

/// <summary>
/// Declaring that a scope may change clusters with nobody to ask.
/// </summary>
public static class UnattendedScopeExtensions
{
    /// <summary>
    /// Declares this scope's work as unattended, naming it for the audit trail.
    ///
    /// <para>Written as an extension on the scope because that is where the decision belongs. A
    /// background service creates a scope per unit of work and then resolves whatever it needs
    /// from it; the services it resolves have no idea whether a human is waiting, and threading
    /// that knowledge down to each mutation would mean a parameter on every seam operation. The
    /// scope knows, once, at the top.</para>
    ///
    /// <para>Harmless on a scope that never reaches a cluster: the declaration is only ever read
    /// by <see cref="ClusterChangeGate"/> when a mutation arrives with no sink registered. So the
    /// set of declarations is not an estimate of what mutates — it is the list of contexts that
    /// <em>could</em>, which is the thing worth being able to read in one place.</para>
    /// </summary>
    /// <param name="reason">
    /// What the work is, not what the mechanism is: <c>"drift-scan"</c>, not <c>"background"</c>.
    /// It is written to the audit trail against every change the scope makes.
    /// </param>
    public static IDisposable DeclareUnattended(this IServiceScope scope, string reason) =>
        scope.ServiceProvider.GetRequiredService<IClusterChangeGate>().DeclareUnattended(reason);
}
