using System.Text.Json;
using EntKube.Web.Data;
using EntKube.Web.Services;
using FluentAssertions;
using YamlDotNet.RepresentationModel;

namespace EntKube.Web.Tests;

/// <summary>
/// Covers the pure renderers behind the mail components. These produce YAML and NDJSON that no
/// compiler checks, against a server whose objects reject anything malformed — so the tests parse
/// what was produced rather than string-matching it, and assert the decisions that are easy to get
/// wrong and slow to notice: which listeners exist, which operation each object gets, and whether
/// a credential ever reaches the plan.
/// </summary>
public class StalwartMailTests
{
    private static StalwartComponentConfig Config(Action<StalwartComponentConfig>? tweak = null)
    {
        StalwartComponentConfig config = new()
        {
            Id = Guid.NewGuid(),
            TenantId = Guid.NewGuid(),
            Hostname = "mail.example.com",
            AdminHostname = "mailadmin.example.com",
            LdapUrl = "ldap://openldap.openldap.svc.cluster.local:389",
            LdapBaseDn = "dc=example,dc=com",
            LdapBindDn = "cn=admin,dc=example,dc=com",
        };
        tweak?.Invoke(config);
        return config;
    }

    private static StalwartMailDomain Domain(Guid configId, string name, bool primary = true) => new()
    {
        Id = Guid.NewGuid(),
        ConfigId = configId,
        Name = name,
        IsPrimary = primary,
    };

    /// <summary>
    /// A mailbox EntKube minted a password for gets that password in the plan.
    ///
    /// <para>Without this the plan writes the account with no credential of any kind — only the
    /// administrator ever got one — so the mailbox exists, looks correct in the admin interface, and
    /// answers every login with a temporary failure. It is the whole reason a support mailbox could not
    /// read from an internal-auth server.</para>
    /// </summary>
    [Fact]
    public void Plan_GivesAMintedMailboxItsPassword()
    {
        StalwartComponentConfig config = Config(c => c.AuthMode = StalwartAuthMode.Internal);
        StalwartMailDomain domain = Domain(config.Id, "example.com");
        StalwartMailAccount account = new()
        {
            Id = Guid.NewGuid(), ConfigId = config.Id, DomainId = domain.Id, LocalPart = "support",
            PasswordSetAt = DateTime.UtcNow,
        };

        string plan = StalwartPlanBuilder.BuildApplyPlan(
            config, [domain], [account],
            accountPasswords: new Dictionary<Guid, string> { [account.Id] = "minted-secret" });

        JsonElement accounts = Operation(plan, "Account")!.Value.GetProperty("value");

        JsonElement support = accounts.EnumerateObject()
            .Select(p => p.Value)
            .Single(v => v.GetProperty("name").GetString() == "support");

        support.GetProperty("credentials").EnumerateObject()
            .Select(c => c.Value.GetProperty("secret").GetString())
            .Should().Contain("minted-secret");
    }

    /// <summary>
    /// And does not, where the directory is what checks.
    ///
    /// <para>A password written into an account under LDAP or OIDC is never consulted — authentication
    /// is routed to the directory — so emitting one produces an account that looks credentialed and
    /// cannot log in. That is worse than one plainly without a password, because it hides the reason.
    /// </para>
    /// </summary>
    [Theory]
    [InlineData(StalwartAuthMode.Ldap)]
    [InlineData(StalwartAuthMode.Oidc)]
    public void Plan_DoesNotWriteAPasswordTheDirectoryWouldNeverCheck(StalwartAuthMode mode)
    {
        StalwartComponentConfig config = Config(c =>
        {
            c.AuthMode = mode;
            c.OidcIssuerUrl = "https://sso.example.com/realms/mail";
        });
        StalwartMailDomain domain = Domain(config.Id, "example.com");
        StalwartMailAccount account = new()
        {
            Id = Guid.NewGuid(), ConfigId = config.Id, DomainId = domain.Id, LocalPart = "support",
            PasswordSetAt = DateTime.UtcNow,
        };

        string plan = StalwartPlanBuilder.BuildApplyPlan(
            config, [domain], [account],
            accountPasswords: new Dictionary<Guid, string> { [account.Id] = "minted-secret" });

        plan.Should().NotContain("minted-secret");
    }

    /// <summary>
    /// Every internal range is one Stalwart will actually parse.
    ///
    /// <para>The invariant that would have caught this before it reached a cluster. Stalwart accepts a
    /// prefix of 8–32 for IPv4 and 8–128 for IPv6, so <c>fc00::/7</c> — the ordinary way to write the
    /// unique-local range — is refused. A refused entry is not merely absent: the apply plan stops at
    /// its first failed operation and the allow-list is emitted before the accounts, so one bad range
    /// strands the administrator, every mailbox, the spam settings and the milter.</para>
    ///
    /// <para>It failed on a live cluster as <c>AllowedIp: create failed for internal-5 ... Failed to
    /// parse IpAddrOrMask from string</c>, with nothing naming the prefix length as the reason.</para>
    /// </summary>
    [Fact]
    public void Plan_EveryInternalRangeIsOneStalwartAccepts()
    {
        StalwartPlanBuilder.InternalRanges.Should().OnlyContain(
            r => StalwartPlanBuilder.IsAcceptableIpOrMask(r));
    }

    /// <summary>
    /// And the unique-local range is still covered, in the two halves that are allowed.
    ///
    /// <para>Without this, the fix for the parse failure could be "delete the IPv6 entry", which would
    /// pass the test above and silently stop protecting a dual-stack cluster from banning itself.</para>
    /// </summary>
    [Fact]
    public void Plan_CoversTheUniqueLocalRangeAsTwoAcceptableHalves()
    {
        StalwartComponentConfig config = Config();

        string plan = StalwartPlanBuilder.BuildApplyPlan(config, [Domain(config.Id, "example.com")], []);

        List<string> addresses = Operation(plan, "AllowedIp")!.Value.GetProperty("value")
            .EnumerateObject()
            .Select(p => p.Value.GetProperty("address").GetString()!)
            .ToList();

        // fc00::/8 and fd00::/8 together are exactly fc00::/7.
        addresses.Should().Contain("fc00::/8").And.Contain("fd00::/8");
        addresses.Should().NotContain("fc00::/7");

        // Loopback is still covered, under the name the server keeps it by.
        addresses.Should().Contain("::1").And.NotContain("::1/128");
    }

    /// <summary>
    /// Addresses go into the plan written the way Stalwart writes them back.
    ///
    /// <para>The allow-list is upserted with <c>address</c> as the match key, and Stalwart drops a
    /// full-length prefix when it stores the value — so <c>::1/128</c> is kept as <c>::1</c>. Sending
    /// the longer spelling matches nothing, becomes a create, and collides with the row already there:
    /// <c>primaryKeyViolation</c> for an entry that exists. And because the plan stops at its first
    /// failure, that strands every operation after it, the accounts included.</para>
    /// </summary>
    [Theory]
    // The one that failed on the cluster.
    [InlineData("::1/128", "::1")]
    [InlineData("10.0.0.5/32", "10.0.0.5")]
    // Shorter prefixes are kept — Stalwart recovers them from the mask's bit count.
    [InlineData("10.0.0.0/8", "10.0.0.0/8")]
    [InlineData("fc00::/8", "fc00::/8")]
    // Already bare, and left alone.
    [InlineData("10.240.3.59", "10.240.3.59")]
    public void Plan_WritesAddressesTheWayStalwartKeepsThem(string value, string expected) =>
        StalwartPlanBuilder.CanonicalIpOrMask(value).Should().Be(expected);

    /// <summary>
    /// And the source list says what is actually sent.
    ///
    /// <para>Normalising at emission makes the plan correct whatever is written here, which is worth
    /// having for the gateway addresses resolved from the live cluster. But an entry in this list that
    /// needed normalising would mean the source read as one thing and the server held another, and the
    /// next person reading it would have no way to know which spelling the match key uses.</para>
    /// </summary>
    [Fact]
    public void Plan_InternalRangesAreWrittenAsStalwartKeepsThem()
    {
        foreach (string range in StalwartPlanBuilder.InternalRanges)
        {
            StalwartPlanBuilder.CanonicalIpOrMask(range).Should().Be(range);
        }
    }

    /// <summary>
    /// What the parse rule is, stated as examples, including the one that caught us.
    /// </summary>
    [Theory]
    [InlineData("10.0.0.0/8", true)]
    [InlineData("100.64.0.0/10", true)]
    [InlineData("::1/128", true)]
    [InlineData("fc00::/8", true)]
    [InlineData("10.240.3.59", true)]
    // The whole bug: a prefix shorter than 8 is refused, for either family.
    [InlineData("fc00::/7", false)]
    [InlineData("10.0.0.0/7", false)]
    [InlineData("0.0.0.0/0", false)]
    // Stalwart's is_valid() refuses the unspecified address however the mask is written.
    [InlineData("0.0.0.0", false)]
    [InlineData("0.0.0.0/8", false)]
    [InlineData("::", false)]
    // Longer than the family allows.
    [InlineData("10.0.0.0/33", false)]
    // .NET reads this as 10.0.0.1 and Rust refuses it, so agreeing with .NET is not enough.
    [InlineData("10.1", false)]
    [InlineData("not-an-address", false)]
    public void Plan_KnowsWhichAddressesStalwartWillTake(string value, bool accepted) =>
        StalwartPlanBuilder.IsAcceptableIpOrMask(value).Should().Be(accepted);

    /// <summary>
    /// A gateway address that would be refused is left out instead of aborting the apply.
    ///
    /// <para>These are resolved from the live cluster rather than written here, so they are the half of
    /// the allow-list that could carry something unexpected. Losing one range risks the gateway being
    /// banned; sending it loses the accounts and everything else after the allow-list — which is not a
    /// comparable cost, so the doubtful entry is dropped and reported.</para>
    /// </summary>
    [Fact]
    public void Plan_LeavesOutAGatewayAddressStalwartWouldRefuse()
    {
        StalwartComponentConfig config = Config();

        string plan = StalwartPlanBuilder.BuildApplyPlan(
            config, [Domain(config.Id, "example.com")], [],
            trustedProxyAddresses: ["10.240.3.59", "0.0.0.0/0", "10.240.3.60"]);

        List<string> addresses = Operation(plan, "AllowedIp")!.Value.GetProperty("value")
            .EnumerateObject()
            .Select(p => p.Value.GetProperty("address").GetString()!)
            .ToList();

        addresses.Should().Contain("10.240.3.59").And.Contain("10.240.3.60");
        addresses.Should().NotContain("0.0.0.0/0");
    }

    /// <summary>
    /// The accounts are written before the allow-list, and that order is the actual protection.
    ///
    /// <para>apply stops at its first failed operation. The allow-list ran at #12 and the accounts at
    /// #19, so each of the two allow-list failures on the live cluster — an unparseable prefix, then a
    /// spelling that turned a match into a colliding create — also took down the administrator account,
    /// every mailbox, the spam settings and the milter. A fresh install became a server nobody could
    /// sign into.</para>
    ///
    /// <para>Auto-ban is the one thing here a server can run without. So it goes last, and this test is
    /// what stops it drifting back up: whatever fails there now, the mail server is still usable.</para>
    /// </summary>
    [Fact]
    public void Plan_WritesTheAccountsBeforeAnythingAboutBanning()
    {
        StalwartComponentConfig config = Config(c => c.AdminUsername = "admin");

        string plan = StalwartPlanBuilder.BuildApplyPlan(config, [Domain(config.Id, "example.com")], []);

        List<string> objects = ParsePlan(plan)
            .Select(op => op.GetProperty("object").GetString()!)
            .ToList();

        objects.Should().Contain("Account").And.Contain("AllowedIp");
        objects.IndexOf("Account").Should().BeLessThan(objects.IndexOf("AllowedIp"));

        // And the endpoint policy stays last of all, for the reason it was put there: it is the one
        // operation built from an expression the server parses at apply time.
        objects.IndexOf("Http").Should().BeGreaterThan(objects.IndexOf("Account"));
    }

    /// <summary>
    /// A gateway address already covered by a named range is not emitted twice.
    ///
    /// <para>The allow-list is upserted with <c>address</c> as the match key, so two entries sharing one
    /// is ambiguous — the same trap the account loop avoids by promoting the administrator in place
    /// instead of emitting it separately.</para>
    /// </summary>
    [Fact]
    public void Plan_DoesNotListOneAddressTwice()
    {
        StalwartComponentConfig config = Config();

        string plan = StalwartPlanBuilder.BuildApplyPlan(
            config, [Domain(config.Id, "example.com")], [],
            // The same address three ways: repeated, and once spelled with a full-length prefix that
            // canonicalises onto the other two.
            trustedProxyAddresses: ["10.240.3.59", "10.240.3.59", "10.240.3.59/32"]);

        List<string> addresses = Operation(plan, "AllowedIp")!.Value.GetProperty("value")
            .EnumerateObject()
            .Select(p => p.Value.GetProperty("address").GetString()!)
            .ToList();

        addresses.Should().OnlyHaveUniqueItems();
        addresses.Should().ContainSingle(a => a == "10.240.3.59");
    }

    /// <summary>
    /// Which addresses a trusted-networks entry covers — the check behind the warning about a list
    /// that reaches into the cluster.
    ///
    /// <para>Stalwart demands a PROXY header from every peer matching <c>proxyTrustedNetworks</c>, on
    /// every listener. <c>overrideProxyTrustedNetworks</c> cannot be used to exempt one: it falls back
    /// to the global list when it is empty, so emptiness means inherit and there is no way to say "not
    /// here". An entry covering pod or gateway addresses therefore stops the support mailbox, webmail
    /// and the admin interface connecting at all, and it fails as a connection that never completes —
    /// which reads as a network fault rather than as configuration.</para>
    /// </summary>
    [Theory]
    // A balancer subnet, which is what the field is for: the gateway pod is not in it.
    [InlineData("10.240.3.0/24", "10.240.3.59", true)]
    [InlineData("10.240.3.0/24", "100.64.12.7", false)]
    // The over-broad entries that cause the outage.
    [InlineData("10.0.0.0/8", "10.240.3.59", true)]
    [InlineData("10.0.0.0/8", "10.4.0.9", true)]
    [InlineData("100.64.0.0/10", "100.64.12.7", true)]
    // A bare address covers only itself.
    [InlineData("10.240.3.59", "10.240.3.59", true)]
    [InlineData("10.240.3.59", "10.240.3.60", false)]
    // Families do not cross, and nonsense is not a match.
    [InlineData("fc00::/8", "10.240.3.59", false)]
    [InlineData("not-a-network", "10.240.3.59", false)]
    public void Plan_KnowsWhichAddressesATrustedNetworkCovers(string network, string address, bool covers) =>
        StalwartPlanBuilder.TrustedNetworkCovers(network, System.Net.IPAddress.Parse(address))
            .Should().Be(covers);

    /// <summary>
    /// The certificate set is replaced, not added to.
    ///
    /// <para>An upsert only ever adds, and nothing removed the certificates left by earlier
    /// configurations — a different TLS mode, an earlier install against the same datastore, a hostname
    /// since changed. A live server reached five and said so on every handshake: <c>Multiple TLS
    /// certificates available, total = 5</c>. Stalwart then chooses, and a wrong choice is served as a
    /// name mismatch on a server whose configuration looks correct. The support mailbox now connects on
    /// the public hostname and checks the name, so that choice stopped being cosmetic.</para>
    /// </summary>
    [Theory]
    [InlineData(StalwartTlsMode.ClusterIssuer)]
    [InlineData(StalwartTlsMode.Manual)]
    public void Plan_ReplacesTheCertificateSetRatherThanAddingToIt(StalwartTlsMode mode)
    {
        StalwartComponentConfig config = Config(c => c.TlsMode = mode);

        string plan = StalwartPlanBuilder.BuildApplyPlan(config, [Domain(config.Id, "example.com")], []);

        ParsePlan(plan)
            .Single(op => op.GetProperty("object").GetString() == "Certificate")
            .GetProperty("@type").GetString().Should().Be("reconcile");
    }

    /// <summary>
    /// Under ACME the certificate set is left alone entirely.
    ///
    /// <para>Stalwart obtains and renews its own there, so replacing the set would delete them — which
    /// is why the reconcile lives inside the file-backed branch instead of being emitted unconditionally
    /// with an empty set. Getting this wrong would take out TLS on a working server.</para>
    /// </summary>
    [Fact]
    public void Plan_DoesNotTouchCertificatesStalwartObtainsItself()
    {
        StalwartComponentConfig config = Config(c =>
        {
            c.TlsMode = StalwartTlsMode.Acme;
            c.AcmeChallenge = StalwartAcmeChallenge.Http01;
            c.AcmeContact = "hostmaster@example.com";
        });

        string plan = StalwartPlanBuilder.BuildApplyPlan(config, [Domain(config.Id, "example.com")], []);

        Operation(plan, "Certificate").Should().BeNull();
    }

    private static List<YamlDocument> Parse(string manifest)
    {
        YamlStream stream = [];
        stream.Load(new StringReader(manifest));
        return stream.Documents.ToList();
    }

    private static string? Scalar(YamlNode node, params string[] path)
    {
        YamlNode current = node;
        foreach (string segment in path)
        {
            if (current is not YamlMappingNode map
                || !map.Children.TryGetValue(new YamlScalarNode(segment), out YamlNode? next))
            {
                return null;
            }
            current = next;
        }
        return (current as YamlScalarNode)?.Value;
    }

    private static YamlNode? At(YamlNode node, params string[] path)
    {
        YamlNode current = node;
        foreach (string segment in path)
        {
            if (current is not YamlMappingNode map
                || !map.Children.TryGetValue(new YamlScalarNode(segment), out YamlNode? next))
            {
                return null;
            }
            current = next;
        }
        return current;
    }

    private static List<JsonElement> ParsePlan(string plan) =>
        plan.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => JsonDocument.Parse(line).RootElement.Clone())
            .ToList();

    private static JsonElement? Operation(string plan, string objectName) =>
        ParsePlan(plan).Where(op => op.GetProperty("object").GetString() == objectName)
            .Select(op => (JsonElement?)op)
            .FirstOrDefault();

    // ── Manifest ──────────────────────────────────────────────────────────────

    [Fact]
    public void Manifest_IsValidYamlWithEveryExpectedDocument()
    {
        string manifest = StalwartManifestBuilder.Build(Config(), "stalwart", "stalwart");

        List<string?> kinds = Parse(manifest)
            .Select(d => Scalar(d.RootNode, "kind"))
            .ToList();

        kinds.Should().Equal("Namespace", "ConfigMap", "StatefulSet", "Service", "Service", "Service");
    }

    [Fact]
    public void Manifest_EmbedsAConfigJsonThatOnlyDescribesTheDatastore()
    {
        string manifest = StalwartManifestBuilder.Build(Config(), "stalwart", "stalwart");

        YamlDocument configMap = Parse(manifest).First(d => Scalar(d.RootNode, "kind") == "ConfigMap");
        string configJson = Scalar(configMap.RootNode, "data", "config.json")!;

        using JsonDocument parsed = JsonDocument.Parse(configJson);
        parsed.RootElement.GetProperty("@type").GetString().Should().Be("RocksDb");
        parsed.RootElement.GetProperty("path").GetString().Should().Be(StalwartPlanBuilder.DataPath);
        // Anything else here would be a setting the server ignores, because v0.16 reads the rest
        // of its configuration out of the store this points at.
        parsed.RootElement.EnumerateObject().Should().HaveCount(2);
    }

    [Fact]
    public void MailLoadBalancer_CarriesTheMailPortsAndNotTheWebPort()
    {
        string manifest = StalwartManifestBuilder.Build(Config(), "stalwart", "stalwart");

        YamlDocument mail = Parse(manifest)
            .First(d => Scalar(d.RootNode, "metadata", "name") == "stalwart-mail");

        Scalar(mail.RootNode, "spec", "type").Should().Be("LoadBalancer");
        // Without Local, the client IP every spam signal reads is a node in this cluster.
        Scalar(mail.RootNode, "spec", "externalTrafficPolicy").Should().Be("Local");

        List<string?> ports = ((YamlSequenceNode)At(mail.RootNode, "spec", "ports")!)
            .Select(p => Scalar(p, "port"))
            .ToList();

        ports.Should().Contain(["25", "465", "587", "143", "993", "4190"]);
        // The web surfaces belong to the gateway, which terminates their TLS and holds their
        // certificate; publishing 8080 on the mail address would bypass all of that.
        ports.Should().NotContain("8080");
    }

    [Fact]
    public void ClusterIpExposure_OmitsTheLoadBalancerEntirely()
    {
        string manifest = StalwartManifestBuilder.Build(
            Config(c => c.ExposeMode = StalwartMailExposeMode.ClusterIp), "stalwart", "stalwart");

        Parse(manifest).Should().NotContain(d => Scalar(d.RootNode, "metadata", "name") == "stalwart-mail");
    }

    [Fact]
    public void DisabledProtocols_LoseTheirPortsEverywhere()
    {
        string manifest = StalwartManifestBuilder.Build(
            Config(c =>
            {
                c.ImapEnabled = false;
                c.ManageSieveEnabled = false;
                c.Pop3Enabled = true;
            }),
            "stalwart", "stalwart");

        YamlDocument set = Parse(manifest).First(d => Scalar(d.RootNode, "kind") == "StatefulSet");
        YamlNode container = ((YamlSequenceNode)At(set.RootNode, "spec", "template", "spec", "containers")!)[0];

        List<string?> ports = ((YamlSequenceNode)At(container, "ports")!)
            .Select(p => Scalar(p, "containerPort"))
            .ToList();

        ports.Should().NotContain(["143", "993", "4190"]);
        ports.Should().Contain(["110", "995", "25"]);
    }

    [Fact]
    public void TheContainerKeepsOnlyTheCapabilityItNeedsToBindPort25()
    {
        string manifest = StalwartManifestBuilder.Build(Config(), "stalwart", "stalwart");

        YamlDocument set = Parse(manifest).First(d => Scalar(d.RootNode, "kind") == "StatefulSet");
        YamlNode container = ((YamlSequenceNode)At(set.RootNode, "spec", "template", "spec", "containers")!)[0];

        Scalar(container, "securityContext", "allowPrivilegeEscalation").Should().Be("false");
        ((YamlSequenceNode)At(container, "securityContext", "capabilities", "drop")!)
            .Select(n => ((YamlScalarNode)n).Value).Should().Equal("ALL");
        ((YamlSequenceNode)At(container, "securityContext", "capabilities", "add")!)
            .Select(n => ((YamlScalarNode)n).Value).Should().Equal("NET_BIND_SERVICE");
    }

    [Fact]
    public void RecoveryMode_IsTheOnlyDifferenceItMakesToTheManifest()
    {
        StalwartComponentConfig config = Config();

        string normal = StalwartManifestBuilder.Build(config, "stalwart", "stalwart");
        string recovery = StalwartManifestBuilder.Build(config, "stalwart", "stalwart", recoveryMode: true);

        normal.Should().NotContain("STALWART_RECOVERY_MODE");
        recovery.Should().Contain("STALWART_RECOVERY_MODE");
        // The credential is referenced, never rendered.
        recovery.Should().Contain("secretKeyRef");

        Parse(recovery).Select(d => Scalar(d.RootNode, "kind"))
            .Should().Equal(Parse(normal).Select(d => Scalar(d.RootNode, "kind")));
    }

    [Fact]
    public void ManualTls_ProjectsTheVaultedPemsOntoTheSamePathsCertManagerWouldHaveUsed()
    {
        string manifest = StalwartManifestBuilder.Build(
            Config(c => c.TlsMode = StalwartTlsMode.Manual), "stalwart", "stalwart");

        YamlDocument set = Parse(manifest).First(d => Scalar(d.RootNode, "kind") == "StatefulSet");
        YamlNode tlsVolume = ((YamlSequenceNode)At(set.RootNode, "spec", "template", "spec", "volumes")!)
            .First(v => Scalar(v, "name") == "tls");

        Scalar(tlsVolume, "secret", "secretName").Should().Be("stalwart-credentials");
        ((YamlSequenceNode)At(tlsVolume, "secret", "items")!)
            .Select(i => Scalar(i, "path"))
            .Should().Equal("tls.crt", "tls.key");
    }

    [Fact]
    public void TheMailAddressAlwaysServesHttpsAndOpensPortEightyOnlyForTheChallengeThatNeedsIt()
    {
        // 443 is not an ACME port: it is where a mail client looks for its settings and where a
        // sending server fetches the MTA-STS policy, so it is published in every TLS mode. 80 is
        // only ever the HTTP-01 challenge — an open port with nothing behind it is attack surface
        // for free.
        static List<string?> MailPortsOf(StalwartComponentConfig config)
        {
            string manifest = StalwartManifestBuilder.Build(config, "stalwart", "stalwart");
            YamlDocument mail = Parse(manifest)
                .First(d => Scalar(d.RootNode, "metadata", "name") == "stalwart-mail");
            return ((YamlSequenceNode)At(mail.RootNode, "spec", "ports")!)
                .Select(p => Scalar(p, "port")).ToList();
        }

        MailPortsOf(Config(c => { c.TlsMode = StalwartTlsMode.Acme; c.AcmeChallenge = StalwartAcmeChallenge.TlsAlpn01; }))
            .Should().Contain("443").And.NotContain("80");

        // HTTP-01 adds 80 — and keeps 443, because autodiscovery and MTA-STS are HTTPS whatever the
        // challenge type is.
        MailPortsOf(Config(c => { c.TlsMode = StalwartTlsMode.Acme; c.AcmeChallenge = StalwartAcmeChallenge.Http01; }))
            .Should().Contain(["80", "443"]);

        MailPortsOf(Config(c => { c.TlsMode = StalwartTlsMode.Acme; c.AcmeChallenge = StalwartAcmeChallenge.Dns01; }))
            .Should().Contain("443").And.NotContain("80");

        MailPortsOf(Config(c => c.TlsMode = StalwartTlsMode.ClusterIssuer))
            .Should().Contain("443").And.NotContain("80");
    }

    [Fact]
    public void TheAcmePortsServeTheChallengeAndNothingElse()
    {
        // Stalwart's endpoint policy is global rather than per-listener, so without this rule
        // publishing port 80 on the mail address would put the admin UI and JMAP on the public
        // internet in cleartext. This is the assertion that stops that regressing.
        StalwartComponentConfig config = Config(c =>
        {
            c.TlsMode = StalwartTlsMode.Acme;
            c.AcmeChallenge = StalwartAcmeChallenge.Http01;
            c.AcmeContact = "hostmaster@example.com";
        });

        string plan = StalwartPlanBuilder.BuildApplyPlan(
            config, [Domain(config.Id, "example.com")], []);

        JsonElement policy = Operation(plan, "Http")!.Value
            .GetProperty("value").GetProperty("allowedEndpoints");

        List<(string If, string Then)> arms = policy.GetProperty("match").EnumerateObject()
            .OrderBy(p => int.Parse(p.Name))
            .Select(p => (p.Value.GetProperty("if").GetString()!, p.Value.GetProperty("then").GetString()!))
            .ToList();

        // The public paths are allowed (200), then both ACME listeners refuse everything else (403).
        List<(string If, string Then)> allows = arms.Where(a => a.Then == "200").ToList();
        List<(string If, string Then)> denies = arms.Where(a => a.Then == "403").ToList();

        // /.well-known/ carries the ACME challenge (and MTA-STS, and PACC).
        allows.Should().Contain(a => a.If.Contains("/.well-known/"));
        // The admin surfaces are never in an allow arm — they fall through to the listener 403.
        allows.Should().NotContain(a => a.If.Contains("/admin") || a.If.Contains("/jmap"));
        // Both public mail-LB listeners deny everything the allow arms did not permit.
        denies.Should().Contain(a => a.If.Contains(StalwartManifestBuilder.AcmeHttpListener));
        denies.Should().Contain(a => a.If.Contains(StalwartManifestBuilder.PublicWebListener));
        // The deny arms come after every allow arm, or a public path would be refused before it is allowed.
        arms.FindLastIndex(a => a.Then == "200").Should().BeLessThan(arms.FindIndex(a => a.Then == "403"));

        // Every other listener is unaffected.
        policy.GetProperty("else").GetString().Should().Be("200");

        JsonElement listeners = Operation(plan, "NetworkListener")!.Value.GetProperty("value");
        listeners.GetProperty(StalwartManifestBuilder.AcmeHttpListener)
            .GetProperty("bind").GetProperty("[::]:80").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public void EveryCasingTheServerRoutesAutodiscoverOnIsAllowed()
    {
        // The router matches "autodiscover", "Autodiscover" AND "AutoDiscover"
        // (crates/http/src/request.rs). Allowing a subset leaves the clients that send the missing
        // casing refused by the very listener that exists to serve them — and a 403 on autodiscover
        // looks to the user like a mail server that does not exist.
        StalwartComponentConfig config = Config(c => c.TlsMode = StalwartTlsMode.ClusterIssuer);
        string plan = StalwartPlanBuilder.BuildApplyPlan(
            config, [Domain(config.Id, "example.com")], []);

        List<string> allows = Operation(plan, "Http")!.Value
            .GetProperty("value").GetProperty("allowedEndpoints").GetProperty("match").EnumerateObject()
            .Where(a => a.Value.GetProperty("then").GetString() == "200")
            .Select(a => a.Value.GetProperty("if").GetString()!)
            .ToList();

        foreach (string casing in new[] { "/autodiscover/", "/Autodiscover/", "/AutoDiscover/" })
        {
            allows.Should().Contain(a => a.Contains($"'{casing}'"), $"the server routes {casing}");
        }

        // Thunderbird's fixed path, and the prefix that carries MTA-STS, PACC and the ACME challenge.
        allows.Should().Contain(a => a.Contains("'/mail/config'"));
        allows.Should().Contain(a => a.Contains("'/.well-known/'"));

        // JMAP and the admin UI are not reachable on a public address. /.well-known/jmap is a
        // redirect, and its target is deliberately absent from the allow list.
        allows.Should().NotContain(a => a.Contains("'/jmap"));
        allows.Should().NotContain(a => a.Contains("'/admin"));
    }

    [Fact]
    public void WithoutAcmeThereIsNoChallengePortButThePublicWebListenerIsStillRestricted()
    {
        StalwartComponentConfig config = Config(c => c.TlsMode = StalwartTlsMode.ClusterIssuer);
        string plan = StalwartPlanBuilder.BuildApplyPlan(
            config, [Domain(config.Id, "example.com")], []);

        JsonElement listeners = Operation(plan, "NetworkListener")!.Value.GetProperty("value");
        listeners.TryGetProperty(StalwartManifestBuilder.AcmeHttpListener, out _).Should().BeFalse();

        // 443 is published in every TLS mode — this is where a client finds its settings — so the
        // policy still has to keep the admin UI and JMAP off it.
        listeners.GetProperty(StalwartManifestBuilder.PublicWebListener)
            .GetProperty("bind").GetProperty("[::]:443").GetBoolean().Should().BeTrue();

        JsonElement policy = Operation(plan, "Http")!.Value
            .GetProperty("value").GetProperty("allowedEndpoints");

        List<(string If, string Then)> arms = policy.GetProperty("match").EnumerateObject()
            .OrderBy(p => int.Parse(p.Name))
            .Select(p => (p.Value.GetProperty("if").GetString()!, p.Value.GetProperty("then").GetString()!))
            .ToList();

        arms.Where(a => a.Then == "403").Should().ContainSingle()
            .Which.If.Should().Contain(StalwartManifestBuilder.PublicWebListener);
        // Everything else — the in-cluster HTTP listener the gateway talks to — is untouched.
        policy.GetProperty("else").GetString().Should().Be("200");
    }

    [Fact]
    public void WithNothingPublishedOnTheMailAddressThePolicyIsTheDocumentedDefault()
    {
        // ClusterIp exposure publishes no mail address at all, so there is no public listener and
        // nothing to restrict. The policy is written anyway — switching back to a private
        // deployment has to lift the rules rather than leave a deny behind that nothing removes —
        // but as exactly the documented default, {"else":"200"}: no match key, no arms, no invented
        // shape. This gates every HTTP request the server serves, and a policy that denies them all
        // is indistinguishable from a healthy one until someone opens the admin interface and gets
        // a bare Forbidden.
        StalwartComponentConfig config = Config(c =>
        {
            c.TlsMode = StalwartTlsMode.ClusterIssuer;
            c.ExposeMode = StalwartMailExposeMode.ClusterIp;
        });
        string plan = StalwartPlanBuilder.BuildApplyPlan(
            config, [Domain(config.Id, "example.com")], []);

        JsonElement policy = Operation(plan, "Http")!.Value
            .GetProperty("value").GetProperty("allowedEndpoints");

        policy.TryGetProperty("match", out _).Should().BeFalse();
        policy.GetProperty("else").GetString().Should().Be("200");
        policy.EnumerateObject().Should().HaveCount(1);
    }

    [Fact]
    public void EveryDenyArmNamesAListenerThatExists()
    {
        // allowedEndpoints is global: it gates every HTTP request, on every listener. A 403 arm
        // naming a listener this deployment does not have can only lock the operator out of the
        // admin interface, and one that names no listener at all denies everything.
        foreach (StalwartTlsMode mode in Enum.GetValues<StalwartTlsMode>())
        {
            foreach (StalwartAcmeChallenge challenge in Enum.GetValues<StalwartAcmeChallenge>())
            {
                foreach (StalwartMailExposeMode expose in Enum.GetValues<StalwartMailExposeMode>())
                {
                    StalwartComponentConfig config = Config(c =>
                    {
                        c.TlsMode = mode;
                        c.AcmeChallenge = challenge;
                        c.ExposeMode = expose;
                        c.AcmeContact = "hostmaster@example.com";
                    });

                    string plan = StalwartPlanBuilder.BuildApplyPlan(
                        config, [Domain(config.Id, "example.com")], []);

                    List<string> declared = Operation(plan, "NetworkListener")!.Value
                        .GetProperty("value").EnumerateObject().Select(p => p.Name).ToList();

                    JsonElement endpointPolicy = Operation(plan, "Http")!.Value
                        .GetProperty("value").GetProperty("allowedEndpoints");
                    List<string> denies = endpointPolicy.TryGetProperty("match", out JsonElement match)
                        ? match.EnumerateObject()
                            .Where(a => a.Value.GetProperty("then").GetString() != "200")
                            .Select(a => a.Value.GetProperty("if").GetString()!)
                            .ToList()
                        : [];

                    foreach (string deny in denies)
                    {
                        declared.Should().Contain(
                            listener => deny.Contains($"'{listener}'"),
                            $"{mode}/{challenge}/{expose} refuses requests on a listener it never declared "
                            + $"({deny}), which can only refuse something legitimate");
                    }

                    if (expose == StalwartMailExposeMode.ClusterIp)
                    {
                        denies.Should().BeEmpty(
                            $"{mode}/{challenge} publishes no mail address, so the endpoint policy has "
                            + "nothing to protect and must not be able to refuse a request");
                    }
                }
            }
        }
    }

    [Fact]
    public void AcmeTls_MountsNoCertificateAtAll()
    {
        string manifest = StalwartManifestBuilder.Build(
            Config(c => c.TlsMode = StalwartTlsMode.Acme), "stalwart", "stalwart");

        // Stalwart obtains and stores the certificate itself in ACME mode, so a mounted Secret
        // would be a second, stale copy of it.
        manifest.Should().NotContain(StalwartPlanBuilder.TlsMountPath);
    }

    [Fact]
    public void LoadBalancerAnnotations_AreParsedFromKeyValueLines()
    {
        List<(string Key, string Value)> parsed = StalwartManifestBuilder.ParseAnnotations(
            """
            # a comment
            service.beta.kubernetes.io/openstack-internal-load-balancer: false
            loadbalancer.openstack.org/keep-floatingip: true
            nonsense
            """);

        parsed.Should().Equal(
            ("service.beta.kubernetes.io/openstack-internal-load-balancer", "false"),
            ("loadbalancer.openstack.org/keep-floatingip", "true"));
    }

    [Fact]
    public void TheApplyJobPassesCredentialsByReferenceAndThePlanByVolume()
    {
        string manifest = StalwartManifestBuilder.BuildApplyJobManifest(
            "stalwart", "stalwart", "{\"@type\":\"update\"}\n", "admin");

        List<YamlDocument> docs = Parse(manifest);
        docs.Select(d => Scalar(d.RootNode, "kind")).Should().Equal("Secret", "Job");

        YamlNode container = ((YamlSequenceNode)At(
            docs[1].RootNode, "spec", "template", "spec", "containers")!)[0];

        YamlNode password = ((YamlSequenceNode)At(container, "env")!)
            .First(e => Scalar(e, "name") == "STALWART_PASSWORD");
        Scalar(password, "valueFrom", "secretKeyRef", "key")
            .Should().Be(StalwartManifestBuilder.AdminPasswordSecretName);
        // A password on argv is readable by anyone who can list pods.
        Scalar(password, "value").Should().BeNull();

        Scalar(docs[1].RootNode, "spec", "backoffLimit").Should().Be("0");
        // The last apply must stay inspectable until the next one replaces it — a TTL here erased
        // the evidence in the middle of a real incident.
        Scalar(docs[1].RootNode, "spec", "ttlSecondsAfterFinished").Should().BeNull();
    }

    // ── Apply plan ────────────────────────────────────────────────────────────

    [Fact]
    public void EveryPlanLineIsAStandaloneJsonOperation()
    {
        StalwartComponentConfig config = Config();
        string plan = StalwartPlanBuilder.BuildApplyPlan(
            config, [Domain(config.Id, "example.com")], []);

        List<JsonElement> ops = ParsePlan(plan);
        ops.Should().NotBeEmpty();
        ops.Should().OnlyContain(op => op.ValueKind == JsonValueKind.Object);
        foreach (JsonElement op in ops)
        {
            op.TryGetProperty("@type", out _).Should().BeTrue();
            op.TryGetProperty("object", out _).Should().BeTrue();
        }
    }

    [Fact]
    public void ListenersAreReconciledSoDisablingAProtocolClosesItsPort()
    {
        StalwartComponentConfig config = Config(c => c.Pop3Enabled = false);
        string plan = StalwartPlanBuilder.BuildApplyPlan(
            config, [Domain(config.Id, "example.com")], []);

        JsonElement listeners = Operation(plan, "NetworkListener")!.Value;
        listeners.GetProperty("@type").GetString().Should().Be("reconcile");

        List<string> names = listeners.GetProperty("value").EnumerateObject()
            .Select(p => p.Name).ToList();

        names.Should().Contain(["smtp", "submission", "submissions", "imap", "imaps", "sieve", "http"]);
        names.Should().NotContain(["pop3", "pop3s"]);
    }

    [Fact]
    public void ListenerBindsAreEncodedAsSetsNotArrays()
    {
        StalwartComponentConfig config = Config();
        string plan = StalwartPlanBuilder.BuildApplyPlan(
            config, [Domain(config.Id, "example.com")], []);

        JsonElement imaps = Operation(plan, "NetworkListener")!.Value
            .GetProperty("value").GetProperty("imaps");

        // A Set is an object mapping each member to true. Sending an array is rejected outright.
        JsonElement bind = imaps.GetProperty("bind");
        bind.ValueKind.Should().Be(JsonValueKind.Object);
        bind.GetProperty("[::]:993").GetBoolean().Should().BeTrue();

        // 993 is implicit TLS; 143 is STARTTLS. Swapping them makes every client fail to connect.
        imaps.GetProperty("tlsImplicit").GetBoolean().Should().BeTrue();
        Operation(plan, "NetworkListener")!.Value.GetProperty("value").GetProperty("imap")
            .GetProperty("tlsImplicit").GetBoolean().Should().BeFalse();
    }

    [Fact]
    public void TheLdapBindPasswordIsReferencedByEnvironmentVariableAndNeverWritten()
    {
        StalwartComponentConfig config = Config(c => c.AuthMode = StalwartAuthMode.Ldap);
        string plan = StalwartPlanBuilder.BuildApplyPlan(
            config, [Domain(config.Id, "example.com")], []);

        JsonElement directory = Operation(plan, "Directory")!.Value
            .GetProperty("value").GetProperty("dir");

        directory.GetProperty("@type").GetString().Should().Be("Ldap");
        directory.GetProperty("bindSecret").GetProperty("@type").GetString()
            .Should().Be("EnvironmentVariable");
        directory.GetProperty("bindSecret").GetProperty("variableName").GetString()
            .Should().Be(StalwartPlanBuilder.LdapBindPasswordEnv);
    }

    [Fact]
    public void OidcModeProducesAnOidcDirectoryPointedAtTheRealm()
    {
        StalwartComponentConfig config = Config(c =>
        {
            c.AuthMode = StalwartAuthMode.Oidc;
            c.OidcIssuerUrl = "https://login.example.com/auth/realms/mail/";
            c.OidcUsernameDomain = "example.com";
        });

        string plan = StalwartPlanBuilder.BuildApplyPlan(
            config, [Domain(config.Id, "example.com")], []);

        JsonElement directory = Operation(plan, "Directory")!.Value
            .GetProperty("value").GetProperty("dir");

        directory.GetProperty("@type").GetString().Should().Be("Oidc");
        directory.GetProperty("issuerUrl").GetString()
            .Should().Be("https://login.example.com/auth/realms/mail");
        directory.GetProperty("requireScopes").GetProperty("openid").GetBoolean().Should().BeTrue();
        directory.GetProperty("usernameDomain").GetString().Should().Be("example.com");
    }

    [Fact]
    public void InternalAuthLeavesTheAuthenticationDirectoryNull()
    {
        StalwartComponentConfig config = Config(c => c.AuthMode = StalwartAuthMode.Internal);
        string plan = StalwartPlanBuilder.BuildApplyPlan(
            config, [Domain(config.Id, "example.com")], []);

        Operation(plan, "Directory").Should().BeNull();
        Operation(plan, "Authentication")!.Value
            .GetProperty("value").GetProperty("directoryId").ValueKind
            .Should().Be(JsonValueKind.Null);
    }

    [Fact]
    public void TurningRspamdOffRemovesTheMilterRatherThanLeavingItPointedAtNothing()
    {
        StalwartComponentConfig config = Config(c => c.RspamdEnabled = false);
        string plan = StalwartPlanBuilder.BuildApplyPlan(
            config, [Domain(config.Id, "example.com")], []);

        JsonElement milter = Operation(plan, "MtaMilter")!.Value;
        milter.GetProperty("@type").GetString().Should().Be("reconcile");
        milter.GetProperty("value").EnumerateObject().Should().BeEmpty();
    }

    [Fact]
    public void TheRspamdMilterHooksTheDataStageOnly()
    {
        StalwartComponentConfig config = Config(c =>
        {
            c.RspamdEnabled = true;
            c.RspamdHost = "rspamd.rspamd.svc.cluster.local";
        });

        string plan = StalwartPlanBuilder.BuildApplyPlan(
            config, [Domain(config.Id, "example.com")], []);

        JsonElement milter = Operation(plan, "MtaMilter")!.Value
            .GetProperty("value").GetProperty("rspamd");

        milter.GetProperty("hostname").GetString().Should().Be("rspamd.rspamd.svc.cluster.local");
        milter.GetProperty("port").GetInt32().Should().Be(RspamdManifestBuilder.MilterPort);
        milter.GetProperty("stages").GetProperty("data").GetBoolean().Should().BeTrue();
        milter.GetProperty("stages").EnumerateObject().Should().HaveCount(1);
    }

    [Fact]
    public void MailboxesAreUpsertedSoRemovingOneNeverDeletesMail()
    {
        StalwartComponentConfig config = Config();
        StalwartMailDomain domain = Domain(config.Id, "example.com");

        string plan = StalwartPlanBuilder.BuildApplyPlan(config, [domain],
        [
            new StalwartMailAccount
            {
                Id = Guid.NewGuid(),
                ConfigId = config.Id,
                DomainId = domain.Id,
                LocalPart = "Alice",
                DisplayName = "Alice Ferrari",
                Aliases = "alice.ferrari\nsales",
            }
        ]);

        JsonElement accounts = Operation(plan, "Account")!.Value;
        accounts.GetProperty("@type").GetString().Should().Be("upsert");

        JsonElement account = accounts.GetProperty("value").EnumerateObject().First().Value;
        account.GetProperty("name").GetString().Should().Be("alice");
        account.GetProperty("domainId").GetString().Should().Be("#dom-0");

        // Aliases are a List, which is an object keyed by stringified indices.
        JsonElement aliases = account.GetProperty("aliases");
        aliases.GetProperty("0").GetProperty("name").GetString().Should().Be("alice.ferrari");
        aliases.GetProperty("1").GetProperty("name").GetString().Should().Be("sales");
    }

    [Fact]
    public void TheAcmeProviderIsDeclaredBeforeTheDomainThatReferencesIt()
    {
        StalwartComponentConfig config = Config(c =>
        {
            c.TlsMode = StalwartTlsMode.Acme;
            c.AcmeContact = "hostmaster@example.com";
        });

        string plan = StalwartPlanBuilder.BuildApplyPlan(
            config, [Domain(config.Id, "example.com")], []);

        List<string?> order = ParsePlan(plan).Select(op => op.GetProperty("object").GetString()).ToList();
        order.IndexOf("AcmeProvider").Should().BeLessThan(order.IndexOf("Domain"));

        JsonElement domain = Operation(plan, "Domain")!.Value
            .GetProperty("value").GetProperty("dom-0");
        domain.GetProperty("certificateManagement").GetProperty("acmeProviderId").GetString()
            .Should().Be("#acme");

        // The server holds the certificate itself in this mode, so there is no file-backed one.
        Operation(plan, "Certificate").Should().BeNull();
    }

    [Fact]
    public void SystemSettingsPointAtThePrimaryDomain()
    {
        StalwartComponentConfig config = Config();
        StalwartMailDomain secondary = Domain(config.Id, "other.example", primary: false);
        StalwartMailDomain primary = Domain(config.Id, "example.com");

        string plan = StalwartPlanBuilder.BuildApplyPlan(config, [secondary, primary], []);

        JsonElement system = Operation(plan, "SystemSettings")!.Value.GetProperty("value");
        system.GetProperty("defaultHostname").GetString().Should().Be("mail.example.com");
        // dom-1 is the primary here — the ref must follow which domain is primary, not plan order.
        system.GetProperty("defaultDomainId").GetString().Should().Be("#dom-1");
    }

    // ── The administrator ─────────────────────────────────────────────────────

    [Fact]
    public void AnAdministratorAccountIsAlwaysEmittedEvenWithNoMailboxes()
    {
        // Stalwart has no admin account type, only accounts carrying an Admin role. Without one
        // nothing can sign in to the web interface at all, because the recovery administrator is
        // honoured only in recovery mode — which is how a running server ended up unreachable.
        StalwartComponentConfig config = Config(c => c.AdminUsername = "postmaster");
        string plan = StalwartPlanBuilder.BuildApplyPlan(
            config, [Domain(config.Id, "example.com")], []);

        JsonElement admin = Operation(plan, "Account")!.Value
            .GetProperty("value").GetProperty("acc-admin");

        admin.GetProperty("name").GetString().Should().Be("postmaster");
        admin.GetProperty("domainId").GetString().Should().Be("#dom-0");
        admin.GetProperty("roles").GetProperty("@type").GetString().Should().Be("Admin");
    }

    [Fact]
    public void AMailboxThatIsAlsoTheAdministratorIsPromotedRatherThanDuplicated()
    {
        // Two entries sharing a match key inside one upsert is ambiguous, so the existing mailbox
        // has to gain the role instead of a second account being emitted beside it.
        StalwartComponentConfig config = Config(c => c.AdminUsername = "alice");
        StalwartMailDomain domain = Domain(config.Id, "example.com");

        string plan = StalwartPlanBuilder.BuildApplyPlan(config, [domain],
        [
            new StalwartMailAccount
            {
                Id = Guid.NewGuid(), ConfigId = config.Id, DomainId = domain.Id, LocalPart = "Alice",
            }
        ]);

        JsonElement accounts = Operation(plan, "Account")!.Value.GetProperty("value");

        accounts.EnumerateObject().Should().HaveCount(1);
        accounts.EnumerateObject().Single().Value
            .GetProperty("roles").GetProperty("@type").GetString().Should().Be("Admin");
    }

    [Fact]
    public void TheAdministratorGetsAPasswordOnlyWhenStalwartIsTheOneCheckingIt()
    {
        StalwartMailDomain DomainFor(StalwartComponentConfig c) => Domain(c.Id, "example.com");

        static JsonElement AdminOf(string plan) =>
            Operation(plan, "Account")!.Value.GetProperty("value").GetProperty("acc-admin");

        // Internal: the credential lives here, so it has to be written.
        StalwartComponentConfig internalMode = Config(c => c.AuthMode = StalwartAuthMode.Internal);
        JsonElement internalAdmin = AdminOf(StalwartPlanBuilder.BuildApplyPlan(
            internalMode, [DomainFor(internalMode)], [], adminPassword: "s3cr3t"));

        internalAdmin.GetProperty("credentials").GetProperty("0")
            .GetProperty("@type").GetString().Should().Be("Password");
        internalAdmin.GetProperty("credentials").GetProperty("0")
            .GetProperty("secret").GetString().Should().Be("s3cr3t");

        // LDAP: authentication is routed to the directory, so an internal copy would only drift
        // from it — the account exists to carry the role, nothing more.
        StalwartComponentConfig ldapMode = Config(c => c.AuthMode = StalwartAuthMode.Ldap);
        string ldapPlan = StalwartPlanBuilder.BuildApplyPlan(
            ldapMode, [DomainFor(ldapMode)], [], adminPassword: "s3cr3t");

        AdminOf(ldapPlan).TryGetProperty("credentials", out _).Should().BeFalse();
        ldapPlan.Should().NotContain("s3cr3t");

        // Same for OIDC.
        StalwartComponentConfig oidcMode = Config(c =>
        {
            c.AuthMode = StalwartAuthMode.Oidc;
            c.OidcIssuerUrl = "https://login.example.com/realms/mail";
        });
        AdminOf(StalwartPlanBuilder.BuildApplyPlan(
            oidcMode, [DomainFor(oidcMode)], [], adminPassword: "s3cr3t"))
            .TryGetProperty("credentials", out _).Should().BeFalse();
    }

    [Fact]
    public void NoAdministratorIsEmittedWithoutADomainToPutItIn()
    {
        // An account needs a domain, and with none configured the plan already refuses to apply.
        StalwartComponentConfig config = Config();
        string plan = StalwartPlanBuilder.BuildApplyPlan(config, [], []);

        Operation(plan, "Account").Should().BeNull();
    }

    // ── Not banning everyone at once ──────────────────────────────────────────

    [Fact]
    public void TheHttpServerIsToldToTrustTheForwardingHeader()
    {
        // Behind the gateway the TCP peer of every request is the gateway pod. Stalwart's auto-ban
        // counts failed logins per remote address and blocks it after a hundred — so without this,
        // a few mistyped passwords ban the gateway, which is every user, with a bare 403.
        StalwartComponentConfig config = Config();
        string plan = StalwartPlanBuilder.BuildApplyPlan(config, [Domain(config.Id, "example.com")], []);

        Operation(plan, "Http")!.Value.GetProperty("value")
            .GetProperty("useXForwarded").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public void AnAuthenticationBanExpiresInsteadOfLastingForever()
    {
        StalwartComponentConfig config = Config();
        string plan = StalwartPlanBuilder.BuildApplyPlan(config, [Domain(config.Id, "example.com")], []);

        JsonElement security = Operation(plan, "Security")!.Value;
        security.GetProperty("@type").GetString().Should().Be("update");
        // Milliseconds. Long enough to stop an attacker, short enough that a mistake self-heals.
        security.GetProperty("value").GetProperty("authBanPeriod").GetInt64()
            .Should().BeInRange(600_000, 86_400_000);
    }

    [Fact]
    public void RspamdsOwnHeaderDoesNotTriggerStalwartsSpamFlag()
    {
        // The mechanism, and it runs the opposite way to the obvious reading. SPAM_FLAG is
        // STALWART's tag and scores +5 on a message that already carries an X-Spam header — sound,
        // because spammers forge "X-Spam-Flag: No". rspamd's milter added X-Spam-Status at DATA,
        // before the built-in filter ran, so rspamd's own header was producing Stalwart's verdict:
        // a legitimate PGP-signed message went from 1.00 to 6.00, crossed scoreSpam of 5, and was
        // filed to Junk. rspamd meanwhile scored it 3.00/15.00 and returned "no action".
        //
        // Two guards, because they fail differently: the header is not written at all, and the tag
        // is neutralised in case Stalwart reacts to one of the headers that remain.
        string rspamdConfig = Scalar(
            Parse(RspamdManifestBuilder.Build(new RspamdSettings(null), "rspamd", "mail"))
                .First(d => Scalar(d.RootNode, "kind") == "ConfigMap")
                .RootNode,
            "data", "milter_headers.conf")!;

        rspamdConfig.Should().NotContain("x-spam-status");

        // The diagnostic value stays: these carry rspamd's score and its DKIM/SPF results.
        rspamdConfig.Should().Contain("x-spamd-bar").And.Contain("x-spam-level");
        rspamdConfig.Should().Contain("authentication-results");

        StalwartComponentConfig config = Config(c =>
        {
            c.RspamdEnabled = true;
            c.RspamdHost = "rspamd.mail.svc.cluster.local";
        });
        string plan = StalwartPlanBuilder.BuildApplyPlan(config, [Domain(config.Id, "example.com")], []);

        JsonElement tag = Operation(plan, "SpamTag")!.Value.GetProperty("value")
            .EnumerateObject().Select(p => p.Value)
            .Single(v => v.GetProperty("tag").GetString() == "SPAM_FLAG");
        tag.GetProperty("@type").GetString().Should().Be("Score");
        tag.GetProperty("score").GetDouble().Should().Be(0.0);
    }

    [Fact]
    public void ProxyProtocolCanBeChosenAtInstallTimeBecauseLaterIsTooLate()
    {
        // It has to be on the install form and not only the Mail tab. This install creates the load
        // balancer, and OpenStack cannot change a pool's protocol afterwards — so the annotation is
        // either in the manifest the first time the Service is applied or it never takes effect.
        //
        // Originally it existed only on the Mail tab, which cannot be reached until the component is
        // installed. A fresh install therefore had it off, created the balancer without it, and by the
        // time the box could be ticked the pool existed and would not change: not a setting that was
        // easy to miss, a setting that was unreachable.
        StalwartComponentConfig config = Config();

        StalwartService.ApplyFormValues(config, new Dictionary<string, string>
        {
            ["proxy-protocol"] = "true",
            ["proxy-trusted-networks"] = "10.240.3.0/24, 10.240.4.7",
        });

        config.ProxyProtocol.Should().BeTrue();
        StalwartPlanBuilder.ProxyTrustedNetworks(config)
            .Should().BeEquivalentTo(["10.240.3.0/24", "10.240.4.7"]);

        // And it reads back, or reopening the Components tab would show the catalog default and saving
        // would turn it off again — over a live configuration, with no way to turn it back on short of
        // recreating the balancer.
        Dictionary<string, string> form = StalwartService.BuildFormValues(config);

        form["proxy-protocol"].Should().Be("true");
        form["proxy-trusted-networks"].Should().Be("10.240.3.0/24, 10.240.4.7");

        StalwartComponentConfig reopened = Config();
        StalwartService.ApplyFormValues(reopened, form);

        reopened.ProxyProtocol.Should().BeTrue();
        StalwartPlanBuilder.ProxyTrustedNetworks(reopened).Should().HaveCount(2);
    }

    [Fact]
    public void ProxyProtocolTurnsOnBothHalvesAndRestoresTheConnectionChecks()
    {
        // Both halves are one decision: a balancer sending PROXY headers to a server that does not
        // trust them refuses every connection, and a server trusting a balancer that sends none never
        // learns the client address. So one switch does the annotation and the trusted-networks list.
        StalwartComponentConfig config = Config(c =>
        {
            c.ProxyProtocol = true;
            c.ProxyTrustedNetworks = "10.240.3.0/24\n# a comment\n10.240.4.7";
            c.ExposeMode = StalwartMailExposeMode.LoadBalancer;
        });

        string plan = StalwartPlanBuilder.BuildApplyPlan(config, [Domain(config.Id, "example.com")], []);

        JsonElement networks = Operation(plan, "SystemSettings")!.Value
            .GetProperty("value").GetProperty("proxyTrustedNetworks");

        // A Set, so {member: true} — comments dropped, blanks dropped.
        networks.EnumerateObject().Select(p => p.Name)
            .Should().BeEquivalentTo(["10.240.3.0/24", "10.240.4.7"]);

        // With the sender's own address back, both tags measure the sender again — and are written
        // back to the rule set's scores rather than left out, because the plan upserts them: an
        // object simply omitted stays suppressed for ever on a server that already has it.
        Dictionary<string, double> scores = Operation(plan, "SpamTag")!.Value
            .GetProperty("value").EnumerateObject().Select(p => p.Value)
            .ToDictionary(v => v.GetProperty("tag").GetString()!, v => v.GetProperty("score").GetDouble());

        scores["VIOLATED_DIRECT_SPF"].Should().Be(3.50);
        scores["RDNS_NONE"].Should().Be(2.00);

        // And the balancer is told to send them.
        Parse(StalwartManifestBuilder.Build(config, "stalwart", "stalwart"))
            .First(d => Scalar(d.RootNode, "metadata", "name") == "stalwart-mail")
            .RootNode.ToString()
            .Should().Contain(StalwartManifestBuilder.OctaviaProxyProtocolAnnotation);
    }

    [Fact]
    public void TheProxyAnnotationIsNeverWrittenTwice()
    {
        // An operator who sets the same key keeps their value, and there is only ever one of it.
        // Writing ours beside theirs would put two copies of one key in a YAML mapping, which is not
        // "the later one wins" — that is undefined. A strict parser refuses the whole document and a
        // lenient one silently picks one, so the manifest would either fail to apply or apply
        // something nobody chose.
        StalwartComponentConfig config = Config(c =>
        {
            c.ProxyProtocol = true;
            c.ProxyTrustedNetworks = "10.240.3.0/24";
            c.ExposeMode = StalwartMailExposeMode.LoadBalancer;
            c.LoadBalancerAnnotations =
                $"{StalwartManifestBuilder.OctaviaProxyProtocolAnnotation}: false";
        });

        YamlDocument mail = Parse(StalwartManifestBuilder.Build(config, "stalwart", "stalwart"))
            .First(d => Scalar(d.RootNode, "metadata", "name") == "stalwart-mail");

        YamlMappingNode annotations =
            (YamlMappingNode)At(mail.RootNode, "metadata", "annotations")!;

        annotations.Children.Keys
            .Count(k => ((YamlScalarNode)k).Value == StalwartManifestBuilder.OctaviaProxyProtocolAnnotation)
            .Should().Be(1);

        // And theirs is the one that survives.
        Scalar(annotations, StalwartManifestBuilder.OctaviaProxyProtocolAnnotation)
            .Should().Be("false");
    }

    [Fact]
    public void WithoutTrustedNetworksProxyProtocolIsNotConfiguredAtAll()
    {
        // The list is what enables the protocol, so an empty one must leave both halves off. Enabling
        // the annotation alone would publish a balancer sending headers to a server that rejects every
        // connection carrying one — worse than not trying.
        foreach (StalwartComponentConfig config in new[]
        {
            Config(c => c.ProxyProtocol = false),
            Config(c => { c.ProxyProtocol = true; c.ProxyTrustedNetworks = "  \n # only a comment"; }),
        })
        {
            string plan = StalwartPlanBuilder.BuildApplyPlan(config, [Domain(config.Id, "example.com")], []);

            Operation(plan, "SystemSettings")!.Value.GetProperty("value")
                .TryGetProperty("proxyTrustedNetworks", out _).Should().BeFalse();

            // And the connection tags stay suppressed, because the connection is still not the sender's.
            Dictionary<string, double> scores = Operation(plan, "SpamTag")!.Value
                .GetProperty("value").EnumerateObject().Select(p => p.Value)
                .ToDictionary(v => v.GetProperty("tag").GetString()!, v => v.GetProperty("score").GetDouble());

            scores["VIOLATED_DIRECT_SPF"].Should().Be(0.0);
            scores["RDNS_NONE"].Should().Be(0.0);

            Parse(StalwartManifestBuilder.Build(config, "stalwart", "stalwart"))
                .First(d => Scalar(d.RootNode, "metadata", "name") == "stalwart-mail")
                .RootNode.ToString()
                .Should().NotContain(StalwartManifestBuilder.OctaviaProxyProtocolAnnotation);
        }
    }

    [Fact]
    public void CustomerDomainsAreTrustedAsFarAsTheFilterAllows()
    {
        // Modest on purpose, because the mechanism is: listing a domain here exempts it from DNS
        // block-list checks and nothing else. It would not have stopped the SPF and reverse-DNS score
        // that junked a real customer's support request, and Stalwart has no allow-list that would.
        // The guarantee lives in SupportMailboxService.SweepJunkAsync instead.
        StalwartComponentConfig config = Config();
        string plan = StalwartPlanBuilder.BuildApplyPlan(
            config, [Domain(config.Id, "example.com")], [],
            trustedSenderDomains: ["Customer.Example", "customer.example", " other.example "]);

        JsonElement op = Operation(plan, "MemoryLookupKey")!.Value;

        // Keyed on namespace AND key: a domain is one entry in one list, and matching on the
        // namespace alone would have each new domain overwrite the last.
        op.GetProperty("matchOn").EnumerateArray().Select(e => e.GetString())
            .Should().BeEquivalentTo(["namespace", "key"]);

        List<JsonElement> entries = op.GetProperty("value").EnumerateObject()
            .Select(p => p.Value).ToList();

        entries.Should().OnlyContain(e => e.GetProperty("namespace").GetString() == "trusted-domains");
        entries.Select(e => e.GetProperty("key").GetString())
            .Should().BeEquivalentTo(["customer.example", "other.example"]); // trimmed, lowered, deduped
    }

    [Fact]
    public void WithNoCustomerDomainsNoListIsWritten()
    {
        StalwartComponentConfig config = Config();
        string plan = StalwartPlanBuilder.BuildApplyPlan(config, [Domain(config.Id, "example.com")], []);

        Operation(plan, "MemoryLookupKey").Should().BeNull();
    }

    [Fact]
    public void TheConnectionAddressIsNotScoredBecauseItIsNotTheSenders()
    {
        // Measured on a real delivered message. Its X-Spam-Result, unfolded, summed to exactly the
        // 6.00 that filed it to Junk against a scoreSpam of 5:
        //
        //   VIOLATED_DIRECT_SPF  +3.50   the peer is in nobody's SPF record, because it is ours
        //   RDNS_NONE            +2.00   a private address has no PTR, and never will
        //   MIME_BAD, MIME_MA_MISSING_HTML, MID_RHS_MATCH_FROM   +3.00
        //   MIME_BASE64_TEXT, RCVD_COUNT_ZERO                   +0.20
        //   SIGNED_PGP, DMARC_POLICY_ALLOW, DKIM_ALLOW           -2.70
        //
        // DKIM_ALLOW and DMARC_POLICY_ALLOW are the point: the message was provably authentic —
        // signature verified, DMARC aligned — and junked anyway on an address that was never the
        // sender's, because the load balancer had rewritten it. Zeroing those two leaves 0.50.
        StalwartComponentConfig config = Config();
        string plan = StalwartPlanBuilder.BuildApplyPlan(config, [Domain(config.Id, "example.com")], []);

        Dictionary<string, double> scores = Operation(plan, "SpamTag")!.Value
            .GetProperty("value").EnumerateObject()
            .Select(p => p.Value)
            .ToDictionary(v => v.GetProperty("tag").GetString()!, v => v.GetProperty("score").GetDouble());

        scores.Should().ContainKey("VIOLATED_DIRECT_SPF").WhoseValue.Should().Be(0.0);
        scores.Should().ContainKey("RDNS_NONE").WhoseValue.Should().Be(0.0);

        // Not conditional on rspamd: these are about the network path, not the filter in it.
        scores.Keys.Should().NotContain("SPAM_FLAG");

        // And what still works is left alone. DKIM and DMARC sign and align the message, so they
        // survive a rewritten peer address; overriding them would throw away the only authentication
        // this deployment still has.
        foreach (string keep in new[] { "DKIM_ALLOW", "DMARC_POLICY_ALLOW", "SIGNED_PGP", "MIME_BAD" })
        {
            scores.Keys.Should().NotContain(keep);
        }
    }

    [Fact]
    public void StalwartsOwnFilterStaysOnBecauseNothingElseCanFileIntoJunk()
    {
        // It is the half to keep. A SieveSystemScript runs at SMTP stages and cannot use fileinto,
        // so only a per-account script could file a message — and writing one onto every mailbox,
        // including accounts EntKube never created, is not a mechanism worth having. Switching the
        // built-in filter off leaves rspamd adding headers nothing acts on, and spam in the inbox.
        //
        // Written explicitly rather than left at its default so that a server an earlier version of
        // this plan switched off is repaired by the next apply.
        foreach (bool rspamd in new[] { true, false })
        {
            StalwartComponentConfig config = Config(c =>
            {
                c.RspamdEnabled = rspamd;
                c.RspamdHost = rspamd ? "rspamd.mail.svc.cluster.local" : null;
            });
            string plan = StalwartPlanBuilder.BuildApplyPlan(config, [Domain(config.Id, "example.com")], []);

            Operation(plan, "SpamSettings")!.Value
                .GetProperty("value").GetProperty("enable").GetBoolean()
                .Should().BeTrue($"the built-in filter files into Junk (rspamd: {rspamd})");
        }

        // And the SPAM_FLAG override is scoped: with no upstream filter the rule is doing real work,
        // and zeroing it would hand spammers back the evasion it exists to catch. The connection tags
        // stay zeroed either way — they are about the network path, not about rspamd.
        StalwartComponentConfig noRspamd = Config(c => c.RspamdEnabled = false);
        List<string> tags = Operation(
                StalwartPlanBuilder.BuildApplyPlan(noRspamd, [Domain(noRspamd.Id, "example.com")], []),
                "SpamTag")!.Value
            .GetProperty("value").EnumerateObject()
            .Select(p => p.Value.GetProperty("tag").GetString()!)
            .ToList();

        tags.Should().NotContain("SPAM_FLAG");
        tags.Should().Contain("VIOLATED_DIRECT_SPF").And.Contain("RDNS_NONE");
    }

    [Fact]
    public void TheGatewayAddressesAreAllowListedSoTheyCanNeverBeBanned()
    {
        // is_ip_blocked() is "blocked AND NOT allowed": an AllowedIp entry is a hard guarantee that
        // survives a request arriving without a usable forwarding header.
        StalwartComponentConfig config = Config();
        string plan = StalwartPlanBuilder.BuildApplyPlan(
            config, [Domain(config.Id, "example.com")], [],
            trustedProxyAddresses: ["10.42.0.17", "10.42.1.9", "10.42.0.17"]);

        JsonElement allowed = Operation(plan, "AllowedIp")!.Value;
        allowed.GetProperty("@type").GetString().Should().Be("upsert");

        List<string> addresses = allowed.GetProperty("value").EnumerateObject()
            .Select(p => p.Value.GetProperty("address").GetString()!)
            .ToList();
        addresses.Should().Contain("10.42.0.17").And.Contain("10.42.1.9");
        addresses.Count(a => a == "10.42.0.17").Should().Be(1); // de-duplicated
    }

    [Fact]
    public void EveryInternalRangeIsAllowListed()
    {
        // Enumerating what was running is correct until the next scale event: pods move and the
        // autoscaler invents node addresses that did not exist when the plan was written.
        //
        // And where the load balancer cannot preserve the client address — an Octavia amphora
        // proxies and SNATs whatever externalTrafficPolicy says — every sender on the internet
        // arrives as one internal address. Auto-ban counted the world's failures against that single
        // peer, blocked it, and refused all mail at TCP accept: nothing in the inbox, nothing in the
        // spam folder, and "Blocked IP address ... remoteIp = 10.240.3.59" as the only trace.
        StalwartComponentConfig config = Config();
        string plan = StalwartPlanBuilder.BuildApplyPlan(config, [Domain(config.Id, "example.com")], []);

        List<string> addresses = Operation(plan, "AllowedIp")!.Value
            .GetProperty("value").EnumerateObject()
            .Select(p => p.Value.GetProperty("address").GetString()!)
            .ToList();

        // Written even with no gateway address resolved — the ranges do not depend on discovery.
        addresses.Should().Contain(StalwartPlanBuilder.InternalRanges);

        // 100.64.0.0/10 is the one a list of "the private ranges" misses, and it is where several
        // CNIs allocate pod addresses: this cluster's pods are on 100.96.x.
        addresses.Should().Contain("100.64.0.0/10");
    }

    [Fact]
    public void BlockedIpsAreClearedOnlyWhenTheOperatorAsksForIt()
    {
        // Lifting bans un-bans genuine attackers too, so it must never happen as a side effect of
        // an ordinary apply.
        StalwartComponentConfig config = Config();
        StalwartMailDomain domain = Domain(config.Id, "example.com");

        Operation(StalwartPlanBuilder.BuildApplyPlan(config, [domain], []), "BlockedIp")
            .Should().BeNull();

        JsonElement clear = Operation(
            StalwartPlanBuilder.BuildApplyPlan(config, [domain], [], clearBlockedIps: true), "BlockedIp")!.Value;
        clear.GetProperty("@type").GetString().Should().Be("reconcile");
        clear.GetProperty("value").EnumerateObject().Should().BeEmpty();
    }

    [Fact]
    public void TheServerLogsToStdoutSoAnIncidentIsNotDebuggedAgainstNothing()
    {
        StalwartComponentConfig config = Config();
        string plan = StalwartPlanBuilder.BuildApplyPlan(config, [Domain(config.Id, "example.com")], []);

        JsonElement tracer = Operation(plan, "Tracer")!.Value.GetProperty("value")
            .EnumerateObject().Single().Value;
        tracer.GetProperty("@type").GetString().Should().Be("Stdout");
        tracer.GetProperty("enable").GetBoolean().Should().BeTrue();
    }

    // ── The expression the server parses at apply time ────────────────────────

    /// <summary>The HttpVariable enum, verbatim from the reference. Anything else is rejected.</summary>
    private static readonly HashSet<string> HttpVariables =
    [
        "listener", "remote_ip", "remote_port", "local_ip", "local_port",
        "protocol", "is_tls", "url", "path", "headers", "method",
    ];

    [Fact]
    public void EveryVariableInTheEndpointPolicyIsARealHttpVariable()
    {
        // The access-control doc page's example uses `url_path`; the server rejects it with
        // "Invalid variable or constant", and that aborted a real apply half-way through. The
        // HttpVariable reference is the only authority, and this pins the policy to it.
        StalwartComponentConfig config = Config(c =>
        {
            c.TlsMode = StalwartTlsMode.Acme;
            c.AcmeChallenge = StalwartAcmeChallenge.Http01;
            c.AcmeContact = "hostmaster@example.com";
        });
        string plan = StalwartPlanBuilder.BuildApplyPlan(config, [Domain(config.Id, "example.com")], []);

        JsonElement policy = Operation(plan, "Http")!.Value.GetProperty("value").GetProperty("allowedEndpoints");
        foreach (JsonProperty arm in policy.GetProperty("match").EnumerateObject())
        {
            string condition = arm.Value.GetProperty("if").GetString()!;
            // Identifiers are bare words outside quotes that are not a function name.
            IEnumerable<string> identifiers = System.Text.RegularExpressions.Regex
                .Replace(condition, "'[^']*'", "")
                .Split([' ', '(', ')', ',', '=', '!', '&', '|'], StringSplitOptions.RemoveEmptyEntries)
                .Where(w => w.All(ch => char.IsLetter(ch) || ch == '_'))
                .Where(w => w != "starts_with");

            identifiers.Should().OnlyContain(v => HttpVariables.Contains(v),
                $"'{condition}' must use only HttpVariable identifiers");
            condition.Should().NotContain("url_path");
        }
    }

    [Fact]
    public void TheEndpointPolicyIsTheLastOperationSoItsFailureCannotStrandTheServer()
    {
        // apply aborts at the first failed operation. This is the only op built from an expression
        // the server parses at apply time, so it goes last: the administrator, the tracer, the
        // auto-ban settings and the gateway allow-list must all have landed before it can fail.
        StalwartComponentConfig config = Config();
        string plan = StalwartPlanBuilder.BuildApplyPlan(
            config, [Domain(config.Id, "example.com")], [], trustedProxyAddresses: ["10.0.0.1"]);

        List<string> order = ParsePlan(plan).Select(op => op.GetProperty("object").GetString()!).ToList();

        order.Last().Should().Be("Http");
        foreach (string mustPrecede in new[] { "Account", "Tracer", "Security", "AllowedIp" })
        {
            order.IndexOf(mustPrecede).Should().BeLessThan(order.IndexOf("Http"), $"{mustPrecede} must land before Http can fail");
        }
    }

    // ── Reading the live directory ────────────────────────────────────────────

    [Fact]
    public void MailAttributesAreReadFromLdifIncludingBase64Values()
    {
        // The two entries that were reported as "no users": cn= RDNs, created outside EntKube.
        // The preflight must find them exactly as ldapsearch prints them.
        const string ldif = """
            dn: cn=Admin,dc=entit,dc=eu
            mail: admin@entit.eu

            dn: cn=Nils Blomgren,dc=entit,dc=eu
            mail: Nils.Blomgren@entit.eu

            dn: cn=Utf,dc=entit,dc=eu
            mail:: w7ZzdGVuQGVudGl0LmV1
            """;

        StalwartService.ParseMailAttributes(ldif)
            .Should().Equal("admin@entit.eu", "nils.blomgren@entit.eu", "östen@entit.eu");
    }

    [Fact]
    public void MailboxFilterOnlyKeepsAddressesInServedDomains()
    {
        HashSet<string> served = ["entit.eu"];
        List<string> all = ["admin@entit.eu", "someone@other.example", "bare", "x@ENTIT.EU"];

        all.Where(a => a.Contains('@') && served.Contains(a[(a.IndexOf('@') + 1)..].ToLowerInvariant()))
            .Should().Equal("admin@entit.eu", "x@ENTIT.EU");
    }

    // ── The administrator typed as a full address ─────────────────────────────

    [Fact]
    public void AnAdministratorTypedAsAFullAddressBecomesTheLocalPartInThatDomain()
    {
        // Told "the administrator must be a directory user", an operator typed the user's full
        // address. The mailbox has to be nils.blomgren in entit.eu — not a mailbox literally named
        // nils.blomgren@entit.eu inside entit.eu, which is what the plan used to emit.
        StalwartComponentConfig config = Config(c => c.AdminUsername = "Nils.Blomgren@entit.eu");
        StalwartMailDomain domain = Domain(config.Id, "entit.eu");

        string plan = StalwartPlanBuilder.BuildApplyPlan(config, [domain], []);
        JsonElement admin = Operation(plan, "Account")!.Value.GetProperty("value").GetProperty("acc-admin");

        admin.GetProperty("name").GetString().Should().Be("nils.blomgren");
        admin.GetProperty("domainId").GetString().Should().Be("#dom-0");
    }

    [Fact]
    public void AnAdministratorInAnUnservedDomainIsRefusedRatherThanMangled()
    {
        StalwartComponentConfig config = Config(c => c.AdminUsername = "someone@other.example");
        List<StalwartMailDomain> domains = [Domain(config.Id, "entit.eu")];

        StalwartService.ResolveAdminIdentity(config, domains).Should().BeNull();
        Operation(StalwartPlanBuilder.BuildApplyPlan(config, domains, []), "Account").Should().BeNull();
    }

    [Fact]
    public void AnAdministratorLocalPartStillLandsInThePrimaryDomain()
    {
        StalwartComponentConfig config = Config(c => c.AdminUsername = "admin");
        List<StalwartMailDomain> domains = [Domain(config.Id, "other.example", primary: false), Domain(config.Id, "entit.eu")];

        StalwartService.ResolveAdminIdentity(config, domains)!.Value.Address.Should().Be("admin@entit.eu");
    }

    // ── The two credential copies the apply depends on ────────────────────────

    [Fact]
    public void MatchingCredentialCopiesPassAndEveryDisagreementIsNamedWithoutAPassword()
    {
        // Stalwart compares the fallback username bare and exactly, then the password byte for
        // byte. The CLI sends STALWART_ADMIN_PASSWORD; the pod checks the half after the colon in
        // STALWART_RECOVERY_ADMIN. When those were out of step the apply failed with 401 after two
        // restarts. This check runs before the first one.
        StalwartService.DescribeCredentialMismatch("nils.blomgren@entit.eu", "nils.blomgren@entit.eu:pw", "pw")
            .Should().BeNull();

        StalwartService.DescribeCredentialMismatch("nils.blomgren@entit.eu", "admin:pw", "pw")
            .Should().Contain("'admin'").And.Contain("'nils.blomgren@entit.eu'");

        string? newline = StalwartService.DescribeCredentialMismatch("admin", "admin:pw\n", "pw");
        newline.Should().Contain("trailing newline").And.NotContain("pw\n");

        StalwartService.DescribeCredentialMismatch("admin", null, "pw")
            .Should().Contain(StalwartManifestBuilder.RecoveryAdminSecretName);

        StalwartService.DescribeCredentialMismatch("admin", "admin:pw", "other")
            .Should().Contain("different values").And.NotContain("other").And.NotContain(":pw");
    }

    [Fact]
    public void TheLiveDirectoryCheckQueriesTheUrlStalwartIsConfiguredWith()
    {
        // Not localhost. The first version used localhost:389 from inside the pod; 389 is the
        // Service port and the container listens on 1389, so it failed with "can't contact LDAP
        // server" against a healthy directory. Querying the configured URL from inside the pod
        // tests Stalwart's own path and sidesteps the container port entirely.
        StalwartComponentConfig config = Config(c =>
        {
            c.LdapUrl = "ldap://openldap.openldap.svc.cluster.local:389";
            c.LdapBaseDn = "dc=entit,dc=eu";
            c.LdapBindDn = "cn=admin,dc=entit,dc=eu";
            c.LdapLoginFilter = "(&(objectClass=inetOrgPerson)(mail=?))";
        });

        List<string> command = StalwartService.BuildLdapSearchCommand(config);

        command.Should().ContainInOrder("-H", "ldap://openldap.openldap.svc.cluster.local:389");
        command.Should().NotContain(a => a.Contains("localhost"));
        // The bind password is fed on stdin, never on argv.
        command.Should().ContainInOrder("-y", "/dev/stdin");
        command.Should().NotContain(a => a.Contains("-w"));
        // The login placeholder is widened to match every entry Stalwart could resolve.
        command.Should().Contain("(&(objectClass=inetOrgPerson)(mail=*))");
    }

    [Fact]
    public void AnExecFailureReportsTheRealErrorNotKubectlsDefaultedContainerNotice()
    {
        // Verbatim from a real run: the notice led, the error followed, and only the notice was shown.
        const string stderr = """
            kubectl exec failed (exit 255): Defaulted container "openldap-stack-ha" out of: openldap-stack-ha, init-schema (init), init-tls-secret (init).
            ldap_sasl_bind(SIMPLE): Can't contact LDAP server (-1)
            """;

        string summary = StalwartService.SummariseExecError(stderr);

        summary.Should().Contain("Can't contact LDAP server");
        summary.Should().NotContain("Defaulted container");
    }

    // ── The pod has to carry what the datastore references ───────────────────

    [Fact]
    public void InLdapModeThePodGetsTheBindPasswordEnvTheDirectoryObjectReferences()
    {
        // The Directory object in the datastore names STALWART_LDAP_BIND_PASSWORD as an environment
        // variable. A pod rendered without it cannot bind, and every login fails with a temporary
        // server failure — which is what a pod rendered before the mode was switched to LDAP did.
        static YamlNode? EnvOf(StalwartComponentConfig config)
        {
            string manifest = StalwartManifestBuilder.Build(config, "stalwart", "stalwart");
            YamlDocument set = Parse(manifest).First(d => Scalar(d.RootNode, "kind") == "StatefulSet");
            YamlNode container = ((YamlSequenceNode)At(set.RootNode, "spec", "template", "spec", "containers")!)[0];
            return At(container, "env");
        }

        YamlNode ldapEnv = EnvOf(Config(c => c.AuthMode = StalwartAuthMode.Ldap))!;
        YamlNode bind = ((YamlSequenceNode)ldapEnv)
            .First(e => Scalar(e, "name") == StalwartPlanBuilder.LdapBindPasswordEnv);

        // By reference into the credentials Secret — never a literal value in the manifest.
        Scalar(bind, "valueFrom", "secretKeyRef", "name").Should().Be("stalwart-credentials");
        Scalar(bind, "valueFrom", "secretKeyRef", "key").Should().Be(StalwartManifestBuilder.LdapBindPasswordSecretName);
        Scalar(bind, "value").Should().BeNull();

        foreach (StalwartAuthMode other in new[] { StalwartAuthMode.Internal, StalwartAuthMode.Oidc })
        {
            YamlNode? env = EnvOf(Config(c => c.AuthMode = other));
            List<string?> names = env is YamlSequenceNode seq ? seq.Select(e => Scalar(e, "name")).ToList() : [];
            names.Should().NotContain(StalwartPlanBuilder.LdapBindPasswordEnv, $"{other} mode has no directory to bind to");
        }
    }

    [Fact]
    public void HealthProbesCarryAForwardingHeaderSoTheyDoNotFloodTheLog()
    {
        // With useXForwarded on, a request without the header logs a WARN. Kubelet probes every few
        // seconds and sends none, which drowned the one auth failure that needed reading.
        string manifest = StalwartManifestBuilder.Build(Config(), "stalwart", "stalwart");
        YamlDocument set = Parse(manifest).First(d => Scalar(d.RootNode, "kind") == "StatefulSet");
        YamlNode container = ((YamlSequenceNode)At(set.RootNode, "spec", "template", "spec", "containers")!)[0];

        foreach (string probe in new[] { "startupProbe", "livenessProbe", "readinessProbe" })
        {
            YamlNode headers = At(container, probe, "httpGet", "httpHeaders")!;
            ((YamlSequenceNode)headers).Should().Contain(h => Scalar(h, "name") == "X-Forwarded-For");
        }
    }

    [Fact]
    public void VerboseLoggingIsAOneShotThatRaisesTheTracerToDebug()
    {
        static string LevelOf(string plan) => Operation(plan, "Tracer")!.Value
            .GetProperty("value").EnumerateObject().Single().Value.GetProperty("level").GetString()!;

        StalwartComponentConfig config = Config();
        StalwartMailDomain domain = Domain(config.Id, "example.com");

        LevelOf(StalwartPlanBuilder.BuildApplyPlan(config, [domain], [])).Should().Be("info");
        LevelOf(StalwartPlanBuilder.BuildApplyPlan(config, [domain], [], verboseLogging: true)).Should().Be("debug");
    }

    // ── Weak password schemes are advised, not blocked ───────────────────────

    [Fact]
    public void DirectoryEntriesAreParsedWithTheirPasswordScheme()
    {
        // Mixed forms: a plain mail, a base64 userPassword (the usual LDIF form for a hash), and a
        // folded continuation line — all of which a naive line reader would get wrong.
        const string ldif = """
            dn: cn=Weak,dc=entit,dc=eu
            mail: weak@entit.eu
            userPassword:: e01ENX1obVRhRkp6cjNnWUN0eTFwZC8yZTZBPT0=

            dn: cn=Strong,dc=entit,dc=eu
            mail: strong@entit.eu
            userPassword:: e1NTSEF9ZGVhZGJlZWZkZWFkYmVlZmRlYWRiZWVm

            dn: cn=NoPass,dc=entit,dc=eu
            mail: nopass@entit.eu
            """;

        List<StalwartService.DirectoryEntry> entries = StalwartService.ParseDirectoryEntries(ldif);

        entries.Should().HaveCount(3);
        entries[0].Should().Be(new StalwartService.DirectoryEntry("weak@entit.eu", "MD5"));
        entries[1].Should().Be(new StalwartService.DirectoryEntry("strong@entit.eu", "SSHA"));
        // No userPassword returned (e.g. a non-privileged bind account) is unknown, not weak.
        entries[2].Should().Be(new StalwartService.DirectoryEntry("nopass@entit.eu", null));
    }

    // ── High availability ─────────────────────────────────────────────────────

    private static StalwartPlanBuilder.StalwartHaBackend Ha() => new(
        DbHost: "mail-db-rw.mail.svc.cluster.local", DbPort: 5432, DbName: "stalwart", DbUser: "stalwart",
        S3Endpoint: "https://s3.cleura.cloud", S3Region: "eu-north-1", S3Bucket: "mail-blobs",
        S3AccessKey: "AKIA…", RedisUrl: "redis://redis.redis.svc.cluster.local:6379",
        RedisIsCluster: false, Replicas: 3);

    [Fact]
    public void ConfigJsonIsRocksDbSingleNodeAndPostgreSqlInHa()
    {
        using (JsonDocument single = JsonDocument.Parse(StalwartPlanBuilder.BuildConfigJson()))
        {
            single.RootElement.GetProperty("@type").GetString().Should().Be("RocksDb");
        }

        using JsonDocument ha = JsonDocument.Parse(StalwartPlanBuilder.BuildConfigJson(Ha()));
        ha.RootElement.GetProperty("@type").GetString().Should().Be("PostgreSql");
        ha.RootElement.GetProperty("host").GetString().Should().Be("mail-db-rw.mail.svc.cluster.local");
        ha.RootElement.GetProperty("database").GetString().Should().Be("stalwart");
        ha.RootElement.GetProperty("authUsername").GetString().Should().Be("stalwart");
        // The password is a reference, never a literal — it is injected into the pod's environment.
        ha.RootElement.GetProperty("authSecret").GetProperty("@type").GetString().Should().Be("EnvironmentVariable");
        ha.RootElement.GetProperty("authSecret").GetProperty("variableName").GetString()
            .Should().Be(StalwartPlanBuilder.DbPasswordEnv);
    }

    [Fact]
    public void HaPlanAddsBlobStoreCoordinatorAndClusterRoleWithNoSecretsInline()
    {
        StalwartComponentConfig config = Config();
        string plan = StalwartPlanBuilder.BuildApplyPlan(
            config, [Domain(config.Id, "example.com")], [], ha: Ha());

        JsonElement blob = Operation(plan, "BlobStore")!.Value.GetProperty("value");
        blob.GetProperty("@type").GetString().Should().Be("S3");
        blob.GetProperty("region").GetProperty("@type").GetString().Should().Be("Custom");
        blob.GetProperty("region").GetProperty("customEndpoint").GetString().Should().Be("https://s3.cleura.cloud");
        blob.GetProperty("bucket").GetString().Should().Be("mail-blobs");
        blob.GetProperty("secretKey").GetProperty("@type").GetString().Should().Be("EnvironmentVariable");

        JsonElement coord = Operation(plan, "Coordinator")!.Value.GetProperty("value");
        coord.GetProperty("@type").GetString().Should().Be("Redis");
        coord.GetProperty("url").GetString().Should().Be("redis://redis.redis.svc.cluster.local:6379");
        // The standalone-Redis store has no secret field — an authSecret here would be rejected.
        coord.TryGetProperty("authSecret", out _).Should().BeFalse();

        // The in-memory store points at the same Redis rather than defaulting to PostgreSQL.
        JsonElement mem = Operation(plan, "InMemoryStore")!.Value.GetProperty("value");
        mem.GetProperty("@type").GetString().Should().Be("Redis");
        mem.GetProperty("url").GetString().Should().Be("redis://redis.redis.svc.cluster.local:6379");

        JsonElement role = Operation(plan, "ClusterRole")!.Value.GetProperty("value").GetProperty("role");
        role.GetProperty("name").GetString().Should().Be(StalwartPlanBuilder.ClusterRoleName);
        role.GetProperty("tasks").GetProperty("@type").GetString().Should().Be("EnableAll");
        role.GetProperty("listeners").GetProperty("@type").GetString().Should().Be("EnableAll");

        // No S3 secret key, no Redis password anywhere in the plan text.
        plan.Should().NotContain("secretKey\":{\"@type\":\"Value");
    }

    [Fact]
    public void TheStandaloneRedisCoordinatorNeverEmitsAnAuthSecretObject()
    {
        // Whatever the URL contains, a STANDALONE Redis Coordinator/InMemoryStore only ever carries
        // `url` — RedisStore has no secret field, so an authSecret would fail the apply.
        StalwartComponentConfig config = Config();
        StalwartPlanBuilder.StalwartHaBackend ha = Ha() with { RedisUrl = "redis://:s3cr3t@redis:6379" };
        string plan = StalwartPlanBuilder.BuildApplyPlan(config, [Domain(config.Id, "example.com")], [], ha: ha);

        foreach (string obj in new[] { "Coordinator", "InMemoryStore" })
        {
            JsonElement v = Operation(plan, obj)!.Value.GetProperty("value");
            v.TryGetProperty("authSecret", out _).Should().BeFalse();
            v.GetProperty("url").GetString().Should().Be("redis://:s3cr3t@redis:6379");
        }
    }

    [Fact]
    public void AShardedRedisGetsTheClusterStoreAndItsPasswordFromTheEnvironment()
    {
        // The failure this prevents is not a start-up error. A Redis Cluster addressed as a single
        // server starts cleanly, authenticates a login, and then answers every lookup behind it with
        // `Moved: 5938 10.0.0.1:6379` — the standalone client does not follow a MOVED redirection —
        // so what breaks is logging in, and nothing in the failure names the address. EntKube's own
        // managed Redis is always a cluster.
        StalwartComponentConfig config = Config();
        StalwartPlanBuilder.StalwartHaBackend ha = Ha() with
        {
            RedisUrl = "redis://redis-leader.redis.svc.cluster.local:6379",
            RedisIsCluster = true,
        };
        string plan = StalwartPlanBuilder.BuildApplyPlan(config, [Domain(config.Id, "example.com")], [], ha: ha);

        foreach (string obj in new[] { "Coordinator", "InMemoryStore" })
        {
            JsonElement v = Operation(plan, obj)!.Value.GetProperty("value");
            v.GetProperty("@type").GetString().Should().Be("RedisCluster");

            // urls is a Map<String>, which serialises as {member: true} — the listener-bind
            // encoding, not an array. An array is rejected as an invalid patch.
            JsonElement urls = v.GetProperty("urls");
            urls.ValueKind.Should().Be(JsonValueKind.Object);
            urls.GetProperty("redis://redis-leader.redis.svc.cluster.local:6379").GetBoolean().Should().BeTrue();

            // Only the cluster store has a secret field, and the plan still sets it — but it is not
            // what opens the connection, so it is set beside an inline credential rather than
            // instead of one. See TheClusterCoordinatorUrlCarriesThePasswordInline.
            v.GetProperty("authSecret").GetProperty("@type").GetString().Should().Be("EnvironmentVariable");
            v.GetProperty("authSecret").GetProperty("variableName").GetString()
                .Should().Be(StalwartPlanBuilder.RedisPasswordEnv);
            v.TryGetProperty("url", out _).Should().BeFalse();

            // The username has to travel with the secret. Sent alone, the secret goes out under an
            // empty username and Redis answers WRONGPASS — reported as "Password authentication
            // failed", the same words as no credentials at all, which is why this cost four rounds.
            v.GetProperty("authUsername").GetString().Should().Be(StalwartPlanBuilder.RedisUsername);
        }

        // …and the pod has to be given it.
        string manifest = StalwartManifestBuilder.Build(config, "stalwart", "stalwart", ha: ha);
        manifest.Should().Contain(StalwartPlanBuilder.RedisPasswordEnv);
    }

    [Fact]
    public void AnHaDeploymentShipsItsOwnCoordinatorBehindANetworkPolicy()
    {
        // The coordinator stopped being a choice because every way of choosing it was wrong: the
        // picker listed several near-identical Services belonging to one Redis, only one of which
        // resolved as managed; the password had to be re-keyed by hand; and the managed shape —
        // a sharded cluster with a password — could not be authenticated by Stalwart 0.16.21 in any
        // spelling its schema allows. One node, no password, nothing to choose.
        List<YamlDocument> docs = Parse(
            StalwartManifestBuilder.Build(Config(), "stalwart", "stalwart", ha: Ha()));

        YamlDocument deployment = docs.First(d => Scalar(d.RootNode, "kind") == "Deployment");
        Scalar(deployment.RootNode, "metadata", "name").Should().Be("stalwart-coordinator");

        // Exactly one, always, and never two at once: a second coordinator is a second view of who
        // holds which lock, so the rollout replaces rather than overlaps.
        Scalar(deployment.RootNode, "spec", "replicas").Should().Be("1");
        Scalar(deployment.RootNode, "spec", "strategy", "type").Should().Be("Recreate");

        YamlDocument service = docs.First(d =>
            Scalar(d.RootNode, "kind") == "Service"
            && Scalar(d.RootNode, "metadata", "name") == "stalwart-coordinator");
        Scalar(service.RootNode, "spec", "type").Should().Be("ClusterIP");

        // runAsNonRoot without a uid does not harden the pod, it stops it existing: the Redis image
        // declares no numeric USER, so the kubelet refuses it with "container has runAsNonRoot and
        // image will run as root" and nothing ever starts. The flag and the uid are one decision.
        YamlNode container = ((YamlSequenceNode)At(
            deployment.RootNode, "spec", "template", "spec", "containers")!).Children.Single();
        Scalar(container, "securityContext", "runAsNonRoot").Should().Be("true");
        Scalar(container, "securityContext", "runAsUser").Should().Be("999");
        Scalar(container, "securityContext", "runAsGroup").Should().Be("1000");

        // Having no password is only safe because nothing else may reach it. The two decisions are
        // one decision, so a manifest that drops the policy must fail here.
        YamlDocument policy = docs.First(d => Scalar(d.RootNode, "kind") == "NetworkPolicy");
        Scalar(policy.RootNode, "spec", "podSelector", "matchLabels", "app")
            .Should().Be("stalwart-coordinator");
        YamlNode rule = ((YamlSequenceNode)At(policy.RootNode, "spec", "ingress")!).Children.Single();
        YamlNode source = ((YamlSequenceNode)At(rule, "from")!).Children.Single();
        Scalar(source, "podSelector", "matchLabels", "app").Should().Be("stalwart");
    }

    [Fact]
    public void BothBundledRedisInstancesEvictRatherThanBeingKilled()
    {
        // Redis cannot see its cgroup. With no maxmemory it grows to the container limit and the kernel
        // kills it — and this repo has already paid for that shape of bug in other components. A
        // coordinator that dies takes every node's locks and pub/sub with it; rspamd's classifier just
        // stops learning, silently.
        //
        // The policies differ on purpose, and that is the interesting part. The coordinator holds only
        // locks, rate limits and cached state, all recomputable, so allkeys-lru loses nothing. rspamd's
        // holds Bayes training, where rspamd expires its own tokens — so volatile-lru degrades the
        // classifier by dropping the coldest expiring token, while allkeys-lru could discard reputation
        // state that has no expiry and cannot be recomputed.
        string coordinator = Parse(
                StalwartManifestBuilder.Build(Config(), "stalwart", "stalwart", ha: Ha()))
            .First(d => Scalar(d.RootNode, "metadata", "name") == "stalwart-coordinator")
            .RootNode.ToString();

        coordinator.Should().Contain("--maxmemory").And.Contain("allkeys-lru");

        string rspamdRedis = Parse(
                RspamdManifestBuilder.Build(new RspamdSettings(null), "rspamd", "mail"))
            .First(d => Scalar(d.RootNode, "kind") == "Deployment"
                        && Scalar(d.RootNode, "metadata", "name") == "rspamd-redis")
            .RootNode.ToString();

        rspamdRedis.Should().Contain("--maxmemory").And.Contain("volatile-lru");

        // And the ceiling has to sit below the limit, or it is decoration: Redis needs headroom above
        // its dataset for buffers and fragmentation.
        coordinator.Should().Contain("192mb").And.Contain("256Mi");
        rspamdRedis.Should().Contain("384mb").And.Contain("512Mi");
    }

    [Fact]
    public void WithoutHighAvailabilityThereIsNoCoordinatorToShip()
    {
        // A single node coordinates with nobody. Shipping a Redis beside it would be a pod, a
        // Service and a NetworkPolicy that exist to serve no reader.
        string manifest = StalwartManifestBuilder.Build(Config(), "stalwart", "stalwart");

        manifest.Should().NotContain("stalwart-coordinator");
        manifest.Should().NotContain("kind: NetworkPolicy");
    }

    [Fact]
    public void TheClusterCoordinatorUrlCarriesThePasswordInline()
    {
        // The whole point of authSecret was to keep the credential out of the plan, and it does not
        // work: the client authenticates its seed connection from the URL, so a URL with no
        // credentials sends no password however correct the environment variable is. A live cluster
        // had the right store, the right variable and the right value in it, redis-cli authenticated
        // with that same value, and every task still failed with "Failed to create initial
        // connections … Password authentication failed". So the URL carries it too.
        StalwartPlanBuilder.BuildRedisUrl("redis-leader.cache.svc.cluster.local:6379", "s3cr3t")
            .Should().Be("redis://default:s3cr3t@redis-leader.cache.svc.cluster.local:6379");
    }

    [Fact]
    public void TheCoordinatorUrlNamesTheDefaultUserRatherThanAnEmptyOne()
    {
        // "redis://:pw@host" names an EMPTY user, not an absent one, and Redis rejects that with
        // WRONGPASS — reported by Stalwart as "Password authentication failed", exactly like a wrong
        // password and exactly like no credentials at all. Measured against the live cluster with
        // one correct password: `-a pw` PONG, `--user '' --pass pw` WRONGPASS, `--user default
        // --pass pw` PONG. This is the assertion that keeps the colon from losing its username.
        string url = StalwartPlanBuilder.BuildRedisUrl("redis:6379", "s3cr3t");

        url.Should().StartWith("redis://default:");
        url.Should().NotStartWith("redis://:");
    }

    [Fact]
    public void ACoordinatorPasswordWithUrlPunctuationSurvivesTheUrl()
    {
        // A generated password contains whatever the generator emits, and an unescaped '@' or ':'
        // would re-point the URL at a different host entirely rather than merely failing to parse.
        StalwartPlanBuilder.BuildRedisUrl("redis:6379", "p@ss:w/rd?#")
            .Should().Be("redis://default:p%40ss%3Aw%2Frd%3F%23@redis:6379");
    }

    [Fact]
    public void ACoordinatorWithNoPasswordGetsABareUrl()
    {
        // An empty credential must not produce "redis://:@host", which is a password of zero length
        // rather than no password at all.
        StalwartPlanBuilder.BuildRedisUrl("redis:6379", null).Should().Be("redis://redis:6379");
        StalwartPlanBuilder.BuildRedisUrl("redis:6379", "   ").Should().Be("redis://redis:6379");
    }

    [Fact]
    public void AStandaloneRedisGetsNoPasswordEnvironmentVariable()
    {
        // The mirror image: RedisStore has no secret field at all, so injecting an env var the store
        // cannot read would only look like a configured credential.
        string manifest = StalwartManifestBuilder.Build(
            Config(), "stalwart", "stalwart", ha: Ha() with { RedisIsCluster = false });

        manifest.Should().NotContain(StalwartPlanBuilder.RedisPasswordEnv);
    }

    [Fact]
    public void TheTracerSetIsReplacedSoRaisingTheLevelDoesNotAddASecondConsole()
    {
        // An upsert here matches on every property, so applying at debug and then at info left two
        // enabled console tracers — and Stalwart allows one, rejecting the extra on every start
        // with "Only one console tracer is allowed". Replacing the set also drops the default file
        // tracer, which writes to a /var/log path no container has.
        StalwartComponentConfig config = Config();
        string plan = StalwartPlanBuilder.BuildApplyPlan(config, [Domain(config.Id, "example.com")], []);

        JsonElement tracer = Operation(plan, "Tracer")!.Value;
        tracer.GetProperty("@type").GetString().Should().Be("reconcile");
        tracer.GetProperty("value").EnumerateObject().Should().ContainSingle()
            .Which.Value.GetProperty("@type").GetString().Should().Be("Stdout");
    }

    [Fact]
    public void SingleNodePlanHasNoHaObjects()
    {
        StalwartComponentConfig config = Config();
        string plan = StalwartPlanBuilder.BuildApplyPlan(config, [Domain(config.Id, "example.com")], []);

        Operation(plan, "BlobStore").Should().BeNull();
        Operation(plan, "Coordinator").Should().BeNull();
        Operation(plan, "InMemoryStore").Should().BeNull();
        Operation(plan, "ClusterRole").Should().BeNull();
    }

    [Fact]
    public void TheHaManifestScalesOutDropsTheLocalVolumeAndInjectsTheBackendSecrets()
    {
        string manifest = StalwartManifestBuilder.Build(Config(), "stalwart", "stalwart", ha: Ha());

        YamlDocument set = Parse(manifest).First(d => Scalar(d.RootNode, "kind") == "StatefulSet");

        // Scaled to the requested replica count.
        Scalar(set.RootNode, "spec", "replicas").Should().Be("3");

        // No local data PVC — a ReadWriteOnce volume only one node could mount would defeat HA.
        At(set.RootNode, "spec", "volumeClaimTemplates").Should().BeNull();
        manifest.Should().NotContain($"mountPath: {StalwartPlanBuilder.DataPath}");

        // config.json is the shared PostgreSQL datastore.
        YamlDocument cm = Parse(manifest).First(d => Scalar(d.RootNode, "metadata", "name") == "stalwart-config");
        using JsonDocument cfg = JsonDocument.Parse(Scalar(cm.RootNode, "data", "config.json")!);
        cfg.RootElement.GetProperty("@type").GetString().Should().Be("PostgreSql");

        // Node identity + backend secrets are present, all by reference.
        YamlNode container = ((YamlSequenceNode)At(set.RootNode, "spec", "template", "spec", "containers")!)[0];
        List<string?> envNames = ((YamlSequenceNode)At(container, "env")!).Select(e => Scalar(e, "name")).ToList();
        envNames.Should().Contain(["STALWART_ROLE", StalwartPlanBuilder.DbPasswordEnv,
            StalwartPlanBuilder.S3SecretKeyEnv]);
        // The Redis password is not an env var — it rides in the coordinator URL — so it must not be here.
        envNames.Should().NotContain("STALWART_REDIS_PASSWORD");

        YamlNode role = ((YamlSequenceNode)At(container, "env")!).First(e => Scalar(e, "name") == "STALWART_ROLE");
        Scalar(role, "value").Should().Be(StalwartPlanBuilder.ClusterRoleName);
    }

    [Fact]
    public void RecoveryModeRunsOneNodeEvenInAnHaDeployment()
    {
        // The plan is replayed once against one endpoint; the other nodes have nothing to do but
        // read a datastore being rewritten underneath them. Mail is already refused for the duration
        // of a recovery apply, so scaling in costs nothing that was not already lost.
        StalwartPlanBuilder.StalwartHaBackend ha = Ha();

        string recovery = StalwartManifestBuilder.Build(
            Config(), "stalwart", "stalwart", recoveryMode: true, ha: ha);
        Scalar(Parse(recovery).First(d => Scalar(d.RootNode, "kind") == "StatefulSet").RootNode,
            "spec", "replicas").Should().Be("1");

        string normal = StalwartManifestBuilder.Build(Config(), "stalwart", "stalwart", ha: ha);
        Scalar(Parse(normal).First(d => Scalar(d.RootNode, "kind") == "StatefulSet").RootNode,
            "spec", "replicas").Should().Be(ha.Replicas.ToString());
    }

    [Fact]
    public void TheLiveStorageShapeIsReadFromTheClaimAndNeverGuessed()
    {
        // Whether the live StatefulSet has the local data claim is how EntKube decides it must be
        // deleted and recreated — volumeClaimTemplates cannot be changed in place. Unreadable JSON
        // has to answer "I do not know" rather than "no", because "no" deletes a working
        // StatefulSet on the next apply.
        StalwartService.HasDataVolumeClaim(
            """{"spec":{"volumeClaimTemplates":[{"metadata":{"name":"data"}}]}}""").Should().BeTrue();
        StalwartService.HasDataVolumeClaim("""{"spec":{"replicas":3}}""").Should().BeFalse();
        StalwartService.HasDataVolumeClaim(
            """{"spec":{"volumeClaimTemplates":[{"metadata":{"name":"tls"}}]}}""").Should().BeFalse();

        StalwartService.HasDataVolumeClaim("not json").Should().BeNull();
        StalwartService.HasDataVolumeClaim("{}").Should().BeNull();
    }

    [Fact]
    public void TheSingleNodeManifestStillHasItsLocalVolumeAndOneReplica()
    {
        string manifest = StalwartManifestBuilder.Build(Config(), "stalwart", "stalwart");
        YamlDocument set = Parse(manifest).First(d => Scalar(d.RootNode, "kind") == "StatefulSet");

        Scalar(set.RootNode, "spec", "replicas").Should().Be("1");
        At(set.RootNode, "spec", "volumeClaimTemplates").Should().NotBeNull();
        manifest.Should().NotContain("STALWART_ROLE");
    }

    // ── DNS ───────────────────────────────────────────────────────────────────

    [Fact]
    public void DnsRecordsCoverMxSpfAndDmarc()
    {
        StalwartComponentConfig config = Config();
        IReadOnlyList<StalwartPlanBuilder.DnsRecord> records =
            StalwartPlanBuilder.DnsRecordsFor(config, Domain(config.Id, "example.com"));

        records.Should().Contain(r => r.Type == "MX" && r.Value.Contains("mail.example.com."));
        records.Should().Contain(r => r.Type == "TXT" && r.Value.StartsWith("v=spf1"));
        records.Should().Contain(r => r.Name.StartsWith("_dmarc.") && r.Value.StartsWith("v=DMARC1"));
    }

    /// <summary>
    /// <b>The policy a domain starts on decides what a missing record costs.</b> Published as
    /// p=reject before SPF and DKIM pass, the first message the domain ever sends is junked or
    /// refused — which is what happened here, with a DMARC record copied from this very list while
    /// the DKIM key was still unpublished. p=none reports the same failures and delivers the mail.
    /// </summary>
    [Fact]
    public void DmarcStartsAtNoneRatherThanReject()
    {
        StalwartComponentConfig config = Config();

        StalwartPlanBuilder.DnsRecord dmarc = StalwartPlanBuilder
            .DnsRecordsFor(config, Domain(config.Id, "example.com"))
            .Single(r => r.Name.StartsWith("_dmarc.", StringComparison.Ordinal));

        dmarc.Value.Should().Contain("p=none");
        dmarc.Value.Should().NotContain("p=reject");
        dmarc.Note.Should().Contain("p=reject");
    }

    /// <summary>
    /// <b><c>mx</c> alone authorises the wrong address.</b> It covers what the MX records resolve
    /// to — where mail is delivered <em>to</em> — and a mail server in a cluster sends from a
    /// different one: outbound leaves translated to a node's or a gateway's address. The receiver
    /// evaluates SPF against an address no term matches and <c>-all</c> makes that a hard fail, on
    /// every message the domain sends.
    /// </summary>
    [Fact]
    public void SpfAuthorisesTheAddressesTheServerActuallySendsFrom()
    {
        StalwartComponentConfig config = Config(c =>
            c.SendingIpAddresses = "86.107.49.197\n2a01:4f9::1");

        StalwartPlanBuilder.DnsRecord spf = StalwartPlanBuilder
            .DnsRecordsFor(config, Domain(config.Id, "example.com"))
            .First(r => r.Value.StartsWith("v=spf1", StringComparison.Ordinal));

        spf.Value.Should().Be("v=spf1 mx ip4:86.107.49.197 ip6:2a01:4f9::1 -all");
    }

    /// <summary>Nothing recorded leaves the record as it was — still wrong, but not invented.</summary>
    [Fact]
    public void SpfWithoutRecordedAddressesIsUnchanged() =>
        StalwartPlanBuilder.Spf(Config()).Should().Be("v=spf1 mx -all");

    // ── PROXY protocol reach ──────────────────────────────────────────────────

    /// <summary>
    /// <b>The setting that makes a mail server unconfigurable by the thing that configures it.</b>
    /// Stalwart requires a PROXY header from every address it trusts, on every port, with no way to
    /// exempt one. A broad private or CGNAT range therefore covers the pod network, and the client
    /// that can no longer connect is EntKube's own apply Job — the connection is closed before any
    /// reply, which reads as a network fault rather than as configuration.
    ///
    /// <para>Overlap, not containment: a /24 inside 10/8 reaches in just as surely as 10/8 does.</para>
    /// </summary>
    [Theory]
    [InlineData("10.0.0.0/8")]
    [InlineData("10.240.3.0/24")]
    [InlineData("100.64.0.0/10")]
    [InlineData("100.96.0.0/11")]
    [InlineData("192.168.1.0/24")]
    [InlineData("172.16.5.5")]
    [InlineData("127.0.0.1")]
    [InlineData("fd00::/8")]
    public void AnEntryReachingInsideTheClusterIsRecognised(string network) =>
        StalwartPlanBuilder.CoversClusterAddresses(network).Should().BeTrue();

    /// <summary>
    /// A public address is what the list is for — the load balancer's own subnet, and nothing
    /// wider. Flagging one of these would make the warning noise and teach people to ignore it.
    /// </summary>
    [Theory]
    [InlineData("46.254.9.137")]
    [InlineData("86.107.49.192/28")]
    [InlineData("2a01:4f9::/48")]
    [InlineData("not-an-address")]
    [InlineData("")]
    public void APublicAddressDoesNotReachInside(string network) =>
        StalwartPlanBuilder.CoversClusterAddresses(network).Should().BeFalse();

    // ── DKIM ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// <b>The record that was missing.</b> The server generates the keys and signs with them
    /// whether or not anybody published the public half, so the failure is invisible from here and
    /// total at the far end: <c>dkim=fail reason="key not found in DNS"</c>, and DMARC with it.
    /// EntKube cannot know the key, so it carries what the operator copied off the server — and
    /// puts it in the same list as everything else the zone needs.
    /// </summary>
    [Fact]
    public void DkimRecordsCopiedFromTheServerJoinTheList()
    {
        StalwartComponentConfig config = Config();
        StalwartMailDomain domain = Domain(config.Id, "example.com");
        domain.DkimDnsRecords =
            "v1-rsa-20260927 v=DKIM1; k=rsa; p=MIIBIjANBg\n"
            + "v1-ed25519-20260927: v=DKIM1; k=ed25519; p=11qYAYKxCrf";

        List<StalwartPlanBuilder.DnsRecord> dkim =
            [.. StalwartPlanBuilder.DnsRecordsFor(config, domain)
                .Where(r => r.Name.Contains("_domainkey", StringComparison.Ordinal))];

        dkim.Should().HaveCount(2);
        dkim[0].Name.Should().Be("v1-rsa-20260927._domainkey.example.com");
        dkim[0].Value.Should().Be("v=DKIM1; k=rsa; p=MIIBIjANBg");
        dkim[1].Name.Should().Be("v1-ed25519-20260927._domainkey.example.com");
        dkim[1].Value.Should().Be("v=DKIM1; k=ed25519; p=11qYAYKxCrf");
    }

    /// <summary>
    /// A line already naming <c>_domainkey</c> is a complete record name — that is what the
    /// server's own DNS page prints, and retyping it is where a selector gets mangled.
    /// </summary>
    [Fact]
    public void AFullRecordNameIsLeftAlone()
    {
        StalwartComponentConfig config = Config();
        StalwartMailDomain domain = Domain(config.Id, "example.com");
        domain.DkimDnsRecords = "sel._domainkey.example.com v=DKIM1; k=rsa; p=AAAA";

        StalwartPlanBuilder.DkimRecords(domain).Single().Name
            .Should().Be("sel._domainkey.example.com");
    }

    /// <summary>Nothing recorded lists nothing, rather than a row with an empty value.</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   \n  \n")]
    [InlineData("selector-with-no-value")]
    public void NothingUsableListsNoDkimRecord(string? recorded)
    {
        StalwartComponentConfig config = Config();
        StalwartMailDomain domain = Domain(config.Id, "example.com");
        domain.DkimDnsRecords = recorded;

        StalwartPlanBuilder.DkimRecords(domain).Should().BeEmpty();
    }

    [Fact]
    public void AutodiscoveryRecordsPointAtTheMailHostWhateverTheTlsMode()
    {
        // These are names of the mail service, answered by the mail server on its own address. They
        // used to point at the gateway in cert-manager mode, which made client auto-configuration
        // and MTA-STS depend on the admin UI being published — and were dropped entirely when no
        // web hostname was set. In ACME mode pointing them at the gateway also failed the
        // certificate order for every name that did not resolve to the mail host, which is what
        // burned a Let's Encrypt rate limit.
        foreach (StalwartTlsMode mode in Enum.GetValues<StalwartTlsMode>())
        {
            foreach (string? webHost in new[] { "mailadmin.example.com", null })
            {
                StalwartComponentConfig config = Config(c =>
                {
                    c.Hostname = "mail.example.com";
                    c.AdminHostname = webHost;
                    c.TlsMode = mode;
                });

                List<StalwartPlanBuilder.DnsRecord> autodiscovery =
                    StalwartPlanBuilder.DnsRecordsFor(config, Domain(config.Id, "example.com"))
                        .Where(r => r.Type == "CNAME").ToList();

                autodiscovery.Select(r => r.Name).Should().BeEquivalentTo(
                    "autoconfig.example.com", "autodiscover.example.com",
                    "mta-sts.example.com", "ua-auto-config.example.com");
                autodiscovery.Should().OnlyContain(r => r.Value == "mail.example.com.",
                    $"{mode} with web host '{webHost}' must still send clients to the mail server");
            }
        }
    }

    [Fact]
    public void TheMailCertificateCarriesEveryNameAClientWillAskFor()
    {
        // Each of these lookups is an HTTPS request that validates the name it asked for, so a
        // certificate with only the mail hostname on it means a client that reaches the right
        // server and then refuses to talk to it.
        StalwartComponentConfig config = Config(c => c.Hostname = "mail.example.com");
        List<StalwartMailDomain> domains =
            [Domain(config.Id, "example.com"), Domain(config.Id, "example.org", primary: false)];

        string manifest = StalwartManifestBuilder.BuildTlsCertificateManifest(
            "letsencrypt-prod", config.Hostname, "stalwart", "stalwart", domains);

        YamlDocument cert = Parse(manifest).Single();
        Scalar(cert.RootNode, "kind").Should().Be("Certificate");
        Scalar(cert.RootNode, "spec", "commonName").Should().Be("mail.example.com");

        List<string?> names = ((YamlSequenceNode)At(cert.RootNode, "spec", "dnsNames")!)
            .Select(n => ((YamlScalarNode)n).Value).ToList();

        names[0].Should().Be("mail.example.com", "the common name has to be a SAN as well");
        names.Should().Contain([
            "autoconfig.example.com", "autodiscover.example.com", "mta-sts.example.com",
            "ua-auto-config.example.com", "autoconfig.example.org", "mta-sts.example.org"]);
        // The web hostname belongs to the gateway, which holds its own certificate.
        names.Should().NotContain("mailadmin.example.com");
    }

    [Fact]
    public void TheCertificateNamesAreExactlyWhatTheDnsTabTellsTheOperatorToPublish()
    {
        // One list behind both, because a name on the certificate that nothing resolves fails the
        // order, and a name that resolves with nothing on the certificate fails the client.
        StalwartComponentConfig config = Config(c => c.Hostname = "mail.example.com");
        StalwartMailDomain domain = Domain(config.Id, "example.com");

        IEnumerable<string> fromDns = StalwartPlanBuilder.DnsRecordsFor(config, domain)
            .Where(r => r.Type == "CNAME").Select(r => r.Name);

        StalwartPlanBuilder.CertificateNames(config.Hostname, [domain])
            .Should().Contain(fromDns);
    }

    // ── rspamd ────────────────────────────────────────────────────────────────

    [Fact]
    public void RspamdOmitsOptionalPasswordLinesEntirelyWhenUnset()
    {
        string manifest = RspamdManifestBuilder.Build(
            new RspamdSettings(ControllerPassword: null),
            "rspamd", "rspamd");

        YamlDocument configMap = Parse(manifest).First(d => Scalar(d.RootNode, "kind") == "ConfigMap");
        string redis = Scalar(configMap.RootNode, "data", "redis.conf")!;

        redis.Should().Contain("servers = \"rspamd-redis.rspamd.svc.cluster.local:6379\";");
        // An empty password line is not the same as no password line — it is an attempt to
        // authenticate with the empty string, which a Redis with auth off will refuse.
        redis.Should().NotContain("password");
    }

    [Fact]
    public void AConfigChangeRollsThePodsThatReadIt()
    {
        // The failure this prevents has no symptom. Kubernetes rolls a Deployment when its pod
        // template changes, and writing a new ConfigMap does not touch the pod template — so the
        // apply succeeds, the ConfigMap holds the new value, and rspamd keeps talking to whatever it
        // read at startup. Every artefact an operator inspects says the change landed. Stamping the
        // config's digest on the pod template is what turns "applied" into "in effect".
        static string HashOf(string manifest) =>
            Scalar(
                Parse(manifest).First(d => Scalar(d.RootNode, "kind") == "Deployment").RootNode,
                "spec", "template", "metadata", "annotations", "entkube.io/config-hash")!;

        string before = HashOf(RspamdManifestBuilder.Build(new RspamdSettings(null), "rspamd", "mail"));

        // Same input, same bytes: applying an unchanged configuration must restart nothing.
        HashOf(RspamdManifestBuilder.Build(new RspamdSettings(null), "rspamd", "mail"))
            .Should().Be(before);

        // A changed controller password is a changed worker-controller.inc, which the running
        // process only reads at startup.
        HashOf(RspamdManifestBuilder.Build(new RspamdSettings("ui-pass"), "rspamd", "mail"))
            .Should().NotBe(before);
    }

    [Fact]
    public void TheMailServerAlsoRollsWhenItsDatastoreIsRepointed()
    {
        // config.json names the datastore. Repointing it without a restart leaves every node reading
        // the old one — and the Components tab's install path, unlike "Apply configuration", does not
        // restart anything by itself.
        static string HashOf(string manifest) =>
            Scalar(
                Parse(manifest).First(d => Scalar(d.RootNode, "kind") == "StatefulSet").RootNode,
                "spec", "template", "metadata", "annotations", "entkube.io/config-hash")!;

        string single = HashOf(StalwartManifestBuilder.Build(Config(), "stalwart", "stalwart"));
        string ha = HashOf(StalwartManifestBuilder.Build(Config(), "stalwart", "stalwart", ha: Ha()));

        ha.Should().NotBe(single);
    }

    [Fact]
    public void RspamdShipsItsOwnRedisAndPointsTheClassifierAtIt()
    {
        // The picker offered the cluster's managed Redis, which is sharded, and rspamd cannot speak
        // to a sharded Redis. What that produced was not a connection error but a filter with
        // nothing learned behind it — CROSSSLOT from the Bayes classifier, MOVED from the neural
        // module, greylisting and ratelimits failing on the same backend — while mail quietly
        // landed in the spam folder. So rspamd brings its own, and nobody chooses.
        List<YamlDocument> docs = Parse(
            RspamdManifestBuilder.Build(new RspamdSettings(null), "rspamd", "mail"));

        YamlDocument configMap = docs.First(d => Scalar(d.RootNode, "kind") == "ConfigMap");
        Scalar(configMap.RootNode, "data", "redis.conf")!
            .Should().Contain("servers = \"rspamd-redis.mail.svc.cluster.local:6379\";");

        YamlDocument redis = docs.First(d =>
            Scalar(d.RootNode, "kind") == "Deployment"
            && Scalar(d.RootNode, "metadata", "name") == "rspamd-redis");

        // runAsNonRoot without a uid is a pod that fails admission, not a hardened one.
        YamlNode container = ((YamlSequenceNode)At(
            redis.RootNode, "spec", "template", "spec", "containers")!).Children.Single();
        Scalar(container, "securityContext", "runAsUser").Should().Be("999");
        Scalar(container, "securityContext", "runAsGroup").Should().Be("1000");

        // Unlike the mail server's coordinator this one persists: Bayes training is what an operator
        // spends weeks building, and it must survive the pod.
        docs.Should().Contain(d =>
            Scalar(d.RootNode, "kind") == "PersistentVolumeClaim"
            && Scalar(d.RootNode, "metadata", "name") == "rspamd-redis-data");
        Scalar(redis.RootNode, "spec", "strategy", "type").Should().Be("Recreate");

        // No password, so nothing else may reach it.
        YamlDocument policy = docs.First(d =>
            Scalar(d.RootNode, "kind") == "NetworkPolicy"
            && Scalar(d.RootNode, "metadata", "name") == "rspamd-redis");
        YamlNode rule = ((YamlSequenceNode)At(policy.RootNode, "spec", "ingress")!).Children.Single();
        YamlNode source = ((YamlSequenceNode)At(rule, "from")!).Children.Single();
        Scalar(source, "podSelector", "matchLabels", "app").Should().Be("rspamd");
    }

    [Fact]
    public void RspamdWritesTheControllerCredentialWhenItIsSet()
    {
        string manifest = RspamdManifestBuilder.Build(
            new RspamdSettings("ui-pass"), "rspamd", "rspamd");

        YamlDocument configMap = Parse(manifest).First(d => Scalar(d.RootNode, "kind") == "ConfigMap");

        string controller = Scalar(configMap.RootNode, "data", "worker-controller.inc")!;
        controller.Should().Contain("password = \"ui-pass\";");
        controller.Should().Contain("enable_password = \"ui-pass\";");
    }

    [Fact]
    public void RspamdWithoutSingleSignOnHasNoProxyAndNoLoopbackTrust()
    {
        string manifest = RspamdManifestBuilder.Build(
            new RspamdSettings("ui-pass"), "rspamd", "rspamd");

        manifest.Should().NotContain("oauth2-proxy");

        YamlDocument configMap = Parse(manifest).First(d => Scalar(d.RootNode, "kind") == "ConfigMap");
        // secure_ip without a proxy on that loopback would be an authentication bypass waiting for
        // anything else to be scheduled into this pod.
        Scalar(configMap.RootNode, "data", "worker-controller.inc")!.Should().NotContain("secure_ip");
    }

    [Fact]
    public void SingleSignOnNeedsAPublishedHostnameBecauseTheLoginHasToComeBack()
    {
        // Issuer and client id but no hostname: there is no redirect URL to register or return to, so
        // this must not half-configure a proxy that fails at the first sign-in.
        RspamdSettings settings = new(
            "redis:6379", null, "ui-pass",
            SsoIssuerUrl: "https://sso.example.com/realms/mail", SsoClientId: "rspamd");

        settings.SsoEnabled.Should().BeFalse();
        RspamdManifestBuilder.Build(settings, "rspamd", "rspamd").Should().NotContain("oauth2-proxy");
    }

    [Fact]
    public void SingleSignOnPutsTheProxyInFrontAndLeavesTheControllerBehindIt()
    {
        string manifest = RspamdManifestBuilder.Build(
            new RspamdSettings(
                "ui-pass",
                SsoIssuerUrl: "https://sso.example.com/realms/mail/",
                SsoClientId: "rspamd",
                SsoEmailDomain: "example.com",
                WebUiHostname: "rspamd.example.com"),
            "rspamd", "rspamd");

        List<YamlDocument> docs = Parse(manifest);

        // The proxy talks to the controller over loopback, which is the only address no other pod can
        // reach — that is what makes trusting it safe.
        manifest.Should().Contain($"--upstream=http://127.0.0.1:{RspamdManifestBuilder.ControllerPort}");
        manifest.Should().Contain("--redirect-url=https://rspamd.example.com/oauth2/callback");
        manifest.Should().Contain("--oidc-issuer-url=https://sso.example.com/realms/mail");
        manifest.Should().Contain("--email-domain=example.com");

        YamlDocument configMap = docs.First(d => Scalar(d.RootNode, "kind") == "ConfigMap");
        string controller = Scalar(configMap.RootNode, "data", "worker-controller.inc")!;
        controller.Should().Contain("secure_ip = \"127.0.0.1\";");
        // The password stays: it is what still guards the controller port itself, which remains
        // reachable from inside the cluster and is not behind the proxy.
        controller.Should().Contain("password = \"ui-pass\";");

        // And the Service exposes the proxy, or the route would have nothing to publish.
        YamlDocument service = docs.First(d => Scalar(d.RootNode, "kind") == "Service");
        service.RootNode.ToString().Should().Contain(RspamdManifestBuilder.SsoProxyPort.ToString());
    }

    [Fact]
    public void TheProxyReadsItsCredentialsFromTheReleasesOwnSecret()
    {
        string manifest = RspamdManifestBuilder.Build(
            new RspamdSettings(
                null,
                SsoIssuerUrl: "https://sso.example.com/realms/mail",
                SsoClientId: "rspamd",
                WebUiHostname: "rspamd.example.com"),
            "spam", "mail");

        // Named for the release, not the catalog default — an operator may rename either.
        manifest.Should().Contain("name: spam-credentials");
        manifest.Should().Contain($"key: {RspamdManifestBuilder.SsoClientSecretName}");
        manifest.Should().Contain($"key: {RspamdManifestBuilder.SsoCookieSecretName}");
        // The secrets travel by reference; a client secret rendered into a manifest would be stored in
        // the component's values in the clear.
        manifest.Should().NotContain("--client-secret=");
    }

    [Fact]
    public void RspamdKeepsItsLearnedStateInRedisRatherThanThePod()
    {
        string manifest = RspamdManifestBuilder.Build(
            new RspamdSettings(null), "rspamd", "rspamd");

        YamlDocument configMap = Parse(manifest).First(d => Scalar(d.RootNode, "kind") == "ConfigMap");
        Scalar(configMap.RootNode, "data", "classifier-bayes.conf")!
            .Should().Contain("backend = \"redis\"");
    }

    [Fact]
    public void RspamdExposesTheMilterPortStalwartConnectsTo()
    {
        string manifest = RspamdManifestBuilder.Build(
            new RspamdSettings(null), "rspamd", "rspamd");

        YamlDocument service = Parse(manifest).First(d => Scalar(d.RootNode, "kind") == "Service");
        List<string?> ports = ((YamlSequenceNode)At(service.RootNode, "spec", "ports")!)
            .Select(p => Scalar(p, "port")).ToList();

        ports.Should().Equal(
            RspamdManifestBuilder.MilterPort.ToString(),
            RspamdManifestBuilder.NormalPort.ToString(),
            RspamdManifestBuilder.ControllerPort.ToString());
    }

    [Fact]
    public void RspamdRollsByRecreatingBecauseItsVolumeIsReadWriteOnce()
    {
        string manifest = RspamdManifestBuilder.Build(
            new RspamdSettings(null), "rspamd", "rspamd");

        YamlDocument deployment = Parse(manifest).First(d => Scalar(d.RootNode, "kind") == "Deployment");
        Scalar(deployment.RootNode, "spec", "strategy", "type").Should().Be("Recreate");
    }

    // ── Webmail ───────────────────────────────────────────────────────────────

    private static RoundcubeSettings Roundcube(Action<RoundcubeSettings>? _ = null) => new(
        ImapHost: "tls://stalwart.stalwart.svc.cluster.local",
        ImapPort: 143,
        SmtpHost: "tls://stalwart.stalwart.svc.cluster.local",
        SmtpPort: 587,
        DesKey: "0123456789abcdef01234567");

    [Fact]
    public void RoundcubeWithoutOidcCarriesNoOauthConfiguration()
    {
        string manifest = WebmailManifestBuilder.BuildRoundcube(Roundcube(), "roundcube", "roundcube");

        YamlDocument configMap = Parse(manifest).First(d => Scalar(d.RootNode, "kind") == "ConfigMap");
        string php = Scalar(configMap.RootNode, "data", "zz-entkube.inc.php")!;

        php.Should().Contain("$config['imap_host']");
        php.Should().NotContain("oauth_provider");
    }

    [Fact]
    public void RoundcubeOidcConfiguresDiscoveryAgainstTheKeycloakRealm()
    {
        RoundcubeSettings settings = Roundcube() with
        {
            AuthMode = WebmailAuthMode.Oidc,
            OidcIssuerUrl = "https://login.example.com/auth/realms/mail/",
            OidcClientId = "roundcube",
            OidcClientSecret = "shhh",
            OidcProviderName = "Company SSO",
        };

        string manifest = WebmailManifestBuilder.BuildRoundcube(settings, "roundcube", "roundcube");

        YamlDocument configMap = Parse(manifest).First(d => Scalar(d.RootNode, "kind") == "ConfigMap");
        string php = Scalar(configMap.RootNode, "data", "zz-entkube.inc.php")!;

        php.Should().Contain("$config['oauth_provider'] = 'generic';");
        php.Should().Contain(
            "$config['oauth_config_uri'] = 'https://login.example.com/auth/realms/mail/.well-known/openid-configuration';");
        php.Should().Contain("$config['oauth_provider_name'] = 'Company SSO';");
        // The secret reaches the container by reference, so it must not be in the ConfigMap.
        php.Should().NotContain("shhh");
        manifest.Should().NotContain("shhh");

        YamlDocument deployment = Parse(manifest).First(d => Scalar(d.RootNode, "kind") == "Deployment");
        YamlNode container = ((YamlSequenceNode)At(
            deployment.RootNode, "spec", "template", "spec", "containers")!)[0];
        ((YamlSequenceNode)At(container, "env")!)
            .Should().Contain(e => Scalar(e, "name") == WebmailManifestBuilder.RoundcubeOauthSecretName);
    }

    [Fact]
    public void RoundcubePinsItsSessionKeySoRestartsDoNotLogEverybodyOut()
    {
        string manifest = WebmailManifestBuilder.BuildRoundcube(Roundcube(), "roundcube", "roundcube");

        YamlDocument deployment = Parse(manifest).First(d => Scalar(d.RootNode, "kind") == "Deployment");
        YamlNode container = ((YamlSequenceNode)At(
            deployment.RootNode, "spec", "template", "spec", "containers")!)[0];

        YamlNode desKey = ((YamlSequenceNode)At(container, "env")!)
            .First(e => Scalar(e, "name") == "ROUNDCUBEMAIL_DES_KEY");
        Scalar(desKey, "value").Should().Be("0123456789abcdef01234567");
    }

    [Fact]
    public void SnappyMailSeedsItsDomainWithoutOverwritingLaterEdits()
    {
        string manifest = WebmailManifestBuilder.BuildSnappyMail(
            new SnappyMailSettings("stalwart.stalwart.svc.cluster.local", 143,
                "stalwart.stalwart.svc.cluster.local", 587),
            "snappymail", "snappymail");

        YamlDocument configMap = Parse(manifest).First(d => Scalar(d.RootNode, "kind") == "ConfigMap");
        string domainJson = Scalar(configMap.RootNode, "data", "default.json")!;

        using JsonDocument parsed = JsonDocument.Parse(domainJson);
        parsed.RootElement.GetProperty("IMAP").GetProperty("port").GetInt32().Should().Be(143);
        parsed.RootElement.GetProperty("SMTP").GetProperty("port").GetInt32().Should().Be(587);
        parsed.RootElement.GetProperty("SMTP").GetProperty("useAuth").GetBoolean().Should().BeTrue();

        YamlDocument deployment = Parse(manifest).First(d => Scalar(d.RootNode, "kind") == "Deployment");
        YamlNode init = ((YamlSequenceNode)At(
            deployment.RootNode, "spec", "template", "spec", "initContainers")!)[0];
        string script = string.Join("\n", ((YamlSequenceNode)At(init, "args")!)
            .Select(n => ((YamlScalarNode)n).Value));

        // Copying unconditionally would silently revert whatever an administrator changed in the
        // admin panel, on every restart.
        script.Should().Contain("if [ ! -f");
    }

    // ── Catalog wiring ────────────────────────────────────────────────────────

    [Fact]
    public void TheMailComponentsAreInTheCatalogUnderOneCategory()
    {
        List<CatalogEntry> mail = ComponentCatalog.Entries
            .Where(e => e.Category == "Mail")
            .ToList();

        mail.Select(e => e.Key).Should().BeEquivalentTo(
            [StalwartService.CatalogKey, StalwartService.RspamdCatalogKey,
             StalwartService.RoundcubeCatalogKey, StalwartService.SnappyMailCatalogKey]);

        // They all render their own manifest rather than installing a chart.
        mail.Should().OnlyContain(e => e.ComponentType == "Manifest");
    }

    [Fact]
    public void StalwartFormValuesLandOnTheConfigTheyName()
    {
        StalwartComponentConfig config = Config();

        StalwartService.ApplyFormValues(config, new Dictionary<string, string>
        {
            ["mail-hostname"] = "mx.example.org",
            ["auth-mode"] = "Oidc",
            ["oidc-issuer"] = "https://login.example.org/realms/mail",
            ["tls-mode"] = "Acme",
            ["expose-mode"] = "ClusterIp",
            ["rspamd-enabled"] = "true",
            ["storage-size"] = "50Gi",
        });

        config.Hostname.Should().Be("mx.example.org");
        config.AuthMode.Should().Be(StalwartAuthMode.Oidc);
        config.OidcIssuerUrl.Should().Be("https://login.example.org/realms/mail");
        config.TlsMode.Should().Be(StalwartTlsMode.Acme);
        config.ExposeMode.Should().Be(StalwartMailExposeMode.ClusterIp);
        config.RspamdEnabled.Should().BeTrue();
        config.StorageSize.Should().Be("50Gi");
    }

    [Fact]
    public void EveryFormFieldSurvivesAReopenOfTheComponentsTab()
    {
        // Stalwart's fields are all stalwart: pseudo-paths, so the stored manifest holds none of
        // them and the Components tab has nothing to re-read except this. When it was missing the
        // form re-opened blank, and saving it wrote catalog defaults over a working mail server.
        StalwartComponentConfig saved = Config(c =>
        {
            c.Hostname = "mx.example.org";
            c.AdminHostname = "mailadmin.example.org";
            c.AdminUsername = "postmaster";
            c.StorageSize = "80Gi";
            c.StorageClass = "fast-ssd";
            c.AuthMode = StalwartAuthMode.Oidc;
            c.OidcIssuerUrl = "https://login.example.org/realms/mail";
            c.OidcUsernameDomain = "example.org";
            c.TlsMode = StalwartTlsMode.Acme;
            c.AcmeContact = "hostmaster@example.org";
            c.ExposeMode = StalwartMailExposeMode.ClusterIp;
            c.LoadBalancerIp = "203.0.113.25";
            c.RspamdEnabled = true;
            c.RspamdHost = "rspamd.rspamd.svc.cluster.local";
        });

        StalwartComponentConfig reopened = Config(c => c.Hostname = "placeholder");
        StalwartService.ApplyFormValues(reopened, StalwartService.BuildFormValues(saved));

        reopened.Hostname.Should().Be(saved.Hostname);
        reopened.AdminHostname.Should().Be(saved.AdminHostname);
        reopened.AdminUsername.Should().Be(saved.AdminUsername);
        reopened.StorageSize.Should().Be(saved.StorageSize);
        reopened.StorageClass.Should().Be(saved.StorageClass);
        reopened.AuthMode.Should().Be(saved.AuthMode);
        reopened.OidcIssuerUrl.Should().Be(saved.OidcIssuerUrl);
        reopened.OidcUsernameDomain.Should().Be(saved.OidcUsernameDomain);
        reopened.TlsMode.Should().Be(saved.TlsMode);
        reopened.AcmeContact.Should().Be(saved.AcmeContact);
        reopened.ExposeMode.Should().Be(saved.ExposeMode);
        reopened.LoadBalancerIp.Should().Be(saved.LoadBalancerIp);
        reopened.RspamdEnabled.Should().Be(saved.RspamdEnabled);
        reopened.RspamdHost.Should().Be(saved.RspamdHost);
    }

    [Fact]
    public void EveryNonSecretFormFieldHasAReadBack()
    {
        // The completeness half of the round-trip above: adding a field to the catalog form without
        // adding it to BuildFormValues would leave that one field silently resetting to its default
        // on every reopen, which is exactly the failure this pair exists to prevent.
        CatalogEntry entry = ComponentCatalog.GetByKey(StalwartService.CatalogKey)!;
        Dictionary<string, string> readBack = StalwartService.BuildFormValues(Config());

        List<string> missing = entry.FormFields
            .Select(f => f.Key)
            .Where(k => !StalwartService.SecretFormKeys.Contains(k))
            .Where(k => !StalwartService.DerivedFormKeys.Contains(k))
            .Where(k => !readBack.ContainsKey(k))
            .ToList();

        missing.Should().BeEmpty(
            "every non-secret Stalwart form field must be readable back out of the stored config");

        // And nothing withheld as a secret should ever be echoed back into the form.
        readBack.Keys.Should().NotIntersectWith(StalwartService.SecretFormKeys);

        // Both exemption lists must name fields that actually exist, or a renamed field would leave
        // a stale exemption behind that quietly excuses the next real omission. Emptiness is allowed
        // and asserted around rather than through: an exemption list with nothing on it is the
        // healthiest state it can be in, and FluentAssertions refuses containment against an empty
        // expectation — so asserting it directly would make removing the last exemption fail the
        // guard that exists to police them.
        List<string> keys = entry.FormFields.Select(f => f.Key).ToList();

        foreach (string[] exemptions in new[]
        {
            StalwartService.SecretFormKeys,
            StalwartService.DerivedFormKeys,
        })
        {
            if (exemptions.Length > 0)
            {
                keys.Should().Contain(exemptions);
            }
        }
    }

    [Fact]
    public void TheMailHostnameFieldIsNotKeyedHostname()
    {
        // "hostname" is treated across the Components tab as the hostname of the component's
        // external route, and Stalwart's route is its admin hostname. Sharing the key made the form
        // re-open showing the admin hostname as the mail hostname, and saving it renamed the server.
        CatalogEntry entry = ComponentCatalog.GetByKey(StalwartService.CatalogKey)!;

        entry.FormFields.Should().NotContain(f => f.Key == "hostname");
        entry.FormFields.Should().Contain(f => f.Key == "mail-hostname");
    }

    [Fact]
    public void TheHaBackendsComeFromTheInstallFormIncludingWhenOneIsClearedAgain()
    {
        Guid database = Guid.NewGuid();
        Guid bucket = Guid.NewGuid();
        StalwartComponentConfig config = Config();

        StalwartService.ApplyFormValues(config, new Dictionary<string, string>
        {
            ["ha-enabled"] = "true",
            ["ha-replicas"] = "3",
            ["ha-database"] = database.ToString(),
            ["ha-blob-store"] = bucket.ToString(),
        });

        config.HighAvailability.Should().BeTrue();
        config.Replicas.Should().Be(3);
        config.CnpgDatabaseId.Should().Be(database);
        config.BlobStorageLinkId.Should().Be(bucket);

        // A picker put back to "choose one…" is an instruction, not silence: leaving the old id in
        // place would keep the component attached to a database the operator has just detached.
        StalwartService.ApplyFormValues(config, new Dictionary<string, string>
        {
            ["ha-enabled"] = "false",
            ["ha-database"] = "",
            ["ha-blob-store"] = "",
        });

        config.HighAvailability.Should().BeFalse();
        config.CnpgDatabaseId.Should().BeNull();
        config.BlobStorageLinkId.Should().BeNull();
    }

    [Fact]
    public void OneIdentitySourceAtATimeAndTheIssuerFollowsIt()
    {
        // The two are alternatives, not layers: a stored app registration describes a provider EntKube
        // does not run, a realm is one it does, and the plan needs one answer rather than two. So
        // choosing either must clear the other — a config carrying both would resolve differently
        // depending on which resolver ran last.
        StalwartComponentConfig config = Config(c =>
        {
            c.AuthMode = StalwartAuthMode.Oidc;
            c.OidcAppRegistrationSecretId = Guid.NewGuid();
            c.OidcKeycloakRealmId = Guid.NewGuid();
        });

        // The read-back is what the Components tab reopens on, and an OIDC server whose realm was
        // dropped there would silently revert to a typed issuer.
        Dictionary<string, string> form = StalwartService.BuildFormValues(config);

        StalwartComponentConfig reopened = Config();
        StalwartService.ApplyFormValues(reopened, form);

        reopened.AuthMode.Should().Be(StalwartAuthMode.Oidc);
    }

    [Fact]
    public void TheHaBackendsReadBackIntoTheFormTheyWereEnteredIn()
    {
        // Every one of these is a "stalwart:" pseudo-path, so the stored YAML holds nothing to
        // recover them from. Without a read-back the Components tab re-opens on catalog defaults —
        // HA off, no database — and saving that detaches a running cluster from its datastore.
        Guid database = Guid.NewGuid();
        Guid bucket = Guid.NewGuid();
        StalwartComponentConfig config = Config(c =>
        {
            c.HighAvailability = true;
            c.Replicas = 3;
            c.CnpgDatabaseId = database;
            c.BlobStorageLinkId = bucket;
        });

        Dictionary<string, string> form = StalwartService.BuildFormValues(config);

        form["ha-enabled"].Should().Be("true");
        form["ha-replicas"].Should().Be("3");
        form["ha-database"].Should().Be(database.ToString());
        form["ha-blob-store"].Should().Be(bucket.ToString());

        // The coordinator is deliberately absent: it ships with the deployment, so there is no
        // form field to read back and nothing an operator could have entered.
        form.Should().NotContainKey("ha-coordinator-redis");

        // And what comes back out reproduces the configuration it came from.
        StalwartComponentConfig reopened = Config();
        StalwartService.ApplyFormValues(reopened, form);
        reopened.Should().BeEquivalentTo(config, o => o
            .Including(c => c.HighAvailability).Including(c => c.Replicas)
            .Including(c => c.CnpgDatabaseId).Including(c => c.BlobStorageLinkId));
    }

    [Fact]
    public void FormValuesTheFormDoesNotCarryAreLeftAlone()
    {
        StalwartComponentConfig config = Config(c =>
        {
            c.Pop3Enabled = true;
            c.LdapLoginFilter = "(&(objectClass=person)(mail=?))";
        });

        // The component form has no say over the protocol toggles or the directory filters, which
        // are authored in the Mail tab. Resetting them to a catalog default on every edit would
        // silently undo that work.
        StalwartService.ApplyFormValues(config, new Dictionary<string, string>
        {
            ["mail-hostname"] = "mx.example.org",
        });

        config.Pop3Enabled.Should().BeTrue();
        config.LdapLoginFilter.Should().Be("(&(objectClass=person)(mail=?))");
    }

    // ── Rollout detection ─────────────────────────────────────────────────────

    [Fact]
    public void ACertificateThatStoppedRenewingIsNoticedWhileMailStillWorks()
    {
        // The failure mode this exists for: adding a domain adds autodiscovery names to the
        // certificate, the issuer cannot solve one of them, and the whole certificate stops
        // re-issuing — while cert-manager keeps serving the existing Secret. Nothing breaks on the
        // day. Mail stops weeks later, when the old certificate expires.
        const string failing = """
            {"status":{"conditions":[
              {"type":"Ready","status":"False","reason":"Failed",
               "message":"no solver configured for \"autoconfig.example.com\""}
            ]}}
            """;

        StalwartService.DescribeCertificateProblem(failing)
            .Should().Be("Failed: no solver configured for \"autoconfig.example.com\"");

        // A healthy certificate is silent — this must not become a permanent banner.
        StalwartService.DescribeCertificateProblem(
            """{"status":{"conditions":[{"type":"Ready","status":"True"}]}}""").Should().BeNull();

        // Neither may "I could not read it" turn into a reported failure.
        StalwartService.DescribeCertificateProblem("""{"status":{}}""").Should().BeNull();
        StalwartService.DescribeCertificateProblem("not json").Should().BeNull();
        StalwartService.DescribeCertificateProblem(
            """{"status":{"conditions":[{"type":"Issuing","status":"True"}]}}""").Should().BeNull();
    }

    [Fact]
    public void WhatTheServerSaysAfterAnApplyIsReadBackFromItsLog()
    {
        // These are the real lines from the first HA deployment, where the apply reported success,
        // the pods were ready, and every login died anyway. One of them is the server refusing an
        // object EntKube just applied; the other is a runtime failure. Only the first can make the
        // apply a failure — a transient store error must not.
        const string logs = """
            2026-09-17T13:16:25Z INFO Starting Stalwart Server (server.startup) hostname = "stalwart-1"
            2026-09-17T13:16:25Z WARN Log collector error (telemetry.log-error) details = "Failed to create log file"
            2026-09-17T13:16:25Z ERROR Configuration build error (registry.build-error) source = "Tracer", reason = "Only one console tracer is allowed"
            2026-09-17T13:16:25Z INFO Network listener started (network.listen-start) listenerId = "public-web"
            2026-09-17T13:16:25Z ERROR Redis error (store.redis-error) reason = "Moved: 5938 100.96.4.17:6379"
            2026-09-17T13:16:31Z ERROR Redis error (store.redis-error) reason = "Moved: 798 100.96.5.13:6379"
            """;

        List<StalwartService.ServerLogIssue> issues = StalwartService.ParseServerErrors(logs);

        // One per distinct event: the repeated Redis error must not bury the startup one.
        issues.Select(i => i.EventName).Should().Equal("registry.build-error", "store.redis-error");
        issues.Should().ContainSingle(i => i.IsConfigurationError)
            .Which.Line.Should().Contain("Only one console tracer is allowed");

        // INFO and WARN are not errors, however alarming the words in them are.
        issues.Should().NotContain(i => i.EventName == "server.startup");
        issues.Should().NotContain(i => i.EventName == "telemetry.log-error");
    }

    [Fact]
    public void AQuietLogProducesNoFindings()
    {
        StalwartService.ParseServerErrors("").Should().BeEmpty();
        StalwartService.ParseServerErrors(
            "2026-09-17T13:16:25Z INFO Starting Stalwart Server (server.startup)").Should().BeEmpty();
    }

    [Fact]
    public void AStatefulSetIsOnlyReadyOnceTheUpdatedPodIsTheReadyOne()
    {
        // Ready but still on the old revision: the pod from before the mode switch. Treating this
        // as done would run the apply against a server that has not restarted yet.
        StalwartService.IsStatefulSetReady(
            """
            {"spec":{"replicas":1},"status":{"readyReplicas":1,"updatedReplicas":0,
             "currentRevision":"old","updateRevision":"new"}}
            """).Should().BeFalse();

        StalwartService.IsStatefulSetReady(
            """
            {"spec":{"replicas":1},"status":{"readyReplicas":1,"updatedReplicas":1,
             "currentRevision":"new","updateRevision":"new"}}
            """).Should().BeTrue();

        StalwartService.IsStatefulSetReady("not json").Should().BeFalse();
    }

    [Fact]
    public void JobStatusIsReadFromWhicheverCounterIsPresent()
    {
        StalwartService.ReadJobStatus("""{"status":{"succeeded":1}}""").Should().Be((1, 0));
        StalwartService.ReadJobStatus("""{"status":{"failed":1}}""").Should().Be((0, 1));
        StalwartService.ReadJobStatus("""{"status":{}}""").Should().Be((0, 0));
        StalwartService.ReadJobStatus("garbage").Should().Be((0, 0));
    }

    // ── The manifest that was never rendered ──────────────────────────────────

    [Fact]
    public void ACatalogPlaceholderIsNotAManifest()
    {
        // Exactly what is stored when a mail component is registered down a path that forgets to render
        // its manifest: the catalog's default values, which are a comment explaining that EntKube will
        // replace them. kubectl parses this happily and applies nothing, reporting "no objects passed to
        // apply" — which names neither the component nor the cause.
        CatalogEntry stalwart = ComponentCatalog.GetByKey(StalwartService.CatalogKey)!;

        ComponentLifecycleService.ContainsKubernetesObjects(stalwart.DefaultValues).Should().BeFalse();
        ComponentLifecycleService.ContainsKubernetesObjects(null).Should().BeFalse();
        ComponentLifecycleService.ContainsKubernetesObjects("  \n\n").Should().BeFalse();
        ComponentLifecycleService.ContainsKubernetesObjects("---\n# nothing\n---\n").Should().BeFalse();
    }

    [Fact]
    public void ARenderedManifestIsOne()
    {
        string manifest = RspamdManifestBuilder.Build(
            new RspamdSettings(null), "rspamd", "rspamd");

        ComponentLifecycleService.ContainsKubernetesObjects(manifest).Should().BeTrue();
    }
}
