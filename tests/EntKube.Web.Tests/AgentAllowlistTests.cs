using EntKube.Agent;
using EntKube.Agents.Protocol;
using EntKube.Web.Services.Agents;
using FluentAssertions;

namespace EntKube.Web.Tests;

/// <summary>
/// Tests for the agent's allowlist and wire format.
///
/// The allowlist is the reason an agent is safe to run inside a customer network:
/// it decides what EntKube can reach through it, and EntKube cannot widen it. A
/// matching rule that is too permissive is the one bug here that would actually
/// matter, so the near-miss cases get most of the attention.
/// </summary>
public class AgentAllowlistTests
{
    private static AgentOptions Options(params string[] hosts) => new()
    {
        ServerUrl = "https://entkube.example.com",
        Token = "token",
        AllowedHosts = [.. hosts],
        AllowedPorts = [443]
    };

    // ──────── Exact hosts ────────

    [Fact]
    public void An_exact_host_is_allowed()
        => Options("identity.example.com").IsAllowed("identity.example.com", 443).Should().BeTrue();

    [Fact]
    public void Host_matching_ignores_case()
        => Options("identity.example.com").IsAllowed("IDENTITY.Example.COM", 443).Should().BeTrue();

    [Fact]
    public void An_unlisted_host_is_refused()
        => Options("identity.example.com").IsAllowed("nova.example.com", 443).Should().BeFalse();

    [Fact]
    public void An_empty_allowlist_permits_nothing()
        => Options().IsAllowed("identity.example.com", 443).Should().BeFalse();

    // ──────── Wildcards ────────

    [Fact]
    public void A_wildcard_allows_subdomains()
        => Options("*.citycloud.com").IsAllowed("s3-kna1.citycloud.com", 443).Should().BeTrue();

    [Fact]
    public void A_wildcard_does_not_allow_the_bare_domain()
        // "*.example.com" is a rule about subdomains; matching the apex too would
        // quietly grant more than the operator wrote.
        => Options("*.example.com").IsAllowed("example.com", 443).Should().BeFalse();

    [Fact]
    public void A_wildcard_does_not_match_a_lookalike_suffix()
        // The classic hole: naive suffix matching lets "evilexample.com" through a
        // rule intended for "example.com".
        => Options("*.example.com").IsAllowed("evilexample.com", 443).Should().BeFalse();

    [Fact]
    public void A_wildcard_does_not_match_a_domain_that_merely_contains_it()
        => Options("*.example.com").IsAllowed("example.com.attacker.net", 443).Should().BeFalse();

    // ──────── Ports ────────

    [Fact]
    public void An_allowed_host_on_an_unlisted_port_is_refused()
        // Allowing a host must not open every service running on it.
        => Options("identity.example.com").IsAllowed("identity.example.com", 22).Should().BeFalse();

    [Fact]
    public void Additional_ports_can_be_permitted_explicitly()
    {
        AgentOptions options = Options("identity.example.com");
        options.AllowedPorts = [443, 5000];

        options.IsAllowed("identity.example.com", 5000).Should().BeTrue();
        options.IsAllowed("identity.example.com", 8080).Should().BeFalse();
    }

    // ──────── Startup validation ────────

    [Fact]
    public void An_agent_without_an_allowlist_refuses_to_start()
    {
        // Starting with no allowlist would be a process that connects and can do
        // nothing; failing loudly with an explanation beats that.
        AgentOptions options = Options();

        options.Validate().Should().ContainSingle(p => p.Contains("AllowedHosts"));
    }

    [Fact]
    public void Missing_server_url_and_token_are_both_reported()
    {
        AgentOptions options = new() { AllowedHosts = ["identity.example.com"] };

        List<string> problems = options.Validate();

        problems.Should().Contain(p => p.Contains("ServerUrl"));
        problems.Should().Contain(p => p.Contains("Token"));
    }

    [Fact]
    public void A_valid_configuration_reports_no_problems()
        => Options("identity.example.com").Validate().Should().BeEmpty();

    [Fact]
    public void A_non_http_server_url_is_rejected()
    {
        AgentOptions options = Options("identity.example.com");
        options.ServerUrl = "ftp://entkube.example.com";

        options.Validate().Should().Contain(p => p.Contains("absolute http or https"));
    }

    // ──────── Endpoint derivation ────────

    [Theory]
    [InlineData("https://entkube.example.com", "wss://entkube.example.com/agent/connect")]
    [InlineData("https://entkube.example.com/", "wss://entkube.example.com/agent/connect")]
    [InlineData("http://localhost:5000", "ws://localhost:5000/agent/connect")]
    public void The_websocket_endpoint_is_derived_from_the_pasted_server_url(string serverUrl, string expected)
        // Operators paste the URL they use in a browser; mapping the scheme here
        // means they do not have to know about ws/wss at all.
        => AgentClient.BuildEndpoint(serverUrl).ToString().Should().Be(expected);

    // ──────── Wire format ────────

    [Fact]
    public void A_frame_round_trips_intact()
    {
        byte[] payload = [1, 2, 3, 4, 5];
        AgentFrame frame = AgentProtocol.Decode(AgentProtocol.Encode(AgentFrameType.Data, 42, payload));

        frame.Type.Should().Be(AgentFrameType.Data);
        frame.StreamId.Should().Be(42u);
        frame.Payload.ToArray().Should().BeEquivalentTo(payload);
    }

    [Fact]
    public void A_truncated_frame_is_rejected_rather_than_guessed_at()
    {
        // Silently accepting a short frame would mis-route bytes between streams,
        // which corrupts data instead of failing.
        Action act = () => AgentProtocol.Decode(new byte[] { 0x03, 0x00 });

        act.Should().Throw<InvalidDataException>();
    }

    [Fact]
    public void An_unknown_frame_type_is_rejected()
    {
        Action act = () => AgentProtocol.Decode(new byte[] { 0xff, 0, 0, 0, 1 });

        act.Should().Throw<InvalidDataException>().WithMessage("*frame type*");
    }

    [Fact]
    public void An_oversized_payload_is_refused_at_encode_time()
    {
        Action act = () => AgentProtocol.Encode(
            AgentFrameType.Data, 1, new byte[AgentProtocol.MaxPayloadSize + 1]);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Theory]
    [InlineData("identity.example.com:443", "identity.example.com", 443)]
    [InlineData("[::1]:5000", "[::1]", 5000)]
    public void A_target_round_trips(string target, string expectedHost, int expectedPort)
    {
        // Splitting on the last colon is what keeps IPv6 literals intact.
        (string host, int port) = AgentProtocol.ParseTarget(target);

        host.Should().Be(expectedHost);
        port.Should().Be(expectedPort);
    }

    [Theory]
    [InlineData("identity.example.com")]
    [InlineData("identity.example.com:0")]
    [InlineData("identity.example.com:99999")]
    [InlineData(":443")]
    public void A_malformed_target_is_rejected(string target)
    {
        Action act = () => AgentProtocol.ParseTarget(target);

        act.Should().Throw<InvalidDataException>();
    }

    [Fact]
    public void An_open_ack_carries_its_refusal_reason()
    {
        // The reason reaches the operator in EntKube, so a refused host reads as a
        // configuration answer rather than a timeout.
        AgentFrame frame = AgentProtocol.Decode(
            AgentProtocol.EncodeOpenAck(7, false, "host is not in this agent's allowlist"));

        (bool success, string? error) = AgentProtocol.DecodeOpenAck(frame.Payload);

        success.Should().BeFalse();
        error.Should().Contain("allowlist");
    }

    // ──────── Enrolment tokens ────────

    [Fact]
    public void Generated_tokens_are_unique_and_url_safe()
    {
        List<string> tokens = [.. Enumerable.Range(0, 100).Select(_ => AgentRegistry.GenerateToken())];

        tokens.Should().OnlyHaveUniqueItems();
        tokens.Should().AllSatisfy(t => t.Should().MatchRegex("^[A-Za-z0-9_-]+$"));
    }

    [Fact]
    public void Token_hashing_is_stable_and_does_not_reveal_the_token()
    {
        string token = AgentRegistry.GenerateToken();
        string hash = AgentRegistry.HashToken(token);

        AgentRegistry.HashToken(token).Should().Be(hash);
        hash.Should().NotContain(token);
        hash.Should().HaveLength(64);
    }
}
