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
/// Service registrations for the <see cref="Module.Catalog"/> module — component installs, upgrades, drift, supply chain and CA trust.
///
/// <para>Moved out of <c>Program.cs</c>, which had 188 of these in one run with nothing
/// saying which subsystem any of them belonged to. Which module owns a service is recorded
/// in <see cref="ModuleMap.Services"/>, and <c>ModuleCompositionTests</c> keeps that map and
/// these files from drifting apart.</para>
/// </summary>
internal static class CatalogServices
{
    internal static IServiceCollection AddCatalogModule(this IServiceCollection services)
    {
        services.AddScoped<ComponentLifecycleService>();
        services.AddScoped<ComponentScanService>();
        services.AddScoped<TrustBundleService>();
        services.AddScoped<CertificateDistributionService>();
        services.AddHostedService<CertificateDistributionReconcileService>();
        services.AddSingleton<EntKube.Web.Services.Upgrades.HelmRepoIndexClient>();
        services.AddScoped<EntKube.Web.Services.Upgrades.ComponentUpgradeService>();
        services.AddScoped<EntKube.Web.Services.Upgrades.ReleaseVolumeGuard>();
        services.AddScoped<EntKube.Web.Services.Upgrades.ComponentUpgradeRunner>();
        services.AddScoped<EntKube.Web.Services.Upgrades.DriftDetectionService>();
        services.AddSingleton<EntKube.Web.Services.Upgrades.DriftScanCache>();
        services.AddHostedService<EntKube.Web.Services.Upgrades.DriftScanService>();
        services.AddScoped<EntKube.Web.Services.SupplyChain.SupplyChainService>();
        services.AddSingleton<EntKube.Web.Services.SupplyChain.SupplyChainScanCache>();
        services.AddHostedService<EntKube.Web.Services.SupplyChain.SupplyChainScanService>();
        services.AddScoped<EntKube.Web.Services.Adoption.DriftAdoptionService>();
        services.AddScoped<ComponentInstallOrchestrator>();
        services.AddScoped<CatalogComponentRegistrar>();

        return services;
    }
}
