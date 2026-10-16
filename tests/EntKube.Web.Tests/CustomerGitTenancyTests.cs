using EntKube.Web.Data;
using EntKube.Web.Services;
using FluentAssertions;

namespace EntKube.Web.Tests;

/// <summary>
/// Whose customer's git access <see cref="CustomerGitService"/> is willing to read and change.
///
/// <para><b>Why there was none.</b> The service had no test file at all. Eight of its nine
/// methods were keyed by a <c>customerId</c> — plus an environment id, a policy id or a
/// credential id — and took no tenant, so the id was the whole authorization. The ninth,
/// <c>CreateCredentialAsync</c>, is the interesting one: it <em>took</em> a tenant id and wrote it
/// onto the row without ever checking the customer, which is a stamp and not a check.</para>
///
/// <para>Two predicate shapes are exercised, because they can be written wrong independently.
/// <c>CustomerGitCredential</c> carries a <c>TenantId</c> column, so its scope is a direct
/// comparison; <c>CustomerGitRepoPolicy</c> does not, so the scope travels through the
/// customer.</para>
/// </summary>
public class CustomerGitTenancyTests : IDisposable
{
    private static readonly byte[] TestRootKey = Convert.FromBase64String(TestServices.TestRootKeyBase64);

    private readonly InterceptingTestDb testDb;
    private readonly ApplicationDbContext db;
    private readonly CustomerGitService sut;

    public CustomerGitTenancyTests()
    {
        testDb = new InterceptingTestDb(TestRootKey);
        db = testDb.CreateContext();
        sut = new CustomerGitService(testDb.Factory, testDb.CreateVaultService());
    }

    public void Dispose()
    {
        db.Dispose();
        testDb.Dispose();
        GC.SuppressFinalize(this);
    }

    /// <summary>A tenant with one customer and one environment, all newly created.</summary>
    private (Guid Tenant, Guid Customer, Guid Environment) Seed(string slug)
    {
        Guid tenant = Guid.NewGuid(), customer = Guid.NewGuid(), environment = Guid.NewGuid();

        db.Tenants.Add(new Tenant { Id = tenant, Name = slug, Slug = $"{slug}-{tenant:N}" });
        db.Customers.Add(new Customer { Id = customer, TenantId = tenant, Name = $"{slug}-co" });
        db.Set<Data.Environment>().Add(new Data.Environment
        {
            Id = environment, TenantId = tenant, Name = $"{slug}-prod",
        });
        db.SaveChanges();

        return (tenant, customer, environment);
    }

    // ──────── Repo policies ────────

    /// <summary>
    /// A repo policy is the allow-list a deployment's git URL is checked against, so reading
    /// another tenant's tells you which hosts and orgs they deploy from, and adding to one is a
    /// repository their next sync will accept.
    /// </summary>
    [Fact]
    public async Task Repo_policies_are_confined_to_their_tenant()
    {
        var us = Seed("us");
        var them = Seed("them");

        await sut.AddRepoPolicyAsync(us.Tenant, us.Customer, us.Environment, "https://github.com/us/*");
        await sut.AddRepoPolicyAsync(them.Tenant, them.Customer, them.Environment, "https://github.com/them/*");

        // The owner sees its own, so the refusals below are about scope and not absence.
        (await sut.GetRepoPoliciesAsync(us.Tenant, us.Customer, us.Environment))
            .Should().ContainSingle().Which.UrlPattern.Should().Be("https://github.com/us/*");

        (await sut.GetRepoPoliciesAsync(us.Tenant, them.Customer, them.Environment))
            .Should().BeEmpty("their policy exists, so this is a refusal");

        // Writing against their customer must not land.
        Func<Task> widenTheirs = () => sut.AddRepoPolicyAsync(
            us.Tenant, them.Customer, them.Environment, "https://github.com/attacker/*");
        await widenTheirs.Should().ThrowAsync<InvalidOperationException>();

        // Their customer with our environment, and ours with theirs: the pair guard has two halves.
        Func<Task> theirCustomerOurEnvironment = () => sut.AddRepoPolicyAsync(
            us.Tenant, them.Customer, us.Environment, "https://github.com/attacker/*");
        Func<Task> ourCustomerTheirEnvironment = () => sut.AddRepoPolicyAsync(
            us.Tenant, us.Customer, them.Environment, "https://github.com/attacker/*");
        await theirCustomerOurEnvironment.Should().ThrowAsync<InvalidOperationException>();
        await ourCustomerTheirEnvironment.Should().ThrowAsync<InvalidOperationException>();

        (await sut.GetRepoPoliciesAsync(them.Tenant, them.Customer, them.Environment))
            .Should().ContainSingle("nothing of ours was added to theirs");
    }

    /// <summary>
    /// Deleting an allow-list entry narrows what the other tenant's deployments may pull, so it is
    /// a denial of service against their next sync as much as it is a deletion.
    /// </summary>
    [Fact]
    public async Task A_stranger_cannot_delete_another_tenants_repo_policy()
    {
        var us = Seed("us");
        var them = Seed("them");

        CustomerGitRepoPolicy theirs = await sut.AddRepoPolicyAsync(
            them.Tenant, them.Customer, them.Environment, "https://github.com/them/*");

        (await sut.DeleteRepoPolicyAsync(us.Tenant, them.Customer, theirs.Id))
            .Should().BeFalse();

        using ApplicationDbContext after = testDb.CreateContext();
        after.Set<CustomerGitRepoPolicy>().Any(p => p.Id == theirs.Id).Should().BeTrue(
            "a false return with the row gone would be the same answer for the wrong reason");

        // Their own tenant still can, so "false" was a refusal.
        (await sut.DeleteRepoPolicyAsync(them.Tenant, them.Customer, theirs.Id))
            .Should().BeTrue();
    }

    // ──────── Credentials ────────

    /// <summary>
    /// A credential row is the key to a secret in the vault: <c>GetPatAsync</c> and its siblings
    /// take a credential id, and <c>UpdateCredentialAsync</c> can repoint the <c>UrlPattern</c>,
    /// which is what decides the host a stored PAT is sent to. So an edit can redirect another
    /// tenant's secret without ever reading it.
    /// </summary>
    [Fact]
    public async Task Credentials_are_confined_to_their_tenant()
    {
        var us = Seed("us");
        var them = Seed("them");

        CustomerGitCredential theirs = await sut.CreateCredentialAsync(
            them.Customer, them.Environment, them.Tenant, "their-pat", GitAuthType.HttpsPat,
            urlPattern: "https://github.com/them/*");

        (await sut.GetCredentialsAsync(them.Tenant, them.Customer, them.Environment))
            .Should().ContainSingle("the owner sees its own");

        (await sut.GetCredentialsAsync(us.Tenant, them.Customer, them.Environment))
            .Should().BeEmpty("the row exists, so this is a refusal");

        // The sync path's lookup — what it returns is handed to git as the credential to
        // authenticate with, so an unscoped match lends their stored PAT to our deployment.
        (await sut.FindMatchingCredentialAsync(
                us.Tenant, them.Customer, them.Environment, "https://github.com/them/app.git"))
            .Should().BeNull();
        (await sut.FindMatchingCredentialAsync(
                them.Tenant, them.Customer, them.Environment, "https://github.com/them/app.git"))
            .Should().NotBeNull("the owner's own sync still resolves it");

        // Repointing their credential at a host of our choosing.
        (await sut.UpdateCredentialAsync(
                us.Tenant, them.Customer, theirs.Id, "hijacked", GitAuthType.HttpsPat,
                null, "https://evil.example.com/*"))
            .Should().BeFalse();

        using ApplicationDbContext after = testDb.CreateContext();
        CustomerGitCredential unchanged = after.Set<CustomerGitCredential>().First(c => c.Id == theirs.Id);
        unchanged.Name.Should().Be("their-pat");
        unchanged.UrlPattern.Should().Be("https://github.com/them/*",
            "the pattern is what decides where the secret is sent");

        (await sut.DeleteCredentialAsync(us.Tenant, them.Customer, theirs.Id)).Should().BeFalse();
        after.ChangeTracker.Clear();
        after.Set<CustomerGitCredential>().Any(c => c.Id == theirs.Id).Should().BeTrue();

        // And the owner can do both.
        (await sut.UpdateCredentialAsync(
                them.Tenant, them.Customer, theirs.Id, "renamed", GitAuthType.HttpsPat, null, null))
            .Should().BeTrue();
        (await sut.DeleteCredentialAsync(them.Tenant, them.Customer, theirs.Id)).Should().BeTrue();
    }

    /// <summary>
    /// ⚠️ <c>CreateCredentialAsync</c> always took a <c>tenantId</c>, and wrote it onto the row
    /// without checking that the customer or the environment were that tenant's. A tenant id that
    /// is only ever stamped is not a check: the row would claim one tenant while hanging off
    /// another's customer, and every later query that trusts the column — all of the ones above —
    /// would then serve it to the wrong side.
    /// </summary>
    [Fact]
    public async Task A_credential_cannot_be_created_against_another_tenants_customer()
    {
        var us = Seed("us");
        var them = Seed("them");

        // ⚠️ Three crossings, not two. The first draft had only the outer pair — their customer
        // with their environment, and our customer with their environment — and both of those are
        // refused by the ENVIRONMENT half alone, so deleting the customer half left this test
        // green. A pair guard needs the crossing that only one half can catch.
        Func<Task> onTheirCustomer = () => sut.CreateCredentialAsync(
            them.Customer, them.Environment, us.Tenant, "planted", GitAuthType.HttpsPat);
        Func<Task> inTheirEnvironment = () => sut.CreateCredentialAsync(
            us.Customer, them.Environment, us.Tenant, "planted", GitAuthType.HttpsPat);
        Func<Task> theirCustomerOurEnvironment = () => sut.CreateCredentialAsync(
            them.Customer, us.Environment, us.Tenant, "planted", GitAuthType.HttpsPat);

        await onTheirCustomer.Should().ThrowAsync<InvalidOperationException>();
        await inTheirEnvironment.Should().ThrowAsync<InvalidOperationException>();
        await theirCustomerOurEnvironment.Should().ThrowAsync<InvalidOperationException>();

        using ApplicationDbContext after = testDb.CreateContext();
        after.Set<CustomerGitCredential>().Should().BeEmpty(
            "a row claiming our tenant on their customer is exactly what the stamp allowed");

        // The owner's own credential is still created.
        CustomerGitCredential ours = await sut.CreateCredentialAsync(
            us.Customer, us.Environment, us.Tenant, "ours", GitAuthType.HttpsPat);
        ours.TenantId.Should().Be(us.Tenant);
    }
}
