using System.Reflection;
using EntKube.Web.Data;
using EntKube.Web.Services.Mail;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace EntKube.Web.Tests;

/// <summary>
/// Saving the mailbox settings over the ones already stored.
///
/// <para><b>Why a reflective test rather than a few assertions.</b> Updating an existing
/// mailbox copies the settings across field by field, and a field left out of that list
/// fails in the quietest way there is: the screen accepts the change, says it saved,
/// reloads showing the new value from the form — and the poller goes on using the old one
/// until somebody reopens the page in a fresh session. Nothing builds wrong and no test
/// about the feature itself notices, because the feature works; only the saving does not.
/// This one fails the moment a new setting is added and not copied.</para>
/// </summary>
public class SupportMailboxSettingsTests : IDisposable
{
    /// <summary>
    /// What the poller writes for itself, and what nobody sets. Everything else on the
    /// entity is configuration, and so must survive a save — including whatever is added
    /// next, which is the whole point of listing the exceptions rather than the rule.
    /// </summary>
    private static readonly HashSet<string> NotConfiguration =
    [
        nameof(SupportMailbox.Id),
        nameof(SupportMailbox.TenantId),
        nameof(SupportMailbox.LastSeenUid),
        nameof(SupportMailbox.LastUidValidity),
        nameof(SupportMailbox.LastPolledAt),
        nameof(SupportMailbox.LastMessageAt),
        nameof(SupportMailbox.LastError),
        nameof(SupportMailbox.ConsecutiveFailures),
        nameof(SupportMailbox.CreatedAt),
        nameof(SupportMailbox.UpdatedAt),
        nameof(SupportMailbox.Tenant),
    ];

    private readonly SqliteConnection connection;
    private readonly ApplicationDbContext db;
    private readonly SupportMailboxService mailboxes;
    private readonly Guid tenantId = Guid.NewGuid();

    public SupportMailboxSettingsTests()
    {
        connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();

        db = new ApplicationDbContext(
            new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(connection).Options);
        db.Database.EnsureCreated();
        db.Tenants.Add(new Tenant { Id = tenantId, Name = "ENTIT", Slug = "entit" });
        db.SaveChanges();

        // The vault and the ingest pipeline are only reached by a password and a poll,
        // neither of which happens here.
        mailboxes = new SupportMailboxService(
            new TestDbContextFactory(connection), null!, null!,
            NullLogger<SupportMailboxService>.Instance);
    }

    public void Dispose()
    {
        db.Dispose();
        connection.Dispose();
        GC.SuppressFinalize(this);
    }

    private static IEnumerable<PropertyInfo> Settings =>
        typeof(SupportMailbox).GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.CanWrite && !NotConfiguration.Contains(p.Name));

    [Fact]
    public async Task Every_setting_survives_a_save_over_an_existing_mailbox()
    {
        await mailboxes.SaveAsync(
            new SupportMailbox
            {
                TenantId = tenantId,
                Host = "imap.entit.example",
                Username = "support@entit.example",
            },
            password: null);

        SupportMailbox changed = new() { TenantId = tenantId, Host = "", Username = "" };
        Dictionary<string, object?> intended = [];

        foreach (PropertyInfo property in Settings)
        {
            object? value = Distinctive(property);
            property.SetValue(changed, value);
            intended[property.Name] = value;
        }

        await mailboxes.SaveAsync(changed, password: null);

        SupportMailbox? stored = await db.SupportMailboxes.AsNoTracking()
            .FirstOrDefaultAsync(m => m.TenantId == tenantId);

        stored.Should().NotBeNull();

        foreach ((string name, object? value) in intended)
        {
            typeof(SupportMailbox).GetProperty(name)!.GetValue(stored)
                .Should().Be(value, $"the {name} somebody typed is what the poller must use");
        }
    }

    /// <summary>A value nothing else would produce, so a field left uncopied cannot pass.</summary>
    private static object? Distinctive(PropertyInfo property)
    {
        Type type = Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType;

        if (type == typeof(string))
        {
            return $"{property.Name}-set-by-the-test";
        }

        if (type == typeof(bool))
        {
            // The opposite of whatever a new mailbox starts with.
            return !(bool)(property.GetValue(new SupportMailbox { Host = "", Username = "" })
                           ?? false);
        }

        if (type == typeof(int))
        {
            return 3607;
        }

        if (type.IsEnum)
        {
            // Any value that is not the default, so leaving the field out cannot match.
            return Enum.GetValues(type).Cast<object>()
                .First(v => (int)v != 0);
        }

        throw new InvalidOperationException(
            $"{property.Name} is a {type.Name}, which this test does not know how to set. "
            + "Teach it, or add the property to NotConfiguration and say why.");
    }
}
