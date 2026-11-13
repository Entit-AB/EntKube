namespace EntKube.Web.Services.Clusters;

/// <summary>
/// Writes and removes one writer's keys inside a Kubernetes Secret that has several writers.
///
/// <para><b>Why a Secret has several writers.</b> An app's Secret in its own namespace is fed by
/// the app's vault secrets and by the credentials of every service it is bound to — Redis,
/// RabbitMQ, Mongo, Postgres, object storage, Kafka, Elasticsearch, Keycloak. Pointing more than
/// one of those at the same Secret is intended and supported: an app wants a single
/// <c>envFrom</c>, not one per service it happens to use.</para>
///
/// <para><b>Neither obvious way to write it is additive.</b> Delete-and-recreate drops every key
/// the writer does not itself carry. And a plain <c>apply</c> is not additive either, which is
/// the trap: apply's three-way merge deletes the keys named in the object's
/// <c>last-applied-configuration</c> annotation but absent from the new config — and that
/// annotation belongs to the <b>object</b>, so it describes whoever applied last, not this
/// writer. Two writers applying in turn therefore delete each other's keys, and it looks like
/// the last one won.</para>
///
/// <para><b>What this adds over a bare merge patch.</b> A merge patch sets the keys it names and
/// deletes the keys it names as null — it has no memory, so it cannot know that a key this
/// writer used to set is gone and should be removed. That is what the per-writer annotation
/// here is for: <c>entkube.io/keys.&lt;owner&gt;</c> records the key names this writer set last
/// time, so the next write can delete exactly the ones it has dropped and nothing else. It is
/// also what lets a binding be removed without deleting a Secret that other writers are still
/// using.</para>
/// </summary>
public static class SharedSecretKeys
{
    /// <summary>The annotation naming the keys one writer owns. Per writer, not per object.</summary>
    public static string AnnotationFor(string owner) => $"entkube.io/keys.{owner}";

    /// <summary>
    /// Sets this writer's keys, and deletes the ones it set last time but no longer has.
    /// Keys belonging to other writers are left exactly as they are, and so are this writer's
    /// own keys named in <paramref name="retain"/>.
    /// </summary>
    /// <param name="owner">
    /// Short, stable name for the writer — "vault", "cache", "messaging", and so on. It keys the
    /// ownership annotation, so renaming one orphans whatever it used to own.
    /// </param>
    /// <param name="keys">
    /// The keys to set. A null value means "delete this key", for a writer whose key set varies
    /// with configuration (a queue name that has been cleared, say); those are not recorded as
    /// owned.
    /// </param>
    /// <param name="retain">
    /// Keys this writer still owns but is not writing on this pass, because the caller is working
    /// from a partial view of its own keys — an environment-scoped vault sync is the case this
    /// exists for. They are neither patched nor deleted, and they stay in the ownership
    /// annotation, so the caller that CAN see them keeps the right to remove them later.
    ///
    /// <para>⚠️ Not the same as passing them with a null value: null is the delete, so the
    /// obvious reach for "leave this one alone" removes exactly the key it meant to protect.</para>
    /// </param>
    public static async Task WriteAsync(
        IClusterClient cluster, string secretName, string ns, string owner,
        IReadOnlyDictionary<string, string?> keys, string? summary = null,
        CancellationToken ct = default, IReadOnlyCollection<string>? retain = null)
    {
        HashSet<string> retaining = retain is null
            ? []
            : new HashSet<string>(retain, StringComparer.Ordinal);

        IReadOnlyCollection<string> ownedBefore =
            await ReadOwnedAsync(cluster, secretName, ns, owner, ct);

        Dictionary<string, string?> patch = new(keys);

        // Dropped since the last write: set to null so the merge patch deletes them. Without
        // this, a revoked credential would stay readable in the cluster — which is the property
        // the delete-and-recreate this replaces was there to get.
        foreach (string dropped in ownedBefore)
        {
            if (!patch.ContainsKey(dropped) && !retaining.Contains(dropped))
            {
                patch[dropped] = null;
            }
        }

        // Retained keys stay claimed. Dropping them from the annotation would not delete
        // anything now, but it would orphan them: this writer would no longer own them, so
        // nothing would ever delete them again — which is how a revoked credential outlives
        // its revocation.
        List<string> ownedNow = keys
            .Where(k => k.Value is not null)
            .Select(k => k.Key)
            .Concat(retaining)
            .Distinct(StringComparer.Ordinal)
            .Order()
            .ToList();

        await cluster.MergeSecretKeysAsync(
            secretName, ns, patch,
            new Dictionary<string, string> { [AnnotationFor(owner)] = string.Join(",", ownedNow) },
            summary, ct);
    }

    /// <summary>
    /// Deletes this writer's keys and forgets them, leaving the Secret and every other writer's
    /// keys in place.
    ///
    /// <para>Used when a binding is removed. Deleting the Secret object instead — which is what
    /// the binding teardown paths used to do — takes the app's own secrets with it whenever the
    /// binding shares their Secret.</para>
    /// </summary>
    public static async Task RemoveAsync(
        IClusterClient cluster, string secretName, string ns, string owner,
        string? summary = null, CancellationToken ct = default)
    {
        IReadOnlyCollection<string> owned =
            await ReadOwnedAsync(cluster, secretName, ns, owner, ct);

        if (owned.Count == 0)
        {
            // Nothing recorded: either already removed, or written before ownership was tracked.
            // Deleting nothing is the safe answer — the alternative is guessing which keys in a
            // shared Secret were this writer's.
            return;
        }

        await cluster.MergeSecretKeysAsync(
            secretName, ns,
            owned.ToDictionary(k => k, _ => (string?)null),
            new Dictionary<string, string> { [AnnotationFor(owner)] = "" },
            summary, ct);
    }

    /// <summary>
    /// The key names this writer recorded last time, or empty when the Secret is absent, the
    /// annotation is missing (written before this existed), or it cannot be read.
    ///
    /// <para>Empty on failure is deliberate: nothing is then deleted. A key that outlives its
    /// source is visible and fixable; a key deleted from under another writer is neither.</para>
    /// </summary>
    public static async Task<IReadOnlyCollection<string>> ReadOwnedAsync(
        IClusterClient cluster, string secretName, string ns, string owner,
        CancellationToken ct = default)
    {
        try
        {
            HelmExecutionResult read =
                await cluster.GetResourceJsonAsync("secret", secretName, ns, ct);

            if (!read.Success || string.IsNullOrWhiteSpace(read.Output))
            {
                return [];
            }

            using System.Text.Json.JsonDocument doc =
                System.Text.Json.JsonDocument.Parse(read.Output);

            if (doc.RootElement.TryGetProperty("metadata", out System.Text.Json.JsonElement meta)
                && meta.TryGetProperty("annotations", out System.Text.Json.JsonElement annotations)
                && annotations.TryGetProperty(
                    AnnotationFor(owner), out System.Text.Json.JsonElement owned)
                && owned.GetString() is string list
                && !string.IsNullOrWhiteSpace(list))
            {
                return list.Split(
                    ',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            // See the summary: empty means "delete nothing".
        }

        return [];
    }

    /// <summary>Stable owner names, so a rename cannot silently orphan a writer's keys.</summary>
    public static class Owners
    {
        public const string Vault = "vault";
        public const string Cache = "cache";
        public const string Messaging = "messaging";
        public const string Database = "database";
        public const string Storage = "storage";
        public const string Streaming = "streaming";
        public const string Search = "search";
        public const string Identity = "identity";
    }
}
