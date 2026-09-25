using EntKube.Web.Data;
using EntKube.Web.Services.Mail;
using FluentAssertions;
using MimeKit;

namespace EntKube.Web.Tests;

/// <summary>
/// Reading the receiving server's verdict on who sent a message.
///
/// <para>The §23 contact register places a message with no caveat at all, on the strength
/// of a From header anybody can write. This is the only thing that can say whether that
/// header was checked — and it is itself reading a header, which is why what it refuses to
/// trust matters more than what it accepts.</para>
/// </summary>
public class SenderAuthenticationTests
{
    private const string OurServer = "mx.entit.se";

    private static MimeMessage WithHeaders(params string[] authenticationResults)
    {
        MimeMessage message = new();
        message.From.Add(new MailboxAddress("Anna", "anna@entit.example"));
        message.Subject = "Fel";
        message.Body = new TextPart("plain") { Text = "Beskrivning." };

        foreach (string value in authenticationResults)
        {
            message.Headers.Add("Authentication-Results", value);
        }

        return message;
    }

    // ---- What it refuses to trust ---------------------------------------------------------

    /// <summary>
    /// <b>The one that matters.</b> Authentication-Results is a header like any other and a
    /// sender may write their own saying everything passed. Only a verdict stamped by the
    /// server we named counts.
    /// </summary>
    [Fact]
    public void A_verdict_from_somebody_elses_server_is_ignored() =>
        SenderAuthentication.Of(
            WithHeaders("mx.attacker.example; dmarc=pass; spf=pass; dkim=pass"), OurServer)
            .Should().Be(SenderAuthenticity.Unknown);

    /// <summary>
    /// With no trusted server named there is nothing to check against, and the answer is
    /// that we do not know — which is deliberately not the same as a pass.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Without_a_named_server_nothing_is_claimed(string? trusted) =>
        SenderAuthentication.Of(
            WithHeaders($"mx.entit.se; dmarc=pass"), trusted)
            .Should().Be(SenderAuthenticity.Unknown);

    [Fact]
    public void A_message_with_no_verdict_at_all_is_unknown() =>
        SenderAuthentication.Of(WithHeaders(), OurServer)
            .Should().Be(SenderAuthenticity.Unknown);

    /// <summary>
    /// A forged header alongside a real one must not dilute the real one. The trusted
    /// server's failure stands whatever else is in the message.
    /// </summary>
    [Fact]
    public void A_forged_pass_does_not_overturn_a_trusted_failure() =>
        SenderAuthentication.Of(
            WithHeaders(
                "mx.attacker.example; dmarc=pass",
                "mx.entit.se; dmarc=fail"),
            OurServer)
            .Should().Be(SenderAuthenticity.Failed);

    // ---- What it concludes ------------------------------------------------------------------

    [Fact]
    public void Our_own_servers_pass_verifies_the_sender() =>
        SenderAuthentication.Of(
            WithHeaders("mx.entit.se; spf=pass smtp.mailfrom=entit.example; dmarc=pass"), OurServer)
            .Should().Be(SenderAuthenticity.Verified);

    [Fact]
    public void Our_own_servers_failure_fails_the_sender() =>
        SenderAuthentication.Of(
            WithHeaders("mx.entit.se; spf=fail smtp.mailfrom=entit.example; dmarc=fail"), OurServer)
            .Should().Be(SenderAuthenticity.Failed);

    /// <summary>
    /// DMARC is the verdict that ties the result back to the domain in From. SPF and DKIM
    /// can both pass for a message whose From says something else entirely, so where DMARC
    /// has spoken it is what counts.
    /// </summary>
    [Fact]
    public void Dmarc_beats_a_passing_spf()
    {
        SenderAuthentication.Of(
            WithHeaders("mx.entit.se; spf=pass; dkim=pass; dmarc=fail"), OurServer)
            .Should().Be(SenderAuthenticity.Failed);
    }

    /// <summary>Without DMARC, either of the others passing is taken as verification.</summary>
    [Theory]
    [InlineData("mx.entit.se; spf=pass; dkim=none")]
    [InlineData("mx.entit.se; dkim=pass; spf=none")]
    public void Without_dmarc_a_pass_on_either_verifies(string header) =>
        SenderAuthentication.Of(WithHeaders(header), OurServer)
            .Should().Be(SenderAuthenticity.Verified);

    /// <summary>
    /// A mixture with nothing passing and nothing clearly failing is genuinely ambiguous,
    /// and guessing at it would be the whole mistake over again.
    /// </summary>
    [Fact]
    public void A_mixture_that_settles_nothing_stays_unknown() =>
        SenderAuthentication.Of(
            WithHeaders("mx.entit.se; spf=softfail; dkim=none"), OurServer)
            .Should().Be(SenderAuthenticity.Unknown);

    [Fact]
    public void The_server_name_is_compared_without_regard_to_case() =>
        SenderAuthentication.Of(WithHeaders("MX.ENTIT.SE; dmarc=pass"), "mx.entit.se")
            .Should().Be(SenderAuthenticity.Verified);
}
