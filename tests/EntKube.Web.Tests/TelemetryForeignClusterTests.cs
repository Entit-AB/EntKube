using EntKube.Telemetry;
using EntKube.Web.Services;
using EntKube.Web.Services.Telemetry;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace EntKube.Web.Tests;

/// <summary>
/// A tenant that names another tenant's cluster gets nothing from the telemetry backends.
///
/// <para>⚠️ <b>This was measured as a leak before it was fixed.</b> The segment store resolves the
/// tenant FROM the cluster, so <c>LogQueryService.QueryAsync(stranger, cluster)</c> returned all six of
/// the owner's lines, the histogram and the namespace list. The cluster id reaches it from a browser's
/// select element on the log browser, the dashboards and the customer portal. The check now sits in
/// <see cref="TelemetryBackendRouter"/>, where every one of those reads passes.</para>
///
/// <para>Each refusal is asserted beside the owner reading the same thing, so a backend that refused
/// everybody would fail here rather than pass.</para>
/// </summary>
public sealed class TelemetryForeignClusterTests(LogPipelineLiveFixture host) : IClassFixture<LogPipelineLiveFixture>
{
    private readonly Guid stranger = Guid.NewGuid();

    private DateTime From => host.Now.AddHours(-1);

    private DateTime To => host.Now.AddMinutes(1);

    private static LogQueryFilter Everything => new() { Namespaces = ["shop", "jobs"] };

    private (LogQueryService Logs, ITelemetryBackends Backends, IServiceScope Scope) Services()
    {
        IServiceScope scope = host.Factory.Services.CreateScope();
        return (scope.ServiceProvider.GetRequiredService<LogQueryService>(),
                scope.ServiceProvider.GetRequiredService<ITelemetryBackends>(), scope);
    }

    [Fact]
    public async Task Another_tenant_s_log_query_reads_nothing_and_the_owner_s_reads_everything()
    {
        (LogQueryService logs, _, IServiceScope scope) = Services();
        using IServiceScope owned = scope;

        KubernetesOperationResult<List<LokiLogStream>> theirs = await logs.QueryAsync(stranger, host.ClusterId, Everything, From, To);
        KubernetesOperationResult<List<LokiLogStream>> mine = await logs.QueryAsync(host.TenantId, host.ClusterId, Everything, From, To);

        (theirs.Data?.Sum(s => s.Entries.Count) ?? 0).Should().Be(0);
        theirs.IsSuccess.Should().BeFalse("a refusal must not read as a quiet cluster");
        mine.Data!.Sum(s => s.Entries.Count).Should().Be(6);
    }

    [Fact]
    public async Task Another_tenant_gets_no_histogram_namespaces_or_pods()
    {
        (LogQueryService logs, _, IServiceScope scope) = Services();
        using IServiceScope owned = scope;

        (await logs.GetHistogramAsync(stranger, host.ClusterId, Everything, From, To)).Data.Should().BeNullOrEmpty();
        (await logs.GetNamespacesAsync(stranger, host.ClusterId, windowMinutes: 120)).Data.Should().BeNullOrEmpty();
        (await logs.GetPodsAsync(stranger, host.ClusterId, "shop", windowMinutes: 120)).Data.Should().BeNullOrEmpty();

        (await logs.GetNamespacesAsync(host.TenantId, host.ClusterId, windowMinutes: 120)).Data.Should().BeEquivalentTo(["shop", "jobs"]);
    }

    [Fact]
    public async Task Another_tenant_is_told_the_cluster_holds_nothing()
    {
        (_, ITelemetryBackends backends, IServiceScope scope) = Services();
        using IServiceScope owned = scope;

        (await backends.LogsFor(stranger).HasDataAsync(host.ClusterId)).Should().BeFalse();
        (await backends.LogsFor(stranger).CountAsync(host.ClusterId, null, null, LogLevel.None, From, To)).IsSuccess.Should().BeFalse();

        (await backends.LogsFor(host.TenantId).HasDataAsync(host.ClusterId)).Should().BeTrue();
    }

    [Fact]
    public async Task Another_tenant_gets_no_traces()
    {
        (_, ITelemetryBackends backends, IServiceScope scope) = Services();
        using IServiceScope owned = scope;

        (await backends.TracesFor(stranger).ListTracesAsync(host.ClusterId, null, From, To)).IsSuccess.Should().BeFalse();
        (await backends.TracesFor(stranger).GetServicesAsync(host.ClusterId)).IsSuccess.Should().BeFalse();

        (await backends.TracesFor(host.TenantId).ListTracesAsync(host.ClusterId, null, From, To)).IsSuccess.Should().BeTrue();
    }
}
