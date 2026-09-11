using EntKube.Web.Services;
using FluentAssertions;

namespace EntKube.Web.Tests;

/// <summary>
/// Reading whether snapshots are actually being taken. The distinction this has to preserve is
/// between a job that exists and a job that has worked: the first is an arrangement, the second is
/// a backup, and only one of them helps on the day the cluster is gone.
/// </summary>
public class EtcdBackupReaderTests
{
    [Fact]
    public void An_installed_job_that_has_never_succeeded_says_so()
    {
        string json = """
            {"items": [{"metadata": {"name": "entkube-etcd-snapshot"}, "spec": {"schedule": "0 2 * * *"}, "status": {}}]}
            """;

        EtcdBackupStatus status = EtcdBackupReader.Read(json, EtcdBackupManifest.JobName);

        status.Installed.Should().BeTrue();
        status.Schedule.Should().Be("0 2 * * *");
        status.Detail.Should().Contain("no snapshot has succeeded");
    }

    [Fact]
    public void A_job_that_has_succeeded_reports_when()
    {
        string json = """
            {"items": [{"metadata": {"name": "entkube-etcd-snapshot"},
              "spec": {"schedule": "0 2 * * *"},
              "status": {"lastSuccessfulTime": "2026-09-11T02:00:31Z"}}]}
            """;

        EtcdBackupStatus status = EtcdBackupReader.Read(json, EtcdBackupManifest.JobName);

        status.Installed.Should().BeTrue();
        status.Detail.Should().Contain("2026-09-11");
    }

    [Fact]
    public void Other_cronjobs_in_the_namespace_are_not_mistaken_for_it()
    {
        // kube-system is full of other people's jobs.
        string json = """
            {"items": [{"metadata": {"name": "some-other-job"}, "spec": {"schedule": "* * * * *"}}]}
            """;

        EtcdBackupReader.Read(json, EtcdBackupManifest.JobName).Installed.Should().BeFalse();
    }

    [Fact]
    public void No_job_at_all_reads_as_not_installed()
    {
        EtcdBackupReader.Read("""{"items": []}""", EtcdBackupManifest.JobName).Installed.Should().BeFalse();
    }

    [Fact]
    public void An_unreadable_reply_is_not_reported_as_a_working_backup()
    {
        // The dangerous direction: anything ambiguous must read as "no backup", never as one.
        EtcdBackupReader.Read("{not json", EtcdBackupManifest.JobName).Installed.Should().BeFalse();
        EtcdBackupReader.Read("", EtcdBackupManifest.JobName).Installed.Should().BeFalse();
    }
}
