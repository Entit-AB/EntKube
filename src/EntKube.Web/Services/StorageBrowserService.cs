using Amazon;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using EntKube.Web.Data;
using Microsoft.EntityFrameworkCore;

namespace EntKube.Web.Services;

/// <summary>
/// An object (file) returned from a bucket listing.
/// </summary>
public class StorageObject
{
    public required string Key { get; set; }
    public required string Prefix { get; set; }

    public string Name => Key.Length > Prefix.Length
        ? Key[Prefix.Length..]
        : Key;

    public long SizeBytes { get; set; }
    public DateTime LastModified { get; set; }
    public string? ETag { get; set; }
}

/// <summary>
/// Result of listing a bucket prefix — folders (common prefixes) plus objects.
/// </summary>
public class StorageBrowseResult
{
    public List<string> Folders { get; set; } = [];
    public List<StorageObject> Objects { get; set; } = [];
}

/// <summary>
/// Provides file-level CRUD operations on any S3-compatible storage link:
/// AWS S3, MinIO, and Cleura S3. Credentials are retrieved from the vault.
/// Downloads are buffered in memory; the maximum file size is 100 MB.
/// </summary>
public class StorageBrowserService(StorageLinkClientFactory clientFactory)
{
    private const long MaxDownloadBytes = 100L * 1024 * 1024;

    // ── S3 Client Factory ──

    /// <summary>Creates an S3 client for the given storage link (via the shared provider-aware factory).</summary>
    private Task<(AmazonS3Client Client, IDisposable? Cleanup)> GetClientAsync(
        Guid tenantId, StorageLink link, CancellationToken ct)
        => clientFactory.CreateClientAsync(tenantId, link, ct);

    private static string RequireBucket(StorageLink link) =>
        link.BucketName ?? throw new InvalidOperationException("Bucket name is not configured for this storage link.");

    // ── List ──

    /// <summary>
    /// Lists objects and "folders" (common prefixes) at the given prefix.
    /// Pass an empty string to list the bucket root.
    /// </summary>
    public async Task<StorageBrowseResult> ListAsync(
        Guid tenantId, StorageLink link, string prefix, CancellationToken ct = default)
    {
        (AmazonS3Client client, IDisposable? cleanup) = await GetClientAsync(tenantId, link, ct);
        using AmazonS3Client _ = client;
        using IDisposable? __ = cleanup;
        string bucket = RequireBucket(link);

        ListObjectsV2Request request = new()
        {
            BucketName = bucket,
            Prefix = prefix,
            Delimiter = "/",
            MaxKeys = 1000
        };

        ListObjectsV2Response response = await client.ListObjectsV2Async(request, ct);

        StorageBrowseResult result = new();

        foreach (string? cp in response.CommonPrefixes ?? [])
        {
            if (cp is not null)
                result.Folders.Add(cp);
        }

        foreach (S3Object obj in response.S3Objects ?? [])
        {
            string? key = obj.Key;
            if (key is null || key == prefix)
                continue; // skip null keys and folder markers

            result.Objects.Add(new StorageObject
            {
                Key = key,
                Prefix = prefix,
                SizeBytes = obj.Size ?? 0,
                LastModified = obj.LastModified ?? DateTime.MinValue,
                ETag = obj.ETag?.Trim('"')
            });
        }

        return result;
    }

    // ── Upload ──

    /// <summary>
    /// Uploads a stream to the given key in the bucket.
    /// </summary>
    public async Task UploadAsync(
        Guid tenantId,
        StorageLink link,
        string key,
        Stream stream,
        string contentType,
        CancellationToken ct = default)
    {
        (AmazonS3Client client, IDisposable? cleanup) = await GetClientAsync(tenantId, link, ct);
        using AmazonS3Client _ = client;
        using IDisposable? __ = cleanup;
        string bucket = RequireBucket(link);

        PutObjectRequest request = new()
        {
            BucketName = bucket,
            Key = key,
            InputStream = stream,
            ContentType = contentType,
            AutoCloseStream = false
        };

        await client.PutObjectAsync(request, ct);
    }

    // ── Download ──

    /// <summary>
    /// Downloads an object and returns its bytes. Maximum 100 MB.
    /// </summary>
    public async Task<byte[]> DownloadAsync(
        Guid tenantId, StorageLink link, string key, CancellationToken ct = default)
    {
        (AmazonS3Client client, IDisposable? cleanup) = await GetClientAsync(tenantId, link, ct);
        using AmazonS3Client _ = client;
        using IDisposable? __ = cleanup;
        string bucket = RequireBucket(link);

        GetObjectRequest request = new() { BucketName = bucket, Key = key };
        using GetObjectResponse response = await client.GetObjectAsync(request, ct);

        if (response.ContentLength > MaxDownloadBytes)
        {
            throw new InvalidOperationException(
                $"File is too large to download through the browser " +
                $"({response.ContentLength / 1024 / 1024} MB). Maximum is {MaxDownloadBytes / 1024 / 1024} MB.");
        }

        using MemoryStream ms = new();
        await response.ResponseStream.CopyToAsync(ms, ct);
        return ms.ToArray();
    }

    // ── Delete ──

    /// <summary>
    /// Deletes a single object.
    /// </summary>
    public async Task DeleteAsync(
        Guid tenantId, StorageLink link, string key, CancellationToken ct = default)
    {
        (AmazonS3Client client, IDisposable? cleanup) = await GetClientAsync(tenantId, link, ct);
        using AmazonS3Client _ = client;
        using IDisposable? __ = cleanup;
        string bucket = RequireBucket(link);
        await client.DeleteObjectAsync(bucket, key, ct);
    }

    /// <summary>
    /// Deletes multiple objects in S3 batch delete requests (max 1000 per request).
    /// </summary>
    public async Task DeleteManyAsync(
        Guid tenantId, StorageLink link, IEnumerable<string> keys, CancellationToken ct = default)
    {
        (AmazonS3Client client, IDisposable? cleanup) = await GetClientAsync(tenantId, link, ct);
        using AmazonS3Client _ = client;
        using IDisposable? __ = cleanup;
        string bucket = RequireBucket(link);

        List<KeyVersion> keyVersions = keys.Select(k => new KeyVersion { Key = k }).ToList();

        for (int i = 0; i < keyVersions.Count; i += 1000)
        {
            DeleteObjectsRequest deleteRequest = new()
            {
                BucketName = bucket,
                Objects = keyVersions.Skip(i).Take(1000).ToList()
            };
            await client.DeleteObjectsAsync(deleteRequest, ct);
        }
    }

    /// <summary>
    /// Deletes all objects under a prefix (folder), including the folder marker itself.
    /// Paginates through all objects to handle folders with more than 1000 items.
    /// </summary>
    public async Task DeleteFolderAsync(
        Guid tenantId, StorageLink link, string folderPrefix, CancellationToken ct = default)
    {
        (AmazonS3Client client, IDisposable? cleanup) = await GetClientAsync(tenantId, link, ct);
        using AmazonS3Client _ = client;
        using IDisposable? __ = cleanup;
        string bucket = RequireBucket(link);
        string prefix = folderPrefix.TrimEnd('/') + '/';
        string? continuationToken = null;

        do
        {
            ListObjectsV2Request listRequest = new()
            {
                BucketName = bucket,
                Prefix = prefix,
                MaxKeys = 1000,
                ContinuationToken = continuationToken
            };

            ListObjectsV2Response listResponse = await client.ListObjectsV2Async(listRequest, ct);

            List<S3Object> objects = listResponse.S3Objects ?? [];
            if (objects.Count > 0)
            {
                DeleteObjectsRequest deleteRequest = new()
                {
                    BucketName = bucket,
                    Objects = objects
                        .Where(o => o.Key is not null)
                        .Select(o => new KeyVersion { Key = o.Key })
                        .ToList()
                };
                await client.DeleteObjectsAsync(deleteRequest, ct);
            }

            continuationToken = listResponse.IsTruncated == true ? listResponse.NextContinuationToken : null;
        }
        while (continuationToken is not null);
    }

    // ── Create Folder ──

    /// <summary>
    /// Creates a "folder" by uploading a zero-byte object with a trailing slash key.
    /// </summary>
    public async Task CreateFolderAsync(
        Guid tenantId, StorageLink link, string folderPath, CancellationToken ct = default)
    {
        (AmazonS3Client client, IDisposable? cleanup) = await GetClientAsync(tenantId, link, ct);
        using AmazonS3Client _ = client;
        using IDisposable? __ = cleanup;
        string bucket = RequireBucket(link);
        string key = folderPath.TrimEnd('/') + '/';

        PutObjectRequest request = new()
        {
            BucketName = bucket,
            Key = key,
            ContentBody = ""
        };

        await client.PutObjectAsync(request, ct);
    }

    // ── Presigned URL ──

    /// <summary>
    /// Generates a presigned GET URL for the given object, valid for <paramref name="expiryHours"/> hours.
    /// The URL allows anyone with it to download the file directly from the storage provider
    /// without credentials. For cluster-internal MinIO endpoints the URL will contain the
    /// internal hostname and will only be reachable from within the cluster.
    /// </summary>
    public async Task<string> GetPresignedUrlAsync(
        Guid tenantId, StorageLink link, string key, int expiryHours = 1, CancellationToken ct = default)
    {
        (AmazonS3Client client, IDisposable? cleanup) = await GetClientAsync(tenantId, link, ct);
        using AmazonS3Client _ = client;
        using IDisposable? __ = cleanup;
        string bucket = RequireBucket(link);

        GetPreSignedUrlRequest request = new()
        {
            BucketName = bucket,
            Key = key,
            Expires = DateTime.UtcNow.AddHours(expiryHours),
            Verb = HttpVerb.GET
        };

        return await client.GetPreSignedURLAsync(request);
    }
}
