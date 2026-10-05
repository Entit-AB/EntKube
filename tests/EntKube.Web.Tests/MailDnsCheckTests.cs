using System.Net;
using EntKube.Web.Services.Mail;
using FluentAssertions;

namespace EntKube.Web.Tests;

/// <summary>
/// Whether the mail host's own records will get its mail accepted.
///
/// <para>Written because an installation published every record the Mail tab told it to —
/// MX, SPF, DMARC, the autodiscovery names — and had a receipt to a gmail.com address
/// refused anyway: the sending address had no PTR, and nothing in the product mentioned one.
/// A list of records to publish cannot tell anybody it is incomplete, so this asks DNS the
/// questions the receiving end asks.</para>
/// </summary>
public class MailDnsCheckTests
{
    /// <summary>A resolver that answers from a script rather than from the internet.</summary>
    private sealed class Zone : IDnsLookup
    {
        public Dictionary<string, IPAddress[]> Forward { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, string> Reverse { get; } = new(StringComparer.OrdinalIgnoreCase);

        public Task<IReadOnlyList<IPAddress>> ForwardAsync(string host, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<IPAddress>>(
                Forward.TryGetValue(host, out IPAddress[]? found) ? found : []);

        public Task<string?> ReverseAsync(IPAddress address, CancellationToken ct = default) =>
            Task.FromResult(Reverse.TryGetValue(address.ToString(), out string? name) ? name : null);
    }

    private const string Host = "mail.entit.se";
    private const string Address = "86.107.49.197";

    private static Zone Published(bool withPtr = true, string ptrName = Host)
    {
        Zone zone = new();
        zone.Forward[Host] = [IPAddress.Parse(Address)];

        if (withPtr)
        {
            zone.Reverse[Address] = ptrName;
        }

        return zone;
    }

    /// <summary>
    /// <b>The failure this was written for.</b> Forward DNS is right, every record in the
    /// table is published, and the address has no reverse name — so Gmail refuses the mail
    /// and says so in a bounce three weeks later instead of here.
    /// </summary>
    [Fact]
    public async Task An_address_with_no_reverse_name_is_reported_as_refused()
    {
        List<MailDnsFinding> findings =
            await new MailDnsCheck(Published(withPtr: false)).CheckAsync(Host);

        MailDnsFinding ptr = findings.Should()
            .ContainSingle(f => f.What.Contains("reverses")).Subject;

        ptr.Verdict.Should().Be(MailDnsVerdict.Refused);
        ptr.Found.Should().BeNull();
        ptr.Explanation.Should().Contain("PTR");

        // And it says whose job it is, because this is the one record nobody can publish
        // in their own zone.
        ptr.Explanation.Should().Contain("provider");
    }

    /// <summary>
    /// A PTR that names something else. Gmail checks both directions, so this is as bad as
    /// having none — and far more confusing, because reverse DNS plainly "works".
    /// </summary>
    [Fact]
    public async Task A_reverse_name_that_disagrees_is_just_as_bad()
    {
        List<MailDnsFinding> findings = await new MailDnsCheck(
            Published(ptrName: "host-86-107-49-197.cloud.example")).CheckAsync(Host);

        findings.Should()
            .ContainSingle(f => f.What.Contains("reverses"))
            .Which.Verdict.Should().Be(MailDnsVerdict.Refused);
    }

    [Fact]
    public async Task Records_that_agree_are_not_reported_as_refusing_anything()
    {
        List<MailDnsFinding> findings = await new MailDnsCheck(Published()).CheckAsync(Host);

        findings.Should().NotContain(f => f.Verdict == MailDnsVerdict.Refused);
    }

    /// <summary>
    /// <b>The assumption that was wrong here.</b> With no sending addresses configured, the
    /// check is looking at the address mail arrives on and saying nothing about the one it
    /// leaves from — which on a Kubernetes install is a different address entirely. Said out
    /// loud, because every finding about the inbound address is beside the point if mail does
    /// not come from it, and because SPF is built from the same empty list.
    /// </summary>
    [Fact]
    public async Task No_sending_addresses_is_flagged_rather_than_assumed_away()
    {
        List<MailDnsFinding> findings = await new MailDnsCheck(Published()).CheckAsync(Host);

        findings.Should()
            .ContainSingle(f => f.What.Contains("Sending addresses"))
            .Which.Verdict.Should().Be(MailDnsVerdict.Warning);
    }

    /// <summary>A trailing dot on the PTR is how a resolver often returns it.</summary>
    [Fact]
    public async Task A_fully_qualified_reverse_name_still_matches()
    {
        List<MailDnsFinding> findings =
            await new MailDnsCheck(Published(ptrName: Host + ".")).CheckAsync(Host);

        findings.Should().NotContain(f => f.Verdict == MailDnsVerdict.Refused);
    }

    /// <summary>
    /// Nothing resolves the name at all. Worse than a missing PTR and differently so: no
    /// other server can deliver to it and no certificate can be issued for it.
    /// </summary>
    [Fact]
    public async Task A_name_that_resolves_to_nothing_is_reported_first()
    {
        List<MailDnsFinding> findings = await new MailDnsCheck(new Zone()).CheckAsync(Host);

        findings.Should().ContainSingle()
            .Which.Verdict.Should().Be(MailDnsVerdict.Refused);
    }

    /// <summary>
    /// The A record and the address the deployment asked for disagreeing. Reverse DNS
    /// cannot reveal this — both halves can be internally consistent and still describe an
    /// address the mail does not come from.
    /// </summary>
    [Fact]
    public async Task An_A_record_pointing_somewhere_else_is_flagged()
    {
        List<MailDnsFinding> findings = await new MailDnsCheck(Published())
            .CheckAsync(Host, expectedAddress: "203.0.113.9");

        findings.Should()
            .ContainSingle(f => f.What.Contains("points at"))
            .Which.Verdict.Should().Be(MailDnsVerdict.Warning);
    }

    [Fact]
    public async Task The_configured_address_matching_is_reported_as_good()
    {
        List<MailDnsFinding> findings = await new MailDnsCheck(Published())
            .CheckAsync(Host, expectedAddress: Address);

        findings.Should().Contain(f => f.What.Contains("points at")
                                       && f.Verdict == MailDnsVerdict.Good);
    }

    /// <summary>No hostname is a thing to say, not a thing to crash on.</summary>
    [Fact]
    public async Task No_hostname_is_reported_rather_than_checked()
    {
        List<MailDnsFinding> findings = await new MailDnsCheck(new Zone()).CheckAsync(null);

        findings.Should().ContainSingle()
            .Which.Verdict.Should().Be(MailDnsVerdict.Warning);
    }

    // ---- The sending address, which is a different address --------------------------------

    private const string Egress = "86.107.49.197";
    private const string Balancer = "203.0.113.10";
    private const string OutHost = "smtp-out.entit.se";

    /// <summary>
    /// The real topology: the mail host points at the balancer, because that is where other
    /// servers deliver, and mail leaves through the cluster's own route out from an address
    /// with nothing to do with it.
    /// </summary>
    private static Zone SplitHorizon(bool withPtr, string ptrName = OutHost, bool forwardOk = true)
    {
        Zone zone = new();
        zone.Forward[Host] = [IPAddress.Parse(Balancer)];

        if (withPtr)
        {
            zone.Reverse[Egress] = ptrName;
        }

        if (forwardOk)
        {
            zone.Forward[ptrName] = [IPAddress.Parse(Egress)];
        }

        return zone;
    }

    /// <summary>
    /// <b>The rejection that started this.</b> Everything about the domain is published and
    /// the address the mail actually comes from has no reverse name.
    /// </summary>
    [Fact]
    public async Task The_sending_address_having_no_PTR_is_the_finding()
    {
        List<MailDnsFinding> findings = await new MailDnsCheck(SplitHorizon(withPtr: false))
            .CheckAsync(Host, Balancer, [Egress]);

        MailDnsFinding ptr = findings.Should()
            .ContainSingle(f => f.What.StartsWith($"{Egress} reverses")).Subject;

        ptr.Verdict.Should().Be(MailDnsVerdict.Refused);

        // And whose job the fix is, because this is the one record nobody can publish in
        // their own zone.
        ptr.Explanation.Should().Contain("provider");
    }

    /// <summary>
    /// A PTR on its own is not enough: the name it gives has to resolve back to the sending
    /// address. That is the second half of what Gmail says in one sentence, and the half a
    /// provider setting the PTR does not give you.
    /// </summary>
    [Fact]
    public async Task A_PTR_whose_name_resolves_nowhere_is_still_refused()
    {
        List<MailDnsFinding> findings = await new MailDnsCheck(
                SplitHorizon(withPtr: true, forwardOk: false))
            .CheckAsync(Host, Balancer, [Egress]);

        findings.Should()
            .ContainSingle(f => f.What.Contains("resolves back to"))
            .Which.Verdict.Should().Be(MailDnsVerdict.Refused);
    }

    /// <summary>
    /// <b>The obvious move that cannot work.</b> Pointing the PTR at the mail host looks
    /// right: that name has to resolve to the balancer so other servers can deliver, so it
    /// will never resolve back to the sending address. Explained rather than left as a bare
    /// mismatch.
    /// </summary>
    [Fact]
    public async Task Pointing_the_PTR_at_the_mail_host_is_explained()
    {
        List<MailDnsFinding> findings = await new MailDnsCheck(
                SplitHorizon(withPtr: true, ptrName: Host, forwardOk: false))
            .CheckAsync(Host, Balancer, [Egress]);

        findings.Should()
            .ContainSingle(f => f.What.Contains("reverse name is the mail host"))
            .Which.Explanation.Should().Contain("smtp-out.");
    }

    [Fact]
    public async Task A_sending_address_that_is_set_up_properly_passes()
    {
        List<MailDnsFinding> findings = await new MailDnsCheck(SplitHorizon(withPtr: true))
            .CheckAsync(Host, Balancer, [Egress]);

        findings.Should().NotContain(f => f.Verdict == MailDnsVerdict.Refused);
    }

    /// <summary>
    /// Once the sending addresses are known, reverse DNS on the inbound address is of no
    /// interest — a receiver only ever looks at where the connection came from.
    /// </summary>
    [Fact]
    public async Task The_inbound_address_is_not_checked_for_a_PTR()
    {
        List<MailDnsFinding> findings = await new MailDnsCheck(SplitHorizon(withPtr: true))
            .CheckAsync(Host, Balancer, [Egress]);

        findings.Should().NotContain(f => f.What.StartsWith($"{Balancer} reverses"));
    }

    /// <summary>
    /// Several sending addresses, which is why the configuration takes a list: one name
    /// serves all of them, because each address's PTR names it and it resolves to the set.
    /// </summary>
    [Fact]
    public async Task Every_sending_address_is_checked()
    {
        const string second = "86.107.49.198";

        Zone zone = SplitHorizon(withPtr: true);
        zone.Forward[OutHost] = [IPAddress.Parse(Egress), IPAddress.Parse(second)];

        List<MailDnsFinding> findings = await new MailDnsCheck(zone)
            .CheckAsync(Host, Balancer, [Egress, second]);

        // The second has no PTR, so it is refused on its own account.
        findings.Should().Contain(f => f.What.StartsWith($"{second} reverses")
                                       && f.Verdict == MailDnsVerdict.Refused);
        findings.Should().Contain(f => f.What.StartsWith($"{Egress} reverses")
                                       && f.Verdict == MailDnsVerdict.Good);
    }
}
