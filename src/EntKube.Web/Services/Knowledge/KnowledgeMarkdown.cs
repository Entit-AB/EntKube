using Markdig;
using Microsoft.AspNetCore.Components;

namespace EntKube.Web.Services.Knowledge;

/// <summary>
/// Renders a knowledge section's markdown for display.
///
/// <para><b>Raw HTML is disabled, deliberately.</b> This text is written by operators and
/// will end up in front of customers — §19 hands the runbook over at off-boarding, and the
/// portal is the obvious next place to show it. Markdig passes raw HTML straight through by
/// default, which would make a knowledge section a script-injection point against everybody
/// who reads it. <c>DisableHtml</c> escapes it instead, so a pasted tag shows as text.</para>
///
/// <para>Everything else is on: tables, task lists and fenced code, because a runbook is
/// mostly commands and checklists and a renderer that cannot show them is not worth
/// having.</para>
/// </summary>
public static class KnowledgeMarkdown
{
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UseAdvancedExtensions()
        .DisableHtml()
        .Build();

    /// <summary>The markdown as HTML, safe to render.</summary>
    public static MarkupString Render(string? markdown) =>
        string.IsNullOrWhiteSpace(markdown)
            ? new MarkupString("")
            : new MarkupString(Markdown.ToHtml(markdown, Pipeline));

    /// <summary>
    /// The first line or so, for a navigator entry — enough to tell two sections apart
    /// without opening either.
    /// </summary>
    public static string Excerpt(string? markdown, int length = 90)
    {
        if (string.IsNullOrWhiteSpace(markdown))
        {
            return "";
        }

        string plain = Markdown.ToPlainText(markdown, Pipeline)
            .ReplaceLineEndings(" ")
            .Trim();

        while (plain.Contains("  ", StringComparison.Ordinal))
        {
            plain = plain.Replace("  ", " ", StringComparison.Ordinal);
        }

        return plain.Length <= length ? plain : plain[..length].TrimEnd() + "…";
    }
}
