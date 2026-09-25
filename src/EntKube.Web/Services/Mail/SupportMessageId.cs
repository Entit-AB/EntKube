using System.Globalization;
using System.Text.RegularExpressions;

namespace EntKube.Web.Services.Mail;

/// <summary>
/// The Message-Id we stamp on mail we send about a ticket, and reading a ticket number
/// back out of one.
///
/// <para><b>Why the number is in the identifier.</b> A reply quotes what it is replying to
/// in <c>In-Reply-To</c> and <c>References</c>, and those survive everything a subject
/// line does not: a client that rewrites "Re:", a person who edits the subject, a forward
/// that strips it. Putting the ticket number in the id we send means a reply can be placed
/// on its ticket with no lookup table and nothing stored — the thread carries the answer
/// back to us.</para>
///
/// <para><b>Why both halves live here.</b> The format was a string literal in the sender
/// and a comment claiming the reader understood it. The reader did not, so a reply with a
/// mangled subject opened a second ticket about the fault already being worked — the exact
/// case the comment said was covered. One place now writes the format and reads it, so
/// they cannot disagree again.</para>
/// </summary>
public static partial class SupportMessageId
{
    /// <summary>
    /// The identifier for a message we are sending about a ticket. Unique per message: the
    /// same ticket sends several, and two sharing an id would make a mess of any client
    /// that threads properly.
    /// </summary>
    public static string For(int ticketNumber) =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"ticket-{ticketNumber}.{Guid.NewGuid():N}@entkube");

    /// <summary>
    /// The ticket number in one of our identifiers, or null when it is not one of ours.
    ///
    /// <para>Only ours are read. A reply whose <c>In-Reply-To</c> points at a colleague's
    /// message must not be mined for a number that happens to look like one.</para>
    /// </summary>
    public static int? TicketNumberIn(string? messageId)
    {
        if (string.IsNullOrWhiteSpace(messageId))
        {
            return null;
        }

        Match match = Ours().Match(messageId.Trim().Trim('<', '>'));

        return match.Success
            && int.TryParse(
                match.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out int number)
            ? number
            : null;
    }

    /// <summary>
    /// Finds the first of our identifiers in a thread's chain, so a long conversation is
    /// still placed on its ticket when the immediate parent was somebody else's message.
    /// </summary>
    public static string? OursIn(IEnumerable<string?> messageIds) =>
        messageIds.FirstOrDefault(id => TicketNumberIn(id) is not null);

    [GeneratedRegex(@"^ticket-(\d{1,9})\.[0-9a-f]{32}@entkube$", RegexOptions.IgnoreCase)]
    private static partial Regex Ours();
}
