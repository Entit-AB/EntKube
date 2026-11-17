using EntKube.Web.Services.Tickets;
using MimeKit;

namespace EntKube.Web.Services.Mail;

/// <summary>
/// The files attached to a mail, as the attachment store takes them. Decoded lazily, one at a
/// time, so a mail with several large attachments is not held in memory all at once.
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

        return files;
    }
}
