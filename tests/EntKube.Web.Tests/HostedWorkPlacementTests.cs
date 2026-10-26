using System.Text.RegularExpressions;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using EntKube.Web.Modules;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

namespace EntKube.Web.Tests;

/// <summary>
/// Where a background service runs when there is more than one EntKube instance.
///
/// <para>All twelve module compositions are added unconditionally, so every one of the ~32 hosted
/// services started on every instance and there was not even a switch to run an instance web-only.
/// Two instances therefore ran every sweep twice — wasteful for most, damaging and quiet for four
/// of them (docs/decomposition.md §9.3).</para>
///
/// <para>⚠️ The two kinds are not interchangeable, which is why this is an API and not a config
/// flag. A sweep must run on exactly one instance; <c>PresenceHeartbeatService</c> must run on
/// <em>every</em> instance, because it refreshes the circuits held by its own process — a single
/// leader would report every other instance's users as offline. A blanket "no background work
/// here" switch would have broken presence silently.</para>
/// </summary>
public class HostedWorkPlacementTests
{
    private const string Web = "../../../../../src/EntKube.Web";

    /// <summary>
    /// ⭐ The ratchet: placement is declared at every site, because a default is how the gap arose.
    ///
    /// <para>Same reasoning as <c>ChangeIntent</c> on the exec verbs — a defaulted parameter lets
    /// every existing site compile unchanged and declare nothing. There is no default here, so the
    /// next hosted service added cannot avoid saying which kind it is.</para>
    ///
    /// <para>⚠️ Comments are stripped. This file's subject documents the old call shape, and
    /// <c>HostedWork.cs</c> names <c>AddHostedService</c> in its own prose — an unstripped scan
    /// would report the fix as the defect, which has already happened twice in this programme.</para>
    /// </summary>
    [Fact]
    public void No_hosted_service_is_registered_without_declaring_where_it_runs()
    {
        string root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, Web));
        Directory.Exists(root).Should().BeTrue(
            "this test reads the web project's source; if it moved the test must follow rather "
            + "than quietly stop checking");

        List<string> offenders = [];
        int filesRead = 0;
        int declarations = 0;

        foreach (string file in Directory
                     .EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
                     .Where(p => !p.Replace('\\', '/').Contains("/obj/")
                                 && !p.Replace('\\', '/').Contains("/bin/")))
        {
            filesRead++;
            string body = Strip(File.ReadAllText(file));

            declarations += Regex.Matches(body, @"\.Add(SingletonWork|PerInstanceWork)\b").Count;

            if (Path.GetFileName(file) == "HostedWork.cs")
            {
                continue;   // the one place the primitive itself is allowed
            }

            foreach (Match m in Regex.Matches(body, @"\.AddHostedService\b"))
            {
                offenders.Add($"{Path.GetFileName(file)} calls AddHostedService directly");
            }
        }

        // Survives success: a moved root or a stripped corpus would empty the offender list and
        // pass while checking nothing. Asserted on what was READ, never on the offender list.
        filesRead.Should().BeGreaterThan(300, "the scan must actually have read the web project");
        declarations.Should().BeGreaterThan(25,
            "every hosted service should now declare its placement — if this collapses, the scan "
            + "is matching nothing and the absence check above is vacuous");

        offenders.Should().BeEmpty(
            "a hosted service registered without declaring its placement runs on every instance, "
            + "which is how two RolloutWatcherServices both call `kubectl rollout undo`");
    }

    /// <summary>
    /// ⭐ Work whose subject is THIS PROCESS must run on every instance, and getting this wrong is
    /// silent.
    ///
    /// <para>All five <c>SegmentSealService</c> registrations and <c>VolumeGuardService</c> were
    /// first classified as singleton work, which was a mistake. A <c>SegmentManagerRegistry</c>
    /// holds managers created in this process, each owning a Lucene index under this process's
    /// <c>DataPath</c>; a sealer seals its own registry, and the volume guard measures its own
    /// disk. Run them only where singleton work runs and every other instance ingests into an
    /// active index that is <b>never sealed</b> — invisible to the others, never uploaded to
    /// object storage, never trimmed, growing until the disk fills — and its final shutdown seal
    /// never runs either, so a pod termination strands whatever it had buffered.</para>
    ///
    /// <para>⚠️ Sealing is also what MAKES telemetry distributed, which is the deeper reason this
    /// is pinned. A sealed segment lands in object storage with its row in the shared catalog, so
    /// every instance can read it; an unsealed one is readable only by the process holding it.
    /// Sealing on every instance is the mechanism by which one instance's ingest becomes
    /// everyone's data.</para>
    ///
    /// <para><c>PresenceHeartbeatService</c> is here for the same shape of reason: it re-stamps
    /// the circuits held by its own process, so a single leader would report every other
    /// instance's users as offline.</para>
    /// </summary>
    [Theory]
    [InlineData("SegmentSealService")]
    [InlineData("VolumeGuardService")]
    [InlineData("PresenceHeartbeatService")]
    public void Work_about_this_process_runs_on_every_instance(string service)
    {
        string program = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, Web, "Program.cs"));
        string code = Strip(File.ReadAllText(program));

        // Every registration of this type must be the per-instance one. Matched on the type name
        // rather than a count, because the five sealers differ only in their argument.
        Regex singleton = new(@"AddSingletonWork\([^;]*\b" + Regex.Escape(service) + @"\b", RegexOptions.Singleline);
        Regex perInstance = new(@"AddPerInstanceWork\([^;]*\b" + Regex.Escape(service) + @"\b|AddPerInstanceWork<"
            + Regex.Escape(service) + ">", RegexOptions.Singleline);

        perInstance.Matches(code).Count.Should().BeGreaterThan(0,
            $"{service} owns state belonging to its own process, so it must run on every instance");

        singleton.Matches(code).Should().BeEmpty(
            $"{service} registered as singleton work means every other instance keeps state "
            + "nothing ever drains — for the sealers that is an unsealed index growing until the "
            + "disk fills");
    }

    /// <summary>
    /// ⚠️ Defaults to TRUE, and the direction matters more than the value.
    ///
    /// <para>Every existing single-instance install must keep sweeping without being
    /// reconfigured; it is the SECOND instance that opts out. A default of false would silently
    /// stop every sweep in every deployment on upgrade — the worst possible direction for this
    /// flag to fail, and not something a test would notice.</para>
    /// </summary>
    [Fact]
    public void Singleton_work_runs_unless_an_instance_opts_out()
    {
        Runs(null).Should().BeTrue("an install that has never heard of this flag must keep sweeping");
        Runs("true").Should().BeTrue();
        Runs("false").Should().BeFalse();
    }

    /// <summary>
    /// ⭐ The gate actually prevents the work, rather than merely recording a preference.
    ///
    /// <para>Asserted through <c>IHostedService.StartAsync</c>, because that is what stops a
    /// <c>BackgroundService</c>: never starting it means its <c>ExecuteAsync</c> never runs. A
    /// test that only checked a boolean would pass against a wrapper that started the service
    /// anyway.</para>
    /// </summary>
    [Fact]
    public async Task An_instance_that_opted_out_never_starts_the_work()
    {
        (Spy spy, IHostedService gated) = Registered("false");

        await gated.StartAsync(CancellationToken.None);
        spy.Started.Should().BeFalse("the sweep must not run on an instance that opted out");

        await gated.StopAsync(CancellationToken.None);
        spy.Stopped.Should().BeFalse(
            "and it must not be stopped either — a BackgroundService that was never started has "
            + "no task to wait on, and asking it to stop is how that surfaces as an error at "
            + "shutdown rather than at start");
    }

    /// <summary>The positive control: without it, "never starts" would also pass if nothing ran.</summary>
    [Fact]
    public async Task An_instance_that_runs_singleton_work_starts_and_stops_it()
    {
        (Spy spy, IHostedService gated) = Registered("true");

        await gated.StartAsync(CancellationToken.None);
        spy.Started.Should().BeTrue();

        await gated.StopAsync(CancellationToken.None);
        spy.Stopped.Should().BeTrue();
    }

    // ── helpers ───────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Builds the real registration and starts whatever came out of it.
    ///
    /// <para>⚠️ Goes through <c>AddSingletonWork</c> and a built container rather than
    /// constructing the wrapper directly. The first version of this test reflected over
    /// <c>SingletonWorkHost&lt;T&gt;</c> and failed on the generic logger's type — and a test that
    /// hand-builds the wrapper would keep passing even if the extension method stopped using it.
    /// This way the thing under test is the registration a module actually writes.</para>
    /// </summary>
    private static (Spy Spy, IHostedService Hosted) Registered(string? configured)
    {
        Dictionary<string, string?> values = configured is null
            ? []
            : new() { [EntKube.Web.Modules.HostedWork.RunSingletonWorkKey] = configured };

        IConfiguration config = new ConfigurationBuilder().AddInMemoryCollection(values).Build();

        ServiceCollection services = new();
        services.AddSingleton(config);
        services.AddLogging();
        services.AddSingleton<Spy>();
        services.AddSingletonWork<Spy>();

        ServiceProvider provider = services.BuildServiceProvider();

        IHostedService hosted = provider.GetServices<IHostedService>().Should().ContainSingle(
            "the registration must produce exactly one hosted service, whichever way it is gated")
            .Subject;

        return (provider.GetRequiredService<Spy>(), hosted);
    }

    private static bool Runs(string? configured)
    {
        (Spy spy, IHostedService hosted) = Registered(configured);
        hosted.StartAsync(CancellationToken.None).GetAwaiter().GetResult();
        return spy.Started;
    }

    private sealed class Spy : IHostedService
    {
        public bool Started;
        public bool Stopped;

        public Task StartAsync(CancellationToken cancellationToken)
        {
            Started = true;
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken)
        {
            Stopped = true;
            return Task.CompletedTask;
        }
    }

    /// <summary>Blanks comments, including trailing ones. See <c>ClusterHealthQueryTests.Strip</c>.</summary>
    private static string Strip(string source) =>
        string.Join('\n', source.Split('\n').Select(l =>
        {
            string t = l.TrimStart();
            if (t.StartsWith("//", StringComparison.Ordinal)
                || t.StartsWith("*", StringComparison.Ordinal)
                || t.StartsWith("/*", StringComparison.Ordinal))
            {
                return "";
            }

            for (int i = 1; i < l.Length - 1; i++)
            {
                if (l[i] == '/' && l[i + 1] == '/' && l[i - 1] != ':')
                {
                    return l[..i];
                }
            }

            return l;
        }));
}
