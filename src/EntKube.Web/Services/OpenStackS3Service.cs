using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Amazon.S3;
using Amazon.S3.Model;
using Amazon.Runtime;
using EntKube.Web.Data;

namespace EntKube.Web.Services;

/// <summary>
/// Result of provisioning a Cleura S3 bucket via OpenStack.
/// Contains the S3 endpoint, access/secret keys, and bucket name.
/// </summary>
public class CleuraS3ProvisionResult
{
    public required string BucketName { get; set; }
    public required string Endpoint { get; set; }
    public required string AccessKey { get; set; }
    public required string SecretKey { get; set; }
    public required string Region { get; set; }
}

/// <summary>
/// Information about an S3 bucket returned from ListBuckets.
/// </summary>
public class S3BucketInfo
{
    public required string Name { get; set; }
    public DateTime CreatedAt { get; set; }
}

/// <summary>
/// A CORS rule for an S3 bucket.
/// </summary>
public class S3CorsRule
{
    public List<string> AllowedOrigins { get; set; } = [];
    public List<string> AllowedMethods { get; set; } = [];
    public List<string> AllowedHeaders { get; set; } = [];
    public int MaxAgeSeconds { get; set; }
}

/// <summary>
/// Handles Cleura/City Cloud S3 bucket provisioning via OpenStack APIs:
/// 1. Authenticates against Keystone to obtain a scoped token
/// 2. Creates EC2 credentials (access/secret key pair) for S3 access
/// 3. Creates the bucket using the AWS S3 SDK against Cleura's S3 endpoint
///
/// The flow:
/// - User has an OpenStackConnection with username/password stored in vault
/// - We authenticate via Keystone v3 password auth
/// - We create EC2 credentials scoped to the project
/// - We use those credentials to call the S3 API and create the bucket
/// - We store the credentials in the vault and return them
/// </summary>
public class OpenStackS3Service(VaultService vaultService, IHttpClientFactory httpClientFactory, OpenStackKeystoneClient keystone)
{
    /// <summary>
    /// Provisions a new S3 bucket on Cleura using the given OpenStack connection.
    /// Authenticates via Keystone, creates EC2 credentials, then creates the bucket.
    /// </summary>
    public async Task<CleuraS3ProvisionResult> CreateBucketAsync(
        Guid tenantId,
        OpenStackConnection connection,
        string bucketName,
        CancellationToken ct = default)
    {
        // Step 1: Retrieve the stored password from the vault.

        string? password = await vaultService.GetOpenStackSecretValueAsync(
            tenantId, connection.Id, "OS_PASSWORD", ct);

        if (string.IsNullOrWhiteSpace(password))
        {
            throw new InvalidOperationException(
                "OpenStack password not found in vault. Re-enter the password in the connection settings.");
        }

        // Step 2: Authenticate against Keystone to get a scoped token, user ID, and project ID.

        KeystoneSession session = await keystone.AuthenticateAsync(connection, password, ct);

        // Step 3: Create EC2 credentials for S3 access.

        (string accessKey, string secretKey) = await keystone.CreateEc2CredentialsAsync(session, connection.AuthUrl, ct);

        // Step 4: Determine the S3 endpoint from the region.

        string endpoint = GetS3Endpoint(connection.Region ?? "Kna1");

        // Step 5: Create the bucket using the S3 API.

        await CreateS3BucketAsync(endpoint, accessKey, secretKey, bucketName, connection.Region ?? "Kna1", ct);

        // Step 6: Apply a default bucket policy that allows full CRUD on objects.
        // Without this, the bucket exists but object operations may be denied.

        string defaultPolicy = BuildDefaultBucketPolicy(bucketName);

        using AmazonS3Client policyClient = CreateS3Client(endpoint, accessKey, secretKey, connection.Region ?? "Kna1");

        await policyClient.PutBucketPolicyAsync(new PutBucketPolicyRequest
        {
            BucketName = bucketName,
            Policy = defaultPolicy
        }, ct);

        return new CleuraS3ProvisionResult
        {
            BucketName = bucketName,
            Endpoint = endpoint,
            AccessKey = accessKey,
            SecretKey = secretKey,
            Region = connection.Region ?? "Kna1"
        };
    }

    /// <summary>
    /// Lists all containers in the OpenStack project via the Swift REST API.
    /// More reliable than S3 ListBuckets against Ceph RADOS Gateway because it
    /// goes through the project-scoped object-store endpoint rather than the
    /// per-EC2-credential RGW user scope.
    /// </summary>
    public async Task<List<S3BucketInfo>> ListBucketsViaSwiftAsync(
        OpenStackConnection connection, string password, CancellationToken ct = default)
    {
        KeystoneSession session = await keystone.AuthenticateAsync(connection, password, ct);
        string token = session.Token;
        string swiftEndpoint = session.RequireEndpoint("object-store");

        // GET <swift_endpoint>?format=json lists all containers owned by this project.

        using HttpClient client = httpClientFactory.CreateClient();

        string listUrl = swiftEndpoint.TrimEnd('/') + "?format=json";

        using HttpRequestMessage listRequest = new(HttpMethod.Get, listUrl);
        listRequest.Headers.Add("X-Auth-Token", token);

        using HttpResponseMessage listResponse = await client.SendAsync(listRequest, ct);

        if (!listResponse.IsSuccessStatusCode)
        {
            string errorBody = await listResponse.Content.ReadAsStringAsync(ct);
            throw new InvalidOperationException(
                $"Swift container listing failed ({listResponse.StatusCode}): {errorBody}");
        }

        string responseBody = await listResponse.Content.ReadAsStringAsync(ct);

        if (string.IsNullOrWhiteSpace(responseBody) || responseBody == "[]")
        {
            return [];
        }

        using JsonDocument doc = JsonDocument.Parse(responseBody);

        return doc.RootElement.EnumerateArray()
            .Select(container =>
            {
                string name = container.TryGetProperty("name", out JsonElement nameEl)
                    ? nameEl.GetString() ?? ""
                    : "";

                DateTime lastModified = default;

                if (container.TryGetProperty("last_modified", out JsonElement lmEl)
                    && DateTime.TryParse(lmEl.GetString(), out DateTime parsed))
                {
                    lastModified = DateTime.SpecifyKind(parsed, DateTimeKind.Utc);
                }

                return new S3BucketInfo { Name = name, CreatedAt = lastModified };
            })
            .Where(b => b.Name.Length > 0)
            .ToList();
    }

    /// <summary>
    /// Creates an S3 bucket using the AWS SDK against Cleura's S3-compatible endpoint.
    /// </summary>
    private static async Task CreateS3BucketAsync(
        string endpoint, string accessKey, string secretKey, string bucketName, string region, CancellationToken ct)
    {
        using AmazonS3Client s3Client = CreateS3Client(endpoint, accessKey, secretKey, region);

        PutBucketRequest putRequest = new()
        {
            BucketName = bucketName
        };

        await s3Client.PutBucketAsync(putRequest, ct);
    }

    /// <summary>
    /// Maps a Cleura region code to the S3 endpoint URL.
    /// Cleura exposes S3-compatible object storage at region-specific endpoints.
    /// </summary>
    private static string GetS3Endpoint(string region)
    {
        // Cleura/City Cloud S3 endpoint pattern (standard HTTPS port).
        // Known regions: Kna1, Sto2, Fra1, Lon1, etc.

        return $"https://s3-{region.ToLowerInvariant()}.citycloud.com";
    }

    /// <summary>
    /// Builds a default S3 bucket policy that grants the bucket owner full CRUD
    /// access to all objects. This is applied immediately after bucket creation
    /// so the EC2 credentials can read, write, list, and delete objects.
    /// </summary>
    private static string BuildDefaultBucketPolicy(string bucketName)
    {
        // Standard S3 bucket policy: allow all object operations for the bucket owner.
        // The "*" principal with the specific bucket ARN scopes this to the
        // credentials that created the bucket (EC2 credentials are project-scoped).

        return $$"""
            {
                "Version": "2012-10-17",
                "Statement": [
                    {
                        "Sid": "AllowFullObjectAccess",
                        "Effect": "Allow",
                        "Principal": "*",
                        "Action": [
                            "s3:GetObject",
                            "s3:PutObject",
                            "s3:DeleteObject",
                            "s3:ListBucket",
                            "s3:GetBucketLocation"
                        ],
                        "Resource": [
                            "arn:aws:s3:::{{bucketName}}",
                            "arn:aws:s3:::{{bucketName}}/*"
                        ]
                    }
                ]
            }
            """;
    }

    // ──────── Bucket Management ────────

    /// <summary>
    /// Lists all buckets accessible with the stored EC2 credentials for a given storage link.
    /// This allows the user to see what buckets exist under their project
    /// without needing to open a separate tool.
    /// </summary>
    public async Task<List<S3BucketInfo>> ListBucketsAsync(
        string endpoint, string accessKey, string secretKey, string region, CancellationToken ct = default)
    {
        using AmazonS3Client s3Client = CreateS3Client(endpoint, accessKey, secretKey, region);

        ListBucketsResponse response = await s3Client.ListBucketsAsync(ct);

        return (response.Buckets ?? [])
            .Select(b => new S3BucketInfo { Name = b.BucketName ?? "", CreatedAt = b.CreationDate.GetValueOrDefault() })
            .ToList();
    }

    /// <summary>
    /// Deletes an S3 bucket. The bucket must be empty before deletion.
    /// This removes the bucket from the object store — it does NOT remove
    /// the StorageLink record (the caller handles that separately).
    /// </summary>
    public async Task DeleteBucketAsync(
        string endpoint, string accessKey, string secretKey, string bucketName, string region, CancellationToken ct = default)
    {
        using AmazonS3Client s3Client = CreateS3Client(endpoint, accessKey, secretKey, region);

        // Empty the bucket first — S3 won't delete non-empty buckets.
        // We page through all objects and delete them in batches of up to 1000.
        // If the bucket doesn't exist at all we treat it as already gone.

        try
        {
            string? continuationToken = null;

            do
            {
                ListObjectsV2Request listRequest = new()
                {
                    BucketName = bucketName,
                    MaxKeys = 1000,
                    ContinuationToken = continuationToken
                };

                ListObjectsV2Response listResponse = await s3Client.ListObjectsV2Async(listRequest, ct);

                List<S3Object> objects = listResponse.S3Objects ?? [];

                if (objects.Count > 0)
                {
                    DeleteObjectsRequest deleteObjectsRequest = new()
                    {
                        BucketName = bucketName,
                        Objects = objects
                            .Select(o => new KeyVersion { Key = o.Key ?? "" })
                            .ToList()
                    };

                    await s3Client.DeleteObjectsAsync(deleteObjectsRequest, ct);
                }

                continuationToken = listResponse.IsTruncated == true ? listResponse.NextContinuationToken : null;
            }
            while (continuationToken is not null);
        }
        catch (AmazonS3Exception ex) when (ex.ErrorCode == "NoSuchBucket")
        {
            // Bucket was already deleted outside of EntKube — nothing left to empty.
            return;
        }

        // Remove the bucket policy before deletion. Ceph RADOS Gateway (used by Cleura)
        // may reject DeleteBucket if a policy is still attached.

        try
        {
            await s3Client.DeleteBucketPolicyAsync(new DeleteBucketPolicyRequest { BucketName = bucketName }, ct);
        }
        catch (AmazonS3Exception)
        {
            // No policy to remove — that's fine, proceed with deletion.
        }

        try
        {
            DeleteBucketRequest deleteRequest = new() { BucketName = bucketName };
            await s3Client.DeleteBucketAsync(deleteRequest, ct);
        }
        catch (AmazonS3Exception ex) when (ex.ErrorCode == "NoSuchBucket")
        {
            // Already gone — nothing to do.
        }
    }

    /// <summary>
    /// Gets the CORS configuration for a bucket.
    /// Returns null if no CORS configuration is set.
    /// </summary>
    public async Task<List<S3CorsRule>?> GetBucketCorsAsync(
        string endpoint, string accessKey, string secretKey, string bucketName, string region, CancellationToken ct = default)
    {
        using AmazonS3Client s3Client = CreateS3Client(endpoint, accessKey, secretKey, region);

        try
        {
            GetCORSConfigurationRequest request = new() { BucketName = bucketName };
            GetCORSConfigurationResponse response = await s3Client.GetCORSConfigurationAsync(request, ct);

            return response.Configuration.Rules
                .Select(r => new S3CorsRule
                {
                    AllowedOrigins = r.AllowedOrigins,
                    AllowedMethods = r.AllowedMethods,
                    AllowedHeaders = r.AllowedHeaders,
                    MaxAgeSeconds = r.MaxAgeSeconds ?? 0
                })
                .ToList();
        }
        catch (AmazonS3Exception ex) when (ex.ErrorCode == "NoSuchCORSConfiguration")
        {
            return null;
        }
    }

    /// <summary>
    /// Sets the CORS configuration for a bucket. Replaces any existing rules.
    /// Pass an empty list to effectively disable CORS (removes the configuration).
    /// </summary>
    public async Task SetBucketCorsAsync(
        string endpoint, string accessKey, string secretKey, string bucketName, string region,
        List<S3CorsRule> rules, CancellationToken ct = default)
    {
        using AmazonS3Client s3Client = CreateS3Client(endpoint, accessKey, secretKey, region);

        if (rules.Count == 0)
        {
            // Remove CORS configuration entirely.

            DeleteCORSConfigurationRequest deleteRequest = new() { BucketName = bucketName };
            await s3Client.DeleteCORSConfigurationAsync(deleteRequest, ct);
            return;
        }

        CORSConfiguration corsConfig = new()
        {
            Rules = rules.Select(r => new CORSRule
            {
                AllowedOrigins = r.AllowedOrigins,
                AllowedMethods = r.AllowedMethods,
                AllowedHeaders = r.AllowedHeaders,
                MaxAgeSeconds = r.MaxAgeSeconds
            }).ToList()
        };

        PutCORSConfigurationRequest putRequest = new()
        {
            BucketName = bucketName,
            Configuration = corsConfig
        };

        await s3Client.PutCORSConfigurationAsync(putRequest, ct);
    }

    /// <summary>
    /// Gets the bucket policy as a JSON string. Returns null if no policy is set.
    /// </summary>
    public async Task<string?> GetBucketPolicyAsync(
        string endpoint, string accessKey, string secretKey, string bucketName, string region, CancellationToken ct = default)
    {
        using AmazonS3Client s3Client = CreateS3Client(endpoint, accessKey, secretKey, region);

        try
        {
            GetBucketPolicyRequest request = new() { BucketName = bucketName };
            GetBucketPolicyResponse response = await s3Client.GetBucketPolicyAsync(request, ct);
            return response.Policy;
        }
        catch (AmazonS3Exception ex) when (ex.ErrorCode == "NoSuchBucketPolicy")
        {
            return null;
        }
    }

    /// <summary>
    /// Sets the bucket policy from a JSON string. Pass null to remove the policy.
    /// The policy JSON must be a valid S3 bucket policy document.
    /// </summary>
    public async Task SetBucketPolicyAsync(
        string endpoint, string accessKey, string secretKey, string bucketName, string region,
        string? policyJson, CancellationToken ct = default)
    {
        using AmazonS3Client s3Client = CreateS3Client(endpoint, accessKey, secretKey, region);

        if (policyJson is null)
        {
            DeleteBucketPolicyRequest deleteRequest = new() { BucketName = bucketName };
            await s3Client.DeleteBucketPolicyAsync(deleteRequest, ct);
            return;
        }

        PutBucketPolicyRequest putRequest = new()
        {
            BucketName = bucketName,
            Policy = policyJson
        };

        await s3Client.PutBucketPolicyAsync(putRequest, ct);
    }

    // ──────── Pre-built Client Overloads (K8s proxy) ────────
    // Called by StorageService when a MinIO link needs to be reached via the
    // Kubernetes API server proxy. The caller owns the AmazonS3Client lifecycle.

    public async Task<List<S3CorsRule>?> GetBucketCorsAsync(
        AmazonS3Client s3Client, string bucketName, CancellationToken ct = default)
    {
        try
        {
            GetCORSConfigurationRequest request = new() { BucketName = bucketName };
            GetCORSConfigurationResponse response = await s3Client.GetCORSConfigurationAsync(request, ct);
            return response.Configuration.Rules
                .Select(r => new S3CorsRule
                {
                    AllowedOrigins = r.AllowedOrigins,
                    AllowedMethods = r.AllowedMethods,
                    AllowedHeaders = r.AllowedHeaders,
                    MaxAgeSeconds  = r.MaxAgeSeconds ?? 0
                })
                .ToList();
        }
        catch (AmazonS3Exception ex) when (ex.ErrorCode == "NoSuchCORSConfiguration")
        {
            return null;
        }
    }

    public async Task SetBucketCorsAsync(
        AmazonS3Client s3Client, string bucketName, List<S3CorsRule> rules, CancellationToken ct = default)
    {
        if (rules.Count == 0)
        {
            await s3Client.DeleteCORSConfigurationAsync(
                new DeleteCORSConfigurationRequest { BucketName = bucketName }, ct);
            return;
        }

        CORSConfiguration corsConfig = new()
        {
            Rules = rules.Select(r => new CORSRule
            {
                AllowedOrigins = r.AllowedOrigins,
                AllowedMethods = r.AllowedMethods,
                AllowedHeaders = r.AllowedHeaders,
                MaxAgeSeconds  = r.MaxAgeSeconds
            }).ToList()
        };

        await s3Client.PutCORSConfigurationAsync(
            new PutCORSConfigurationRequest { BucketName = bucketName, Configuration = corsConfig }, ct);
    }

    public async Task<string?> GetBucketPolicyAsync(
        AmazonS3Client s3Client, string bucketName, CancellationToken ct = default)
    {
        try
        {
            GetBucketPolicyResponse response = await s3Client.GetBucketPolicyAsync(
                new GetBucketPolicyRequest { BucketName = bucketName }, ct);
            return response.Policy;
        }
        catch (AmazonS3Exception ex) when (ex.ErrorCode == "NoSuchBucketPolicy")
        {
            return null;
        }
    }

    public async Task SetBucketPolicyAsync(
        AmazonS3Client s3Client, string bucketName, string? policyJson, CancellationToken ct = default)
    {
        if (policyJson is null)
        {
            await s3Client.DeleteBucketPolicyAsync(
                new DeleteBucketPolicyRequest { BucketName = bucketName }, ct);
            return;
        }

        await s3Client.PutBucketPolicyAsync(
            new() { BucketName = bucketName, Policy = policyJson }, ct);
    }

    // ──────── EC2 Credential Rotation ────────

    /// <summary>
    /// Rotates the EC2 credentials for a Cleura S3 storage link.
    /// This creates new credentials via Keystone, updates the vault,
    /// and deletes the old credentials. The bucket itself is not affected —
    /// only the access/secret key pair changes.
    ///
    /// Flow:
    /// 1. Authenticate with Keystone using the stored password
    /// 2. Create new EC2 credentials
    /// 3. Verify the new credentials work (list bucket)
    /// 4. Return the new credentials (caller updates vault)
    /// </summary>
    public async Task<(string AccessKey, string SecretKey)> RotateCredentialsAsync(
        Guid tenantId, OpenStackConnection connection, string bucketName, CancellationToken ct = default)
    {
        // Retrieve the OpenStack password from vault.

        string? password = await vaultService.GetOpenStackSecretValueAsync(
            tenantId, connection.Id, "OS_PASSWORD", ct);

        if (string.IsNullOrWhiteSpace(password))
        {
            throw new InvalidOperationException(
                "OpenStack password not found in vault. Re-enter the password in the connection settings.");
        }

        // Authenticate and create fresh EC2 credentials.

        KeystoneSession session = await keystone.AuthenticateAsync(connection, password, ct);
        (string newAccessKey, string newSecretKey) = await keystone.CreateEc2CredentialsAsync(session, connection.AuthUrl, ct);

        // Verify the new credentials work by listing the bucket.

        string endpoint = GetS3Endpoint(connection.Region ?? "Kna1");

        using AmazonS3Client s3Client = CreateS3Client(endpoint, newAccessKey, newSecretKey, connection.Region ?? "Kna1");

        try
        {
            ListObjectsV2Request listRequest = new() { BucketName = bucketName, MaxKeys = 1 };
            await s3Client.ListObjectsV2Async(listRequest, ct);
        }
        catch (AmazonS3Exception ex)
        {
            throw new InvalidOperationException(
                $"New credentials failed verification against bucket '{bucketName}': {ex.Message}", ex);
        }

        return (newAccessKey, newSecretKey);
    }

    // ──────── Helpers ────────

    /// <summary>
    /// Creates a configured AmazonS3Client for Cleura's S3-compatible endpoint.
    /// </summary>
    private static AmazonS3Client CreateS3Client(string endpoint, string accessKey, string secretKey, string region)
    {
        if (string.IsNullOrEmpty(endpoint))
        {
            throw new InvalidOperationException("S3 endpoint is required but was not configured for this storage link.");
        }

        AmazonS3Config config = new()
        {
            ServiceURL = endpoint,
            ForcePathStyle = true,
            AuthenticationRegion = region,
            // Disable automatic region detection — we know the endpoint.
            // Without this, the SDK may hang trying to resolve the bucket region.
            UseHttp = endpoint.StartsWith("http://", StringComparison.OrdinalIgnoreCase),
            Timeout = TimeSpan.FromSeconds(30),
            MaxErrorRetry = 1
        };

        BasicAWSCredentials credentials = new(accessKey, secretKey);
        return new AmazonS3Client(credentials, config);
    }
}
