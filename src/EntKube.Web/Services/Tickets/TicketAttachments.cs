using Amazon.S3;
using Amazon.S3.Model;
using EntKube.Web.Data;
using EntKube.Web.Data.Modules;
using Microsoft.EntityFrameworkCore;

namespace EntKube.Web.Services.Tickets;

/// <summary>
/// Where attachment bytes live. An interface so the rules around it — limits, tenancy, what is
/// recorded when a file cannot be kept — can be tested without a bucket.
/// </summary>
public interface IAttachmentObjectStore
{
    /// <summary>Why nothing can be stored for this tenant right now, or null when it can.</summary>
    Task<string?> WhyUnavailableAsync(Guid tenantId, CancellationToken ct = default);

    /// <summary>Stores the bytes; returns the storage link used.</summary>
    Task<Guid> PutAsync(Guid tenantId, string key, Stream content, string contentType, CancellationToken ct = default);

    /// <summary>The bytes, or null when the object is gone.</summary>
    Task<Stream?> OpenAsync(Guid tenantId, Guid storageLinkId, string key, CancellationToken ct = default);
}

/// <summary>
/// The tenant's chosen S3 storage link, reached the way telemetry reaches its own: the link is
/// read through Data Services' <see cref="StorageService"/> for the tenant asking, so a setting
/// naming another tenant's link finds nothing, and the client comes from
/// <see cref="StorageLinkClientFactory"/> with the credentials in the vault.
/// </summary>
public sealed class S3AttachmentObjectStore(
    IDbContextFactory<SupportDbContext> dbFactory,
    StorageService storage,
    StorageLinkClientFactory clients) : IAttachmentObjectStore
{
    public async Task<string?> WhyUnavailableAsync(Guid tenantId, CancellationToken ct = default)
    {
        (StorageLink? link, string? why) = await LinkAsync(tenantId, null, ct);
        return link is null ? why : null;
    }

    public async Task<Guid> PutAsync(Guid tenantId, string key, Stream content, string contentType, CancellationToken ct = default)
    {
        (StorageLink? link, string? why) = await LinkAsync(tenantId, null, ct);

        if (link is null)
        {
            throw new InvalidOperationException(why);
        }

        (AmazonS3Client client, IDisposable? cleanup) = await clients.CreateClientAsync(tenantId, link, ct);

        using (cleanup)
        using (client)
        {
            await client.PutObjectAsync(new PutObjectRequest
            {
                BucketName = link.BucketName,
                Key = key,
                InputStream = content,
                ContentType = contentType,
                AutoCloseStream = false,
            }, ct);
        }

        return link.Id;
    }

    public async Task<Stream?> OpenAsync(Guid tenantId, Guid storageLinkId, string key, CancellationToken ct = default)
    {
        (StorageLink? link, _) = await LinkAsync(tenantId, storageLinkId, ct);

        if (link is null)
        {
            return null;
        }

        (AmazonS3Client client, IDisposable? cleanup) = await clients.CreateClientAsync(tenantId, link, ct);

        using (cleanup)
        using (client)
        {
            try
            {
                using GetObjectResponse response = await client.GetObjectAsync(link.BucketName, key, ct);
                MemoryStream copy = new();
                await response.ResponseStream.CopyToAsync(copy, ct);
                copy.Position = 0;
                return copy;
            }
            catch (AmazonS3Exception ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                return null;
            }
        }
    }

    /// <summary>
    /// The link to use: the one an attachment was stored with, or the tenant's current setting.
    /// Always one of the tenant's own links — looked up through the tenant, never by id alone.
    /// </summary>
    private async Task<(StorageLink? Link, string? Why)> LinkAsync(Guid tenantId, Guid? storageLinkId, CancellationToken ct)
    {
        Guid? id = storageLinkId;

        if (id is null)
        {
            using SupportDbContext db = await dbFactory.CreateDbContextAsync(ct);
            id = await db.SupportAttachmentSettings.AsNoTracking()
                .Where(s => s.TenantId == tenantId).Select(s => (Guid?)s.StorageLinkId).FirstOrDefaultAsync(ct);
        }

        if (id is null)
        {
            return (null, "No attachment storage is configured for this tenant (Support › Mail › Attachments).");
        }

        StorageLink? link = (await storage.GetStorageLinksAsync(tenantId, ct: ct)).FirstOrDefault(l => l.Id == id);

        if (link is null)
        {
            return (null, "The storage link chosen for attachments no longer exists.");
        }

        if (string.IsNullOrWhiteSpace(link.BucketName))
        {
            return (null, $"The storage link “{link.Name}” has no bucket.");
        }

        return (link, null);
    }
}

/// <summary>An attachment as offered for keeping: a name, a type, and a way to read it.</summary>
public sealed record IncomingAttachment(string FileName, string? ContentType, Func<Stream> Open);

/// <summary>An attachment as a ticket shows it.</summary>
public readonly record struct AttachmentView(
    Guid Id, string FileName, string ContentType, long Size, bool Stored, string? Problem,
    AttachmentSource Source, bool CustomerVisible, DateTime CreatedAt, string? CreatedBy, Guid? TicketEventId);

/// <summary>
/// Attachments on tickets: kept in the tenant's bucket, recorded even when they could not be,
/// and handed out only through a tenant-scoped read.
/// </summary>
public class TicketAttachmentService(IDbContextFactory<SupportDbContext> dbFactory, IAttachmentObjectStore store)
{
    /// <summary>The largest single file kept.</summary>
    public const long MaxFileBytes = 25L * 1024 * 1024;

    /// <summary>The most kept from one mail or one note.</summary>
    public const long MaxBatchBytes = 50L * 1024 * 1024;

    /// <summary>
    /// The content type a download is served with. Only types a browser cannot run are passed
    /// through; anything else — HTML, SVG, scripts — goes out as octet-stream, so a file a
    /// customer sent can never execute in our origin.
    /// </summary>
    public static string ServedContentType(string? stored)
    {
        string t = (stored ?? "").Split(';')[0].Trim().ToLowerInvariant();

        return t is "application/pdf" or "image/png" or "image/jpeg" or "image/gif" or "image/webp"
            or "text/plain" or "text/csv" or "application/zip"
            or "application/vnd.openxmlformats-officedocument.wordprocessingml.document"
            or "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet"
            or "application/vnd.openxmlformats-officedocument.presentationml.presentation"
            or "application/msword" or "application/vnd.ms-excel"
            ? t
            : "application/octet-stream";
    }

    /// <summary>A file name made safe to show and to send in a header: no path, no control characters.</summary>
    public static string SafeFileName(string? name)
    {
        string n = Path.GetFileName((name ?? "").Replace('\\', '/'));
        n = new string([.. n.Where(c => !char.IsControl(c))]).Trim();

        if (n.Length == 0)
        {
            return "attachment";
        }

        return n.Length > 200 ? n[..200] : n;
    }

    // ───────────────────────────── Settings ─────────────────────────────

    public async Task<Guid?> GetStorageLinkAsync(Guid tenantId, CancellationToken ct = default)
    {
        using SupportDbContext db = await dbFactory.CreateDbContextAsync(ct);
        return await db.SupportAttachmentSettings.AsNoTracking()
            .Where(s => s.TenantId == tenantId).Select(s => (Guid?)s.StorageLinkId).FirstOrDefaultAsync(ct);
    }

    /// <summary>
    /// Chooses the storage link, or clears it with null. The caller offers only the tenant's own
    /// links; the object store checks the tenant again on every use.
    /// </summary>
    public async Task SetStorageLinkAsync(Guid tenantId, Guid? storageLinkId, string actor, CancellationToken ct = default)
    {
        using SupportDbContext db = await dbFactory.CreateDbContextAsync(ct);

        SupportAttachmentSetting? row = await db.SupportAttachmentSettings.FirstOrDefaultAsync(s => s.TenantId == tenantId, ct);

        if (storageLinkId is not Guid id)
        {
            if (row is not null)
            {
                db.SupportAttachmentSettings.Remove(row);
            }
        }
        else
        {
            if (row is null)
            {
                row = new SupportAttachmentSetting { TenantId = tenantId };
                db.SupportAttachmentSettings.Add(row);
            }

            row.StorageLinkId = id;
            row.UpdatedAt = DateTime.UtcNow;
            row.UpdatedBy = actor;
        }

        await db.SaveChangesAsync(ct);
    }

    public Task<string?> WhyUnavailableAsync(Guid tenantId, CancellationToken ct = default) =>
        store.WhyUnavailableAsync(tenantId, ct);

    // ───────────────────────────── Keeping ─────────────────────────────

    /// <summary>
    /// Keeps the attachments of a mail just taken in. Never throws: mail intake must not stop
    /// because a bucket is down, and every file that could not be kept is recorded with why.
    /// </summary>
    public async Task<int> KeepFromMailAsync(
        Guid tenantId, Guid inboundMessageId, IReadOnlyList<IncomingAttachment> files, CancellationToken ct = default)
    {
        int kept = 0;

        try
        {
            List<TicketAttachment> rows = await KeepAsync(tenantId, files, AttachmentSource.Mail, customerVisible: true, "mail", ct);

            foreach (TicketAttachment row in rows)
            {
                row.InboundMailMessageId = inboundMessageId;
                kept += row.Stored ? 1 : 0;
            }

            await SaveAsync(rows, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Recording the failure itself failed (the database, not the bucket). Nothing more
            // can be written; the mail is in, and its text is on the message.
            _ = ex;
        }

        return kept;
    }

    /// <summary>
    /// Adds files to a ticket, against one of its history entries when given. Refuses — saying
    /// why — rather than storing anything, when no storage is configured or a file is too large:
    /// an agent attaching a file can act on that at once, unlike a customer's mail.
    /// </summary>
    /// <exception cref="InvalidOperationException">Nothing was stored, and why.</exception>
    public async Task<List<AttachmentView>?> AddToTicketAsync(
        Guid tenantId, Guid ticketId, Guid? ticketEventId, IReadOnlyList<IncomingAttachment> files,
        bool customerVisible, string actor, CancellationToken ct = default)
    {
        using (SupportDbContext db = await dbFactory.CreateDbContextAsync(ct))
        {
            if (!await db.Tickets.AnyAsync(t => t.Id == ticketId && t.TenantId == tenantId, ct))
            {
                return null;
            }

            if (ticketEventId is Guid eventId && !await db.TicketEvents.AnyAsync(e => e.Id == eventId && e.TicketId == ticketId, ct))
            {
                return null;
            }
        }

        if (await store.WhyUnavailableAsync(tenantId, ct) is string why)
        {
            throw new InvalidOperationException(why);
        }

        List<TicketAttachment> rows = await KeepAsync(tenantId, files, AttachmentSource.Agent, customerVisible, actor, ct);

        if (rows.FirstOrDefault(r => !r.Stored) is { } failed)
        {
            throw new InvalidOperationException($"{failed.FileName}: {failed.Problem}");
        }

        foreach (TicketAttachment row in rows)
        {
            row.TicketId = ticketId;
            row.TicketEventId = ticketEventId;
        }

        await SaveAsync(rows, ct);
        return [.. rows.Select(View)];
    }

    private async Task<List<TicketAttachment>> KeepAsync(
        Guid tenantId, IReadOnlyList<IncomingAttachment> files, AttachmentSource source, bool customerVisible,
        string actor, CancellationToken ct)
    {
        string? unavailable = await store.WhyUnavailableAsync(tenantId, ct);
        List<TicketAttachment> rows = [];
        long total = 0;

        foreach (IncomingAttachment file in files)
        {
            TicketAttachment row = new()
            {
                Id = Guid.NewGuid(),
                TenantId = tenantId,
                Source = source,
                FileName = SafeFileName(file.FileName),
                ContentType = string.IsNullOrWhiteSpace(file.ContentType) ? "application/octet-stream" : file.ContentType.Trim(),
                CustomerVisible = customerVisible,
                CreatedBy = actor,
            };

            rows.Add(row);

            if (unavailable is not null)
            {
                row.Problem = $"Not kept: {unavailable}";
                continue;
            }

            try
            {
                // Read to memory, bounded, so the size is known before anything is stored and a
                // stream that claims one length and delivers another cannot get past the limit.
                await using Stream source_ = file.Open();
                using MemoryStream buffer = new();
                byte[] chunk = new byte[81920];
                int read;

                while ((read = await source_.ReadAsync(chunk, ct)) > 0)
                {
                    buffer.Write(chunk, 0, read);

                    if (buffer.Length > MaxFileBytes)
                    {
                        break;
                    }
                }

                row.Size = buffer.Length;

                if (buffer.Length > MaxFileBytes)
                {
                    row.Problem = $"Not kept: larger than the {MaxFileBytes / (1024 * 1024)} MB limit.";
                    continue;
                }

                if (total + buffer.Length > MaxBatchBytes)
                {
                    row.Problem = $"Not kept: together with the others, over the {MaxBatchBytes / (1024 * 1024)} MB limit.";
                    continue;
                }

                buffer.Position = 0;
                string key = $"support-attachments/{tenantId:N}/{DateTime.UtcNow:yyyy/MM}/{row.Id:N}";

                row.StorageLinkId = await store.PutAsync(tenantId, key, buffer, ServedContentType(row.ContentType), ct);
                row.ObjectKey = key;
                row.Stored = true;
                total += buffer.Length;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                row.Problem = $"Not kept: the upload failed ({ex.GetType().Name}).";
            }
        }

        return rows;
    }

    private async Task SaveAsync(List<TicketAttachment> rows, CancellationToken ct)
    {
        if (rows.Count == 0)
        {
            return;
        }

        using SupportDbContext db = await dbFactory.CreateDbContextAsync(ct);
        db.TicketAttachments.AddRange(rows);
        await db.SaveChangesAsync(ct);
    }

    // ───────────────────────────── Reading ─────────────────────────────

    /// <summary>
    /// A ticket's attachments: its own, and those of every mail filed on it — including mails
    /// filed on duplicates merged into it, so the whole matter's files are in one place.
    /// </summary>
    public async Task<List<AttachmentView>> ForTicketAsync(Guid tenantId, Guid ticketId, CancellationToken ct = default)
    {
        using SupportDbContext db = await dbFactory.CreateDbContextAsync(ct);

        List<Guid> tickets = [ticketId, .. await db.Tickets
            .Where(t => t.MergedIntoTicketId == ticketId && t.TenantId == tenantId).Select(t => t.Id).ToListAsync(ct)];

        List<Guid> mails = await db.InboundMailMessages
            .Where(m => m.TicketId != null && tickets.Contains(m.TicketId.Value))
            .Select(m => m.Id).ToListAsync(ct);

        List<TicketAttachment> rows = await db.TicketAttachments.AsNoTracking()
            .Where(a => a.TenantId == tenantId
                        && ((a.TicketId != null && tickets.Contains(a.TicketId.Value))
                            || (a.InboundMailMessageId != null && mails.Contains(a.InboundMailMessageId.Value))))
            .OrderBy(a => a.CreatedAt)
            .ToListAsync(ct);

        return [.. rows.Select(View)];
    }

    /// <summary>The file's bytes and how to serve them — only for this tenant's attachment.</summary>
    public async Task<(AttachmentView Meta, Stream Content)?> OpenAsync(Guid tenantId, Guid attachmentId, CancellationToken ct = default)
    {
        using SupportDbContext db = await dbFactory.CreateDbContextAsync(ct);

        TicketAttachment? row = await db.TicketAttachments.AsNoTracking()
            .FirstOrDefaultAsync(a => a.Id == attachmentId && a.TenantId == tenantId && a.Stored, ct);

        if (row is null || row.StorageLinkId is not Guid linkId || row.ObjectKey is null)
        {
            return null;
        }

        Stream? content = await store.OpenAsync(tenantId, linkId, row.ObjectKey, ct);
        return content is null ? null : (View(row), content);
    }

    private static AttachmentView View(TicketAttachment a) =>
        new(a.Id, a.FileName, a.ContentType, a.Size, a.Stored, a.Problem, a.Source, a.CustomerVisible,
            a.CreatedAt, a.CreatedBy, a.TicketEventId);
}
