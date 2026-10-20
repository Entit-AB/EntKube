using EntKube.Web.Data;
using EntKube.Web.Data.Modules;
using EntKube.Web.Services;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace EntKube.Web.Tests;

/// <summary>
/// Whose alert routing rule, and whose notification channel.
///
/// <para>A routing rule decides which channel an incident is delivered to. That makes two
/// things worth stealing, and both arrived as bare ids:</para>
///
/// <list type="number">
///   <item>The <b>rule</b>. <c>UpdateRuleAsync</c> and <c>DeleteRuleAsync</c> found it with
///   <c>FindAsync([id])</c> — a key lookup, which cannot carry a predicate. Editing somebody
///   else's rule changes what pages them; deleting it stops them being paged at all; setting
///   <c>SuppressIncident</c> drops their alerts silently, which is the quiet version of the
///   same thing.</item>
///   <item>The <b>channel</b>. Both create and update assigned <c>rule.ChannelId = channelId</c>
///   with nothing checking whose channel that was. Pointing our rule at their channel sends our
///   incidents to them. The sharper direction is the other one: take <i>their</i> rule, point it
///   at <i>our</i> channel, and their incident notifications — cluster names, namespaces, alert
///   text — are delivered to us.</item>
/// </list>
/// </summary>
public class AlertRoutingTenancyTests : IDisposable
{
    private readonly SqliteConnection connection;
    private readonly TelemetryDbContext db;
    private readonly AlertRoutingService sut;

    private readonly Guid tenantId = Guid.NewGuid();
    private readonly Guid otherTenantId = Guid.NewGuid();

    public AlertRoutingTenancyTests()
    {
        connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();

        TestDbContextFactory factory = new(connection);
        db = ((IDbContextFactory<TelemetryDbContext>)factory).CreateDbContext();
        db.Database.EnsureCreated();

        using (ApplicationDbContext seed = new(
            new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(connection).Options))
        {
            seed.Tenants.AddRange(
                new Tenant { Id = tenantId, Name = "ENTIT", Slug = $"entit-{tenantId:N}" },
                new Tenant { Id = otherTenantId, Name = "Annan", Slug = $"annan-{otherTenantId:N}" });
            seed.SaveChanges();
        }

        sut = new AlertRoutingService(factory);
    }

    public void Dispose()
    {
        db.Dispose();
        connection.Dispose();
        GC.SuppressFinalize(this);
    }

    private Guid SeedChannel(Guid owner, string name)
    {
        NotificationChannel channel = new()
        {
            Id = Guid.NewGuid(),
            TenantId = owner,
            Name = name,
            Type = NotificationChannelType.Teams,
            ConfigurationJson = """{"webhookUrl":"https://example.invalid/hook"}""",
        };
        db.NotificationChannels.Add(channel);
        db.SaveChanges();
        return channel.Id;
    }

    private Guid SeedRule(Guid owner, Guid? channelId = null, string name = "Journal critical")
    {
        AlertRoutingRule rule = new()
        {
            Id = Guid.NewGuid(),
            TenantId = owner,
            Name = name,
            Priority = 10,
            ChannelId = channelId,
            MatchSeverity = "critical",
            IsEnabled = true,
        };
        db.AlertRoutingRules.Add(rule);
        db.SaveChanges();
        return rule.Id;
    }

    [Fact]
    public async Task Updating_a_rule_of_another_tenant_changes_nothing()
    {
        Guid theirChannel = SeedChannel(otherTenantId, "Deras Teams");
        Guid theirRule = SeedRule(otherTenantId, theirChannel);

        await sut.UpdateRuleAsync(
            tenantId, theirRule, "Tystad", 1, null,
            null, null, null, null, null, isEnabled: false, suppressIncident: true);

        AlertRoutingRule stored = await db.AlertRoutingRules.AsNoTracking()
            .FirstAsync(r => r.Id == theirRule);
        stored.Name.Should().Be("Journal critical");
        stored.IsEnabled.Should().BeTrue();
        stored.SuppressIncident.Should().BeFalse();
        stored.ChannelId.Should().Be(theirChannel);
    }

    [Fact]
    public async Task Deleting_a_rule_of_another_tenant_leaves_it_in_place()
    {
        Guid theirRule = SeedRule(otherTenantId);

        await sut.DeleteRuleAsync(tenantId, theirRule);

        (await db.AlertRoutingRules.AsNoTracking().AnyAsync(r => r.Id == theirRule))
            .Should().BeTrue();
    }

    [Fact]
    public async Task A_rule_cannot_be_created_pointing_at_another_tenants_channel()
    {
        Guid theirChannel = SeedChannel(otherTenantId, "Deras Teams");

        Func<Task> act = () => sut.CreateRuleAsync(
            tenantId, "Vidarebefordra", 10, theirChannel,
            null, null, "critical", null, null);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*not this tenant's*");
        (await db.AlertRoutingRules.AsNoTracking().AnyAsync()).Should().BeFalse();
    }

    [Fact]
    public async Task A_rule_cannot_be_repointed_at_another_tenants_channel()
    {
        Guid ourChannel = SeedChannel(tenantId, "Vår Teams");
        Guid theirChannel = SeedChannel(otherTenantId, "Deras Teams");
        Guid ourRule = SeedRule(tenantId, ourChannel);

        Func<Task> act = () => sut.UpdateRuleAsync(
            tenantId, ourRule, "Journal critical", 10, theirChannel,
            null, null, "critical", null, null, isEnabled: true);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*not this tenant's*");
        AlertRoutingRule stored = await db.AlertRoutingRules.AsNoTracking()
            .FirstAsync(r => r.Id == ourRule);
        stored.ChannelId.Should().Be(ourChannel);
    }

    /// <summary>
    /// The sharp direction. Not "can I reach their rule" — the row predicate already covers
    /// that — but what the attempt is <i>for</i>: taking a rule that fires on their alerts and
    /// making it deliver to a channel we own. The rule must keep its channel.
    /// </summary>
    [Fact]
    public async Task Another_tenants_rule_cannot_be_redirected_to_our_channel()
    {
        Guid ourChannel = SeedChannel(tenantId, "Vår Teams");
        Guid theirChannel = SeedChannel(otherTenantId, "Deras Teams");
        Guid theirRule = SeedRule(otherTenantId, theirChannel);

        await sut.UpdateRuleAsync(
            tenantId, theirRule, "Journal critical", 10, ourChannel,
            null, null, "critical", null, null, isEnabled: true);

        AlertRoutingRule stored = await db.AlertRoutingRules.AsNoTracking()
            .FirstAsync(r => r.Id == theirRule);
        stored.ChannelId.Should().Be(theirChannel);
    }

    [Fact]
    public async Task A_rule_naming_our_own_channel_is_created_and_updated()
    {
        Guid ourChannel = SeedChannel(tenantId, "Vår Teams");

        Guid id = await sut.CreateRuleAsync(
            tenantId, "Journal critical", 10, ourChannel,
            null, null, "critical", null, null);

        await sut.UpdateRuleAsync(
            tenantId, id, "Journal critical (dygnet runt)", 5, ourChannel,
            null, null, "critical", null, null, isEnabled: true);

        AlertRoutingRule stored = await db.AlertRoutingRules.AsNoTracking()
            .FirstAsync(r => r.Id == id);
        stored.TenantId.Should().Be(tenantId);
        stored.Name.Should().Be("Journal critical (dygnet runt)");
        stored.Priority.Should().Be(5);
        stored.ChannelId.Should().Be(ourChannel);

        await sut.DeleteRuleAsync(tenantId, id);
        (await db.AlertRoutingRules.AsNoTracking().AnyAsync(r => r.Id == id)).Should().BeFalse();
    }

    /// <summary>
    /// A suppression rule carries no channel. The ownership check must pass a null channel
    /// through rather than refusing — otherwise the guard would break the feature instead of
    /// protecting it.
    /// </summary>
    [Fact]
    public async Task A_suppression_rule_without_a_channel_is_allowed()
    {
        Guid id = await sut.CreateRuleAsync(
            tenantId, "Tysta testmiljön", 1, null,
            null, "test", null, null, null, suppressIncident: true);

        AlertRoutingRule stored = await db.AlertRoutingRules.AsNoTracking()
            .FirstAsync(r => r.Id == id);
        stored.ChannelId.Should().BeNull();
        stored.SuppressIncident.Should().BeTrue();
    }
}
