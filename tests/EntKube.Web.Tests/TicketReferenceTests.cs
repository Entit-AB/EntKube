using EntKube.Web.Services.Tickets;
using FluentAssertions;

namespace EntKube.Web.Tests;

/// <summary>
/// The reference a customer keeps in the subject line.
///
/// <para>Everything here is about one distinction: whether a subject proves it came from
/// something we sent, or merely contains a number. The old format could not tell the
/// difference — "Order #90210" read as a reference, and so did a mistyped digit — and that
/// difference is now what decides whether a reply is acted on without a person reading it.
/// So it is worth a test each way round.</para>
/// </summary>
public class TicketReferenceTests
{
    private static readonly TicketReference Ours = TestTicketReference.Instance;

    /// <summary>The round trip, which is most of the point.</summary>
    [Fact]
    public void A_reference_we_minted_is_read_back_and_proven()
    {
        string reference = Ours.For(1042);

        Ours.InSubject($"Re: {reference} Journalen svarar inte")
            .Should().Be(new SubjectReference(1042, Proven: true));
    }

    /// <summary>
    /// <b>The mistyped digit.</b> The failure the old format could not see: 413 is a
    /// perfectly good reference to somebody else's ticket, and nothing about it looked
    /// wrong. The number is still read — a person may well want to know what they meant —
    /// but it arrives unproven, so nothing happens to it unread.
    /// </summary>
    [Fact]
    public void A_changed_digit_is_still_read_but_is_not_proven()
    {
        string mistyped = Ours.For(412).Replace("EK-412-", "EK-413-");

        Ours.InSubject(mistyped).Should().Be(new SubjectReference(413, Proven: false));
    }

    /// <summary>
    /// <b>The stray number.</b> It has no token at all, so it cannot be proven — which is
    /// what stops it being acted on. It is still read as a number, because a bare number is
    /// also what every reference sent before this format existed looks like.
    /// </summary>
    [Fact]
    public void A_bare_number_is_read_but_never_proven()
    {
        Ours.InSubject("[#412] Journalen svarar inte")
            .Should().Be(new SubjectReference(412, Proven: false));

        Ours.InSubject("Order #90210 shipped")
            .Should().Be(new SubjectReference(90210, Proven: false));
    }

    /// <summary>
    /// A subject carrying both is about the ticket, not about whichever number comes first.
    /// This is the case the old reader got wrong: it took the first <c>#</c> it found and
    /// went looking for a ticket with that number.
    /// </summary>
    [Fact]
    public void The_reference_is_preferred_over_any_other_number_in_the_subject()
    {
        Ours.InSubject($"Order #90210 — re: {Ours.For(412)}")
            .Should().Be(new SubjectReference(412, Proven: true));
    }

    /// <summary>Nothing numeric that is not a reference at all.</summary>
    [Fact]
    public void A_subject_with_no_reference_in_it_names_no_ticket()
    {
        Ours.InSubject("Journalen svarar inte").Should().BeNull();
        Ours.InSubject("Order 90210 shipped").Should().BeNull();
        Ours.InSubject("").Should().BeNull();
        Ours.InSubject(null).Should().BeNull();
    }

    /// <summary>
    /// Retyped off a phone screen, in a client that lower-cases, inside a sentence and
    /// without the brackets. All of those are the same reference.
    /// </summary>
    [Fact]
    public void The_reference_survives_being_quoted_by_hand()
    {
        int number = 1042;
        string core = Ours.For(number).Trim('[', ']');

        Ours.ProvenIn($"about {core.ToLowerInvariant()}, still broken").Should().Be(number);
        Ours.ProvenIn(core).Should().Be(number);
    }

    /// <summary>
    /// <b>Why it is a capability and not a format.</b> The token is a MAC, so somebody who
    /// knows exactly how a reference is built still cannot produce one for a ticket they
    /// were never told about. That is what makes acting on a proven reference safe without
    /// also checking who sent it.
    /// </summary>
    [Fact]
    public void A_token_minted_with_a_different_key_is_not_proven()
    {
        TicketReference somebodyElse = new([.. Enumerable.Repeat((byte)7, 32)]);

        Ours.InSubject(somebodyElse.For(412))
            .Should().Be(new SubjectReference(412, Proven: false));
    }

    /// <summary>
    /// A reference is never read out of the middle of a longer identifier — a build tag, a
    /// part number, somebody else's ticket system.
    /// </summary>
    [Fact]
    public void A_reference_is_not_found_inside_a_longer_identifier()
    {
        Ours.InSubject("Build XEK-412-ABCDE failed").Should().BeNull();
        Ours.InSubject("Part EK-412-ABCDEF in stock").Should().BeNull();
    }

    /// <summary>
    /// The token has to actually depend on the number — a constant would read as a valid
    /// reference for every ticket, which is the one way this could be worthless while
    /// looking right. Not a proof of anything cryptographic: a 25-bit check has collisions,
    /// and at two thousand numbers a handful is expected, so near-all-distinct is what is
    /// asserted rather than all.
    /// </summary>
    [Fact]
    public void The_token_depends_on_the_number()
    {
        string[] tokens = [.. Enumerable.Range(1, 2000)
            .Select(n => Ours.For(n).TrimEnd(']').Split('-')[^1])];

        tokens.Distinct().Should().HaveCountGreaterThan(1990);
    }
}
