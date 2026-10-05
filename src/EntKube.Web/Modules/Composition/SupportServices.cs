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
/// Service registrations for the <see cref="Module.Support"/> module — tickets, support mail, contracts, time, knowledge and on-call.
///
/// <para>Moved out of <c>Program.cs</c>, which had 188 of these in one run with nothing
/// saying which subsystem any of them belonged to. Which module owns a service is recorded
/// in <see cref="ModuleMap.Services"/>, and <c>ModuleCompositionTests</c> keeps that map and
/// these files from drifting apart.</para>
/// </summary>
internal static class SupportServices
{
    internal static IServiceCollection AddSupportModule(this IServiceCollection services)
    {
        services.AddScoped<OnCallService>();
        services.AddScoped<EntKube.Web.Services.Contracts.ContractService>();
        services.AddScoped<EntKube.Web.Services.Tickets.TicketService>();
        services.AddScoped<EntKube.Web.Services.Time.TimeService>();
        services.AddScoped<EntKube.Web.Services.Knowledge.KnowledgeService>();
        services.AddScoped<EntKube.Web.Services.Mail.MailTriageRuleService>();
        services.AddScoped<EntKube.Web.Services.Support.SupportDutyService>();
        services.AddScoped<EntKube.Web.Services.Mail.SupportMailService>();
        services.AddScoped<EntKube.Web.Services.Mail.SmtpSettingsResolver>();
        services.AddScoped<EntKube.Web.Services.Tickets.TicketNotifier>();
        services.AddScoped<EntKube.Web.Services.Mail.SupportMailboxService>();
        services.AddSingleton<EntKube.Web.Services.Mail.MailboxTokenProvider>();
        services.AddScoped<IInboundTicketAdapter, JiraAdapter>();
        services.AddScoped<IInboundTicketAdapter, ServiceNowAdapter>();
        services.AddScoped<TicketBridgeService>();
        services.AddScoped<TicketBridgeAdmin>();
        services.AddHostedService<EntKube.Web.Services.Mail.SupportMailPoller>();
        services.AddScoped<EntKube.Web.Services.Reporting.MonthlyReportService>();

        return services;
    }
}
