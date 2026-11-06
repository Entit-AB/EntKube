using EntKube.Telemetry;
using EntKube.Web.Data;
using EntKube.Web.Data.Modules;
using Microsoft.EntityFrameworkCore;

namespace EntKube.Web.Services;

/// <summary>One or more occurrences of one error, ready to be folded into an issue.</summary>
public sealed record ErrorObservation(
    string Fingerprint, string Title, string? Namespace, string? Workload, string Sample, string? TraceId,
    long Count, DateTime FirstAt, DateTime LastAt);

/// <summary>The issue inbox, and the arithmetic of folding what a scan saw into it.</summary>
public sealed class ErrorTrackingService(IDbContextFactory<TelemetryDbContext> dbFactory, IConfiguration config)
{
    public const int MaxTitleLength = 300;
    public const int MaxSampleLength = 2000;

    /// <summary>
    /// The most issues one cluster or site may hold. A message the normalizer cannot reduce would
    /// otherwise make a row per line, in the operational database.
    /// </summary>
    public const int MaxIssuesPerScope = 2000;

    public async Task<List<ErrorIssue>> ListAsync(
        Guid tenantId, ErrorIssueStatus? status = null, int limit = 200, CancellationToken ct = default)
    {
        await using TelemetryDbContext db = await dbFactory.CreateDbContextAsync(ct);

        return await db.ErrorIssues.AsNoTracking()
            .Where(i => i.TenantId == tenantId && (status == null || i.Status == status))
            .OrderByDescending(i => i.LastSeenAt)
            .ThenBy(i => i.Id)
            .Take(Math.Clamp(limit, 1, 1000))
            .ToListAsync(ct);
    }

    /// <summary>Returns false when there is no such issue in this tenant.</summary>
    public async Task<bool> SetStatusAsync(
        Guid tenantId, Guid id, ErrorIssueStatus status, string? actor, CancellationToken ct = default)
    {
        await using TelemetryDbContext db = await dbFactory.CreateDbContextAsync(ct);

        ErrorIssue? issue = await db.ErrorIssues.FirstOrDefaultAsync(i => i.Id == id && i.TenantId == tenantId, ct);
        if (issue is null) return false;

        DateTime now = DateTime.UtcNow;

        issue.Status = status;
        issue.ResolvedAt = status == ErrorIssueStatus.Resolved ? now : null;
        issue.ResolvedBy = status == ErrorIssueStatus.Resolved ? actor : null;
        issue.UpdatedAt = now;

        await db.SaveChangesAsync(ct);
        return true;
    }

    /// <summary>
    /// Where a scope's next scan starts from: the cursor, or a look back for one never scanned.
    /// </summary>
    public async Task<DateTime?> CursorAsync(Guid tenantId, Guid scopeId, CancellationToken ct = default)
    {
        await using TelemetryDbContext db = await dbFactory.CreateDbContextAsync(ct);

        return await db.ErrorScanCursors.AsNoTracking()
            .Where(c => c.ScopeId == scopeId && c.TenantId == tenantId)
            .Select(c => (DateTime?)c.ScannedThrough)
            .FirstOrDefaultAsync(ct);
    }

    /// <summary>
    /// Claims a window. Only the instance whose update moves the cursor from where it read it applies
    /// what it saw, so two instances running the sweep cannot both count the same lines. Claiming before
    /// applying means a crash between the two loses a window rather than double-counting it.
    /// </summary>
    public async Task<bool> TryClaimAsync(
        Guid tenantId, Guid scopeId, DateTime? expected, DateTime through, CancellationToken ct = default)
    {
        await using TelemetryDbContext db = await dbFactory.CreateDbContextAsync(ct);

        if (expected is { } from)
        {
            int moved = await db.ErrorScanCursors
                .Where(c => c.ScopeId == scopeId && c.TenantId == tenantId && c.ScannedThrough == from)
                .ExecuteUpdateAsync(u => u.SetProperty(c => c.ScannedThrough, through), ct);

            return moved == 1;
        }

        db.ErrorScanCursors.Add(new ErrorScanCursor { ScopeId = scopeId, TenantId = tenantId, ScannedThrough = through });

        try
        {
            await db.SaveChangesAsync(ct);
            return true;
        }
        catch (DbUpdateException)
        {
            return false;   // the other instance's first scan got there first
        }
    }

    /// <summary>
    /// Folds a scan's observations into issues, returning how many were new.
    ///
    /// <para><b>A regression needs a later occurrence.</b> A scan looks back, so it can see lines from
    /// before somebody pressed Resolve. Only an occurrence after that moment reopens the issue: those
    /// earlier lines are the fault that was just fixed.</para>
    /// </summary>
    public async Task<int> ApplyAsync(
        Guid tenantId, ErrorIssueSource source, Guid scopeId, string scopeName,
        IReadOnlyList<ErrorObservation> observations, bool partial, DateTime now, CancellationToken ct = default)
    {
        if (observations.Count == 0) return 0;

        await using TelemetryDbContext db = await dbFactory.CreateDbContextAsync(ct);

        List<string> fingerprints = [.. observations.Select(o => o.Fingerprint)];

        Dictionary<string, ErrorIssue> existing = await db.ErrorIssues
            .Where(i => i.TenantId == tenantId && i.ScopeId == scopeId && fingerprints.Contains(i.Fingerprint))
            .ToDictionaryAsync(i => i.Fingerprint, ct);

        int total = await db.ErrorIssues.CountAsync(i => i.TenantId == tenantId && i.ScopeId == scopeId, ct);
        int created = 0;

        foreach (ErrorObservation o in observations)
        {
            if (existing.TryGetValue(o.Fingerprint, out ErrorIssue? issue))
            {
                issue.Occurrences += o.Count;
                issue.CountIsPartial |= partial;
                issue.FirstSeenAt = o.FirstAt < issue.FirstSeenAt ? o.FirstAt : issue.FirstSeenAt;
                issue.UpdatedAt = now;

                if (o.LastAt >= issue.LastSeenAt)
                {
                    issue.LastSeenAt = o.LastAt;
                    issue.SampleMessage = Cut(o.Sample, MaxSampleLength);
                    issue.SampleTraceId = o.TraceId ?? issue.SampleTraceId;
                }

                if (issue.Status == ErrorIssueStatus.Resolved && o.LastAt > issue.ResolvedAt)
                {
                    issue.Status = ErrorIssueStatus.Open;
                    issue.RegressedAt = o.LastAt;
                    issue.Regressions++;
                    issue.ResolvedAt = null;
                    issue.ResolvedBy = null;
                }

                continue;
            }

            if (total + created >= MaxIssuesPerScope) continue;

            db.ErrorIssues.Add(new ErrorIssue
            {
                Id = Guid.NewGuid(),
                TenantId = tenantId,
                Source = source,
                ScopeId = scopeId,
                ScopeName = Cut(scopeName, 200),
                Fingerprint = o.Fingerprint,
                Title = Cut(o.Title, MaxTitleLength),
                Namespace = o.Namespace,
                Workload = o.Workload,
                SampleMessage = Cut(o.Sample, MaxSampleLength),
                SampleTraceId = o.TraceId,
                FirstSeenAt = o.FirstAt,
                LastSeenAt = o.LastAt,
                Occurrences = o.Count,
                CountIsPartial = partial,
                Status = ErrorIssueStatus.Open,
                UpdatedAt = now,
            });

            created++;
        }

        await db.SaveChangesAsync(ct);
        return created;
    }

    /// <summary>Open issues in a scope that are new, or came back, since a moment.</summary>
    public static Task<int> CountNewOrRegressedAsync(
        TelemetryDbContext db, Guid tenantId, Guid scopeId, DateTime since, CancellationToken ct) =>
        db.ErrorIssues.CountAsync(i =>
            i.TenantId == tenantId && i.ScopeId == scopeId && i.Status == ErrorIssueStatus.Open
            && (i.FirstSeenAt >= since || i.RegressedAt >= since), ct);

    /// <summary>Deletes issues not seen for a long time, by age alone. Returns how many went.</summary>
    public async Task<int> PruneAsync(DateTime now, CancellationToken ct = default)
    {
        int days = Math.Max(1, config.GetValue<int?>("ErrorTracking:RetentionDays") ?? 90);
        DateTime cutoff = now.AddDays(-days);

        await using TelemetryDbContext db = await dbFactory.CreateDbContextAsync(ct);
        return await db.ErrorIssues.Where(i => i.LastSeenAt < cutoff).ExecuteDeleteAsync(ct);
    }

    private static string Cut(string value, int max) => value.Length <= max ? value : value[..max];

    /// <summary>Groups log lines by fingerprint. Every line in <paramref name="streams"/> is an error.</summary>
    public static List<ErrorObservation> ObserveLogs(IEnumerable<LokiLogStream> streams)
    {
        Dictionary<string, ErrorObservation> seen = [];

        foreach (LokiLogStream stream in streams)
        {
            string ns = stream.Labels.GetValueOrDefault("namespace", "");
            string workload = ErrorFingerprint.Workload(stream.Labels.GetValueOrDefault("pod", ""));

            foreach (LokiLogEntry entry in stream.Entries)
            {
                string title = ErrorFingerprint.Normalize(entry.Line);
                if (title.Length == 0) continue;

                Fold(seen, new ErrorObservation(
                    ErrorFingerprint.Compute(ns, workload, title), title, ns, workload, entry.Line, entry.TraceId,
                    1, entry.Timestamp, entry.Timestamp));
            }
        }

        return [.. seen.Values];
    }

    /// <summary>
    /// Groups a site's aggregated errors. The store gives a last-seen time only, so that is the first
    /// time as well, which can only make an issue look younger than it is on its first scan.
    /// </summary>
    public static List<ErrorObservation> ObserveBrowser(IEnumerable<RumTopError> errors)
    {
        Dictionary<string, ErrorObservation> seen = [];

        foreach (RumTopError error in errors)
        {
            string title = ErrorFingerprint.Normalize(error.Message);
            if (title.Length == 0) continue;

            Fold(seen, new ErrorObservation(
                ErrorFingerprint.Compute(title), title, null, null, error.Message, null,
                error.Count, error.LastSeen, error.LastSeen));
        }

        return [.. seen.Values];
    }

    private static void Fold(Dictionary<string, ErrorObservation> seen, ErrorObservation next)
    {
        if (!seen.TryGetValue(next.Fingerprint, out ErrorObservation? held))
        {
            seen[next.Fingerprint] = next;
            return;
        }

        bool newer = next.LastAt >= held.LastAt;

        seen[next.Fingerprint] = held with
        {
            Count = held.Count + next.Count,
            FirstAt = next.FirstAt < held.FirstAt ? next.FirstAt : held.FirstAt,
            LastAt = newer ? next.LastAt : held.LastAt,
            Sample = newer ? next.Sample : held.Sample,
            TraceId = newer ? next.TraceId ?? held.TraceId : held.TraceId,
        };
    }
}
