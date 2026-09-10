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
    public void AcmeChallengePortsArePublishedOnTheMailAddressOnlyForTheChallengeThatNeedsThem()
    {
        // TLS-ALPN-01 wants 443, HTTP-01 wants 80, DNS-01 wants neither, and no other TLS mode
        // wants either — an open port with nothing behind it is attack surface for free.
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

        MailPortsOf(Config(c => { c.TlsMode = StalwartTlsMode.Acme; c.AcmeChallenge = StalwartAcmeChallenge.Http01; }))
            .Should().Contain("80").And.NotContain("443");

        MailPortsOf(Config(c => { c.TlsMode = StalwartTlsMode.Acme; c.AcmeChallenge = StalwartAcmeChallenge.Dns01; }))
            .Should().NotContain(["80", "443"]);

        MailPortsOf(Config(c => c.TlsMode = StalwartTlsMode.ClusterIssuer))
            .Should().NotContain(["80", "443"]);
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
        denies.Should().Contain(a => a.If.Contains(StalwartManifestBuilder.AcmeTlsListener));
        // The deny arms come after every allow arm, or a public path would be refused before it is allowed.
        arms.FindLastIndex(a => a.Then == "200").Should().BeLessThan(arms.FindIndex(a => a.Then == "403"));

        // Every other listener is unaffected.
        policy.GetProperty("else").GetString().Should().Be("200");

        JsonElement listeners = Operation(plan, "NetworkListener")!.Value.GetProperty("value");
        listeners.GetProperty(StalwartManifestBuilder.AcmeHttpListener)
            .GetProperty("bind").GetProperty("[::]:80").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public void WithoutAcmeThereAreNoAcmeListenersAndNoRestrictions()
    {
        StalwartComponentConfig config = Config(c => c.TlsMode = StalwartTlsMode.ClusterIssuer);
        string plan = StalwartPlanBuilder.BuildApplyPlan(
            config, [Domain(config.Id, "example.com")], []);

        JsonElement listeners = Operation(plan, "NetworkListener")!.Value.GetProperty("value");
        listeners.TryGetProperty(StalwartManifestBuilder.AcmeHttpListener, out _).Should().BeFalse();
        listeners.TryGetProperty(StalwartManifestBuilder.AcmeTlsListener, out _).Should().BeFalse();

        // The policy is still written, so switching away from ACME lifts the rules rather than
        // leaving a deny behind that nothing would ever remove — but it is written as an explicit
        // allow, never as an empty match. This gates every HTTP request the server serves, and a
        // policy that denies them all is indistinguishable from a healthy one until someone opens
        // the admin interface and gets a bare Forbidden.
        JsonElement policy = Operation(plan, "Http")!.Value
            .GetProperty("value").GetProperty("allowedEndpoints");

        // Exactly the documented default, {"else":"200"} — no match key, no arms, no invented
        // shape. This is the one form with evidence behind it, on a setting where getting the
        // shape wrong locks the operator out of the admin interface.
        policy.TryGetProperty("match", out _).Should().BeFalse();
        policy.GetProperty("else").GetString().Should().Be("200");
        policy.EnumerateObject().Should().HaveCount(1);
    }

    [Fact]
    public void TheEndpointPolicyNeverDeniesWithoutAnAcmeListenerToProtect()
    {
        // allowedEndpoints is global: it gates every HTTP request, on every listener. A 403 arm in a
        // deployment with no ACME listener has nothing legitimate to deny and can only lock the
        // operator out of the admin interface.
        foreach (StalwartTlsMode mode in Enum.GetValues<StalwartTlsMode>())
        {
            foreach (StalwartAcmeChallenge challenge in Enum.GetValues<StalwartAcmeChallenge>())
            {
                StalwartComponentConfig config = Config(c =>
                {
                    c.TlsMode = mode;
                    c.AcmeChallenge = challenge;
                    c.AcmeContact = "hostmaster@example.com";
                });

                string plan = StalwartPlanBuilder.BuildApplyPlan(
                    config, [Domain(config.Id, "example.com")], []);

                JsonElement listeners = Operation(plan, "NetworkListener")!.Value.GetProperty("value");
                bool hasAcmeListener =
                    listeners.TryGetProperty(StalwartManifestBuilder.AcmeHttpListener, out _)
                    || listeners.TryGetProperty(StalwartManifestBuilder.AcmeTlsListener, out _);

                JsonElement endpointPolicy = Operation(plan, "Http")!.Value
                    .GetProperty("value").GetProperty("allowedEndpoints");
                List<string> denies = endpointPolicy.TryGetProperty("match", out JsonElement match)
                    ? match.EnumerateObject()
                        .Where(a => a.Value.GetProperty("then").GetString() != "200")
                        .Select(a => a.Value.GetProperty("if").GetString()!)
                        .ToList()
                    : [];

                if (!hasAcmeListener)
                {
                    denies.Should().BeEmpty(
                        $"{mode}/{challenge} opens no ACME port, so the endpoint policy has nothing "
                        + "to protect and must not be able to refuse a request");
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
        addresses.Should().BeEquivalentTo(["10.42.0.17", "10.42.1.9"]); // de-duplicated
    }

    [Fact]
    public void NoAllowListIsWrittenWhenNoGatewayAddressesWereResolved()
    {
        StalwartComponentConfig config = Config();
        string plan = StalwartPlanBuilder.BuildApplyPlan(config, [Domain(config.Id, "example.com")], []);

        Operation(plan, "AllowedIp").Should().BeNull();
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
        S3AccessKey: "AKIA…", RedisUrl: "redis://redis.redis.svc.cluster.local:6379", Replicas: 3);

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
    public void TheRedisCoordinatorNeverEmitsAnAuthSecretObject()
    {
        // Whatever the URL contains, the Coordinator/InMemoryStore objects only ever carry `url` —
        // the Redis store struct has no secret field, so an authSecret would fail the apply.
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
        // DKIM keys are generated by the server and only exist after the domain is applied, so
        // predicting a selector here would be inventing one.
        records.Should().NotContain(r => r.Name.Contains("_domainkey"));
    }

    [Fact]
    public void AutodiscoveryRecordsPointAtTheMailHostInAcmeModeAndTheGatewayOtherwise()
    {
        // In ACME mode Stalwart certifies autoconfig/autodiscover/mta-sts itself, so those names
        // must resolve to the mail host or the certificate order fails — the actual cause of a
        // burned Let's Encrypt rate limit. In cert-manager mode they are served via the gateway.
        StalwartComponentConfig acme = Config(c =>
        {
            c.Hostname = "mail.example.com";
            c.AdminHostname = "mailadmin.example.com";
            c.TlsMode = StalwartTlsMode.Acme;
        });
        foreach (var r in StalwartPlanBuilder.DnsRecordsFor(acme, Domain(acme.Id, "example.com"))
                     .Where(r => r.Name.StartsWith("autoconfig.") || r.Name.StartsWith("autodiscover.")
                              || r.Name.StartsWith("mta-sts.") || r.Name.StartsWith("ua-auto-config.")))
        {
            r.Value.Should().Be("mail.example.com.");
        }

        StalwartComponentConfig cm = Config(c =>
        {
            c.Hostname = "mail.example.com";
            c.AdminHostname = "mailadmin.example.com";
            c.TlsMode = StalwartTlsMode.ClusterIssuer;
        });
        StalwartPlanBuilder.DnsRecordsFor(cm, Domain(cm.Id, "example.com"))
            .First(r => r.Name.StartsWith("autoconfig.")).Value.Should().Be("mailadmin.example.com.");
    }

    // ── rspamd ────────────────────────────────────────────────────────────────

    [Fact]
    public void RspamdOmitsOptionalPasswordLinesEntirelyWhenUnset()
    {
        string manifest = RspamdManifestBuilder.Build(
            new RspamdSettings("redis:6379", RedisPassword: null, ControllerPassword: null),
            "rspamd", "rspamd");

        YamlDocument configMap = Parse(manifest).First(d => Scalar(d.RootNode, "kind") == "ConfigMap");
        string redis = Scalar(configMap.RootNode, "data", "redis.conf")!;

        redis.Should().Contain("servers = \"redis:6379\";");
        // An empty password line is not the same as no password line — it is an attempt to
        // authenticate with the empty string, which a Redis with auth off will refuse.
        redis.Should().NotContain("password");
    }

    [Fact]
    public void RspamdWritesRedisAndControllerCredentialsWhenTheyAreSet()
    {
        string manifest = RspamdManifestBuilder.Build(
            new RspamdSettings("redis:6379", "s3cr3t", "ui-pass"), "rspamd", "rspamd");

        YamlDocument configMap = Parse(manifest).First(d => Scalar(d.RootNode, "kind") == "ConfigMap");

        Scalar(configMap.RootNode, "data", "redis.conf")!.Should().Contain("password = \"s3cr3t\";");

        string controller = Scalar(configMap.RootNode, "data", "worker-controller.inc")!;
        controller.Should().Contain("password = \"ui-pass\";");
        controller.Should().Contain("enable_password = \"ui-pass\";");
    }

    [Fact]
    public void RspamdWithoutSingleSignOnHasNoProxyAndNoLoopbackTrust()
    {
        string manifest = RspamdManifestBuilder.Build(
            new RspamdSettings("redis:6379", null, "ui-pass"), "rspamd", "rspamd");

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
                "redis:6379", null, "ui-pass",
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
                "redis:6379", null, null,
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
            new RspamdSettings("redis:6379", null, null), "rspamd", "rspamd");

        YamlDocument configMap = Parse(manifest).First(d => Scalar(d.RootNode, "kind") == "ConfigMap");
        Scalar(configMap.RootNode, "data", "classifier-bayes.conf")!
            .Should().Contain("backend = \"redis\"");
    }

    [Fact]
    public void RspamdExposesTheMilterPortStalwartConnectsTo()
    {
        string manifest = RspamdManifestBuilder.Build(
            new RspamdSettings("redis:6379", null, null), "rspamd", "rspamd");

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
            new RspamdSettings("redis:6379", null, null), "rspamd", "rspamd");

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
        // a stale exemption behind that quietly excuses the next real omission.
        List<string> keys = entry.FormFields.Select(f => f.Key).ToList();
        keys.Should().Contain(StalwartService.SecretFormKeys);
        keys.Should().Contain(StalwartService.DerivedFormKeys);
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
            new RspamdSettings("redis:6379", null, null), "rspamd", "rspamd");

        ComponentLifecycleService.ContainsKubernetesObjects(manifest).Should().BeTrue();
    }
}
