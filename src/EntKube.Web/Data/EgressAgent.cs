namespace EntKube.Web.Data;

/// <summary>
/// A registered EntKube egress agent: a small process the customer runs inside
/// their own network, which dials out to EntKube and holds the connection open.
///
/// This exists for networks that can reach a provider's API but permit nothing
/// inbound. EntKube cannot connect to them, and they will not publish a listener,
/// so the connection has to be established from the inside out. Once it is,
/// EntKube can ask the agent to open TCP streams to allowlisted hosts.
///
/// The agent enforces its own host allowlist from local config, so a compromised
/// EntKube cannot use the link to reach arbitrary hosts inside the customer's
/// network. Traffic over the link is end-to-end TLS between EntKube and the
/// destination — the agent relays ciphertext it cannot read.
/// </summary>
public class EgressAgent
{
    public Guid Id { get; set; }

    /// <summary>The tenant that owns this agent.</summary>
    public Guid TenantId { get; set; }

    /// <summary>Human-friendly name, e.g. "Head office network".</summary>
    public required string Name { get; set; }

    /// <summary>What this agent is for — which network it sits in, who runs it.</summary>
    public string? Description { get; set; }

    /// <summary>
    /// SHA-256 of the enrolment token, hex-encoded. The token itself is shown once
    /// at creation and never stored, so a database compromise does not yield a
    /// credential that can impersonate the agent.
    /// </summary>
    public required string TokenHash { get; set; }

    /// <summary>When the agent last had a link open, for showing staleness in the UI.</summary>
    public DateTime? LastSeenAt { get; set; }

    /// <summary>Address the agent last connected from — useful for confirming it is the expected network.</summary>
    public string? LastRemoteAddress { get; set; }

    /// <summary>
    /// Hosts the agent reported it is willing to dial, recorded at connect time.
    /// Informational only: the agent's local config is authoritative, and this is
    /// here so an operator can see what it will actually allow without logging
    /// into the box it runs on.
    /// </summary>
    public string? ReportedAllowlist { get; set; }

    /// <summary>Set false to refuse the agent's link without deleting its registration.</summary>
    public bool IsEnabled { get; set; } = true;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    // Navigation
    public Tenant Tenant { get; set; } = null!;
}
