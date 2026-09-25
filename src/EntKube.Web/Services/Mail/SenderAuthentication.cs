using System.Text.RegularExpressions;
using EntKube.Web.Data;
using MimeKit;

namespace EntKube.Web.Services.Mail;

/// <summary>
/// Reading the receiving server's verdict on a sender.
///
/// <para><b>Why this exists.</b> The §23 contact register is the strongest signal the
/// triage has — a named person's address places a message with no caveat at all. The
/// address it matches on is the From header, which is written by whoever sent the message.
/// A forged From therefore arrives as a customer's technical contact, and the queue says
/// so with no hint that anything was assumed.</para>
///
/// <para><b>And why it is careful about what it trusts.</b> <c>Authentication-Results</c>
/// is the obvious header to read, and it is also a header anybody can write: a sender is
/// free to include one saying their own mail passed everything. The only ones worth
/// anything are those stamped by a server we actually trust, identified by its
/// <c>authserv-id</c> — which the tenant has to tell us, because we cannot work it out.
/// Without that name this reports <see cref="SenderAuthenticity.Unknown"/> and claims
/// nothing, which is the honest answer and not a reassuring one.</para>
/// </summary>
public static partial class SenderAuthentication
{
    /// <summary>
    /// The receiving server's verdict on a message, as far as it can be trusted.
    /// </summary>
    /// <param name="message">The message as fetched.</param>
    /// <param name="trustedServer">
    /// The <c>authserv-id</c> of the server whose verdicts count — normally the hostname of
    /// the mail server the tenant's mailbox is on. Null or empty means no check.
    /// </param>
    public static SenderAuthenticity Of(MimeMessage message, string? trustedServer)
    {
        if (string.IsNullOrWhiteSpace(trustedServer))
        {
            return SenderAuthenticity.Unknown;
        }

        string trusted = trustedServer.Trim();
        SenderAuthenticity verdict = SenderAuthenticity.Unknown;

        foreach (Header header in message.Headers.Where(
            h => h.Field.Equals("Authentication-Results", StringComparison.OrdinalIgnoreCase)))
        {
            string value = header.Value.Trim();

            // The authserv-id is the first token, before the first semicolon. A header
            // stamped by anybody else — including one the sender wrote themselves — is
            // read and discarded.
            string server = value.Split(';', 2)[0].Trim().Split(' ', 2)[0].Trim();

            if (!server.Equals(trusted, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            SenderAuthenticity here = Interpret(value);

            // The strongest statement wins: a server that stamped two headers and failed
            // the sender in one of them has failed the sender.
            if (here == SenderAuthenticity.Failed)
            {
                return SenderAuthenticity.Failed;
            }

            if (here == SenderAuthenticity.Verified)
            {
                verdict = SenderAuthenticity.Verified;
            }
        }

        return verdict;
    }

    /// <summary>
    /// What one trusted header says.
    ///
    /// <para>DMARC is the verdict that matters, because it is the one that ties the result
    /// back to the domain in the From header — SPF and DKIM can both pass for a message
    /// whose From says something else entirely. Where DMARC is absent, a pass on either of
    /// the others is taken as verification and a failure on both as a failure; a mixture is
    /// left unknown, because it is genuinely ambiguous and guessing would be the whole
    /// mistake again.</para>
    /// </summary>
    private static SenderAuthenticity Interpret(string header)
    {
        if (Result("dmarc", header) is string dmarc)
        {
            return dmarc is "pass" ? SenderAuthenticity.Verified : SenderAuthenticity.Failed;
        }

        string? spf = Result("spf", header);
        string? dkim = Result("dkim", header);

        if (spf is "pass" || dkim is "pass")
        {
            return SenderAuthenticity.Verified;
        }

        return spf is "fail" && dkim is "fail"
            ? SenderAuthenticity.Failed
            : SenderAuthenticity.Unknown;
    }

    /// <summary>The verdict for one method, lower-cased, or null when it is not mentioned.</summary>
    private static string? Result(string method, string header)
    {
        Match match = Regex.Match(
            header,
            $@"\b{method}\s*=\s*([a-z]+)",
            RegexOptions.IgnoreCase,
            TimeSpan.FromMilliseconds(250));

        return match.Success ? match.Groups[1].Value.ToLowerInvariant() : null;
    }
}
