using System.Net;
using System.Net.Sockets;

namespace EntKube.Web.Services.Mail;

/// <summary>How a published record stands up to what a receiving server will check.</summary>
public enum MailDnsVerdict
{
    /// <summary>It is as it should be.</summary>
    Good = 0,

    /// <summary>Worth knowing, but mail will still be accepted.</summary>
    Warning = 1,

    /// <summary>Mail will be refused by at least one large receiver until it is fixed.</summary>
    Refused = 2,
}

/// <summary>One thing that was checked, and what it means.</summary>
/// <param name="What">The check, in the words an operator would use.</param>
/// <param name="Found">What DNS actually says, or null when it says nothing.</param>
/// <param name="Verdict">Whether this is fine, worth knowing, or the reason mail bounces.</param>
/// <param name="Explanation">What follows from it, and what to do.</param>
public readonly record struct MailDnsFinding(
    string What, string? Found, MailDnsVerdict Verdict, string Explanation);

/// <summary>Looking names and addresses up. An interface so the checking can be tested.</summary>
public interface IDnsLookup
{
    /// <summary>The addresses a name resolves to, or empty when it resolves to none.</summary>
    Task<IReadOnlyList<IPAddress>> ForwardAsync(string host, CancellationToken ct = default);

    /// <summary>The name an address reverses to, or null when it has no PTR.</summary>
    Task<string?> ReverseAsync(IPAddress address, CancellationToken ct = default);
}

/// <summary>The resolver the application actually runs against.</summary>
public class SystemDnsLookup : IDnsLookup
{
    public async Task<IReadOnlyList<IPAddress>> ForwardAsync(
        string host, CancellationToken ct = default)
    {
        try
        {
            return await Dns.GetHostAddressesAsync(host, ct);
        }
        catch (SocketException)
        {
            // No such name, or no answer. Both are "DNS says nothing", which is the thing
            // the caller wants to report — not an error to propagate.
            return [];
        }
        catch (ArgumentException)
        {
            return [];
        }
    }

    public async Task<string?> ReverseAsync(IPAddress address, CancellationToken ct = default)
    {
        try
        {
            // By address string, because the IPAddress overload's second parameter is an
            // AddressFamily rather than a cancellation token.
            IPHostEntry entry = await Dns.GetHostEntryAsync(address.ToString(), ct);

            return string.IsNullOrWhiteSpace(entry.HostName) ? null : entry.HostName;
        }
        catch (SocketException)
        {
            // The ordinary answer for an address with no PTR, and the one this was written
            // to find.
            return null;
        }
    }
}

/// <summary>
/// Checking the mail host's own records against what a large receiver will insist on.
///
/// <para><b>Why this exists.</b> The DNS table in the Mail tab lists what to publish, and an
/// installation published all of it — MX, SPF, DMARC, the autodiscovery names — and still had
/// a receipt to a gmail.com address refused: <em>"the IP address sending this message does
/// not have a PTR record setup, or the corresponding forward DNS entry does not match the
/// sending IP."</em> Every record the table named was correct. The one that was missing was
/// not about the domain at all, it was about the address the mail leaves from, and nothing
/// anywhere said so.</para>
///
/// <para>A list of records to publish cannot tell anybody that it is wrong. This asks DNS
/// the same questions the receiving end asks, and says what follows — so the answer arrives
/// before a customer's mail does, rather than as a bounce three weeks later.</para>
///
/// <para><b>Reverse DNS is the one nobody can publish themselves.</b> A PTR lives in the
/// owner of the address's zone — the cloud provider's — so it is a console field or a
/// support request and not something to add alongside the others. That is exactly why it is
/// the one that gets forgotten.</para>
/// </summary>
public class MailDnsCheck(IDnsLookup dns)
{
    /// <summary>
    /// Checks the mail host, and the address it is expected to be.
    /// </summary>
    /// <param name="hostname">
    /// The mail hostname: the SMTP greeting, the name on the certificate, and what every MX
    /// record points at.
    /// </param>
    /// <param name="expectedAddress">
    /// The address the mail host is expected to resolve to — the balancer's, where other
    /// servers connect in. Given, the forward record is checked against it.
    /// </param>
    /// <param name="sendingAddresses">
    /// The addresses outbound mail is seen to come from —
    /// <c>StalwartPlanBuilder.SendingAddressesOf</c>. These are the ones every deliverability
    /// check is actually about, and the ones a cluster makes easy to get wrong: traffic
    /// leaving a pod takes the cluster's own route out, which is a router with an address of
    /// its own and nothing to do with the balancer.
    ///
    /// <para>They are already asked for, because SPF needs them. Nothing was checking the
    /// two records a receiver looks at before it will take the connection at all.</para>
    /// </param>
    public async Task<List<MailDnsFinding>> CheckAsync(
        string? hostname, string? expectedAddress = null,
        IReadOnlyList<string>? sendingAddresses = null, CancellationToken ct = default)
    {
        List<MailDnsFinding> findings = [];

        if (string.IsNullOrWhiteSpace(hostname))
        {
            findings.Add(new(
                "Mail hostname", null, MailDnsVerdict.Warning,
                "No mail hostname is set, so there is nothing to check. It is the SMTP "
                + "greeting, the name on the certificate and what every MX record points at."));

            return findings;
        }

        string host = hostname.Trim().TrimEnd('.');

        IReadOnlyList<IPAddress> addresses = await dns.ForwardAsync(host, ct);

        if (addresses.Count == 0)
        {
            findings.Add(new(
                $"{host} resolves", null, MailDnsVerdict.Refused,
                "Nothing resolves this name, so no other server can deliver to it, no "
                + "certificate can be issued for it, and the greeting it sends names a host "
                + "that does not exist. Publish an A record on the address mail leaves from."));

            return findings;
        }

        findings.Add(new(
            $"{host} resolves", string.Join(", ", addresses), MailDnsVerdict.Good,
            "Other servers can find the mail host."));

        // The A record against the address the installation believes it has. A mismatch here
        // is invisible to reverse DNS — both halves can be internally consistent and still
        // describe an address the mail does not come from.
        if (!string.IsNullOrWhiteSpace(expectedAddress)
            && IPAddress.TryParse(expectedAddress.Trim(), out IPAddress? expected))
        {
            bool points = addresses.Any(a => a.Equals(expected));

            findings.Add(new(
                $"{host} points at the configured address",
                points ? expected.ToString() : $"{string.Join(", ", addresses)} — not {expected}",
                points ? MailDnsVerdict.Good : MailDnsVerdict.Warning,
                points
                    ? "The name resolves to the address this deployment asked for."
                    : "The name does not resolve to the address configured for the mail "
                      + "service. One of the two is stale. Whichever is right, what matters "
                      + "is the address mail actually leaves from — which is what the "
                      + "receiving end checks, and what the reverse lookup below is about."));
        }

        // The sending side, when it is known. Checked instead of the inbound address rather
        // than as well, because a receiver only ever looks at the address the connection came
        // from — reverse DNS on the balancer is of no interest to anybody.
        if (sendingAddresses is { Count: > 0 })
        {
            foreach (string sending in sendingAddresses)
            {
                findings.AddRange(await CheckSendingAsync(sending, host, ct));
            }

            return findings;
        }

        findings.Add(new(
            "Sending addresses", null, MailDnsVerdict.Warning,
            "None are configured, so mail is assumed to leave from the address it arrives on. "
            + "That is true of a single-homed server and false of most Kubernetes installs, "
            + "where traffic leaving a pod takes the cluster's own route out. SPF is built "
            + "from the same list, so an empty one also means outbound mail is not authorised "
            + "for this domain. If a receiver has rejected mail naming an address that is not "
            + "the one above, that is the address to set."));

        foreach (IPAddress address in addresses)
        {
            string? reverse = await dns.ReverseAsync(address, ct);

            if (reverse is null)
            {
                findings.Add(new(
                    $"{address} reverses", null, MailDnsVerdict.Refused,
                    "This address has no PTR record. Gmail and Microsoft both refuse mail "
                    + "from an address without one — this is the single most common reason "
                    + "support mail bounces. A PTR is published by whoever owns the address, "
                    + $"not in this domain's zone: ask the provider to point {address} at "
                    + $"{host}, which is usually a console field or a support request."));

                continue;
            }

            bool matches = string.Equals(
                reverse.TrimEnd('.'), host, StringComparison.OrdinalIgnoreCase);

            findings.Add(new(
                $"{address} reverses", reverse,
                matches ? MailDnsVerdict.Good : MailDnsVerdict.Refused,
                matches
                    ? "Forward and reverse agree, which is what a receiving server checks."
                    : $"The reverse name is {reverse.TrimEnd('.')} and the mail host is "
                      + $"{host}. They disagree, and a receiver that checks both — Gmail "
                      + "does — treats that the same as having no PTR at all. Either point "
                      + $"the PTR at {host}, or make {host} the name the greeting uses."));
        }

        return findings;
    }

    /// <summary>
    /// The two records a sending address needs, and whether they agree with each other.
    ///
    /// <para>A receiver checks that the address has a PTR, and that the name the PTR gives
    /// resolves back to that same address. One without the other is worth nothing — which is
    /// why they are reported together rather than as separate facts to piece together, and
    /// why a PTR the provider set is not on its own the end of it.</para>
    /// </summary>
    private async Task<List<MailDnsFinding>> CheckSendingAsync(
        string sendingAddress, string mailHost, CancellationToken ct)
    {
        List<MailDnsFinding> findings = [];

        if (!IPAddress.TryParse(sendingAddress.Trim(), out IPAddress? sending))
        {
            findings.Add(new(
                "Sending address", sendingAddress, MailDnsVerdict.Warning,
                "This is not an address. It should be the one a receiving server sees the "
                + "connection coming from — the address named in a rejection, or the one in "
                + "the Received header of a message that got through."));

            return findings;
        }

        string? reverse = await dns.ReverseAsync(sending, ct);

        if (reverse is null)
        {
            findings.Add(new(
                $"{sending} reverses", null, MailDnsVerdict.Refused,
                "This address has no PTR record. Gmail and Microsoft both refuse mail from "
                + "such an address outright, before anything else about the message is "
                + "considered. A PTR is published by whoever owns the address, not in the "
                + "mail domain's zone — usually a provider console field or a support "
                + $"request. Point it at smtp-out.{DomainOf(mailHost)}, which the DNS table "
                + "above lists an A record for."));

            return findings;
        }

        string reverseName = reverse.TrimEnd('.');

        findings.Add(new(
            $"{sending} reverses", reverseName, MailDnsVerdict.Good,
            "The address mail leaves from has a PTR record."));

        // Forward-confirmed reverse DNS: the name the PTR gives has to resolve back to the
        // address that sent. This is the half a PTR alone does not buy, and the half Gmail
        // names in the same sentence as the missing one.
        IReadOnlyList<IPAddress> back = await dns.ForwardAsync(reverseName, ct);
        bool confirmed = back.Any(a => a.Equals(sending));

        findings.Add(new(
            $"{reverseName} resolves back to {sending}",
            back.Count == 0 ? null : string.Join(", ", back),
            confirmed ? MailDnsVerdict.Good : MailDnsVerdict.Refused,
            confirmed
                ? "Forward and reverse agree, which is what a receiving server checks."
                : back.Count == 0
                    ? $"The PTR names {reverseName}, and nothing resolves that name. A "
                      + "receiver treats a reverse name it cannot confirm the same as no "
                      + $"reverse name at all. Publish an A record for {reverseName} on "
                      + $"{sending}."
                    : $"The PTR names {reverseName}, but that name resolves to "
                      + $"{string.Join(", ", back)} rather than to {sending}. A receiver "
                      + "checks both directions and refuses mail when they disagree."));

        // Pointing the PTR at the mail host is the obvious move and cannot work, so it is
        // explained rather than left as a bare mismatch.
        if (string.Equals(reverseName, mailHost, StringComparison.OrdinalIgnoreCase)
            && !confirmed)
        {
            findings.Add(new(
                "The reverse name is the mail host", mailHost, MailDnsVerdict.Refused,
                $"{mailHost} has to resolve to the address other servers deliver to, so it "
                + "cannot also resolve to the address mail leaves from. Give the sending "
                + $"address its own name — smtp-out.{DomainOf(mailHost)} — and point the PTR "
                + "at that instead."));
        }

        return findings;
    }

    /// <summary>The domain part of a host name, for suggesting a name beside it.</summary>
    private static string DomainOf(string host)
    {
        int dot = host.IndexOf('.', StringComparison.Ordinal);

        return dot > 0 && dot < host.Length - 1 ? host[(dot + 1)..] : host;
    }
}
