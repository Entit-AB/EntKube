namespace EntKube.Web.Data;

/// <summary>
/// Join entity linking a customer to an environment. A customer belongs to a
/// tenant, but only appears in the environments it has actually been added to —
/// creating a customer under an environment in the tenant tree makes it a member
/// of that environment alone, not of every environment in the tenant.
///
/// Membership is separate from apps on purpose: a freshly created customer has
/// no apps yet, and must still be visible under the environment it was created
/// in so apps can be added to it there.
/// </summary>
public class CustomerEnvironment
{
    public Guid CustomerId { get; set; }

    public Guid EnvironmentId { get; set; }

    public DateTime LinkedAt { get; set; } = DateTime.UtcNow;

    // Navigation
    public Customer Customer { get; set; } = null!;
    public Environment Environment { get; set; } = null!;
}
