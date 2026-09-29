using EntKube.Web.Services.Mail;
using FluentAssertions;

namespace EntKube.Web.Tests;

/// <summary>
/// The ticket number we hide in the Message-Id of mail we send.
///
/// <para>It is there so a reply can be placed on its ticket with no lookup table and
/// nothing stored: the thread carries the answer back. What has to hold is that only our
/// own identifiers are read — a reply whose In-Reply-To points at a colleague's message
/// must not be mined for a number that happens to look like one.</para>
/// </summary>
public class SupportMessageIdTests
{
    [Fact]
    public void An_identifier_we_wrote_gives_its_ticket_number_back() =>
        SupportMessageId.TicketNumberIn(SupportMessageId.For(1042)).Should().Be(1042);

    // ---- The right-hand side ----------------------------------------------------------------

    /// <summary>
    /// <b>It has to be a domain that exists.</b> It used to be the bare word "entkube",
    /// which resolves to nothing and matches no From address — one of the oldest and
    /// cheapest signals a filter has that a message came from something which does not send
    /// much mail. Spent at exactly the moment a receipt is being judged by a mailbox that
    /// has never heard of us, which is every first receipt.
    /// </summary>
    [Fact]
    public void The_identifier_is_built_from_the_sending_domain() =>
        SupportMessageId.For(1042, "support@entit.se").Should().EndWith("@entit.se");

    /// <summary>The domain is taken as it is written, not as it was typed.</summary>
    [Fact]
    public void The_domain_is_lower_cased() =>
        SupportMessageId.For(7, "Support@ENTIT.se").Should().EndWith("@entit.se");

    /// <summary>
    /// Nothing usable falls back to the old word rather than inventing a domain. A receipt
    /// that threads is worth more than one that scores well.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-an-address")]
    [InlineData("trailing@")]
    [InlineData("no-dot@localhost")]
    public void Without_a_usable_domain_the_old_word_stands(string? from) =>
        SupportMessageId.For(7, from).Should().EndWith($"@{SupportMessageId.LegacyDomain}");

    /// <summary>
    /// <b>Every identifier already sent keeps the domain it went out with</b> — including
    /// the bare word, which is in the inbox of everyone who has ever reported a fault here.
    /// Their replies thread on it, so it is read for as long as those tickets live.
    /// </summary>
    [Fact]
    public void An_identifier_sent_under_the_old_word_still_threads() =>
        SupportMessageId.TicketNumberIn("ticket-1042.abcdef01234567890123456789abcdef@entkube")
            .Should().Be(1042);

    /// <summary>And one sent from an address the tenant has since changed away from.</summary>
    [Fact]
    public void An_identifier_sent_from_a_former_address_still_threads() =>
        SupportMessageId.TicketNumberIn("ticket-9.abcdef01234567890123456789abcdef@old.entit.se")
            .Should().Be(9);

    /// <summary>
    /// The same ticket sends several messages, and two sharing an identifier would confuse
    /// any client that threads properly.
    /// </summary>
    [Fact]
    public void Two_messages_about_one_ticket_get_different_identifiers() =>
        SupportMessageId.For(1042).Should().NotBe(SupportMessageId.For(1042));

    /// <summary>Mail clients quote identifiers in angle brackets; both forms are read.</summary>
    [Fact]
    public void Angle_brackets_are_tolerated() =>
        SupportMessageId.TicketNumberIn($"<{SupportMessageId.For(7)}>").Should().Be(7);

    /// <summary>
    /// <b>The one that matters.</b> Somebody else's identifier is not ours, however much
    /// it looks like it. Reading a number out of one would put a reply on a stranger's
    /// ticket.
    /// </summary>
    [Theory]
    [InlineData("CAB1234@mail.entit.example")]
    [InlineData("ticket-1042@some-other-helpdesk.example")]
    [InlineData("ticket-1042.notahexguid@entit.se")]
    [InlineData("ticket-1042.abcdef01234567890123456789abcdef@entit se")]
    [InlineData("ticket-1042.abcdef01234567890123456789abcdef")]
    [InlineData("ticket-1042.notahexguid@entkube")]
    [InlineData("ticket-.abcdef01234567890123456789abcdef@entkube")]
    [InlineData("prefix-ticket-1042.abcdef01234567890123456789abcdef@entkube")]
    [InlineData("")]
    [InlineData(null)]
    public void Somebody_elses_identifier_yields_nothing(string? messageId) =>
        SupportMessageId.TicketNumberIn(messageId).Should().BeNull();

    /// <summary>
    /// In a long conversation the immediate parent is often a colleague's message. Ours is
    /// further back in References, and finding it is the difference between placing the
    /// reply and opening a second ticket.
    /// </summary>
    [Fact]
    public void Ours_is_found_further_back_in_the_chain()
    {
        string ours = SupportMessageId.For(1042);

        SupportMessageId.OursIn(
            ["first@entit.example", ours, "reply@entit.example"]).Should().Be(ours);
    }

    [Fact]
    public void A_chain_with_none_of_ours_gives_nothing() =>
        SupportMessageId.OursIn(["a@entit.example", "b@entit.example"]).Should().BeNull();
}
