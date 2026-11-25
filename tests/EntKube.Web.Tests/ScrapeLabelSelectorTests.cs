using System.Text.Json.Nodes;
using EntKube.Web.Services.ScrapeMonitors;
using FluentAssertions;

namespace EntKube.Web.Tests;

/// <summary>
/// Label selectors read the way Kubernetes reads them, with a third answer for what cannot be told.
/// </summary>
public class ScrapeLabelSelectorTests
{
    private static readonly Dictionary<string, string> Labels = ScrapeWorld.Labels(("app", "redis"), ("role", "leader"));

    private static bool? Selects(string? json)
        => LabelSelector.Selects(LabelSelector.From(json is null ? null : JsonNode.Parse(json)), Labels);

    /// <summary>The Prometheus CRD's own words: empty selects every object, null selects none.</summary>
    [Fact]
    public void An_absent_selector_selects_nothing_and_an_empty_one_everything()
    {
        Selects(null).Should().BeFalse();
        Selects("{}").Should().BeTrue();
        Selects("""{"matchLabels":{}}""").Should().BeTrue();
    }

    [Theory]
    [InlineData("""{"matchLabels":{"app":"redis"}}""", true)]
    [InlineData("""{"matchLabels":{"app":"redis","role":"leader"}}""", true)]
    [InlineData("""{"matchLabels":{"app":"redis","role":"follower"}}""", false)]
    [InlineData("""{"matchLabels":{"missing":"x"}}""", false)]
    [InlineData("""{"matchExpressions":[{"key":"role","operator":"In","values":["leader","follower"]}]}""", true)]
    [InlineData("""{"matchExpressions":[{"key":"role","operator":"In","values":["follower"]}]}""", false)]
    [InlineData("""{"matchExpressions":[{"key":"missing","operator":"In","values":["x"]}]}""", false)]
    [InlineData("""{"matchExpressions":[{"key":"role","operator":"NotIn","values":["leader"]}]}""", false)]
    [InlineData("""{"matchExpressions":[{"key":"role","operator":"NotIn","values":["follower"]}]}""", true)]
    [InlineData("""{"matchExpressions":[{"key":"missing","operator":"NotIn","values":["x"]}]}""", true)]
    [InlineData("""{"matchExpressions":[{"key":"role","operator":"Exists"}]}""", true)]
    [InlineData("""{"matchExpressions":[{"key":"missing","operator":"Exists"}]}""", false)]
    [InlineData("""{"matchExpressions":[{"key":"role","operator":"DoesNotExist"}]}""", false)]
    [InlineData("""{"matchExpressions":[{"key":"missing","operator":"DoesNotExist"}]}""", true)]
    public void Every_requirement_has_to_be_met(string selector, bool selects)
        => Selects(selector).Should().Be(selects);

    [Theory]
    [InlineData("""{"matchExpressions":[{"key":"role","operator":"Gt","values":["1"]}]}""")]
    [InlineData("""{"matchExpressions":"role"}""")]
    [InlineData("""{"matchExpressions":[{"key":"role"}]}""")]
    [InlineData("""{"matchLabels":{"app":3}}""")]
    [InlineData("""{"matchLabels":["app"]}""")]
    [InlineData("\"app=redis\"")]
    public void What_cannot_be_read_cannot_be_told(string selector)
        => Selects(selector).Should().BeNull();

    /// <summary>An operator nobody knows can only narrow, so a label that is plainly wrong still decides.</summary>
    [Fact]
    public void A_requirement_that_is_plainly_unmet_decides_whatever_else_cannot_be_told()
    {
        Selects("""{"matchLabels":{"app":"kafka"},"matchExpressions":[{"key":"role","operator":"Gt"}]}""").Should().BeFalse();
        Selects("""{"matchExpressions":[{"key":"role","operator":"Gt"},{"key":"missing","operator":"Exists"}]}""").Should().BeFalse();
    }
}
