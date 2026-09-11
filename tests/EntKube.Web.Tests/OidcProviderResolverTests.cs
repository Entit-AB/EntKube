using EntKube.Web.Services;
using FluentAssertions;

namespace EntKube.Web.Tests;

/// <summary>
/// The OIDC-provider resolver — the one place provider-specific correctness lives. Every value
/// asserted here was checked against the provider's own docs, because the failure mode of getting
/// them wrong (a token silently rejected at login) is exactly what a unit test should catch instead
/// of a production sign-in.
/// </summary>
public class OidcProviderResolverTests
{
    [Fact]
    public void EntrasIssuerIsDerivedFromTheTenantWithTheV2Suffix()
    {
        OAuthClientBundle b = new()
        {
            Provider = OAuthProvider.Entra,
            TenantId = "11111111-2222-3333-4444-555555555555",
            ClientId = "app-guid", ClientSecret = "s",
        };
        // The trailing /v2.0 is load-bearing — the iss claim and discovery document depend on it.
        b.EffectiveIssuer.Should().Be("https://login.microsoftonline.com/11111111-2222-3333-4444-555555555555/v2.0");
    }

    [Fact]
    public void EntraRequiresTheAudienceToBeTheClientIdAndAsksForTheProfileScope()
    {
        // Both verified against Microsoft docs: Entra rejects a token whose aud != client id, and
        // preferred_username is only emitted when the profile scope is requested.
        ResolvedOidc r = OAuthClientHelper.Resolve(new OAuthClientBundle
        {
            Provider = OAuthProvider.Entra, TenantId = "tid", ClientId = "app-guid", ClientSecret = "s",
        })!;

        r.RequireAudience.Should().Be("app-guid");
        r.ClaimUsername.Should().Be("preferred_username");
        r.Scopes.Should().Contain("profile");
        r.ClaimGroups.Should().Be("groups");
    }

    [Fact]
    public void GoogleUsesEmailAsTheUsernameAndItsFixedIssuer()
    {
        ResolvedOidc r = OAuthClientHelper.Resolve(new OAuthClientBundle
        {
            Provider = OAuthProvider.Google, ClientId = "g", ClientSecret = "s",
        })!;

        r.IssuerUrl.Should().Be("https://accounts.google.com");
        r.ClaimUsername.Should().Be("email");
    }

    [Fact]
    public void AnExplicitIssuerOverridesTheDerivedOne()
    {
        OAuthClientBundle b = new()
        {
            Provider = OAuthProvider.Entra, TenantId = "tid", Issuer = "https://login.example/tid/v2.0",
            ClientId = "c", ClientSecret = "s",
        };
        b.EffectiveIssuer.Should().Be("https://login.example/tid/v2.0");
    }

    [Fact]
    public void AnIncompleteRegistrationResolvesToNullRatherThanAWrongIssuer()
    {
        // Entra with no tenant and no explicit issuer has nothing to point at.
        OAuthClientHelper.Resolve(new OAuthClientBundle
        {
            Provider = OAuthProvider.Entra, ClientId = "c", ClientSecret = "s",
        }).Should().BeNull();
    }

    [Fact]
    public void AnOperatorsExplicitScopesAreKeptOverTheDefault()
    {
        ResolvedOidc r = OAuthClientHelper.Resolve(new OAuthClientBundle
        {
            Provider = OAuthProvider.Entra, TenantId = "tid", ClientId = "c", ClientSecret = "s",
            Scopes = "openid email profile offline_access",
        })!;
        r.Scopes.Should().Be("openid email profile offline_access");
    }
}
