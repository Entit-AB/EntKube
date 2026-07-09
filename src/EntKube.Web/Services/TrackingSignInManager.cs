using System.Security.Claims;
using EntKube.Web.Data;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;

namespace EntKube.Web.Services;

/// <summary>
/// A <see cref="SignInManager{TUser}"/> that records the timestamp of each
/// successful sign-in on <see cref="ApplicationUser.LastLoginAt"/>.
///
/// <see cref="SignInWithClaimsAsync"/> is the single funnel every sign-in path
/// flows through — password, passkey, two-factor completion, and external
/// logins — so stamping here captures them all without touching the individual
/// login pages.
/// </summary>
public class TrackingSignInManager : SignInManager<ApplicationUser>
{
    public TrackingSignInManager(
        UserManager<ApplicationUser> userManager,
        IHttpContextAccessor contextAccessor,
        IUserClaimsPrincipalFactory<ApplicationUser> claimsFactory,
        IOptions<IdentityOptions> optionsAccessor,
        ILogger<SignInManager<ApplicationUser>> logger,
        IAuthenticationSchemeProvider schemes,
        IUserConfirmation<ApplicationUser> confirmation)
        : base(userManager, contextAccessor, claimsFactory, optionsAccessor, logger, schemes, confirmation)
    {
    }

    public override async Task SignInWithClaimsAsync(
        ApplicationUser user,
        AuthenticationProperties? authenticationProperties,
        IEnumerable<Claim> additionalClaims)
    {
        await base.SignInWithClaimsAsync(user, authenticationProperties, additionalClaims);

        // Record last login on a best-effort basis — a failure here must never
        // block the user from actually signing in.
        try
        {
            user.LastLoginAt = DateTime.UtcNow;
            await UserManager.UpdateAsync(user);
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Failed to record LastLoginAt for user {UserId}.", user.Id);
        }
    }
}
