using EntKube.Web.Services.Mail;
using FluentAssertions;

namespace EntKube.Web.Tests;

/// <summary>
/// Reading DKIM records off the mail server.
///
/// <para><b>Why the reading is lenient and the naming is discovered.</b> This is the one shape
/// EntKube reads and does not control, and two earlier attempts at it were written from a reading
/// of the documentation and were wrong — there is no REST endpoint for DNS records, and the
/// server answers 404 to every GET it is not satisfied by, so a wrong path and a real one look
/// identical. What is left is to ask the server what it has and take what is recognisable.</para>
///
/// <para>Being strict would buy nothing and cost the thing that matters: on the day the payload
/// gains a field, a strict reader produces an empty list with no explanation and the operator is
/// back to copying values out of a second admin interface by hand — which is how a key went
/// unpublished for as long as it did.</para>
/// </summary>
public class StalwartDnsServiceTests
{
    // ---- Finding what holds the keys --------------------------------------------------------

    /// <summary>
    /// The object is found in the schema rather than named here, because nobody has read this
    /// schema yet. Whatever the server calls it, if it says "dkim" it is the one to ask about.
    /// </summary>
    [Theory]
    [InlineData("""{"objects":{"x:DkimSignature":{"singularName":"DKIM key"}}}""", "x:DkimSignature")]
    [InlineData("""{"objects":["x:Domain","x:DkimKey"]}""", "x:DkimKey")]
    [InlineData("""{"schemas":[{"name":"dkim-signature"}]}""", "dkim-signature")]
    public void The_object_holding_dkim_keys_is_found_in_the_schema(string schema, string expected) =>
        StalwartDnsService.DkimObjectIn(schema).Should().Be(expected);

    /// <summary>
    /// A schema with nothing of the sort returns null rather than a guess — and the caller then
    /// shows the operator what the server actually said, which is one step from a fix.
    /// </summary>
    [Theory]
    [InlineData("""{"objects":{"x:Domain":{},"x:Account":{}}}""")]
    [InlineData("not json")]
    [InlineData("")]
    public void A_schema_with_no_dkim_object_names_nothing(string schema) =>
        StalwartDnsService.DkimObjectIn(schema).Should().BeNull();

    // ---- Reading the records ----------------------------------------------------------------

    /// <summary>The selector and the public half, in the form the DNS list renders.</summary>
    [Fact]
    public void A_record_is_reduced_to_its_selector_and_value() =>
        StalwartDnsService.DkimLinesIn(
            """{"data":[{"selector":"v1-rsa-20260927","domain":"entit.eu","publicKey":"v=DKIM1; k=rsa; p=MIIB"}]}""",
            "entit.eu")
            .Should().Be("v1-rsa-20260927 v=DKIM1; k=rsa; p=MIIB");

    /// <summary>Wrapped under any of the usual names, or bare — all read the same.</summary>
    [Theory]
    [InlineData("""{"items":[{"selector":"s","value":"v=DKIM1; p=A"}]}""")]
    [InlineData("""{"records":[{"selector":"s","content":"v=DKIM1; p=A"}]}""")]
    [InlineData("""[{"selector":"s","record":"v=DKIM1; p=A"}]""")]
    [InlineData("""{"selector":"s","publicKey":"v=DKIM1; p=A"}""")]
    public void The_payload_may_be_wrapped_or_bare(string json) =>
        StalwartDnsService.DkimLinesIn(json, "entit.eu").Should().Be("s v=DKIM1; p=A");

    /// <summary>
    /// A record belonging to another domain is not this domain's. Two domains on one server would
    /// otherwise each publish the other's key.
    /// </summary>
    [Fact]
    public void A_record_for_another_domain_is_not_taken() =>
        StalwartDnsService.DkimLinesIn(
            """[{"selector":"s","domain":"other.example","publicKey":"v=DKIM1; p=A"}]""",
            "entit.eu")
            .Should().BeNull();

    /// <summary>A full record name keeps its own shape; the list rebuilds the rest.</summary>
    [Fact]
    public void A_full_record_name_is_reduced_to_the_selector() =>
        StalwartDnsService.DkimLinesIn(
            """[{"name":"sel._domainkey.entit.eu","content":"v=DKIM1; p=A"}]""", "entit.eu")
            .Should().Be("sel v=DKIM1; p=A");

    /// <summary>
    /// Nothing usable is null, not empty: the caller leaves whatever was recorded before standing
    /// rather than wiping a domain's records because one answer came back in an unfamiliar shape.
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData("not json at all")]
    [InlineData("{}")]
    [InlineData("""{"data":[]}""")]
    [InlineData("""{"data":[{"selector":"s"}]}""")]
    public void Nothing_usable_reads_as_nothing(string json) =>
        StalwartDnsService.DkimLinesIn(json, "entit.eu").Should().BeNull();

    // ---- The request itself -----------------------------------------------------------------

    /// <summary>
    /// <b>Loopback, and the token on stdin.</b> Over loopback there is no proxy to strip the
    /// Authorization header — which is why the API server's proxy cannot be used for this — and no
    /// PROXY greeting to send, which a pod-to-pod connection to this server would need. The token
    /// is read from stdin rather than passed as an argument, where anything able to read the
    /// process table in that container could see it.
    /// </summary>
    [Fact]
    public void The_request_is_made_over_loopback_with_the_token_on_stdin()
    {
        string command = StalwartDnsService.Query("/api/schema");

        command.Should().StartWith("read -r TOK;");
        command.Should().Contain("http://127.0.0.1:8080/api/schema");
        command.Should().Contain("Authorization: Bearer $TOK");
        command.Should().NotContain("stalwart.stalwart.svc");
    }
}
