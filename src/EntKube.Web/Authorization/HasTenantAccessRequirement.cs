using System.Security.Claims;
using EntKube.Web.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;

namespace EntKube.Web.Authorization;

public class HasTenantAccessRequirement : IAuthorizationRequirement { }

public class HasTenantAccessHandler(IDbContextFactory<ApplicationDbContext> dbFactory)
    : AuthorizationHandler<HasTenantAccessRequirement>
{
    protected override async Task HandleRequirementAsync(
        AuthorizationHandlerContext context,
        HasTenantAccessRequirement requirement)
    {
        if (context.User.Identity?.IsAuthenticated != true) return;

        if (context.User.IsInRole("Admin"))
        {
            context.Succeed(requirement);
            return;
        }

        string? userId = context.User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (userId is null) return;

        using ApplicationDbContext db = dbFactory.CreateDbContext();
        if (await db.TenantMemberships.AnyAsync(m => m.UserId == userId))
            context.Succeed(requirement);
    }
}
