namespace EntKube.Web.Data;

public class ExternalRouteHealthHistory
{
    public Guid Id { get; set; }
    public Guid RouteId { get; set; }
    public bool IsReachable { get; set; }
    public int? StatusCode { get; set; }
    public int? ResponseMs { get; set; }
    public DateTime CheckedAt { get; set; } = DateTime.UtcNow;

    public ExternalRoute Route { get; set; } = null!;
}
