using EntKube.Web.Data;
using MimeKit;
using EntKube.Web.Services.Mail;
using Microsoft.EntityFrameworkCore;

namespace EntKube.Web.Services.Contracts;

/// <summary>
/// The classification in force for an application on a date.
/// </summary>
/// <param name="Level">The service level.</param>
/// <param name="Window">
/// The support window, after inheritance. Null when nothing has been agreed — an instance
/// whose parent application has no window either, or an application classified but never
/// given one. Null is reported rather than defaulted, because guessing S1 would quietly
/// understate both the window fee and the hours the SLA clock runs.
/// </param>
/// <param name="WindowInherited">True when the window came from the parent application (§10.2.1).</param>
/// <param name="EffectiveFrom">When this classification took effect.</param>
public readonly record struct ResolvedServiceLevel(
    ManagementLevel Level,
    SupportWindow? Window,
    bool WindowInherited,
    DateTime EffectiveFrom);

/// <summary>
/// Reads the agreement: which terms applied to an application or a portfolio on a given
/// date, and what the base fee comes to.
///
/// <para>Everything here takes an <c>asOf</c> instant rather than reading the clock. A
/// statement for March is produced in April and must answer as March — the same discipline
/// the cost ledger keeps, for the same reason.</para>
/// </summary>
public class ContractService(IDbContextFactory<ApplicationDbContext> dbFactory)
{
    /// <summary>
    /// How deep an instance chain may be followed when inheriting a support window. An
    /// instance of an instance is not something §10.2.1 contemplates, but data can always
    /// say otherwise, and a cycle must not hang a page.
    /// </summary>
    private const int MaxParentDepth = 8;

    public async Task<ApplicationContract?> GetContractAsync(Guid appId, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        return await db.ApplicationContracts
            .Include(c => c.ServiceLevels)
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.AppId == appId, ct);
    }

    /// <summary>
    /// The service level and support window in force for an application on a date, with
    /// an instance inheriting its parent application's window when it has none of its own.
    /// Null when the application has no contract or nothing had taken effect by then.
    /// </summary>
    public async Task<ResolvedServiceLevel?> ResolveServiceLevelAsync(
        Guid appId, DateTime asOf, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        List<ApplicationContract> contracts = await db.ApplicationContracts
            .Include(c => c.ServiceLevels)
            .AsNoTracking()
            .ToListAsync(ct);

        return Resolve(appId, asOf, contracts.ToDictionary(c => c.AppId));
    }

    /// <summary>
    /// The Annex B terms in force for a customer on a date — the latest agreement that had
    /// taken effect by then.
    /// </summary>
    public async Task<PortfolioAgreement?> GetPortfolioAgreementAsync(
        Guid customerId, DateTime asOf, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        return await db.PortfolioAgreements
            .AsNoTracking()
            .Where(a => a.CustomerId == customerId && a.EffectiveFrom <= asOf)
            .OrderByDescending(a => a.EffectiveFrom)
            .FirstOrDefaultAsync(ct);
    }

    /// <summary>
    /// The price list in force on a date: the customer's own if one has been negotiated,
    /// otherwise the tenant's standard list.
    /// </summary>
    public async Task<PriceList?> GetPriceListAsync(
        Guid tenantId, Guid? customerId, DateTime asOf, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        IQueryable<PriceList> lists = db.PriceLists
            .Include(p => p.Entries)
            .AsNoTracking()
            .Where(p => p.TenantId == tenantId && p.EffectiveFrom <= asOf);

        if (customerId is not null)
        {
            PriceList? negotiated = await lists
                .Where(p => p.CustomerId == customerId)
                .OrderByDescending(p => p.EffectiveFrom)
                .FirstOrDefaultAsync(ct);

            if (negotiated is not null)
            {
                return negotiated;
            }
        }

        return await lists
            .Where(p => p.CustomerId == null)
            .OrderByDescending(p => p.EffectiveFrom)
            .FirstOrDefaultAsync(ct);
    }

    /// <summary>
    /// The base fee of §10 for a customer on a date: one window fee priced on the most
    /// extensive window in the portfolio, plus one knowledge fee per application.
    ///
    /// <para>Applications that cannot be priced are listed rather than charged at zero, so a
    /// missing classification shows up as a gap instead of as a discount.</para>
    /// </summary>
    public async Task<BaseFeeBreakdown> CalculateBaseFeeAsync(
        Guid customerId, DateTime asOf, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        Customer? customer = await db.Customers.AsNoTracking()
            .FirstOrDefaultAsync(c => c.Id == customerId, ct);

        if (customer is null)
        {
            return new BaseFeeBreakdown(null, 0m, [], []);
        }

        // Every contract in the tenant, because resolving an instance's inherited window may
        // walk to a parent application that belongs to another customer.
        List<ApplicationContract> allContracts = await db.ApplicationContracts
            .Include(c => c.ServiceLevels)
            .AsNoTracking()
            .Where(c => c.TenantId == customer.TenantId)
            .ToListAsync(ct);

        Dictionary<Guid, ApplicationContract> byApp = allContracts.ToDictionary(c => c.AppId);

        Dictionary<Guid, string> appNames = await db.Apps.AsNoTracking()
            .Where(a => a.CustomerId == customerId)
            .ToDictionaryAsync(a => a.Id, a => a.Name, ct);

        PriceList? prices = await GetPriceListAsync(customer.TenantId, customerId, asOf, ct);

        List<ApplicationContract> active = [.. allContracts
            .Where(c => appNames.ContainsKey(c.AppId) && IsUnderManagement(c, asOf))];

        Dictionary<Guid, int> ordinals = InstanceOrdinals(allContracts);

        List<ApplicationFee> fees = [];
        List<string> unpriced = [];

        foreach (ApplicationContract contract in active.OrderBy(c => appNames[c.AppId]))
        {
            string name = appNames[contract.AppId];
            ResolvedServiceLevel? resolved = Resolve(contract.AppId, asOf, byApp);

            if (resolved is null || resolved.Value.Window is null)
            {
                unpriced.Add($"{name}: no service level or support window in force");
                continue;
            }

            string key = ContractPricing.KnowledgeFeeKey(
                resolved.Value.Level, ordinals.GetValueOrDefault(contract.AppId));

            decimal? fee = Lookup(prices, PriceKind.KnowledgeFee, key);

            if (fee is null)
            {
                unpriced.Add($"{name}: no knowledge fee in the price list for {key}");
                continue;
            }

            fees.Add(new ApplicationFee(
                contract.AppId, name, resolved.Value.Level, resolved.Value.Window.Value, key, fee.Value));
        }

        SupportWindow? widest = ContractPricing.MostExtensiveWindow(fees.Select(f => f.Window));
        decimal windowFee = 0m;

        if (widest is not null)
        {
            decimal? amount = Lookup(prices, PriceKind.WindowFee, widest.Value.ToString());
            if (amount is null)
            {
                unpriced.Add($"No window fee in the price list for {widest}");
            }
            else
            {
                windowFee = amount.Value;
            }
        }

        return new BaseFeeBreakdown(widest, windowFee, fees, unpriced);
    }

    // ---- Writes ------------------------------------------------------------------------

    /// <summary>
    /// Creates or updates an application's Annex A. The classification is not touched here
    /// — it moves through <see cref="RecordServiceLevelAsync"/>, which keeps its history.
    /// </summary>
    public async Task<ApplicationContract> SaveContractAsync(
        ApplicationContract edited, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        ApplicationContract? existing = await db.ApplicationContracts
            .FirstOrDefaultAsync(c => c.AppId == edited.AppId, ct);

        if (existing is null)
        {
            edited.Id = edited.Id == Guid.Empty ? Guid.NewGuid() : edited.Id;
            edited.CreatedAt = DateTime.UtcNow;
            edited.UpdatedAt = edited.CreatedAt;
            db.ApplicationContracts.Add(edited);
            await db.SaveChangesAsync(ct);
            return edited;
        }

        existing.Origin = edited.Origin;
        existing.DevelopedBy = edited.DevelopedBy;
        existing.OnboardedAt = edited.OnboardedAt;
        existing.GuaranteeEndsAt = edited.GuaranteeEndsAt;
        existing.ParentAppId = edited.ParentAppId;
        existing.SlaStartsAt = edited.SlaStartsAt;
        existing.ManagementEndedAt = edited.ManagementEndedAt;
        existing.MonthlyWorkCapHours = edited.MonthlyWorkCapHours;
        existing.OnboardingFee = edited.OnboardingFee;
        existing.Criticality = edited.Criticality;
        existing.Notes = edited.Notes;
        existing.UpdatedAt = DateTime.UtcNow;

        await db.SaveChangesAsync(ct);
        return existing;
    }

    /// <summary>
    /// Records a classification taking effect from a date. Always an insert: §4.1 and §16.2
    /// both change the level over the life of the agreement, and the fee follows, so the
    /// previous classification has to stay readable.
    /// </summary>
    public async Task<ApplicationServiceLevel> RecordServiceLevelAsync(
        Guid contractId,
        ManagementLevel level,
        SupportWindow? window,
        DateTime effectiveFrom,
        string reason,
        string? recordedBy,
        CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        ApplicationServiceLevel entry = new()
        {
            Id = Guid.NewGuid(),
            ApplicationContractId = contractId,
            Level = level,
            SupportWindow = window,
            EffectiveFrom = effectiveFrom,
            Reason = reason,
            RecordedBy = recordedBy,
            RecordedAt = DateTime.UtcNow,
        };

        db.ApplicationServiceLevels.Add(entry);
        await db.SaveChangesAsync(ct);
        return entry;
    }

    /// <summary>
    /// Deletes a classification entry. For correcting a mistyped row, not for changing the
    /// terms: a real change is a new entry with its own effective date.
    /// </summary>
    public async Task DeleteServiceLevelAsync(Guid id, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        ApplicationServiceLevel? entry = await db.ApplicationServiceLevels.FindAsync([id], ct);
        if (entry is not null)
        {
            db.ApplicationServiceLevels.Remove(entry);
            await db.SaveChangesAsync(ct);
        }
    }

    /// <summary>Records an Annex B taking effect from a date.</summary>
    public async Task<PortfolioAgreement> RecordPortfolioAgreementAsync(
        PortfolioAgreement agreement, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        agreement.Id = agreement.Id == Guid.Empty ? Guid.NewGuid() : agreement.Id;
        agreement.RecordedAt = DateTime.UtcNow;

        db.PortfolioAgreements.Add(agreement);
        await db.SaveChangesAsync(ct);
        return agreement;
    }

    public async Task<List<PortfolioAgreement>> ListPortfolioAgreementsAsync(
        Guid customerId, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        return await db.PortfolioAgreements.AsNoTracking()
            .Where(a => a.CustomerId == customerId)
            .OrderByDescending(a => a.EffectiveFrom)
            .ToListAsync(ct);
    }

    public async Task<List<ApplicationServiceLevel>> ListServiceLevelsAsync(
        Guid contractId, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        return await db.ApplicationServiceLevels.AsNoTracking()
            .Where(l => l.ApplicationContractId == contractId)
            .OrderByDescending(l => l.EffectiveFrom)
            .ThenByDescending(l => l.RecordedAt)
            .ToListAsync(ct);
    }

    public async Task<List<ContractContact>> ListContactsAsync(
        Guid customerId, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        return await db.ContractContacts.AsNoTracking()
            .Where(c => c.CustomerId == customerId)
            .OrderBy(c => c.Party)
            .ThenBy(c => c.Role)
            .ThenBy(c => c.Name)
            .ToListAsync(ct);
    }

    public async Task<ContractContact> SaveContactAsync(
        ContractContact contact, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        if (contact.Id == Guid.Empty)
        {
            contact.Id = Guid.NewGuid();
            contact.CreatedAt = DateTime.UtcNow;
            contact.UpdatedAt = contact.CreatedAt;
            db.ContractContacts.Add(contact);
        }
        else
        {
            contact.UpdatedAt = DateTime.UtcNow;
            db.ContractContacts.Update(contact);
        }

        await db.SaveChangesAsync(ct);
        return contact;
    }

    /// <summary>The mail domains registered to a customer, most specific first.</summary>
    public async Task<List<CustomerEmailDomain>> ListEmailDomainsAsync(
        Guid customerId, CancellationToken ct = default)
    {
        using ApplicationDbContext db = await dbFactory.CreateDbContextAsync(ct);

        return await db.CustomerEmailDomains.AsNoTracking()
            .Where(d => d.CustomerId == customerId)
            .OrderBy(d => d.Domain)
            .ToListAsync(ct);
    }

    /// <summary>
    /// Registers a mail domain to a customer, so support mail from anyone there is placed.
    /// </summary>
    /// <returns>The row, or an explanation of why it was refused.</returns>
    public async Task<(CustomerEmailDomain? Added, string? Refused)> AddEmailDomainAsync(
        Guid tenantId, Guid customerId, string entered, string? notes, string? addedBy,
        CancellationToken ct = default)
    {
        string? domain = SenderDomain.Normalise(entered);

        if (domain is null)
        {
            return (null, "That is not a mail domain — it needs at least one dot, as in entit.example.");
        }

        if (SenderDomain.PublicProviders.Contains(domain))
        {
            return (null,
                $"{domain} belongs to everybody, so registering it would place every message "
                + "from it with this customer — including strangers. Add the individual "
                + "addresses as contacts instead.");
        }

        using ApplicationDbContext db = await dbFactory.CreateDbContextAsync(ct);

        CustomerEmailDomain? existing = await db.CustomerEmailDomains
            .Include(d => d.Customer)
            .FirstOrDefaultAsync(d => d.TenantId == tenantId && d.Domain == domain, ct);

        if (existing is not null)
        {
            return existing.CustomerId == customerId
                ? (existing, null)
                : (null, $"{domain} is already registered to {existing.Customer.Name}.");
        }

        CustomerEmailDomain added = new()
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            CustomerId = customerId,
            Domain = domain,
            Notes = notes,
            AddedBy = addedBy,
        };

        db.CustomerEmailDomains.Add(added);
        await db.SaveChangesAsync(ct);

        return (added, null);
    }

    /// <summary>The support addresses that route to a customer.</summary>
    public async Task<List<CustomerSupportAddress>> ListSupportAddressesAsync(
        Guid customerId, CancellationToken ct = default)
    {
        using ApplicationDbContext db = await dbFactory.CreateDbContextAsync(ct);

        return await db.CustomerSupportAddresses.AsNoTracking()
            .Where(a => a.CustomerId == customerId)
            .OrderBy(a => a.Address)
            .ToListAsync(ct);
    }

    /// <summary>
    /// Gives a customer a support address of their own.
    /// </summary>
    /// <returns>The row, or an explanation of why it was refused.</returns>
    public async Task<(CustomerSupportAddress? Added, string? Refused)> AddSupportAddressAsync(
        Guid tenantId, Guid customerId, string entered, bool replyFromThis, string? notes,
        string? addedBy, CancellationToken ct = default)
    {
        string address = (entered ?? "").Trim().ToLowerInvariant();

        if (!MailboxAddress.TryParse(address, out MailboxAddress? parsed)
            || string.IsNullOrWhiteSpace(parsed.Address)
            || !parsed.Address.Contains('@', StringComparison.Ordinal))
        {
            return (null, "That is not an email address.");
        }

        address = parsed.Address.ToLowerInvariant();

        using ApplicationDbContext db = await dbFactory.CreateDbContextAsync(ct);

        // The tenant's own generic address would route every message to one customer.
        string? generic = await db.SupportMailboxes.AsNoTracking()
            .Where(m => m.TenantId == tenantId).Select(m => m.Address).FirstOrDefaultAsync(ct);

        if (address.Equals(generic, StringComparison.OrdinalIgnoreCase))
        {
            return (null,
                $"{address} is the mailbox everything arrives in, so giving it to one "
                + "customer would route every message to them. Use an alias that delivers "
                + "into it, such as their own name at the same domain.");
        }

        CustomerSupportAddress? existing = await db.CustomerSupportAddresses
            .Include(a => a.Customer)
            .FirstOrDefaultAsync(a => a.TenantId == tenantId && a.Address == address, ct);

        if (existing is not null)
        {
            return existing.CustomerId == customerId
                ? (existing, null)
                : (null, $"{address} already routes to {existing.Customer.Name}.");
        }

        // At most one address replies come from, so the newest wins if it claims that.
        if (replyFromThis)
        {
            foreach (CustomerSupportAddress other in await db.CustomerSupportAddresses
                .Where(a => a.CustomerId == customerId && a.ReplyFromThis).ToListAsync(ct))
            {
                other.ReplyFromThis = false;
            }
        }

        CustomerSupportAddress added = new()
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            CustomerId = customerId,
            Address = address,
            ReplyFromThis = replyFromThis,
            Notes = notes,
            AddedBy = addedBy,
        };

        db.CustomerSupportAddresses.Add(added);
        await db.SaveChangesAsync(ct);

        return (added, null);
    }

    public async Task DeleteSupportAddressAsync(Guid id, CancellationToken ct = default)
    {
        using ApplicationDbContext db = await dbFactory.CreateDbContextAsync(ct);

        CustomerSupportAddress? address =
            await db.CustomerSupportAddresses.FindAsync([id], ct);

        if (address is not null)
        {
            db.CustomerSupportAddresses.Remove(address);
            await db.SaveChangesAsync(ct);
        }
    }

    public async Task DeleteEmailDomainAsync(Guid id, CancellationToken ct = default)
    {
        using ApplicationDbContext db = await dbFactory.CreateDbContextAsync(ct);

        CustomerEmailDomain? domain = await db.CustomerEmailDomains.FindAsync([id], ct);

        if (domain is not null)
        {
            db.CustomerEmailDomains.Remove(domain);
            await db.SaveChangesAsync(ct);
        }
    }

    public async Task DeleteContactAsync(Guid id, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        ContractContact? contact = await db.ContractContacts.FindAsync([id], ct);
        if (contact is not null)
        {
            db.ContractContacts.Remove(contact);
            await db.SaveChangesAsync(ct);
        }
    }

    /// <summary>
    /// Removes a recorded Annex B.
    ///
    /// <para>For a row that should not be there — a mistyped date, a change that was
    /// entered twice. Not the way to end an arrangement: that is a new agreement with a
    /// later effective date, which leaves the old one readable. Deleting the one that was
    /// in force changes which terms a past month resolves to.</para>
    /// </summary>
    public async Task DeletePortfolioAgreementAsync(Guid id, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        PortfolioAgreement? agreement = await db.PortfolioAgreements.FindAsync([id], ct);

        if (agreement is not null)
        {
            db.PortfolioAgreements.Remove(agreement);
            await db.SaveChangesAsync(ct);
        }
    }

    /// <summary>
    /// Removes a price list and everything in it.
    ///
    /// <para><b>This reprices the past.</b> A month resolves its prices from the list in
    /// force on its own dates, so deleting one makes every month that used it fall back to
    /// whichever list preceded it — including months already reported and invoiced. It is
    /// here for a list entered by mistake, not for superseding one.</para>
    /// </summary>
    public async Task DeletePriceListAsync(Guid id, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        PriceList? list = await db.PriceLists
            .Include(p => p.Entries)
            .FirstOrDefaultAsync(p => p.Id == id, ct);

        if (list is not null)
        {
            db.PriceLists.Remove(list);
            await db.SaveChangesAsync(ct);
        }
    }

    /// <summary>
    /// Changes a price list's own details — when it takes effect, and the note explaining
    /// what it is.
    /// </summary>
    public async Task UpdatePriceListAsync(
        Guid id, DateTime effectiveFrom, string? notes, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        PriceList? list = await db.PriceLists.FirstOrDefaultAsync(p => p.Id == id, ct);

        if (list is not null)
        {
            list.EffectiveFrom = effectiveFrom;
            list.Notes = notes;
            await db.SaveChangesAsync(ct);
        }
    }

    /// <summary>
    /// Sets one amount in a price list, adding the line if the list does not carry it yet.
    ///
    /// <para>Needed because <see cref="StandardPriceList"/> is a template: the amounts it
    /// seeds are the standard ones, and what governs a customer is what they signed. It is
    /// also how a typo gets fixed.</para>
    ///
    /// <para><b>An edit reprices every month that used this list</b>, including ones
    /// already reported — §28 gives the customer thirty days to dispute a report, and a
    /// figure that changes underneath one is how that dispute starts. To change prices
    /// going forward, add a list with a later effective date instead.</para>
    /// </summary>
    public async Task<PriceListEntry> SavePriceListEntryAsync(
        Guid priceListId,
        PriceKind kind,
        string key,
        decimal amount,
        decimal? hours = null,
        CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        PriceListEntry? entry = await db.PriceListEntries
            .FirstOrDefaultAsync(e => e.PriceListId == priceListId && e.Kind == kind && e.Key == key, ct);

        if (entry is null)
        {
            int nextOrder = await db.PriceListEntries
                .Where(e => e.PriceListId == priceListId)
                .Select(e => (int?)e.SortOrder)
                .MaxAsync(ct) ?? -1;

            entry = new PriceListEntry
            {
                Id = Guid.NewGuid(),
                PriceListId = priceListId,
                Kind = kind,
                Key = key,
                Amount = amount,
                Hours = hours,
                SortOrder = nextOrder + 1,
            };

            db.PriceListEntries.Add(entry);
        }
        else
        {
            entry.Amount = amount;
            entry.Hours = hours;
        }

        await db.SaveChangesAsync(ct);
        return entry;
    }

    /// <summary>
    /// Removes one amount from a price list. Anything priced from that line then reports
    /// as unpriced rather than as free, which is the point of reporting gaps at all.
    /// </summary>
    public async Task DeletePriceListEntryAsync(Guid entryId, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        PriceListEntry? entry = await db.PriceListEntries.FindAsync([entryId], ct);

        if (entry is not null)
        {
            db.PriceListEntries.Remove(entry);
            await db.SaveChangesAsync(ct);
        }
    }

    public async Task<List<PriceList>> ListPriceListsAsync(
        Guid tenantId, Guid? customerId, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        return await db.PriceLists
            .Include(p => p.Entries)
            .AsNoTracking()
            .Where(p => p.TenantId == tenantId && p.CustomerId == customerId)
            .OrderByDescending(p => p.EffectiveFrom)
            .ToListAsync(ct);
    }

    /// <summary>
    /// Stores a new price list. Existing lists are never touched — §19's January indexation
    /// is a new version, and a statement for an earlier month has to keep pricing at the
    /// list that was in force then.
    /// </summary>
    public async Task<PriceList> AddPriceListAsync(PriceList list, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        db.PriceLists.Add(list);
        await db.SaveChangesAsync(ct);
        return list;
    }

    /// <summary>
    /// A copy of the price list in force, with every amount raised by a percentage — §19's
    /// annual indexation against SCB's AKI, which has a floor of 2%. Amounts are rounded to
    /// whole kronor, the unit the annex is written in.
    /// </summary>
    public async Task<PriceList?> IndexPriceListAsync(
        Guid tenantId,
        Guid? customerId,
        DateTime effectiveFrom,
        decimal percent,
        string? createdBy,
        CancellationToken ct = default)
    {
        decimal applied = Math.Max(percent, MinimumIndexationPercent);

        PriceList? current = await GetPriceListAsync(tenantId, customerId, effectiveFrom, ct);
        if (current is null)
        {
            return null;
        }

        PriceList indexed = new()
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            CustomerId = customerId,
            EffectiveFrom = effectiveFrom,
            Currency = current.Currency,
            Notes = $"Indexed {applied:0.##}% per §19 from the list effective {current.EffectiveFrom:yyyy-MM-dd}.",
            CreatedBy = createdBy,
            CreatedAt = DateTime.UtcNow,
        };

        foreach (PriceListEntry entry in current.Entries.OrderBy(e => e.SortOrder))
        {
            indexed.Entries.Add(new PriceListEntry
            {
                Id = Guid.NewGuid(),
                PriceListId = indexed.Id,
                Kind = entry.Kind,
                Key = entry.Key,
                Amount = Math.Round(entry.Amount * (1 + (applied / 100m)), 0, MidpointRounding.AwayFromZero),
                Hours = entry.Hours,
                SortOrder = entry.SortOrder,
            });
        }

        return await AddPriceListAsync(indexed, ct);
    }

    /// <summary>
    /// The floor §19 puts under the annual price adjustment: by at least 2%.
    /// </summary>
    public const decimal MinimumIndexationPercent = 2m;

    /// <summary>One amount from a price list, or null when the list does not carry it.</summary>
    public static decimal? Lookup(PriceList? list, PriceKind kind, string key) =>
        list?.Entries.FirstOrDefault(e => e.Kind == kind && e.Key == key)?.Amount;

    /// <summary>
    /// Whether the application was under management at that instant — on-boarded by then
    /// and not yet taken out under §19. A contract with no on-boarding date has not started.
    /// </summary>
    private static bool IsUnderManagement(ApplicationContract contract, DateTime asOf) =>
        contract.OnboardedAt is not null
        && contract.OnboardedAt <= asOf
        && (contract.ManagementEndedAt is null || contract.ManagementEndedAt > asOf);

    /// <summary>
    /// Each instance's position among the instances of its parent application, counted from
    /// one, so §10.2.1's reduced rate from the twenty-first can be applied.
    /// </summary>
    private static Dictionary<Guid, int> InstanceOrdinals(IEnumerable<ApplicationContract> contracts)
    {
        Dictionary<Guid, int> ordinals = [];

        IEnumerable<IGrouping<Guid, ApplicationContract>> families = contracts
            .Where(c => c.ParentAppId is not null)
            .GroupBy(c => c.ParentAppId!.Value);

        foreach (IGrouping<Guid, ApplicationContract> family in families)
        {
            IReadOnlyList<ApplicationContract> ordered =
                ContractPricing.InOrdinalOrder(family, c => c.OnboardedAt, c => c.AppId);

            for (int i = 0; i < ordered.Count; i++)
            {
                ordinals[ordered[i].AppId] = i + 1;
            }
        }

        return ordinals;
    }

    private static ResolvedServiceLevel? Resolve(
        Guid appId, DateTime asOf, IReadOnlyDictionary<Guid, ApplicationContract> byApp)
    {
        if (!byApp.TryGetValue(appId, out ApplicationContract? contract))
        {
            return null;
        }

        ApplicationServiceLevel? level = contract.ServiceLevels
            .Where(l => l.EffectiveFrom <= asOf)
            .OrderByDescending(l => l.EffectiveFrom)
            .ThenByDescending(l => l.RecordedAt)
            .FirstOrDefault();

        if (level is null)
        {
            return null;
        }

        if (level.SupportWindow is not null)
        {
            return new ResolvedServiceLevel(level.Level, level.SupportWindow, false, level.EffectiveFrom);
        }

        // §10.2.1: an instance inherits its parent application's window unless it states one.
        Guid? parentAppId = contract.ParentAppId;

        for (int depth = 0; depth < MaxParentDepth && parentAppId is not null; depth++)
        {
            if (!byApp.TryGetValue(parentAppId.Value, out ApplicationContract? parent))
            {
                break;
            }

            ApplicationServiceLevel? parentLevel = parent.ServiceLevels
                .Where(l => l.EffectiveFrom <= asOf)
                .OrderByDescending(l => l.EffectiveFrom)
                .ThenByDescending(l => l.RecordedAt)
                .FirstOrDefault();

            if (parentLevel?.SupportWindow is not null)
            {
                return new ResolvedServiceLevel(
                    level.Level, parentLevel.SupportWindow, true, level.EffectiveFrom);
            }

            parentAppId = parent.ParentAppId == parentAppId ? null : parent.ParentAppId;
        }

        return new ResolvedServiceLevel(level.Level, null, false, level.EffectiveFrom);
    }
}
