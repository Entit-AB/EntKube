using EntKube.Web.Services;
using FluentAssertions;

namespace EntKube.Web.Tests;

/// <summary>
/// The slug is auto-generated from the tenant name — users shouldn't have to
/// think about URL encoding. These tests verify the conversion logic.
/// </summary>
public class SlugGenerationTests
{
    [Theory]
    [InlineData("Acme Corp", "acme-corp")]
    [InlineData("My Cool Tenant", "my-cool-tenant")]
    [InlineData("hello", "hello")]
    [InlineData("UPPERCASE", "uppercase")]
    [InlineData("special!@#chars", "special-chars")]
    [InlineData("  spaces  ", "spaces")]
    [InlineData("multi---hyphens", "multi-hyphens")]
    [InlineData("trailing-", "trailing")]
    [InlineData("-leading", "leading")]
    [InlineData("dots.and.periods", "dots-and-periods")]
    public void GenerateSlug_ProducesExpectedResult(string name, string expected)
    {
        string slug = TenantService.GenerateSlug(name);

        slug.Should().Be(expected);
    }
}
