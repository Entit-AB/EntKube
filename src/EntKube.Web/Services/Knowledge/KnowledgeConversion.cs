using System.Text;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;
using EntKube.Web.Data;
using EntKube.Web.Services.Tickets;
using Markdig;

namespace EntKube.Web.Services.Knowledge;

/// <summary>A knowledge section's markdown as knowledge-base HTML, with how faithfully it went.</summary>
/// <param name="Html">Sanitised HTML for the answer's body.</param>
/// <param name="Verdict">Exact, Mapped (see <paramref name="Notes"/>), or Fallback (preformatted markdown).</param>
/// <param name="Notes">What was mapped or lost, for the move ledger and the banner on the answer.</param>
public sealed record KnowledgeConverted(string Html, MoveVerdict Verdict, IReadOnlyList<string> Notes);

/// <summary>
/// Turns a runbook section's markdown into knowledge-base rich text (helpdesk phase 11) —
/// deterministically, and checked.
///
/// <list type="number">
/// <item>Render with exactly the pipeline the Knowledge panel used (<see cref="KnowledgeMarkdown"/>),
/// raw HTML disabled, so what is converted is what people saw.</item>
/// <item>Map what the sanitiser or the editor cannot hold, and note each: header cells become bold
/// cells in one table body (the vendored editor knows only table/tbody/tr/td), task boxes become
/// ☑/☐, images become links, links with a scheme the sanitiser drops (ssh, smb, file) become their
/// label and target as text, definition lists become paragraphs, footnote and container wrappers
/// are unwrapped, rules are dropped, diagrams and maths become preformatted text.</item>
/// <item>Sanitise with <see cref="RichText"/>.</item>
/// <item><b>Verify:</b> every character of the rendered text, and every link target, must appear in
/// order in the result. Additions (☐, "(image)") are allowed; anything missing sends the section
/// to the fallback — its markdown, whole, as preformatted text — rather than lose a word.</item>
/// </list>
///
/// <para>The section's markdown is kept as the newest earlier version either way, so nothing a
/// person wrote depends on this conversion being perfect.</para>
/// </summary>
public static class KnowledgeConversion
{
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UseAdvancedExtensions()
        .DisableHtml()
        .Build();

    public static KnowledgeConverted Convert(string? markdown) => Convert(markdown, RichText.Sanitize);

    /// <summary>
    /// The conversion with the sanitiser given — the seam that lets a test make it lose text and
    /// prove the result falls back instead. Everything else uses <see cref="RichText.Sanitize"/>.
    /// </summary>
    public static KnowledgeConverted Convert(string? markdown, Func<string, string> sanitize)
    {
        if (string.IsNullOrWhiteSpace(markdown))
        {
            return new KnowledgeConverted("", MoveVerdict.Exact, []);
        }

        string rendered = Markdown.ToHtml(markdown, Pipeline);
        List<string> notes = [];

        HtmlParser parser = new();
        IDocument reference = parser.ParseDocument($"<body>{rendered}</body>");
        string expected = Signature(reference.Body!);

        IDocument doc = parser.ParseDocument($"<body>{rendered}</body>");
        Normalise(doc, notes);

        string html = sanitize(doc.Body!.InnerHtml);
        string actual = Signature(parser.ParseDocument($"<body>{html}</body>").Body!);

        if (!IsSubsequence(expected, actual))
        {
            return new KnowledgeConverted(
                $"<pre>{System.Net.WebUtility.HtmlEncode(markdown)}</pre>",
                MoveVerdict.Fallback,
                [.. notes, "Some text could not be carried across exactly, so the section is shown as its markdown."]);
        }

        return new KnowledgeConverted(html, notes.Count == 0 ? MoveVerdict.Exact : MoveVerdict.Mapped, notes.Distinct().ToList());
    }

    private static void Normalise(IDocument doc, List<string> notes)
    {
        IElement body = doc.Body!;

        // Footnote back-links are navigation, not content.
        foreach (IElement back in body.QuerySelectorAll("a.footnote-back-ref").ToList())
        {
            back.Remove();
            notes.Add("footnote back-links removed");
        }

        // A footnote reference keeps its number visibly apart from the word before it: unwrapped
        // bare, "20[^1]" read "201" — a different number in an operating instruction.
        foreach (IElement reference in body.QuerySelectorAll("a.footnote-ref").ToList())
        {
            reference.Replace(doc.CreateTextNode($"[{reference.TextContent.Trim()}]"));
            notes.Add("footnote references shown as [n]");
        }

        // Superscript and subscript likewise: "10^6^" must not become "106".
        foreach (IElement sup in body.QuerySelectorAll("sup").ToList())
        {
            sup.Replace(doc.CreateTextNode($"^({sup.TextContent})"));
            notes.Add("superscript shown as ^(x)");
        }

        foreach (IElement sub in body.QuerySelectorAll("sub").ToList())
        {
            sub.Replace(doc.CreateTextNode($"_({sub.TextContent})"));
            notes.Add("subscript shown as _(x)");
        }

        // Embedded media: a link to it, since frames and players are not on the allow-list.
        foreach (IElement media in body.QuerySelectorAll("iframe, video, audio").ToList())
        {
            string src = media.GetAttribute("src") ?? media.QuerySelector("source")?.GetAttribute("src") ?? "";
            IElement a = doc.CreateElement("a");
            a.SetAttribute("href", src);
            a.TextContent = $"{src} (video)";
            media.Replace(src.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? a : doc.CreateTextNode($"{src} (video)"));
            notes.Add("embedded media became links");
        }

        // An abbreviation's expansion lives in an attribute the sanitiser drops: write it out.
        foreach (IElement abbr in body.QuerySelectorAll("abbr[title]").ToList())
        {
            abbr.Replace(doc.CreateTextNode($"{abbr.TextContent} ({abbr.GetAttribute("title")})"));
            notes.Add("abbreviation expansions written out");
        }

        // Tables: one tbody, header cells as bold cells.
        foreach (IElement table in body.QuerySelectorAll("table").ToList())
        {
            List<IElement> rows = [.. table.QuerySelectorAll("tr")];
            IElement tbody = doc.CreateElement("tbody");

            foreach (IElement row in rows)
            {
                IElement tr = doc.CreateElement("tr");

                foreach (IElement cell in row.Children.Where(c => c.LocalName is "td" or "th").ToList())
                {
                    IElement td = doc.CreateElement("td");

                    if (cell.LocalName == "th")
                    {
                        IElement strong = doc.CreateElement("strong");
                        strong.InnerHtml = cell.InnerHtml;
                        td.AppendChild(strong);
                        notes.Add("table header cells became bold cells");
                    }
                    else
                    {
                        td.InnerHtml = cell.InnerHtml;
                    }

                    if (cell.HasAttribute("style") || cell.HasAttribute("align"))
                    {
                        notes.Add("table column alignment dropped");
                    }

                    tr.AppendChild(td);
                }

                tbody.AppendChild(tr);
            }

            table.InnerHtml = "";
            table.AppendChild(tbody);
        }

        Rename(doc, body, "del", "s", notes, "strikethrough kept as strikethrough");
        Rename(doc, body, "ins", "u", notes, "inserted text shown underlined");
        Rename(doc, body, "mark", "strong", notes, "highlighted text shown bold");

        foreach (IElement input in body.QuerySelectorAll("input[type=checkbox]").ToList())
        {
            input.Replace(doc.CreateTextNode(input.HasAttribute("checked") ? "☑ " : "☐ "));
            notes.Add("task boxes became ☑ and ☐");
        }

        foreach (IElement img in body.QuerySelectorAll("img").ToList())
        {
            string alt = img.GetAttribute("alt") ?? "";
            string src = img.GetAttribute("src") ?? "";

            if (img.Closest("a") is not null)
            {
                // Inside a link (a badge): a nested link would be closed by the parser and leave
                // the outer one empty and invisible. Name the image as text inside the link.
                img.Replace(doc.CreateTextNode($"{(alt.Length > 0 ? alt + " " : "")}(image: {src})"));
            }
            else if (src.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || src.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                IElement a = doc.CreateElement("a");
                a.SetAttribute("href", src);
                a.TextContent = $"{(alt.Length > 0 ? alt : "image")} (image)";
                img.Replace(a);
            }
            else
            {
                img.Replace(doc.CreateTextNode(alt.Length > 0 ? $"{alt} (image)" : "(image)"));
            }

            notes.Add("images became links");
        }

        foreach (IElement a in body.QuerySelectorAll("a[href]").ToList())
        {
            string href = a.GetAttribute("href") ?? "";

            if (href.StartsWith('#'))
            {
                Unwrap(a);
                notes.Add("links within the section became text");
            }
            else if (!(href.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                       || href.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
                       || href.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase)))
            {
                string label = a.TextContent;
                a.Replace(doc.CreateTextNode(label == href ? href : $"{label} ({href})"));
                notes.Add("links the editor cannot open (ssh, smb, file, relative) kept as text");
            }
        }

        foreach (IElement dl in body.QuerySelectorAll("dl").ToList())
        {
            foreach (IElement item in dl.Children.ToList())
            {
                IElement p = doc.CreateElement("p");

                if (item.LocalName == "dt")
                {
                    IElement strong = doc.CreateElement("strong");
                    strong.InnerHtml = item.InnerHtml;
                    p.AppendChild(strong);
                }
                else
                {
                    p.InnerHtml = item.InnerHtml;
                }

                dl.Parent!.InsertBefore(p, dl);
            }

            dl.Remove();
            notes.Add("definition lists became paragraphs");
        }

        foreach (IElement hr in body.QuerySelectorAll("hr").ToList())
        {
            hr.Remove();
            notes.Add("horizontal rules removed");
        }

        // Diagrams and maths: their source, as preformatted text.
        foreach (IElement block in body.QuerySelectorAll("div.mermaid, div.math, pre.mermaid").ToList())
        {
            IElement pre = doc.CreateElement("pre");
            pre.TextContent = block.TextContent;
            block.Replace(pre);
            notes.Add("diagrams and formulas kept as their source text");
        }

        foreach (IElement figcaption in body.QuerySelectorAll("figcaption").ToList())
        {
            IElement p = doc.CreateElement("p");
            p.InnerHtml = figcaption.InnerHtml;
            figcaption.Replace(p);
        }

        foreach (string wrapper in new[] { "abbr", "span", "figure", "div" })
        {
            foreach (IElement element in body.QuerySelectorAll(wrapper).ToList())
            {
                Unwrap(element);
            }
        }
    }

    private static void Rename(IDocument doc, IElement body, string from, string to, List<string> notes, string note)
    {
        foreach (IElement element in body.QuerySelectorAll(from).ToList())
        {
            IElement replacement = doc.CreateElement(to);
            replacement.InnerHtml = element.InnerHtml;
            element.Replace(replacement);
            notes.Add(note);
        }
    }

    private static void Unwrap(IElement element)
    {
        INode parent = element.Parent!;

        foreach (INode child in element.ChildNodes.ToList())
        {
            parent.InsertBefore(child, element);
        }

        element.Remove();
    }

    /// <summary>
    /// What must survive: every non-space character of the text, in order, with each web or mail
    /// link's target after its text. Fragment-only links (#footnote) and footnote back-links are
    /// navigation and are left out.
    /// </summary>
    private static string Signature(IElement root)
    {
        StringBuilder sb = new();
        Walk(root, sb);
        return sb.ToString();
    }

    private static void Walk(INode node, StringBuilder sb)
    {
        if (node is IElement e && e.LocalName == "a" && e.ClassList.Contains("footnote-back-ref"))
        {
            return;
        }

        if (node is IText text)
        {
            foreach (char c in text.Data)
            {
                if (!char.IsWhiteSpace(c))
                {
                    sb.Append(c);
                }
            }

            return;
        }

        foreach (INode child in node.ChildNodes)
        {
            Walk(child, sb);
        }

        // What an element carries in attributes that a reader must still get: a media source, an
        // abbreviation's expansion.
        if (node is IElement carrier)
        {
            string? carried = carrier.LocalName switch
            {
                "iframe" or "video" or "audio" or "source" => carrier.GetAttribute("src"),
                "abbr" => carrier.GetAttribute("title"),
                _ => null,
            };

            if (carried is not null)
            {
                sb.Append(carried.Replace(" ", "", StringComparison.Ordinal));
            }
        }

        // A link's target counts only when the link has text a reader can see and click: an empty
        // anchor keeps its href in the markup but is lost to the reader.
        if (node is IElement link && link.LocalName == "a" && link.GetAttribute("href") is { } href
            && !string.IsNullOrWhiteSpace(link.TextContent)
            && (href.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                || href.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
                || href.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase)))
        {
            sb.Append(href.Replace(" ", "", StringComparison.Ordinal));
        }
    }

    /// <summary>Whether every character of <paramref name="expected"/> appears in <paramref name="actual"/>, in order.</summary>
    private static bool IsSubsequence(string expected, string actual)
    {
        int i = 0;

        foreach (char c in actual)
        {
            if (i < expected.Length && expected[i] == c)
            {
                i++;
            }
        }

        return i == expected.Length;
    }
}
