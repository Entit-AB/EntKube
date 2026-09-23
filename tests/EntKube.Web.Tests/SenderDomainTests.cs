using EntKube.Web.Services.Mail;
using FluentAssertions;

namespace EntKube.Web.Tests;

/// <summary>
/// Deciding which customer a sender's domain belongs to.
///
/// <para>The consequence of getting this wrong is one customer's support mail arriving in
/// another customer's queue, already triaged and already read. There is no error and
/// nothing to notice — which is why the rule is pure, and why the near-misses are tested
/// rather than the obvious cases.</para>
/// </summary>
public class SenderDomainTests
{
    // ---- Reading the domain off an address ------------------------------------------------

    [Theory]
    [InlineData("anna@entit.example", "entit.example")]
    [InlineData("Anna.Lindqvist@ENTIT.EXAMPLE", "entit.example")]
    [InlineData("  anna@entit.example  ", "entit.example")]
    [InlineData("anna@it.entit.example", "it.entit.example")]
    // A trailing dot is a fully-qualified name and the same domain.
    [InlineData("anna@entit.example.", "entit.example")]
    public void The_domain_is_read_off_the_address(string address, string expected) =>
        SenderDomain.Of(address).Should().Be(expected);

    /// <summary>An address may be absent or malformed; the matcher must not throw on it.</summary>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    [InlineData("not-an-address")]
    [InlineData("trailing@")]
    public void An_address_with_no_domain_has_none(string? address) =>
        SenderDomain.Of(address).Should().BeNull();

    /// <summary>
    /// An address with more than one @ is legal when the local part is quoted. The domain
    /// is what follows the last one.
    /// </summary>
    [Fact]
    public void The_domain_is_what_follows_the_last_at() =>
        SenderDomain.Of("\"odd@name\"@entit.example").Should().Be("entit.example");

    // ---- What a registered domain claims ---------------------------------------------------

    [Fact]
    public void A_domain_claims_itself() =>
        SenderDomain.Claims("entit.example", "entit.example").Should().BeTrue();

    /// <summary>
    /// A customer saying "our mail comes from entit.example" means their subdomains too. Making
    /// them register each one would mean support mail from a new subdomain silently going
    /// unplaced.
    /// </summary>
    [Fact]
    public void A_domain_claims_its_subdomains() =>
        SenderDomain.Claims("entit.example", "it.entit.example").Should().BeTrue();

    /// <summary>
    /// <b>The one that matters.</b> Without the dot boundary, registering entit.example would
    /// claim notentit.example — a domain anybody in the world can register, whose mail would
    /// then be triaged straight into that customer's queue.
    /// </summary>
    [Theory]
    [InlineData("notentit.example")]
    [InlineData("xentit.example")]
    [InlineData("entit.example.evil.com")]
    public void A_domain_does_not_claim_one_that_merely_ends_the_same(string sender) =>
        SenderDomain.Claims("entit.example", sender).Should().BeFalse();

    [Fact]
    public void A_subdomain_registration_does_not_claim_the_parent() =>
        SenderDomain.Claims("it.entit.example", "entit.example").Should().BeFalse();

    // ---- Choosing between several ------------------------------------------------------------

    private sealed record Registered(string Domain, string Customer);

    /// <summary>
    /// Two customers can legitimately hold overlapping domains — a subsidiary on a
    /// subdomain of its parent. The more specific one wins.
    /// </summary>
    [Fact]
    public void The_most_specific_registered_domain_wins()
    {
        Registered[] registered =
        [
            new("entit.example", "Entit AB"),
            new("it.entit.example", "Entit Europe"),
        ];

        SenderDomain.BestMatch(registered, r => r.Domain, "it.entit.example")!
            .Customer.Should().Be("Entit Europe");

        SenderDomain.BestMatch(registered, r => r.Domain, "hr.entit.example")!
            .Customer.Should().Be("Entit AB");
    }

    /// <summary>
    /// The answer cannot depend on the order rows came back in, or the same sender lands
    /// in different queues on different days and nobody can reproduce it.
    /// </summary>
    [Fact]
    public void The_answer_does_not_depend_on_the_order_of_the_register()
    {
        Registered[] forwards = [new("entit.example", "Entit AB"), new("it.entit.example", "Entit Europe")];
        Registered[] backwards = [.. forwards.Reverse()];

        SenderDomain.BestMatch(forwards, r => r.Domain, "it.entit.example")!.Customer
            .Should().Be(SenderDomain.BestMatch(backwards, r => r.Domain, "it.entit.example")!.Customer);
    }

    [Fact]
    public void Nothing_matches_when_no_domain_is_registered() =>
        SenderDomain.BestMatch<Registered>([], r => r.Domain, "entit.example").Should().BeNull();

    [Fact]
    public void Nothing_matches_a_sender_with_no_domain() =>
        SenderDomain.BestMatch([new Registered("entit.example", "Entit AB")], r => r.Domain, null)
            .Should().BeNull();

    // ---- Tidying what somebody typed ----------------------------------------------------------

    [Theory]
    [InlineData("entit.example", "entit.example")]
    [InlineData("  ENTIT.EXAMPLE ", "entit.example")]
    [InlineData("@entit.example", "entit.example")]
    [InlineData("*.entit.example", "entit.example")]
    [InlineData(".entit.example", "entit.example")]
    [InlineData("https://entit.example/", "entit.example")]
    // Somebody pasting a whole address meant the domain in it.
    [InlineData("anna@entit.example", "entit.example")]
    public void What_was_typed_is_tidied_into_a_domain(string entered, string expected) =>
        SenderDomain.Normalise(entered).Should().Be(expected);

    /// <summary>
    /// A bare word is a hostname on somebody's network, not a mail domain, and would claim
    /// far more than whoever typed it meant.
    /// </summary>
    [Theory]
    [InlineData("entit")]
    [InlineData("localhost")]
    [InlineData("")]
    [InlineData("@")]
    [InlineData(null)]
    public void Something_that_is_not_a_domain_is_refused(string? entered) =>
        SenderDomain.Normalise(entered).Should().BeNull();

    // ---- The providers that belong to everybody -------------------------------------------------

    /// <summary>
    /// Registering a public provider would hand every consumer address at it to one
    /// customer — and the mistake is invisible afterwards, because mail simply starts
    /// arriving under the wrong name, already triaged.
    /// </summary>
    [Theory]
    [InlineData("gmail.com")]
    [InlineData("GMAIL.COM")]
    [InlineData("hotmail.se")]
    [InlineData("icloud.com")]
    [InlineData("telia.com")]
    public void A_public_provider_is_recognised_as_one(string domain) =>
        SenderDomain.PublicProviders.Contains(domain).Should().BeTrue();

    [Fact]
    public void A_customers_own_domain_is_not_a_public_provider() =>
        SenderDomain.PublicProviders.Contains("entit.example").Should().BeFalse();
}
