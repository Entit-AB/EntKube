namespace EntKube.Web.Data;

/// <summary>
/// An allowed git repository URL pattern for a customer. Supports wildcards
/// using '*' (matches any sequence of characters within a segment) and '**'
/// (matches across path separators). For example:
///   https://github.com/acme/*       — any repo under the acme org
///   git@github.com:acme/*           — SSH variant of the same
///   https://dev.azure.com/contoso/** — any repo anywhere under contoso's org
/// </summary>
public class CustomerGitRepoPolicy
{
    public Guid Id { get; set; }

    public Guid CustomerId { get; set; }

    /// <summary>
    /// The URL pattern. May contain '*' or '**' wildcards.
    /// Must be unique within a customer.
    /// </summary>
    public required string UrlPattern { get; set; }

    public Guid EnvironmentId { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    // Navigation
    public Customer Customer { get; set; } = null!;
    public Environment Environment { get; set; } = null!;
}
