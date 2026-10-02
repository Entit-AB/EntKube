namespace EntKube.Web.Services.Mail;

/// <summary>
/// Finding the part of a technician's mail that is meant for the customer.
///
/// <para><b>Why there is a line to write above.</b> What we send a technician about a ticket
/// contains things the customer must not be handed: the response deadline §14.4 measures us
/// against, the priority described as unconfirmed, who reported it, and whatever the
/// previous correspondence said. A reply relayed verbatim sends all of it on. The usual
/// answer is to guess where the quoted part starts — strip lines beginning with
/// <c>&gt;</c>, look for "On … wrote:" — and that guess is wrong often enough to matter: a
/// client that top-posts without a marker, or writes "Den … skrev:", or quotes in HTML,
/// defeats it silently and in the direction that leaks.</para>
///
/// <para>So the mail carries an explicit line and we cut there. It either found the line or
/// it did not, which is a question with an answer rather than a heuristic with a success
/// rate — and when it did not, nothing is sent and a person is asked.</para>
///
/// <para>Pure, so what reaches a customer can be argued over in a test.</para>
/// </summary>
public static class SupportReplyBody
{
    /// <summary>
    /// The line itself, put in every mail we send one of our own people about a ticket.
    ///
    /// <para>Short on purpose. A client that re-wraps at 72 characters must not be able to
    /// break it in half, because half a sentinel is no sentinel and the reply would be held
    /// for no reason the sender can see.</para>
    /// </summary>
    public const string Sentinel = "Reply above this line to answer the customer";

    /// <summary>The sentinel as it is written into an outgoing mail, with its rule.</summary>
    public static string Block =>
        $"""
         {Sentinel}
         ───────────────────────────────────────────────
         """;

    /// <summary>
    /// What this mail is saying to the customer, or null when it is not saying anything.
    ///
    /// <para>Null means hold it: either the line is not there — so this is not a reply to
    /// something we sent, or a client that mangled it beyond recognition — or there is
    /// nothing above the line, which is what a reply looks like when somebody has hit send
    /// on an empty message or written underneath it by habit.</para>
    /// </summary>
    public static string? Above(string? body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return null;
        }

        string[] lines = body.ReplaceLineEndings("\n").Split('\n');
        int cut = Array.FindIndex(lines, IsSentinel);

        if (cut < 0)
        {
            return null;
        }

        string said = string.Join('\n', lines.Take(cut)).Trim();

        // A signature the sender's client appended above the quoted part. The delimiter is
        // the one piece of this that actually is standardised (RFC 3676 §4.3), so it is
        // worth honouring — a customer does not need somebody's job title and mobile.
        int signature = said.ReplaceLineEndings("\n").Split('\n').ToList()
            .FindIndex(line => line.TrimEnd() == "--");

        if (signature >= 0)
        {
            said = string.Join(
                '\n', said.ReplaceLineEndings("\n").Split('\n').Take(signature)).Trim();
        }

        return string.IsNullOrWhiteSpace(said) ? null : said;
    }

    /// <summary>
    /// Whether this line is the sentinel, as it comes back through a mail client.
    ///
    /// <para>Quoting marks and indentation are stripped before looking, because a reply is
    /// normally written above a quoted copy of what we sent and the line arrives wearing
    /// whatever that client's quote prefix is. Nothing else is assumed: the comparison is
    /// on the text we wrote.</para>
    /// </summary>
    private static bool IsSentinel(string line)
    {
        string bare = line.TrimStart('>', ' ', '\t', '|');

        return bare.Contains(Sentinel, StringComparison.OrdinalIgnoreCase);
    }
}
