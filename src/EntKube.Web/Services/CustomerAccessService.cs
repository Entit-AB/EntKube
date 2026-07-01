using EntKube.Web.Data;
using Microsoft.EntityFrameworkCore;

namespace EntKube.Web.Services;

/// <summary>
/// Manages which users can access which customers through the customer portal.
/// A tenant administrator grants access to users, specifying a role (Viewer,
/// Operator, Admin) that controls what they can do.
///
/// When a user visits the portal, this service determines which customers they
/// can see and what actions they're allowed to perform.
/// </summary>
public class CustomerAccessService(IDbContextFactory<ApplicationDbContext> dbFactory)
{
    /// <summary>
    /// Grants a user access to a customer with a specific role. The default
    /// role is Viewer, which lets them browse apps, deployments, and logs
    /// without modifying anything.
    /// </summary>
    public async Task<CustomerAccess> GrantAccessAsync(
        string userId,
        Guid customerId,
        CustomerAccessRole role = CustomerAccessRole.Viewer,
        CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        bool exists = await db.CustomerAccesses.AnyAsync(
            a => a.UserId == userId && a.CustomerId == customerId, ct);

        if (exists)
        {
            throw new InvalidOperationException(
                $"User '{userId}' already has access to customer '{customerId}'.");
        }

        CustomerAccess access = new()
        {
            UserId = userId,
            CustomerId = customerId,
            Role = role
        };

        db.CustomerAccesses.Add(access);
        await db.SaveChangesAsync(ct);
        return access;
    }

    /// <summary>
    /// Revokes a user's access to a customer. After this, they can no longer
    /// see the customer in the portal.
    /// </summary>
    public async Task RevokeAccessAsync(
        string userId, Guid customerId, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        CustomerAccess? access = await db.CustomerAccesses.FindAsync([userId, customerId], ct);

        if (access is not null)
        {
            db.CustomerAccesses.Remove(access);
            await db.SaveChangesAsync(ct);
        }
    }

    /// <summary>
    /// Changes a user's role for a customer. For example, promoting a Viewer
    /// to Operator so they can restart pods and trigger redeployments.
    /// </summary>
    public async Task UpdateRoleAsync(
        string userId,
        Guid customerId,
        CustomerAccessRole newRole,
        CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        CustomerAccess? access = await db.CustomerAccesses.FindAsync([userId, customerId], ct);

        if (access is not null)
        {
            access.Role = newRole;
            await db.SaveChangesAsync(ct);
        }
    }

    /// <summary>
    /// Returns a user's access record for a specific customer, or null if
    /// they don't have access. Used to check authorization before operations.
    /// </summary>
    public async Task<CustomerAccess?> GetAccessAsync(
        string userId, Guid customerId, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        return await db.CustomerAccesses.FindAsync([userId, customerId], ct);
    }

    /// <summary>
    /// Returns all customers a user has been granted access to, including
    /// each customer's apps. This is the entry point for the portal — the
    /// user picks a customer, then drills into apps and deployments.
    /// </summary>
    public async Task<List<Customer>> GetAccessibleCustomersAsync(
        string userId, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        // Find all customer IDs this user has access to, then load the
        // customers with their apps in a single query.

        return await db.CustomerAccesses
            .Where(ca => ca.UserId == userId)
            .Select(ca => ca.CustomerId)
            .Join(
                db.Customers
                    .Include(c => c.Tenant)
                    .Include(c => c.Apps).ThenInclude(a => a.AppEnvironments),
                id => id,
                c => c.Id,
                (id, customer) => customer)
            .OrderBy(c => c.Name)
            .ToListAsync(ct);
    }

    /// <summary>
    /// Returns all users who have access to a specific customer, along with
    /// their roles. Used by tenant admins to manage access.
    /// </summary>
    public async Task<List<CustomerAccess>> GetCustomerUsersAsync(
        Guid customerId, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        return await db.CustomerAccesses
            .Include(ca => ca.User)
            .Where(ca => ca.CustomerId == customerId)
            .OrderBy(ca => ca.User.UserName)
            .ToListAsync(ct);
    }
}
