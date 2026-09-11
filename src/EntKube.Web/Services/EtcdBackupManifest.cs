namespace EntKube.Web.Services;

/// <summary>Where etcd snapshots are shipped to.</summary>
public sealed class EtcdBackupTarget
{
    public required string Endpoint { get; init; }
    public required string Bucket { get; init; }
    public required string AccessKey { get; init; }
    public required string SecretKey { get; init; }
    public string Region { get; init; } = "us-east-1";

    /// <summary>How many snapshots to keep. Older ones are pruned by the job itself.</summary>
    public int KeepCount { get; init; } = 14;
}

/// <summary>
/// A CronJob that snapshots etcd and ships the result off the cluster.
///
/// <para><b>Why this is separate from Velero.</b> Velero backs up what runs on a cluster: objects
/// and volume contents, restored into a cluster that already exists. It cannot restore the cluster
/// itself, because the thing it would restore into is the thing that is gone. An etcd snapshot is
/// the whole control plane's state and is what a cluster is rebuilt from. Having one and not the
/// other is a plan for every failure except the one that matters.</para>
///
/// <para><b>Why the destination is off-cluster.</b> Shipping snapshots to storage the cluster
/// itself serves — CubeFS living on its own nodes — is circular: losing the cluster loses the
/// backups taken to recover it. The target is EntKube's own object storage, or a bucket on the
/// cloud outside the cluster.</para>
/// </summary>
public static class EtcdBackupManifest
{
    public const string Namespace = "kube-system";
    public const string JobName = "entkube-etcd-snapshot";
    public const string SecretName = "entkube-etcd-backup";

    /// <summary>
    /// The CronJob and the credentials it reads. Runs on a control-plane node with host networking,
    /// because etcd listens on loopback and its client certificates are on that node's filesystem —
    /// there is no way to snapshot it from anywhere else.
    /// </summary>
    public static string Build(string clusterName, EtcdBackupTarget target, string schedule = "0 2 * * *")
    {
        string script = BackupScript(clusterName, target.KeepCount);

        return $"""
            apiVersion: v1
            kind: Secret
            metadata:
              name: {SecretName}
              namespace: {Namespace}
            type: Opaque
            stringData:
              AWS_ACCESS_KEY_ID: {target.AccessKey}
              AWS_SECRET_ACCESS_KEY: {target.SecretKey}
              AWS_DEFAULT_REGION: {target.Region}
              S3_ENDPOINT: {target.Endpoint}
              S3_BUCKET: {target.Bucket}
            ---
            apiVersion: batch/v1
            kind: CronJob
            metadata:
              name: {JobName}
              namespace: {Namespace}
              labels:
                app.kubernetes.io/managed-by: entkube
            spec:
              schedule: "{schedule}"
              concurrencyPolicy: Forbid
              successfulJobsHistoryLimit: 3
              failedJobsHistoryLimit: 3
              jobTemplate:
                spec:
                  backoffLimit: 2
                  template:
                    spec:
                      restartPolicy: OnFailure
                      hostNetwork: true
                      nodeSelector:
                        node-role.kubernetes.io/control-plane: ""
                      tolerations:
                        - key: node-role.kubernetes.io/control-plane
                          effect: NoSchedule
                        - key: node-role.kubernetes.io/master
                          effect: NoSchedule
                      containers:
                        - name: snapshot
                          image: bitnami/etcd:3.5
                          command: ["/bin/bash", "-c"]
                          args:
                            - |
            {Indent(script, 18)}
                          envFrom:
                            - secretRef:
                                name: {SecretName}
                          volumeMounts:
                            - name: etcd-certs
                              mountPath: /etc/kubernetes/pki/etcd
                              readOnly: true
                          resources:
                            requests:
                              cpu: 100m
                              memory: 128Mi
                            limits:
                              memory: 512Mi
                      volumes:
                        - name: etcd-certs
                          hostPath:
                            path: /etc/kubernetes/pki/etcd
                            type: Directory
            """;
    }

    /// <summary>
    /// Snapshot, verify, upload, prune. The verify step is not ceremony: <c>etcdctl snapshot save</c>
    /// exits zero on a truncated file often enough that an unverified backup is a file you find out
    /// about during a restore.
    /// </summary>
    public static string BackupScript(string clusterName, int keepCount) =>
        // Not an interpolated literal: the script is full of ${...} and awk '{...}', and in a raw
        // interpolated string those braces are interpolation syntax rather than text. Substituting
        // afterwards keeps the shell readable as shell.
        ScriptTemplate
            .Replace("__CLUSTER__", clusterName, StringComparison.Ordinal)
            .Replace("__KEEP__", keepCount.ToString(), StringComparison.Ordinal);

    private const string ScriptTemplate = """
        set -euo pipefail

        STAMP=$(date -u +%Y%m%dT%H%M%SZ)
        SNAPSHOT=/tmp/__CLUSTER__-etcd-${STAMP}.db

        export ETCDCTL_API=3
        etcdctl \
          --endpoints=https://127.0.0.1:2379 \
          --cacert=/etc/kubernetes/pki/etcd/ca.crt \
          --cert=/etc/kubernetes/pki/etcd/server.crt \
          --key=/etc/kubernetes/pki/etcd/server.key \
          snapshot save "$SNAPSHOT"

        # A snapshot that cannot be read back is not a backup. This is cheap, and it catches the
        # truncated writes that `snapshot save` reports as success.
        etcdctl snapshot status "$SNAPSHOT" --write-out=table

        aws --endpoint-url "$S3_ENDPOINT" s3 cp "$SNAPSHOT" \
          "s3://$S3_BUCKET/__CLUSTER__/etcd/$(basename "$SNAPSHOT")"

        rm -f "$SNAPSHOT"

        # Prune, oldest first. Names sort chronologically because the timestamp is fixed-width.
        aws --endpoint-url "$S3_ENDPOINT" s3 ls "s3://$S3_BUCKET/__CLUSTER__/etcd/" \
          | awk '{print $4}' | sort | head -n -__KEEP__ \
          | while read -r old; do
              [ -n "$old" ] && aws --endpoint-url "$S3_ENDPOINT" s3 rm "s3://$S3_BUCKET/__CLUSTER__/etcd/$old"
            done

        echo "etcd snapshot for __CLUSTER__ complete"
        """;

    private static string Indent(string text, int spaces)
    {
        string pad = new(' ', spaces);
        return string.Concat(text.Replace("\r\n", "\n").TrimEnd('\n').Split('\n').Select(l => pad + l + "\n"))
            .TrimEnd('\n');
    }
}
