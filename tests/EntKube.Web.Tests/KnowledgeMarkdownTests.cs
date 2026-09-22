using EntKube.Web.Services.Knowledge;
using FluentAssertions;

namespace EntKube.Web.Tests;

/// <summary>
/// Rendering a knowledge section.
///
/// <para>The one that matters is the escaping. This text is written by operators and §19
/// hands it to the customer at off-boarding; the portal is the obvious next place to show
/// it. A markdown renderer that passes raw HTML through turns a runbook into a script
/// injection point against everybody who reads it.</para>
/// </summary>
public class KnowledgeMarkdownTests
{
    [Fact]
    public void Markdown_becomes_html()
    {
        string html = KnowledgeMarkdown.Render("## Restore\n\nRun `kubectl rollout undo`.").Value;

        html.Should().Contain("<h2");
        html.Should().Contain("<code>kubectl rollout undo</code>");
    }

    [Fact]
    public void Tables_render_because_a_runbook_is_mostly_tables_and_commands()
    {
        string html = KnowledgeMarkdown.Render("| A | B |\n| --- | --- |\n| 1 | 2 |").Value;

        html.Should().Contain("<table");
        html.Should().Contain("<td>1</td>");
    }

    /// <summary>A pasted tag shows as text rather than running.</summary>
    [Fact]
    public void Raw_html_is_escaped_rather_than_passed_through()
    {
        string html = KnowledgeMarkdown.Render("<script>alert('x')</script>").Value;

        html.Should().NotContain("<script>");
        html.Should().Contain("&lt;script&gt;");
    }

    [Fact]
    public void An_html_attribute_cannot_smuggle_a_handler_in()
    {
        string html = KnowledgeMarkdown.Render("<img src=x onerror=\"alert(1)\">").Value;

        html.Should().NotContain("<img");
        html.Should().Contain("&lt;img");
    }

    [Fact]
    public void Nothing_renders_as_nothing() =>
        KnowledgeMarkdown.Render(null).Value.Should().BeEmpty();

    /// <summary>The navigator shows a line of each section, so the markup has to come off.</summary>
    [Fact]
    public void An_excerpt_is_plain_text()
    {
        string excerpt = KnowledgeMarkdown.Excerpt("## Heading\n\nSome **bold** text here.");

        excerpt.Should().NotContain("#");
        excerpt.Should().NotContain("*");
        excerpt.Should().Contain("bold");
    }

    [Fact]
    public void A_long_excerpt_is_cut()
    {
        string excerpt = KnowledgeMarkdown.Excerpt(new string('a', 300), length: 40);

        excerpt.Should().HaveLength(41);
        excerpt.Should().EndWith("…");
    }
}
