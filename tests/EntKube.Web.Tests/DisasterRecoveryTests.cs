using EntKube.Web.Data;
using EntKube.Web.Services;
using EntKube.Web.Services.Dr;
using FluentAssertions;
using YamlDotNet.Serialization;

namespace EntKube.Web.Tests;

/// <summary>
/// Tests for Velero-backed disaster recovery: parsing Velero's custom resources,
/// generating schedules, and judging whether a cluster could actually be recovered.
///
/// The property most of these defend: a backup that EXISTS is not a backup that WORKS.
/// </summary>
public class DisasterRecoveryTests
{
    private static readonly DateTime Now = new(2026, 8, 21, 12, 0, 0, DateTimeKind.Utc);

    // ── Parsing Velero's CRs ──

    private const string BackupsJson = """
    {
      "items": [
        {
          "metadata": { "name": "daily-20260821", "labels": { "velero.io/schedule-name": "daily" } },
          "spec": { "includedNamespaces": ["acme-prod"], "storageLocation": "default" },
          "status": {
            "phase": "Completed",
            "startTimestamp": "2026-08-21T02:00:00Z",
            "completionTimestamp": "2026-08-21T02:12:00Z",
            "expiration": "2026-09-20T02:00:00Z",
            "errors": 0, "warnings": 2
          }
        },
        {
          "metadata": { "name": "daily-20260820" },
          "spec": {},
          "status": {
            "phase": "PartiallyFailed",
            "startTimestamp": "2026-08-20T02:00:00Z",
            "completionTimestamp": "2026-08-20T02:09:00Z",
            "errors": 3
          }
        }
      ]
    }
    """;

    [Fact]
    public void Parses_backups_with_their_phase_timing_and_counts()
    {
        IReadOnlyList<VeleroBackup> backups = VeleroService.ParseBackups(BackupsJson);

        backups.Should().HaveCount(2);
        VeleroBackup first = backups[0];
        first.Name.Should().Be("daily-20260821");
        first.Phase.Should().Be(VeleroPhase.Completed);
        first.CreatedBySchedule.Should().Be("daily");
        first.Warnings.Should().Be(2);
        first.IncludedNamespaces.Should().BeEquivalentTo(["acme-prod"]);
        first.CompletedAt.Should().Be(new DateTime(2026, 8, 21, 2, 12, 0, DateTimeKind.Utc));
    }

    [Fact]
    public void A_partially_failed_backup_is_not_usable()
    {
        // Velero gives it a completion timestamp, so anything asking only "did it finish"
        // treats a backup that skipped resources as a success.
        VeleroBackup partial = VeleroService.ParseBackups(BackupsJson).Single(b => b.Name == "daily-20260820");

        partial.CompletedAt.Should().NotBeNull();
        partial.IsUsable.Should().BeFalse();
    }

    [Fact]
    public void A_completed_backup_with_errors_is_not_usable()
    {
        const string json = """
        { "items": [ { "metadata": { "name": "b" }, "spec": {},
          "status": { "phase": "Completed", "errors": 4 } } ] }
        """;

        VeleroService.ParseBackups(json).Single().IsUsable.Should().BeFalse();
    }

    [Fact]
    public void An_absent_snapshot_volumes_field_means_volumes_are_captured()
    {
        // Velero defaults snapshotVolumes to true. Reading an absent field as false would
        // raise a false alarm on every correctly-configured schedule.
        const string json = """
        { "items": [ { "metadata": { "name": "daily" },
          "spec": { "schedule": "0 2 * * *", "template": { "ttl": "720h0m0s" } } } ] }
        """;

        VeleroService.ParseSchedules(json).Single().SnapshotVolumes.Should().BeTrue();
    }

    [Fact]
    public void An_explicit_false_snapshot_volumes_is_honoured()
    {
        const string json = """
        { "items": [ { "metadata": { "name": "daily" },
          "spec": { "schedule": "0 2 * * *", "template": { "snapshotVolumes": false } } } ] }
        """;

        VeleroService.ParseSchedules(json).Single().SnapshotVolumes.Should().BeFalse();
    }

    [Fact]
    public void Parses_storage_location_availability()
    {
        const string json = """
        { "items": [ { "metadata": { "name": "default" },
          "spec": { "provider": "aws", "default": true, "objectStorage": { "bucket": "backups" } },
          "status": { "phase": "Unavailable" } } ] }
        """;

        VeleroStorageLocation location = VeleroService.ParseStorageLocations(json).Single();
        location.Bucket.Should().Be("backups");
        location.IsDefault.Should().BeTrue();
        location.IsAvailable.Should().BeFalse();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("{}")]
    [InlineData("""{ "items": "wrong shape" }""")]
    public void Unreadable_responses_parse_to_nothing_rather_than_throwing(string? json)
    {
        // A missing CRD returns something unusable; it must read as "no backups", never
        // crash a fleet-wide sweep.
        VeleroService.ParseBackups(json).Should().BeEmpty();
        VeleroService.ParseSchedules(json).Should().BeEmpty();
        VeleroService.ParseStorageLocations(json).Should().BeEmpty();
    }

    // ── Generated schedule manifests ──

    private static object ParseYaml(string yaml) =>
        new DeserializerBuilder().Build().Deserialize<object>(yaml)!;

    [Fact]
    public void Generates_a_parseable_schedule_with_retention_in_hours()
    {
        string yaml = VeleroService.BuildScheduleManifest(
            "daily", "0 2 * * *", retentionDays: 30, includedNamespaces: null,
            snapshotVolumes: true, storageLocation: "default");

        Action parse = () => ParseYaml(yaml);
        parse.Should().NotThrow();

        yaml.Should().Contain("kind: Schedule");
        yaml.Should().Contain("schedule: \"0 2 * * *\"");
        yaml.Should().Contain("ttl: 720h0m0s");
        yaml.Should().Contain("managed-by: entkube");
    }

    [Fact]
    public void An_omitted_namespace_list_backs_up_everything()
    {
        // Emitting an empty includedNamespaces would mean "nothing" to Velero, producing a
        // schedule that captures no resources while still looking configured.
        string yaml = VeleroService.BuildScheduleManifest(
            "all", "0 2 * * *", 30, includedNamespaces: [], snapshotVolumes: true, storageLocation: null);

        yaml.Should().NotContain("includedNamespaces");
    }

    [Fact]
    public void Namespaces_are_emitted_when_given()
    {
        string yaml = VeleroService.BuildScheduleManifest(
            "scoped", "0 2 * * *", 30, ["acme-prod", "acme-stage"], true, null);

        yaml.Should().Contain("- acme-prod").And.Contain("- acme-stage");
        ParseYaml(yaml).Should().NotBeNull();
    }

    [Fact]
    public void Retention_is_clamped_to_at_least_one_day()
    {
        // A zero TTL means "never expire" to Velero, which is the opposite of what someone
        // entering 0 into a retention box intends.
        VeleroService.BuildScheduleManifest("d", "0 2 * * *", 0, null, true, null)
            .Should().Contain("ttl: 24h0m0s");
    }

    // ── Readiness judgement ──

    private static ClusterDrStatus Status(
        bool installed = true,
        IReadOnlyList<VeleroBackup>? backups = null,
        IReadOnlyList<VeleroSchedule>? schedules = null,
        IReadOnlyList<VeleroRestore>? restores = null,
        IReadOnlyList<VeleroStorageLocation>? locations = null) => new()
    {
        ClusterId = Guid.NewGuid(),
        ClusterName = "prod-eu-west-1",
        IsVeleroInstalled = installed,
        Backups = backups ?? [],
        Schedules = schedules ?? [],
        Restores = restores ?? [],
        StorageLocations = locations ?? [],
    };

    private static VeleroBackup Backup(VeleroPhase phase, DateTime completed, int errors = 0) => new()
    {
        Name = "b", Phase = phase, StartedAt = completed.AddMinutes(-10), CompletedAt = completed, Errors = errors,
    };

    private static VeleroSchedule Schedule(bool paused = false, bool volumes = true) => new()
    {
        Name = "daily", Cron = "0 2 * * *", IsPaused = paused, SnapshotVolumes = volumes,
    };

    [Fact]
    public void A_cluster_without_velero_produces_no_gaps()
    {
        // Not "failing DR" — outside the feature's scope. Flagging every such cluster
        // would drown the real gaps.
        DrReadiness.Evaluate(Status(installed: false), Now).Should().BeEmpty();
    }

    [Fact]
    public void No_backup_at_all_is_critical()
    {
        IReadOnlyList<DrGap> gaps = DrReadiness.Evaluate(Status(schedules: [Schedule()]), Now);

        gaps.Should().Contain(g => g.Key.StartsWith("dr-no-backup:"));
        gaps.Single(g => g.Key.StartsWith("dr-no-backup:")).Severity.Should().Be(DrSeverity.Critical);
    }

    [Fact]
    public void Only_partially_failed_backups_still_counts_as_having_nothing_to_restore()
    {
        IReadOnlyList<DrGap> gaps = DrReadiness.Evaluate(
            Status(backups: [Backup(VeleroPhase.PartiallyFailed, Now.AddHours(-2), errors: 3)],
                   schedules: [Schedule()]),
            Now);

        DrGap gap = gaps.Single(g => g.Key.StartsWith("dr-no-backup:"));
        gap.Severity.Should().Be(DrSeverity.Critical);
        gap.Detail.Should().Contain("PartiallyFailed");
    }

    [Fact]
    public void A_recent_clean_backup_raises_no_backup_gap()
    {
        IReadOnlyList<DrGap> gaps = DrReadiness.Evaluate(
            Status(backups: [Backup(VeleroPhase.Completed, Now.AddHours(-3))],
                   schedules: [Schedule()],
                   restores: [new VeleroRestore { Name = "r", BackupName = "b",
                                                  Phase = VeleroPhase.Completed, CompletedAt = Now.AddDays(-10) }]),
            Now);

        gaps.Should().BeEmpty();
    }

    [Fact]
    public void A_backup_older_than_the_staleness_window_is_critical()
    {
        IReadOnlyList<DrGap> gaps = DrReadiness.Evaluate(
            Status(backups: [Backup(VeleroPhase.Completed, Now.AddHours(-48))], schedules: [Schedule()]),
            Now);

        gaps.Should().Contain(g => g.Key.StartsWith("dr-stale-backup:"));
    }

    [Fact]
    public void A_backup_inside_the_staleness_window_is_not_stale()
    {
        DrReadiness.Evaluate(
            Status(backups: [Backup(VeleroPhase.Completed, Now.AddHours(-30))], schedules: [Schedule()]), Now)
            .Should().NotContain(g => g.Key.StartsWith("dr-stale-backup:"));
    }

    [Fact]
    public void No_schedule_is_flagged_even_when_a_manual_backup_exists()
    {
        IReadOnlyList<DrGap> gaps = DrReadiness.Evaluate(
            Status(backups: [Backup(VeleroPhase.Completed, Now.AddHours(-1))]), Now);

        gaps.Should().Contain(g => g.Key.StartsWith("dr-no-schedule:"));
    }

    [Fact]
    public void A_paused_schedule_is_flagged()
    {
        DrReadiness.Evaluate(
            Status(backups: [Backup(VeleroPhase.Completed, Now.AddHours(-1))],
                   schedules: [Schedule(paused: true)]), Now)
            .Should().Contain(g => g.Key.Contains("dr-schedule-paused:"));
    }

    [Fact]
    public void A_schedule_that_skips_volume_data_is_flagged()
    {
        // A restore would bring back empty volumes, which looks like success until
        // someone opens the application.
        DrReadiness.Evaluate(
            Status(backups: [Backup(VeleroPhase.Completed, Now.AddHours(-1))],
                   schedules: [Schedule(volumes: false)]), Now)
            .Should().Contain(g => g.Key.Contains("dr-schedule-no-volumes:"));
    }

    [Fact]
    public void An_unavailable_storage_location_is_critical()
    {
        IReadOnlyList<DrGap> gaps = DrReadiness.Evaluate(
            Status(backups: [Backup(VeleroPhase.Completed, Now.AddHours(-1))],
                   schedules: [Schedule()],
                   locations: [new VeleroStorageLocation { Name = "default", Provider = "aws", Phase = "Unavailable" }]),
            Now);

        gaps.Single(g => g.Key.Contains("dr-storage-unavailable:")).Severity.Should().Be(DrSeverity.Critical);
    }

    [Fact]
    public void Backups_that_have_never_been_restored_are_flagged_as_untested()
    {
        IReadOnlyList<DrGap> gaps = DrReadiness.Evaluate(
            Status(backups: [Backup(VeleroPhase.Completed, Now.AddHours(-1))], schedules: [Schedule()]), Now);

        DrGap gap = gaps.Single(g => g.Key.StartsWith("dr-untested:"));
        gap.Severity.Should().Be(DrSeverity.Info);
        gap.Detail.Should().Contain("hypothesis");
    }

    [Fact]
    public void An_old_restore_drill_is_flagged_as_stale()
    {
        IReadOnlyList<DrGap> gaps = DrReadiness.Evaluate(
            Status(backups: [Backup(VeleroPhase.Completed, Now.AddHours(-1))],
                   schedules: [Schedule()],
                   restores: [new VeleroRestore { Name = "r", BackupName = "b",
                                                  Phase = VeleroPhase.Completed, CompletedAt = Now.AddDays(-400) }]),
            Now);

        gaps.Should().Contain(g => g.Key.StartsWith("dr-untested:"));
    }

    [Fact]
    public void Untested_is_not_reported_when_there_is_nothing_usable_to_test()
    {
        // "Your backups are untested" alongside "you have no backups" is noise; the
        // second is the only actionable one.
        DrReadiness.Evaluate(Status(schedules: [Schedule()]), Now)
            .Should().NotContain(g => g.Key.StartsWith("dr-untested:"));
    }
}

/// <summary>
/// Tests for pointing Velero at a registered storage link rather than at hand-typed
/// bucket details. The mapping is what decides whether a component installs cleanly
/// and then fails every backup, so each key is checked rather than eyeballed.
/// </summary>
public class VeleroStorageIntegrationTests
{
    private static StorageLink Link(
        StorageProvider provider = StorageProvider.MinIO,
        string? bucket = "entkube-backups",
        string? endpoint = "https://minio.example.com:9000",
        string? region = "eu-north-1") => new()
    {
        Id = Guid.NewGuid(),
        TenantId = Guid.NewGuid(),
        EnvironmentId = Guid.NewGuid(),
        Name = "Production backups",
        Provider = provider,
        BucketName = bucket,
        Endpoint = endpoint,
        Region = region,
    };

    [Fact]
    public void Maps_the_link_onto_veleros_backup_storage_location()
    {
        Dictionary<string, string> values = VeleroService.BuildStorageValues(Link());

        values["configuration.backupStorageLocation.0.bucket"].Should().Be("entkube-backups");
        values["configuration.backupStorageLocation.0.config.region"].Should().Be("eu-north-1");
        values["configuration.backupStorageLocation.0.config.s3Url"].Should().Be("https://minio.example.com:9000");
        values["configuration.backupStorageLocation.0.provider"].Should().Be("aws");
    }

    [Fact]
    public void Uses_the_aws_plugin_even_for_non_aws_stores()
    {
        // The AWS plugin serves every S3-compatible backend; naming the provider after the
        // store would leave Velero looking for a plugin that is not installed.
        VeleroService.BuildStorageValues(Link(StorageProvider.MinIO))
            ["configuration.backupStorageLocation.0.provider"].Should().Be("aws");
    }

    [Fact]
    public void Forces_path_style_addressing_for_s3_compatible_stores()
    {
        // MinIO and Ceph RGW do not serve virtual-hosted-style bucket URLs; getting this
        // backwards produces DNS failures against a bucket-prefixed hostname.
        VeleroService.BuildStorageValues(Link(StorageProvider.MinIO))
            ["configuration.backupStorageLocation.0.config.s3ForcePathStyle"].Should().Be("true");
    }

    [Fact]
    public void Leaves_path_style_off_for_real_aws()
    {
        VeleroService.BuildStorageValues(Link(StorageProvider.AwsS3))
            ["configuration.backupStorageLocation.0.config.s3ForcePathStyle"].Should().Be("false");
    }

    [Fact]
    public void A_link_without_a_region_gets_the_conventional_default()
    {
        // Most S3-compatible stores accept us-east-1 but reject an empty region outright.
        VeleroService.BuildStorageValues(Link(region: null))
            ["configuration.backupStorageLocation.0.config.region"].Should().Be("us-east-1");
    }

    [Fact]
    public void A_link_without_an_endpoint_writes_no_s3url()
    {
        // Real AWS needs no endpoint, and writing an empty one makes the plugin dial "".
        VeleroService.BuildStorageValues(Link(StorageProvider.AwsS3, endpoint: null))
            .Should().NotContainKey("configuration.backupStorageLocation.0.config.s3Url");
    }

    // ── Credentials ──

    [Fact]
    public void Builds_the_ini_profile_the_aws_plugin_reads()
    {
        // The plugin reads an INI file, not environment variables, so the whole block is
        // one secret rather than two values.
        string credentials = VeleroService.BuildCredentialsFile("AKIAEXAMPLE", "s3cr3t");

        credentials.Should().StartWith("[default]\n");
        credentials.Should().Contain("aws_access_key_id=AKIAEXAMPLE");
        credentials.Should().Contain("aws_secret_access_key=s3cr3t");
        credentials.Should().EndWith("\n");
    }

    [Fact]
    public void The_catalog_entry_takes_a_storage_link_rather_than_typed_bucket_details()
    {
        CatalogEntry velero = ComponentCatalog.Entries.Single(e => e.Key == "velero");

        velero.FormFields.Should().Contain(f => f.Type == FormFieldType.StorageLink);

        // Endpoint, bucket, region and keys must not be re-enterable: two copies of a
        // credential can disagree, and a rotated key would then have to be chased through
        // every component that copied it.
        string[] retypedFields = ["bucket", "s3-url", "region", "access-key", "secret-key"];
        velero.FormFields.Select(f => f.Key).Should().NotIntersectWith(retypedFields);
    }

    [Fact]
    public void The_credentials_field_is_hidden_and_vault_backed()
    {
        ComponentFormField credentials = ComponentCatalog.Entries
            .Single(e => e.Key == "velero").FormFields
            .Single(f => f.Key == "velero-s3-credentials");

        credentials.Hidden.Should().BeTrue();
        credentials.StoreAsSecret.Should().BeTrue();
        credentials.YamlPath.Should().Be("credentials.secretContents.cloud");
    }

    [Fact]
    public void Stored_config_round_trips_so_the_picker_can_be_repopulated()
    {
        Guid linkId = Guid.NewGuid();
        string json = System.Text.Json.JsonSerializer.Serialize(
            new VeleroService.VeleroConfig { StorageLinkId = linkId });

        VeleroService.TryReadConfig(json)!.StorageLinkId.Should().Be(linkId);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("{not json")]
    [InlineData("{\"somethingElse\":1}")]
    public void Unreadable_stored_config_does_not_throw(string? json)
    {
        // Components configured before this existed carry other JSON, or none.
        Action act = () => VeleroService.TryReadConfig(json);
        act.Should().NotThrow();
    }
}

/// <summary>
/// Tests prompted by a real failure: a Velero component installed cleanly, every backup
/// came back PartiallyFailed, and the readiness report said only that — leaving the
/// operator to go to the CLI to find out why.
/// </summary>
public class VeleroFailureReportingTests
{
    private static readonly DateTime Now = new(2026, 8, 23, 12, 0, 0, DateTimeKind.Utc);

    private static ClusterDrStatus StatusWith(VeleroBackup backup) => new()
    {
        ClusterId = Guid.NewGuid(),
        ClusterName = "prod",
        IsVeleroInstalled = true,
        Backups = [backup],
        Schedules = [new VeleroSchedule { Name = "daily", Cron = "0 2 * * *" }],
    };

    [Fact]
    public void Veleros_own_failure_reason_reaches_the_operator()
    {
        ClusterDrStatus status = StatusWith(new VeleroBackup
        {
            Name = "b", Phase = VeleroPhase.PartiallyFailed,
            StartedAt = Now.AddMinutes(-10), CompletedAt = Now.AddMinutes(-5),
            Errors = 4, FailureReason = "found a backup with status Failed during migration",
        });

        DrGap gap = DrReadiness.Evaluate(status, Now).Single(g => g.Key.StartsWith("dr-no-backup:"));

        gap.Detail.Should().Contain("found a backup with status Failed during migration");
    }

    [Fact]
    public void Validation_errors_reach_the_operator()
    {
        ClusterDrStatus status = StatusWith(new VeleroBackup
        {
            Name = "b", Phase = VeleroPhase.Failed, StartedAt = Now.AddMinutes(-10),
            ValidationErrors = ["backup storage location \"default\" is unavailable"],
        });

        DrReadiness.Evaluate(status, Now).Single(g => g.Key.StartsWith("dr-no-backup:"))
            .Detail.Should().Contain("is unavailable");
    }

    [Fact]
    public void A_partially_failed_backup_with_item_errors_points_at_the_usual_cause()
    {
        // The common one on a fresh install: snapshots disabled AND file-system backup off,
        // so a volume can be captured by neither route.
        ClusterDrStatus status = StatusWith(new VeleroBackup
        {
            Name = "b", Phase = VeleroPhase.PartiallyFailed,
            StartedAt = Now.AddMinutes(-10), CompletedAt = Now.AddMinutes(-5), Errors = 7,
        });

        DrReadiness.Evaluate(status, Now).Single(g => g.Key.StartsWith("dr-no-backup:"))
            .Detail.Should().Contain("defaultVolumesToFsBackup");
    }

    [Fact]
    public void A_clean_backup_carries_no_failure_noise()
    {
        ClusterDrStatus status = StatusWith(new VeleroBackup
        {
            Name = "b", Phase = VeleroPhase.Completed,
            StartedAt = Now.AddMinutes(-10), CompletedAt = Now.AddMinutes(-5),
        });

        DrReadiness.Evaluate(status, Now).Should().NotContain(g => g.Key.StartsWith("dr-no-backup:"));
    }

    [Fact]
    public void The_failure_reason_is_parsed_off_the_backup_resource()
    {
        const string json = """
        { "items": [ {
            "metadata": { "name": "daily-1" },
            "spec": {},
            "status": {
              "phase": "PartiallyFailed", "errors": 3,
              "failureReason": "unable to write backup",
              "validationErrors": [ "storage location unavailable" ]
            } } ] }
        """;

        VeleroBackup backup = VeleroService.ParseBackups(json).Single();

        backup.FailureReason.Should().Be("unable to write backup");
        backup.ValidationErrors.Should().ContainSingle().Which.Should().Contain("unavailable");
        backup.IsUsable.Should().BeFalse();
    }

    [Fact]
    public void The_catalog_enables_file_system_backup_for_volumes()
    {
        // The bug this class exists for: deployNodeAgent true and snapshotsEnabled false,
        // but defaultVolumesToFsBackup left at the chart's default of false. A volume was
        // then backed up by neither route and every backup ended PartiallyFailed.
        CatalogEntry velero = ComponentCatalog.Entries.Single(e => e.Key == "velero");

        velero.DefaultValues.Should().NotBeNull();
        velero.DefaultValues!.Should().Contain("defaultVolumesToFsBackup: true");
        velero.DefaultValues.Should().Contain("deployNodeAgent: true");
    }
}
