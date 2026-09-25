using EntKube.Web.Data;
using EntKube.Web.Services.Tickets.Bridge;
using FluentAssertions;

namespace EntKube.Web.Tests;

/// <summary>
/// Turning another system's priority into one of §14.2's.
///
/// <para>This is the one piece of the bridge with money directly attached: a P1 carries a
/// two-hour response, a call-out rate, and 10% of the window fee if it is missed. Getting
/// it from a table of guesses shipped in the product would be deciding the agreement on the
/// customer's behalf.</para>
/// </summary>
public class PriorityMapTests
{
    [Fact]
    public void The_configured_lines_are_read()
    {
        IReadOnlyDictionary<string, TicketPriority> map = PriorityMap.Parse(
            """
            Highest=P1
            High = P2
            Medium=3
            Low=p4
            """);

        PriorityMap.Proposed("Highest", map).Should().Be(TicketPriority.P1);
        PriorityMap.Proposed("High", map).Should().Be(TicketPriority.P2);
        PriorityMap.Proposed("Medium", map).Should().Be(TicketPriority.P3);
        PriorityMap.Proposed("Low", map).Should().Be(TicketPriority.P4);
    }

    /// <summary>
    /// <b>The one that matters.</b> A value nobody mapped proposes nothing, and §14.3's
    /// first assessment decides it. Falling back to P3 would look like the customer had
    /// proposed one — quietly settling a question nobody was asked, in writing, on a record
    /// §28 makes binding after thirty days.
    /// </summary>
    [Fact]
    public void An_unmapped_priority_proposes_nothing()
    {
        IReadOnlyDictionary<string, TicketPriority> map = PriorityMap.Parse("Highest=P1");

        PriorityMap.Proposed("Blocker", map).Should().BeNull();
        PriorityMap.Proposed("", map).Should().BeNull();
        PriorityMap.Proposed(null, map).Should().BeNull();
    }

    [Fact]
    public void Nothing_is_proposed_when_nothing_is_configured() =>
        PriorityMap.Proposed("Highest", PriorityMap.Parse(null)).Should().BeNull();

    /// <summary>ServiceNow sends 1–5 as numbers, and its 3 is not §14.2's P3.</summary>
    [Fact]
    public void A_numeric_priority_maps_like_any_other()
    {
        IReadOnlyDictionary<string, TicketPriority> map = PriorityMap.Parse(
            """
            1=P1
            2=P1
            3=P2
            4=P3
            5=P4
            """);

        PriorityMap.Proposed("2", map).Should().Be(TicketPriority.P1);
        PriorityMap.Proposed("3", map).Should().Be(TicketPriority.P2);
    }

    [Fact]
    public void The_sending_systems_value_is_matched_without_regard_to_case_or_padding() =>
        PriorityMap.Proposed("  highest  ", PriorityMap.Parse("Highest=P1"))
            .Should().Be(TicketPriority.P1);

    /// <summary>A mapping nobody can annotate is one nobody will keep correct.</summary>
    [Fact]
    public void Comments_and_blank_lines_are_ignored()
    {
        IReadOnlyDictionary<string, TicketPriority> map = PriorityMap.Parse(
            """
            # Their service desk uses ITIL numbering
            1=P1

            2=P2   # impact high, urgency high
            """);

        map.Should().HaveCount(2);
        PriorityMap.Proposed("2", map).Should().Be(TicketPriority.P2);
    }

    /// <summary>Nonsense in the box must not throw on a path a webhook reaches.</summary>
    [Theory]
    [InlineData("no equals sign here")]
    [InlineData("Highest=P9")]
    [InlineData("Highest=")]
    [InlineData("=P1")]
    [InlineData("Highest=urgent")]
    public void A_line_that_says_nothing_usable_is_skipped(string line) =>
        PriorityMap.Parse(line).Should().BeEmpty();

    /// <summary>A duplicated line resolves the same way every time.</summary>
    [Fact]
    public void The_first_of_two_lines_for_one_value_wins() =>
        PriorityMap.Proposed("High", PriorityMap.Parse("High=P1\nHigh=P4"))
            .Should().Be(TicketPriority.P1);
}

/// <summary>
/// The shared secret a customer's system presents.
///
/// <para>Reached by an unauthenticated request, so what it does with rubbish matters as
/// much as what it does with the right answer.</para>
/// </summary>
public class BridgeSecretTests
{
    [Fact]
    public void An_issued_secret_matches_what_was_stored()
    {
        string secret = BridgeSecret.Issue();

        BridgeSecret.Matches(secret, BridgeSecret.Store(secret)).Should().BeTrue();
    }

    [Fact]
    public void A_different_secret_does_not() =>
        BridgeSecret.Matches(BridgeSecret.Issue(), BridgeSecret.Store(BridgeSecret.Issue()))
            .Should().BeFalse();

    /// <summary>
    /// Two connections issued the same secret would still store different hashes — the salt
    /// is what stops one stolen row telling you about another.
    /// </summary>
    [Fact]
    public void The_same_secret_stores_differently_twice()
    {
        string secret = BridgeSecret.Issue();

        BridgeSecret.Store(secret).Should().NotBe(BridgeSecret.Store(secret));
    }

    [Fact]
    public void An_issued_secret_is_long_and_url_safe()
    {
        string secret = BridgeSecret.Issue();

        secret.Length.Should().BeGreaterThan(32);
        secret.Should().MatchRegex("^[A-Za-z0-9_-]+$");
    }

    /// <summary>
    /// Nothing presented, nothing stored, or a stored value from a scheme that no longer
    /// exists — all of it is "no", and none of it is an exception on a public endpoint.
    /// </summary>
    [Theory]
    [InlineData(null, "s256:AAAA:BBBB")]
    [InlineData("", "s256:AAAA:BBBB")]
    [InlineData("anything", null)]
    [InlineData("anything", "")]
    [InlineData("anything", "not-a-stored-secret")]
    [InlineData("anything", "s256:only-two-parts")]
    [InlineData("anything", "other:AAAA:BBBB")]
    [InlineData("anything", "s256:!!!not base64!!!:BBBB")]
    public void Anything_malformed_is_simply_not_a_match(string? presented, string? stored) =>
        BridgeSecret.Matches(presented, stored).Should().BeFalse();

    /// <summary>
    /// A connection with no secret set yet must not be open to everybody — which is what
    /// an empty stored value compared loosely would mean.
    /// </summary>
    [Fact]
    public void A_connection_with_no_secret_accepts_nothing() =>
        BridgeSecret.Matches("", null).Should().BeFalse();
}
