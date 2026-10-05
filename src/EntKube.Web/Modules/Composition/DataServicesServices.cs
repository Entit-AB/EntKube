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
/// Service registrations for the <see cref="Module.DataServices"/> module — managed Postgres, Mongo, Redis, Kafka, RabbitMQ, Elastic, LDAP, Harbor and object storage.
///
/// <para>Moved out of <c>Program.cs</c>, which had 188 of these in one run with nothing
/// saying which subsystem any of them belonged to. Which module owns a service is recorded
/// in <see cref="ModuleMap.Services"/>, and <c>ModuleCompositionTests</c> keeps that map and
/// these files from drifting apart.</para>
/// </summary>
internal static class DataServicesServices
{
    internal static IServiceCollection AddDataServicesModule(this IServiceCollection services)
    {
        services.AddScoped<DockerRegistryService>();
        services.AddScoped<DatabaseService>();
        services.AddScoped<CnpgService>();
        services.AddScoped<MongoService>();
        services.AddScoped<RegisteredPostgresService>();
        services.AddScoped<OpenStackS3Service>();
        services.AddScoped<StorageService>();
        services.AddScoped<StorageLinkClientFactory>();
        services.AddScoped<StorageBrowserService>();
        services.AddScoped<RabbitMQService>();
        services.AddScoped<RedisService>();
        services.AddScoped<KafkaService>();
        services.AddScoped<ElasticsearchService>();
        services.AddScoped<HarborService>();
        services.AddScoped<OpenLdapService>();
        services.AddHostedService<MessagingStatusPollingService>();
        services.AddHostedService<SearchStatusPollingService>();

        return services;
    }
}
