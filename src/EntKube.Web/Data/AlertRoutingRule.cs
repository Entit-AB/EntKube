namespace EntKube.Web.Data;

public class AlertRoutingRule
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public required string Name { get; set; }
    public int Priority { get; set; }
    public Guid? ChannelId { get; set; }
    public string? MatchAlertName { get; set; }
    public string? MatchNamespace { get; set; }
    public string? MatchSeverity { get; set; }
    public string? MatchLabelKey { get; set; }
    public string? MatchLabelValue { get; set; }
    public bool IsEnabled { get; set; } = true;

    // When true, matching alerts are silently dropped — no incident is created.
    // ChannelId is not required for suppression rules.
    public bool SuppressIncident { get; set; }

    // Optional: restrict this rule to a specific cluster (null = match all clusters).
    public Guid? MatchClusterId { get; set; }

    public Tenant Tenant { get; set; } = null!;
    public NotificationChannel? Channel { get; set; }
    public KubernetesCluster? MatchCluster { get; set; }
}
