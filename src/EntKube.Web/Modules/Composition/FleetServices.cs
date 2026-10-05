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
/// Service registrations for the <see cref="Module.Fleet"/> module — clusters, nodes, cloud providers, blueprints, the apply path and disaster recovery.
///
/// <para>Moved out of <c>Program.cs</c>, which had 188 of these in one run with nothing
/// saying which subsystem any of them belonged to. Which module owns a service is recorded
/// in <see cref="ModuleMap.Services"/>, and <c>ModuleCompositionTests</c> keeps that map and
/// these files from drifting apart.</para>
/// </summary>
internal static class FleetServices
{
    internal static IServiceCollection AddFleetModule(this IServiceCollection services)
    {
        services.AddScoped<KubernetesOperationsService>();
        services.AddScoped<NodeManagementService>();
        services.AddScoped<WorkloadService>();
        services.AddSingleton<KubernetesProxyClientPool>();
        services.AddScoped<ClusterTenantResolver>();
        services.AddScoped<EntKube.Web.Services.ClusterChanges.IClusterChangeGate, EntKube.Web.Services.ClusterChanges.ClusterChangeGate>();
        services.AddScoped<IKubernetesClientFactory, KubernetesClientFactory>();
        services.AddSingleton<OpenStackHttpFactory>();
        services.AddSingleton<ClusterEgressTunnel>();
        services.AddSingleton<AgentRegistry>();
        services.AddScoped<ClusterEgressRelay>();
        services.AddScoped<OpenStackKeystoneClient>();
        services.AddScoped<OpenStackComputeService>();
        services.AddScoped<OpenStackInventoryService>();
        services.AddScoped<ClusterProvisioningService>();
        services.AddScoped<RemediationService>();
        services.AddScoped<EntKube.Web.Services.Dr.VeleroService>();
        services.AddSingleton<EntKube.Web.Services.Dr.DrScanCache>();
        services.AddHostedService<EntKube.Web.Services.Dr.DrScanService>();
        services.AddScoped<ClusterBlueprintService>();
        services.AddScoped<BlueprintFromClusterService>();
        services.AddHostedService<BootstrapRunnerService>();

        return services;
    }
}
