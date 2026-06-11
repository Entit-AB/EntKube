namespace EntKube.Web.Data;

public class SlaTarget
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid? CustomerId { get; set; }
    public Guid? AppId { get; set; }
    public double TargetPercent { get; set; } = 99.9;
    public int MeasurementWindowDays { get; set; } = 30;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public Tenant Tenant { get; set; } = null!;
    public Customer? Customer { get; set; }
    public App? App { get; set; }
}
