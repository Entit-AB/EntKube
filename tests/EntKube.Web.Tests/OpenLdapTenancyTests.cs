using EntKube.Web.Data;
using EntKube.Web.Data.Modules;
using EntKube.Web.Services;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace EntKube.Web.Tests;

/// <summary>
/// Whose directory a method acts on.
///
/// <para>Only <see cref="OpenLdapComponentConfig"/> carries a TenantId; the OUs, users and
/// groups under it carry a ConfigId — so every one of these methods took an id that named a
/// row without naming an owner. What is behind them is a live LDAP directory: the authored
/// state is rendered into the seed LDIF and applied on the next install or upgrade, so a row
/// written here becomes an account, a group or a membership on somebody's directory server.</para>
/// </summary>
public class OpenLdapTenancyTests : IDisposable
{
    private readonly SqliteConnection connection;
    private readonly DataServicesDbContext db;
    private readonly OpenLdapService sut;

    private readonly Guid tenantId = Guid.NewGuid();
    private readonly Guid configId = Guid.NewGuid();

    private readonly Guid otherTenantId = Guid.NewGuid();
    private readonly Guid theirConfigId = Guid.NewGuid();

    public OpenLdapTenancyTests()
    {
        connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();

        TestDbContextFactory factory = new(connection);
        db = ((IDbContextFactory<DataServicesDbContext>)factory).CreateDbContext();
        db.Database.EnsureCreated();

        using (ApplicationDbContext seed = new(
            new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(connection).Options))
        {
            seed.Tenants.AddRange(
                new Tenant { Id = tenantId, Name = "ENTIT", Slug = $"entit-{tenantId:N}" },
                new Tenant { Id = otherTenantId, Name = "Annan", Slug = $"annan-{otherTenantId:N}" });
            seed.SaveChanges();
        }

        db.OpenLdapComponentConfigs.AddRange(
            new OpenLdapComponentConfig { Id = configId, TenantId = tenantId, BaseDn = "dc=entit,dc=se" },
            new OpenLdapComponentConfig
            {
                Id = theirConfigId, TenantId = otherTenantId, BaseDn = "dc=annan,dc=se",
            });
        db.SaveChanges();

        sut = new OpenLdapService(factory, null!, null!, null!, null!, null!,
            NullLogger<OpenLdapService>.Instance);
    }

    public void Dispose()
    {
        db.Dispose();
        connection.Dispose();
        GC.SuppressFinalize(this);
    }

    /// <summary>One of everything in the other tenant's directory.</summary>
    private (Guid Ou, Guid User, Guid Group) Theirs()
    {
        Guid ou = Guid.NewGuid(), user = Guid.NewGuid(), group = Guid.NewGuid();

        db.OpenLdapOrganizationalUnits.Add(new OpenLdapOrganizationalUnit
        {
            Id = ou, ConfigId = theirConfigId, Name = "people",
        });
        db.OpenLdapUsers.Add(new OpenLdapUser
        {
            Id = user, ConfigId = theirConfigId, Uid = "anna", Cn = "Anna Andersson",
            PasswordSsha = "{SSHA}theirs", Enabled = true,
        });
        db.OpenLdapGroups.Add(new OpenLdapGroup
        {
            Id = group, ConfigId = theirConfigId, Cn = "journal-admins",
        });
        db.OpenLdapGroupMembers.Add(new OpenLdapGroupMember
        {
            Id = Guid.NewGuid(), GroupId = group, UserId = user,
        });
        db.SaveChanges();

        return (ou, user, group);
    }

    private OpenLdapUser Ours(string uid = "nils")
    {
        OpenLdapUser user = new()
        {
            Id = Guid.NewGuid(), ConfigId = configId, Uid = uid, Cn = uid, Enabled = true,
        };
        db.OpenLdapUsers.Add(user);
        db.SaveChanges();
        return user;
    }

    [Fact]
    public async Task Another_tenants_directory_is_not_readable()
    {
        Theirs();

        (await sut.GetConfigAsync(tenantId, configId)).Should().NotBeNull();
        (await sut.GetConfigAsync(tenantId, theirConfigId)).Should().BeNull();
    }

    /// <summary>
    /// <b>An account with a password we chose, in a directory that is not ours.</b> The
    /// ConfigId rides on the entity and the password is hashed into the row, so by id alone
    /// this created a credential on somebody else's directory server.
    /// </summary>
    [Fact]
    public async Task An_account_cannot_be_created_in_another_tenants_directory()
    {
        Func<Task> act = () => sut.AddUserAsync(tenantId, new OpenLdapUser
        {
            ConfigId = theirConfigId, Uid = "backdoor", Cn = "Backdoor", Enabled = true,
        }, "hunter2");

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("Directory not found, or it is not this tenant's.");

        (await db.OpenLdapUsers.AsNoTracking().CountAsync(u => u.ConfigId == theirConfigId))
            .Should().Be(0);
    }

    /// <summary>
    /// <b>Re-passwording somebody else's account.</b> newPassword is written to whichever row
    /// the Id names, and an LDAP account is what the applications behind that directory
    /// authenticate against.
    /// </summary>
    [Fact]
    public async Task Another_tenants_account_cannot_be_re_passworded_or_disabled()
    {
        (Guid _, Guid theirUser, Guid _) = Theirs();

        Func<Task> act = () => sut.UpdateUserAsync(tenantId, new OpenLdapUser
        {
            Id = theirUser, ConfigId = theirConfigId, Uid = "anna", Cn = "Anna Andersson",
            Enabled = false,
        }, "hunter2");

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("User not found, or it is not this tenant's.");

        OpenLdapUser stored = await db.OpenLdapUsers.AsNoTracking().FirstAsync(u => u.Id == theirUser);
        stored.PasswordSsha.Should().Be("{SSHA}theirs");
        stored.Enabled.Should().BeTrue();
    }

    [Fact]
    public async Task Another_tenants_rows_cannot_be_deleted()
    {
        (Guid theirOu, Guid theirUser, Guid theirGroup) = Theirs();

        await sut.DeleteOuAsync(tenantId, theirOu);
        await sut.DeleteUserAsync(tenantId, theirUser);
        await sut.DeleteGroupAsync(tenantId, theirGroup);

        (await db.OpenLdapOrganizationalUnits.AsNoTracking().AnyAsync(o => o.Id == theirOu))
            .Should().BeTrue();
        (await db.OpenLdapUsers.AsNoTracking().AnyAsync(u => u.Id == theirUser)).Should().BeTrue();
        (await db.OpenLdapGroups.AsNoTracking().AnyAsync(g => g.Id == theirGroup)).Should().BeTrue();

        // DeleteUserAsync clears the user's memberships before the user, and both halves need
        // the tenant: taking somebody out of their own LDAP groups is a removal of access,
        // which is its own kind of damage even when the account survives.
        (await db.OpenLdapGroupMembers.AsNoTracking().AnyAsync(m => m.UserId == theirUser))
            .Should().BeTrue();
    }

    [Fact]
    public async Task Nothing_can_be_added_to_another_tenants_directory()
    {
        Func<Task> ou = () => sut.AddOuAsync(tenantId, theirConfigId, "ours", null);
        Func<Task> group = () => sut.AddGroupAsync(tenantId, new OpenLdapGroup
        {
            ConfigId = theirConfigId, Cn = "ours",
        });

        await ou.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("Directory not found, or it is not this tenant's.");
        await group.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("Directory not found, or it is not this tenant's.");
    }

    /// <summary>
    /// <b>Group membership in a directory is authorization</b> — whatever the group grants,
    /// whoever is in it gets. Setting the members of a group by id alone was granting
    /// ourselves whatever somebody else's group grants.
    /// </summary>
    [Fact]
    public async Task Another_tenants_group_membership_cannot_be_set()
    {
        (Guid _, Guid theirUser, Guid theirGroup) = Theirs();
        OpenLdapUser ours = Ours();

        Func<Task> act = () => sut.SetGroupMembersAsync(tenantId, theirGroup, [ours.Id]);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("Group not found, or it is not this tenant's.");

        // Their group still has exactly the member it had — ours was not added, and theirs
        // was not swept away by the delete-then-insert this method does.
        List<Guid> members = await db.OpenLdapGroupMembers.AsNoTracking()
            .Where(m => m.GroupId == theirGroup)
            .Select(m => m.UserId)
            .ToListAsync();

        members.Should().Equal(theirUser);
        members.Should().NotContain(ours.Id);
    }

    /// <summary>
    /// The members are checked against the group's own directory, not merely the tenant. A
    /// user from elsewhere has no DN under this base, and BuildSeedLdif drops a member it
    /// cannot resolve — so the row would persist as a membership the LDIF silently omits.
    /// </summary>
    [Fact]
    public async Task A_member_from_another_directory_is_refused()
    {
        (Guid _, Guid theirUser, Guid _) = Theirs();

        OpenLdapGroup ourGroup = await sut.AddGroupAsync(tenantId, new OpenLdapGroup
        {
            ConfigId = configId, Cn = "admins",
        });
        OpenLdapUser ourUser = Ours();

        // Ours alone is fine, so the refusal below is about the member and not the group.
        await sut.SetGroupMembersAsync(tenantId, ourGroup.Id, [ourUser.Id]);
        (await db.OpenLdapGroupMembers.AsNoTracking().CountAsync(m => m.GroupId == ourGroup.Id))
            .Should().Be(1);

        Func<Task> act = () => sut.SetGroupMembersAsync(tenantId, ourGroup.Id, [ourUser.Id, theirUser]);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("A member does not belong to this directory.");

        // And the refusal did not take the existing membership with it.
        (await db.OpenLdapGroupMembers.AsNoTracking().CountAsync(m => m.GroupId == ourGroup.Id))
            .Should().Be(1);
    }
}
