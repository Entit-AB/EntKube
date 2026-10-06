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
/// Service registrations for the <see cref="Module.Delivery"/> module — apps, deployments, rollouts, governance, autoscaling and git.
///
/// <para>Moved out of <c>Program.cs</c>, which had 188 of these in one run with nothing
/// saying which subsystem any of them belonged to. Which module owns a service is recorded
/// in <see cref="ModuleMap.Services"/>, and <c>ModuleCompositionTests</c> keeps that map and
/// these files from drifting apart.</para>
/// </summary>
internal static class DeliveryServices
{
    internal static IServiceCollection AddDeliveryModule(this IServiceCollection services)
    {
        services.AddSingleton<DeploymentStatusNotifier>();
        services.AddScoped<DeploymentService>();
        services.AddScoped<DeploymentImportService>();
        services.AddScoped<KyvernoPolicyService>();
        services.AddScoped<KedaScalerService>();
        services.AddScoped<EntKube.Web.Services.Rollouts.RolloutService>();
        services.AddHostedService<EntKube.Web.Services.Rollouts.RolloutWatcherService>();
        services.AddScoped<AppGovernanceService>();
        services.AddScoped<PortalServiceScopeService>();
        services.AddScoped<GitOperationsService>();
        services.AddScoped<GitRepositoryService>();
        services.AddScoped<CustomerGitService>();
        services.AddScoped<AppOfAppsService>();
        services.AddSingleton<GitSyncService>();
        services.AddScoped<GitWebhookService>();
        services.AddHostedService<DeploymentSyncService>();

        // The module's contract — what everyone else is allowed to know about it.
        services.AddScoped<EntKube.Contracts.Delivery.IDeliveryApi, EntKube.Web.Modules.Api.DeliveryApi>();

        return services;
    }
}
