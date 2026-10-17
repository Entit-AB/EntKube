using EntKube.Web.Data;
using EntKube.Web.Services;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Moq.Protected;

namespace EntKube.Web.Tests;

/// <summary>
/// Whose incident <see cref="NotificationService"/> is willing to notify about.
///
/// <para>⚠️ <c>ReNotifyAsync</c> took an incident id and a tenant, and used the tenant only to
/// pick the channels and routing rules — never to authorize the incident. Both halves are
/// tenant-scoped on their own, which is precisely what made the leak <b>outbound</b>: a
/// stranger's incident, with their alert name, summary and labels, pushed into <em>our</em>
/// Teams webhook or mailbox. <c>AlertIncident</c> has no <c>TenantId</c>, so the scope runs
/// through its cluster.</para>
///
/// <para>The caller was the other half of the same mistake: <c>IncidentManagement.razor</c>
/// passed <c>incident.Cluster!.TenantId</c> — the tenant read off the row being acted on, which
/// compares that row to itself — while the page had a <c>TenantId</c> parameter it used for
/// every other call on the same screen.</para>
/// </summary>
public class NotificationTenancyTests : IDisposable
{
    private readonly SqliteConnection connection;
    private readonly ApplicationDbContext db;
    private readonly TestDbContextFactory dbFactory;
    private readonly NotificationService sut;

    /// <summary>Every webhook POST the service attempted, as a URL.</summary>
    private readonly List<string> posted = [];

    public NotificationTenancyTests()
    {
        connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();

        db = new ApplicationDbContext(
            new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(connection).Options);
        db.Database.EnsureCreated();
        dbFactory = new TestDbContextFactory(connection);

        Mock<HttpMessageHandler> handler = new();
        handler.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(), ItExpr.IsAny<CancellationToken>())
            .Returns((HttpRequestMessage r, CancellationToken _) =>
            {
                posted.Add(r.RequestUri!.ToString());
                return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK));
            });

        Mock<IHttpClientFactory> http = new();
        http.Setup(f => f.CreateClient(It.IsAny<string>()))
            .Returns(() => new HttpClient(handler.Object));

        sut = new NotificationService(
            http.Object, dbFactory, new ConfigurationBuilder().Build(),
            NullLogger<NotificationService>.Instance);
    }

    public void Dispose()
    {
        db.Dispose();
        connection.Dispose();
        GC.SuppressFinalize(this);
    }

    /// <summary>A tenant with a cluster, one firing incident on it, and one webhook channel.</summary
    private (Guid Tenant, Guid Incident) Seed(string slug)
    {
        Guid tenant = Guid.NewGuid(), environment = Guid.NewGuid(), cluster = Guid.NewGuid();

        db.Tenants.Add(new Tenant { Id = tenant, Name = slug, Slug = $"{slug}-{tenant:N}" });
        db.Set<Data.Environment>().Add(new Data.Environment
        {
            Id = environment, TenantId = tenant, Name = $"{slug}-prod",
        });
        db.KubernetesClusters.Add(new KubernetesCluster
        {
            Id = cluster, TenantId = tenant, EnvironmentId = environment,
            Name = $"{slug}-cluster", ApiServerUrl = "https://k8s.example.com",
        });

        Guid incident = Guid.NewGuid();
        db.Set<AlertIncident>().Add(new AlertIncident
        {
            Id = incident, ClusterId = cluster,
            Fingerprint = $"fp-{slug}", AlertName = $"{slug}-PodCrashLooping",
            Severity = "critical", Summary = $"{slug} billing-api is down",
            StartsAt = DateTime.UtcNow.AddMinutes(-10), Status = IncidentStatus.Active,
        });

        db.Set<NotificationChannel>().Add(new NotificationChannel
        {
            Id = Guid.NewGuid(), TenantId = tenant, Name = $"{slug}-teams",
            Type = NotificationChannelType.Webhook,
            ConfigurationJson = $"{{\"url\":\"https://hooks.example.com/{slug}\"}}",
            IsEnabled = true,
        });

        db.SaveChanges();
        return (tenant, incident);
    }

    [Fact]
    public async Task Another_tenants_incident_is_not_pushed_into_our_channels()
    {
        var us = Seed("us");
        var them = Seed("them");

        // ⚠️ The count is the assertion, not the webhook. Each tenant here has exactly one
        // enabled channel, so ReNotifyAsync returns the number of channels it dispatched to:
        // 1 when the incident resolves, 0 when it does not. Asserting on the HTTP call instead
        // would be asserting on this harness — a dispatch failure is caught and logged inside
        // SendAsync, so the channel still counts as attempted either way.
        int refused = await sut.ReNotifyAsync(them.Incident, us.Tenant);

        refused.Should().Be(0,
            "their alert name, summary and labels must not reach our channels — ours were "
            + "already tenant-scoped, which is what made an unscoped incident read a leak "
            + "outwards rather than inwards");

        // Both owners' own re-notify dispatches, so the zero above is a refusal and not a
        // channel that was never going to fire.
        (await sut.ReNotifyAsync(them.Incident, them.Tenant)).Should().Be(1);
        (await sut.ReNotifyAsync(us.Incident, us.Tenant)).Should().Be(1);
    }
}
