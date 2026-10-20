using EntKube.Web.Data;
using EntKube.Web.Data.Modules;
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
public class CustomerAccessService(IDbContextFactory<IdentityDbContext> dbFactory)
{
    /// <summary>
    /// Whether a customer is this tenant's.
    ///
    /// <para>⚠️ The rows this service writes <b>are</b> authorization: a <c>CustomerAccess</c>
    /// row is what lets a user open a customer in the portal. So the grant, the revoke and the
    /// role change are not ordinary writes that happen to need scoping — they decide who may
    /// read somebody's deployments and, at Operator, restart their pods.</para>
    ///
    /// <para>And the consumer side is <i>correct</i>, which is what made this dangerous rather
    /// than merely untidy: <see cref="GetAccessAsync"/> and
    /// <see cref="GetAccessibleCustomersAsync"/> are keyed on the signed-in user, so a row
    /// planted against another tenant's customer is honoured by a check that is doing its job.
    /// A scoped consumer does not contain an unscoped producer; it legitimises it.</para>
    /// </summary>
    private static Task<bool> CustomerIsTenantsAsync(
        IdentityDbContext db, Guid tenantId, Guid customerId, CancellationToken ct) =>
        db.Customers.AnyAsync(c => c.Id == customerId && c.TenantId == tenantId, ct);

    /// <summary>
    /// Grants a user access to a customer with a specific role. The default
    /// role is Viewer, which lets them browse apps, deployments, and logs
    /// without modifying anything.
    /// </summary>
    /// <param name="tenantId">
    /// Whose customer. ⚠️ This method mints access. Without the tenant it granted a user of our
    /// choosing a role on a customer in another tenant, and the portal — correctly — would have
    /// let them in.
    /// </param>
    public async Task<CustomerAccess> GrantAccessAsync(
        Guid tenantId,
        string userId,
        Guid customerId,
        CustomerAccessRole role = CustomerAccessRole.Viewer,
        CancellationToken ct = default)
    {
        using IdentityDbContext db = dbFactory.CreateDbContext();

        if (!await CustomerIsTenantsAsync(db, tenantId, customerId, ct))
        {
            throw new InvalidOperationException("The customer does not belong to this tenant.");
        }

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
    /// <param name="tenantId">
    /// Whose customer. Revoking is the denial direction: it locks another tenant's portal user
    /// out of the customer they are responsible for.
    /// </param>
    public async Task RevokeAccessAsync(
        Guid tenantId, string userId, Guid customerId, CancellationToken ct = default)
    {
        using IdentityDbContext db = dbFactory.CreateDbContext();

        // FindAsync takes a key — here a composite one — and cannot carry a predicate.
        CustomerAccess? access = await db.CustomerAccesses
            .FirstOrDefaultAsync(
                a => a.UserId == userId && a.CustomerId == customerId
                  && a.Customer.TenantId == tenantId, ct);

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
    /// <param name="tenantId">
    /// Whose customer. ⚠️ The escalation direction, and it has no caller today: the doc above
    /// describes promoting a Viewer to Operator "so they can restart pods and trigger
    /// redeployments". It is public surface, so it is scoped like the two beside it rather than
    /// left as the one unscoped way to change a role.
    /// </param>
    public async Task UpdateRoleAsync(
        Guid tenantId,
        string userId,
        Guid customerId,
        CustomerAccessRole newRole,
        CancellationToken ct = default)
    {
        using IdentityDbContext db = dbFactory.CreateDbContext();

        CustomerAccess? access = await db.CustomerAccesses
            .FirstOrDefaultAsync(
                a => a.UserId == userId && a.CustomerId == customerId
                  && a.Customer.TenantId == tenantId, ct);

        if (access is not null)
        {
            access.Role = newRole;
            await db.SaveChangesAsync(ct);
        }
    }

    /// <summary>
    /// Returns a user's access record for a specific customer, or null if
    /// they don't have access. Used to check authorization before operations.
    ///
    /// <para><b>Deliberately takes no tenant.</b> This is the question "may <i>this</i> user see
    /// this customer", and its one caller is the portal passing the <b>signed-in</b> user's id.
    /// The user is the principal, so there is no caller tenant distinct from the row being
    /// asked about — adding one would compare the row to itself. Same for
    /// <see cref="GetAccessibleCustomersAsync"/>.</para>
    ///
    /// <para>⚠️ The precondition: <b>the <c>userId</c> must be the caller's own.</b> A caller that
    /// passes somebody else's user id is asking a question it has not earned, and this method
    /// cannot tell. It is the producers above that are scoped by tenant, not this.</para>
    /// </summary>
    public async Task<CustomerAccess?> GetAccessAsync(
        string userId, Guid customerId, CancellationToken ct = default)
    {
        using IdentityDbContext db = dbFactory.CreateDbContext();

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
        using IdentityDbContext db = dbFactory.CreateDbContext();

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
    /// <param name="tenantId">
    /// Whose customer. This includes the <c>User</c> row and orders by user name, so an
    /// unscoped read hands back the names and sign-in addresses of another tenant's portal
    /// users.
    /// </param>
    public async Task<List<CustomerAccess>> GetCustomerUsersAsync(
        Guid tenantId, Guid customerId, CancellationToken ct = default)
    {
        using IdentityDbContext db = dbFactory.CreateDbContext();

        return await db.CustomerAccesses
            .Include(ca => ca.User)
            .Where(ca => ca.CustomerId == customerId && ca.Customer.TenantId == tenantId)
            .OrderBy(ca => ca.User.UserName)
            .ToListAsync(ct);
    }
}
