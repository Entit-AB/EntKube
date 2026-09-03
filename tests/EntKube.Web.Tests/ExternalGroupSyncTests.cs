using System.Security.Claims;
using EntKube.Web.Data;
using EntKube.Web.Services.Sso;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace EntKube.Web.Tests;

/// <summary>
/// Tests for SSO group → tenant access reconciliation.
///
/// The dangerous behaviour here is revocation: getting it wrong either leaves
/// offboarded people with access, or deletes access an operator granted by hand.
/// Both directions are pinned.
/// </summary>
public class ExternalGroupSyncTests : IDisposable
{
    private readonly SqliteConnection connection;
    private readonly ApplicationDbContext db;
    private readonly ExternalGroupSync sut;

    private readonly Guid tenantA = Guid.NewGuid();
    private readonly Guid tenantB = Guid.NewGuid();
    private readonly Guid manualTenant = Guid.NewGuid();
    private readonly Guid roleAdmin = Guid.NewGuid();
    private readonly Guid roleViewer = Guid.NewGuid();
    private readonly Guid roleManual = Guid.NewGuid();
    private const string UserId = "user-1";

    public ExternalGroupSyncTests()
    {
        connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();

        db = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseSqlite(connection).Options);
        db.Database.EnsureCreated();

        db.Users.Add(new ApplicationUser { Id = UserId, UserName = "u@example.com", Email = "u@example.com" });

        db.Tenants.AddRange(
            new Tenant { Id = tenantA, Name = "A", Slug = "a" },
            new Tenant { Id = tenantB, Name = "B", Slug = "b" },
            new Tenant { Id = manualTenant, Name = "Manual", Slug = "manual" });

        db.TenantRoles.AddRange(
            new TenantRole { Id = roleAdmin, TenantId = tenantA, Name = "Admin" },
            new TenantRole { Id = roleViewer, TenantId = tenantA, Name = "Viewer" },
            new TenantRole { Id = roleManual, TenantId = manualTenant, Name = "Admin" });

        db.SaveChanges();

        sut = new ExternalGroupSync(new TestDbContextFactory(connection), NullLogger<ExternalGroupSync>.Instance);
    }

    public void Dispose()
    {
        db.Dispose();
        connection.Dispose();
        GC.SuppressFinalize(this);
    }

    private void Map(string group, Guid tenantId, Guid roleId)
    {
        db.ExternalGroupMappings.Add(new ExternalGroupMapping
        {
            Id = Guid.NewGuid(),
            ExternalGroup = group,
            TenantId = tenantId,
            RoleId = roleId,
        });
        db.SaveChanges();
    }

    private void GrantManually(Guid tenantId, Guid roleId)
    {
        db.TenantMemberships.Add(new TenantMembership
        {
            UserId = UserId, TenantId = tenantId, RoleId = roleId,
        });
        db.SaveChanges();
    }

    private static ClaimsPrincipal PrincipalWith(string claimType, params string[] values) =>
        new(new ClaimsIdentity(values.Select(v => new Claim(claimType, v))));

    private List<TenantMembership> Memberships() =>
        [.. db.TenantMemberships.AsNoTracking().Where(m => m.UserId == UserId)];

    // ── Reading groups from the token ──

    [Fact]
    public void Reads_repeated_group_claims()
    {
        ExternalGroupSync.ReadGroups(PrincipalWith("groups", "eng", "ops"), "groups")
            .Should().BeEquivalentTo(["eng", "ops"]);
    }

    [Fact]
    public void Reads_groups_packed_into_a_single_json_array_claim()
    {
        // Some providers emit one claim holding a JSON array rather than repeated claims;
        // reading zero groups there would silently revoke everyone's access.
        ExternalGroupSync.ReadGroups(PrincipalWith("groups", """["eng","ops"]"""), "groups")
            .Should().BeEquivalentTo(["eng", "ops"]);
    }

    [Fact]
    public void A_bracketed_value_that_is_not_json_is_treated_as_a_literal_group()
    {
        ExternalGroupSync.ReadGroups(PrincipalWith("groups", "[not json"), "groups")
            .Should().BeEquivalentTo(["[not json"]);
    }

    [Fact]
    public void Duplicate_and_blank_group_values_are_dropped()
    {
        ExternalGroupSync.ReadGroups(PrincipalWith("groups", "eng", "eng", "  ", "ops"), "groups")
            .Should().BeEquivalentTo(["eng", "ops"]);
    }

    [Fact]
    public void Groups_are_read_from_the_configured_claim_only()
    {
        ExternalGroupSync.ReadGroups(PrincipalWith("roles", "eng"), "groups").Should().BeEmpty();
        ExternalGroupSync.ReadGroups(PrincipalWith("roles", "eng"), "roles").Should().BeEquivalentTo(["eng"]);
    }

    // ── Granting ──

    [Fact]
    public async Task A_mapped_group_grants_membership_with_the_mapped_role()
    {
        Map("eng", tenantA, roleAdmin);

        GroupSyncResult result = await sut.SyncAsync(UserId, PrincipalWith("groups", "eng"), "groups");

        result.Granted.Should().BeEquivalentTo([tenantA]);
        Memberships().Should().ContainSingle()
            .Which.Should().Match<TenantMembership>(m => m.TenantId == tenantA && m.RoleId == roleAdmin);
    }

    [Fact]
    public async Task An_unmapped_group_grants_nothing()
    {
        Map("eng", tenantA, roleAdmin);

        GroupSyncResult result = await sut.SyncAsync(UserId, PrincipalWith("groups", "marketing"), "groups");

        result.Granted.Should().BeEmpty();
        result.HasNoAccess.Should().BeTrue();
        Memberships().Should().BeEmpty();
    }

    [Fact]
    public async Task Group_matching_is_exact_not_normalised()
    {
        // Entra emits object ids; case-folding or trimming could match a group the
        // operator never intended to grant.
        Map("Engineering", tenantA, roleAdmin);

        (await sut.SyncAsync(UserId, PrincipalWith("groups", "engineering"), "groups"))
            .Granted.Should().BeEmpty();
    }

    [Fact]
    public async Task A_role_change_in_the_mapping_updates_the_existing_membership()
    {
        Map("eng", tenantA, roleAdmin);
        await sut.SyncAsync(UserId, PrincipalWith("groups", "eng"), "groups");

        db.ExternalGroupMappings.Single().RoleId = roleViewer;
        await db.SaveChangesAsync();

        GroupSyncResult result = await sut.SyncAsync(UserId, PrincipalWith("groups", "eng"), "groups");

        result.Updated.Should().BeEquivalentTo([tenantA]);
        Memberships().Single().RoleId.Should().Be(roleViewer);
    }

    [Fact]
    public async Task Two_groups_mapping_to_the_same_tenant_resolve_deterministically()
    {
        // Whichever wins, the same token must always produce the same access — otherwise
        // a user's role would flicker with claim ordering.
        Map("aaa", tenantA, roleViewer);
        Map("zzz", tenantA, roleAdmin);

        await sut.SyncAsync(UserId, PrincipalWith("groups", "zzz", "aaa"), "groups");
        Guid first = Memberships().Single().RoleId;

        db.TenantMemberships.RemoveRange(db.TenantMemberships);
        await db.SaveChangesAsync();

        await sut.SyncAsync(UserId, PrincipalWith("groups", "aaa", "zzz"), "groups");
        Memberships().Single().RoleId.Should().Be(first);
    }

    // ── Revoking — the dangerous half ──

    [Fact]
    public async Task Losing_a_group_revokes_the_access_it_granted()
    {
        // This is what makes offboarding work.
        Map("eng", tenantA, roleAdmin);
        await sut.SyncAsync(UserId, PrincipalWith("groups", "eng"), "groups");
        Memberships().Should().ContainSingle();

        GroupSyncResult result = await sut.SyncAsync(UserId, PrincipalWith("groups"), "groups");

        result.Revoked.Should().BeEquivalentTo([tenantA]);
        Memberships().Should().BeEmpty();
        result.HasNoAccess.Should().BeTrue();
    }

    [Fact]
    public async Task A_manually_granted_membership_in_an_unmapped_tenant_is_never_revoked()
    {
        // Deleting an operator's hand-granted access because an unrelated SSO login did not
        // mention it would be a spectacular way to lock people out of their own platform.
        Map("eng", tenantA, roleAdmin);
        GrantManually(manualTenant, roleManual);

        GroupSyncResult result = await sut.SyncAsync(UserId, PrincipalWith("groups", "eng"), "groups");

        result.Revoked.Should().BeEmpty();
        Memberships().Should().Contain(m => m.TenantId == manualTenant);
    }

    [Fact]
    public async Task Revocation_is_confined_to_tenants_that_sso_actually_governs()
    {
        Map("eng", tenantA, roleAdmin);
        GrantManually(manualTenant, roleManual);
        GrantManually(tenantA, roleViewer);

        // No groups asserted at all: only the SSO-governed tenant loses access.
        await sut.SyncAsync(UserId, PrincipalWith("groups"), "groups");

        Memberships().Should().ContainSingle().Which.TenantId.Should().Be(manualTenant);
    }

    [Fact]
    public async Task A_user_keeping_one_group_and_losing_another_keeps_the_right_access()
    {
        Map("eng", tenantA, roleAdmin);
        Map("ops", tenantB, roleAdmin);
        await sut.SyncAsync(UserId, PrincipalWith("groups", "eng", "ops"), "groups");
        Memberships().Should().HaveCount(2);

        GroupSyncResult result = await sut.SyncAsync(UserId, PrincipalWith("groups", "eng"), "groups");

        result.Revoked.Should().BeEquivalentTo([tenantB]);
        Memberships().Should().ContainSingle().Which.TenantId.Should().Be(tenantA);
        result.HasNoAccess.Should().BeFalse();
    }

    [Fact]
    public async Task A_sync_that_changes_nothing_reports_nothing()
    {
        Map("eng", tenantA, roleAdmin);
        await sut.SyncAsync(UserId, PrincipalWith("groups", "eng"), "groups");

        GroupSyncResult result = await sut.SyncAsync(UserId, PrincipalWith("groups", "eng"), "groups");

        result.Granted.Should().BeEmpty();
        result.Revoked.Should().BeEmpty();
        result.Updated.Should().BeEmpty();
        result.HasNoAccess.Should().BeFalse();
    }

    [Fact]
    public async Task With_no_mappings_configured_at_all_nothing_is_touched()
    {
        // An instance that has not configured SSO group mapping must not have its
        // hand-granted memberships quietly deleted by an SSO login.
        GrantManually(manualTenant, roleManual);

        GroupSyncResult result = await sut.SyncAsync(UserId, PrincipalWith("groups", "eng"), "groups");

        result.Revoked.Should().BeEmpty();
        Memberships().Should().ContainSingle();
    }
}
