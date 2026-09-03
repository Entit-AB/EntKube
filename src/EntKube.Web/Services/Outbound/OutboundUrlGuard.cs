using System.Net;
using System.Net.Sockets;

namespace EntKube.Web.Services.Outbound;

/// <summary>The verdict on an operator-supplied outbound URL.</summary>
public sealed record OutboundUrlVerdict
{
    public required bool IsAllowed { get; init; }

    /// <summary>Why it was refused, phrased for the operator who typed the URL.</summary>
    public string? Reason { get; init; }

    /// <summary>Addresses the host resolved to, when resolution happened.</summary>
    public IReadOnlyList<IPAddress> Resolved { get; init; } = [];

    public static OutboundUrlVerdict Allow(IReadOnlyList<IPAddress>? resolved = null) =>
        new() { IsAllowed = true, Resolved = resolved ?? [] };

    public static OutboundUrlVerdict Refuse(string reason) =>
        new() { IsAllowed = false, Reason = reason };
}

/// <summary>
/// Decides whether EntKube may make an outbound request to a URL a user supplied.
///
/// This exists because of where EntKube sits. The management plane can reach every
/// cluster it manages, the host's own loopback, and — on a cloud instance — the link-local
/// metadata endpoint that hands out instance credentials. A notification webhook URL is
/// configured by a *tenant* user, who is not necessarily trusted with that network
/// position. Without this check, "send my alerts to this URL" is a request for EntKube to
/// fetch anything it can reach and, for endpoints that echo the response, hand it back.
///
/// Default-deny for anything that is not a public address. Operators with a genuine
/// internal receiver can allow it explicitly through configuration — an instance-wide
/// setting, deliberately not a per-tenant one, since the whole point is that the tenant
/// is not the party who gets to make this decision.
///
/// Known residual risk: a hostname that resolves to a public address at validation time
/// and a private one when the request is made (DNS rebinding) still gets through. Closing
/// that needs the connection pinned to the validated address, which means a custom
/// SocketsHttpHandler connect callback; it is a worthwhile follow-up but a much larger
/// change than the check itself.
/// </summary>
public static class OutboundUrlGuard
{
    /// <summary>
    /// Validates a URL, optionally resolving DNS. Resolution is what catches a hostname
    /// pointing at an internal address, which is the form this attack usually takes —
    /// "metadata.internal" rather than a literal 169.254.169.254.
    /// </summary>
    public static OutboundUrlVerdict Validate(string? url, bool allowPrivateTargets)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return OutboundUrlVerdict.Refuse("No URL was supplied.");
        }

        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out Uri? uri))
        {
            return OutboundUrlVerdict.Refuse("Not a valid absolute URL.");
        }

        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
        {
            // file:, gopher:, ftp: and friends are how a URL fetcher gets turned into a
            // local file reader.
            return OutboundUrlVerdict.Refuse($"Only http and https URLs are allowed, not '{uri.Scheme}'.");
        }

        if (!string.IsNullOrEmpty(uri.UserInfo))
        {
            // Credentials in the URL are both a leak risk in logs and a common way to
            // confuse a parser about which host is really being addressed.
            return OutboundUrlVerdict.Refuse("Credentials embedded in the URL are not allowed.");
        }

        if (allowPrivateTargets)
        {
            return OutboundUrlVerdict.Allow();
        }

        // A literal address needs no DNS, and skipping resolution avoids a lookup that
        // could itself be used to probe internal DNS.
        if (IPAddress.TryParse(uri.Host.Trim('[', ']'), out IPAddress? literal))
        {
            return IsPubliclyRoutable(literal)
                ? OutboundUrlVerdict.Allow([literal])
                : OutboundUrlVerdict.Refuse(Describe(literal));
        }

        IPAddress[] addresses;
        try
        {
            addresses = Dns.GetHostAddresses(uri.DnsSafeHost);
        }
        catch (SocketException)
        {
            return OutboundUrlVerdict.Refuse($"'{uri.DnsSafeHost}' could not be resolved.");
        }

        if (addresses.Length == 0)
        {
            return OutboundUrlVerdict.Refuse($"'{uri.DnsSafeHost}' resolved to no addresses.");
        }

        // EVERY resolved address must be public. A host resolving to both a public and a
        // private address is exactly the shape of a rebinding attempt, and picking the
        // convenient one would defeat the check.
        foreach (IPAddress address in addresses)
        {
            if (!IsPubliclyRoutable(address))
            {
                return OutboundUrlVerdict.Refuse(
                    $"'{uri.DnsSafeHost}' resolves to {address}, which is {Describe(address).ToLowerInvariant()}");
            }
        }

        return OutboundUrlVerdict.Allow(addresses);
    }

    /// <summary>
    /// Async form, for the request paths that are already asynchronous. DNS resolution
    /// blocks, and doing it synchronously on a notification-delivery thread would stall
    /// it for the length of a lookup against a hostile or slow resolver.
    /// </summary>
    public static async Task<OutboundUrlVerdict> ValidateAsync(
        string? url, bool allowPrivateTargets, CancellationToken ct = default)
    {
        // Everything before resolution is pure string work, so reuse it rather than
        // maintaining two copies of the scheme and credential rules.
        if (string.IsNullOrWhiteSpace(url)
            || !Uri.TryCreate(url.Trim(), UriKind.Absolute, out Uri? uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            || !string.IsNullOrEmpty(uri.UserInfo))
        {
            return Validate(url, allowPrivateTargets);
        }

        if (allowPrivateTargets)
        {
            return OutboundUrlVerdict.Allow();
        }

        if (IPAddress.TryParse(uri.Host.Trim('[', ']'), out IPAddress? literal))
        {
            return IsPubliclyRoutable(literal)
                ? OutboundUrlVerdict.Allow([literal])
                : OutboundUrlVerdict.Refuse(Describe(literal));
        }

        IPAddress[] addresses;
        try
        {
            addresses = await Dns.GetHostAddressesAsync(uri.DnsSafeHost, ct);
        }
        catch (SocketException)
        {
            return OutboundUrlVerdict.Refuse($"'{uri.DnsSafeHost}' could not be resolved.");
        }

        if (addresses.Length == 0)
        {
            return OutboundUrlVerdict.Refuse($"'{uri.DnsSafeHost}' resolved to no addresses.");
        }

        foreach (IPAddress address in addresses)
        {
            if (!IsPubliclyRoutable(address))
            {
                return OutboundUrlVerdict.Refuse(
                    $"'{uri.DnsSafeHost}' resolves to {address}, which is {Describe(address).ToLowerInvariant()}");
            }
        }

        return OutboundUrlVerdict.Allow(addresses);
    }

    /// <summary>
    /// True only for addresses that are routable on the public internet. Everything
    /// else — loopback, link-local, private, multicast, reserved — is refused.
    /// </summary>
    public static bool IsPubliclyRoutable(IPAddress address)
    {
        // An IPv4-mapped IPv6 address (::ffff:169.254.169.254) reaches the same host as
        // the bare IPv4 one, so it has to be unwrapped or the check is trivially bypassed.
        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        if (IPAddress.IsLoopback(address)
            || address.Equals(IPAddress.Any)
            || address.Equals(IPAddress.IPv6Any))
        {
            return false;
        }

        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            byte[] octets = address.GetAddressBytes();

            return octets[0] switch
            {
                0 => false,                                  // 0.0.0.0/8 "this network"
                10 => false,                                 // 10.0.0.0/8 private
                127 => false,                                // loopback
                169 when octets[1] == 254 => false,           // 169.254.0.0/16 link-local — cloud metadata
                172 when octets[1] >= 16 && octets[1] <= 31 => false,  // 172.16.0.0/12 private
                192 when octets[1] == 168 => false,           // 192.168.0.0/16 private
                192 when octets[1] == 0 && octets[2] == 0 => false,    // 192.0.0.0/24 IETF protocol assignments
                100 when octets[1] >= 64 && octets[1] <= 127 => false, // 100.64.0.0/10 carrier-grade NAT
                >= 224 => false,                             // multicast and reserved
                _ => true,
            };
        }

        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            if (address.IsIPv6LinkLocal || address.IsIPv6SiteLocal || address.IsIPv6Multicast)
            {
                return false;
            }

            // fc00::/7 unique-local — the IPv6 equivalent of the private ranges above.
            byte first = address.GetAddressBytes()[0];
            if ((first & 0xFE) == 0xFC)
            {
                return false;
            }

            return true;
        }

        // Anything that is neither IPv4 nor IPv6 is not something to dial.
        return false;
    }

    private static string Describe(IPAddress address)
    {
        IPAddress effective = address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;

        if (IPAddress.IsLoopback(effective))
        {
            return "A loopback address is not an allowed destination.";
        }

        if (effective.AddressFamily == AddressFamily.InterNetwork)
        {
            byte[] octets = effective.GetAddressBytes();
            if (octets[0] == 169 && octets[1] == 254)
            {
                return "A link-local address is not an allowed destination "
                     + "(this range hosts cloud instance-metadata services).";
            }
        }

        return "A private, reserved or non-routable address is not an allowed destination.";
    }
}
