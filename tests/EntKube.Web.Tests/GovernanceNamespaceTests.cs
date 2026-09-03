using EntKube.Web.Services;
using FluentAssertions;

namespace EntKube.Web.Tests;

/// <summary>
/// The governance namespace lock is validated before the tab offers to create it on a
/// cluster — an invalid DNS-1123 label would be rejected by the API server anyway, and
/// the operator should hear about it while typing rather than after an apply fails.
/// </summary>
public class GovernanceNamespaceTests
{
    [Theory]
    [InlineData("customer-prod")]
    [InlineData("app1")]
    [InlineData("a")]
    [InlineData("1-2-3")]
    public void ValidateNamespaceName_AcceptsValidLabels(string ns)
    {
        AppGovernanceService.ValidateNamespaceName(ns).Should().BeNull();
    }

    [Theory]
    [InlineData("Customer-Prod")]   // uppercase
    [InlineData("-leading")]
    [InlineData("trailing-")]
    [InlineData("has spaces")]
    [InlineData("has_underscore")]
    [InlineData("dots.not.allowed")]
    public void ValidateNamespaceName_RejectsInvalidLabels(string ns)
    {
        AppGovernanceService.ValidateNamespaceName(ns).Should().NotBeNull();
    }

    [Fact]
    public void ValidateNamespaceName_RejectsNamesOver63Characters()
    {
        string ns = new('a', 64);
        AppGovernanceService.ValidateNamespaceName(ns).Should().Contain("63");
    }

    [Fact]
    public void NamespaceCheckResult_MissingSkipsUnreachableClusters()
    {
        var result = new NamespaceCheckResult
        {
            Namespace = "customer-prod",
            Clusters =
            [
                new NamespaceClusterStatus { ClusterId = Guid.NewGuid(), ClusterName = "has-it",     Exists = true },
                new NamespaceClusterStatus { ClusterId = Guid.NewGuid(), ClusterName = "missing-it", Exists = false },
                new NamespaceClusterStatus { ClusterId = Guid.NewGuid(), ClusterName = "offline",    Error  = "connection refused" },
            ],
        };

        // An unreachable cluster's namespace state is unknown, so it must not be offered
        // for creation — only the cluster we positively know is missing it.
        result.Missing.Should().ContainSingle().Which.ClusterName.Should().Be("missing-it");
    }
}
