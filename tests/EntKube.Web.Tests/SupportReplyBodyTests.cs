using EntKube.Web.Services.Mail;
using FluentAssertions;

namespace EntKube.Web.Tests;

/// <summary>
/// What of a technician's mail reaches the customer.
///
/// <para>The one test file here where being wrong sends something. What we write to a
/// technician about a ticket carries the §14.4 deadline, the priority described as
/// unconfirmed, and the name of the person who reported it; a reply that relays the quoted
/// part sends all three to the customer. So the cases below are mostly about what is
/// <em>not</em> returned.</para>
/// </summary>
public class SupportReplyBodyTests
{
    /// <summary>What the mail we send looks like, as a client quotes it back.</summary>
    private static string Quoted(string wrote, string prefix = "> ") =>
        $"""
         {wrote}

         {prefix}{SupportReplyBody.Sentinel}
         {prefix}───────────────────────────────────────────────
         {prefix}A new ticket is open. Replying to this message answers the customer directly.
         {prefix}
         {prefix}  Reference:   [EK-412-K7QX9]
         {prefix}  Priority:    P1 (as reported — confirm it in writing, §14.3)
         {prefix}  Respond by:  Tue 22 Sep 14:00
         {prefix}  Reported by: Karin Karlsson
         """;

    [Fact]
    public void What_was_written_above_the_line_is_what_goes()
    {
        SupportReplyBody.Above(Quoted("Vi har hittat felet och rullar ut en rättning i kväll."))
            .Should().Be("Vi har hittat felet och rullar ut en rättning i kväll.");
    }

    /// <summary>
    /// The whole point, stated as the thing that must never happen. Each of these is in the
    /// mail the technician is replying to, and none of it is ours to hand over.
    /// </summary>
    [Fact]
    public void Nothing_below_the_line_is_ever_returned()
    {
        string? said = SupportReplyBody.Above(Quoted("Vi tittar på det."));

        said.Should().NotBeNull();
        said.Should().NotContain("Respond by");
        said.Should().NotContain("Reported by");
        said.Should().NotContain("as reported");
        said.Should().NotContain("EK-412-K7QX9");
        said.Should().NotContain(SupportReplyBody.Sentinel);
    }

    /// <summary>
    /// Clients quote differently, and some do not quote at all — they indent, or mark with
    /// a bar, or leave the line bare. The text we wrote is what is looked for, so all of
    /// them work.
    /// </summary>
    [Theory]
    [InlineData("> ")]
    [InlineData(">")]
    [InlineData(">> ")]
    [InlineData("    ")]
    [InlineData("| ")]
    [InlineData("")]
    public void However_the_client_quotes_it(string prefix)
    {
        SupportReplyBody.Above(Quoted("Löst.", prefix)).Should().Be("Löst.");
    }

    /// <summary>
    /// <b>No line, nothing sent.</b> This is not a reply to a mail of ours — or it is one a
    /// client mangled past recognising. Either way guessing would be guessing in the
    /// direction that leaks, so the answer is null and a person is asked.
    /// </summary>
    [Fact]
    public void A_mail_without_the_line_says_nothing()
    {
        SupportReplyBody.Above(
                """
                Vi tittar på det.

                On Tue, 22 Sep 2026, Support wrote:
                >   Respond by:  Tue 22 Sep 14:00
                """)
            .Should().BeNull();
    }

    /// <summary>
    /// Written underneath the line by habit, or sent empty. There is nothing above it, so
    /// there is nothing to send — and what is below is ours, not theirs.
    /// </summary>
    [Fact]
    public void Nothing_above_the_line_is_nothing_to_send()
    {
        SupportReplyBody.Above(Quoted("")).Should().BeNull();
        SupportReplyBody.Above(Quoted("   \n  \n")).Should().BeNull();
    }

    /// <summary>
    /// A signature the client appended. RFC 3676's delimiter is the one part of this that
    /// is actually standardised, and a customer does not need somebody's mobile number.
    /// </summary>
    [Fact]
    public void A_signature_is_left_off()
    {
        string? said = SupportReplyBody.Above(
            Quoted("Rättningen är ute.\n\n-- \nNils Blomgren\nENTIT AB\n070-000 00 00"));

        said.Should().Be("Rättningen är ute.");
    }

    /// <summary>
    /// Several paragraphs, and the blank lines between them kept. A reply reflowed into one
    /// run-on paragraph reads as machine-written, which is the opposite of the point.
    /// </summary>
    [Fact]
    public void The_shape_of_what_they_wrote_is_kept()
    {
        SupportReplyBody.Above(Quoted("Hej Karin,\n\nVi har hittat felet.\n\nHälsningar"))
            .Should().Be("Hej Karin,\n\nVi har hittat felet.\n\nHälsningar");
    }

    [Fact]
    public void An_empty_mail_says_nothing()
    {
        SupportReplyBody.Above(null).Should().BeNull();
        SupportReplyBody.Above("").Should().BeNull();
        SupportReplyBody.Above("   ").Should().BeNull();
    }

    /// <summary>
    /// The sentinel has to be short enough that a client wrapping at 72 characters cannot
    /// break it in two — half a sentinel is no sentinel, and the reply would then be held
    /// for a reason nobody can see.
    /// </summary>
    [Fact]
    public void The_line_is_short_enough_to_survive_being_wrapped()
    {
        // Room for a quote prefix and some indentation on top.
        SupportReplyBody.Sentinel.Length.Should().BeLessThan(60);
        SupportReplyBody.Sentinel.Should().NotContain("\n");
    }
}
