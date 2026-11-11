using EntKube.Telemetry;
using EntKube.Web.Modules;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace EntKube.Web.Tests;

/// <summary>
/// Every signal's active index is sealed by its own <see cref="SegmentSealService"/>, and the five loops are
/// five instances of ONE type. <c>AddHostedService(factory)</c> registers through <c>TryAddEnumerable</c>,
/// which drops a second registration of the same implementation type without a word, so from the day the
/// store shipped only the logs loop existed. Spans, verbose logs, RUM and trace summaries never sealed; on
/// the management plane the span index reached 625 million documents and 125 GB, and every trace query
/// searched all of it. These tests count what the host actually holds, rather than what was registered.
/// </summary>
public class SealerRegistrationTests
{
    private sealed class Probe : IHostedService
    {
        public Task StartAsync(CancellationToken ct) => Task.CompletedTask;
        public Task StopAsync(CancellationToken ct) => Task.CompletedTask;
    }

    private static IServiceProvider Build(Action<IServiceCollection> register)
    {
        ServiceCollection services = new();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        register(services);
        return services.BuildServiceProvider();
    }

    [Fact]
    public void Two_per_instance_factory_registrations_of_one_type_are_two_hosted_services()
    {
        IServiceProvider sp = Build(s =>
        {
            s.AddPerInstanceWork(_ => new Probe());
            s.AddPerInstanceWork(_ => new Probe());
        });

        sp.GetServices<IHostedService>().OfType<Probe>().Should().HaveCount(2,
            "a second registration of the same type is a second loop, not a duplicate to drop");
    }

    [Fact]
    public void Two_singleton_factory_registrations_of_one_type_are_two_hosted_services()
    {
        IServiceProvider sp = Build(s =>
        {
            s.AddSingletonWork(_ => new Probe());
            s.AddSingletonWork(_ => new Probe());
        });

        sp.GetServices<IHostedService>().OfType<SingletonWorkHost<Probe>>().Should().HaveCount(2);
    }

    [Fact]
    public void The_real_host_runs_one_seal_loop_for_every_signal()
    {
        string dir = Path.Combine(Path.GetTempPath(), "sealers-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        using WebApplicationFactory<Program> factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseEnvironment("Development");
            b.UseSetting("DatabaseProvider", "Sqlite");
            b.UseSetting("ConnectionStrings:DefaultConnection", $"Data Source={Path.Combine(dir, "app.db")}");
            b.UseSetting("Telemetry:DataPath", Path.Combine(dir, "telemetry"));
            b.UseSetting("Telemetry:ObjectStorage:Bucket", "");
            b.UseSetting("Vault:RootKey", "dGhpcyBpcyBhIDMyIGJ5dGUga2V5ISEhMTIzNDU2Nzg=");
            b.UseSetting("DataProtection:KeyPath", Path.Combine(dir, "keys"));
            b.UseSetting("Seed:AdminEmail", "");
        });
        IServiceProvider services = factory.Services;

        ISegmentManagerRegistry[] signals =
        [
            services.GetRequiredService<SegmentManagerRegistry<LogSegmentManager>>(),
            services.GetRequiredService<SegmentManagerRegistry<VerboseLogSegmentManager>>(),
            services.GetRequiredService<SegmentManagerRegistry<SpanSegmentManager>>(),
            services.GetRequiredService<SegmentManagerRegistry<RumSegmentManager>>(),
            services.GetRequiredService<SegmentManagerRegistry<TraceSummarySegmentManager>>(),
        ];

        ISegmentManagerRegistry[] sealed_ = services.GetServices<IHostedService>()
            .OfType<SegmentSealService>().Select(s => s.Registry).ToArray();

        sealed_.Should().HaveCount(signals.Length, "one loop per signal, and the host held one");
        sealed_.Should().BeEquivalentTo(signals, o => o.WithStrictOrdering().Using<ISegmentManagerRegistry>(
            c => c.Subject.Should().BeSameAs(c.Expectation)).WhenTypeIs<ISegmentManagerRegistry>(),
            "each signal must have its own loop: spans unsealed is the 125 GB index");

        try { Directory.Delete(dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}
