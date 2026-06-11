namespace EntKube.Web.Data;

/// <summary>
/// Links an application user to a specific customer, granting them access
/// to view that customer's apps, deployments, and cluster operations.
///
/// A tenant admin assigns customer access to users. Once linked, the user
/// can visit the customer portal and see only their customer's apps — they
/// won't see other customers, tenants, or admin-level features.
///
/// A user can have access to multiple customers (across tenants), and a
/// customer can have multiple users with access.
/// </summary>
public class CustomerAccess
{
    public string UserId { get; set; } = null!;

    public Guid CustomerId { get; set; }

    /// <summary>
    /// The role this user holds for the customer. Controls what operations
    /// they can perform — a Viewer can browse and see logs, an Operator
    /// can also restart pods and redeploy, an Admin can manage deployments.
    /// </summary>
    public CustomerAccessRole Role { get; set; } = CustomerAccessRole.Viewer;

    public DateTime GrantedAt { get; set; } = DateTime.UtcNow;

    // Navigation
    public ApplicationUser User { get; set; } = null!;
    public Customer Customer { get; set; } = null!;
}

/// <summary>
/// What a user can do when accessing a customer's resources through the portal.
/// </summary>
public enum CustomerAccessRole
{
    /// <summary>
    /// Can view apps, deployments, resource tree, and pod logs.
    /// Cannot modify anything.
    /// </summary>
    Viewer,

    /// <summary>
    /// Everything a Viewer can do, plus restart pods, redeploy,
    /// and trigger sync operations.
    /// </summary>
    Operator,

    /// <summary>
    /// Everything an Operator can do, plus create/delete deployments
    /// and manage manifests.
    /// </summary>
    Admin
}
