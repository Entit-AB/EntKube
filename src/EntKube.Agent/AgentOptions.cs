namespace EntKube.Agent;

/// <summary>
/// Everything the agent needs, read from appsettings.json, environment variables
/// (prefix <c>ENTKUBE_</c>) or the command line.
/// </summary>
public sealed class AgentOptions
{
    /// <summary>
    /// Base URL of the EntKube instance, e.g. "https://entkube.example.com".
    /// The agent connects outbound to this; nothing connects to the agent.
    /// </summary>
    public string ServerUrl { get; set; } = "";

    /// <summary>Enrolment token generated in EntKube. Shown once at creation.</summary>
    public string Token { get; set; } = "";

    /// <summary>
    /// Hosts this agent is permitted to open connections to. **This is the
    /// security boundary.** The agent refuses anything not listed here, whatever
    /// EntKube asks for, so a compromised EntKube cannot use the link to reach
    /// other systems on this network.
    ///
    /// Entries are hostnames, matched case-insensitively. A leading "*." allows
    /// subdomains of that suffix but not the bare domain — "*.example.com" allows
    /// "s3.example.com" and not "example.com".
    /// </summary>
    public List<string> AllowedHosts { get; set; } = [];

    /// <summary>
    /// Ports the agent will dial. Defaults to HTTPS only; widening this is a
    /// deliberate act because the whole point is a narrow hole.
    /// </summary>
    public List<int> AllowedPorts { get; set; } = [443];

    /// <summary>Seconds to wait before reconnecting after the link drops. Backs off up to <see cref="MaxReconnectSeconds"/>.</summary>
    public int ReconnectSeconds { get; set; } = 5;

    /// <summary>Ceiling for reconnect backoff.</summary>
    public int MaxReconnectSeconds { get; set; } = 60;

    /// <summary>Seconds to wait for an outbound TCP connection before giving up.</summary>
    public int ConnectTimeoutSeconds { get; set; } = 15;

    /// <summary>Log each stream that is opened and closed. Off by default to keep logs quiet.</summary>
    public bool VerboseLogging { get; set; }

    /// <summary>
    /// Validates the configuration and returns the problems found, so the agent
    /// can refuse to start with a message that says what to fix rather than
    /// failing obscurely once connected.
    /// </summary>
    public List<string> Validate()
    {
        List<string> problems = [];

        if (string.IsNullOrWhiteSpace(ServerUrl))
        {
            problems.Add("ServerUrl is required (e.g. https://entkube.example.com).");
        }
        else if (!Uri.TryCreate(ServerUrl, UriKind.Absolute, out Uri? uri)
                 || uri.Scheme is not ("http" or "https"))
        {
            problems.Add($"ServerUrl '{ServerUrl}' must be an absolute http or https URL.");
        }

        if (string.IsNullOrWhiteSpace(Token))
        {
            problems.Add("Token is required — generate one in EntKube under the egress agent.");
        }

        if (AllowedHosts.Count == 0)
        {
            problems.Add(
                "AllowedHosts is empty. The agent will not dial anything without an explicit allowlist; "
                + "list the hostnames it should be able to reach.");
        }

        if (AllowedPorts.Count == 0)
        {
            problems.Add("AllowedPorts is empty. List at least one port (443 is the usual value).");
        }

        return problems;
    }

    /// <summary>
    /// Decides whether the agent will dial this target.
    ///
    /// Deliberately strict: exact host match, or a "*." suffix rule that requires
    /// a real subdomain. No regex, no substring matching — a permissive rule here
    /// is the one bug that would matter.
    /// </summary>
    public bool IsAllowed(string host, int port)
    {
        if (!AllowedPorts.Contains(port)) return false;
        if (string.IsNullOrWhiteSpace(host)) return false;

        foreach (string entry in AllowedHosts)
        {
            string rule = entry.Trim();

            if (rule.StartsWith("*.", StringComparison.Ordinal))
            {
                // Require something before the dot, so "*.example.com" does not
                // match "example.com" or a lookalike like "evilexample.com".
                string suffix = rule[1..]; // ".example.com"

                if (host.Length > suffix.Length
                    && host.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
            else if (string.Equals(host, rule, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>A compact description of the allowlist, reported to EntKube so it can be seen in the UI.</summary>
    public string DescribeAllowlist()
        => string.Join(",", AllowedHosts.Select(h => h.Trim()).Where(h => h.Length > 0));
}
