using Microsoft.AspNetCore.Components.Authorization;

namespace EntKube.Web.Services;

/// <summary>
/// Who is doing this, for the record.
///
/// <para><b>Why this exists as a service.</b> The support subsystem's whole claim is that
/// no state changes without a person's name on it — §14.3 makes confirming a priority a
/// written act, §14.4 makes a resolution something somebody agreed, and §14.6 attaches
/// money to a mis-clocked P1. Each panel was getting that name from an
/// <c>ActorName</c> parameter, and almost nothing passed one: the ticket queue rendered
/// the detail without it, the tenant tree rendered the support inbox without it. Every
/// accept, reject, dismiss and resolution was therefore filed under "unattributed", and
/// the property the design rests on was not true of the running system.</para>
///
/// <para>Asking a parameter to carry it made every call site a place to forget. This
/// cannot be forgotten: a component injects it and gets the signed-in user.</para>
/// </summary>
public class CurrentActor(AuthenticationStateProvider auth)
{
    /// <summary>
    /// What is recorded when nobody can be identified at all. Kept as a word rather than
    /// an empty string so a row in the ledger says plainly that the name is missing,
    /// instead of looking like a name that failed to render.
    /// </summary>
    public const string Unattributed = "unattributed";

    /// <summary>
    /// The signed-in user's name, or <see cref="Unattributed"/>.
    ///
    /// <para>Never throws. An action attributed to nobody is bad; an action that fails
    /// because the identity could not be read is worse — a P1 that cannot be resolved
    /// because of an authentication hiccup is an outage made out of bookkeeping.</para>
    /// </summary>
    public async Task<string> NameAsync()
    {
        try
        {
            AuthenticationState state = await auth.GetAuthenticationStateAsync();

            string? name = state.User.Identity?.IsAuthenticated == true
                ? state.User.Identity.Name
                : null;

            return string.IsNullOrWhiteSpace(name) ? Unattributed : name;
        }
        catch
        {
            return Unattributed;
        }
    }

    /// <summary>
    /// The signed-in user's name, or <paramref name="fallback"/> when there is none — for
    /// the portal, where an action taken by a customer is attributed to the customer if
    /// their user cannot be named.
    /// </summary>
    public async Task<string> NameOrAsync(string fallback)
    {
        string name = await NameAsync();

        return name == Unattributed ? fallback : name;
    }
}
