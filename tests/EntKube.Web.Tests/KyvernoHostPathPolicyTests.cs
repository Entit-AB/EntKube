using EntKube.Web.Data;
using EntKube.Web.Services;
using FluentAssertions;

namespace EntKube.Web.Tests;

/// <summary>
/// Tests for the hostPath policy's JMESPath expression.
///
/// The failure this guards against is not a false negative but a false POSITIVE that takes the
/// cluster with it. `spec.volumes[].hostPath` projects to null on a Pod with no volumes at all,
/// length(null) is a type error, and a Kyverno rule that fails to evaluate under the default
/// failurePolicy of Fail denies the pod. The policy meant to reject hostPath mounts instead
/// rejected every volume-less pod in its namespace — including cert-manager's ACME solver, which
/// is how a hostPath rule ends up blocking certificate renewal.
/// </summary>
public class KyvernoHostPathPolicyTests
{
    private static string Policy() => KyvernoPolicyService.BuildManifest(
        [new KyvernoPolicy
        {
            Id = Guid.NewGuid(),
            PolicyType = KyvernoPolicyType.DisallowHostPath,
            ValidationFailureAction = KyvernoValidationFailureAction.Enforce
        }],
        "acme-prod");

    [Fact]
    public void The_hostpath_expression_defaults_a_missing_volumes_list_to_empty()
    {
        // Without the `|| `[]`` the expression is nil-unsafe, and nil-unsafe here means
        // "denies pods that have no volumes", not "logs a warning".
        Policy().Should().Contain("request.object.spec.volumes[].hostPath || `[]` | length(@)");
    }

    [Fact]
    public void The_hostpath_expression_still_denies_an_actual_hostpath_mount()
    {
        // The guard must not have been bought by weakening the check itself.
        string yaml = Policy();

        yaml.Should().Contain("operator: GreaterThan");
        yaml.Should().Contain("value: \"0\"");
        yaml.Should().Contain("HostPath volumes are not allowed.");
    }
}
