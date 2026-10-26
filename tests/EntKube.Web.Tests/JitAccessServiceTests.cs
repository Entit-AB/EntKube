using EntKube.Web.Data;
using EntKube.Web.Services;
using EntKube.Web.Services.Jit;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace EntKube.Web.Tests;

/// <summary>
/// The request → approve → revoke lifecycle of a just-in-time grant, with the cluster behind a
/// fake provisioner.
///
/// Most of what is asserted here is refusal. A grant that is issued when it should not have been
/// is the failure mode that matters, and every guard on that path — namespace exclusivity,
/// self-approval, approver permission, the duration ceiling — is a separate way it could be
/// issued wrongly.
/// </summary>
public class JitAccessServiceTests : IDisposable
{
    private readonly SqliteConnection connection;
    private readonly ApplicationDbContext db;
    private readonly FakeProvisioner provisioner = new();
    private readonly JitAccessService sut;
    private readonly TestDbContextFactory factory;

    private readonly Guid tenantId = Guid.NewGuid();
    private readonly Guid customerId = Guid.NewGuid();
    private readonly Guid appId = Guid.NewGuid();
    private readonly Guid envId = Guid.NewGuid();
    private readonly Guid clusterId = Guid.NewGuid();

    /// <summary>A second tenant that owns nothing here — the stranger asking the questions.</summary>
    private readonly Guid strangerTenant = Guid.NewGuid();
    private const string Namespace = "acme-billing";

    // Account ids and email addresses are deliberately different values here, because in
    // production they are: an account's id is a GUID and its username is an email. A fixture that
    // sets Id = email cannot tell a grant keyed correctly from one keyed to the wrong identifier —
    // which is exactly how a request nobody could see, and a queue nobody could approve, passed a
    // full suite.
    private const string SubjectId = "0c3e6a24-2b17-4a1e-9f0e-9f7b31c2a1d4";
    private const string SubjectEmail = "customer@acme.example";

    private const string ApproverId = "f26b8d91-6f4c-4f9b-8c2d-6a1f0b5e7c33";
    private const string ApproverEmail = "operator@entit.se";

    private const string DeployerId = "9b41c7e2-5a68-43d1-90b7-2f8c4d6e1a05";
    private const string DeployerEmail = "viewer@entit.se";

    private const string OtherSubjectId = "3d7a52f8-8c19-4b62-a5d3-7e0b94c1f682";
    private const string OtherSubjectEmail = "someone@acme.example";

    public JitAccessServiceTests()
    {
        connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();

        DbContextOptions<ApplicationDbContext> options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseSqlite(connection)
            .Options;

        db = new ApplicationDbContext(options);
        db.Database.EnsureCreated();

        Seed();

        factory = new TestDbContextFactory(connection);
        sut = new JitAccessService(
            factory, provisioner, new AuditService(factory, new EntKube.Web.Modules.Api.DeliveryApi(factory)), NullLogger<JitAccessService>.Instance);
    }

    // ── Namespace exclusivity ─────────────────────────────────────────────────

    [Fact]
    public async Task A_namespace_of_its_own_is_exclusive()
    {
        NamespaceExclusivityResult result =
            await sut.CheckNamespaceExclusivityAsync(tenantId, appId, clusterId, Namespace);

        result.IsExclusive.Should().BeTrue();
    }

    [Fact]
    public async Task A_namespace_shared_with_another_apps_deployment_is_refused()
    {
        // Namespace-scoped RBAC is the whole confinement. If a second app lives here, a correct
        // grant still exposes it.
        AddApp(Guid.NewGuid(), "Globex API", deployNamespace: Namespace);
        await db.SaveChangesAsync();

        NamespaceExclusivityResult result =
            await sut.CheckNamespaceExclusivityAsync(tenantId, appId, clusterId, Namespace);

        result.IsExclusive.Should().BeFalse();
        result.Reason.Should().Contain("Globex API");
    }

    [Fact]
    public async Task A_namespace_another_app_is_governance_locked_to_is_refused()
    {
        // An app can be locked to a namespace before it has ever deployed into it. It is still
        // going to be sharing.
        Guid otherAppId = Guid.NewGuid();
        AddApp(otherAppId, "Globex API", deployNamespace: "globex-api");
        db.AppEnvironments.Add(new AppEnvironment
        {
            AppId = otherAppId, EnvironmentId = envId, Namespace = Namespace
        });
        await db.SaveChangesAsync();

        (await sut.CheckNamespaceExclusivityAsync(tenantId, appId, clusterId, Namespace))
            .IsExclusive.Should().BeFalse();
    }

    [Fact]
    public async Task Requesting_into_a_shared_namespace_is_refused()
    {
        AddApp(Guid.NewGuid(), "Globex API", deployNamespace: Namespace);
        await db.SaveChangesAsync();

        Func<Task> act = () => sut.RequestAsync(tenantId, appId, envId, SubjectId, "debugging", SubjectEmail);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*shared*");
    }

    // ── Request ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_request_starts_pending_with_no_clock_running()
    {
        JitGrant grant = await sut.RequestAsync(tenantId, appId, envId, SubjectId, "debugging a 500", SubjectEmail);

        grant.StatusAt(DateTime.UtcNow).Should().Be(JitGrantStatus.Pending);
        grant.ExpiresAt.Should().BeNull("nothing has been granted yet");
        grant.TokenHash.Should().BeNull();
        grant.Namespace.Should().Be(Namespace);
        grant.TenantId.Should().Be(tenantId);
    }

    [Fact]
    public async Task A_request_without_a_reason_is_refused()
    {
        Func<Task> act = () => sut.RequestAsync(tenantId, appId, envId, SubjectId, "   ", SubjectEmail);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*reason*");
    }

    [Fact]
    public async Task An_app_with_no_deployment_here_has_nothing_to_grant()
    {
        Guid emptyApp = Guid.NewGuid();
        db.Apps.Add(new App { Id = emptyApp, CustomerId = customerId, Name = "Undeployed" });
        await db.SaveChangesAsync();

        Func<Task> act = () => sut.RequestAsync(tenantId, emptyApp, envId, SubjectId, "why not", SubjectEmail);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*no deployment*");
    }

    [Fact]
    public async Task An_app_on_two_clusters_must_say_which_one()
    {
        // "The app's cluster" is not well defined — an environment can host several.
        Guid secondCluster = AddCluster("second");
        db.AppDeployments.Add(new AppDeployment
        {
            Id = Guid.NewGuid(), AppId = appId, EnvironmentId = envId,
            ClusterId = secondCluster, Name = "billing-dr", Namespace = Namespace
        });
        await db.SaveChangesAsync();

        Func<Task> act = () => sut.RequestAsync(tenantId, appId, envId, SubjectId, "debugging", SubjectEmail);
        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*more than one cluster*");

        JitGrant grant = await sut.RequestAsync(tenantId, 
            appId, envId, SubjectId, "debugging", SubjectEmail, clusterId: secondCluster);
        grant.KubernetesClusterId.Should().Be(secondCluster);
    }

    [Fact]
    public async Task The_governance_locked_namespace_wins_over_the_deployments()
    {
        db.AppEnvironments.Add(new AppEnvironment
        {
            AppId = appId, EnvironmentId = envId, Namespace = "locked-ns"
        });
        await db.SaveChangesAsync();

        JitGrant grant = await sut.RequestAsync(tenantId, appId, envId, SubjectId, "debugging", SubjectEmail);

        grant.Namespace.Should().Be("locked-ns");
    }

    // ── What was asked for ────────────────────────────────────────────────────

    [Fact]
    public async Task A_request_records_the_level_and_length_it_asked_for()
    {
        JitGrant grant = await sut.RequestAsync(tenantId, 
            appId, envId, SubjectId, "restarting a stuck worker", SubjectEmail,
            requestedLevel: JitAccessLevel.Operate,
            requestedDuration: TimeSpan.FromHours(2));

        grant.RequestedLevel.Should().Be(JitAccessLevel.Operate);
        grant.RequestedMinutes.Should().Be(120);

        // Asking is not being granted. Level and ExpiresAt stay unset until somebody decides.
        grant.ApprovedAt.Should().BeNull();
        grant.ExpiresAt.Should().BeNull();
        grant.StatusAt(DateTime.UtcNow).Should().Be(JitGrantStatus.Pending);
    }

    [Fact]
    public async Task A_request_that_says_nothing_asks_for_the_least_and_the_default_length()
    {
        JitGrant grant = await sut.RequestAsync(tenantId, appId, envId, SubjectId, "debugging", SubjectEmail);

        grant.RequestedLevel.Should().Be(JitAccessLevel.Observe);
        grant.RequestedMinutes.Should().Be((int)JitAccessService.DefaultDuration.TotalMinutes);
    }

    [Fact]
    public async Task A_request_longer_than_the_ceiling_is_clamped_where_it_is_made()
    {
        // Clamped on the way in rather than at approval, so the queue never shows an approver a
        // request it would have to silently shorten.
        JitGrant grant = await sut.RequestAsync(tenantId, 
            appId, envId, SubjectId, "debugging", SubjectEmail,
            requestedDuration: TimeSpan.FromDays(7));

        grant.RequestedMinutes.Should().Be((int)JitAccessService.MaximumDuration.TotalMinutes);
        JitAccessService.DurationOptionsMinutes.Should().Contain(grant.RequestedMinutes);
    }

    [Fact]
    public async Task The_approver_is_not_bound_by_what_was_asked_for()
    {
        JitGrant requested = await sut.RequestAsync(tenantId, 
            appId, envId, SubjectId, "restarting a stuck worker", SubjectEmail,
            requestedLevel: JitAccessLevel.Operate,
            requestedDuration: TimeSpan.FromHours(4));

        JitApproval approval = await sut.ApproveAsync(tenantId, requested.Id, ApproverId, ApproverEmail, JitAccessLevel.Observe, TimeSpan.FromMinutes(15));

        approval.Grant.Level.Should().Be(JitAccessLevel.Observe);
        approval.Grant.ExpiresAt.Should().BeCloseTo(
            DateTime.UtcNow.AddMinutes(15), TimeSpan.FromMinutes(1));

        // The ask survives the decision — "asked for Operate, granted Observe" is a record worth
        // keeping, and it is the whole reason these are separate columns.
        approval.Grant.RequestedLevel.Should().Be(JitAccessLevel.Operate);
        approval.Grant.RequestedMinutes.Should().Be(240);
    }

    // ── Identity ──────────────────────────────────────────────────────────────
    //
    // A grant names a person twice: once as an account id, which is a key, and once as a display
    // name, which is not. Shipping those the wrong way round is silent — the request simply never
    // arrives and the queue simply has no approvers — so each direction is pinned here.

    [Fact]
    public async Task An_email_address_is_refused_as_the_subject_of_a_grant()
    {
        // The subject is a foreign key to an account. An email reaches the database as a key that
        // matches nothing, which used to surface as an unreadable constraint violation after the
        // request had apparently been accepted.
        Func<Task> act = () => sut.RequestAsync(tenantId, appId, envId, SubjectEmail, "debugging", SubjectEmail);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*could not be identified*");
    }

    [Fact]
    public async Task A_request_reaches_both_the_requester_and_the_tenant_queue()
    {
        JitGrant requested = await sut.RequestAsync(tenantId, appId, envId, SubjectId, "debugging", SubjectEmail);

        (await sut.ListForUserAsync(tenantId, SubjectId)).Should().ContainSingle(g => g.Id == requested.Id);
        (await sut.ListPendingAsync(tenantId)).Should().ContainSingle(g => g.Id == requested.Id);
    }

    [Fact]
    public async Task The_approver_permission_is_held_by_an_account_not_by_an_email()
    {
        (await sut.IsApproverAsync(tenantId, ApproverId)).Should().BeTrue();

        (await sut.IsApproverAsync(tenantId, ApproverEmail)).Should()
            .BeFalse("membership is keyed to an account id, so an email is a lookup that never matches");
    }

    // ── Approval ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task Approval_mints_a_credential_and_starts_the_clock()
    {
        JitGrant requested = await sut.RequestAsync(tenantId, appId, envId, SubjectId, "debugging", SubjectEmail);

        JitApproval approval = await sut.ApproveAsync(tenantId, requested.Id, ApproverId, ApproverEmail, JitAccessLevel.Observe);

        approval.Plaintext.Should().NotBeNullOrEmpty();
        approval.Grant.StatusAt(DateTime.UtcNow).Should().Be(JitGrantStatus.Active);
        approval.Grant.ExpiresAt.Should().BeCloseTo(
            DateTime.UtcNow.Add(JitAccessService.DefaultDuration), TimeSpan.FromMinutes(1));
        provisioner.Minted.Should().ContainSingle();
    }

    [Fact]
    public async Task The_plaintext_token_is_never_stored()
    {
        JitGrant requested = await sut.RequestAsync(tenantId, appId, envId, SubjectId, "debugging", SubjectEmail);
        JitApproval approval = await sut.ApproveAsync(tenantId, requested.Id, ApproverId, ApproverEmail, JitAccessLevel.Observe);

        JitGrant stored = (await sut.GetAsync(tenantId, requested.Id))!;

        stored.TokenHash.Should().NotBeNullOrEmpty();
        stored.TokenHash.Should().NotBe(approval.Plaintext);
        stored.DisplayPrefix.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task The_requester_cannot_approve_their_own_request()
    {
        JitGrant requested = await sut.RequestAsync(tenantId, appId, envId, SubjectId, "debugging", SubjectEmail);

        Func<Task> act = () =>
            sut.ApproveAsync(tenantId, requested.Id, SubjectId, SubjectEmail, JitAccessLevel.Observe);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*person who made it*");
        provisioner.Minted.Should().BeEmpty();
    }

    [Fact]
    public async Task The_subject_cannot_approve_a_request_filed_on_their_behalf()
    {
        // Approving your own request and approving one somebody filed for you are the same thing
        // from the cluster's side.
        JitGrant requested = await sut.RequestAsync(tenantId, 
            appId, envId, subjectUserId: OtherSubjectId, "debugging", requestedBy: ApproverEmail);

        Func<Task> act = () =>
            sut.ApproveAsync(tenantId, requested.Id, OtherSubjectId, OtherSubjectEmail, JitAccessLevel.Observe);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*grants access to*");
    }

    [Fact]
    public async Task Deploy_rights_do_not_carry_approval_rights()
    {
        // The reuse that was tempting and wrong. This user holds Manage on Deployments and can
        // change what runs in the namespace; that is not the same authority as letting somebody
        // outside the organisation into it.
        JitGrant requested = await sut.RequestAsync(tenantId, appId, envId, SubjectId, "debugging", SubjectEmail);

        Func<Task> act = () =>
            sut.ApproveAsync(tenantId, requested.Id, DeployerId, DeployerEmail, JitAccessLevel.Observe);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*permission*");
        provisioner.Minted.Should().BeEmpty();
    }

    [Fact]
    public async Task A_namespace_that_became_shared_between_request_and_approval_is_refused()
    {
        // Governance can re-point an app in the gap. Approval is where the namespace is frozen,
        // so it is where the check has to run again.
        JitGrant requested = await sut.RequestAsync(tenantId, appId, envId, SubjectId, "debugging", SubjectEmail);

        AddApp(Guid.NewGuid(), "Globex API", deployNamespace: Namespace);
        await db.SaveChangesAsync();

        Func<Task> act = () => sut.ApproveAsync(tenantId, requested.Id, ApproverId, ApproverEmail, JitAccessLevel.Observe);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*shared*");
    }

    [Fact]
    public async Task An_already_decided_request_cannot_be_approved_again()
    {
        JitGrant requested = await sut.RequestAsync(tenantId, appId, envId, SubjectId, "debugging", SubjectEmail);
        await sut.ApproveAsync(tenantId, requested.Id, ApproverId, ApproverEmail, JitAccessLevel.Observe);

        Func<Task> act = () => sut.ApproveAsync(tenantId, requested.Id, ApproverId, ApproverEmail, JitAccessLevel.Operate);

        await act.Should().ThrowAsync<InvalidOperationException>();
        provisioner.Minted.Should().ContainSingle("a second approval must not mint a second credential");
    }

    // ── Withdrawing and lapsing ───────────────────────────────────────────────

    [Fact]
    public async Task The_requester_can_take_back_a_request_nobody_has_answered()
    {
        JitGrant requested = await sut.RequestAsync(tenantId, appId, envId, SubjectId, "debugging", SubjectEmail);

        await sut.EndOwnGrantAsync(tenantId, requested.Id, SubjectId, SubjectEmail);

        JitGrant stored = (await sut.GetAsync(tenantId, requested.Id))!;
        stored.StatusAt(DateTime.UtcNow).Should().Be(JitGrantStatus.Withdrawn,
            "nothing was granted, so there was nothing to revoke");
        stored.RevokeReason.Should().Be("withdrawn by the requester");

        (await sut.ListPendingAsync(tenantId)).Should().BeEmpty();
        provisioner.TornDown.Should().BeEmpty("a pending request never reached a cluster");
    }

    [Fact]
    public async Task Handing_back_a_live_grant_removes_its_cluster_objects()
    {
        JitGrant requested = await sut.RequestAsync(tenantId, appId, envId, SubjectId, "debugging", SubjectEmail);
        await sut.ApproveAsync(tenantId, requested.Id, ApproverId, ApproverEmail, JitAccessLevel.Observe);

        await sut.EndOwnGrantAsync(tenantId, requested.Id, SubjectId, SubjectEmail);

        JitGrant stored = (await sut.GetAsync(tenantId, requested.Id))!;
        stored.StatusAt(DateTime.UtcNow).Should().Be(JitGrantStatus.Revoked,
            "access existed, so ending it is a revocation and the record should say so");
        provisioner.TornDown.Should().ContainSingle();
    }

    [Fact]
    public async Task A_request_belonging_to_somebody_else_cannot_be_ended()
    {
        JitGrant requested = await sut.RequestAsync(tenantId, appId, envId, SubjectId, "debugging", SubjectEmail);

        Func<Task> act = () =>
            sut.EndOwnGrantAsync(tenantId, requested.Id, OtherSubjectId, OtherSubjectEmail);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*somebody else*");
        (await sut.ListPendingAsync(tenantId)).Should().ContainSingle();
    }

    [Fact]
    public async Task A_request_nobody_answers_lapses_and_leaves_the_queue()
    {
        JitGrant stale = NewGrant();
        stale.RequestedAt = DateTime.UtcNow - JitGrant.PendingWindow - TimeSpan.FromMinutes(1);
        db.JitGrants.Add(stale);
        await db.SaveChangesAsync();

        stale.StatusAt(DateTime.UtcNow).Should().Be(JitGrantStatus.Lapsed);

        (await sut.ListPendingAsync(tenantId)).Should().BeEmpty(
            "an approver cannot act on it, so it is not a queue item");
        (await sut.ListForUserAsync(tenantId, SubjectId)).Should().ContainSingle(
            "the request still happened, and rows are never deleted");
    }

    [Fact]
    public async Task A_lapsed_request_cannot_be_approved()
    {
        JitGrant stale = NewGrant();
        stale.RequestedAt = DateTime.UtcNow - JitGrant.PendingWindow - TimeSpan.FromMinutes(1);
        db.JitGrants.Add(stale);
        await db.SaveChangesAsync();

        Func<Task> act = () =>
            sut.ApproveAsync(tenantId, stale.Id, ApproverId, ApproverEmail, JitAccessLevel.Observe);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*lapsed*");
        provisioner.Minted.Should().BeEmpty();
    }

    [Fact]
    public async Task A_request_still_inside_the_window_is_untouched()
    {
        JitGrant waiting = NewGrant();
        waiting.RequestedAt = DateTime.UtcNow - JitGrant.PendingWindow + TimeSpan.FromMinutes(5);
        db.JitGrants.Add(waiting);
        await db.SaveChangesAsync();

        waiting.StatusAt(DateTime.UtcNow).Should().Be(JitGrantStatus.Pending);
        (await sut.ListPendingAsync(tenantId)).Should().ContainSingle();
    }

    // ── Duration ──────────────────────────────────────────────────────────────

    [Fact]
    public void Duration_defaults_and_is_capped()
    {
        JitAccessService.ClampDuration(null).Should().Be(JitAccessService.DefaultDuration);
        JitAccessService.ClampDuration(TimeSpan.Zero).Should().Be(JitAccessService.DefaultDuration);
        JitAccessService.ClampDuration(TimeSpan.FromMinutes(-5)).Should().Be(JitAccessService.DefaultDuration);
        JitAccessService.ClampDuration(TimeSpan.FromMinutes(30)).Should().Be(TimeSpan.FromMinutes(30));
        JitAccessService.ClampDuration(TimeSpan.FromDays(30)).Should().Be(JitAccessService.MaximumDuration);
    }

    /// <summary>
    /// ⚠️ <b>The three default assertions above cannot see the ceiling, because the expectation
    /// moves with the value.</b> <c>ClampDuration(null).Should().Be(DefaultDuration)</c> is an
    /// identity — <c>ClampDuration</c> literally returns that field — so it holds for any
    /// <c>DefaultDuration</c> whatsoever, including one far above <c>MaximumDuration</c>. And the
    /// default path genuinely is not clamped: both <c>requested is null</c> and
    /// <c>requested &lt;= Zero</c> return early, <em>above</em> the
    /// <c>requested &gt; MaximumDuration</c> comparison. So the half of this test's name that says
    /// "is capped" would survive a <c>DefaultDuration</c> of thirty days.
    ///
    /// <para>This is insurance rather than a live defect, and worth saying so: both shipped
    /// callers — <c>RequestAsync</c> and <c>ApproveAsync</c> — pass an explicit duration from
    /// <see cref="JitAccessService.DurationOptionsMinutes"/>, so nothing reaches the unclamped
    /// path today. What is missing is only that two adjacent, individually documented constants
    /// have no stated relation, and the one that is called a "hard ceiling" does not bind the
    /// other. Three lines fix that; the asymmetry in <c>ClampDuration</c> is left alone, since
    /// reordering it would change behaviour nobody asked to change.</para>
    /// </summary>
    [Fact]
    public void The_default_lifetime_is_inside_the_hard_ceiling()
    {
        JitAccessService.DefaultDuration.Should().BeLessThanOrEqualTo(JitAccessService.MaximumDuration,
            "MaximumDuration is documented as a hard ceiling — 'anything longer stops being "
            + "just-in-time' — and ClampDuration returns DefaultDuration from two guards that sit "
            + "ABOVE the ceiling comparison, so a default beyond it is handed out unclamped");

        JitAccessService.DurationOptionsMinutes.Should().Contain(
            (int)JitAccessService.DefaultDuration.TotalMinutes,
            "the default has to be a lifetime either side could also have picked deliberately; "
            + "a default nobody can choose from the queue is a second, hidden policy");

        JitAccessService.DurationOptionsMinutes.Should().OnlyContain(
            m => m <= (int)JitAccessService.MaximumDuration.TotalMinutes,
            "the options list's own doc says they are 'all inside MaximumDuration' — asserted "
            + "here rather than trusted, since a new option is a one-token edit");
    }

    [Fact]
    public async Task A_grant_longer_than_the_ceiling_is_shortened_not_refused()
    {
        // Refusing would only push people to approve twice in a row for the same effect.
        JitGrant requested = await sut.RequestAsync(tenantId, appId, envId, SubjectId, "debugging", SubjectEmail);

        JitApproval approval = await sut.ApproveAsync(tenantId, requested.Id, ApproverId, ApproverEmail, JitAccessLevel.Observe, TimeSpan.FromDays(7));

        approval.Grant.ExpiresAt.Should().BeCloseTo(
            DateTime.UtcNow.Add(JitAccessService.MaximumDuration), TimeSpan.FromMinutes(1));
    }

    // ── Status ────────────────────────────────────────────────────────────────

    [Fact]
    public void Status_is_derived_from_timestamps_not_stored()
    {
        // A status column would need something to write "Expired" into it, and a grant whose
        // expiry depends on a background job having run is not time-boxed at all.
        DateTime now = new(2026, 9, 15, 12, 0, 0, DateTimeKind.Utc);
        JitGrant grant = NewGrant();

        grant.StatusAt(now).Should().Be(JitGrantStatus.Pending);

        grant.ApprovedAt = now.AddMinutes(-30);
        grant.ExpiresAt = now.AddMinutes(30);
        grant.StatusAt(now).Should().Be(JitGrantStatus.Active);
        grant.IsLiveAt(now).Should().BeTrue();

        grant.StatusAt(now.AddHours(2)).Should().Be(JitGrantStatus.Expired);
        grant.IsLiveAt(now.AddHours(2)).Should().BeFalse();

        grant.RevokedAt = now;
        grant.StatusAt(now).Should().Be(JitGrantStatus.Revoked);
    }

    [Fact]
    public void Expiry_is_exclusive_at_the_boundary()
    {
        DateTime now = new(2026, 9, 15, 12, 0, 0, DateTimeKind.Utc);
        JitGrant grant = NewGrant();
        grant.ApprovedAt = now.AddHours(-1);
        grant.ExpiresAt = now;

        grant.IsLiveAt(now).Should().BeFalse("a grant is not live in the instant it expires");
        grant.IsLiveAt(now.AddTicks(-1)).Should().BeTrue();
    }

    [Fact]
    public void An_approved_grant_with_no_expiry_is_treated_as_expired()
    {
        // Fail closed. A null expiry on an approved grant can only be a bug, and the safe reading
        // of "we do not know when this ends" is "it has ended".
        JitGrant grant = NewGrant();
        grant.ApprovedAt = DateTime.UtcNow.AddMinutes(-5);
        grant.ExpiresAt = null;

        grant.StatusAt(DateTime.UtcNow).Should().Be(JitGrantStatus.Expired);
    }

    // ── Deny and revoke ───────────────────────────────────────────────────────

    [Fact]
    public async Task Denying_keeps_the_row()
    {
        JitGrant requested = await sut.RequestAsync(tenantId, appId, envId, SubjectId, "debugging", SubjectEmail);

        await sut.DenyAsync(tenantId, requested.Id, ApproverEmail, "not needed");

        JitGrant stored = (await sut.GetAsync(tenantId, requested.Id))!;
        stored.StatusAt(DateTime.UtcNow).Should().Be(JitGrantStatus.Denied);
        stored.DeniedBy.Should().Be(ApproverEmail);
    }

    [Fact]
    public async Task Revoking_tears_down_the_cluster_objects_before_marking_the_row()
    {
        // A bound token cannot be invalidated early, so deleting the RoleBinding is what actually
        // stops it working. Marking the row only stops the proxy.
        JitGrant requested = await sut.RequestAsync(tenantId, appId, envId, SubjectId, "debugging", SubjectEmail);
        await sut.ApproveAsync(tenantId, requested.Id, ApproverId, ApproverEmail, JitAccessLevel.Observe);

        await sut.RevokeAsync(tenantId, requested.Id, ApproverEmail, "finished");

        provisioner.TornDown.Should().ContainSingle();
        JitGrant stored = (await sut.GetAsync(tenantId, requested.Id))!;
        stored.StatusAt(DateTime.UtcNow).Should().Be(JitGrantStatus.Revoked);
        stored.TornDownAt.Should().NotBeNull();
    }

    [Fact]
    public async Task Revoking_twice_tears_down_once()
    {
        JitGrant requested = await sut.RequestAsync(tenantId, appId, envId, SubjectId, "debugging", SubjectEmail);
        await sut.ApproveAsync(tenantId, requested.Id, ApproverId, ApproverEmail, JitAccessLevel.Observe);

        await sut.RevokeAsync(tenantId, requested.Id, ApproverEmail);
        await sut.RevokeAsync(tenantId, requested.Id, ApproverEmail);

        provisioner.TornDown.Should().ContainSingle();
    }

    [Fact]
    public async Task Revoking_a_request_that_was_never_approved_touches_no_cluster()
    {
        JitGrant requested = await sut.RequestAsync(tenantId, appId, envId, SubjectId, "debugging", SubjectEmail);

        await sut.RevokeAsync(tenantId, requested.Id, ApproverEmail, "withdrawn");

        provisioner.TornDown.Should().BeEmpty("nothing was ever created in the cluster");
    }

    [Fact]
    public async Task Finished_grants_stay_in_the_history()
    {
        JitGrant requested = await sut.RequestAsync(tenantId, appId, envId, SubjectId, "debugging", SubjectEmail);
        await sut.ApproveAsync(tenantId, requested.Id, ApproverId, ApproverEmail, JitAccessLevel.Observe);
        await sut.RevokeAsync(tenantId, requested.Id, ApproverEmail, "done");

        (await sut.ListForTenantAsync(tenantId)).Should().ContainSingle();
        (await sut.ListForUserAsync(tenantId, SubjectId)).Should().ContainSingle();
        (await sut.ListPendingAsync(tenantId)).Should().BeEmpty();
    }

    [Fact]
    public async Task The_pending_queue_is_oldest_first()
    {
        // It is a queue somebody works through, not a feed.
        JitGrant first = await sut.RequestAsync(tenantId, appId, envId, SubjectId, "first", SubjectEmail);
        await Task.Delay(10);
        JitGrant second = await sut.RequestAsync(tenantId, appId, envId, SubjectId, "second", SubjectEmail);

        List<JitGrant> pending = await sut.ListPendingAsync(tenantId);

        pending.Select(g => g.Id).Should().ContainInOrder(first.Id, second.Id);
    }

    // ── Fixtures ──────────────────────────────────────────────────────────────

    private JitGrant NewGrant() => new()
    {
        Id = Guid.NewGuid(), TenantId = tenantId, CustomerId = customerId, AppId = appId,
        EnvironmentId = envId, KubernetesClusterId = clusterId, Namespace = Namespace,
        UserId = SubjectId, Reason = "debugging", RequestedBy = SubjectEmail,
    };

    private void AddUser(string id, string email) =>
        db.Users.Add(new ApplicationUser { Id = id, UserName = email, Email = email });

    private void Seed()
    {
        db.Tenants.AddRange(
            new Tenant { Id = tenantId, Name = "Entit", Slug = "entit" },
            new Tenant { Id = strangerTenant, Name = "Stranger", Slug = "stranger" });
        db.Customers.Add(new Customer { Id = customerId, TenantId = tenantId, Name = "Acme" });
        db.Environments.Add(new EntKube.Web.Data.Environment
        {
            Id = envId, TenantId = tenantId, Name = "Production"
        });

        AddCluster("primary", clusterId);
        AddApp(appId, "Billing API", deployNamespace: Namespace);

        AddUser(SubjectId, SubjectEmail);
        AddUser(ApproverId, ApproverEmail);
        AddUser(DeployerId, DeployerEmail);
        AddUser(OtherSubjectId, OtherSubjectEmail);

        // The approver holds Manage on JIT access. The "viewer" here is not a nobody — they hold
        // Manage on Deployments, which is the permission this check used to reuse. That is the
        // point of the fixture: someone who can deploy this app must still not be able to hand a
        // customer a shell into its namespace.
        Guid managerRole = Guid.NewGuid();
        Guid viewerRole = Guid.NewGuid();
        db.TenantRoles.Add(new TenantRole
        {
            Id = managerRole, TenantId = tenantId, Name = "Operator",
            PermissionsJson = TenantRoleService.EncodePermissions(
                new Dictionary<TenantFeature, AccessLevel>
                {
                    [TenantFeature.JitAccess] = AccessLevel.Manage
                })
        });
        db.TenantRoles.Add(new TenantRole
        {
            Id = viewerRole, TenantId = tenantId, Name = "Deployer",
            PermissionsJson = TenantRoleService.EncodePermissions(
                new Dictionary<TenantFeature, AccessLevel>
                {
                    [TenantFeature.Deployments] = AccessLevel.Manage,
                    [TenantFeature.JitAccess] = AccessLevel.View,
                })
        });
        db.TenantMemberships.Add(new TenantMembership
        {
            UserId = ApproverId, TenantId = tenantId, RoleId = managerRole
        });
        db.TenantMemberships.Add(new TenantMembership
        {
            UserId = DeployerId, TenantId = tenantId, RoleId = viewerRole
        });

        db.SaveChanges();
    }

    private Guid AddCluster(string name, Guid? id = null)
    {
        Guid newId = id ?? Guid.NewGuid();
        db.KubernetesClusters.Add(new KubernetesCluster
        {
            Id = newId, TenantId = tenantId, EnvironmentId = envId,
            Name = name, ApiServerUrl = "https://cluster.example:6443", Kubeconfig = "apiVersion: v1"
        });
        return newId;
    }

    private void AddApp(Guid id, string name, string deployNamespace)
    {
        db.Apps.Add(new App { Id = id, CustomerId = customerId, Name = name });
        db.AppDeployments.Add(new AppDeployment
        {
            Id = Guid.NewGuid(), AppId = id, EnvironmentId = envId, ClusterId = clusterId,
            Name = $"{name}-deployment", Namespace = deployNamespace
        });
    }

    // ── Tenancy ───────────────────────────────────────────────────────────────

    /// <summary>
    /// The one that mattered most. <c>RequestAsync</c> wrote the grant with
    /// <c>TenantId = app.TenantId</c>, read off the app the caller named — so naming another
    /// tenant's app filed a pending request <b>inside that tenant</b>.
    ///
    /// <para>That is not noise in somebody else's table. <c>ApproveAsync</c> is correctly scoped
    /// to the asking tenant, which means it would have <em>accepted</em> this row: the victim's
    /// own approver sees a normal-looking pending request and approving it mints namespace access
    /// on their cluster for whatever subject the outsider named. The assertion is therefore not
    /// only that the call is refused but that no grant was written at all — a rejected request
    /// still sitting in the queue would be the whole problem.</para>
    /// </summary>
    [Fact]
    public async Task A_stranger_cannot_file_a_request_against_another_tenant_s_app()
    {
        Func<Task> act = () => sut.RequestAsync(
            strangerTenant, appId, envId, SubjectId, "let me in", SubjectEmail);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*does not belong to this tenant*");

        await using ApplicationDbContext check = factory.CreateDbContext();
        check.JitGrants.Should().BeEmpty(
            "a request that lands in the victim's queue is approvable by the victim's own "
            + "approver, so refusing the call is only half of it");
    }

    /// <summary>
    /// Denying had no tenant and no ownership check — only a test that the grant was pending — so
    /// any pending request in the installation could be denied by anyone who could reach the
    /// method, and the denial stamps <c>DeniedBy</c> on it.
    /// </summary>
    [Fact]
    public async Task A_stranger_cannot_deny_another_tenant_s_pending_request()
    {
        JitGrant grant = await sut.RequestAsync(
            tenantId, appId, envId, SubjectId, "debugging a 500", SubjectEmail);

        Func<Task> act = () => sut.DenyAsync(strangerTenant, grant.Id, "someone@elsewhere");

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*does not belong to this tenant*");

        await using ApplicationDbContext check = factory.CreateDbContext();
        JitGrant after = await check.JitGrants.FirstAsync(g => g.Id == grant.Id);
        after.DeniedAt.Should().BeNull();
        after.DeniedBy.Should().BeNull();
    }

    /// <summary>
    /// The refusal from <c>CheckNamespaceExclusivityAsync</c> <b>names the apps</b> sharing the
    /// namespace, so an unscoped sweep hands another tenant's application names to whoever asked.
    /// </summary>
    [Fact]
    public async Task Namespace_exclusivity_does_not_name_another_tenant_s_apps()
    {
        Guid otherApp = Guid.NewGuid();
        AddApp(otherApp, "Secret Project", deployNamespace: Namespace);
        await db.SaveChangesAsync();

        NamespaceExclusivityResult asOwner =
            await sut.CheckNamespaceExclusivityAsync(tenantId, appId, clusterId, Namespace);
        asOwner.Reason.Should().Contain("Secret Project",
            "the owner is entitled to know which of their own apps shares the namespace");

        NamespaceExclusivityResult asStranger =
            await sut.CheckNamespaceExclusivityAsync(strangerTenant, appId, clusterId, Namespace);
        asStranger.IsExclusive.Should().BeTrue(
            "the stranger owns nothing in this namespace, so there is nothing for them to be "
            + "told about");
    }

    [Fact]
    public async Task A_stranger_resolves_no_targets_for_another_tenant_s_app()
    {
        (await sut.ResolveTargetsAsync(tenantId, appId, envId)).Should().NotBeEmpty(
            "the owner still resolves their own app");

        (await sut.ResolveTargetsAsync(strangerTenant, appId, envId)).Should().BeEmpty(
            "a target is a cluster and a namespace — the shape of someone else's deployment");
    }

    [Fact]
    public async Task A_stranger_cannot_read_or_list_another_tenant_s_grants()
    {
        JitGrant grant = await sut.RequestAsync(
            tenantId, appId, envId, SubjectId, "debugging a 500", SubjectEmail);

        (await sut.GetAsync(tenantId, grant.Id)).Should().NotBeNull();
        (await sut.GetAsync(strangerTenant, grant.Id)).Should().BeNull();

        (await sut.ListForUserAsync(tenantId, SubjectId)).Should().HaveCount(1);
        (await sut.ListForUserAsync(strangerTenant, SubjectId)).Should().BeEmpty(
            "an account can hold access in more than one tenant, and a portal rendered for one "
            + "customer must not list the grants it holds elsewhere");
    }

    public void Dispose()
    {
        db.Dispose();
        connection.Dispose();
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Records what it was asked to do, so the service's ordering can be asserted — and, since the
    /// provisioner now takes the tenant it acts as, which tenant it was asked as.
    /// </summary>
    private sealed class FakeProvisioner : IJitProvisioner
    {
        public List<Guid> Minted { get; } = [];
        public List<Guid> TornDown { get; } = [];
        public List<Guid> ActedAsTenants { get; } = [];

        public Task<MintedCredential> MintAsync(
            Guid tenantId, JitGrant grant, TimeSpan duration, CancellationToken ct = default)
        {
            Minted.Add(grant.Id);
            ActedAsTenants.Add(tenantId);
            return Task.FromResult(new MintedCredential
            {
                ServiceAccountName = $"jit-{grant.Id:N}"[..20],
                Plaintext = $"ekj_{Guid.NewGuid():N}",
                TokenHash = Convert.ToHexStringLower(
                    System.Security.Cryptography.SHA256.HashData(grant.Id.ToByteArray())),
                DisplayPrefix = "ekj_test",
                TokenExpiresAt = DateTime.UtcNow.Add(duration),
                Kubeconfig = "apiVersion: v1",
            });
        }

        public Task TearDownAsync(Guid tenantId, JitGrant grant, CancellationToken ct = default)
        {
            ActedAsTenants.Add(tenantId);
            TornDown.Add(grant.Id);
            return Task.CompletedTask;
        }
    }
}
