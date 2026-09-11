using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using EntKube.Web.Services;
using FluentAssertions;
using YamlDotNet.RepresentationModel;

namespace EntKube.Web.Tests;

/// <summary>
/// The two things that kill a healthy cluster on a timer nobody wrote down: certificates that
/// expire a year after it was built, and the absence of the one backup that could rebuild it.
/// </summary>
public class ClusterCertificateExpiryTests
{
    /// <summary>A kubeconfig carrying a real certificate with a chosen expiry.</summary>
    private static string KubeconfigExpiringIn(TimeSpan remaining)
    {
        using RSA key = RSA.Create(2048);
        CertificateRequest request = new("CN=kubernetes-admin", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        // notBefore is anchored a year before the expiry rather than to "now", so an already-expired
        // certificate is still a valid one to construct — which is the case worth testing.
        DateTimeOffset notAfter = DateTimeOffset.UtcNow.Add(remaining);
        using X509Certificate2 certificate = request.CreateSelfSigned(notAfter.AddDays(-365), notAfter);

        string base64 = Convert.ToBase64String(certificate.Export(X509ContentType.Cert));

        return $"""
            apiVersion: v1
            clusters:
            - cluster:
                server: https://10.0.0.5:6443
              name: kubernetes
            users:
            - name: kubernetes-admin
              user:
                client-certificate-data: {base64}
            """;
    }

    [Fact]
    public void A_year_out_there_is_nothing_to_do()
    {
        CertificateStatus status = ClusterCertificateExpiry.FromKubeconfig(KubeconfigExpiringIn(TimeSpan.FromDays(350)));

        status.Urgency.Should().Be(CertificateUrgency.Fine);
        status.DaysRemaining.Should().BeGreaterThan(300);
    }

    [Fact]
    public void Inside_ninety_days_it_is_worth_planning_and_an_upgrade_would_cover_it()
    {
        CertificateStatus status = ClusterCertificateExpiry.FromKubeconfig(KubeconfigExpiringIn(TimeSpan.FromDays(60)));

        status.Urgency.Should().Be(CertificateUrgency.Soon);
        status.Message.Should().Contain("upgrade");
    }

    [Fact]
    public void Inside_thirty_days_it_says_to_roll_the_control_plane()
    {
        CertificateStatus status = ClusterCertificateExpiry.FromKubeconfig(KubeconfigExpiringIn(TimeSpan.FromDays(10)));

        status.Urgency.Should().Be(CertificateUrgency.Urgent);
        status.Message.Should().Contain("Roll the control plane");
    }

    [Fact]
    public void An_expired_certificate_explains_what_has_already_broken()
    {
        // By this point kubelets cannot authenticate. Someone reading this is looking at a cluster
        // that stopped working for no apparent reason.
        CertificateStatus status = ClusterCertificateExpiry.FromKubeconfig(KubeconfigExpiringIn(TimeSpan.FromDays(-5)));

        status.Urgency.Should().Be(CertificateUrgency.Expired);
        status.Message.Should().Contain("can no longer");
    }

    [Theory]
    [InlineData(-1, CertificateUrgency.Expired)]
    [InlineData(0, CertificateUrgency.Urgent)]
    [InlineData(30, CertificateUrgency.Urgent)]
    [InlineData(31, CertificateUrgency.Soon)]
    [InlineData(90, CertificateUrgency.Soon)]
    [InlineData(91, CertificateUrgency.Fine)]
    public void The_thresholds_are_where_they_are_documented_to_be(int days, CertificateUrgency expected)
    {
        ClusterCertificateExpiry.UrgencyFor(days).Should().Be(expected);
    }

    [Fact]
    public void A_kubeconfig_without_an_embedded_certificate_is_not_reported_as_a_problem()
    {
        // Token-authenticated kubeconfigs are renewed elsewhere. Raising an alarm about a cluster
        // that is fine is how the real alarms stop being read.
        CertificateStatus status = ClusterCertificateExpiry.FromKubeconfig(
            "apiVersion: v1\nusers:\n- name: admin\n  user:\n    token: abc\n");

        status.Urgency.Should().Be(CertificateUrgency.Fine);
        status.NotAfter.Should().BeNull();
    }

    [Fact]
    public void Unreadable_certificate_data_does_not_throw()
    {
        ClusterCertificateExpiry.FromKubeconfig("client-certificate-data: not-base64!!")
            .Urgency.Should().Be(CertificateUrgency.Fine);
    }
}

/// <summary>
/// The etcd snapshot job. Velero restores workloads into a cluster that still exists; this is what
/// a cluster is rebuilt from, and the difference only becomes apparent on the day it matters.
/// </summary>
public class EtcdBackupManifestTests
{
    private static EtcdBackupTarget Target => new()
    {
        Endpoint = "https://minio.entkube.internal",
        Bucket = "cluster-backups",
        AccessKey = "AKIAEXAMPLE",
        SecretKey = "s3cr3t"
    };

    private static List<YamlMappingNode> Documents(string manifest)
    {
        YamlStream stream = new();
        stream.Load(new StringReader(manifest));
        return stream.Documents.Select(d => (YamlMappingNode)d.RootNode).ToList();
    }

    [Fact]
    public void The_manifest_is_valid_yaml_with_the_job_and_its_credentials()
    {
        List<YamlMappingNode> documents = Documents(EtcdBackupManifest.Build("prod-eu-1", Target));

        documents.Select(d => d["kind"].ToString()).Should().BeEquivalentTo("Secret", "CronJob");
    }

    [Fact]
    public void The_job_runs_on_a_control_plane_node_with_host_networking()
    {
        // etcd listens on loopback and its client certificates live on the node's filesystem.
        // There is nowhere else this can run from.
        YamlMappingNode cronJob = Documents(EtcdBackupManifest.Build("prod-eu-1", Target))
            .Single(d => d["kind"].ToString() == "CronJob");

        YamlMappingNode podSpec = (YamlMappingNode)
            ((YamlMappingNode)((YamlMappingNode)((YamlMappingNode)((YamlMappingNode)cronJob["spec"])["jobTemplate"])["spec"])["template"])["spec"];

        podSpec["hostNetwork"].ToString().Should().Be("true");
        ((YamlMappingNode)podSpec["nodeSelector"]).Children.Keys
            .Select(k => k.ToString()).Should().Contain("node-role.kubernetes.io/control-plane");
    }

    [Fact]
    public void The_job_tolerates_the_control_plane_taint_or_it_would_never_be_scheduled()
    {
        EtcdBackupManifest.Build("prod-eu-1", Target)
            .Should().Contain("key: node-role.kubernetes.io/control-plane");
    }

    [Fact]
    public void Only_one_snapshot_runs_at_a_time()
    {
        // Two concurrent snapshots of the same etcd is load on a control plane for no benefit.
        EtcdBackupManifest.Build("prod-eu-1", Target).Should().Contain("concurrencyPolicy: Forbid");
    }

    [Fact]
    public void The_snapshot_is_verified_before_it_is_trusted()
    {
        // etcdctl snapshot save exits zero on a truncated file often enough that an unverified
        // backup is a file you find out about during a restore.
        string script = EtcdBackupManifest.BackupScript("prod-eu-1", 14);

        script.Should().Contain("snapshot save");
        script.Should().Contain("snapshot status");
        script.IndexOf("snapshot save", StringComparison.Ordinal)
            .Should().BeLessThan(script.IndexOf("snapshot status", StringComparison.Ordinal));
    }

    [Fact]
    public void Snapshots_are_shipped_off_the_cluster_and_pruned()
    {
        string script = EtcdBackupManifest.BackupScript("prod-eu-1", 14);

        script.Should().Contain("s3://$S3_BUCKET/prod-eu-1/etcd/");
        script.Should().Contain("head -n -14");
    }

    [Fact]
    public void The_shell_survives_being_generated_from_csharp()
    {
        // The braces in ${STAMP} and awk '{print $4}' are interpolation syntax in a raw string,
        // and a script that arrives with them eaten fails at 2am inside a container.
        string script = EtcdBackupManifest.BackupScript("prod-eu-1", 14);

        script.Should().Contain("${STAMP}");
        script.Should().Contain("awk '{print $4}'");
        script.Should().NotContain("__CLUSTER__");
        script.Should().NotContain("__KEEP__");
    }

    [Fact]
    public void The_script_stops_at_the_first_failure()
    {
        // Without this the upload runs after a failed snapshot and overwrites a good backup with
        // nothing.
        EtcdBackupManifest.BackupScript("prod-eu-1", 14).Should().StartWith("set -euo pipefail");
    }
}
