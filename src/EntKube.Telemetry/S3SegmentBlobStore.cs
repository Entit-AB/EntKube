using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;

namespace EntKube.Web.Services.Telemetry;

/// <summary>
/// S3/MinIO-backed <see cref="ISegmentBlobStore"/> for sealed telemetry segments, built on the AWS SDK
/// already used across the app. Configured from the <c>Telemetry:ObjectStorage</c> section
/// (Endpoint/Bucket/Region/AccessKey/SecretKey/ForcePathStyle), overridable via
/// <c>Telemetry__ObjectStorage__*</c> env vars — the same pattern as the rest of the telemetry config.
/// When no bucket is configured the store reports <see cref="IsConfigured"/> = false and the engine
/// falls back to the local blob store, so a deployment without object storage still works.
/// </summary>
public sealed class S3SegmentBlobStore : ISegmentBlobStore, IDisposable
{
    private readonly IAmazonS3? _client;
    private readonly string _bucket;

    public S3SegmentBlobStore(IConfiguration config)
    {
        _bucket = config.GetValue<string>("Telemetry:ObjectStorage:Bucket") ?? "";
        string endpoint = config.GetValue<string>("Telemetry:ObjectStorage:Endpoint") ?? "";
        string region = config.GetValue<string>("Telemetry:ObjectStorage:Region") ?? "us-east-1";
        string accessKey = config.GetValue<string>("Telemetry:ObjectStorage:AccessKey") ?? "";
        string secretKey = config.GetValue<string>("Telemetry:ObjectStorage:SecretKey") ?? "";
        bool forcePathStyle = config.GetValue<bool?>("Telemetry:ObjectStorage:ForcePathStyle") ?? true;

        if (string.IsNullOrWhiteSpace(_bucket) || string.IsNullOrWhiteSpace(accessKey))
            return; // unconfigured → IsConfigured=false

        var s3Config = new AmazonS3Config
        {
            ForcePathStyle = forcePathStyle,   // required for MinIO / non-AWS S3
            AuthenticationRegion = region,
        };
        if (!string.IsNullOrWhiteSpace(endpoint))
            s3Config.ServiceURL = endpoint;    // MinIO / S3-compatible endpoint
        else
            s3Config.RegionEndpoint = Amazon.RegionEndpoint.GetBySystemName(region);

        _client = new AmazonS3Client(new BasicAWSCredentials(accessKey, secretKey), s3Config);
    }

    public bool IsConfigured => _client is not null;

    public async Task PutAsync(string key, string localFilePath, CancellationToken ct = default)
    {
        EnsureConfigured();
        await _client!.PutObjectAsync(new PutObjectRequest
        {
            BucketName = _bucket,
            Key = key,
            FilePath = localFilePath,
        }, ct);
    }

    public async Task GetAsync(string key, string destFilePath, CancellationToken ct = default)
    {
        EnsureConfigured();
        Directory.CreateDirectory(Path.GetDirectoryName(destFilePath)!);
        using GetObjectResponse response = await _client!.GetObjectAsync(_bucket, key, ct);
        await response.WriteResponseStreamToFileAsync(destFilePath, append: false, ct);
    }

    public async Task DeleteAsync(string key, CancellationToken ct = default)
    {
        EnsureConfigured();
        await _client!.DeleteObjectAsync(_bucket, key, ct);
    }

    private void EnsureConfigured()
    {
        if (_client is null)
            throw new InvalidOperationException("Telemetry object storage is not configured (Telemetry:ObjectStorage:Bucket/AccessKey).");
    }

    public void Dispose() => _client?.Dispose();
}
