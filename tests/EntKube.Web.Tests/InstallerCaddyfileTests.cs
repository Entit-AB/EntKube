using EntKube.Installer;

namespace EntKube.Web.Tests;

/// <summary>
/// Reading an existing Caddyfile.
///
/// This is a small reader for a file format the installer does not own, so the tests are mostly
/// about what it must NOT do. Getting a domain wrong here would order a Let's Encrypt certificate
/// for a name that is not this server's — so anything that cannot be confidently identified as a
/// hostname must come back null, and the installer then asks, exactly as it did before.
/// </summary>
public class InstallerCaddyfileTests
{
    [Fact]
    public void The_site_address_is_read_as_the_domain()
    {
        CaddyfileFacts facts = CaddyfileFacts.Parse("""
            entkube.example.com {
            	reverse_proxy entkube:8080
            }
            """);

        Assert.Equal("entkube.example.com", facts.Domain);
    }

    [Fact]
    public void The_acme_email_is_read_from_the_global_options_block()
    {
        CaddyfileFacts facts = CaddyfileFacts.Parse("""
            {
            	email ops@example.com
            }

            entkube.example.com {
            	reverse_proxy entkube:8080
            }
            """);

        Assert.Equal("ops@example.com", facts.AcmeEmail);
        Assert.Equal("entkube.example.com", facts.Domain);
    }

    [Fact]
    public void An_email_inside_a_site_block_is_not_taken_as_the_acme_account()
    {
        // A site block can carry a directive that begins the same way. Taking one would put a
        // stranger's address on the ACME account.
        CaddyfileFacts facts = CaddyfileFacts.Parse("""
            entkube.example.com {
            	email not-the-acme-account@example.com
            	reverse_proxy entkube:8080
            }
            """);

        Assert.Null(facts.AcmeEmail);
    }

    [Fact]
    public void The_generated_caddyfile_yields_no_domain_but_is_flagged_as_using_a_placeholder()
    {
        // This is what the installer itself writes. There is no literal domain in it, and reporting
        // "{env.DOMAIN}" as a hostname would be worse than reporting nothing.
        CaddyfileFacts facts = CaddyfileFacts.Parse("""
            {
            	email {env.ACME_EMAIL}
            }

            {env.DOMAIN} {
            	reverse_proxy entkube:8080
            }
            """);

        Assert.Null(facts.Domain);
        Assert.Null(facts.AcmeEmail);
        Assert.True(facts.UsesEnvPlaceholder);
    }

    [Theory]
    [InlineData("https://entkube.example.com {", "entkube.example.com")]
    [InlineData("http://entkube.example.com {", "entkube.example.com")]
    [InlineData("entkube.example.com:443 {", "entkube.example.com")]
    [InlineData("entkube.example.com, www.example.com {", "entkube.example.com")]
    [InlineData("entkube.example.com www.example.com {", "entkube.example.com")]
    public void An_address_is_reduced_to_its_hostname(string header, string expected)
    {
        CaddyfileFacts facts = CaddyfileFacts.Parse(header + "\n\treverse_proxy entkube:8080\n}");

        Assert.Equal(expected, facts.Domain);
    }

    [Theory]
    [InlineData(":443 {")]
    [InlineData(":80 {")]
    [InlineData("*.example.com {")]
    [InlineData("localhost {")]
    public void Something_that_is_not_a_specific_hostname_is_not_guessed_at(string header)
    {
        // A bare port names no host; a wildcard names many and none; "localhost" is not a name a
        // certificate can be issued for. All of them mean "ask".
        CaddyfileFacts facts = CaddyfileFacts.Parse(header + "\n\treverse_proxy entkube:8080\n}");

        Assert.Null(facts.Domain);
    }

    [Fact]
    public void A_commented_out_site_block_is_not_read()
    {
        CaddyfileFacts facts = CaddyfileFacts.Parse("""
            # old.example.com {
            #	reverse_proxy entkube:8080
            # }

            entkube.example.com {
            	reverse_proxy entkube:8080
            }
            """);

        Assert.Equal("entkube.example.com", facts.Domain);
    }

    [Fact]
    public void A_nested_block_is_not_mistaken_for_a_site_address()
    {
        // reverse_proxy opens a block of its own. Its header is not a hostname and must not be read
        // as one.
        CaddyfileFacts facts = CaddyfileFacts.Parse("""
            entkube.example.com {
            	reverse_proxy entkube:8080 {
            		header_up Host {host}
            	}
            }
            """);

        Assert.Equal("entkube.example.com", facts.Domain);
    }

    [Fact]
    public void The_first_site_block_wins_when_there_are_several()
    {
        CaddyfileFacts facts = CaddyfileFacts.Parse("""
            entkube.example.com {
            	reverse_proxy entkube:8080
            }

            other.example.com {
            	respond "hi"
            }
            """);

        Assert.Equal("entkube.example.com", facts.Domain);
    }

    [Fact]
    public void An_empty_or_meaningless_file_yields_nothing_rather_than_a_guess()
    {
        Assert.Null(CaddyfileFacts.Parse(string.Empty).Domain);
        Assert.Null(CaddyfileFacts.Parse("# nothing here\n").Domain);
        Assert.Null(CaddyfileFacts.None.Domain);
    }
}
