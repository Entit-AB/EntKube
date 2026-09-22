using EntKube.Web.Data;
using EntKube.Web.Services.Mail;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace EntKube.Web.Tests;

/// <summary>
/// The phrases the support mailbox watches for, as tenant configuration.
///
/// <para>These are the customer's words, not ours — a Swedish customer reports that
/// something "fungerar inte", and nobody can guess the list in advance. What matters is
/// that a tenant can take the phrases over, that taking them over is a starting point
/// rather than a reset, and that a phrase somebody deletes does not come back because it
/// is still in the code.</para>
/// </summary>
public class MailTriageRuleTests : IDisposable
{
    private readonly SqliteConnection connection;
    private readonly ApplicationDbContext db;
    private readonly MailTriageRuleService rules;

    private readonly Guid tenantId = Guid.NewGuid();
    private readonly Guid otherTenantId = Guid.NewGuid();

    public MailTriageRuleTests()
    {
        connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();

        db = new ApplicationDbContext(
            new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(connection).Options);
        db.Database.EnsureCreated();

        db.Tenants.AddRange(
            new Tenant { Id = tenantId, Name = "ENTIT", Slug = "entit" },
            new Tenant { Id = otherTenantId, Name = "Other", Slug = "other" });
        db.SaveChanges();

        rules = new MailTriageRuleService(new TestDbContextFactory(connection));
    }

    public void Dispose()
    {
        db.Dispose();
        connection.Dispose();
        GC.SuppressFinalize(this);
    }

    private Task<MailTriageRule> Add(
        MailSignal signal, string phrase, TicketPriority? priority = null, string? criterion = null) =>
        rules.SaveAsync(new MailTriageRule
        {
            TenantId = tenantId,
            Signal = signal,
            Phrase = phrase,
            Priority = priority,
            Criterion = criterion,
        });

    // ---- The built-in set --------------------------------------------------------------

    /// <summary>
    /// The mailbox has to work before anybody configures anything, so a tenant with no
    /// rules of its own runs on the built-in list.
    /// </summary>
    [Fact]
    public async Task A_tenant_with_no_rules_uses_the_built_in_set()
    {
        MailTriageRuleSet effective = await rules.GetEffectiveAsync(tenantId);

        effective.Should().BeSameAs(MailTriageRuleSet.BuiltIn);
        (await rules.IsConfiguredAsync(tenantId)).Should().BeFalse();
    }

    /// <summary>
    /// The examples are Swedish and English on purpose: the agreement is Swedish, and a
    /// table of English phrases would recognise nothing a Swedish customer writes.
    /// </summary>
    [Theory]
    [InlineData("ingen kan logga in i systemet", TicketPriority.P1)]
    [InlineData("nobody can log in — all users affected", TicketPriority.P1)]
    [InlineData("exporten fungerar inte", TicketPriority.P2)]
    [InlineData("ett stavfel på sidan", TicketPriority.P4)]
    [InlineData("something a bit odd", TicketPriority.P3)]
    public void The_built_in_phrases_cover_both_languages(string text, TicketPriority expected)
    {
        (TicketPriority priority, _) = MailTriageRuleSet.BuiltIn.ProposePriority(text);

        priority.Should().Be(expected);
    }

    [Fact]
    public void Every_built_in_priority_phrase_says_what_it_is_evidence_of() =>
        MailTriageRuleSet.BuiltIn.Rules
            .Where(r => r.Signal == MailSignal.Priority)
            .Should().OnlyContain(r => !string.IsNullOrWhiteSpace(r.Criterion));

    // ---- Taking them over ----------------------------------------------------------------

    [Fact]
    public async Task Adopting_the_built_ins_copies_them_in_for_editing()
    {
        int copied = await rules.AdoptBuiltInAsync(tenantId);

        copied.Should().Be(MailTriageRuleSet.BuiltIn.Rules.Count);
        (await rules.IsConfiguredAsync(tenantId)).Should().BeTrue();
        (await rules.ListAsync(tenantId)).Should().HaveCount(copied);
    }

    /// <summary>A starting point, not a reset — adopting twice must not duplicate everything.</summary>
    [Fact]
    public async Task Adopting_twice_does_nothing_the_second_time()
    {
        await rules.AdoptBuiltInAsync(tenantId);
        int second = await rules.AdoptBuiltInAsync(tenantId);

        second.Should().Be(0);
        (await rules.ListAsync(tenantId)).Should().HaveCount(MailTriageRuleSet.BuiltIn.Rules.Count);
    }

    /// <summary>
    /// The point of owning the rules. A tenant that deletes a phrase must not have it
    /// quietly reappear because the built-in list still mentions it.
    /// </summary>
    [Fact]
    public async Task A_deleted_phrase_stays_deleted()
    {
        await rules.AdoptBuiltInAsync(tenantId);

        MailTriageRule doomed = (await rules.ListAsync(tenantId))
            .First(r => r.Phrase == "ingen kan logga in");

        await rules.DeleteAsync(doomed.Id);

        MailTriageRuleSet effective = await rules.GetEffectiveAsync(tenantId);

        effective.Rules.Should().NotContain(r => r.Phrase == "ingen kan logga in");
        effective.ProposePriority("ingen kan logga in").Priority.Should().NotBe(TicketPriority.P1);
    }

    [Fact]
    public async Task One_tenants_rules_do_not_reach_another()
    {
        await Add(MailSignal.Priority, "brandlarm", TicketPriority.P1, "the building is on fire");

        MailTriageRuleSet mine = await rules.GetEffectiveAsync(tenantId);
        MailTriageRuleSet theirs = await rules.GetEffectiveAsync(otherTenantId);

        mine.ProposePriority("brandlarm i huset").Priority.Should().Be(TicketPriority.P1);
        theirs.Should().BeSameAs(MailTriageRuleSet.BuiltIn);
    }

    // ---- Editing --------------------------------------------------------------------------

    [Fact]
    public async Task A_new_phrase_takes_effect()
    {
        await Add(MailSignal.Priority, "journalen låser sig", TicketPriority.P1,
            "the clinical record freezes");

        MailTriageRuleSet effective = await rules.GetEffectiveAsync(tenantId);
        (TicketPriority priority, string? criterion) =
            effective.ProposePriority("journalen låser sig när vi sparar");

        priority.Should().Be(TicketPriority.P1);
        criterion.Should().Be("the clinical record freezes");
    }

    [Fact]
    public async Task Matching_ignores_case_and_surrounding_text()
    {
        await Add(MailSignal.Priority, "Kan Inte Boka", TicketPriority.P2, "booking is unavailable");

        MailTriageRuleSet effective = await rules.GetEffectiveAsync(tenantId);

        effective.ProposePriority("hej! vi kan inte boka några tider idag")
            .Priority.Should().Be(TicketPriority.P2);
    }

    [Fact]
    public async Task A_disabled_phrase_stops_matching()
    {
        MailTriageRule rule = await Add(
            MailSignal.Priority, "kan inte boka", TicketPriority.P2, "booking is unavailable");

        rule.IsEnabled = false;
        await rules.SaveAsync(rule);

        MailTriageRuleSet effective = await rules.GetEffectiveAsync(tenantId);

        effective.ProposePriority("vi kan inte boka").Priority.Should().Be(TicketPriority.P3);
    }

    /// <summary>
    /// Two rows for the same words would make the proposal depend on which the ordering
    /// happened to reach first, so the pairing is unique per signal.
    /// </summary>
    [Fact]
    public async Task The_same_phrase_cannot_be_added_twice_for_one_signal()
    {
        await Add(MailSignal.Priority, "kan inte boka", TicketPriority.P2, "booking is unavailable");

        Func<Task> act = async () =>
            await Add(MailSignal.Priority, "kan inte boka", TicketPriority.P1, "something else");

        await act.Should().ThrowAsync<DbUpdateException>();
    }

    /// <summary>
    /// A criterion on a development or third-party rule would show an operator a reason
    /// for a decision that rule does not make, so it is dropped on save.
    /// </summary>
    [Fact]
    public async Task Only_a_priority_rule_keeps_a_priority_and_a_criterion()
    {
        MailTriageRule saved = await rules.SaveAsync(new MailTriageRule
        {
            TenantId = tenantId,
            Signal = MailSignal.ThirdParty,
            Phrase = "regionens driftpartner",
            Priority = TicketPriority.P1,
            Criterion = "should not survive",
        });

        saved.Priority.Should().BeNull();
        saved.Criterion.Should().BeNull();
    }

    [Fact]
    public async Task Phrases_are_trimmed_so_a_stray_space_does_not_stop_a_match()
    {
        await Add(MailSignal.ThirdParty, "  regionens driftpartner  ");

        MailTriageRuleSet effective = await rules.GetEffectiveAsync(tenantId);

        effective.MentionsThirdParty("vi inväntar regionens driftpartner").Should().BeTrue();
    }

    // ---- The other two signals ---------------------------------------------------------------

    [Fact]
    public async Task A_development_phrase_flags_a_request_as_section_15()
    {
        await rules.AdoptBuiltInAsync(tenantId);

        MailTriageRuleSet effective = await rules.GetEffectiveAsync(tenantId);

        effective.LooksLikeDevelopment("vi skulle vilja ha en ny rapport").Should().BeTrue();
        effective.LooksLikeDevelopment("exporten kraschar").Should().BeFalse();
    }

    [Fact]
    public async Task A_third_party_phrase_suggests_a_pause()
    {
        await rules.AdoptBuiltInAsync(tenantId);

        MailTriageRuleSet effective = await rules.GetEffectiveAsync(tenantId);

        effective.MentionsThirdParty("vi väntar på hosting").Should().BeTrue();
        effective.MentionsThirdParty("allt fungerar nu").Should().BeFalse();
    }

    // ---- Preview -------------------------------------------------------------------------------

    /// <summary>
    /// The point of editable rules is seeing what an edit does. The preview answers that
    /// without waiting for a real message to arrive.
    /// </summary>
    [Fact]
    public async Task The_preview_shows_what_a_sample_would_become()
    {
        await Add(MailSignal.Priority, "journalen låser sig", TicketPriority.P1,
            "the clinical record freezes");
        await Add(MailSignal.ThirdParty, "regionens driftpartner");

        var preview = await rules.PreviewAsync(
            tenantId, "Journalen låser sig och regionens driftpartner svarar inte");

        preview.Priority.Should().Be(TicketPriority.P1);
        preview.Criterion.Should().Be("the clinical record freezes");
        preview.ThirdParty.Should().BeTrue();
        preview.Development.Should().BeFalse();
    }

    [Fact]
    public async Task The_preview_of_an_unconfigured_tenant_uses_the_built_ins()
    {
        var preview = await rules.PreviewAsync(tenantId, "Ingen kan logga in");

        preview.Priority.Should().Be(TicketPriority.P1);
        preview.Criterion.Should().Be("login impossible for all users");
    }
}
