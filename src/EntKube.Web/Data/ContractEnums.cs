namespace EntKube.Web.Data;

/// <summary>
/// The four support windows of §9 — the hours during which the supplier is staffed and
/// during which the SLA clocks of §14.4 run. Chosen per application in Bilaga A; the most
/// extensive one in the portfolio sets the fönsteravgift (§10.1).
/// </summary>
public enum SupportWindow
{
    /// <summary>Kontorstid — helgfria vardagar 08:00–17:00. No jour.</summary>
    S1 = 1,

    /// <summary>Utökad kontorstid — helgfria vardagar 05:00–22:00.</summary>
    S2 = 2,

    /// <summary>Utökad kontorstid inkl. helger och röda dagar — alla dagar 05:00–22:00.</summary>
    S3 = 3,

    /// <summary>Dygnet runt — 24/7/365.</summary>
    S4 = 4,
}

/// <summary>
/// The §13 time categories. Which one applies is decided by <em>when the work was done</em>,
/// not by which support window the application bought — §13 is explicit about that, and it
/// is the mistake most likely to be made when reading the two tables side by side.
/// </summary>
public enum SupportTimeCategory
{
    /// <summary>Ordinarie — helgfria vardagar 08:00–17:00.</summary>
    Ordinary = 0,

    /// <summary>Kväll/morgon — helgfria vardagar 05:00–08:00 and 17:00–22:00.</summary>
    EveningMorning = 1,

    /// <summary>Helg och röd dag — 05:00–22:00.</summary>
    WeekendOrRedDay = 2,

    /// <summary>Natt — 22:00–05:00, every day of the week.</summary>
    Night = 3,

    /// <summary>
    /// Utryckning — work on a P1 incident outside the application's chosen support window.
    /// Not derivable from the clock alone: it depends on which window was bought and on the
    /// ticket's priority, so callers state it rather than the calendar inferring it.
    /// </summary>
    Callout = 4,
}

/// <summary>
/// Who wrote the application, which decides whether Del 2 or Del 3 of the agreement governs
/// it — and with that, whether there is an on-boarding fee and a guarantee period.
/// </summary>
public enum ContractOrigin
{
    /// <summary>Del 2 — developed by the customer or by another supplier.</summary>
    ExternallyDeveloped = 0,

    /// <summary>Del 3 — developed and delivered by the supplier.</summary>
    SupplierDeveloped = 1,
}

/// <summary>
/// The förvaltningsnivå of §10.2, which sets the application's kännedomsavgift.
/// </summary>
public enum ManagementLevel
{
    /// <summary>Enkel — static or simple dynamic web application, few or no integrations.</summary>
    Simple = 0,

    /// <summary>Standard — business logic, 1–3 integrations, a database, user management.</summary>
    Standard = 1,

    /// <summary>Komplex — many integrations, high data volume, real-time or high security demands.</summary>
    Complex = 2,

    /// <summary>
    /// Instans — a further deployment of a moderapplikation already under management
    /// (§10.2.1). Same code and image, its own monitoring, alarms, certificates and config.
    /// </summary>
    Instance = 3,

    /// <summary>Behöver undersökas — cannot be classified without a technical review.</summary>
    NeedsInvestigation = 4,
}

/// <summary>
/// How work is paid for, chosen per portfolio in Bilaga B. It never replaces the
/// grundavgift, which is charged under either model (§10).
/// </summary>
public enum PricingModel
{
    /// <summary>Modell A — a timbank of hours bought monthly at a discount (§11).</summary>
    HourBank = 0,

    /// <summary>Modell B — löpande räkning, every hour billed in arrears (§12).</summary>
    TimeAndMaterials = 1,
}

/// <summary>Which side of the agreement a contact belongs to.</summary>
public enum ContractParty
{
    Customer = 0,
    Supplier = 1,
}

/// <summary>
/// The roles the agreement names. §23 requires a customer contact with a mandate plus a
/// deputy; §14.5 requires named people at escalation levels 2 and 3 on both sides, recorded
/// in Bilaga B and kept current by each party.
/// </summary>
public enum ContractContactRole
{
    /// <summary>The technical contact with a mandate to prioritise and approve (§23).</summary>
    TechnicalContact = 0,

    /// <summary>The deputy §23 also requires.</summary>
    Deputy = 1,

    /// <summary>Applikationsansvarig — escalation level 2 on the customer side (§14.5).</summary>
    ApplicationOwner = 2,

    /// <summary>Escalation level 2: the supplier's owner of the förvaltningsavtal (§14.5).</summary>
    EscalationLevel2 = 3,

    /// <summary>Escalation level 3: the supplier's VD, the customer's IT manager (§14.5).</summary>
    EscalationLevel3 = 4,

    /// <summary>Where invoices and statements go (§20).</summary>
    Billing = 5,
}

/// <summary>
/// The kinds of amount Bilaga C carries. The price annex is replaced wholesale by a new
/// signed version rather than edited in place, so the entries are data rather than columns.
/// </summary>
public enum PriceKind
{
    /// <summary>Fönsteravgift per support window, C.1. Key: the <see cref="SupportWindow"/>.</summary>
    WindowFee = 0,

    /// <summary>Kännedomsavgift per förvaltningsnivå, C.2. Key: the <see cref="ManagementLevel"/>.</summary>
    KnowledgeFee = 1,

    /// <summary>Timbank tier, C.3. Key: the number of hours; the row also carries them numerically.</summary>
    HourBankTier = 2,

    /// <summary>Hourly rate per time category, C.4. Key: the <see cref="SupportTimeCategory"/>.</summary>
    HourlyRate = 3,

    /// <summary>One-off fees, C.5 — on-boarding per level, deploying a new instance.</summary>
    OneOffFee = 4,
}
