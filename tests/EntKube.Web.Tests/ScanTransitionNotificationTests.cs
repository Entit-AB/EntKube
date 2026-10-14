using EntKube.Web.Services.Dr;
using EntKube.Web.Services.Outbound;
using EntKube.Web.Services.Upgrades;
using FluentAssertions;

namespace EntKube.Web.Tests;

/// <summary>
/// When a drift sweep and a DR sweep speak to a tenant's notification channels, and — mostly — when
/// they do not. This is the roadmap's outstanding #2 item: webhook events for drift detected and DR
/// gap opened.
///
/// <para><b>The whole difficulty is restraint.</b> Both signals are states, not events: a drifted
/// component is still drifted on the next sweep, and a cluster with no backups will have none in
/// five minutes either. A channel told about the state fires for as long as the problem lasts,
/// which is how an operator learns to mute it — and #5's rollback alert is muted along with it.
/// So these tests are mostly about silence, and the one that matters most is the sweep that finds
/// everything already broken and says nothing.</para>
/// </summary>
public class ScanTransitionNotificationTests
{
    // ──────── Drift ────────

    [Fact]
    public void A_deployment_that_has_just_drifted_is_announced()
    {
        DriftReport before = Report(("api", DriftState.InSync));
        DriftReport after = Report(("api", DriftState.Drifted));

        IReadOnlyList<DriftResult> newly = ScanTransitions.NewlyDrifted(before, after);

        newly.Should().ContainSingle().Which.DeploymentName.Should().Be("api");

        (string Title, string Body, string Severity)? message = ScanTransitions.DriftMessage(newly);

        message.Should().NotBeNull();
        message!.Value.Title.Should().Contain("api");
        message.Value.Body.Should().Contain("prod-cluster");
        message.Value.Severity.Should().Be("warning",
            "a drifted component is still serving — it is a thing to look at today, not to wake "
            + "somebody for");
    }

    [Fact]
    public void A_deployment_that_was_already_drifted_is_not_announced_again()
    {
        DriftReport before = Report(("api", DriftState.Drifted));
        DriftReport after = Report(("api", DriftState.Drifted));

        ScanTransitions.NewlyDrifted(before, after).Should().BeEmpty();
        ScanTransitions.DriftMessage(ScanTransitions.NewlyDrifted(before, after)).Should().BeNull();
    }

    /// <summary>
    /// The case that decides whether this feature is usable. The caches are per-process, so the
    /// first sweep after a restart has nothing to compare against — and announcing everything
    /// already drifted would make deploying EntKube itself page somebody.
    /// </summary>
    [Fact]
    public void The_first_sweep_after_a_restart_announces_nothing()
    {
        DriftReport after = Report(("api", DriftState.Drifted), ("web", DriftState.Drifted));

        ScanTransitions.NewlyDrifted(previous: null, after).Should().BeEmpty();
    }

    [Fact]
    public void Drift_clearing_is_left_to_the_digest()
    {
        DriftReport before = Report(("api", DriftState.Drifted));
        DriftReport after = Report(("api", DriftState.InSync));

        ScanTransitions.NewlyDrifted(before, after).Should().BeEmpty(
            "a channel that speaks on both edges is twice as easy to mute");
    }

    [Fact]
    public void An_unreadable_cluster_is_not_reported_as_drift()
    {
        DriftReport before = Report(("api", DriftState.InSync));
        DriftReport after = Report(("api", DriftState.Unknown));

        ScanTransitions.NewlyDrifted(before, after).Should().BeEmpty(
            "\"could not tell\" must never read as \"differs\" — the same rule the drift sweep "
            + "itself keeps");
    }

    [Fact]
    public void Several_new_drifts_are_one_message_not_several()
    {
        DriftReport before = Report(("api", DriftState.InSync), ("web", DriftState.InSync));
        DriftReport after = Report(("api", DriftState.Drifted), ("web", DriftState.Drifted));

        (string Title, string Body, string Severity)? message =
            ScanTransitions.DriftMessage(ScanTransitions.NewlyDrifted(before, after));

        message!.Value.Title.Should().Be("Drift detected on 2 deployments");
        message.Value.Body.Should().Contain("api").And.Contain("web");
    }

    // ──────── DR gaps ────────

    [Fact]
    public void A_cluster_that_has_just_lost_its_backup_is_announced_as_critical()
    {
        DateTime now = DateTime.UtcNow;
        List<ClusterDrStatus> before = [WithUsableBackup(now)];
        List<ClusterDrStatus> after = [WithNoBackup()];

        IReadOnlyList<(ClusterDrStatus Cluster, DrGap Gap)> opened =
            ScanTransitions.NewlyOpenedGaps(before, after, now);

        opened.Should().ContainSingle();

        (string Title, string Body, string Severity)? message = ScanTransitions.DrMessage(opened);

        message.Should().NotBeNull();
        message!.Value.Severity.Should().Be("critical",
            "nothing to restore from is total exposure, and the Advisor already calls it a today "
            + "problem");
        message.Value.Body.Should().Contain("prod-cluster");
    }

    [Fact]
    public void A_gap_that_was_already_open_is_not_announced_again()
    {
        DateTime now = DateTime.UtcNow;
        List<ClusterDrStatus> before = [WithNoBackup()];
        List<ClusterDrStatus> after = [WithNoBackup()];

        ScanTransitions.NewlyOpenedGaps(before, after, now).Should().BeEmpty();
    }

    [Fact]
    public void The_first_dr_sweep_announces_nothing()
    {
        ScanTransitions.NewlyOpenedGaps(previous: null, [WithNoBackup()], DateTime.UtcNow)
            .Should().BeEmpty();
    }

    [Fact]
    public void A_cluster_without_velero_produces_no_gap_to_announce()
    {
        DateTime now = DateTime.UtcNow;
        List<ClusterDrStatus> before = [NoVelero()];
        List<ClusterDrStatus> after = [NoVelero()];

        ScanTransitions.NewlyOpenedGaps(before, after, now).Should().BeEmpty(
            "a cluster outside the feature's scope is not failing DR — DrReadiness already says so");
    }

    // ──────── helpers ────────

    private static DriftReport Report(params (string Name, DriftState State)[] deployments) => new()
    {
        GeneratedAt = DateTime.UtcNow,
        Results =
        [
            .. deployments.Select(d => new DriftResult
            {
                // Stable per name, so "the same deployment" means the same thing across sweeps.
                DeploymentId = NameId(d.Name),
                DeploymentName = d.Name,
                AppName = $"{d.Name}-app",
                ClusterId = NameId("prod-cluster"),
                ClusterName = "prod-cluster",
                Namespace = "apps",
                State = d.State,
            })
        ],
    };

    private static Guid NameId(string name)
    {
        byte[] bytes = new byte[16];
        System.Text.Encoding.UTF8.GetBytes(name).CopyTo(bytes, 0);
        return new Guid(bytes);
    }

    private static ClusterDrStatus WithUsableBackup(DateTime now) => new()
    {
        ClusterId = NameId("prod-cluster"),
        ClusterName = "prod-cluster",
        IsVeleroInstalled = true,
        Schedules = [new VeleroSchedule { Name = "daily", Cron = "0 2 * * *" }],
        Backups =
        [
            new VeleroBackup
            {
                Name = "daily-1", Phase = VeleroPhase.Completed,
                StartedAt = now.AddHours(-2), CompletedAt = now.AddHours(-2),
            }
        ],
    };

    private static ClusterDrStatus WithNoBackup() => new()
    {
        ClusterId = NameId("prod-cluster"),
        ClusterName = "prod-cluster",
        IsVeleroInstalled = true,
        Schedules = [new VeleroSchedule { Name = "daily", Cron = "0 2 * * *" }],
    };

    private static ClusterDrStatus NoVelero() => new()
    {
        ClusterId = NameId("other-cluster"),
        ClusterName = "other-cluster",
        IsVeleroInstalled = false,
    };
}
