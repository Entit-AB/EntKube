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
/// Service registrations for the <see cref="Module.Identity"/> module — users, tenants, customers, roles, API tokens, JIT access and Keycloak.
///
/// <para>Moved out of <c>Program.cs</c>, which had 188 of these in one run with nothing
/// saying which subsystem any of them belonged to. Which module owns a service is recorded
/// in <see cref="ModuleMap.Services"/>, and <c>ModuleCompositionTests</c> keeps that map and
/// these files from drifting apart.</para>
/// </summary>
internal static class IdentityServices
{
    internal static IServiceCollection AddIdentityModule(this IServiceCollection services)
    {
        services.AddScoped<EntKube.Web.Services.Sso.ExternalGroupSync>();
        services.AddScoped<TenantService>();
        services.AddScoped<UserAccessService>();
        services.AddScoped<UserManagementService>();
        services.AddScoped<TenantRoleService>();
        services.AddScoped<CustomerAccessService>();
        services.AddHostedService<EntKube.Web.Services.Jit.JitGrantReaperService>();
        services.AddScoped<EntKube.Web.Services.Jit.JitAccessService>();
        services.AddScoped<EntKube.Web.Services.Jit.JitProxyService>();
        services.AddSingleton<EntKube.Web.Services.Jit.JitUpstreamClientPool>();
        services.AddScoped<EntKube.Web.Services.PublicApi.ApiTokenService>();
        services.AddScoped<EntKube.Web.Services.Scim.ScimUserService>();
        services.AddScoped<KeycloakService>();
        services.AddScoped<AuditService>();
        services.AddHostedService<KeycloakBackupSchedulerService>();

        return services;
    }
}
