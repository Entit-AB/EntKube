using Markdig;
using Microsoft.AspNetCore.Components;

namespace EntKube.Web.Services.Knowledge;

/// <summary>
/// Renders the markdown an application's knowledge was written in before the knowledge merge
/// (helpdesk phase 11): a section not moved yet, and the markdown versions in a moved
/// section's history. New text is rich text and goes through <c>RichText</c> instead.
///
/// <para><b>Raw HTML is disabled, deliberately.</b> This text was written by operators, often
/// pasted from a document written somewhere else, and §19 hands the runbook over at
/// off-boarding. Markdig passes raw HTML straight through by default, which would make a
/// section a script-injection point against everybody who reads it. <c>DisableHtml</c>
/// escapes it instead, so a pasted tag shows as text.</para>
///
/// <para><b>And the output is sanitised, because <c>DisableHtml</c> is not enough.</b> The
/// advanced extensions include generic attributes, so <c>[x](https://a){onclick=…}</c> or
/// <c># Title {onmouseover=…}</c> rendered a live event handler, and Markdig filters no link
/// scheme, so <c>[x](javascript:…)</c> rendered a live link. The HTML Markdig produces goes
/// through the same allow-list as mail and answers (<c>RichText.Sanitize</c>): its tags, only
/// <c>href</c>, only http, https and mailto. Task-list checkboxes do not survive that; the
/// item text does.</para>
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
            : new MarkupString(EntKube.Web.Services.Tickets.RichText.Sanitize(Markdown.ToHtml(markdown, Pipeline)));

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
