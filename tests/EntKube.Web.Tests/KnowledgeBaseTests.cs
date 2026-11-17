using EntKube.Web.Data;
using EntKube.Web.Services.Tickets;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace EntKube.Web.Tests;

/// <summary>
/// Phase 5 (docs/helpdesk.md): the help-answer library — who may read what, when a scheduled
/// answer appears, and that nothing crosses a tenant or reaches the wrong customer.
/// </summary>
public class KnowledgeBaseTests : IDisposable
{
    private readonly SqliteConnection connection;
    private readonly ApplicationDbContext db;
    private readonly KnowledgeBaseService kb;

    private readonly Guid ours = Guid.NewGuid();
    private readonly Guid theirs = Guid.NewGuid();
    private readonly Guid vard = Guid.NewGuid();
    private readonly Guid skola = Guid.NewGuid();
    private readonly Guid theirCustomer = Guid.NewGuid();

    private static readonly DateTime Now = new(2026, 11, 17, 12, 0, 0, DateTimeKind.Utc);

    public KnowledgeBaseTests()
    {
        connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();
        db = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(connection).Options);
        db.Database.EnsureCreated();
        db.Tenants.Add(new Tenant { Id = ours, Name = "ENTIT", Slug = "entit" });
        db.Tenants.Add(new Tenant { Id = theirs, Name = "Other", Slug = "other" });
        db.Customers.Add(new Customer { Id = vard, TenantId = ours, Name = "Vårdbolaget" });
        db.Customers.Add(new Customer { Id = skola, TenantId = ours, Name = "Skolbolaget" });
        db.Customers.Add(new Customer { Id = theirCustomer, TenantId = theirs, Name = "Regionen" });
        db.SaveChanges();

        kb = new KnowledgeBaseService(new TestDbContextFactory(connection));
    }

    public void Dispose()
    {
        db.Dispose();
        connection.Dispose();
        GC.SuppressFinalize(this);
    }

    private async Task<KbAnswer> Answer(KbCategory cat, string title, KbVisibility v, DateTime? publishAt = null, Guid? customer = null) =>
        await kb.SaveAnswerAsync(ours, new KbAnswer
        {
            Title = title, CategoryId = cat.Id, Visibility = v, PublishAt = publishAt, CustomerId = customer,
            BodyHtml = $"<p>How to {title.ToLowerInvariant()}.</p>", Language = "sv",
        }, "nils");

    [Fact]
    public async Task A_customer_sees_only_public_published_answers_for_them_or_for_everyone()
    {
        KbCategory cat = await kb.SaveCategoryAsync(ours, null, "Inloggning", null);
        await Answer(cat, "Reset password", KbVisibility.Public);
        await Answer(cat, "Admin trick", KbVisibility.Internal);
        await Answer(cat, "Half written", KbVisibility.Draft);
        await Answer(cat, "Old way", KbVisibility.Archived);
        await Answer(cat, "Next week", KbVisibility.Public, Now.AddDays(7));
        await Answer(cat, "Only for Vård", KbVisibility.Public, customer: vard);

        (await kb.ForCustomerAsync(ours, vard, null, Now)).Select(a => a.Title).Should().BeEquivalentTo(["Reset password", "Only for Vård"]);
        (await kb.ForCustomerAsync(ours, skola, null, Now)).Select(a => a.Title).Should().Equal("Reset password");
        (await kb.ForCustomerAsync(ours, vard, null, Now.AddDays(8))).Select(a => a.Title).Should().Contain("Next week");
        (await kb.ForCustomerAsync(theirs, theirCustomer, null, Now)).Should().BeEmpty();
    }

    /// <summary>
    /// An agent writing to one customer is offered internal answers too, but never one written
    /// for another customer — inserting it would put that customer's material in this mail.
    /// </summary>
    [Fact]
    public async Task An_agent_is_offered_published_answers_but_not_another_customers()
    {
        KbCategory cat = await kb.SaveCategoryAsync(ours, null, "Allmänt", null);
        await Answer(cat, "Reset password", KbVisibility.Public);
        await Answer(cat, "Admin trick", KbVisibility.Internal);
        await Answer(cat, "Half written", KbVisibility.Draft);
        await Answer(cat, "Only for Vård", KbVisibility.Public, customer: vard);

        (await kb.ForAgentAsync(ours, skola, null, Now)).Select(a => a.Title).Should().BeEquivalentTo(["Reset password", "Admin trick"]);
        (await kb.ForAgentAsync(ours, vard, "vård", Now)).Select(a => a.Title).Should().Equal("Only for Vård");
    }

    [Fact]
    public async Task A_customer_cannot_open_an_answer_they_may_not_see_by_its_id()
    {
        KbCategory cat = await kb.SaveCategoryAsync(ours, null, "Allmänt", null);
        KbAnswer secret = await Answer(cat, "Admin trick", KbVisibility.Internal);
        KbAnswer forVard = await Answer(cat, "Only for Vård", KbVisibility.Public, customer: vard);

        (await kb.GetForCustomerAsync(ours, skola, secret.Id, Now)).Should().BeNull();
        (await kb.GetForCustomerAsync(ours, skola, forVard.Id, Now)).Should().BeNull();
        (await kb.GetForCustomerAsync(ours, vard, forVard.Id, Now)).Should().NotBeNull();
        (await kb.GetForCustomerAsync(theirs, theirCustomer, forVard.Id, Now)).Should().BeNull();
    }

    [Fact]
    public async Task The_body_is_sanitised_when_saved_and_its_text_is_searchable()
    {
        KbCategory cat = await kb.SaveCategoryAsync(ours, null, "Allmänt", null);
        KbAnswer a = await kb.SaveAnswerAsync(ours, new KbAnswer
        {
            Title = "VPN", CategoryId = cat.Id, Visibility = KbVisibility.Public, Language = "en",
            BodyHtml = "<p>Install the <strong>client</strong></p><script>steal()</script>",
        }, "nils");

        a.BodyHtml.Should().Be("<p>Install the <strong>client</strong></p>");
        (await kb.ForCustomerAsync(ours, vard, "CLIENT install", Now)).Should().ContainSingle();
        (await kb.ForCustomerAsync(ours, vard, "steal", Now)).Should().BeEmpty();
    }

    [Fact]
    public async Task Categories_move_with_their_contents_but_never_under_themselves_and_only_empty_ones_go()
    {
        KbCategory top = await kb.SaveCategoryAsync(ours, null, "Top", null);
        KbCategory child = await kb.SaveCategoryAsync(ours, null, "Child", top.Id);
        await Answer(child, "Something", KbVisibility.Public);

        Func<Task> cycle = () => kb.SaveCategoryAsync(ours, top.Id, "Top", child.Id);
        await cycle.Should().ThrowAsync<InvalidOperationException>().WithMessage("*under itself*");

        Func<Task> deleteFull = () => kb.DeleteCategoryAsync(ours, top.Id);
        await deleteFull.Should().ThrowAsync<InvalidOperationException>().WithMessage("*subcategories*");

        Func<Task> otherTenantParent = () => kb.SaveCategoryAsync(theirs, null, "X", top.Id);
        await otherTenantParent.Should().ThrowAsync<InvalidOperationException>();
    }

    [Theory]
    [InlineData("sv", true)]
    [InlineData("en-GB", true)]
    [InlineData("swedish", false)]
    [InlineData("", false)]
    public async Task The_language_is_a_tag(string language, bool ok)
    {
        KbCategory cat = await kb.SaveCategoryAsync(ours, null, "Allmänt", null);

        Func<Task> save = () => kb.SaveAnswerAsync(ours, new KbAnswer
        {
            Title = "X", CategoryId = cat.Id, Visibility = KbVisibility.Draft, Language = language,
        }, "nils");

        if (ok) await save.Should().NotThrowAsync(); else await save.Should().ThrowAsync<ArgumentException>();
    }
}
