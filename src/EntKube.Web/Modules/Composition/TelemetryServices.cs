using EntKube.Web.Authorization;
using EntKube.Web.Client.Pages;
using EntKube.Web.Components.Account;
using EntKube.Web.Components;
using EntKube.Web.Data.Modules;
using EntKube.Web.Data;
using EntKube.Web.Services.Agents;
using EntKube.Web.Services.Telemetry;
using EntKube.Web.Services.Tickets.Bridge;
using EntKube.Web.Services.Tickets;
using EntKube.Web.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Server.Circuits;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using StackExchange.Redis;

namespace EntKube.Web.Modules.Composition;

/// <summary>
/// Service registrations for the <see cref="Module.Telemetry"/> module — logs, traces, metrics, dashboards, alerts, incidents and notifications.
///
/// <para>Moved out of <c>Program.cs</c>, which had 188 of these in one run with nothing
/// saying which subsystem any of them belonged to. Which module owns a service is recorded
/// in <see cref="ModuleMap.Services"/>, and <c>ModuleCompositionTests</c> keeps that map and
/// these files from drifting apart.</para>
/// </summary>
internal static class TelemetryServices
{
    internal static IServiceCollection AddTelemetryModule(this IServiceCollection services)
    {
        services.AddScoped<PrometheusService>();
        services.AddSingleton<ISegmentCatalog, EfSegmentCatalog>();
        services.AddSingleton<TelemetryStorageSettingService>();
        services.AddSingleton<TenantBlobStoreFactory>();
        services.AddSingleton<ITelemetryIngest, SegmentTelemetryStore>();
        services.AddScoped<TelemetryNodeClient>();
        services.AddScoped<SegmentLogService>();
        services.AddScoped<SegmentTraceService>();
        services.AddScoped<NodeLogBackend>();
        services.AddScoped<NodeTraceService>();
        services.AddScoped<ILogBackend, ClusterRoutedLogBackend>();
        services.AddScoped<ITraceQueryService, ClusterRoutedTraceService>();
        services.AddScoped<IRumQueryService, SegmentRumService>();
        services.AddScoped<IMetricsQuery, PromMetricsService>();
        services.AddSingleton<IngestTokenService>();
        services.AddScoped<EntKubeTelemetryService>();
        services.AddSingleton<IngestRateLimiter>();
        services.AddSingleton<RumSiteService>();
        services.AddScoped<TelemetryAlertRuleService>();
        services.AddScoped<DashboardService>();
        services.AddScoped<IncidentDispatcher>();
        services.AddHostedService<TelemetryAlertEvaluator>();
        services.AddScoped<LogQueryService>();
        services.AddScoped<IncidentService>();
        services.AddScoped<NotificationService>();
        services.AddScoped<NotificationProviderConfigService>();
        services.AddScoped<AlertRoutingService>();
        services.AddScoped<LokiService>();
        services.AddScoped<MimirService>();
        services.AddScoped<TempoService>();
        services.AddScoped<IncidentCorrelationService>();
        services.AddScoped<StormSuppressionService>();
        services.AddScoped<ErrorBudgetService>();
        services.AddScoped<EntKube.Web.Services.Tickets.AlertTicketBridge>();
        services.AddScoped<CustomerNotificationService>();
        services.AddHostedService<AlertSyncService>();
        services.AddHostedService<AlertEscalationService>();
        services.AddHostedService<UptimeTrackingService>();

        return services;
    }
}
