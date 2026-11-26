using EntKube.Web.Services.ScrapeMonitors;

namespace EntKube.Web.Services;

/// <summary>
/// What a monitoring panel says beside its "Apply monitors now" button once the engine has answered.
/// The engine applies every monitor on the cluster; the panel is about one workload, so it says what
/// the engine now holds about that one, in the engine's own words.
/// </summary>
/// <param name="Tone">Bootstrap's text colour: <c>success</c>, <c>warning</c> or <c>danger</c>.</param>
/// <param name="Text">A full sentence or two.</param>
public sealed record MonitorsApplied(string Tone, string Text)
{
    /// <summary>The engine's key for a CNPG database: ScrapeTargetCatalog's, which the panel tests hold it to.</summary>
    public static string DatabaseKey(string ns, string name) => $"cnpg/{ns}/{name}";

    /// <summary>The engine's key for a RabbitMQ broker.</summary>
    public static string BrokerKey(string ns, string name) => $"rabbitmq/{ns}/{name}";

    /// <summary>The engine's key for a Keycloak release (the keycloakx chart): its namespace and its release.</summary>
    public static string KeycloakKey(string ns, string release) => $"keycloak/{ns}/{release}";

    public static MonitorsApplied Failed(string? error) =>
        new("danger", string.IsNullOrWhiteSpace(error) ? "The monitors could not be applied." : error);

    /// <summary>The answer of <see cref="IScrapeMonitors.ApplyAsync"/>, about the target with this key.</summary>
    public static MonitorsApplied From(KubernetesOperationResult<ScrapeMonitorPlan> result, string key)
    {
        if (!result.IsSuccess || result.Data is not { } plan) return Failed(result.Error);

        return plan.Scope switch
        {
            MonitorPlanScope.NoPrometheus => new("warning",
                "EntKube did not install Prometheus on this cluster, so it keeps no monitors here and wrote none."),
            MonitorPlanScope.NoMonitorKinds => new("warning",
                "This cluster has no PodMonitor or ServiceMonitor resource type yet, so no monitor was written."),
            _ => plan.Rows.FirstOrDefault(r => r.Key == key) is { } row
                ? new(Scraped(row.State) ? "success" : "warning", $"Monitors applied on this cluster. {row.Detail}")
                : new("warning", "Monitors applied on this cluster, but EntKube's plan has no row for this one."),
        };
    }

    /// <summary>Somebody scrapes it, EntKube or another monitor. Every other state is a reason nobody does.</summary>
    private static bool Scraped(MonitorState state) =>
        state is MonitorState.Managed or MonitorState.CoveredElsewhere or MonitorState.Annotated;
}
