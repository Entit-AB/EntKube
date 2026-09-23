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
