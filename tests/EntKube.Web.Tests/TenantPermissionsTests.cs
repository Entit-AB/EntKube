using EntKube.Web.Data;
using EntKube.Web.Services;
using FluentAssertions;
using Microsoft.AspNetCore.Identity;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace EntKube.Web.Tests;

/// <summary>
/// What a person may do in a tenant, decided by the role they hold there.
///
/// <para>Until now being a member was the permission: of eighteen features only two were ever
/// asked about. These tests pin the three things that change that without taking anything away
/// from anybody — how a grant is read, how the roles that already exist are carried across, and
/// what a brand-new tenant's roles start with.</para>
/// </summary>
public class TenantPermissionsTests : IDisposable
{
    private readonly SqliteConnection connection;
    private readonly ApplicationDbContext db;
    private readonly TenantRoleService roles;

    private readonly Guid ours = Guid.NewGuid();
    private readonly Guid theirs = Guid.NewGuid();

    public TenantPermissionsTests()
    {
        connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();

        db = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(connection).Options);
        db.Database.EnsureCreated();

        db.Tenants.AddRange(
            new Tenant { Id = ours, Name = "Entit", Slug = "entit" },
            new Tenant { Id = theirs, Name = "Stranger", Slug = "stranger" });
        db.SaveChanges();

        roles = new TenantRoleService(new TestDbContextFactory(connection));
    }

    public void Dispose()
    {
        db.Dispose();
        connection.Dispose();
    }

    // ───────────────────────────── Reading a grant ─────────────────────────────

    [Fact]
    public async Task A_member_holds_exactly_the_level_their_role_grants()
    {
        // An agent who may work tickets but not merge them or move them between tenants.
        Member("user-anna", ours, Role(ours, "Agent", Grants((TenantFeature.SupportTickets, AccessLevel.Edit))));

        EffectivePermissions anna = await roles.PermissionsForAsync(ours, "user-anna");

        anna.Allows(TenantFeature.SupportTickets, AccessLevel.View).Should().BeTrue();
        anna.Allows(TenantFeature.SupportTickets, AccessLevel.Edit).Should().BeTrue();
        anna.Allows(TenantFeature.SupportTickets, AccessLevel.Manage).Should().BeFalse();
    }

    [Fact]
    public async Task A_feature_the_role_does_not_mention_is_not_granted()
    {
        Member("user-anna", ours, Role(ours, "Agent", Grants((TenantFeature.SupportTickets, AccessLevel.Manage))));

        EffectivePermissions anna = await roles.PermissionsForAsync(ours, "user-anna");

        anna.Allows(TenantFeature.CustomerAgreements, AccessLevel.View).Should().BeFalse();
    }

    [Fact]
    public async Task A_role_in_another_tenant_grants_nothing_here()
    {
        // Manage on tickets over there; an ordinary member with nothing over here.
        Member("user-anna", theirs, Role(theirs, "Lead", Grants((TenantFeature.SupportTickets, AccessLevel.Manage))));
        Member("user-anna", ours, Role(ours, "Agent", null));

        EffectivePermissions anna = await roles.PermissionsForAsync(ours, "user-anna");

        anna.Allows(TenantFeature.SupportTickets, AccessLevel.View).Should().BeFalse();
    }

    [Fact]
    public async Task Somebody_who_is_not_a_member_holds_nothing()
    {
        Role(ours, "Agent", Grants((TenantFeature.SupportTickets, AccessLevel.Manage)));
        db.Users.Add(new ApplicationUser { Id = "user-eve", UserName = "eve" });
        db.SaveChanges();

        (await roles.PermissionsForAsync(ours, "user-eve")).Allows(TenantFeature.SupportTickets, AccessLevel.View).Should().BeFalse();
        (await roles.PermissionsForAsync(ours, null)).Allows(TenantFeature.SupportTickets, AccessLevel.View).Should().BeFalse();
    }

    [Fact]
    public async Task A_global_admin_holds_everything_without_being_a_member()
    {
        db.Users.Add(new ApplicationUser { Id = "user-root", UserName = "root" });
        db.Roles.Add(new IdentityRole("Admin") { Id = "role-admin", NormalizedName = "ADMIN" });
        db.UserRoles.Add(new IdentityUserRole<string> { UserId = "user-root", RoleId = "role-admin" });
        db.SaveChanges();

        EffectivePermissions root = await roles.PermissionsForAsync(ours, "user-root");

        root.Allows(TenantFeature.CustomerPortalAccess, AccessLevel.Manage).Should().BeTrue();
    }

    // ───────────────────────────── Carrying existing roles across ─────────────────────────────

    [Fact]
    public void A_role_that_never_had_permissions_keeps_everything_it_could_already_do()
    {
        // Before this, a role with no permissions at all still opened every support page,
        // because membership was the only check. Carrying it across must not take that away.
        Dictionary<TenantFeature, AccessLevel> upgraded =
            PermissionCatalog.CarryAcross([], knowledgeBaseEditorsChosen: false);

        foreach (TenantFeature feature in PermissionCatalog.IntroducedByEnforcement)
        {
            upgraded.Should().Contain(feature, PermissionCatalog.Describe(feature).Highest);
        }

        upgraded.Should().Contain(TenantFeature.KnowledgeBase, AccessLevel.Manage);
        upgraded.Should().Contain(TenantFeature.Customers, AccessLevel.Manage);
    }

    [Fact]
    public void A_feature_whose_highest_level_is_View_is_carried_across_at_View()
    {
        // Reporting only reads. Granting it "Manage" would store a level the roles page cannot show.
        Dictionary<TenantFeature, AccessLevel> upgraded =
            PermissionCatalog.CarryAcross([], knowledgeBaseEditorsChosen: false);

        upgraded.Should().Contain(TenantFeature.SupportReporting, AccessLevel.View);
    }

    [Fact]
    public void A_customers_level_an_admin_chose_is_kept_now_that_it_means_something()
    {
        Dictionary<TenantFeature, AccessLevel> upgraded = PermissionCatalog.CarryAcross(
            new() { [TenantFeature.Customers] = AccessLevel.View, [TenantFeature.Clusters] = AccessLevel.Edit },
            knowledgeBaseEditorsChosen: false);

        upgraded.Should().Contain(TenantFeature.Customers, AccessLevel.View);
        upgraded.Should().Contain(TenantFeature.Clusters, AccessLevel.Edit);
    }

    [Fact]
    public void Where_knowledge_base_editors_were_chosen_everyone_else_keeps_reading_but_not_editing()
    {
        // The old opt-in: once some role was made the knowledge base's editor, everybody else
        // could only read it. Both halves of that survive.
        Dictionary<TenantFeature, AccessLevel> editor = PermissionCatalog.CarryAcross(
            new() { [TenantFeature.KnowledgeBase] = AccessLevel.Edit }, knowledgeBaseEditorsChosen: true);
        Dictionary<TenantFeature, AccessLevel> agent = PermissionCatalog.CarryAcross([], knowledgeBaseEditorsChosen: true);

        editor.Should().Contain(TenantFeature.KnowledgeBase, AccessLevel.Edit);
        agent.Should().Contain(TenantFeature.KnowledgeBase, AccessLevel.View);
    }

    [Fact]
    public async Task Existing_roles_are_carried_across_once()
    {
        Guid editors = Role(ours, "KB editors", Grants((TenantFeature.KnowledgeBase, AccessLevel.Edit)), schema: 0);
        Guid agents = Role(ours, "Agents", null, schema: 0);
        Guid elsewhere = Role(theirs, "Members", null, schema: 0);

        IReadOnlyList<RolePermissionUpgrade> first = await roles.CarryRolesAcrossAsync();
        IReadOnlyList<RolePermissionUpgrade> second = await roles.CarryRolesAcrossAsync();

        first.Sum(u => u.Roles).Should().Be(3);
        second.Should().BeEmpty();

        // The opt-in is judged per tenant: choosing editors over here says nothing about over there.
        Stored(editors).Should().Contain(TenantFeature.KnowledgeBase, AccessLevel.Edit);
        Stored(agents).Should().Contain(TenantFeature.KnowledgeBase, AccessLevel.View);
        Stored(elsewhere).Should().Contain(TenantFeature.KnowledgeBase, AccessLevel.Manage);
        Stored(agents).Should().Contain(TenantFeature.SupportTickets, AccessLevel.Manage);

        db.TenantRoles.AsNoTracking().Should().OnlyContain(r => r.PermissionSchema == TenantRole.CurrentPermissionSchema);
    }

    [Fact]
    public async Task A_role_made_after_enforcement_is_never_carried_across()
    {
        // A new role starts with nothing. If the carry-across touched it, every role created from
        // now on would quietly gain Manage on support at the next restart.
        TenantRole made = await roles.CreateRoleAsync(ours, "Contractors");

        await roles.CarryRolesAcrossAsync();

        Stored(made.Id).Should().BeEmpty();
    }

    [Fact]
    public void A_role_restored_from_an_older_backup_is_carried_across_after_the_restore()
    {
        // A role read from a bundle written before enforcement has no schema in it. Left at the
        // entity's default it would count as already carried across, and lose its support access.
        TenantRole restored = new() { Id = Guid.NewGuid(), TenantId = ours, Name = "Member", PermissionSchema = TenantRole.CurrentPermissionSchema };

        BackupService.PrepareTenantRolesForRestore([restored], bundleVersion: 22);

        restored.PermissionSchema.Should().Be(0);
    }

    [Fact]
    public void A_role_restored_from_a_current_backup_is_left_as_it_was()
    {
        TenantRole restored = new() { Id = Guid.NewGuid(), TenantId = ours, Name = "Member", PermissionSchema = TenantRole.CurrentPermissionSchema };

        BackupService.PrepareTenantRolesForRestore([restored], bundleVersion: Data.Backup.BackupBundle.CurrentVersion);

        restored.PermissionSchema.Should().Be(TenantRole.CurrentPermissionSchema);
    }

    // ───────────────────────────── A new tenant's roles ─────────────────────────────

    [Fact]
    public void A_new_tenants_roles_start_with_what_their_names_promise()
    {
        List<TenantRole> made = PermissionCatalog.DefaultRolesFor(ours);

        Dictionary<string, Dictionary<TenantFeature, AccessLevel>> byName =
            made.ToDictionary(r => r.Name, r => TenantRoleService.DecodePermissions(r.PermissionsJson));

        byName.Keys.Should().BeEquivalentTo("Administrator", "Member", "Viewer");

        foreach (FeaturePermission f in PermissionCatalog.Features)
        {
            byName["Administrator"].Should().Contain(f.Feature, f.Highest);
        }

        byName["Member"].Should().Contain(TenantFeature.SupportTickets, AccessLevel.Edit);
        byName["Member"].Should().NotContainKey(TenantFeature.CustomerPortalAccess);
        byName["Viewer"].Should().Contain(TenantFeature.SupportTickets, AccessLevel.View);
        byName["Viewer"].Should().NotContainKey(TenantFeature.SupportTools);

        made.Should().OnlyContain(r => r.TenantId == ours && r.PermissionSchema == TenantRole.CurrentPermissionSchema);
    }

    // ───────────────────────────── The catalog ─────────────────────────────

    [Fact]
    public void Every_feature_is_on_the_roles_page()
    {
        PermissionCatalog.Features.Select(f => f.Feature).Should().BeEquivalentTo(Enum.GetValues<TenantFeature>());
    }

    [Fact]
    public void Every_feature_can_be_switched_off_and_offers_its_levels_in_order()
    {
        foreach (FeaturePermission f in PermissionCatalog.Features)
        {
            f.Levels.Should().StartWith(AccessLevel.None, f.Label);
            f.Levels.Should().BeInAscendingOrder(f.Label);
            f.Levels.Should().HaveCountGreaterThan(1, f.Label);
            f.Levels.Skip(1).Should().OnlyContain(level => f.Describe(level).Length > 0, f.Label);
        }
    }

    /// <summary>
    /// Stored grants are keyed by the feature's number. Appending is the only safe change: a
    /// member inserted earlier would re-point every grant already stored after it.
    /// </summary>
    [Fact]
    public void The_new_features_keep_their_stored_numbers()
    {
        ((int)TenantFeature.SupportTickets).Should().Be(18);
        ((int)TenantFeature.SupportTools).Should().Be(19);
        ((int)TenantFeature.SupportReporting).Should().Be(20);
        ((int)TenantFeature.SupportRoster).Should().Be(21);
        ((int)TenantFeature.SupportChannels).Should().Be(22);
        ((int)TenantFeature.ServiceLevels).Should().Be(23);
        ((int)TenantFeature.Incidents).Should().Be(24);
        ((int)TenantFeature.CustomerAgreements).Should().Be(25);
        ((int)TenantFeature.CustomerPortalAccess).Should().Be(26);
    }

    // ───────────────────────────── Helpers ─────────────────────────────

    private static string Grants(params (TenantFeature Feature, AccessLevel Level)[] grants) =>
        TenantRoleService.EncodePermissions(grants.ToDictionary(g => g.Feature, g => g.Level));

    private Guid Role(Guid tenant, string name, string? permissions, int schema = TenantRole.CurrentPermissionSchema)
    {
        Guid id = Guid.NewGuid();
        db.TenantRoles.Add(new TenantRole { Id = id, TenantId = tenant, Name = name, PermissionsJson = permissions, PermissionSchema = schema });
        db.SaveChanges();
        return id;
    }

    private void Member(string userId, Guid tenant, Guid role)
    {
        if (!db.Users.Any(u => u.Id == userId))
        {
            db.Users.Add(new ApplicationUser { Id = userId, UserName = userId });
        }

        db.TenantMemberships.Add(new TenantMembership { UserId = userId, TenantId = tenant, RoleId = role });
        db.SaveChanges();
    }

    private Dictionary<TenantFeature, AccessLevel> Stored(Guid roleId) =>
        TenantRoleService.DecodePermissions(db.TenantRoles.AsNoTracking().Single(r => r.Id == roleId).PermissionsJson);
}
