using EntKube.Web.Data;
using EntKube.Web.Data.Modules;
using Microsoft.EntityFrameworkCore;

namespace EntKube.Web.Services.Knowledge;

/// <summary>What converting one tenant's not-yet-moved knowledge sections would give.</summary>
public sealed record KnowledgeMergeForecast(Guid TenantId, string TenantSlug, int Exact, int Mapped, int Fallback, int Detached)
{
    public int Total => Exact + Mapped + Fallback + Detached;
}

/// <summary>
/// Moving the per-application runbooks into the knowledge base (helpdesk phase 11).
///
/// <para><b>Release A runs only the dry run</b>: at startup it converts every section that has no
/// move-ledger row, IN MEMORY, and logs per tenant how many would convert exactly, with mappings,
/// or fall back to preformatted markdown — and how many belong to an application outside their
/// tenant. It writes nothing. The move itself ships in release B, once every instance runs A and
/// the logs have been read.</para>
///
/// <para>The tenant is an input: each tenant's sections are read under its own id, never taken
/// off a row.</para>
/// </summary>
public class KnowledgeMerge(IDbContextFactory<SupportDbContext> dbFactory)
{
    public async Task<List<KnowledgeMergeForecast>> ForecastAsync(CancellationToken ct = default)
    {
        using SupportDbContext db = await dbFactory.CreateDbContextAsync(ct);
        List<KnowledgeMergeForecast> forecasts = [];

        foreach (var tenant in await db.Tenants.Select(t => new { t.Id, t.Slug }).ToListAsync(ct))
        {
            List<Guid> moved = await db.KnowledgeSectionMoves.Where(m => m.TenantId == tenant.Id).Select(m => m.SectionId).ToListAsync(ct);

            var pending = await db.KnowledgeSections.AsNoTracking()
                .Where(s => s.TenantId == tenant.Id && !moved.Contains(s.Id))
                .Select(s => new { s.AppId, s.Body })
                .ToListAsync(ct);

            if (pending.Count == 0)
            {
                continue;
            }

            HashSet<Guid> ownApps = [.. await db.Apps.Where(a => a.Customer.TenantId == tenant.Id).Select(a => a.Id).ToListAsync(ct)];
            int exact = 0, mapped = 0, fallback = 0, detached = 0;

            foreach (var section in pending)
            {
                if (!ownApps.Contains(section.AppId))
                {
                    detached++;
                    continue;
                }

                switch (KnowledgeConversion.Convert(section.Body).Verdict)
                {
                    case MoveVerdict.Exact: exact++; break;
                    case MoveVerdict.Mapped: mapped++; break;
                    default: fallback++; break;
                }
            }

            forecasts.Add(new KnowledgeMergeForecast(tenant.Id, tenant.Slug, exact, mapped, fallback, detached));
        }

        return forecasts;
    }
}
