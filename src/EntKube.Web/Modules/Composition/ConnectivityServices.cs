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
/// Service registrations for the <see cref="Module.Connectivity"/> module — routes, ingress, service mesh, mTLS and VPN.
///
/// <para>Moved out of <c>Program.cs</c>, which had 188 of these in one run with nothing
/// saying which subsystem any of them belonged to. Which module owns a service is recorded
/// in <see cref="ModuleMap.Services"/>, and <c>ModuleCompositionTests</c> keeps that map and
/// these files from drifting apart.</para>
/// </summary>
internal static class ConnectivityServices
{
    internal static IServiceCollection AddConnectivityModule(this IServiceCollection services)
    {
        services.AddScoped<ExternalRouteService>();
        services.AddScoped<AppRouteService>();
        services.AddScoped<AppL4RouteService>();
        services.AddScoped<ConnectivityGraphService>();
        services.AddScoped<IngressDashboardService>();
        services.AddScoped<TailscaleService>();
        services.AddScoped<HeadscaleService>();
        services.AddScoped<VpnService>();
        services.AddScoped<MtlsService>();
        services.AddScoped<MeshMtlsService>();
        services.AddScoped<OutboundMtlsService>();
        services.AddHostedService<ExternalRouteHealthService>();
        services.AddHostedService<AppL4RouteHealthService>();
        services.AddHostedService<HeadscaleCertSyncService>();

        return services;
    }
}
