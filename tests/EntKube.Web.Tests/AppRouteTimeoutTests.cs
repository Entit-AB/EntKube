using EntKube.Web.Data;
using EntKube.Web.Services;
using FluentAssertions;

namespace EntKube.Web.Tests;

/// <summary>
/// Covers the per-rule request timeout on app-level HTTPRoutes. Without a timeouts block
/// the gateway waits indefinitely, so a wedged backend hangs the browser instead of failing
/// fast — but a finite timeout bounds the whole exchange, so streaming paths must be able to
/// opt out with 0 while the rest of the hostname keeps failing fast.
/// </summary>
public class AppRouteTimeoutTests
{
    private static AppDeploymentRoute Rule(string pathPrefix, string serviceName, int? timeoutSeconds)
    {
        AppRoute route = new() { Id = Guid.NewGuid(), AppId = Guid.NewGuid(), Hostname = "app.example.com" };

        return new AppDeploymentRoute
        {
            Id = Guid.NewGuid(),
            AppRouteId = route.Id,
            AppRoute = route,
            AppDeploymentId = Guid.NewGuid(),
            AppDeployment = new AppDeployment
            {
                Id = Guid.NewGuid(),
                Name = "prod",
                Namespace = "apps"
            },
            PathPrefix = pathPrefix,
            ServiceName = serviceName,
            ServicePort = 8080,
            GatewayName = "traefik-gateway",
            GatewayNamespace = "traefik",
            RequestTimeoutSeconds = timeoutSeconds
        };
    }

    [Fact]
    public void GenerateHttpRouteYaml_NoTimeoutSet_AppliesPlatformDefault()
    {
        AppDeploymentRoute dr = Rule("/", "web", null);

        string yaml = AppRouteService.GenerateHttpRouteYaml(dr.AppRoute, [dr]);

        yaml.Should().Contain("      timeouts:");
        yaml.Should().Contain($"        request: {ExternalRouteService.DefaultRequestTimeoutSeconds}s");
        yaml.Should().Contain($"        backendRequest: {ExternalRouteService.DefaultRequestTimeoutSeconds}s");
    }

    [Fact]
    public void GenerateHttpRouteYaml_ZeroTimeout_EmitsNoTimeoutsBlock()
    {
        AppDeploymentRoute dr = Rule("/", "web", 0);

        string yaml = AppRouteService.GenerateHttpRouteYaml(dr.AppRoute, [dr]);

        yaml.Should().NotContain("timeouts:");
    }

    [Fact]
    public void GenerateHttpRouteYaml_TimeoutIsPerRule_NotPerHostname()
    {
        // Two paths on one hostname: the streaming one opts out, the other still fails fast.

        AppDeploymentRoute api = Rule("/api", "api-svc", 30);
        AppDeploymentRoute events = Rule("/events", "sse-svc", 0);
        events.AppRoute = api.AppRoute;

        string yaml = AppRouteService.GenerateHttpRouteYaml(api.AppRoute, [api, events]);

        // Exactly one rule carries a timeouts block, and it is the /api one.
        yaml.Split('\n').Count(l => l.Trim() == "timeouts:").Should().Be(1);
        yaml.Should().Contain("        request: 30s");

        int apiIndex = yaml.IndexOf("api-svc", StringComparison.Ordinal);
        int sseIndex = yaml.IndexOf("sse-svc", StringComparison.Ordinal);
        int timeoutIndex = yaml.IndexOf("timeouts:", StringComparison.Ordinal);
        timeoutIndex.Should().BeGreaterThan(apiIndex).And.BeLessThan(sseIndex);
    }

    [Fact]
    public void GenerateHttpRouteYaml_RewriteFilterAndTimeout_BothLandInTheSameRule()
    {
        AppDeploymentRoute dr = Rule("/int/company-data", "backend", 45);
        dr.RewritePath = "/";

        string yaml = AppRouteService.GenerateHttpRouteYaml(dr.AppRoute, [dr]);

        yaml.Should().Contain("      filters:");
        yaml.Should().Contain("      backendRefs:");
        yaml.Should().Contain("      timeouts:");
        yaml.Should().Contain("        request: 45s");
    }

    // ──────── Gateway API duration parsing (import/adoption) ────────

    [Theory]
    [InlineData("30s", 30)]
    [InlineData("1m", 60)]
    [InlineData("1m30s", 90)]
    [InlineData("2h", 7200)]
    [InlineData("0s", 0)]
    [InlineData("500ms", 1)]   // rounds up — rounding down to 0 would mean "no timeout"
    [InlineData("garbage", null)]
    [InlineData("", null)]
    public void ParseGatewayDurationSeconds_ReadsGatewayApiDurations(string duration, int? expected)
    {
        DeploymentImportService.ParseGatewayDurationSeconds(duration).Should().Be(expected);
    }
}
