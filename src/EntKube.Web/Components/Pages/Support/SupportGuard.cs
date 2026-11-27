using EntKube.Web.Data;
using EntKube.Web.Services;

namespace EntKube.Web.Components.Pages.Support;

/// <summary>
/// The check every write in the support desk makes before it writes.
///
/// <para>The desk works out once what the person may do in the tenant and hands it down to
/// every section as a cascading value. Each handler that changes something asks here first. It
/// has to be asked in the handler, not only where buttons are drawn: a Blazor event runs on the
/// server whatever the page showed, so a stale page or an event sent by hand reaches it all the
/// same.</para>
///
/// <para>A component shown anywhere else was handed nothing, and nothing means no access —
/// a forgotten wiring refuses instead of quietly allowing everything.</para>
/// </summary>
public static class SupportGuard
{
    /// <summary>
    /// Whether the write is refused. When it is, and a toast service was given, the person is
    /// told which permission it needed.
    /// </summary>
    public static bool Refuses(
        EffectivePermissions? permissions, TenantFeature feature, AccessLevel level, ToastService? toasts = null)
    {
        if ((permissions ?? EffectivePermissions.Nothing).Allows(feature, level))
        {
            return false;
        }

        toasts?.Error(RefusalFor(feature, level));
        return true;
    }

    /// <summary>What a refused person is told: the permission, by the name the roles page uses.</summary>
    public static string RefusalFor(TenantFeature feature, AccessLevel level) =>
        $"Your role here does not allow that. It needs {PermissionCatalog.Describe(feature).Label}: {level}.";
}
