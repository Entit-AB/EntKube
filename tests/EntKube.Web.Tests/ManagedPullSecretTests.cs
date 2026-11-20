using EntKube.Web.Services;
using FluentAssertions;

namespace EntKube.Web.Tests;

/// <summary>
/// Whether a component's values name a pull secret the operator chose, which decides whether EntKube
/// creates and refreshes its own <c>entkube-registry</c> Secret on Apply.
///
/// <para>The question only became live when <c>YamlFormMerger.ExtractValue</c> learned to read
/// indexed paths: before that, <c>imagePullSecrets.0.name</c> always read as null and EntKube always
/// refreshed its Secret. Reading it for real exposed that EntKube's own name reaches stored values on
/// import, and must not count as the operator's choice.</para>
/// </summary>
public class ManagedPullSecretTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("replicas: 1")]
    [InlineData("imagePullSecrets: []")]
    public void No_pull_secret_named_is_not_an_operator_choice(string? values)
    {
        ComponentLifecycleService.NamesOperatorChosenPullSecret(values).Should().BeFalse();
    }

    [Theory]
    [InlineData("imagePullSecrets:\n  - name: entkube-registry\n")]
    [InlineData("{\"imagePullSecrets\":[{\"name\":\"entkube-registry\"}]}")] // as an imported release stores it
    public void EntKubes_own_pull_secret_is_not_an_operator_choice(string values)
    {
        // Imported back from a release EntKube installed. Counting it as the operator's would stop
        // EntKube refreshing the Secret: a rotated credential never reaches the cluster.
        ComponentLifecycleService.NamesOperatorChosenPullSecret(values).Should().BeFalse();
    }

    [Fact]
    public void A_secret_the_operator_named_wins()
    {
        ComponentLifecycleService.NamesOperatorChosenPullSecret("imagePullSecrets:\n  - name: acme-pull\n")
            .Should().BeTrue();
    }
}
