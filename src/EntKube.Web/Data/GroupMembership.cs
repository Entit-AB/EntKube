namespace EntKube.Web.Data;

/// <summary>
/// Join entity between a user and a group. A user can belong to multiple
/// groups within a tenant, and a group can contain multiple users.
/// </summary>
public class GroupMembership
{
    public string UserId { get; set; } = null!;

    public Guid GroupId { get; set; }

    public DateTime JoinedAt { get; set; } = DateTime.UtcNow;

    // Navigation
    public ApplicationUser User { get; set; } = null!;
    public Group Group { get; set; } = null!;
}
