using Microsoft.AspNetCore.Identity;

namespace EntKube.Web.Data;

// Add profile data for application users by adding properties to the ApplicationUser class
public class ApplicationUser : IdentityUser
{
    /// <summary>
    /// UTC timestamp of the user's most recent successful sign-in (password,
    /// passkey, two-factor completion, or external login). Null if the user has
    /// never signed in. Stamped centrally by <c>TrackingSignInManager</c>.
    /// </summary>
    public DateTime? LastLoginAt { get; set; }
}

