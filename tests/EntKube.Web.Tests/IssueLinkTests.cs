using System.Net;
using System.Text;
using EntKube.Web.Data;
using EntKube.Web.Services.Contracts;
using EntKube.Web.Services.Tickets;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace EntKube.Web.Tests;

/// <summary>
/// Phase 6 (docs/helpdesk.md): GitHub and GitLab issues linked to tickets — which addresses are
/// issues, that only configured hosts are ever called, and that one tenant's token or answer never
/// serves another.
/// </summary>
public class IssueLinkTests : IDisposable
{
    private readonly SqliteConnection connection;
    private readonly ApplicationDbContext db;
    private readonly TicketService tickets;
    private readonly StubTrackers http = new();
    private readonly MemoryTokens tokens = new();
    private readonly IssueLinkService issues;

    private readonly Guid ours = Guid.NewGuid();
    private readonly Guid theirs = Guid.NewGuid();
    private readonly Guid customer = Guid.NewGuid();
    private readonly Guid theirCustomer = Guid.NewGuid();

    public IssueLinkTests()
    {
        connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();
        db = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(connection).Options);
        db.Database.EnsureCreated();
        db.Tenants.Add(new Tenant { Id = ours, Name = "ENTIT", Slug = "entit" });
        db.Tenants.Add(new Tenant { Id = theirs, Name = "Other", Slug = "other" });
        db.Customers.Add(new Customer { Id = customer, TenantId = ours, Name = "Vårdbolaget" });
        db.Customers.Add(new Customer { Id = theirCustomer, TenantId = theirs, Name = "Regionen" });
        db.SaveChanges();

        TestDbContextFactory factory = new(connection);
        tickets = new TicketService(factory, new ContractService(factory), SilentTicketNotifier.For(factory));
        issues = new IssueLinkService(factory, tokens, http, new MemoryCache(new MemoryCacheOptions()));
    }

    public void Dispose()
    {
        db.Dispose();
        connection.Dispose();
        GC.SuppressFinalize(this);
    }

    private Task<Ticket> Raise(Guid? tenant = null) =>
        tickets.CreateAsync(tenant ?? ours, tenant == theirs ? theirCustomer : customer, null, "Printing fails", "",
            TicketChannel.Email, TicketPriority.P3, DateTime.UtcNow);

    private const string GitHubIssue = """
        {"title":"Printer queue stalls","state":"open","labels":[{"name":"bug"},{"name":"customer"}],
         "assignees":[{"login":"anna"}],"milestone":{"title":"2026.12"}}
        """;

    [Theory]
    [InlineData("https://github.com/acme/portal/issues/12", IssueTrackerKind.GitHub, "github.com", "acme/portal", 12)]
    [InlineData("https://github.com/acme/portal/issues/12#issuecomment-99", IssueTrackerKind.GitHub, "github.com", "acme/portal", 12)]
    [InlineData("https://GitLab.Example.se/team/sub/portal/-/issues/7", IssueTrackerKind.GitLab, "gitlab.example.se", "team/sub/portal", 7)]
    [InlineData("https://gitlab.com/acme/portal/-/work_items/3?x=1", IssueTrackerKind.GitLab, "gitlab.com", "acme/portal", 3)]
    public void An_issue_address_is_taken_apart(string url, IssueTrackerKind kind, string host, string project, int number)
    {
        IssueReference.Parse(url).Should().Be(new IssueReference(kind, host, project, number));
    }

    [Theory]
    [InlineData("http://github.com/acme/portal/issues/12")]      // not https
    [InlineData("https://github.com/acme/portal/pull/12")]       // a pull request
    [InlineData("https://gitlab.com/acme/portal/-/merge_requests/3")]
    [InlineData("https://github.com/acme/portal")]
    [InlineData("https://github.com/acme/portal/issues/0")]
    [InlineData("https://github.com:8443/acme/portal/issues/1")] // a port the tracker was not set up with
    [InlineData("not a link")]
    public void Anything_but_an_issue_address_is_refused(string url) =>
        IssueReference.Parse(url).Should().BeNull();

    [Theory]
    [InlineData("https://GitLab.Example.se/", "gitlab.example.se")]
    [InlineData("github.com", "github.com")]
    [InlineData("10.0.0.5", null)]
    [InlineData("localhost", null)]
    [InlineData("https://git.example.se/gitlab", null)]
    public void A_tracker_host_is_a_bare_dns_name(string typed, string? stored) =>
        IssueReference.NormaliseHost(typed).Should().Be(stored);

    [Fact]
    public async Task A_link_to_a_host_nobody_set_up_is_refused_and_nothing_is_called()
    {
        Ticket ticket = await Raise();

        Func<Task> link = () => issues.LinkAsync(ours, ticket.Id, "https://evil.example.com/a/b/issues/1", "nils");

        await link.Should().ThrowAsync<InvalidOperationException>().WithMessage("*evil.example.com is not set up*");
        (await issues.LinksAsync(ours, ticket.Id)).Should().BeEmpty();
        http.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task A_host_set_up_as_gitlab_does_not_take_a_github_shaped_link()
    {
        Ticket ticket = await Raise();
        await issues.SaveTrackerAsync(ours, null, IssueTrackerKind.GitLab, "git.example.se", true, null, "nils");

        Func<Task> link = () => issues.LinkAsync(ours, ticket.Id, "https://git.example.se/a/b/issues/1", "nils");

        await link.Should().ThrowAsync<InvalidOperationException>().WithMessage("*not set up as a GitHub tracker*");
    }

    [Fact]
    public async Task A_github_issue_is_read_with_the_token_and_shows_its_state_labels_and_people()
    {
        Ticket ticket = await Raise();
        IssueTracker gh = await issues.SaveTrackerAsync(ours, null, IssueTrackerKind.GitHub, "github.com", true, "ghp_ours", "nils");
        http.Answer("https://api.github.com/repos/acme/portal/issues/12", HttpStatusCode.OK, GitHubIssue);

        await issues.LinkAsync(ours, ticket.Id, "https://github.com/acme/portal/issues/12#issuecomment-1", "nils");
        IssueView view = (await issues.ViewsAsync(ours, ticket.Id)).Single();

        view.Should().BeEquivalentTo(new
        {
            Url = "https://github.com/acme/portal/issues/12", Reference = "acme/portal#12", Title = "Printer queue stalls",
            IsOpen = (bool?)true, Labels = new[] { "bug", "customer" }, Assignees = new[] { "anna" }, Milestone = "2026.12",
            Problem = (string?)null,
        });
        http.Requests.Single().Headers.Authorization!.ToString().Should().Be("Bearer ghp_ours");
        tokens.Stored.Should().ContainKey((ours, gh.Id));
    }

    [Fact]
    public async Task A_gitlab_issue_is_read_by_its_encoded_project_path_with_a_private_token_header()
    {
        Ticket ticket = await Raise();
        await issues.SaveTrackerAsync(ours, null, IssueTrackerKind.GitLab, "git.example.se", true, "glpat-x", "nils");
        http.Answer("https://git.example.se/api/v4/projects/team%2Fsub%2Fportal/issues/7", HttpStatusCode.OK,
            """{"title":"Login loops","state":"closed","labels":["sso"],"assignees":[{"username":"erik"}]}""");

        await issues.LinkAsync(ours, ticket.Id, "https://git.example.se/team/sub/portal/-/issues/7", "nils");
        IssueView view = (await issues.ViewsAsync(ours, ticket.Id)).Single();

        view.IsOpen.Should().BeFalse();
        view.Labels.Should().Equal("sso");
        view.Assignees.Should().Equal("erik");
        http.Requests.Single().Headers.GetValues("PRIVATE-TOKEN").Should().Equal("glpat-x");
        http.Requests.Single().Headers.Authorization.Should().BeNull();
    }

    [Fact]
    public async Task An_issue_that_cannot_be_read_keeps_its_link_and_says_why()
    {
        Ticket ticket = await Raise();
        await issues.SaveTrackerAsync(ours, null, IssueTrackerKind.GitHub, "github.com", true, null, "nils");
        http.Answer("https://api.github.com/repos/acme/secret/issues/1", HttpStatusCode.NotFound, "{}");

        await issues.LinkAsync(ours, ticket.Id, "https://github.com/acme/secret/issues/1", "nils");
        IssueView view = (await issues.ViewsAsync(ours, ticket.Id)).Single();

        view.Title.Should().BeNull();
        view.Problem.Should().Contain("Not found");
        view.Reference.Should().Be("acme/secret#1");
        http.Requests.Single().Headers.Authorization.Should().BeNull("no token was stored");
    }

    [Fact]
    public async Task A_removed_or_switched_off_tracker_is_not_called_for_existing_links()
    {
        Ticket ticket = await Raise();
        IssueTracker gh = await issues.SaveTrackerAsync(ours, null, IssueTrackerKind.GitHub, "github.com", true, null, "nils");
        await issues.LinkAsync(ours, ticket.Id, "https://github.com/acme/portal/issues/12", "nils");

        await issues.SaveTrackerAsync(ours, gh.Id, IssueTrackerKind.GitHub, "github.com", false, null, "nils");
        (await issues.ViewsAsync(ours, ticket.Id)).Single().Problem.Should().Contain("switched off");

        await issues.DeleteTrackerAsync(ours, gh.Id);
        (await issues.ViewsAsync(ours, ticket.Id)).Single().Problem.Should().Contain("no longer set up");

        http.Requests.Should().BeEmpty();
    }

    /// <summary>
    /// Stranger first, then the owner: if the remembered answer were keyed by address alone, the
    /// owner would be served the stranger's "not found" — and, the other way round, a stranger the
    /// owner's private issue.
    /// </summary>
    [Fact]
    public async Task Two_tenants_linking_the_same_issue_each_read_it_with_their_own_token()
    {
        Ticket mine = await Raise();
        Ticket theirsTicket = await Raise(theirs);
        await issues.SaveTrackerAsync(theirs, null, IssueTrackerKind.GitHub, "github.com", true, "ghp_theirs", "eve");
        await issues.SaveTrackerAsync(ours, null, IssueTrackerKind.GitHub, "github.com", true, "ghp_ours", "nils");
        http.Answer("https://api.github.com/repos/acme/private/issues/5", req =>
            req.Headers.Authorization?.Parameter == "ghp_ours"
                ? (HttpStatusCode.OK, """{"title":"Our private issue","state":"open"}""")
                : (HttpStatusCode.NotFound, "{}"));

        await issues.LinkAsync(theirs, theirsTicket.Id, "https://github.com/acme/private/issues/5", "eve");
        await issues.LinkAsync(ours, mine.Id, "https://github.com/acme/private/issues/5", "nils");

        (await issues.ViewsAsync(theirs, theirsTicket.Id)).Single().Title.Should().BeNull();
        (await issues.ViewsAsync(ours, mine.Id)).Single().Title.Should().Be("Our private issue");
        (await issues.ViewsAsync(theirs, theirsTicket.Id)).Single().Title.Should().BeNull();
    }

    [Fact]
    public async Task A_tenant_cannot_link_to_unlink_from_or_use_another_tenants_tracker_or_ticket()
    {
        Ticket theirsTicket = await Raise(theirs);
        Ticket mine = await Raise();
        await issues.SaveTrackerAsync(theirs, null, IssueTrackerKind.GitHub, "github.com", true, "ghp_theirs", "eve");
        TicketIssueLink theirLink = await issues.LinkAsync(theirs, theirsTicket.Id, "https://github.com/a/b/issues/1", "eve");

        Func<Task> onTheirTicket = () => issues.LinkAsync(ours, theirsTicket.Id, "https://github.com/a/b/issues/1", "nils");
        await onTheirTicket.Should().ThrowAsync<InvalidOperationException>().WithMessage("*no such ticket*");

        Func<Task> withTheirTracker = () => issues.LinkAsync(ours, mine.Id, "https://github.com/a/b/issues/1", "nils");
        await withTheirTracker.Should().ThrowAsync<InvalidOperationException>().WithMessage("*not set up*");

        (await issues.UnlinkAsync(ours, theirLink.Id)).Should().BeFalse();
        (await issues.LinksAsync(theirs, theirsTicket.Id)).Should().ContainSingle();
    }

    [Fact]
    public async Task The_same_issue_linked_twice_is_one_link_and_one_host_is_one_tracker()
    {
        Ticket ticket = await Raise();
        await issues.SaveTrackerAsync(ours, null, IssueTrackerKind.GitHub, "github.com", true, null, "nils");

        TicketIssueLink first = await issues.LinkAsync(ours, ticket.Id, "https://github.com/a/b/issues/1", "nils");
        TicketIssueLink again = await issues.LinkAsync(ours, ticket.Id, "https://github.com/a/b/issues/1#top", "nils");

        again.Id.Should().Be(first.Id);
        Func<Task> twice = () => issues.SaveTrackerAsync(ours, null, IssueTrackerKind.GitHub, "GitHub.com", true, null, "nils");
        await twice.Should().ThrowAsync<InvalidOperationException>().WithMessage("*already set up*");
    }

    [Fact]
    public async Task Saving_a_tracker_keeps_replaces_or_removes_its_token()
    {
        IssueTracker t = await issues.SaveTrackerAsync(ours, null, IssueTrackerKind.GitHub, "github.com", true, "one", "nils");

        await issues.SaveTrackerAsync(ours, t.Id, IssueTrackerKind.GitHub, "github.com", true, null, "nils");
        tokens.Stored[(ours, t.Id)].Should().Be("one");

        await issues.SaveTrackerAsync(ours, t.Id, IssueTrackerKind.GitHub, "github.com", true, "two", "nils");
        tokens.Stored[(ours, t.Id)].Should().Be("two");

        await issues.SaveTrackerAsync(ours, t.Id, IssueTrackerKind.GitHub, "github.com", true, "", "nils");
        tokens.Stored.Should().NotContainKey((ours, t.Id));
    }

    // ───────────────────────────── fakes ─────────────────────────────

    /// <summary>Tokens by (tenant, tracker). Tenant-keyed on purpose: the vault is.</summary>
    private sealed class MemoryTokens : IIssueTrackerTokens
    {
        public Dictionary<(Guid, Guid), string> Stored { get; } = [];

        public Task<string?> GetAsync(Guid tenantId, Guid trackerId, CancellationToken ct) =>
            Task.FromResult(Stored.GetValueOrDefault((tenantId, trackerId)));

        public Task<bool> HasAsync(Guid tenantId, Guid trackerId, CancellationToken ct) =>
            Task.FromResult(Stored.ContainsKey((tenantId, trackerId)));

        public Task SetAsync(Guid tenantId, Guid trackerId, string? token, CancellationToken ct)
        {
            if (string.IsNullOrEmpty(token)) Stored.Remove((tenantId, trackerId)); else Stored[(tenantId, trackerId)] = token;
            return Task.CompletedTask;
        }
    }

    /// <summary>Answers by exact address, recording every request; anything unplanned is a 500.</summary>
    private sealed class StubTrackers : HttpMessageHandler, IHttpClientFactory
    {
        private readonly Dictionary<string, Func<HttpRequestMessage, (HttpStatusCode, string)>> answers = [];

        public List<HttpRequestMessage> Requests { get; } = [];

        public void Answer(string url, HttpStatusCode code, string body) => answers[url] = _ => (code, body);

        public void Answer(string url, Func<HttpRequestMessage, (HttpStatusCode, string)> answer) => answers[url] = answer;

        public HttpClient CreateClient(string name) => new(this, disposeHandler: false);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add(request);
            (HttpStatusCode code, string body) = answers.TryGetValue(request.RequestUri!.AbsoluteUri, out var answer)
                ? answer(request)
                : (HttpStatusCode.InternalServerError, "{}");
            return Task.FromResult(new HttpResponseMessage(code) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
        }
    }
}

/// <summary>
/// The issue-link service for render tests that do not exercise it: no tokens, and an HTTP factory
/// that fails the test if anything is called — with no trackers set up, nothing may be.
/// </summary>
internal static class NoIssueTrackers
{
    public static IssueLinkService For(IDbContextFactory<EntKube.Web.Data.Modules.SupportDbContext> factory) =>
        new(factory, new NoTokens(), new NoHttp(), new MemoryCache(new MemoryCacheOptions()));

    private sealed class NoTokens : IIssueTrackerTokens
    {
        public Task<string?> GetAsync(Guid tenantId, Guid trackerId, CancellationToken ct) => Task.FromResult<string?>(null);

        public Task<bool> HasAsync(Guid tenantId, Guid trackerId, CancellationToken ct) => Task.FromResult(false);

        public Task SetAsync(Guid tenantId, Guid trackerId, string? token, CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class NoHttp : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => throw new InvalidOperationException("No issue tracker may be called here.");
    }
}
