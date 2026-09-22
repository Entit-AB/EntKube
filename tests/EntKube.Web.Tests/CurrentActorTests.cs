using System.Security.Claims;
using EntKube.Web.Services;
using FluentAssertions;
using Microsoft.AspNetCore.Components.Authorization;

namespace EntKube.Web.Tests;

/// <summary>
/// Who gets recorded as having done something.
///
/// <para>The support subsystem's central claim is that no state changes without a name on
/// it — §14.3 makes confirming a priority a written act and §14.6 attaches money to a
/// mis-clocked P1. That claim was not true of the running system: the name came from a
/// component parameter and almost nothing passed one, so the ledger said "unattributed".</para>
///
/// <para>What is defended here is the other half: that reading the name can never be the
/// reason an action fails. A P1 that cannot be resolved because of an authentication
/// hiccup is an outage made out of bookkeeping.</para>
/// </summary>
public class CurrentActorTests
{
    private sealed class Stub(Func<Task<AuthenticationState>> get) : AuthenticationStateProvider
    {
        public override Task<AuthenticationState> GetAuthenticationStateAsync() => get();
    }

    private static AuthenticationState SignedInAs(string? name)
    {
        List<Claim> claims = name is null ? [] : [new Claim(ClaimTypes.Name, name)];

        return new AuthenticationState(new ClaimsPrincipal(
            new ClaimsIdentity(claims, authenticationType: "test")));
    }

    private static AuthenticationState Anonymous() =>
        new(new ClaimsPrincipal(new ClaimsIdentity()));

    [Fact]
    public async Task The_signed_in_users_name_is_what_is_recorded()
    {
        CurrentActor actor = new(new Stub(() => Task.FromResult(SignedInAs("nils"))));

        (await actor.NameAsync()).Should().Be("nils");
    }

    [Fact]
    public async Task Nobody_signed_in_is_recorded_as_unattributed()
    {
        CurrentActor actor = new(new Stub(() => Task.FromResult(Anonymous())));

        (await actor.NameAsync()).Should().Be(CurrentActor.Unattributed);
    }

    /// <summary>
    /// Authenticated but nameless — a token with no name claim. An empty string in the
    /// ledger looks like a name that failed to render; the word says what happened.
    /// </summary>
    [Fact]
    public async Task An_authenticated_user_with_no_name_is_recorded_as_unattributed()
    {
        CurrentActor actor = new(new Stub(() => Task.FromResult(SignedInAs(null))));

        (await actor.NameAsync()).Should().Be(CurrentActor.Unattributed);
    }

    /// <summary>
    /// The one that matters. Recording who did something must never be the reason the
    /// something cannot be done.
    /// </summary>
    [Fact]
    public async Task A_broken_identity_provider_does_not_stop_the_work()
    {
        CurrentActor actor = new(new Stub(
            () => throw new InvalidOperationException("no circuit")));

        (await actor.NameAsync()).Should().Be(CurrentActor.Unattributed);
    }

    /// <summary>
    /// In the portal an action is the customer's even when their user cannot be named, so
    /// the ledger says "Capio" rather than "unattributed".
    /// </summary>
    [Fact]
    public async Task The_portal_falls_back_to_the_customers_name()
    {
        CurrentActor actor = new(new Stub(() => Task.FromResult(Anonymous())));

        (await actor.NameOrAsync("Capio")).Should().Be("Capio");
    }

    [Fact]
    public async Task A_named_user_beats_the_fallback()
    {
        CurrentActor actor = new(new Stub(() => Task.FromResult(SignedInAs("anna"))));

        (await actor.NameOrAsync("Capio")).Should().Be("anna");
    }
}
