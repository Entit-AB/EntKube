using System.Text.Json;
using EntKube.Web.Services.Dr;
using EntKube.Web.Services.PublicApi;
using FluentAssertions;
using Xunit;
using ContractRollout = EntKube.Contracts.Delivery.RolloutSummary;
using ContractStatus = EntKube.Contracts.Delivery.RolloutVerdictStatus;

namespace EntKube.Web.Tests;

/// <summary>
/// <c>GET /rollouts</c> and <c>GET /disaster-recovery</c> serialise exactly as the anonymous
/// objects they replaced. These were the last two operations in <c>PublicApiEndpoints</c> with no
/// described success body; typing them took
/// <c>ApiDocumentedResponsesTests.UndescribedSuccessBodies</c> to 7, which is now entirely SCIM.
///
/// <para>⚠️ <b>The oracles are the OLD code, cut out of <c>origin/main</c> rather than
/// retyped.</b> A record's mapping and its expected values both written from one reading of the
/// handler would agree on a consistent misreading of it.</para>
///
/// <para>⚠️ <b>Both routes carry an enum with a consequential ordinal-0 member.</b>
/// <c>RolloutVerdictStatus.Watching</c> is 0 — the state of every release still being analysed —
/// and <c>DrSeverity.Critical</c> is 0, the finding that says a cluster loss right now is
/// unrecoverable. Typing either member as its enum rather than as <c>string</c> publishes a
/// number in place of the single most consequential word on each route. The theories below assert
/// the literal JSON text, not the record members, because a test comparing two declarations to
/// each other stays green through a rename applied to both — and
/// <c>ApiRolloutStatusTests.The_contract_and_entity_rollout_status_names_agree</c> is exactly such
/// a test.</para>
/// </summary>
public class RolloutDrWireShapeTests
{
    private static readonly JsonSerializerOptions AsTheApiSerialises = new(JsonSerializerDefaults.Web);
    private static readonly DateTime At = new(2026, 11, 12, 6, 0, 0, DateTimeKind.Utc);

    /// <summary>
    /// ⚠️ Every nullable member SET and every string distinct, because the point is a byte
    /// comparison. <c>Status</c> is a non-zero member so a fixture on the default could not pass
    /// while reading the wrong enum.
    /// </summary>
    private static ContractRollout Rollout() => new(
        Id: Guid.Parse("50110000-0000-0000-0000-00000000000a"),
        AppName: "billing",
        DeploymentName: "billing-api-prod",
        EnvironmentName: "prod",
        ClusterName: "prod-north",
        Status: ContractStatus.RolledBack,
        StartedAt: At,
        FinishedAt: At.AddMinutes(12),
        Verdict: "error rate 4.1% over the window, threshold 1%",
        TriggeredBy: "release-pipeline");

    /// <summary>
    /// A clean, recent backup and an exercised restore — the healthy posture.
    ///
    /// <para>⚠️ <b>TWO schedules, one backup, one restore, deliberately.</b> <c>scheduleCount</c>
    /// is the only one of the four collections that reaches the wire, and with one of each a
    /// count taken from the wrong collection would serialise to the same number — so every byte
    /// comparison below would pass over the mistake. The second schedule is clean and unpaused so
    /// it adds no gap of its own.</para>
    /// </summary>
    private static ClusterDrStatus Protected() => new()
    {
        ClusterId = Guid.Parse("c0000000-0000-0000-0000-00000000000c"),
        ClusterName = "prod-north",
        IsVeleroInstalled = true,
        Error = "must-not-ship-but-is-on-the-wire",
        Schedules =
        [
            new() { Name = "daily", Cron = "0 2 * * *", LastBackupAt = At.AddHours(-4) },
            new() { Name = "weekly", Cron = "0 3 * * 0", LastBackupAt = At.AddDays(-3) },
        ],
        Backups =
        [
            new()
            {
                Name = "daily-20261112",
                Phase = VeleroPhase.Completed,
                StartedAt = At.AddHours(-4),
                CompletedAt = At.AddHours(-4).AddMinutes(11),
            },
        ],
        Restores =
        [
            new()
            {
                Name = "drill-20261001",
                BackupName = "daily-20261001",
                Phase = VeleroPhase.Completed,
                CompletedAt = At.AddDays(-42),
            },
        ],
        StorageLocations =
        [
            new() { Name = "default", Provider = "aws", Bucket = "must-not-ship", Phase = "Available" },
        ],
    };

    [Fact]
    public void A_rollout_serialises_exactly_as_the_anonymous_object_it_replaced()
    {
        ContractRollout r = Rollout();

        // Copied verbatim from origin/main's handler.
        object asItWas = new
        {
            id = r.Id,
            app = r.AppName,
            deployment = r.DeploymentName,
            environment = r.EnvironmentName,
            cluster = r.ClusterName,
            status = r.Status.ToString(),
            startedAt = r.StartedAt,
            finishedAt = r.FinishedAt,
            verdict = r.Verdict,
            triggeredBy = r.TriggeredBy,
        };

        JsonSerializer.Serialize(RolloutResponse.From(r), AsTheApiSerialises)
            .Should().Be(JsonSerializer.Serialize(asItWas, AsTheApiSerialises));
    }

    /// <summary>
    /// ⚠️ <b>The fixture is the UNPROTECTED cluster</b>, so <c>gaps</c> is non-empty and the
    /// nested shape is actually compared. A healthy cluster serialises <c>"gaps": []</c>, under
    /// which every field of <see cref="DrGapResponse"/> could be wrong and this byte comparison
    /// would still pass.
    /// </summary>
    [Fact]
    public void A_dr_status_serialises_exactly_as_the_anonymous_object_it_replaced()
    {
        ClusterDrStatus s = Protected() with { Backups = [], Restores = [] };
        DateTime now = At;

        // Copied verbatim from origin/main's handler.
        object asItWas = new
        {
            clusterId = s.ClusterId,
            cluster = s.ClusterName,
            veleroInstalled = s.IsVeleroInstalled,
            error = s.Error,
            restorable = s.LastUsableBackup is not null,
            lastUsableBackupAt = s.LastUsableBackup?.CompletedAt,
            lastRestoreAt = s.LastSuccessfulRestore?.CompletedAt,
            scheduleCount = s.Schedules.Count,
            gaps = DrReadiness.Evaluate(s, now).Select(g => new
            {
                key = g.Key,
                severity = g.Severity.ToString(),
                title = g.Title,
                detail = g.Detail,
                remediation = g.Remediation,
            }),
        };

        DrReadiness.Evaluate(s, now).Should().NotBeEmpty(
            "otherwise the nested gap shape above is compared over nothing");

        JsonSerializer.Serialize(DrStatusResponse.From(s, now), AsTheApiSerialises)
            .Should().Be(JsonSerializer.Serialize(asItWas, AsTheApiSerialises));
    }

    /// <summary>
    /// ⚠️ And the healthy posture too, because the two differ in every derived field: the
    /// unprotected fixture above has <c>restorable: false</c> and both timestamps null, so it
    /// alone could not tell a reversed flag or a swapped pair of timestamps from the truth.
    /// </summary>
    [Fact]
    public void A_protected_cluster_serialises_exactly_as_the_anonymous_object_it_replaced()
    {
        ClusterDrStatus s = Protected();
        DateTime now = At;

        object asItWas = new
        {
            clusterId = s.ClusterId,
            cluster = s.ClusterName,
            veleroInstalled = s.IsVeleroInstalled,
            error = s.Error,
            restorable = s.LastUsableBackup is not null,
            lastUsableBackupAt = s.LastUsableBackup?.CompletedAt,
            lastRestoreAt = s.LastSuccessfulRestore?.CompletedAt,
            scheduleCount = s.Schedules.Count,
            gaps = DrReadiness.Evaluate(s, now).Select(g => new
            {
                key = g.Key,
                severity = g.Severity.ToString(),
                title = g.Title,
                detail = g.Detail,
                remediation = g.Remediation,
            }),
        };

        // The two timestamps are DIFFERENT instants in this fixture, which is what makes a swap
        // between them visible at all.
        s.LastUsableBackup!.CompletedAt.Should().NotBe(s.LastSuccessfulRestore!.CompletedAt);

        JsonSerializer.Serialize(DrStatusResponse.From(s, now), AsTheApiSerialises)
            .Should().Be(JsonSerializer.Serialize(asItWas, AsTheApiSerialises));
    }

    [Fact]
    public void The_keys_are_the_ones_clients_already_read()
    {
        Keys(RolloutResponse.From(Rollout())).Should().Equal(
            "id", "app", "deployment", "environment", "cluster", "status", "startedAt",
            "finishedAt", "verdict", "triggeredBy");

        ClusterDrStatus s = Protected() with { Backups = [], Restores = [] };

        Keys(DrStatusResponse.From(s, At)).Should().Equal(
            "clusterId", "cluster", "veleroInstalled", "error", "restorable",
            "lastUsableBackupAt", "lastRestoreAt", "scheduleCount", "gaps");

        Nested(DrStatusResponse.From(s, At), "gaps").Should().Equal(
            "key", "severity", "title", "detail", "remediation");
    }

    /// <summary>
    /// ⚠️ <b>What <c>ClusterDrStatus</c> carries and the wire does not.</b> A byte comparison
    /// catches an addition only while the old literal remains the oracle; a key list says so
    /// forever.
    ///
    /// <para>The schedules, backups, restores and storage locations are all present on the source
    /// and none of them is published — only <c>scheduleCount</c>. So an operator told
    /// <c>"dr-schedule-paused"</c> by the <c>gaps</c> list cannot learn from this route WHICH
    /// schedule is paused: the gap's <c>key</c> carries the name, and nothing else does. Likewise
    /// a failing backup's <c>FailureReason</c> reaches the wire only inside a gap's prose
    /// <c>detail</c>, where no client can parse it.</para>
    /// </summary>
    [Fact]
    public void The_four_collections_the_source_carries_and_the_wire_does_not_stay_off_it()
    {
        ClusterDrStatus s = Protected();

        s.Schedules.Should().NotBeEmpty();
        s.Backups.Should().NotBeEmpty();
        s.Restores.Should().NotBeEmpty();
        s.StorageLocations.Should().NotBeEmpty("otherwise this fact asserts the absence of nothing");

        // ⚠️ And the published count is distinguishable from the three collections that are not
        // published, which is what makes a count taken from the wrong one visible at all.
        s.Schedules.Count.Should().NotBe(s.Backups.Count);
        s.Schedules.Count.Should().NotBe(s.Restores.Count);
        s.Schedules.Count.Should().NotBe(s.StorageLocations.Count);

        DrStatusResponse.From(s, At).ScheduleCount.Should().Be(s.Schedules.Count);

        Keys(DrStatusResponse.From(s, At)).Should().NotContain(
            ["schedules", "backups", "restores", "storageLocations"],
            "none of these has ever been on the wire");

        JsonSerializer.Serialize(DrStatusResponse.From(s, At), AsTheApiSerialises)
            .Should().NotContain(
                "daily-20261112",
                "and the distinctive backup name proves the collection was not merely renamed");
    }

    /// <summary>
    /// ⚠️ <b><c>restorable</c> is not "a backup finished".</b> Velero stamps a
    /// <c>PartiallyFailed</c> backup with a completion time although it skipped resources, so a
    /// field built from the newest ATTEMPTED backup would report an unrestorable cluster as
    /// protected — which is the one error in this subsystem that is discovered during a restore.
    /// </summary>
    [Fact]
    public void A_partially_failed_backup_is_not_restorable()
    {
        ClusterDrStatus s = Protected() with
        {
            Backups =
            [
                new()
                {
                    Name = "daily-20261112",
                    Phase = VeleroPhase.PartiallyFailed,
                    StartedAt = At.AddHours(-4),
                    CompletedAt = At.AddHours(-4).AddMinutes(11),
                },
            ],
        };

        s.LastAttemptedBackup.Should().NotBeNull(
            "the point of this fact is that there IS a recent, completed-looking backup");
        s.LastAttemptedBackup!.CompletedAt.Should().NotBeNull();

        DrStatusResponse response = DrStatusResponse.From(s, At);

        response.Restorable.Should().BeFalse();
        response.LastUsableBackupAt.Should().BeNull();
    }

    /// <summary>
    /// ⚠️ <b><c>restorable: true</c> with <c>lastUsableBackupAt: null</c> is reachable</b>, and a
    /// client that infers protection from the timestamp rather than from the flag therefore reads
    /// a protected cluster as unprotected. <c>IsUsable</c> asks for a clean <c>Completed</c> phase
    /// and no errors; it does not ask for a <c>CompletedAt</c>. Reproduced as shipped — this fact
    /// records the combination rather than claiming it cannot happen.
    /// </summary>
    [Fact]
    public void A_usable_backup_with_no_completion_time_is_still_restorable()
    {
        ClusterDrStatus s = Protected() with
        {
            Backups =
            [
                new()
                {
                    Name = "daily-20261112",
                    Phase = VeleroPhase.Completed,
                    StartedAt = At.AddHours(-4),
                    CompletedAt = null,
                },
            ],
        };

        DrStatusResponse response = DrStatusResponse.From(s, At);

        response.Restorable.Should().BeTrue();
        response.LastUsableBackupAt.Should().BeNull();
    }

    /// <summary>
    /// ⚠️ <b>The gaps are judged against the instant passed in, not against the wall clock.</b>
    /// The handler reads <c>DateTime.UtcNow</c> once and hands the same value to every cluster on
    /// the response; a <see cref="DrStatusResponse.From"/> that called <c>UtcNow</c> itself would
    /// compile, would pass every byte comparison above, and would compare two clusters on one
    /// page to two different instants — with the staleness threshold between them on exactly the
    /// finding this route exists to report.
    /// </summary>
    [Fact]
    public void The_gaps_are_judged_against_the_instant_the_caller_supplies()
    {
        ClusterDrStatus s = Protected();

        string[] fresh = [.. DrStatusResponse.From(s, At).Gaps.Select(g => g.Key)];
        string[] stale = [.. DrStatusResponse.From(s, At + DrReadiness.StaleAfter.Add(TimeSpan.FromDays(1)))
            .Gaps.Select(g => g.Key)];

        fresh.Should().NotContain(
            k => k.StartsWith("dr-stale-backup:", StringComparison.Ordinal),
            "four hours after a clean backup, nothing is stale");

        stale.Should().Contain(
            k => k.StartsWith("dr-stale-backup:", StringComparison.Ordinal),
            "and past StaleAfter the same unchanged cluster has an unrecoverable window");
    }

    /// <summary>
    /// ⚠️ <b>Hard-coded expectations, asserted over the serialised text.</b> <c>Watching</c> is
    /// ordinal 0, so a number here would be read as data rather than as a defect — and every
    /// release still inside its analysis window carries it.
    /// </summary>
    [Theory]
    [InlineData(ContractStatus.Watching, "Watching")]
    [InlineData(ContractStatus.Promoted, "Promoted")]
    [InlineData(ContractStatus.RolledBack, "RolledBack")]
    [InlineData(ContractStatus.Alerted, "Alerted")]
    [InlineData(ContractStatus.Inconclusive, "Inconclusive")]
    [InlineData(ContractStatus.RollbackFailed, "RollbackFailed")]
    [InlineData(ContractStatus.Superseded, "Superseded")]
    public void A_rollout_status_is_published_by_name(ContractStatus status, string expected)
    {
        RolloutResponse row = RolloutResponse.From(Rollout() with { Status = status });

        row.Status.Should().Be(expected);
        JsonSerializer.Serialize(row, AsTheApiSerialises)
            .Should().Contain($"\"status\":\"{expected}\"");
    }

    /// <summary>
    /// ⚠️ <c>Critical</c> is ordinal 0 — "there is no usable backup; a cluster loss right now is
    /// unrecoverable".
    /// </summary>
    [Theory]
    [InlineData(DrSeverity.Critical, "Critical")]
    [InlineData(DrSeverity.Warning, "Warning")]
    [InlineData(DrSeverity.Info, "Info")]
    public void A_dr_severity_is_published_by_name(DrSeverity severity, string expected)
    {
        DrGapResponse gap = DrGapResponse.From(new DrGap
        {
            Key = "dr-no-backup:c0000000-0000-0000-0000-00000000000c",
            Title = "“prod-north” has no usable backup",
            Detail = "Velero is installed but has never completed a backup.",
            Severity = severity,
            Remediation = "Create a backup schedule.",
        });

        gap.Severity.Should().Be(expected);
        JsonSerializer.Serialize(gap, AsTheApiSerialises)
            .Should().Contain($"\"severity\":\"{expected}\"");
    }

    private static IEnumerable<string> Keys<T>(T value) =>
        JsonSerializer.SerializeToNode(value, AsTheApiSerialises)!.AsObject().Select(p => p.Key);

    private static IEnumerable<string> Nested<T>(T value, string property) =>
        JsonSerializer.SerializeToNode(value, AsTheApiSerialises)!
            .AsObject()[property]!.AsArray()[0]!.AsObject().Select(p => p.Key);
}
