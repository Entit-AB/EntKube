namespace EntKube.Web.Services.Mail;

/// <summary>Where a poll should start reading.</summary>
/// <param name="After">
/// The highest UID already taken in. Nothing at or below it is fetched again.
/// </param>
/// <param name="Reset">
/// Whether the stored position was thrown away because the mailbox was rebuilt underneath
/// us. Worth reporting rather than doing silently: it means the next poll re-reads
/// everything in the folder.
/// </param>
public readonly record struct MailboxResumePoint(uint After, bool Reset)
{
    /// <summary>
    /// The first UID to ask the server for.
    ///
    /// <para>Saturates rather than wrapping. A mailbox whose UIDs have reached the top of
    /// the range is not a situation anyone will meet, but <c>After + 1</c> on
    /// <see cref="uint.MaxValue"/> is zero, and zero is not a UID — IMAP numbers from one,
    /// so the search would either be rejected or silently mean "everything". Re-reading
    /// the last message is harmless, because ingestion is idempotent on the message id;
    /// re-reading the whole folder is not.</para>
    /// </summary>
    public uint FirstUid => After == uint.MaxValue ? uint.MaxValue : After + 1;
}

/// <summary>
/// How far a support mailbox has been read.
///
/// <para><b>Why progress is a UID and not the read flag.</b> A mailbox configured to touch
/// nothing still has to read each message once, and a shared mailbox a person also reads
/// would otherwise have messages marked seen out from under them — or, worse, would treat
/// a human's reading as ours and never take them in.</para>
///
/// <para><b>And why the UID alone is not enough.</b> IMAP UIDs are only unique within one
/// UIDVALIDITY. When a server reports a different one the mailbox has been rebuilt and
/// every number means something else, so a cursor carried across that boundary would skip
/// whatever now sits below it — silently, and for good. The answer is to throw the cursor
/// away and read the folder again; the message-id check is what stops that becoming a
/// second set of tickets.</para>
/// </summary>
public static class MailboxCursor
{
    /// <summary>
    /// Where to resume, given what was stored and what the server now says.
    /// </summary>
    /// <param name="lastSeenUid">The stored high-water mark, or null if never polled.</param>
    /// <param name="lastUidValidity">The validity that mark belongs to, or null.</param>
    /// <param name="folderUidValidity">What the server reports now.</param>
    public static MailboxResumePoint Resume(
        uint? lastSeenUid, uint? lastUidValidity, uint folderUidValidity)
    {
        // Never polled is not a reset: there was no position to lose.
        if (lastUidValidity is null)
        {
            return new MailboxResumePoint(0, Reset: false);
        }

        if (lastUidValidity != folderUidValidity)
        {
            return new MailboxResumePoint(0, Reset: true);
        }

        return new MailboxResumePoint(lastSeenUid ?? 0, Reset: false);
    }

    /// <summary>
    /// The high-water mark after a poll: the highest UID handled, or the one we started
    /// from when the poll found nothing.
    ///
    /// <para>Never goes backwards. A server that returns UIDs out of order, or a poll that
    /// overlapped another, must not rewind the cursor and re-read what has already been
    /// taken in.</para>
    /// </summary>
    public static uint Advance(uint after, IEnumerable<uint> handled)
    {
        uint highest = after;

        foreach (uint uid in handled)
        {
            if (uid > highest)
            {
                highest = uid;
            }
        }

        return highest;
    }
}
