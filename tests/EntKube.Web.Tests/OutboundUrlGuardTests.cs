using System.Net;
using EntKube.Web.Services.Outbound;
using FluentAssertions;

namespace EntKube.Web.Tests;

/// <summary>
/// Tests for the outbound URL guard.
///
/// This is security code: the management plane can reach every managed cluster, its
/// own loopback, and the cloud metadata endpoint, while the URL is supplied by a
/// tenant user who is not trusted with that network position. Each case below is a
/// way that boundary has historically been crossed.
/// </summary>
public class OutboundUrlGuardTests
{
    /// <summary>
    /// True when public DNS is answering. Three cases below need a real lookup, and the
    /// thing under test is the guard's logic, not the network — a resolver hiccup in CI
    /// should not turn a green suite red. When DNS is unavailable those cases assert
    /// nothing rather than failing on something they never measured.
    /// </summary>
    private static bool PublicDnsWorks()
    {
        try
        {
            return System.Net.Dns.GetHostAddresses("example.com").Length > 0;
        }
        catch (System.Net.Sockets.SocketException)
        {
            return false;
        }
    }

    private static OutboundUrlVerdict Check(string? url) =>
        OutboundUrlGuard.Validate(url, allowPrivateTargets: false);

    // ── The destinations that matter most ──

    [Fact]
    public void The_cloud_metadata_endpoint_is_refused()
    {
        // 169.254.169.254 hands out instance credentials on every major cloud. It is the
        // single most valuable target for an SSRF, and the reason this guard exists.
        OutboundUrlVerdict verdict = Check("http://169.254.169.254/latest/meta-data/");

        verdict.IsAllowed.Should().BeFalse();
        verdict.Reason.Should().Contain("link-local");
    }

    [Theory]
    [InlineData("http://127.0.0.1:8080/hook")]
    [InlineData("http://localhost/hook")]
    [InlineData("http://[::1]/hook")]
    public void Loopback_is_refused_in_every_spelling(string url)
    {
        Check(url).IsAllowed.Should().BeFalse();
    }

    [Theory]
    [InlineData("http://10.0.0.5/hook")]
    [InlineData("http://172.16.4.1/hook")]
    [InlineData("http://172.31.255.254/hook")]
    [InlineData("http://192.168.1.1/hook")]
    [InlineData("http://100.64.0.1/hook")]
    public void Private_and_carrier_grade_nat_ranges_are_refused(string url)
    {
        Check(url).IsAllowed.Should().BeFalse();
    }

    [Theory]
    [InlineData("http://172.15.0.1/hook")]  // just below the private block
    [InlineData("http://172.32.0.1/hook")]  // just above it
    [InlineData("http://192.167.1.1/hook")] // adjacent to 192.168/16
    public void Addresses_adjacent_to_private_blocks_are_still_allowed(string url)
    {
        // The boundaries are the part of a range check most likely to be wrong.
        Check(url).IsAllowed.Should().BeTrue();
    }

    [Fact]
    public void An_ipv4_mapped_ipv6_address_cannot_smuggle_a_private_target_through()
    {
        // ::ffff:169.254.169.254 reaches exactly the same host as the bare IPv4 address.
        Check("http://[::ffff:169.254.169.254]/latest/meta-data/").IsAllowed.Should().BeFalse();
        Check("http://[::ffff:127.0.0.1]/hook").IsAllowed.Should().BeFalse();
        Check("http://[::ffff:10.0.0.1]/hook").IsAllowed.Should().BeFalse();
    }

    [Theory]
    [InlineData("http://[fe80::1]/hook")]     // link-local
    [InlineData("http://[fc00::1]/hook")]     // unique-local
    [InlineData("http://[fd12:3456::1]/hook")] // unique-local
    public void Non_routable_ipv6_ranges_are_refused(string url)
    {
        Check(url).IsAllowed.Should().BeFalse();
    }

    [Theory]
    [InlineData("http://0.0.0.0/hook")]
    [InlineData("http://224.0.0.1/hook")]
    [InlineData("http://255.255.255.255/hook")]
    public void Unspecified_multicast_and_broadcast_are_refused(string url)
    {
        Check(url).IsAllowed.Should().BeFalse();
    }

    // ── Scheme and shape ──

    [Theory]
    [InlineData("file:///etc/passwd")]
    [InlineData("ftp://example.com/x")]
    [InlineData("gopher://example.com/x")]
    public void Only_http_and_https_are_allowed(string url)
    {
        // A URL fetcher that honours file: is a local file reader.
        OutboundUrlVerdict verdict = Check(url);

        verdict.IsAllowed.Should().BeFalse();
        verdict.Reason.Should().Contain("http");
    }

    [Fact]
    public void Credentials_embedded_in_the_url_are_refused()
    {
        OutboundUrlVerdict verdict = Check("https://user:pass@example.com/hook");

        verdict.IsAllowed.Should().BeFalse();
        verdict.Reason.Should().Contain("Credentials");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not a url")]
    [InlineData("/relative/path")]
    public void Blank_or_unparseable_input_is_refused(string? url)
    {
        Check(url).IsAllowed.Should().BeFalse();
    }

    // ── What must still work ──

    [Theory]
    [InlineData("https://hooks.slack.com/services/T000/B000/xxx")]
    [InlineData("https://example.com:8443/webhook")]
    [InlineData("http://93.184.216.34/hook")]
    public void Ordinary_public_endpoints_are_allowed(string url)
    {
        // The literal-address case needs no resolver, so it is always checked.
        if (!url.Contains("93.184") && !PublicDnsWorks())
        {
            return;
        }

        Check(url).IsAllowed.Should().BeTrue();
    }

    [Fact]
    public void A_hostname_that_resolves_publicly_is_allowed_and_reports_its_addresses()
    {
        if (!PublicDnsWorks())
        {
            return;
        }

        OutboundUrlVerdict verdict = Check("https://example.com/hook");

        verdict.IsAllowed.Should().BeTrue();
        verdict.Resolved.Should().NotBeEmpty();
    }

    [Fact]
    public void A_hostname_resolving_to_loopback_is_refused()
    {
        // The realistic shape of the attack is a name, not a literal address.
        OutboundUrlVerdict verdict = Check("http://localhost:9000/hook");

        verdict.IsAllowed.Should().BeFalse();
        verdict.Reason.Should().Contain("localhost");
    }

    [Fact]
    public void An_unresolvable_hostname_is_refused_rather_than_attempted()
    {
        Check("https://this-name-does-not-exist.invalid/hook").IsAllowed.Should().BeFalse();
    }

    // ── The operator escape hatch ──

    [Fact]
    public void Private_targets_are_allowed_when_the_operator_opts_in()
    {
        // An instance-wide setting, deliberately not per-tenant: the whole point is that
        // the tenant is not the party who decides this.
        OutboundUrlGuard.Validate("http://10.0.0.5/hook", allowPrivateTargets: true)
            .IsAllowed.Should().BeTrue();
    }

    [Fact]
    public void The_opt_in_does_not_re_enable_non_http_schemes()
    {
        // Allowing an internal receiver is not the same as allowing file: reads.
        OutboundUrlGuard.Validate("file:///etc/passwd", allowPrivateTargets: true)
            .IsAllowed.Should().BeFalse();
    }

    [Fact]
    public void The_opt_in_does_not_re_enable_embedded_credentials()
    {
        OutboundUrlGuard.Validate("https://u:p@10.0.0.5/hook", allowPrivateTargets: true)
            .IsAllowed.Should().BeFalse();
    }

    // ── The routability predicate directly ──

    [Theory]
    [InlineData("8.8.8.8", true)]
    [InlineData("1.1.1.1", true)]
    [InlineData("2606:4700:4700::1111", true)]
    [InlineData("169.254.169.254", false)]
    [InlineData("127.0.0.1", false)]
    [InlineData("10.255.255.255", false)]
    [InlineData("::1", false)]
    public void Classifies_addresses_as_publicly_routable_or_not(string address, bool expected)
    {
        OutboundUrlGuard.IsPubliclyRoutable(IPAddress.Parse(address)).Should().Be(expected);
    }
}

/// <summary>
/// Tests for webhook payload signing — what lets a receiver tell a genuine EntKube
/// delivery from anyone else who learned the URL.
/// </summary>
public class WebhookSignerTests
{
    private const string Secret = "s3cr3t";
    private const string Body = """{"alertName":"disk-full","severity":"critical"}""";
    private const long Timestamp = 1_774_000_000;

    [Fact]
    public void Produces_a_prefixed_hex_signature()
    {
        string signature = WebhookSigner.Sign(Secret, Timestamp, Body);

        signature.Should().StartWith("sha256=");
        signature[7..].Should().HaveLength(64).And.MatchRegex("^[0-9a-f]+$");
    }

    [Fact]
    public void Signing_is_deterministic()
    {
        WebhookSigner.Sign(Secret, Timestamp, Body)
            .Should().Be(WebhookSigner.Sign(Secret, Timestamp, Body));
    }

    [Fact]
    public void A_correct_signature_verifies()
    {
        WebhookSigner.Verify(Secret, Timestamp, Body, WebhookSigner.Sign(Secret, Timestamp, Body))
            .Should().BeTrue();
    }

    [Fact]
    public void Altering_the_body_invalidates_the_signature()
    {
        // The whole point: a bearer token would still be valid on a tampered body.
        string signature = WebhookSigner.Sign(Secret, Timestamp, Body);

        WebhookSigner.Verify(Secret, Timestamp, """{"severity":"info"}""", signature)
            .Should().BeFalse();
    }

    [Fact]
    public void Replaying_with_a_different_timestamp_invalidates_the_signature()
    {
        // The timestamp is inside the signed material, so a captured delivery cannot be
        // replayed later with a fresh timestamp and a still-valid signature.
        string signature = WebhookSigner.Sign(Secret, Timestamp, Body);

        WebhookSigner.Verify(Secret, Timestamp + 1, Body, signature).Should().BeFalse();
    }

    [Fact]
    public void A_different_secret_produces_a_different_signature()
    {
        WebhookSigner.Sign("other", Timestamp, Body)
            .Should().NotBe(WebhookSigner.Sign(Secret, Timestamp, Body));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("sha256=deadbeef")]
    [InlineData("garbage")]
    public void A_missing_or_wrong_signature_does_not_verify(string? presented)
    {
        WebhookSigner.Verify(Secret, Timestamp, Body, presented).Should().BeFalse();
    }
}
