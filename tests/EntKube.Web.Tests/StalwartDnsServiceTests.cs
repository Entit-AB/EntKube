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

    /// <summary>
    /// <b>A refusal is not an empty schema.</b> The server says no in an RFC 7807 problem
    /// document, which parses as JSON perfectly well — so "bytes came back" was reported as
    /// "reached the server and authenticated", which was a claim nothing had checked.
    /// </summary>
    [Theory]
    [InlineData("""{"type":"about:blank","status":401,"title":"Unauthorized"}""")]
    [InlineData("""{"status":403,"title":"Forbidden"}""")]
    public void A_refusal_is_recognised_as_one(string body) =>
        StalwartDnsService.Unauthorized(body).Should().BeTrue();

    [Theory]
    [InlineData("""{"objects":{"x:Domain":{}}}""")]
    [InlineData("""{"status":"ok"}""")]
    [InlineData("not json")]
    public void An_answer_is_not_a_refusal(string body) =>
        StalwartDnsService.Unauthorized(body).Should().BeFalse();

    /// <summary>
    /// When nothing in the schema holds DKIM keys, what the server does have is the useful thing
    /// to say — a schema is thousands of lines of forms and layouts around a short list of
    /// objects, and the list is what names the one to read instead.
    /// </summary>
    [Fact]
    public void The_objects_the_schema_lists_can_be_named()
    {
        StalwartDnsService.ObjectNamesIn("""{"objects":{"x:Domain":{},"x:Account":{}}}""")
            .Should().BeEquivalentTo("x:Domain", "x:Account");

        StalwartDnsService.ObjectNamesIn("""{"objects":[{"name":"x:Domain"},"x:Account"]}""")
            .Should().BeEquivalentTo("x:Domain", "x:Account");

        StalwartDnsService.ObjectNamesIn("""{"forms":{}}""").Should().BeEmpty();
    }

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

    // ---- Reading what the CLI prints --------------------------------------------------------

    /// <summary>
    /// <c>describe</c> prints a human-readable listing, not JSON, so the structured reader finds
    /// nothing in it. The rule is the same either way: the server names its own objects, and
    /// whichever says "dkim" is the one to snapshot. Guessing that name is what went wrong twice.
    /// </summary>
    /// <summary>
    /// <b>Verbatim from the server, and the reason the first rule was wrong.</b> Alphabetical
    /// order puts the failure-report singleton first, so "the first name containing dkim" picked
    /// a settings object with no key in it — and the snapshot that followed was of DKIM report
    /// settings. The name alone cannot tell them apart; what each one says about itself can.
    /// </summary>
    private const string Listing = """
        Directory               Defines an external directory for account authentication and lookups.
        DkimReportSettings      Configures DKIM authentication failure report generation. [singleton]
        DkimSignature           Defines a DKIM signature used to sign outgoing email messages.
        DmarcReportSettings     Configures DMARC aggregate and failure report generation. [singleton]
        Domain                  Defines an email domain and its DNS, DKIM, and TLS certificate settings.
        """;

    [Fact]
    public void The_signing_object_is_preferred_over_the_report_settings() =>
        StalwartDnsService.DkimObjectInText(Listing).Should().Be("DkimSignature");

    /// <summary>A server that words it differently still yields something rather than nothing.</summary>
    [Fact]
    public void A_listing_with_only_a_settings_object_still_names_it() =>
        StalwartDnsService.DkimObjectInText(
            "DkimReportSettings      Configures DKIM report generation. [singleton]")
            .Should().Be("DkimReportSettings");

    /// <summary>
    /// Snapshot takes bare object names — "Use bare object names (Domain, Account…)" — so the
    /// prefix and any view or variant suffix are dropped rather than passed to a command that
    /// rejects them.
    /// </summary>
    [Theory]
    [InlineData("x:DkimSignature    Defines a DKIM signature.", "DkimSignature")]
    [InlineData("DkimSignature/list    Defines a DKIM signature.", "DkimSignature")]
    public void The_name_handed_on_is_the_bare_one(string line, string expected) =>
        StalwartDnsService.DkimObjectInText(line).Should().Be(expected);

    [Theory]
    [InlineData("Account   Defines a user.\nDomain    Defines an email domain.")]
    [InlineData("")]
    public void A_listing_with_no_dkim_object_names_nothing(string described) =>
        StalwartDnsService.DkimObjectInText(described).Should().BeNull();

    /// <summary>
    /// The domain object is asked for alongside, because the server describes it as holding "its
    /// DNS, DKIM, and TLS certificate settings" — so if the published form of a key lives anywhere
    /// but the signature object, it is there. One snapshot takes both.
    /// </summary>
    [Fact]
    public void The_domain_object_is_read_alongside_the_keys() =>
        StalwartDnsService.AlsoSnapshot.Should().Contain("Domain");

    /// <summary>
    /// <b>Every type the server has, so none has to be discovered by failing.</b> The CLI refuses
    /// to export a plan with a dangling reference and names only the first one missing — so
    /// finding them one at a time costs an apply each, and this server lists 117.
    /// </summary>
    [Fact]
    public void Every_object_type_is_read_from_the_listing()
    {
        IReadOnlyList<string> names = StalwartDnsService.ObjectNamesInText(Listing);

        names.Should().BeEquivalentTo(
            "Directory", "DkimReportSettings", "DkimSignature", "DmarcReportSettings", "Domain");
    }

    /// <summary>Descriptions and blank lines are not names, and a name appears once.</summary>
    [Fact]
    public void Only_names_are_taken_from_the_listing() =>
        StalwartDnsService.ObjectNamesInText(
            "Account   Defines a user or group account.\n\nAccount   again\n   \n")
            .Should().BeEquivalentTo("Account");

    /// <summary>
    /// <b>The one type that must never be waved through.</b> The CLI refuses to export a plan
    /// with a dangling reference — "DkimSignature references Tenant but Tenant is not in the
    /// snapshot selection" — and allowing a type to go unresolved <em>drops that reference from
    /// the output</em>. Harmless for a tenant or a certificate; fatal for Domain, which is how a
    /// key is matched to the zone it has to be published in.
    /// </summary>
    [Fact]
    public void The_domain_reference_is_never_allowed_to_go_unresolved()
    {
        StalwartDnsService.SnapshotUnresolved.Should().Contain("Tenant");
        StalwartDnsService.SnapshotUnresolved.Should().NotContain("Domain");

        // And the two lists must not contradict each other.
        StalwartDnsService.AlsoSnapshot.Should()
            .NotIntersectWith(StalwartDnsService.SnapshotUnresolved);
    }

    // ---- What a real snapshot contains ------------------------------------------------------

    /// <summary>
    /// <b>Verbatim from the server, and the end of the matter.</b> A signature object carries the
    /// private half and nothing else — the CLI says so itself: "DkimSignature has secret field(s)
    /// the server returns anonymized (****); their values cannot be captured". There is no public
    /// key here to publish and no way to ask for one; it exists only as something derived from a
    /// key the server will not hand over.
    ///
    /// <para>What can be read is the selector, which is the half of the record nobody can guess —
    /// an arbitrary name the server chose, which changes when the key rotates, and against which
    /// a perfectly good public key fails every check if published wrongly.</para>
    /// </summary>
    private const string RealSnapshot = """
        {"@type":"upsert","object":"Domain","matchOn":["name"],"value":{"domain-b":{"name":"entit.eu","dnsManagement":{"@type":"Manual"},"isEnabled":true}}}
        {"@type":"upsert","object":"DkimSignature","matchOn":["selector"],"value":{"dkimsignature-jgyf3t9eaaqb":{"@type":"Dkim1RsaSha256","selector":"v1-rsa-20260927","privateKey":{"@type":"Text"},"domainId":"#domain-b"}}}
        {"@type":"upsert","object":"DkimSignature","matchOn":["selector"],"value":{"dkimsignature-jgyf3syiaaab":{"@type":"Dkim1Ed25519Sha256","selector":"v1-ed25519-20260927","privateKey":{"@type":"Text"},"domainId":"#domain-b"}}}
        """;

    [Fact]
    public void A_real_snapshot_yields_no_publishable_record() =>
        StalwartDnsService.DkimLinesInPlan(RealSnapshot, "entit.eu").Should().BeNull();

    /// <summary>
    /// The domain comes from a reference — <c>"domainId":"#domain-b"</c> — resolved against the
    /// Domain upsert in the same plan. That is exactly why Domain is in the selection and never
    /// waved through: allowing it would drop the reference and orphan every selector.
    /// </summary>
    [Fact]
    public void The_selectors_are_read_and_matched_to_their_domain() =>
        StalwartDnsService.DkimSelectorsInPlan(RealSnapshot, "entit.eu")
            .Should().BeEquivalentTo("v1-rsa-20260927", "v1-ed25519-20260927");

    /// <summary>Another domain's signatures are not this domain's.</summary>
    [Fact]
    public void Selectors_belonging_elsewhere_are_not_claimed() =>
        StalwartDnsService.DkimSelectorsInPlan(RealSnapshot, "other.example").Should().BeEmpty();

    /// <summary>
    /// <c>snapshot</c> writes a plan file: NDJSON, one operation per line rather than one
    /// document. Each line is read with the same lenient reader as any other answer.
    /// </summary>
    [Fact]
    public void Records_are_read_out_of_a_snapshot_plan()
    {
        string plan =
            """{"op":"upsert","type":"Domain","values":{"d0":{"name":"entit.eu"}}}""" + "\n"
            + """{"selector":"v1-rsa-20260927","domain":"entit.eu","publicKey":"v=DKIM1; k=rsa; p=MIIB"}""" + "\n"
            + """{"selector":"v1-ed25519-20260927","domain":"entit.eu","publicKey":"v=DKIM1; k=ed25519; p=11q"}""";

        StalwartDnsService.DkimLinesInPlan(plan, "entit.eu").Should().Be(
            "v1-rsa-20260927 v=DKIM1; k=rsa; p=MIIB\n"
            + "v1-ed25519-20260927 v=DKIM1; k=ed25519; p=11q");
    }

    /// <summary>Progress chatter and blank lines are not JSON and are stepped over.</summary>
    [Fact]
    public void Noise_around_the_plan_is_ignored() =>
        StalwartDnsService.DkimLinesInPlan(
            "Snapshotting DkimSignature…\n\n"
            + """{"selector":"s","domain":"entit.eu","publicKey":"v=DKIM1; p=A"}""" + "\ndone\n",
            "entit.eu")
            .Should().Be("s v=DKIM1; p=A");

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

        // The server reads the client address from this header and warns once per request that
        // arrives without one — noise that reads like part of the problem to whoever debugs next.
        command.Should().Contain("X-Forwarded-For: 127.0.0.1");

        // And the status, because without it every outcome looks alike: a refusal is JSON, an
        // answer is JSON, and a body that is neither cannot be told from a request that never
        // arrived. The server's log does not distinguish them either.
        command.Should().Contain(StalwartDnsService.StatusMarker);

        // Never the Service name: EntKube runs outside the cluster, so that resolves nowhere
        // here, and the API server's proxy would strip the Authorization header anyway.
        command.Should().NotContain("stalwart.stalwart.svc");
    }

    /// <summary>The status the server gave, split from the body it sent.</summary>
    [Fact]
    public void The_status_is_read_back_off_the_response()
    {
        (int? status, string body) = StalwartDnsService.SplitStatus(
            "{\"objects\":{}}\n" + StalwartDnsService.StatusMarker + "200");

        status.Should().Be(200);
        body.Should().Be("{\"objects\":{}}");
    }

    /// <summary>
    /// No marker means curl never got far enough to have a status — a different answer from any
    /// status, and the one that used to be invisible.
    /// </summary>
    [Fact]
    public void A_request_that_never_completed_has_no_status()
    {
        (int? status, string body) = StalwartDnsService.SplitStatus("(the request failed: …)");

        status.Should().BeNull();
        body.Should().Be("(the request failed: …)");
    }
}
