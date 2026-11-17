using EntKube.Web.Services.Mail;
using Ganss.Xss;

namespace EntKube.Web.Services.Tickets;

/// <summary>
/// The one place HTML written in EntKube is made safe (docs/helpdesk.md, decision 3).
///
/// <para><b>An allow-list, not a filter.</b> What survives is the formatting the composer can
/// produce — paragraphs, emphasis, lists, quotes, code, links — and nothing else: no images (files
/// go as attachments), no styles or classes (they can hide text or impersonate our own UI), no
/// scripts, frames, forms or event handlers. Links keep only <c>http</c>, <c>https</c> and
/// <c>mailto</c>, and open in a new tab without a referrer.</para>
///
/// <para>Applied when HTML is <b>written</b> (so what is stored and mailed is already safe) and
/// again when it is <b>rendered</b> (so a row written before a rule changed, or restored from a
/// backup, is held to today's rules).</para>
/// </summary>
public static class RichText
{
    private static readonly HtmlSanitizer Sanitizer = Build();

    private static HtmlSanitizer Build()
    {
        HtmlSanitizer s = new();

        s.AllowedTags.Clear();
        foreach (string tag in new[] { "p", "br", "strong", "b", "em", "i", "u", "s", "a", "ul", "ol", "li",
                                       "blockquote", "pre", "code", "h1", "h2", "h3" })
        {
            s.AllowedTags.Add(tag);
        }

        s.AllowedAttributes.Clear();
        s.AllowedAttributes.Add("href");

        s.AllowedSchemes.Clear();
        s.AllowedSchemes.Add("http");
        s.AllowedSchemes.Add("https");
        s.AllowedSchemes.Add("mailto");

        s.AllowedCssProperties.Clear();
        s.AllowedAtRules.Clear();
        s.AllowedClasses.Clear();
        s.AllowDataAttributes = false;

        // Every surviving link opens away from EntKube and tells the far end nothing about where
        // it was clicked.
        s.PostProcessNode += (_, e) =>
        {
            if (e.Node is AngleSharp.Html.Dom.IHtmlAnchorElement anchor)
            {
                anchor.SetAttribute("target", "_blank");
                anchor.SetAttribute("rel", "noopener noreferrer nofollow");
            }
        };

        return s;
    }

    /// <summary>The HTML with everything outside the allow-list removed. Null and blank give "".</summary>
    public static string Sanitize(string? html) =>
        string.IsNullOrWhiteSpace(html) ? "" : Sanitizer.Sanitize(html).Trim();

    /// <summary>
    /// The same content as text — the mail's plain part, the history's searchable text, and what
    /// the portal and digests show. Made from the sanitised HTML, so nothing removed from the one
    /// survives in the other.
    /// </summary>
    public static string ToText(string? html) =>
        MailMessageReader.PlainTextFrom(Sanitize(html)).Trim();

    /// <summary>Whether a piece of HTML says anything once sanitised.</summary>
    public static bool IsEmpty(string? html) => string.IsNullOrWhiteSpace(ToText(html));

    /// <summary>Plain text as HTML: escaped, with its line breaks kept.</summary>
    public static string FromText(string? text) =>
        string.Join("<br>", (text ?? "").ReplaceLineEndings("\n").Split('\n').Select(System.Net.WebUtility.HtmlEncode));
}
