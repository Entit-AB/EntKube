using EntKube.Web.Services;
using FluentAssertions;

namespace EntKube.Web.Tests;

/// <summary>
/// Naming a remote registry's provider the way Harbor names it.
///
/// <para>Harbor resolves the <c>type</c> field of a new registry against a table of adapter factories,
/// and the lookup miss it raises is a plain Go error with no code attached. Harbor's error middleware
/// has nowhere to file a codeless error, so it answers <c>500 {"code":"UNKNOWN","message":"internal
/// server error"}</c> — the same answer it gives for a genuinely broken Harbor, and it never mentions
/// the provider. A plausible-looking identifier such as <c>ghcr</c> or <c>gcr</c> therefore fails in a
/// way that points at the server rather than at the spelling.</para>
///
/// <para>The picker is filled from Harbor's own /replication/adapterinfos at runtime; this list is only
/// what stands in when that call cannot be made, so it is the copy that can drift unnoticed.</para>
/// </summary>
public class HarborRegistryAdapterTests
{
    /// <summary>
    /// Harbor's adapter identifiers, from the RegistryType* constants in
    /// src/pkg/reg/model/registry.go. Nothing outside this set is a registry provider.
    /// </summary>
    private static readonly string[] HarborAdapterTypes =
    [
        "harbor", "docker-hub", "docker-registry", "huawei-SWR", "google-gcr", "aws-ecr",
        "azure-acr", "ali-acr", "jfrog-artifactory", "quay", "gitlab", "dtr", "tencent-tcr",
        "github-ghcr", "volcengine-cr", "helm-hub", "artifact-hub"
    ];

    [Fact]
    public void Every_offered_provider_is_one_Harbor_has_an_adapter_for()
    {
        foreach (HarborRegistryAdapter adapter in HarborService.FallbackRegistryAdapters)
        {
            HarborAdapterTypes.Should().Contain(adapter.Type,
                $"Harbor answers a provider it does not know with an unexplained 500, so \"{adapter.Type}\" "
                + "would look like a broken registry rather than a bad identifier");
        }
    }

    /// <summary>
    /// The two spellings that shipped: both are what the provider is colloquially called, and both
    /// are wrong. They are the obvious thing to write again.
    /// </summary>
    [Theory]
    [InlineData("ghcr")]
    [InlineData("gcr")]
    public void The_colloquial_spellings_are_not_offered(string colloquial)
    {
        HarborService.FallbackRegistryAdapters.Should().NotContain(a => a.Type == colloquial,
            "Harbor calls these github-ghcr and google-gcr");
    }

    [Fact]
    public void Providers_are_offered_once_and_carry_a_label()
    {
        HarborService.FallbackRegistryAdapters.Select(a => a.Type).Should().OnlyHaveUniqueItems();
        HarborService.FallbackRegistryAdapters.Should().OnlyContain(a => a.Label.Length > 0);
    }

    /// <summary>
    /// A fixed endpoint locks the URL box, so an identifier with the wrong address there is a
    /// registry nobody can correct from the form.
    /// </summary>
    [Fact]
    public void Docker_Hub_is_pinned_to_the_endpoint_Harbor_fixes_it_to()
    {
        HarborRegistryAdapter hub = HarborService.FallbackRegistryAdapters
            .Single(a => a.Type == "docker-hub");

        hub.FixedUrl.Should().Be("https://hub.docker.com");
    }
}
