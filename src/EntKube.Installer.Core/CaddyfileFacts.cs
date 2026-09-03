namespace EntKube.Installer;

/// <summary>
/// What an existing Caddyfile says about the deployment.
///
/// For a hand-rolled install this is often the only place the domain is written down. Someone
/// setting Caddy up by hand types the hostname straight into the site block rather than routing it
/// through a DOMAIN variable, so the .env has no domain in it and the installer would otherwise ask
/// for something already sitting in a file beside it.
/// </summary>
public sealed record CaddyfileFacts(string? Domain, string? AcmeEmail, bool UsesEnvPlaceholder)
{
    public static CaddyfileFacts None { get; } = new(null, null, false);

    /// <summary>
    /// Reads the site address and the ACME email out of a Caddyfile.
    ///
    /// Deliberately a small, conservative reader rather than a Caddyfile parser. It needs two facts
    /// out of a file it does not own, and a value it cannot confidently identify is left null — the
    /// caller then asks, which is the same behaviour as before and never worse. Guessing a domain
    /// wrong would order a certificate for someone else's name.
    /// </summary>
    public static CaddyfileFacts Parse(string content)
    {
        string? domain = null;
        string? email = null;
        bool placeholder = false;
        int depth = 0;
        bool inGlobalOptions = false;

        foreach (string raw in content.Split('\n'))
        {
            string line = StripComment(raw).Trim();

            if (line.Length == 0)
            {
                continue;
            }

            // `email you@example.com`, and only inside the global options block. A site block can
            // contain a directive that starts the same way, and taking one of those would put a
            // stranger's address on the ACME account.
            if (inGlobalOptions && email is null
                && line.StartsWith("email ", StringComparison.OrdinalIgnoreCase))
            {
                string candidate = line[6..].Trim();

                if (candidate.Contains('@') && !candidate.Contains('{'))
                {
                    email = candidate;
                }
            }

            if (line.EndsWith('{'))
            {
                string header = line[..^1].Trim();

                if (header.Length == 0 && depth == 0)
                {
                    inGlobalOptions = true;
                }

                // A bare "{" opens the global options block; anything before it is a site address.
                if (header.Length > 0 && depth == 0)
                {
                    foreach (string token in header.Split([' ', '\t', ','], StringSplitOptions.RemoveEmptyEntries))
                    {
                        if (token.Contains("{env.", StringComparison.OrdinalIgnoreCase) || token.Contains('{'))
                        {
                            placeholder = true;
                            continue;
                        }

                        domain ??= AsHostname(token);
                    }
                }

                depth++;
                continue;
            }

            if (line.EndsWith('}'))
            {
                depth = Math.Max(depth - 1, 0);

                if (depth == 0)
                {
                    inGlobalOptions = false;
                }
            }
        }

        return new CaddyfileFacts(domain, email, placeholder);
    }

    /// <summary>
    /// A site address reduced to a hostname, or null when it is not one.
    ///
    /// Caddy addresses may carry a scheme and a port, and may be a bare port (":443"), a wildcard,
    /// or a path. Only something that looks like a real host is useful here.
    /// </summary>
    private static string? AsHostname(string token)
    {
        string value = token.Trim();

        foreach (string scheme in (string[])["https://", "http://"])
        {
            if (value.StartsWith(scheme, StringComparison.OrdinalIgnoreCase))
            {
                value = value[scheme.Length..];
            }
        }

        // A path makes it a route, not a host.
        int slash = value.IndexOf('/');
        if (slash >= 0)
        {
            value = value[..slash];
        }

        // Trailing :port. Guarded against IPv6 literals, which are full of colons and are not
        // something this installer should be inferring a certificate name from.
        int colon = value.LastIndexOf(':');
        if (colon > 0 && value.IndexOf(':') == colon)
        {
            value = value[..colon];
        }

        if (value.Length == 0
            || value.StartsWith('*')
            || value.Contains('{')
            || !value.Contains('.'))
        {
            return null;
        }

        return value;
    }

    private static string StripComment(string line)
    {
        // Caddy comments start at '#' when it begins a token. Not mid-token, so a '#' inside a value
        // is left alone.
        int hash = line.IndexOf('#');

        while (hash >= 0)
        {
            if (hash == 0 || char.IsWhiteSpace(line[hash - 1]))
            {
                return line[..hash];
            }

            hash = line.IndexOf('#', hash + 1);
        }

        return line;
    }
}
