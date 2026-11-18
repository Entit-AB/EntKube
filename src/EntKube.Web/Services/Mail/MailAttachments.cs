using EntKube.Web.Services.Tickets;
using MimeKit;

namespace EntKube.Web.Services.Mail;

/// <summary>
/// The files attached to a mail, as the attachment store takes them. Decoded lazily, one at a
/// time, so a mail with several large attachments is not held in memory all at once.
///
/// <para><b>Inline parts too</b> (helpdesk phase 8). A screenshot pasted into an Outlook or Gmail
/// mail is an <em>inline</em> part, not an attachment, and MimeKit's <c>Attachments</c> leaves it
/// out — so it used to vanish: the HTML that would show it is not allowed images, and nothing kept
/// the file. Every inline part that is not the mail's own text is now kept as a file.</para>
/// </summary>
public static class MailAttachments
{
    public static IReadOnlyList<IncomingAttachment> From(MimeMessage message)
    {
        List<IncomingAttachment> files = [];

        foreach (MimeEntity entity in message.Attachments)
        {
            switch (entity)
            {
                case MimePart part:
                    files.Add(new IncomingAttachment(
                        part.FileName ?? "attachment",
                        part.ContentType.MimeType,
                        () =>
                        {
                            MemoryStream decoded = new();
                            part.Content?.DecodeTo(decoded);
                            decoded.Position = 0;
                            return decoded;
                        }));
                    break;

                // A forwarded mail attached whole: kept as the .eml it is.
                case MessagePart attached when attached.Message is not null:
                    files.Add(new IncomingAttachment(
                        (attached.Message.Subject is { Length: > 0 } subject ? subject : "attached message") + ".eml",
                        "message/rfc822",
                        () =>
                        {
                            MemoryStream raw = new();
                            attached.Message.WriteTo(raw);
                            raw.Position = 0;
                            return raw;
                        }));
                    break;
            }
        }

        HashSet<MimeEntity> taken = [.. message.Attachments];
        int unnamed = 0;

        foreach (MimePart part in message.BodyParts.OfType<MimePart>())
        {
            // The text and HTML bodies (and calendar text) are the mail itself, not files.
            if (taken.Contains(part) || part.ContentType.MediaType.Equals("text", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string name = part.FileName is { Length: > 0 } given
                ? given
                : $"inline-{(part.ContentType.MediaType.Equals("image", StringComparison.OrdinalIgnoreCase) ? "image" : "file")}-{++unnamed}.{Extension(part.ContentType.MediaSubtype)}";

            files.Add(new IncomingAttachment(name, part.ContentType.MimeType, () =>
            {
                MemoryStream decoded = new();
                part.Content?.DecodeTo(decoded);
                decoded.Position = 0;
                return decoded;
            }));
        }

        return files;
    }

    /// <summary>A file extension from a MIME subtype — letters and digits only, so nothing the sender chose reaches a path.</summary>
    private static string Extension(string? subtype)
    {
        string clean = new([.. (subtype ?? "").ToLowerInvariant().Where(char.IsAsciiLetterOrDigit)]);
        return clean switch { "" => "bin", "jpeg" => "jpg", "svgxml" => "svg", _ => clean.Length > 8 ? clean[..8] : clean };
    }
}
