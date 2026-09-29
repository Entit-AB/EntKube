using EntKube.Web.Data;
using EntKube.Web.Services;
using EntKube.Web.Services.Mail;
using FluentAssertions;
using Xunit;

namespace EntKube.Web.Tests;

/// <summary>
/// Deriving a support mailbox's connection from the mail server it was chosen on, instead of asking
/// somebody to type it.
/// </summary>
public class MailboxConnectionTests
{
    private static StalwartComponentConfig Config(Action<StalwartComponentConfig>? edit = null)
    {
        StalwartComponentConfig config = new()
        {
            Id = Guid.NewGuid(),
            TenantId = Guid.NewGuid(),
            ClusterComponentId = Guid.NewGuid(),
            Hostname = "mail.example.com",
        };
        edit?.Invoke(config);
        return config;
    }

    private static ClusterComponent Component() => new()
    {
        Id = Guid.NewGuid(),
        Name = "stalwart",
        ComponentType = "Manifest",
        ReleaseName = "mail",
        Namespace = "messaging",
        ClusterId = Guid.NewGuid(),
    };

    private static (StalwartMailAccount Account, StalwartMailDomain Domain) Mailbox(Guid configId)
    {
        StalwartMailDomain domain = new()
        {
            Id = Guid.NewGuid(), ConfigId = configId, Name = "Example.COM", IsPrimary = true,
        };
        StalwartMailAccount account = new()
        {
            Id = Guid.NewGuid(), ConfigId = configId, DomainId = domain.Id, LocalPart = "Support",
        };
        return (account, domain);
    }

    [Fact]
    public void TheConnectionComesFromTheServerRatherThanFromWhatWasTyped()
    {
        StalwartComponentConfig config = Config();
        ClusterComponent component = Component();
        (StalwartMailAccount account, StalwartMailDomain domain) = Mailbox(config.Id);

        MailboxConnection where =
            MailboxConnectionResolver.Resolve(config, component, account, domain)!;

        // The in-cluster Service, built from the release and namespace — not the public hostname. The
        // poller is in the same cluster, so going out through the gateway and back would make a local
        // read depend on DNS, the load balancer and a certificate chain it needs none of.
        where.Host.Should().Be("mail.messaging.svc.cluster.local");
        where.Port.Should().Be(MailPorts.Imaps);
        where.UseSsl.Should().BeTrue();

        // Lower-cased and joined: the login is the address, however the account was capitalised.
        where.Username.Should().Be("support@example.com");
    }

    /// <summary>
    /// A server whose directory is OIDC is reached on its public hostname, not on its Service name.
    ///
    /// <para>The in-cluster Service is the better address when it works — no public DNS, no load
    /// balancer, no certificate chain for a connection that never leaves the cluster — and it is not an
    /// address an OIDC server can be reached on, even from inside its own cluster. That is a fact about
    /// how these deployments authenticate rather than something derivable here, and the comment that
    /// used to justify the Service name argued from the poller's location, which was never the
    /// question.</para>
    /// </summary>
    [Fact]
    public void AnOidcServerIsReachedOnItsPublicHostname()
    {
        StalwartComponentConfig config = Config(c => c.AuthMode = StalwartAuthMode.Oidc);
        (StalwartMailAccount account, StalwartMailDomain domain) = Mailbox(config.Id);

        MailboxConnection where =
            MailboxConnectionResolver.Resolve(config, Component(), account, domain)!;

        where.Host.Should().Be("mail.example.com");
        where.Port.Should().Be(MailPorts.Imaps);
        where.UseSsl.Should().BeTrue();
        where.UseOAuth.Should().BeTrue();

        // And the certificate is issued for that name, so the check applies rather than being waived.
        // Waiving it was right for the Service name and is not something to carry over: on the public
        // hostname a name mismatch would mean something is actually wrong.
        where.ValidateCertificateName.Should().BeTrue();
    }

    /// <summary>
    /// A server that checks passwords itself keeps the in-cluster Service.
    ///
    /// <para>The other half, without which the test above would pass equally well if every mailbox were
    /// sent out through the load balancer — losing the reason the Service name was chosen, and taking a
    /// working configuration with it.</para>
    /// </summary>
    [Theory]
    [InlineData(StalwartAuthMode.Internal)]
    [InlineData(StalwartAuthMode.Ldap)]
    public void AServerThatChecksPasswordsKeepsTheInClusterService(StalwartAuthMode mode)
    {
        StalwartComponentConfig config = Config(c => c.AuthMode = mode);
        (StalwartMailAccount account, StalwartMailDomain domain) = Mailbox(config.Id);

        MailboxConnection where =
            MailboxConnectionResolver.Resolve(config, Component(), account, domain)!;

        where.Host.Should().Be("mail.messaging.svc.cluster.local");
        where.ValidateCertificateName.Should().BeFalse();
    }

    /// <summary>
    /// With no hostname set there is nothing public to reach, so the Service name is used rather than
    /// an empty host — which would fail as an unparseable address instead of as a missing setting.
    /// </summary>
    [Fact]
    public void AnOidcServerWithNoHostnameFallsBackRatherThanBuildingAnEmptyAddress()
    {
        StalwartComponentConfig config = Config(c =>
        {
            c.AuthMode = StalwartAuthMode.Oidc;
            c.Hostname = "   ";
        });
        (StalwartMailAccount account, StalwartMailDomain domain) = Mailbox(config.Id);

        MailboxConnectionResolver.Resolve(config, Component(), account, domain)!
            .Host.Should().Be("mail.messaging.svc.cluster.local");
    }

    [Fact]
    public void TheCertificateNameIsNotCheckedForAServerReachedByItsServiceName()
    {
        // This is the failure that reads as a security problem and is not one. The certificate is for
        // the hostname users arrive on; the poller connects by the Service name, so the name cannot
        // match and a client insisting on one fails the handshake — which is how the support mailbox
        // reported "TLS failed" against a perfectly healthy server. Insisting is the wrong check
        // here: the connection never leaves the cluster and the server holding that certificate is
        // the one EntKube issued it to.
        StalwartComponentConfig config = Config();
        (StalwartMailAccount account, StalwartMailDomain domain) = Mailbox(config.Id);

        MailboxConnectionResolver.Resolve(config, Component(), account, domain)!
            .ValidateCertificateName.Should().BeFalse();

        // A mailbox somebody typed in has nothing to say it is ours, so it gets the full check.
        MailboxConnectionResolver.FromStoredSettings(new SupportMailbox
        {
            TenantId = Guid.NewGuid(), Host = "imap.customer.example", Username = "support",
        }).ValidateCertificateName.Should().BeTrue();
    }

    [Fact]
    public void TheCredentialTypeFollowsWhatTheServerCanActuallyValidate()
    {
        // Not a setting, a consequence. Upstream: a directory "serves every protocol and both
        // credential types", and "only an OIDC-type directory can validate bearer tokens, and only
        // LDAP or SQL can validate passwords". So on an OIDC server no password stored anywhere would
        // be checked, and sending one is refused by a server that never checks passwords.
        (StalwartMailAccount account, StalwartMailDomain domain) = Mailbox(Guid.NewGuid());

        foreach ((StalwartAuthMode mode, bool expectToken) in new[]
        {
            (StalwartAuthMode.Oidc, true),
            (StalwartAuthMode.Internal, false),
            (StalwartAuthMode.Ldap, false),
        })
        {
            MailboxConnectionResolver
                .Resolve(Config(c => c.AuthMode = mode), Component(), account, domain)!
                .UseOAuth.Should().Be(expectToken, $"auth mode {mode}");
        }

        // A mailbox that was typed in is a password: nothing here knows of a provider to mint against.
        MailboxConnectionResolver.FromStoredSettings(new SupportMailbox
        {
            TenantId = Guid.NewGuid(), Host = "imap.customer.example", Username = "support",
        }).UseOAuth.Should().BeFalse();
    }

    [Fact]
    public void AServerWithImapSwitchedOffOffersNothingToRead()
    {
        // Better than deriving a connection to a port that is not listening: the caller says so, and
        // the operator is told to turn IMAP on rather than shown a timeout.
        StalwartComponentConfig config = Config(c => c.ImapEnabled = false);
        (StalwartMailAccount account, StalwartMailDomain domain) = Mailbox(config.Id);

        MailboxConnectionResolver.Resolve(config, Component(), account, domain).Should().BeNull();
    }
}
