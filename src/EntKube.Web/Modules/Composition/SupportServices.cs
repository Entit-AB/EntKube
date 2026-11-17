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
        services.AddScoped<EntKube.Web.Services.Tickets.TicketWorkService>();
        services.AddScoped<EntKube.Web.Services.Tickets.TicketOverviews>();
        services.AddScoped<EntKube.Web.Services.Tickets.TicketBulkService>();
        services.AddScoped<EntKube.Web.Services.Tickets.TextModuleService>();
        services.AddScoped<EntKube.Web.Services.Tickets.TicketTemplateService>();
        services.AddScoped<EntKube.Web.Services.Tickets.MacroService>();
        services.AddScoped<EntKube.Web.Services.Tickets.TicketChecklistService>();
        services.AddScoped<EntKube.Web.Services.Tickets.TicketDraftService>();
        services.AddScoped<EntKube.Web.Services.Tickets.IAttachmentObjectStore, EntKube.Web.Services.Tickets.S3AttachmentObjectStore>();
        services.AddScoped<EntKube.Web.Services.Tickets.TicketAttachmentService>();
        services.AddScoped<EntKube.Web.Services.Tickets.TicketNotificationService>();
        services.AddScoped<EntKube.Web.Services.Tickets.OutOfOfficeService>();
        services.AddScoped<EntKube.Web.Services.Tickets.TicketPresenceService>();
        services.AddScoped<EntKube.Web.Services.Tickets.CalendarFeedService>();
        services.AddScoped<EntKube.Web.Services.Tickets.KnowledgeBaseService>();
        services.AddScoped<EntKube.Web.Services.Tickets.TicketFollowUp>();
        services.AddSingletonWork<EntKube.Web.Services.Tickets.TicketFollowUpSweep>();
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
        services.AddSingletonWork<EntKube.Web.Services.Mail.SupportMailPoller>();
        services.AddScoped<EntKube.Web.Services.Reporting.MonthlyReportService>();
        services.AddScoped<EntKube.Contracts.Support.ISupportApi, EntKube.Web.Modules.Api.SupportApi>();
        services.AddScoped<EntKube.Contracts.Support.ITicketApi, EntKube.Web.Modules.Api.TicketApi>();

        return services;
    }
}
