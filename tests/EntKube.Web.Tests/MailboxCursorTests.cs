using EntKube.Web.Services.Mail;
using FluentAssertions;

namespace EntKube.Web.Tests;

/// <summary>
/// How far the support mailbox has been read.
///
/// <para>The protocol conversation around this is still unexercised — nothing here has
/// spoken to a real IMAP server. The decision it drives now is, and the decision is the
/// half that can be wrong quietly: a cursor carried across a rebuilt mailbox skips
/// whatever now sits below it, for good, with no error anywhere. Support mail simply
/// stops arriving from a customer nobody has heard from lately.</para>
/// </summary>
public class MailboxCursorTests
{
    // ---- Resuming ---------------------------------------------------------------------------

    /// <summary>A mailbox nobody has polled reads from the beginning.</summary>
    [Fact]
    public void A_mailbox_never_polled_starts_at_the_beginning()
    {
        MailboxResumePoint resume = MailboxCursor.Resume(null, null, folderUidValidity: 42);

        resume.After.Should().Be(0);
        resume.FirstUid.Should().Be(1, "IMAP numbers from one");
        resume.Reset.Should().BeFalse("there was no position to lose");
    }

    [Fact]
    public void A_mailbox_polled_before_resumes_after_what_it_read()
    {
        MailboxResumePoint resume = MailboxCursor.Resume(1200, 42, folderUidValidity: 42);

        resume.After.Should().Be(1200);
        resume.FirstUid.Should().Be(1201);
        resume.Reset.Should().BeFalse();
    }

    /// <summary>
    /// <b>The one that matters.</b> UIDs mean nothing across a UIDVALIDITY change. Carrying
    /// 1200 into a rebuilt mailbox would skip everything now numbered below it — silently,
    /// permanently, and looking exactly like a quiet week.
    /// </summary>
    [Fact]
    public void A_rebuilt_mailbox_is_read_again_from_the_beginning()
    {
        MailboxResumePoint resume = MailboxCursor.Resume(1200, 42, folderUidValidity: 43);

        resume.After.Should().Be(0);
        resume.FirstUid.Should().Be(1);
        resume.Reset.Should().BeTrue("the caller should say so rather than do it quietly");
    }

    /// <summary>
    /// A validity that goes backwards is as meaningless as one that goes forwards — the
    /// number is an identifier, not a version.
    /// </summary>
    [Fact]
    public void A_validity_that_moves_at_all_resets_the_cursor() =>
        MailboxCursor.Resume(1200, 42, folderUidValidity: 41).Reset.Should().BeTrue();

    /// <summary>
    /// Stored validity with no stored UID: the mailbox was opened and nothing was taken in.
    /// That is a real state and it reads from the beginning without claiming a reset.
    /// </summary>
    [Fact]
    public void A_validity_with_no_uid_reads_from_the_beginning_without_a_reset()
    {
        MailboxResumePoint resume = MailboxCursor.Resume(null, 42, folderUidValidity: 42);

        resume.After.Should().Be(0);
        resume.Reset.Should().BeFalse();
    }

    /// <summary>
    /// At the top of the range, <c>After + 1</c> is zero, and zero is not a UID — the
    /// search would be refused or, worse, quietly mean everything. Re-reading the last
    /// message is harmless because ingestion is idempotent; re-reading the folder is not.
    /// </summary>
    [Fact]
    public void The_first_uid_saturates_rather_than_wrapping_to_zero()
    {
        MailboxResumePoint resume = MailboxCursor.Resume(uint.MaxValue, 42, folderUidValidity: 42);

        resume.After.Should().Be(uint.MaxValue);
        resume.FirstUid.Should().Be(uint.MaxValue, "not 0, which would mean the whole folder");
    }

    // ---- Advancing ---------------------------------------------------------------------------

    [Fact]
    public void The_cursor_moves_to_the_highest_uid_taken_in() =>
        MailboxCursor.Advance(100, [101, 104, 102]).Should().Be(104);

    /// <summary>A poll that found nothing leaves the cursor where it was.</summary>
    [Fact]
    public void A_poll_that_found_nothing_leaves_the_cursor_alone() =>
        MailboxCursor.Advance(100, []).Should().Be(100);

    /// <summary>
    /// The cursor never goes backwards. Two polls overlapping, or a server answering out of
    /// order, must not rewind it into mail already taken in — which would be harmless once
    /// and a loop if it kept happening.
    /// </summary>
    [Fact]
    public void The_cursor_never_goes_backwards() =>
        MailboxCursor.Advance(100, [7, 12, 99]).Should().Be(100);

    /// <summary>Order is not assumed; the server may answer in any.</summary>
    [Fact]
    public void The_order_the_server_answers_in_does_not_matter() =>
        MailboxCursor.Advance(0, [500, 1, 250]).Should()
            .Be(MailboxCursor.Advance(0, [1, 250, 500]));
}
