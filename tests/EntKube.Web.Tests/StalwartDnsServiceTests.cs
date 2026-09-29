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

    // ---- Announcing the connection ----------------------------------------------------------

    /// <summary>
    /// <b>Verified against the running server, not reasoned about.</b> Where the trusted-networks
    /// list reaches inside the cluster, Stalwart closes a plain connection before a byte of HTTP is
    /// read — it looks like a network fault — and answers the identical request that opens with a
    /// PROXY v1 greeting. The line has to be the one every client that speaks the protocol sends.
    /// </summary>
    [Fact]
    public void The_proxy_greeting_is_the_v1_line_haproxy_defines() =>
        StalwartDnsService.ProxyV1Line(
            new System.Net.IPEndPoint(System.Net.IPAddress.Parse("100.96.5.249"), 44321),
            new System.Net.IPEndPoint(System.Net.IPAddress.Parse("100.64.76.161"), 8080))
            .Should().Be("PROXY TCP4 100.96.5.249 100.64.76.161 44321 8080\r\n");

    /// <summary>The family is part of the line, and a v6 socket must say TCP6.</summary>
    [Fact]
    public void A_v6_connection_announces_itself_as_tcp6() =>
        StalwartDnsService.ProxyV1Line(
            new System.Net.IPEndPoint(System.Net.IPAddress.Parse("fd00::5"), 1234),
            new System.Net.IPEndPoint(System.Net.IPAddress.Parse("fd00::1"), 8080))
            .Should().StartWith("PROXY TCP6 fd00::5 fd00::1 1234 8080");

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
