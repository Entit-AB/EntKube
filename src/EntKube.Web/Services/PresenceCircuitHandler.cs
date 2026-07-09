using System.Security.Claims;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Server.Circuits;

namespace EntKube.Web.Services;

/// <summary>
/// Reports circuit connect/disconnect events for the authenticated user into the
/// configured <see cref="IPresenceTracker"/>, so admins can see who is online.
///
/// Registered scoped (one instance per circuit). The signed-in user's ID is
/// resolved once from the circuit's authentication state and reused for the
/// symmetric disconnect, so transient reconnects balance out correctly. The
/// circuit id is used as a stable per-connection key.
/// </summary>
public sealed class PresenceCircuitHandler : CircuitHandler
{
    private readonly IPresenceTracker _tracker;
    private readonly AuthenticationStateProvider _authStateProvider;
    private string? _userId;

    public PresenceCircuitHandler(IPresenceTracker tracker, AuthenticationStateProvider authStateProvider)
    {
        _tracker = tracker;
        _authStateProvider = authStateProvider;
    }

    public override async Task OnConnectionUpAsync(Circuit circuit, CancellationToken cancellationToken)
    {
        _userId ??= await ResolveUserIdAsync();
        if (_userId is not null)
            await _tracker.ConnectAsync(_userId, circuit.Id);
    }

    public override async Task OnConnectionDownAsync(Circuit circuit, CancellationToken cancellationToken)
    {
        if (_userId is not null)
            await _tracker.DisconnectAsync(_userId, circuit.Id);
    }

    private async Task<string?> ResolveUserIdAsync()
    {
        AuthenticationState state = await _authStateProvider.GetAuthenticationStateAsync();
        return state.User.Identity?.IsAuthenticated == true
            ? state.User.FindFirstValue(ClaimTypes.NameIdentifier)
            : null;
    }
}
