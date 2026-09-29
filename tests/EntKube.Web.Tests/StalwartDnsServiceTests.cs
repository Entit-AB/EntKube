using EntKube.Web.Services.Mail;
using FluentAssertions;

namespace EntKube.Web.Tests;

/// <summary>
/// Reading the DKIM records out of what the mail server answers.
///
/// <para><b>Why this is parsed leniently.</b> It is the one shape EntKube reads and does not
/// control. Being strict about it buys nothing and costs the thing that matters: on the day the
/// server's response gains a field, a strict reader produces an empty DNS list with no
/// explanation, and the operator is back to copying values out of a second admin interface by
/// hand — which is how a key went unpublished for as long as it did.</para>
///
/// <para>So the payload may be wrapped or bare, the value may be called two things, and anything
/// that is not a DKIM record is left alone: MX, SPF and DMARC are already in the list from what
/// EntKube knows, and its SPF is the better answer because it knows the address the server
/// actually sends from.</para>
/// </summary>
public class StalwartDnsServiceTests
{
    private const string Wrapped = """
        {"data":[
          {"type":"MX","name":"entit.eu.","content":"10 mail.entit.eu."},
          {"type":"TXT","name":"entit.eu.","content":"v=spf1 mx -all"},
          {"type":"TXT","name":"v1-rsa-20260927._domainkey.entit.eu.","content":"v=DKIM1; k=rsa; p=MIIBIjANBg"},
          {"type":"TXT","name":"v1-ed25519-20260927._domainkey.entit.eu.","content":"v=DKIM1; k=ed25519; p=11qYAYKxCrf"},
          {"type":"TXT","name":"_dmarc.entit.eu.","content":"v=DMARC1; p=reject"}
        ]}
        """;

    /// <summary>
    /// The record that matters, reduced to the selector and the value — the form a person can
    /// read and correct, with the full name rebuilt from the domain when the list is rendered.
    /// </summary>
    [Fact]
    public void The_dkim_records_are_taken_and_the_rest_left_alone()
    {
        string? lines = StalwartDnsService.DkimLinesIn(Wrapped, "entit.eu");

        lines.Should().Be(
            "v1-rsa-20260927 v=DKIM1; k=rsa; p=MIIBIjANBg\n"
            + "v1-ed25519-20260927 v=DKIM1; k=ed25519; p=11qYAYKxCrf");
    }

    /// <summary>Wrapped in <c>data</c> or not — both are read.</summary>
    [Fact]
    public void A_bare_array_is_read_too() =>
        StalwartDnsService.DkimLinesIn(
            """[{"type":"TXT","name":"sel._domainkey.entit.eu","content":"v=DKIM1; p=A"}]""",
            "entit.eu")
            .Should().Be("sel v=DKIM1; p=A");

    /// <summary>And the value under either of the two names it goes by.</summary>
    [Fact]
    public void The_value_may_be_called_value() =>
        StalwartDnsService.DkimLinesIn(
            """[{"type":"TXT","name":"sel._domainkey.entit.eu","value":"v=DKIM1; p=A"}]""",
            "entit.eu")
            .Should().Be("sel v=DKIM1; p=A");

    /// <summary>
    /// A record with no type stated is still taken when it is plainly a DKIM one. Refusing it
    /// would mean an empty list because of a field nobody reads.
    /// </summary>
    [Fact]
    public void A_record_with_no_type_is_still_read() =>
        StalwartDnsService.DkimLinesIn(
            """[{"name":"sel._domainkey.entit.eu","content":"v=DKIM1; p=A"}]""", "entit.eu")
            .Should().Be("sel v=DKIM1; p=A");

    /// <summary>Quoted values come back unquoted — the zone file's quoting is not the value.</summary>
    [Fact]
    public void Surrounding_quotes_are_dropped() =>
        StalwartDnsService.DkimLinesIn(
            """[{"type":"TXT","name":"sel._domainkey.entit.eu","content":"\"v=DKIM1; p=A\""}]""",
            "entit.eu")
            .Should().Be("sel v=DKIM1; p=A");

    /// <summary>
    /// Nothing usable is null, not an empty string: the caller leaves whatever was fetched
    /// before standing rather than wiping a domain's records because one request came back odd.
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData("not json at all")]
    [InlineData("{}")]
    [InlineData("""{"data":[]}""")]
    [InlineData("""{"data":[{"type":"MX","name":"entit.eu.","content":"10 mail.entit.eu."}]}""")]
    [InlineData("""{"error":"unauthorized"}""")]
    public void Nothing_usable_reads_as_nothing(string json) =>
        StalwartDnsService.DkimLinesIn(json, "entit.eu").Should().BeNull();
}
