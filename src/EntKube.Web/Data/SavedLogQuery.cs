namespace EntKube.Web.Data;

/// <summary>A named log query, shared by everyone in the tenant.</summary>
public class SavedLogQuery
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public string Name { get; set; } = "";
    public string Query { get; set; } = "";
    public string? CreatedBy { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}
