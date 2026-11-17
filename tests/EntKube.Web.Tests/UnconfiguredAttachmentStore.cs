using EntKube.Web.Services.Tickets;

namespace EntKube.Web.Tests;

/// <summary>
/// An attachment store for tests that are not about attachments: never configured, so nothing is
/// stored and the ticket screen shows why — the state of every installation that has not chosen
/// a storage link.
/// </summary>
internal sealed class UnconfiguredAttachmentStore : IAttachmentObjectStore
{
    public const string Reason = "No attachment storage is configured for this tenant.";

    public Task<string?> WhyUnavailableAsync(Guid tenantId, CancellationToken ct = default) => Task.FromResult<string?>(Reason);

    public Task<Guid> PutAsync(Guid tenantId, string key, Stream content, string contentType, CancellationToken ct = default) =>
        throw new InvalidOperationException(Reason);

    public Task<Stream?> OpenAsync(Guid tenantId, Guid storageLinkId, string key, CancellationToken ct = default) =>
        Task.FromResult<Stream?>(null);
}
